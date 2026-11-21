using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.NewSolver;

internal readonly record struct RngStamp(
    PredictionRngState Shuffle,
    PredictionRngState CardGen,
    PredictionRngState PotionGen,
    PredictionRngState CardSelect,
    PredictionRngState EnergyCosts,
    PredictionRngState Targets,
    PredictionRngState OrbGen,
    PredictionRngState MonsterAi,
    PredictionRngState Niche)
{
    public static RngStamp Capture(CombatPredictionSimulator sim, Player player) => new(
        sim.Rng.ShuffleState,
        sim.Rng.CombatCardGenerationState,
        sim.Rng.CombatPotionGenerationState,
        sim.Rng.CombatCardSelectionState,
        sim.Rng.CombatEnergyCostsState,
        sim.Rng.CombatTargetsState,
        sim.Rng.CombatOrbGenerationState,
        sim.Rng.MonsterAiState,
        sim.Rng.NicheState);
}

internal sealed class SimStepState(
    CombatPredictionSimulator simulator,
    ForkableSet<uint> processedEnemyDeaths,
    int knownTopRemaining,
    Dictionary<uint, int>? teammateDeathTurns = null,
    double teammateSupportValue = 0,
    Dictionary<uint, (int Hp, int OstyHp, double PowerValue)>? teammatePostRootSnapshot = null)
{
    private Dictionary<uint, int>? _teammateDeathTurns = teammateDeathTurns;
    private Dictionary<uint, (int Hp, int OstyHp, double PowerValue)>? _teammatePostRootSnapshot = teammatePostRootSnapshot;

    public CombatPredictionSimulator Simulator { get; } = simulator;
    public SimulatedCombatState Combat => (SimulatedCombatState)Simulator.State.CombatState;
    public ForkableSet<uint> ProcessedEnemyDeaths { get; } = processedEnemyDeaths;
    public int KnownTopRemaining { get; set; } = knownTopRemaining;
    public double TeammateSupportValue { get; set; } = teammateSupportValue;

    public int GetTeammateDeathTurn(uint combatId)
        => _teammateDeathTurns != null && _teammateDeathTurns.TryGetValue(combatId, out int turn) ? turn : int.MaxValue;

    public bool TryGetTeammatePostRootSnapshot(uint combatId, out int hp, out int ostyHp, out double powerValue)
    {
        if (_teammatePostRootSnapshot != null && _teammatePostRootSnapshot.TryGetValue(combatId, out var snap))
        {
            hp = snap.Hp;
            ostyHp = snap.OstyHp;
            powerValue = snap.PowerValue;
            return true;
        }
        hp = 0;
        ostyHp = 0;
        powerValue = 0;
        return false;
    }

    public void RecordTeammateDeaths(Player localPlayer, int turnNumber)
    {
        if (Combat.Players.Count <= 1)
            return;
        foreach (Player p in Combat.Players)
        {
            if (ReferenceEquals(p, localPlayer))
                continue;
            if (p.Creature.CombatId is uint id && Simulator.State.GetCreature(p.Creature).IsDead)
            {
                _teammateDeathTurns ??= [];
                _teammateDeathTurns.TryAdd(id, turnNumber);
            }
        }
    }

    public void CaptureTeammatePostRootSnapshotIfUnset(Player localPlayer)
    {
        if (Combat.Players.Count <= 1 || _teammatePostRootSnapshot != null)
            return;
        _teammatePostRootSnapshot = new Dictionary<uint, (int Hp, int OstyHp, double PowerValue)>(Combat.Players.Count - 1);
        foreach (Player p in Combat.Players)
        {
            if (ReferenceEquals(p, localPlayer) || p.Creature.CombatId is not uint id)
                continue;
            SimCreatureState cs = Simulator.State.GetCreature(p.Creature);
            int hp = cs.IsAlive ? cs.CurrentHp : 0;
            int ostyHp = 0;
            if (cs.IsAlive && Combat.GetOsty(p) is { } osty)
            {
                SimCreatureState os = Simulator.State.GetCreature(osty);
                if (os.IsAlive)
                    ostyHp = os.CurrentHp;
            }
            double powerVal = cs.IsAlive ? PartyEvaluator.EvaluateTeammateBuffPowers(Combat, p.Creature) : 0;
            _teammatePostRootSnapshot[id] = (hp, ostyHp, powerVal);
        }
    }

    public SimStepState Fork() => new(
        Simulator.Fork(),
        ProcessedEnemyDeaths.Fork(),
        KnownTopRemaining,
        _teammateDeathTurns is { Count: > 0 } d ? new Dictionary<uint, int>(d) : null,
        TeammateSupportValue,
        _teammatePostRootSnapshot is { Count: > 0 } s ? new Dictionary<uint, (int, int, double)>(s) : null);
}

internal readonly record struct StepOutcome(
    PlanAction Action,
    SimStepState State,
    bool RevealedInformation,
    bool EndedCombat,
    bool AdvancedTurn);

internal static class CombatStepEngine
{
    public static SimStepState CreateRoot(CombatRootSnapshot root, int initialKnownTop)
    {
        CombatPredictionSimulator sim = root.ForkSimulator();
        ForkableSet<uint> deaths = new();
        SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
        foreach (Creature enemy in combat.Enemies)
        {
            if (enemy.CombatId is uint id && sim.State.GetCreature(enemy).IsDead)
                deaths.Add(id);
        }
        return new SimStepState(sim, deaths, initialKnownTop);
    }

    public static List<PlanAction> EnumerateBaseActions(
        SimStepState state,
        Player player,
        SolverDisplayNames names,
        bool includePotions)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;
        int turn = combat.GetPlayerTurnNumber(player);
        List<PlanAction> actions = [];
        if (!sim.IsInProgress || sim.State.GetCreature(player.Creature).IsDead)
            return actions;

        SimPlayerCombatState ps = sim.State.GetPlayerCombatState(player);
        IReadOnlyList<PredictedCard> handCards = ps.Hand.Cards;
        int handCount = handCards.Count;
        string[]? seenKeys = handCount > 0 ? new string[handCount] : null;
        string[]? seenIds = handCount > 0 ? new string[handCount] : null;

        for (int handIdx = 0; handIdx < handCount; handIdx++)
        {
            PredictedCard card = handCards[handIdx];
            string cardId = card.Preview.Id.Entry;
            string stateKey = CardChoiceSupport.ChoiceCardKey(card);
            int cardOcc = 0;
            bool dupStateKey = false;
            for (int prev = 0; prev < handIdx; prev++)
            {
                if (string.Equals(seenIds![prev], cardId, StringComparison.Ordinal))
                    cardOcc++;
                if (string.Equals(seenKeys![prev], stateKey, StringComparison.Ordinal))
                    dupStateKey = true;
            }
            seenIds![handIdx] = cardId;
            seenKeys![handIdx] = stateKey;

            if (dupStateKey)
                continue;
            int stateOcc = 0;
            if (!combat.CanPlayCard(sim, card) || CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview))
                continue;

            TargetType tt = card.Preview.TargetType;
            if (tt is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer)
            {
                IReadOnlyList<Creature> targets = combat.GetValidManualCardTargets(card.Preview);
                int idx = 0;
                foreach (Creature target in targets)
                {
                    if (!sim.State.GetCreature(target).IsAlive)
                        continue;
                    if (tt == TargetType.AnyEnemy && !sim.State.IsHittable(target))
                        continue;
                    actions.Add(new PlanAction(
                        PlanActionKind.PlayCard,
                        turn,
                        CardId: cardId,
                        CardOccurrence: cardOcc,
                        TargetIndex: idx++,
                        TargetCombatId: target.CombatId,
                        CardTitle: names.Card(card.Preview),
                        TargetName: names.Creature(target),
                        CardStateKey: stateKey,
                        CardStateOccurrence: stateOcc,
                        CardUpgradeLevel: card.Preview.CurrentUpgradeLevel,
                        CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? ""));
                }
            }
            else
            {
                actions.Add(new PlanAction(
                    PlanActionKind.PlayCard,
                    turn,
                    CardId: cardId,
                    CardOccurrence: cardOcc,
                    CardTitle: names.Card(card.Preview),
                    CardStateKey: stateKey,
                    CardStateOccurrence: stateOcc,
                    CardUpgradeLevel: card.Preview.CurrentUpgradeLevel,
                    CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? ""));
            }
        }

        if (includePotions)
        {
            HashSet<string> seenPotions = new(StringComparer.Ordinal);
            for (int slot = 0; slot < player.PotionSlots.Count; slot++)
            {
                if (!combat.IsPotionAvailable(player, slot))
                    continue;
                PotionModel? potion = combat.GetPotionAtSlot(player, slot);
                if (potion == null)
                    continue;
                if (!seenPotions.Add(potion.Id.Entry))
                    continue;
                TargetType tt = potion.TargetType;
                if (tt is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer)
                {
                    IReadOnlyList<Creature> targets = combat.GetValidManualTargets(player.Creature, tt);
                    int idx = 0;
                    foreach (Creature target in targets)
                    {
                        if (!sim.State.GetCreature(target).IsAlive)
                            continue;
                        if (tt == TargetType.AnyEnemy && !sim.State.IsHittable(target))
                            continue;
                        actions.Add(new PlanAction(
                            PlanActionKind.UsePotion,
                            turn,
                            TargetIndex: idx++,
                            TargetCombatId: target.CombatId,
                            TargetName: names.Creature(target),
                            PotionSlot: slot,
                            PotionId: potion.Id.Entry,
                            PotionTitle: names.Potion(potion)));
                    }
                }
                else
                {
                    actions.Add(new PlanAction(
                        PlanActionKind.UsePotion,
                        turn,
                        PotionSlot: slot,
                        PotionId: potion.Id.Entry,
                        PotionTitle: names.Potion(potion)));
                }
            }
        }

        actions.Add(new PlanAction(PlanActionKind.EndTurn, turn));
        return actions;
    }

    public static List<StepOutcome> ExpandAction(
        SimStepState parent,
        PlanAction baseAction,
        Player player,
        SolverDisplayNames names,
        int maxChoiceBranches,
        PartyRootBaseline? baseline = null,
        int maxChoiceDepth = 3,
        bool skipCanPlayCheck = false)
    {
        List<StepOutcome> results = [];
        ExpandActionRecursive(parent, baseAction, player, names, maxChoiceBranches, baseline, maxChoiceDepth, results, skipCanPlayCheck);
        return results;
    }

    private static void ExpandActionRecursive(
        SimStepState parent,
        PlanAction action,
        Player player,
        SolverDisplayNames names,
        int maxChoiceBranches,
        PartyRootBaseline? baseline,
        int remainingDepth,
        List<StepOutcome> results,
        bool skipCanPlayCheck = false)
    {
        SimStepState next = parent.Fork();
        bool completed;
        bool revealed;
        bool advancedTurn;
        try
        {
            completed = ExecuteSingleAction(next, action, player, out revealed, out advancedTurn, skipCanPlayCheck);
        }
        catch (InvalidPlannedChoiceBranchException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (NotSupportedException)
        {
            return;
        }

        if (completed)
        {
            bool ended = !next.Simulator.IsInProgress
                || next.Simulator.State.GetCreature(player.Creature).IsDead
                || next.Combat.BattlewornDummyTimedOut;
            results.Add(new StepOutcome(action, next, revealed, ended, advancedTurn));
            return;
        }

        if (remainingDepth <= 0)
            return;

        SimulatedCombatState combat = next.Combat;
        if (action.Kind == PlanActionKind.UsePotion && action.Choice == null)
        {
            PotionModel? potion = parent.Combat.GetPotionAtSlot(player, action.PotionSlot);
            if (potion != null && PotionChoiceSupport.RequiresChoice(potion))
            {
                SimStepState prep = parent.Fork();
                Creature? pTarget = prep.Combat.GetCreature(action.TargetCombatId);
                prep.Combat.BeginActionChoices(action.NestedChoices);
                bool prepared;
                try
                {
                    prepared = PotionExecutionSupport.Prepare(prep.Simulator, prep.Combat, potion, action.PotionSlot, pTarget);
                }
                finally
                {
                    prep.Combat.EndActionChoices();
                }
                if (prepared)
                {
                    CardChoiceSpec pSpec = PotionChoiceSupport.GetSpec(prep.Simulator, potion);
                    IReadOnlyList<PlanCardChoice> pBranches = CardChoiceSupport.BuildChoices(pSpec, names, maxChoiceBranches, maxChoiceBranches);
                    int pTake = Math.Min(maxChoiceBranches, pBranches.Count);
                    for (int i = 0; i < pTake; i++)
                    {
                        PlanAction updated = action with { Choice = pBranches[i] };
                        ExpandActionRecursive(parent, updated, player, names, maxChoiceBranches, baseline, remainingDepth - 1, results);
                    }
                    return;
                }
            }
        }
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> knowledgeChoices = KnowledgeDemonChoiceSupport.BuildChoices(knowledgeRequest, names);
            int limit = Math.Min(maxChoiceBranches, knowledgeChoices.Count);
            for (int i = 0; i < limit; i++)
            {
                PlanAction updated = AttachChoice(action, knowledgeChoices[i], isPrimary: false, isTurnStart: action.Kind == PlanActionKind.EndTurn);
                ExpandActionRecursive(parent, updated, player, names, maxChoiceBranches, baseline, remainingDepth - 1, results);
            }
            return;
        }

        if (combat.PendingTurnStartChoice is not { } request)
            return;

        CardChoiceSpec spec;
        try
        {
            spec = TurnStartChoiceSupport.BuildPendingSpec(next.Simulator, combat, player);
        }
        catch
        {
            return;
        }

        int branchCap = request.Spec?.MaxBranches is int explicitCap
            ? Math.Min(maxChoiceBranches, explicitCap)
            : maxChoiceBranches;
        IReadOnlyList<PlanCardChoice> branches = ShouldUseSimulatorChoiceRanking(spec)
            ? BuildSimulatorRankedChoices(parent, spec, player, names, branchCap, baseline)
            : CardChoiceSupport.BuildChoices(spec, names, branchCap, branchCap);
        bool isTurnStart = action.Kind == PlanActionKind.EndTurn
            || request.Timing is PlanChoiceTiming.PlayerTurnEnd or PlanChoiceTiming.EnemyTurn or PlanChoiceTiming.PlayerTurnStart;
        bool isPrimary = !isTurnStart && string.IsNullOrEmpty(request.SourceId) && action.Choice == null;

        int take = Math.Min(branchCap, branches.Count);
        for (int i = 0; i < take; i++)
        {
            PlanCardChoice tagged = branches[i] with
            {
                SourceId = request.SourceId,
                ContextId = request.ContextId,
                Timing = request.Timing,
            };
            PlanAction updated = AttachChoice(action, tagged, isPrimary, isTurnStart);
            ExpandActionRecursive(parent, updated, player, names, maxChoiceBranches, baseline, remainingDepth - 1, results);
        }
    }

    private static bool ShouldUseSimulatorChoiceRanking(CardChoiceSpec spec)
        => !spec.IsImplicitAllSelection
            && spec.MaxCount == 1
            && spec.Options.Count > 0
            && spec.Effect is PlanChoiceEffect.Modify
                or PlanChoiceEffect.Nightmare
                or PlanChoiceEffect.Duplicate
                or PlanChoiceEffect.Upgrade;

    private static IReadOnlyList<PlanCardChoice> BuildSimulatorRankedChoices(
        SimStepState stateAtChoice,
        CardChoiceSpec spec,
        Player player,
        SolverDisplayNames names,
        int branchCap,
        PartyRootBaseline? baseline)
    {
        PartyRootBaseline evalBaseline = baseline ?? CaptureFallbackBaseline(stateAtChoice, player);
        List<(PlanCardChoice Choice, double Score, string Key)> ranked = [];
        HashSet<string> seenKeys = new(StringComparer.Ordinal);

        int remainingEnemyHp = 0;
        foreach (Creature enemy in stateAtChoice.Combat.Enemies)
        {
            SimCreatureState es = stateAtChoice.Simulator.State.GetCreature(enemy);
            int effHp = stateAtChoice.Combat.EffectiveEnemyHp(enemy, es);
            if (es.IsAlive || effHp > 0)
                remainingEnemyHp += Math.Max(0, effHp);
        }
        double turnsLeft = Math.Clamp(remainingEnemyHp / 18.0, 1.0, 8.0);
        int maxEnergy = Math.Max(1, PersistentPowerSupport.GetModifiedMaxEnergy(stateAtChoice.Combat, player));

        foreach (PredictedCard option in spec.Options)
        {
            string key = CardChoiceSupport.ChoiceCardKey(option);
            if (!seenKeys.Add(key))
                continue;
            double score = ScoreSingleChoiceTarget(stateAtChoice, option, spec.Effect, player, evalBaseline, turnsLeft, maxEnergy);
            PlanCardToken token = CreateChoiceToken(option, spec.Options, spec.SourceCards, names);
            ranked.Add((new PlanCardChoice(spec.Effect, spec.SourcePile, [token], ContextId: spec.ContextId), score, key));
        }

        List<PlanCardChoice> ordered = ranked
            .OrderByDescending(item => Math.Round(item.Score, 6))
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(branchCap)
            .Select(item => item.Choice)
            .ToList();

        if (spec.MinCount == 0 && ordered.Count < branchCap)
            ordered.Add(new PlanCardChoice(spec.Effect, spec.SourcePile, [], ContextId: spec.ContextId));

        return ordered;
    }

    private static double ScoreSingleChoiceTarget(
        SimStepState stateAtChoice,
        PredictedCard option,
        PlanChoiceEffect effect,
        Player player,
        PartyRootBaseline baseline,
        double turnsLeft,
        int maxEnergy)
    {
        SimPlayerCombatState ps = stateAtChoice.Simulator.State.GetPlayerCombatState(player);
        int currentCost = option.Preview.EnergyCost.CostsX ? 0 : Math.Max(0, option.GetEnergyCostWithModifiers(stateAtChoice.Simulator, ps));
        double baseDelta = MeasureSinglePlayDelta(stateAtChoice, option, player, baseline, upgradeBeforePlay: false);

        if (effect == PlanChoiceEffect.Upgrade)
        {
            double upgradedDelta = MeasureSinglePlayDelta(stateAtChoice, option, player, baseline, upgradeBeforePlay: true);
            double deltaGain = upgradedDelta - baseDelta;
            bool isPower = option.Preview.Type == CardType.Power;
            bool exhausts = option.HasKeyword(stateAtChoice.Simulator.State, CardKeyword.Exhaust);
            double mult = isPower ? 1.0 : (exhausts ? 1.0 : Math.Clamp(turnsLeft / 3.0, 1.0, 2.5));
            return deltaGain * mult;
        }

        if (effect == PlanChoiceEffect.Modify)
        {
            int newCost = option.Preview.EnergyCost.CostsX ? 0 : currentCost + 1;
            bool isPower = option.Preview.Type == CardType.Power;
            bool exhausts = option.HasKeyword(stateAtChoice.Simulator.State, CardKeyword.Exhaust);
            double cycles = isPower ? 1.0 : (exhausts ? 1.0 : Math.Clamp(turnsLeft / 3.0, 1.0, 2.5));
            double extraPlayValue = baseDelta * cycles;
            double energyTax = newCost > maxEnergy ? 12.0 : 1.6 * cycles;
            if (isPower && turnsLeft >= 3.0 && newCost <= maxEnergy)
                extraPlayValue *= 1.15;
            return extraPlayValue - energyTax;
        }

        if (effect == PlanChoiceEffect.Nightmare)
            return baseDelta * 3.0 - currentCost * 1.4;

        double dupScale = option.Preview.Type == CardType.Power ? 1.25 : 1.0;
        return baseDelta * dupScale - currentCost * 0.7;
    }

    private static double MeasureSinglePlayDelta(
        SimStepState stateAtChoice,
        PredictedCard option,
        Player player,
        PartyRootBaseline baseline,
        bool upgradeBeforePlay)
    {
        SimStepState probe = stateAtChoice.Fork();
        CombatPredictionSimulator sim = probe.Simulator;
        SimulatedCombatState combat = probe.Combat;
        SimPlayerCombatState ps = sim.State.GetPlayerCombatState(player);
        PredictedCard? probeCard = ps.Hand.Cards.FirstOrDefault(c => ReferenceEquals(c.Original, option.Original))
            ?? ps.Hand.Cards.FirstOrDefault(c => string.Equals(CardChoiceSupport.ChoiceCardKey(c), CardChoiceSupport.ChoiceCardKey(option), StringComparison.Ordinal));
        if (probeCard == null)
            return 0;

        if (upgradeBeforePlay && probeCard.Preview.IsUpgradable)
            probeCard.Upgrade();

        int energyCost = probeCard.GetEnergyCostWithModifiers(sim, ps);
        int starCost = probeCard.GetStarCostWithModifiers(sim, ps);
        if (energyCost > ps.Energy)
            ps.GainEnergy(energyCost - ps.Energy);
        if (starCost > ps.Stars)
            ps.GainStars(starCost - ps.Stars);

        double beforeScore = PartyEvaluator.EvaluateTerminalOrHorizon(probe, baseline, includePendingIncomingThreat: true);
        Creature? target = ResolveBestProbeTarget(probe, probeCard, player);
        try
        {
            combat.BeginActionChoices(TurnStartChoiceCursor.ForAutomaticPolicy(req =>
                req.Spec != null ? CardChoiceSupport.BuildAutomaticPolicyChoice(req.Spec) : null));
            try
            {
                using (combat.BeginCardExecutionScope(probe.ProcessedEnemyDeaths))
                {
                    if (!sim.ManualPlay(probeCard, target, out _))
                        return 0;
                    using (sim.BeginExecutionDispatch())
                        CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, probe.ProcessedEnemyDeaths);
                    SettleActionBoundary(sim, combat);
                }
            }
            finally
            {
                combat.EndActionChoices();
            }
        }
        catch
        {
            return 0;
        }

        double afterScore = PartyEvaluator.EvaluateTerminalOrHorizon(probe, baseline, includePendingIncomingThreat: true);
        return afterScore - beforeScore;
    }

    private static Creature? ResolveBestProbeTarget(SimStepState probe, PredictedCard card, Player player)
    {
        TargetType tt = card.Preview.TargetType;
        if (tt is not (TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer))
            return null;
        IReadOnlyList<Creature> targets = probe.Combat.GetValidManualCardTargets(card.Preview);
        Creature? best = null;
        int bestHp = int.MaxValue;
        foreach (Creature t in targets)
        {
            SimCreatureState ts = probe.Simulator.State.GetCreature(t);
            if (!ts.IsAlive)
                continue;
            if (tt == TargetType.AnyEnemy && !probe.Simulator.State.IsHittable(t))
                continue;
            if (ts.CurrentHp < bestHp)
            {
                bestHp = ts.CurrentHp;
                best = t;
            }
        }
        return best;
    }

    private static PlanCardToken CreateChoiceToken(
        PredictedCard card,
        IReadOnlyList<PredictedCard> options,
        IReadOnlyList<PredictedCard> source,
        SolverDisplayNames names)
    {
        string stateKey = CardChoiceSupport.ChoiceCardKey(card);
        int sourceOcc = CountSameStateKeyBefore(source, card, stateKey);
        int optionOcc = ReferenceEquals(source, options)
            ? sourceOcc
            : CountSameStateKeyBefore(options, card, stateKey);
        return new PlanCardToken(
            card.Preview.Id.Entry,
            card.Preview.CurrentUpgradeLevel,
            stateKey,
            sourceOcc,
            optionOcc,
            names.Card(card.Preview));
    }

    private static int CountSameStateKeyBefore(IReadOnlyList<PredictedCard> list, PredictedCard target, string stateKey)
    {
        int count = 0;
        foreach (PredictedCard item in list)
        {
            if (ReferenceEquals(item, target))
                return count;
            if (string.Equals(CardChoiceSupport.ChoiceCardKey(item), stateKey, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static PartyRootBaseline CaptureFallbackBaseline(SimStepState state, Player player)
    {
        Dictionary<uint, int> hp = [];
        Dictionary<uint, int> maxHp = [];
        Dictionary<uint, int> empty = [];
        foreach (Player p in state.Combat.Players)
        {
            uint id = p.Creature.CombatId ?? 0;
            SimCreatureState cs = state.Simulator.State.GetCreature(p.Creature);
            hp[id] = cs.CurrentHp;
            maxHp[id] = cs.MaxHp;
        }
        int enemyHp = 0;
        foreach (Creature e in state.Combat.Enemies)
        {
            SimCreatureState es = state.Simulator.State.GetCreature(e);
            if (es.IsAlive)
                enemyHp += es.CurrentHp;
        }
        return new PartyRootBaseline(
            player,
            state.Combat.GetPlayerTurnNumber(player),
            hp, maxHp, empty, empty, empty,
            new Dictionary<int, int>(),
            state.Combat.BrightestFlameMaxHpSpent,
            Math.Max(1, enemyHp),
            default,
            false);
    }

    private static PlanAction AttachChoice(PlanAction action, PlanCardChoice choice, bool isPrimary, bool isTurnStart)
    {
        if (isTurnStart)
        {
            List<PlanCardChoice> existing = action.TurnStartChoices is { } list ? [.. list, choice] : [choice];
            return action with { TurnStartChoices = existing };
        }
        if (isPrimary)
            return action with { Choice = choice };

        List<PlanCardChoice> nested = action.NestedChoices is { } nl ? [.. nl, choice] : [choice];
        int beforePrimary = action.Choice == null ? nested.Count : action.NestedChoicesBeforePrimary;
        return action with { NestedChoices = nested, NestedChoicesBeforePrimary = beforePrimary };
    }

    public static bool TryReplayAction(
        SimStepState state,
        PlanAction action,
        Player player,
        out bool revealed,
        out bool advancedTurn)
    {
        try
        {
            return ExecuteSingleAction(state, action, player, out revealed, out advancedTurn);
        }
        catch (InvalidPlannedChoiceBranchException)
        {
            revealed = false;
            advancedTurn = false;
            return false;
        }
        catch (InvalidOperationException)
        {
            revealed = false;
            advancedTurn = false;
            return false;
        }
        catch (NotSupportedException)
        {
            revealed = false;
            advancedTurn = false;
            return false;
        }
    }

    private static bool ExecuteSingleAction(
        SimStepState state,
        PlanAction action,
        Player player,
        out bool revealed,
        out bool advancedTurn,
        bool skipCanPlayCheck = false)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;
        revealed = false;
        advancedTurn = false;

        if (!sim.IsInProgress || sim.State.GetCreature(player.Creature).IsDead)
            return true;

        int turnBefore = combat.GetPlayerTurnNumber(player);
        int historyStart = sim.History.Entries.Count;
        int shufflesBefore = sim.ShuffleEventCount;
        int hittableBefore = combat.HittableEnemies.Count;
        RngStamp rngBefore = RngStamp.Capture(sim, player);

        if (action.Kind == PlanActionKind.EndTurn)
        {
            revealed = true;
            advancedTurn = true;
            bool ok = AdvanceRound(state, player, action.TurnStartChoices);
            _ = combat.ConsumePlayerTurnEndRequest();
            if (!ok || !SettleActionBoundary(sim, combat))
                return false;
            state.KnownTopRemaining = 0;
            return true;
        }

        if (action.Kind == PlanActionKind.UsePotion)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, action.PotionSlot);
            if (potion == null || !string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal)
                || !combat.IsPotionAvailable(player, action.PotionSlot))
            {
                return false;
            }
            Creature? target = combat.GetCreature(action.TargetCombatId);
            combat.BeginActionChoices(action.NestedChoices);
            try
            {
                if (PotionChoiceSupport.RequiresChoice(potion) && action.Choice == null)
                    return false;
                if (!PotionExecutionSupport.Prepare(sim, combat, potion, action.PotionSlot, target))
                    return false;
                if (!PotionExecutionSupport.Complete(sim, combat, potion, target, action.Choice, historyStart, state.ProcessedEnemyDeaths))
                    return false;
                if (!SettleActionBoundary(sim, combat))
                    return false;
            }
            finally
            {
                combat.EndActionChoices();
            }
            sim.CheckWinCondition(combat.GetPlayerTurnNumber(player));
            if (combat.ConsumePlayerTurnEndRequest() && sim.IsInProgress)
            {
                revealed = true;
                advancedTurn = true;
                if (!AdvanceRound(state, player, action.TurnStartChoices) || !SettleActionBoundary(sim, combat))
                    return false;
                _ = combat.ConsumePlayerTurnEndRequest();
                state.KnownTopRemaining = 0;
                return true;
            }
            revealed = DetectInformationReveal(state, player, historyStart, shufflesBefore, rngBefore, turnBefore, hittableBefore);
            return true;
        }

        SimPlayerCombatState ps = sim.State.GetPlayerCombatState(player);
        PredictedCard? card = FindCard(ps.Hand.Cards, action);
        if (card == null || (!skipCanPlayCheck && !combat.CanPlayCard(sim, card)))
            return false;

        (int Energy, int Hand, int Deck)[]? allyBefore = CaptureAllyResources(sim, combat, player);
        Creature? cardTarget = combat.GetCreature(action.TargetCombatId);
        combat.BeginActionChoices(action.GetActionChoicesInExecutionOrder());
        try
        {
            using (combat.BeginCardExecutionScope(state.ProcessedEnemyDeaths))
            {
                if (!sim.ManualPlay(card, cardTarget, out _))
                    return false;
                bool deathsOk;
                using (sim.BeginExecutionDispatch())
                    deathsOk = CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths);
                if (!deathsOk || !SettleActionBoundary(sim, combat))
                    return false;
            }
        }
        finally
        {
            combat.EndActionChoices();
        }
        if (allyBefore != null)
            AccumulateAllySupport(state, player, allyBefore, turnBefore);

        sim.CheckWinCondition(combat.GetPlayerTurnNumber(player));
        if (combat.ConsumePlayerTurnEndRequest() && sim.IsInProgress)
        {
            revealed = true;
            advancedTurn = true;
            if (!AdvanceRound(state, player, action.TurnStartChoices) || !SettleActionBoundary(sim, combat))
                return false;
            _ = combat.ConsumePlayerTurnEndRequest();
            state.KnownTopRemaining = 0;
            return true;
        }

        revealed = DetectInformationReveal(state, player, historyStart, shufflesBefore, rngBefore, turnBefore, hittableBefore);
        return true;
    }

    private static (int Energy, int Hand, int Deck)[]? CaptureAllyResources(
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        Player localPlayer)
    {
        if (combat.Players.Count <= 1)
            return null;
        var snap = new (int Energy, int Hand, int Deck)[combat.Players.Count];
        for (int i = 0; i < combat.Players.Count; i++)
        {
            Player p = combat.Players[i];
            if (ReferenceEquals(p, localPlayer))
                continue;
            SimPlayerCombatState ps = sim.State.GetPlayerCombatState(p);
            snap[i] = (ps.Energy, ps.Hand.Cards.Count, ps.DrawPile.Cards.Count + ps.DiscardPile.Cards.Count + ps.Hand.Cards.Count);
        }
        return snap;
    }

    private static void AccumulateAllySupport(
        SimStepState state,
        Player localPlayer,
        (int Energy, int Hand, int Deck)[] before,
        int turnNumber)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;
        double decay = 1.0 / (1.0 + 0.15 * Math.Max(0, turnNumber - 1));
        for (int i = 0; i < combat.Players.Count && i < before.Length; i++)
        {
            Player p = combat.Players[i];
            if (ReferenceEquals(p, localPlayer) || sim.State.GetCreature(p.Creature).IsDead)
                continue;
            SimPlayerCombatState ps = sim.State.GetPlayerCombatState(p);
            int dEnergy = ps.Energy - before[i].Energy;
            int dHand = ps.Hand.Cards.Count - before[i].Hand;
            int dDeck = (ps.DrawPile.Cards.Count + ps.DiscardPile.Cards.Count + ps.Hand.Cards.Count) - before[i].Deck;
            if (dEnergy > 0)
                state.TeammateSupportValue += dEnergy * 2.4 * decay;
            if (dHand > 0)
                state.TeammateSupportValue += dHand * 1.4 * decay;
            if (dDeck > 0)
                state.TeammateSupportValue += dDeck * 0.45 * decay;
        }
    }

    private static bool DetectInformationReveal(
        SimStepState state,
        Player player,
        int historyStart,
        int shufflesBefore,
        RngStamp rngBefore,
        int turnBefore,
        int hittableBefore)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;

        if (combat.GetPlayerTurnNumber(player) != turnBefore)
        {
            state.KnownTopRemaining = 0;
            return true;
        }

        if (sim.ShuffleEventCount != shufflesBefore)
        {
            state.KnownTopRemaining = 0;
            return true;
        }

        int draws = 0;
        bool revealEvent = false;
        foreach (CombatPredictionHistoryEntry entry in sim.History.EntriesFrom(historyStart))
        {
            if (entry is CombatPredictionCardDrawnEntry)
                draws++;
            else if (entry is CombatPredictionCardGenerationOptionsEntry
                or CombatPredictionCardCostsRandomizedEntry
                or CombatPredictionAutoPlayFromDrawPileEntry
                or CombatPredictionPotionGeneratedEntry
                or CombatPredictionCardAfflictedEntry)
            {
                revealEvent = true;
            }
        }

        if (draws > state.KnownTopRemaining)
        {
            state.KnownTopRemaining = 0;
            revealEvent = true;
        }
        else if (draws > 0)
        {
            state.KnownTopRemaining -= draws;
        }

        RngStamp rngAfter = RngStamp.Capture(sim, player);
        if (rngAfter != rngBefore)
        {
            bool onlyTargetRngChanged = rngAfter with { Targets = rngBefore.Targets } == rngBefore;
            if (!onlyTargetRngChanged || Math.Max(hittableBefore, combat.HittableEnemies.Count) > 1)
                revealEvent = true;
        }

        return revealEvent;
    }

    private static bool AdvanceRound(
        SimStepState state,
        Player player,
        IReadOnlyList<PlanCardChoice>? turnStartChoices)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;
        if (!sim.IsInProgress)
            return true;

        IReadOnlyList<PlanCardChoice>? roundChoicePlans = turnStartChoices?
            .Where(choice => choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse || choice.Timing != PlanChoiceTiming.EnemyTurn)
            .ToArray();
        TurnStartChoiceCursor roundChoices = new(roundChoicePlans);
        combat.BeginActionChoices(roundChoices);
        combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
        try
        {
            int roundHistoryStart = sim.History.Entries.Count;
            int etherealExhaustCount = combat.CountEtherealCardsInHand(sim, player);

            if (!PlayerTurnEndLifecycle.RunPhaseOne(sim, combat, player, [player.Creature]))
                return false;
            combat.CommitHistoryCourseTurn(player);
            combat.NormalizeAeonglassWithers(sim);
            combat.NormalizeCardAfflictions(sim);
            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths))
                return false;
            if (!sim.IsInProgress)
                return true;

            CorePowerSupport.FlushPlayerHandAtTurnEnd(sim, combat, player);
            if (!PlayerTurnEndLifecycle.RunPhaseTwo(sim, combat, [player.Creature], etherealExhaustCount))
                return false;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths))
                return false;

            bool takingExtraTurn = HookMirrors.ShouldTakeExtraTurn(sim, combat, player);
            bool hasActiveEmotionChip = false;
            IReadOnlyList<RelicModel> playerRelics = combat.RelicsOf(player);
            for (int i = 0; i < playerRelics.Count; i++)
            {
                if (playerRelics[i] is EmotionChip chip && !chip.IsMelted)
                {
                    hasActiveEmotionChip = true;
                    break;
                }
            }
            SimCreatureState simPlayer = sim.State.GetCreature(player.Creature);

            if (!takingExtraTurn)
            {
                combat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
                Creature[] actingEnemies = combat.Enemies.ToArray();
                combat.CurrentSide = CombatSide.Enemy;
                foreach (Creature enemy in combat.Enemies)
                    combat.BeginSideTurn(enemy);
                combat.SnapshotPowerAmountsAtTurnStart(combat.Enemies);

                if (!HookMirrors.BeforeSideTurnStart(sim, combat.CurrentSide, combat.Enemies))
                    return false;

                foreach (Creature enemy in combat.Enemies)
                {
                    SimCreatureState simEnemy = sim.State.GetCreature(enemy);
                    if (simEnemy.Block > 0)
                    {
                        if (combat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                            simEnemy.DamageBlock(simEnemy.Block, ValueProp.Move);
                        else
                            PersistentRelicSupport.TriggerAfterPreventingBlockClear(sim, preventer, enemy);
                    }
                    if (!CorePowerSupport.TriggerAfterBlockCleared(sim, combat, enemy))
                        return false;
                }

                bool decrementEnemyPlating = combat.RoundNumber > 1;
                if (!combat.TriggerSideTurnStart(sim, CombatSide.Enemy, combat.Enemies, decrementEnemyPlating))
                    return false;

                int enemyPoisonStart = sim.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(sim, combat, combat.Enemies))
                    return false;
                TriggeredPowerSupport.CompensateHistorySince(sim, combat, enemyPoisonStart);
                if (combat.HasPendingChoice)
                    return false;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths))
                    return false;
                if (sim.CheckWinCondition(combat.GetPlayerTurnNumber(player)))
                    return true;

                Dictionary<Creature, MoveState> performedMoves = new(actingEnemies.Length);
                foreach (Creature actingEnemy in actingEnemies)
                {
                    if (!combat.CanPerformMonsterMove(sim, actingEnemy))
                        continue;
                    ForecastMove move = combat.CurrentMonsterMove(actingEnemy);
                    if (combat.ConsumeStunNextMove(actingEnemy))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (sim.CheckWinCondition(combat.GetPlayerTurnNumber(player)))
                            return true;
                        continue;
                    }
                    if (combat.TryConsumeForcedMonsterMove(actingEnemy, out string forcedMove, out int forcedDamage))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (forcedMove == "EXPLODE_MOVE")
                        {
                            MonsterMoveSemantics.DamagePlayer(sim, combat, move.Owner, player.Creature, forcedDamage);
                            if (combat.HasPendingChoice)
                                return false;
                            using (sim.PushDamageSource(CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, move.Owner.Monster?.Id.Entry)))
                                sim.Kill(move.Owner, force: true);
                            if (combat.HasPendingChoice)
                                return false;
                            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths))
                                return false;
                        }
                        if (sim.CheckWinCondition(combat.GetPlayerTurnNumber(player)))
                            return true;
                        continue;
                    }

                    bool playerDied = MonsterMoveSemantics.ApplyForecastMove(
                        sim, combat, move, player.Creature, state.ProcessedEnemyDeaths, turnStartChoices);
                    state.RecordTeammateDeaths(player, combat.GetPlayerTurnNumber(player));
                    performedMoves[actingEnemy] = move.Move;
                    if (move.Owner.CombatId is uint revivedId && sim.State.GetCreature(move.Owner).IsAlive)
                        state.ProcessedEnemyDeaths.Remove(revivedId);
                    if (combat.HasPendingChoice)
                        return false;
                    if (sim.CheckWinCondition(combat.GetPlayerTurnNumber(player)) || playerDied)
                        return true;
                }

                if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(sim, combat, combat.Enemies.ToArray()))
                    return false;
                if (combat.BattlewornDummyTimedOut)
                    return true;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths))
                    return false;

                int playerPoisonStart = sim.History.Entries.Count;
                List<Creature> alivePlayerCreatures = CollectAliveCreatures(sim, combat.PlayerCreatures);
                if (!CorePowerSupport.TriggerPoison(sim, combat, alivePlayerCreatures))
                    return false;
                TriggeredPowerSupport.CompensateHistorySince(sim, combat, playerPoisonStart);
                if (combat.HasPendingChoice)
                    return false;
                state.RecordTeammateDeaths(player, combat.GetPlayerTurnNumber(player));
                state.CaptureTeammatePostRootSnapshotIfUnset(player);
                combat.ClearNoDraw(player.Creature);
                combat.RecordRelicRoundDamage(sim, player, roundHistoryStart);
                if (sim.CheckWinCondition(combat.GetPlayerTurnNumber(player)))
                    return true;
                combat.PrepareMonsterMovesForNextRound(sim, performedMoves);
            }
            else
            {
                if (hasActiveEmotionChip)
                    combat.RecordRelicRoundDamage(sim, player, roundHistoryStart);
                if (!HookMirrors.AfterTakingExtraTurn(sim, combat, player))
                    return false;
            }

            return AdvancePlayerStart(state, player, roundChoices, takingExtraTurn);
        }
        finally
        {
            combat.EndActionChoices();
        }
    }

    private static bool AdvancePlayerStart(
        SimStepState state,
        Player player,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;
        SimPlayerCombatState ps = sim.State.GetPlayerCombatState(player);

        combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        combat.CurrentSide = CombatSide.Player;
        if (!takingExtraTurn)
            combat.RoundNumber++;
        combat.AdvancePlayerTurn(player);
        combat.BeginSideTurn(player.Creature);
        List<Creature> playerParticipants = CollectAliveCreatures(sim, combat.PlayerCreatures);
        combat.SnapshotPowerAmountsAtTurnStart(playerParticipants);

        if (!HookMirrors.BeforeSideTurnStart(sim, combat.CurrentSide, [player.Creature]))
            return false;

        foreach (Creature pc in playerParticipants)
        {
            SimCreatureState pcs = sim.State.GetCreature(pc);
            if (pcs.Block > 0)
            {
                if (combat.ShouldClearBlock(pc, out AbstractModel? preventer))
                    pcs.DamageBlock(pcs.Block, ValueProp.Move);
                else
                    PersistentRelicSupport.TriggerAfterPreventingBlockClear(sim, preventer, pc);
            }
            if (!CorePowerSupport.TriggerAfterBlockCleared(sim, combat, pc))
                return false;
        }

        if (PersistentRelicSupport.ShouldPlayerResetEnergy(combat, player))
            ps.LoseEnergy(ps.Energy);
        ps.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(combat, player)
            + combat.ConsumeEnergyNextTurn(player));
        if (combat.HasPendingChoice || !PersistentPowerSupport.TriggerAfterEnergyReset(sim, combat, player))
            return false;
        TurnStartRelicSupport.TriggerAfterEnergyReset(sim, combat, player);
        if (combat.HasPendingChoice)
            return false;
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(sim, combat, player);
        if (combat.HasPendingChoice)
            return false;

        combat.PrepareBeforeHandDraw(sim, player, roundChoices);
        if (combat.HasPendingChoice)
            return false;
        using (sim.BeginExecutionDispatch())
            PowerLifecycleSupport.ResolvePowerAmountChanges(sim, combat);
        if (combat.HasPendingChoice)
            return false;

        int drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, CombatManager.baseHandDrawCount);
        int drawHistoryStart = sim.History.Entries.Count;
        sim.Draw(player, drawCount, fromHandDraw: true);
        if (combat.HasPendingChoice)
            return false;
        using (sim.BeginExecutionDispatch())
            TriggeredPowerSupport.CompensateHistorySince(sim, combat, drawHistoryStart);
        if (combat.HasPendingChoice)
            return false;

        combat.TriggerAfterPlayerTurnStart(sim, player.Creature, roundChoices);
        if (combat.HasPendingChoice)
            return false;

        using (sim.BeginExecutionDispatch())
        {
            if (!combat.TriggerSideTurnStart(sim, CombatSide.Player, [player.Creature],
                    decrementPlating: combat.GetPlayerTurnNumber(player) != 1, takingExtraTurn))
            {
                return false;
            }
        }
        if (combat.HasPendingChoice)
            return false;

        using (sim.BeginExecutionDispatch())
        {
            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, state.ProcessedEnemyDeaths))
                return false;
        }
        if (combat.HasPendingChoice)
            return false;

        using (sim.BeginExecutionDispatch())
            EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(sim, player);
        if (combat.HasPendingChoice)
            return false;

        using (sim.BeginExecutionDispatch())
            combat.TriggerAutoPrePlayEarly(sim, player, combat.GetPlayerTurnNumber(player), roundChoices, state.ProcessedEnemyDeaths);
        if (combat.HasPendingChoice)
            return false;

        combat.NormalizeAeonglassWithers(sim);
        combat.NormalizeCardAfflictions(sim);
        sim.CheckWinCondition(combat.GetPlayerTurnNumber(player));
        return true;
    }

    private static bool SettleActionBoundary(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        using IDisposable? dispatch = sim.BeginExecutionDispatch();
        sim.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(sim, combat);
        if (sim.HasPendingChoice)
            return false;
        combat.NormalizeAeonglassWithers(sim);
        if (sim.HasPendingChoice)
            return false;
        combat.NormalizeCardAfflictions(sim);
        return !sim.HasPendingChoice;
    }

    private static PredictedCard? FindCard(IReadOnlyList<PredictedCard> cards, PlanAction action)
    {
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            int occ = action.CardStateOccurrence;
            foreach (PredictedCard c in cards)
            {
                if (!string.Equals(CardChoiceSupport.ChoiceCardKey(c), action.CardStateKey, StringComparison.Ordinal))
                    continue;
                if (occ-- == 0)
                    return c;
            }
            return null;
        }
        int idOcc = action.CardOccurrence;
        foreach (PredictedCard c in cards)
        {
            if (!string.Equals(c.Preview.Id.Entry, action.CardId, StringComparison.Ordinal))
                continue;
            if (idOcc-- == 0)
                return c;
        }
        return null;
    }

    public static string ActionKey(PlanAction action)
    {
        string choices = string.Join(';', action.GetActionChoicesInExecutionOrder().Select(choice =>
            choice.Effect + ":" + string.Join(',', choice.Cards.Select(card =>
                $"{card.CardId}+{card.UpgradeLevel}#{card.OptionOccurrence}"))));
        return $"{action.Kind}|{action.CardStateKey}@{action.CardStateOccurrence}|{action.CardId}#{action.CardOccurrence}"
            + $"|{action.TargetCombatId}|{action.PotionSlot}|{choices}";
    }

    public static string UnchosenActionKey(PlanAction action)
        => ActionKey(action with { Choice = null, NestedChoices = null, NestedChoicesBeforePrimary = 0 });

    public static bool HasRevealedChoice(PlanAction action)
        => action.GetActionChoicesInExecutionOrder().Any(c => c.Effect == PlanChoiceEffect.GenerateToHand);

    public static string PrefixKey(IReadOnlyList<PlanAction> actions)
    {
        if (actions.Count == 0)
            return "-";
        if (actions.Count == 1)
            return HasRevealedChoice(actions[0]) ? UnchosenActionKey(actions[0]) : ActionKey(actions[0]);
        return string.Join(" ; ", actions.Select((a, idx) =>
            idx == actions.Count - 1 && HasRevealedChoice(a) ? UnchosenActionKey(a) : ActionKey(a)));
    }

    private static List<Creature> CollectAliveCreatures(
        CombatPredictionSimulator sim,
        IReadOnlyList<Creature> creatures)
    {
        List<Creature> alive = new(creatures.Count);
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature c = creatures[i];
            if (sim.State.GetCreature(c).IsAlive)
                alive.Add(c);
        }
        return alive;
    }
}
