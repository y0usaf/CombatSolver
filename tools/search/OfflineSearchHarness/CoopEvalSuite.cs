using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using CombatSolver.NewSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal static class CoopEvalSuite
{
    private sealed record ScenarioSpec(
        string Id,
        int ExpectedPlayers,
        string TargetCoopCard,
        bool ExpectsAllyTarget,
        int MaxTurns);

    private static readonly Dictionary<string, ScenarioSpec> Specs = new(StringComparer.Ordinal)
    {
        ["lift-rescue-2p"] = new("lift-rescue-2p", 2, "LIFT", true, 8),
        ["believe-synergy-2p"] = new("believe-synergy-2p", 2, "BELIEVE_IN_YOU", true, 8),
        ["legion-2p"] = new("legion-2p", 2, "LEGION_OF_BONE", false, 8),
        ["legion-4p"] = new("legion-4p", 4, "LEGION_OF_BONE", false, 8),
        ["rally-2p"] = new("rally-2p", 2, "RALLY", false, 8),
        ["rally-4p"] = new("rally-4p", 4, "RALLY", false, 8),
        ["beacon-synergy-2p"] = new("beacon-synergy-2p", 2, "BEACON_OF_HOPE", false, 8),
        ["beacon-synergy-4p"] = new("beacon-synergy-4p", 4, "BEACON_OF_HOPE", false, 8),
        ["energy-surge-2p"] = new("energy-surge-2p", 2, "ENERGY_SURGE", false, 8),
        ["energy-surge-4p"] = new("energy-surge-4p", 4, "ENERGY_SURGE", false, 8),
    };

    internal static void Run(CombatState combat, HarnessOptions options, MainLoopContext loop, string arg)
    {
        string[] parts = arg.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Specs.TryGetValue(parts[0], out ScenarioSpec? spec) || parts[1] is not ("v2" or "v3"))
            throw new ArgumentException($"OFFLINE_HARNESS_COOP_EVAL must be <scenario>:<v2|v3>, got '{arg}'.");
        string mode = parts[1];
        if (combat.Players.Count != spec.ExpectedPlayers)
            throw new InvalidOperationException($"Scenario {spec.Id} requires {spec.ExpectedPlayers} players, got {combat.Players.Count}.");

        InstallTimeBypass();
        Player local = LocalContext.GetMe(combat) ?? combat.Players[0];
        Player primaryAlly = combat.Players.First(p => !ReferenceEquals(p, local));

        SetupScenario(combat, local, primaryAlly, spec.Id, loop);

        Dictionary<ulong, int> startHpByPlayer = combat.Players.ToDictionary(p => p.NetId, p => p.Creature.CurrentHp);
        int localStartHp = local.Creature.CurrentHp;

        SolverSpeed speed = SolverSpeed.Normal;
        FairPlannerSettings v2Settings = FairPlannerSettings.For(speed) with { Samples = 8, MaxParallelism = 4 };
        NewSolverConfig v3Config = NewSolverConfig.For(speed) with { Samples = 8, MaxParallelism = 4 };
        SolverSettingsSnapshot solverSettings = SolverSettings.Capture();
        SolverSearchProfile v2Profile = v2Settings.Profile(solverSettings.Profile);

        bool coopCardPlayed = false;
        bool allyTargetMatched = false;
        List<string> firstTurnCommitted = [];
        List<double> decisionMs = [];
        int decisions = 0;
        int lastTurn = 1;
        string outcome = "turn_cap";

        while (true)
        {
            string? terminal = CheckTerminal(combat, local);
            if (terminal != null)
            {
                outcome = terminal;
                break;
            }
            int turn = local.PlayerCombatState!.TurnNumber;
            lastTurn = turn;
            if (turn > spec.MaxTurns)
            {
                outcome = "turn_cap";
                break;
            }

            if (!CombatManager.Instance.IsPlayerReadyToEndTurn(local))
            {
                WaitForQueueIdle(loop);
                terminal = CheckTerminal(combat, local);
                if (terminal != null)
                {
                    outcome = terminal;
                    break;
                }

                Stopwatch sw = Stopwatch.StartNew();
                IReadOnlyList<PlanAction> planned = DecideLocal(
                    combat, local, mode, v2Settings, v2Profile, solverSettings, v3Config, loop);
                sw.Stop();
                decisions++;
                decisionMs.Add(sw.Elapsed.TotalMilliseconds);

                if (turn == 1)
                {
                    foreach (PlanAction act in planned)
                        firstTurnCommitted.Add($"{act.Kind}:{act.CardId}{act.PotionId}>{act.TargetCombatId}");
                }

                bool endedLocalTurn = false;
                foreach (PlanAction action in planned)
                {
                    if (action.Kind == PlanActionKind.PlayCard && string.Equals(action.CardId, spec.TargetCoopCard, StringComparison.Ordinal))
                    {
                        coopCardPlayed = true;
                        if (spec.ExpectsAllyTarget && action.TargetCombatId == primaryAlly.Creature.CombatId)
                            allyTargetMatched = true;
                    }
                    if (!ExecuteLocalAction(combat, local, action, loop))
                        break;
                    if (action.Kind == PlanActionKind.EndTurn)
                    {
                        endedLocalTurn = true;
                        break;
                    }
                    if (CheckTerminal(combat, local) != null)
                        break;
                }

                if (!endedLocalTurn && CheckTerminal(combat, local) == null && !CombatManager.Instance.IsPlayerReadyToEndTurn(local))
                {
                    if (decisions > spec.MaxTurns * 12)
                    {
                        ExecuteLocalAction(combat, local, new PlanAction(PlanActionKind.EndTurn, turn), loop);
                    }
                    else
                    {
                        continue;
                    }
                }
            }

            if (CheckTerminal(combat, local) is { } postLocalTerminal)
            {
                outcome = postLocalTerminal;
                break;
            }

            RunTeammateTurns(combat, local, turn, loop);
            WaitForNextTurnOrEnd(combat, local, turn, loop);
        }

        int localEndHp = Math.Max(0, local.Creature.CurrentHp);
        int localHpLost = Math.Max(0, localStartHp - localEndHp);
        int partyHpLost = 0;
        int teammatesAlive = 0;
        foreach (Player p in combat.Players)
        {
            int lost = Math.Max(0, startHpByPlayer[p.NetId] - Math.Max(0, p.Creature.CurrentHp));
            partyHpLost += lost;
            if (!ReferenceEquals(p, local) && p.Creature.IsAlive)
                teammatesAlive++;
        }

        double meanMs = decisionMs.Count > 0 ? decisionMs.Average() : 0;
        var summary = new
        {
            scenario = spec.Id,
            players = combat.Players.Count,
            mode,
            outcome,
            turns = lastTurn,
            localHpLost,
            partyHpLost,
            teammatesAlive,
            teammateCount = combat.Players.Count - 1,
            coopCardPlayed,
            allyTargetMatched = spec.ExpectsAllyTarget ? allyTargetMatched : (bool?)null,
            decisions,
            meanDecisionMs = Math.Round(meanMs, 1),
            firstTurnCommitted,
        };

        string outPath = Path.Combine(options.OutputDirectory, $"coop-eval-{spec.Id}-{mode}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"COOP_EVAL scenario={spec.Id} players={combat.Players.Count} mode={mode} outcome={outcome} " +
            $"turns={lastTurn} local_hp_lost={localHpLost} party_hp_lost={partyHpLost} " +
            $"teammates_alive={teammatesAlive}/{combat.Players.Count - 1} coop_card={coopCardPlayed} " +
            $"ally_target={allyTargetMatched} mean_ms={meanMs:F1}");
    }

    private static void SetupScenario(CombatState combat, Player local, Player ally, string scenarioId, MainLoopContext loop)
    {
        switch (scenarioId)
        {
            case "lift-rescue-2p":
                ReplaceHand(combat, local, loop, ["Lift", "StrikeIronclad", "DefendIronclad"]);
                ReplaceDrawPile(combat, local, loop, ["StrikeIronclad", "DefendIronclad", "Bash", "StrikeIronclad", "DefendIronclad"]);
                SetEnergy(local, 3);
                SetHp(local, 70, loop);
                ReplaceHand(combat, ally, loop, ["StrikeIronclad", "StrikeIronclad", "Bash"]);
                SetEnergy(ally, 2);
                SetHp(ally, 8, loop);
                ConfigureEnemies(combat, loop, keepCount: 2, hpPerEnemy: 26, bonusStrength: 5);
                break;

            case "believe-synergy-2p":
                ReplaceHand(combat, local, loop, ["BelieveInYou", "StrikeIronclad", "DefendIronclad"]);
                ReplaceDrawPile(combat, local, loop, ["StrikeIronclad", "DefendIronclad", "StrikeIronclad", "DefendIronclad"]);
                SetEnergy(local, 2);
                SetHp(local, 70, loop);
                ReplaceHand(combat, ally, loop, ["Bludgeon", "StrikeIronclad"]);
                ReplaceDrawPile(combat, ally, loop, ["StrikeIronclad", "DefendIronclad", "StrikeIronclad", "DefendIronclad"]);
                SetEnergy(ally, 0);
                SetHp(ally, 50, loop);
                ConfigureEnemies(combat, loop, keepCount: 1, hpPerEnemy: 44, bonusStrength: 4);
                break;

            case "legion-2p":
            case "legion-4p":
                ReplaceHand(combat, local, loop, ["LegionOfBone", "DefendNecrobinder", "StrikeNecrobinder"]);
                ReplaceDrawPile(combat, local, loop, ["DefendNecrobinder", "StrikeNecrobinder", "DefendNecrobinder", "StrikeNecrobinder", "LegionOfBone"]);
                SetEnergy(local, 3);
                foreach (Player p in combat.Players)
                {
                    SetHp(p, 26, loop);
                    if (!ReferenceEquals(p, local))
                    {
                        ReplaceHand(combat, p, loop, ["StrikeIronclad", "DefendIronclad"]);
                        SetEnergy(p, 1);
                    }
                }
                ConfigureEnemies(combat, loop, keepCount: 2, hpPerEnemy: scenarioId == "legion-4p" ? 42 : 30, bonusStrength: 5);
                break;

            case "rally-2p":
            case "rally-4p":
                ReplaceHand(combat, local, loop, ["Rally", "DefendIronclad", "StrikeIronclad"]);
                ReplaceDrawPile(combat, local, loop, ["Rally", "DefendIronclad", "StrikeIronclad", "DefendIronclad", "StrikeIronclad"]);
                SetEnergy(local, 3);
                SetHp(local, 40, loop);
                bool firstAlly = true;
                foreach (Player p in combat.Players)
                {
                    if (ReferenceEquals(p, local))
                        continue;
                    SetHp(p, firstAlly ? 9 : 16, loop);
                    firstAlly = false;
                    ReplaceHand(combat, p, loop, ["StrikeIronclad", "StrikeIronclad"]);
                    SetEnergy(p, 1);
                }
                ConfigureEnemies(combat, loop, keepCount: 2, hpPerEnemy: scenarioId == "rally-4p" ? 38 : 28, bonusStrength: 5);
                break;

            case "beacon-synergy-2p":
            case "beacon-synergy-4p":
                ReplaceHand(combat, local, loop, ["BeaconOfHope", "DefendIronclad", "DefendIronclad", "StrikeIronclad"]);
                ReplaceDrawPile(combat, local, loop, ["ShrugItOff", "DefendIronclad", "DefendIronclad", "StrikeIronclad", "ShrugItOff"]);
                SetEnergy(local, 3);
                SetHp(local, 48, loop);
                foreach (Player p in combat.Players)
                {
                    if (ReferenceEquals(p, local))
                        continue;
                    SetHp(p, 24, loop);
                    ReplaceHand(combat, p, loop, ["StrikeIronclad", "DefendIronclad"]);
                    SetEnergy(p, 1);
                }
                ConfigureEnemies(combat, loop, keepCount: 2, hpPerEnemy: scenarioId == "beacon-synergy-4p" ? 56 : 44, bonusStrength: 4);
                break;

            case "energy-surge-2p":
            case "energy-surge-4p":
                ReplaceHand(combat, local, loop, ["EnergySurge", "StrikeIronclad", "DefendIronclad"]);
                ReplaceDrawPile(combat, local, loop, ["StrikeIronclad", "DefendIronclad", "StrikeIronclad", "DefendIronclad"]);
                SetEnergy(local, 3);
                SetHp(local, 60, loop);
                foreach (Player p in combat.Players)
                {
                    if (ReferenceEquals(p, local))
                        continue;
                    SetHp(p, 45, loop);
                    ReplaceHand(combat, p, loop, ["Bludgeon", "StrikeIronclad"]);
                    ReplaceDrawPile(combat, p, loop, ["StrikeIronclad", "DefendIronclad", "StrikeIronclad", "DefendIronclad"]);
                    SetEnergy(p, 2);
                }
                ConfigureEnemies(combat, loop, keepCount: 1, hpPerEnemy: scenarioId == "energy-surge-4p" ? 96 : 42, bonusStrength: 5);
                break;

            default:
                throw new ArgumentException($"Unknown scenario: {scenarioId}");
        }
    }

    private static IReadOnlyList<PlanAction> DecideLocal(
        CombatState combat,
        Player local,
        string mode,
        FairPlannerSettings v2Settings,
        SolverSearchProfile v2Profile,
        SolverSettingsSnapshot solverSettings,
        NewSolverConfig v3Config,
        MainLoopContext loop)
    {
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        if (mode == "v3")
        {
            ulong seed = NewFairSolver.VisibleSeed(combat, local);
            bool forced = NewFairSolver.IsForcedEndTurnLive(local);
            int knownTop = FairDrawPileKnowledge.Capture(local.PlayerCombatState!.DrawPile).KnownTop;
            int sampleCount = forced ? 1 : v3Config.Samples;
            CombatRootSnapshot[] roots = [.. Enumerable.Range(0, sampleCount).Select(i =>
                CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed + (ulong)i)))];
            Task<NewSolverDecision> task = Task.Run(() =>
                NewFairSolver.DecideFromWorlds(roots, names, knownTop, v3Config, CancellationToken.None));
            loop.RunUntilCompleted(task, TimeSpan.FromMinutes(3), "coop v3 decision");
            return task.GetAwaiter().GetResult().CommittedActions;
        }
        else
        {
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot policy = FairDecisionPlanner.IsolatedPolicy(
                SolverController.CaptureSearchPolicy(solverSettings, combat, includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat)),
                v2Profile);
            ulong seed = FairDecisionPlanner.VisibleSeed(combat, local);
            bool forced = FairDecisionPlanner.IsForcedEndTurn(local);
            int sampleCount = forced ? 1 : v2Settings.Samples;
            CombatRootSnapshot[] roots = [.. Enumerable.Range(0, sampleCount).Select(i =>
                CombatRootSnapshot.Capture(combat, fair: FairObservation.FromSeed(seed + (ulong)i)))];
            SearchPolicySnapshot decisionPolicy = forced ? FairDecisionPlanner.ForcedEndTurnPolicy(policy) : policy;
            Task<FairDecision?> task = Task.Run(() =>
            {
                using ISearchGcScope gcScope = FairDecisionPlanner.EnterGcScope(
                    solverSettings.NoGcRegionBudgetBytes, CancellationToken.None);
                return FairDecisionPlanner.Decide(
                    roots,
                    (root, prefix) => FairDecisionPlanner.Solve(root, names, damage, decisionPolicy, prefix, CancellationToken.None),
                    (root, action) => FairDecisionPlanner.RevealedVariants(root, names, damage, decisionPolicy, action, CancellationToken.None),
                    (root, actions) => FairDecisionPlanner.RevealFreeLength(root, names, damage, decisionPolicy, actions, CancellationToken.None),
                    v2Settings,
                    CancellationToken.None);
            });
            loop.RunUntilCompleted(task, TimeSpan.FromMinutes(5), "coop v2 decision");
            FairDecision? decision = task.GetAwaiter().GetResult();
            if (decision == null)
                return [new PlanAction(PlanActionKind.EndTurn, local.PlayerCombatState!.TurnNumber)];
            return [decision.Action, .. decision.Route.BestNode.Actions.Skip(1).Take(Math.Max(0, decision.CommitCount - 1))];
        }
    }

    private static bool ExecuteLocalAction(CombatState combat, Player local, PlanAction action, MainLoopContext loop)
    {
        WaitForQueueIdle(loop);
        if (CheckTerminal(combat, local) != null)
            return false;
        Creature? target = SolverController.ResolveLiveActionTarget(combat, action);
        switch (action.Kind)
        {
            case PlanActionKind.EndTurn:
            {
                CombatManager.Instance.OnEndedTurnLocally();
                EndPlayerTurnAction end = new(local, local.PlayerCombatState!.TurnNumber);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                loop.RunUntilCompleted(end.CompletionTask, TimeSpan.FromSeconds(30), "local end turn");
                return true;
            }
            case PlanActionKind.PlayCard:
            {
                CardModel card;
                try
                {
                    card = SolverController.FindCardForDeployment([.. local.PlayerCombatState!.Hand.Cards], action);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                if (!card.CanPlayTargeting(target))
                    return false;
                Task<GameAction> queued = SolverController.EnqueueAndCaptureActionAsync(
                    c => c is PlayCardAction p && ReferenceEquals(p.NetCombatCard.ToCardModelOrNull(), card),
                    () => card.TryManualPlay(target),
                    CancellationToken.None);
                loop.RunUntilCompleted(queued, TimeSpan.FromSeconds(30), $"enqueue {action.CardId}");
                loop.RunUntilCompleted(queued.GetAwaiter().GetResult().CompletionTask, TimeSpan.FromSeconds(30), $"complete {action.CardId}");
                return true;
            }
            default:
                return false;
        }
    }

    private static void RunTeammateTurns(CombatState combat, Player local, int turn, MainLoopContext loop)
    {
        foreach (Player mate in combat.Players)
        {
            if (ReferenceEquals(mate, local))
                continue;
            if (CheckTerminal(combat, local) != null)
                return;
            if (!mate.Creature.IsAlive)
                continue;

            int safety = 0;
            while (safety++ < 12 && CheckTerminal(combat, local) == null && mate.Creature.IsAlive)
            {
                WaitForQueueIdle(loop);
                CardModel? nextCard = PickTeammateCard(combat, mate, out Creature? target);
                if (nextCard == null)
                    break;
                PlayCardAction play = new(nextCard, target);
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(play);
                loop.RunUntilCompleted(play.CompletionTask, TimeSpan.FromSeconds(30), $"teammate play {nextCard.Id.Entry}");
            }

            if (CheckTerminal(combat, local) != null)
                return;
            if (mate.Creature.IsAlive && !CombatManager.Instance.IsPlayerReadyToEndTurn(mate))
            {
                WaitForQueueIdle(loop);
                EndPlayerTurnAction end = new(mate, mate.PlayerCombatState!.TurnNumber);
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(end);
                loop.RunUntilCompleted(end.CompletionTask, TimeSpan.FromSeconds(30), "teammate end turn");
            }
        }
    }

    private static CardModel? PickTeammateCard(CombatState combat, Player mate, out Creature? target)
    {
        target = null;
        PlayerCombatState? pcs = mate.PlayerCombatState;
        if (pcs == null)
            return null;
        Creature? lowestEnemy = combat.Enemies
            .Where(e => e.IsAlive)
            .OrderBy(e => e.CurrentHp)
            .FirstOrDefault();

        CardModel? bestAttack = null;
        int bestAttackCost = -1;
        CardModel? bestSkill = null;
        foreach (CardModel card in pcs.Hand.Cards)
        {
            if (card.Type == CardType.Attack)
            {
                Creature? candTarget = card.TargetType == TargetType.AnyEnemy ? lowestEnemy : null;
                if (card.CanPlayTargeting(candTarget))
                {
                    int cost = card.EnergyCost.GetWithModifiers(CostModifiers.All);
                    if (cost > bestAttackCost)
                    {
                        bestAttackCost = cost;
                        bestAttack = card;
                        target = candTarget;
                    }
                }
            }
            else if (bestSkill == null && card.Type == CardType.Skill && card.TargetType is TargetType.Self or TargetType.None or TargetType.AllAllies)
            {
                if (card.CanPlayTargeting(null))
                    bestSkill = card;
            }
        }

        if (bestAttack != null)
            return bestAttack;
        target = null;
        return bestSkill;
    }

    private static void WaitForNextTurnOrEnd(CombatState combat, Player local, int finishedTurn, MainLoopContext loop)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (CheckTerminal(combat, local) != null)
                return;
            if (combat.CurrentSide == CombatSide.Player
                && local.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && local.PlayerCombatState.TurnNumber > finishedTurn
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(local)
                && !RunManager.Instance.ActionExecutor.IsRunning
                && RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted)
            {
                return;
            }
            loop.Pump(TimeSpan.FromMilliseconds(5));
        }
        throw new TimeoutException($"Co-op turn advance timed out after turn {finishedTurn}.");
    }

    private static void WaitForQueueIdle(MainLoopContext loop)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ActionExecutor ex = RunManager.Instance.ActionExecutor;
            if (ex.CurrentlyRunningAction == null && ex.FinishedExecutingActions().IsCompleted)
                return;
            loop.Pump(TimeSpan.FromMilliseconds(5));
        }
        throw new TimeoutException("Action queue did not become idle.");
    }

    private static string? CheckTerminal(CombatState combat, Player local)
    {
        if (local.Creature.IsDead)
            return "defeat";
        if (!CombatManager.Instance.IsInProgress || combat.Enemies.All(e => !e.IsAlive))
            return "victory";
        return null;
    }

    private static void ReplaceHand(CombatState combat, Player player, MainLoopContext loop, IReadOnlyList<string> cardNames)
    {
        async Task DoReplace()
        {
            PlayerCombatState pcs = player.PlayerCombatState!;
            await CardPileCmd.RemoveFromCombat([.. pcs.Hand.Cards], true);
            foreach (string name in cardNames)
            {
                CardModel canonical = ModelDb.AllCards.Single(c => c.GetType().Name == name || c.Id.Entry == name);
                CardModel card = combat.CreateCard(canonical, player);
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoReplace(), TimeSpan.FromSeconds(30), "coop replace hand");
    }

    private static void ReplaceDrawPile(CombatState combat, Player player, MainLoopContext loop, IReadOnlyList<string> cardNames)
    {
        async Task DoReplace()
        {
            PlayerCombatState pcs = player.PlayerCombatState!;
            await CardPileCmd.RemoveFromCombat([.. pcs.DrawPile.Cards], true);
            foreach (string name in cardNames)
            {
                CardModel canonical = ModelDb.AllCards.Single(c => c.GetType().Name == name || c.Id.Entry == name);
                CardModel card = combat.CreateCard(canonical, player);
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Draw, player);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        loop.RunUntilCompleted(DoReplace(), TimeSpan.FromSeconds(30), "coop replace draw");
    }

    private static void SetEnergy(Player player, int energy)
    {
        PlayerCombatState pcs = player.PlayerCombatState!;
        if (pcs.Energy > energy)
            pcs.LoseEnergy(pcs.Energy - energy);
        else if (pcs.Energy < energy)
            pcs.GainEnergy(energy - pcs.Energy);
    }

    private static void SetHp(Player player, int hp, MainLoopContext loop)
    {
        if (player.Creature.MaxHp < hp)
            loop.RunUntilCompleted(CreatureCmd.SetMaxHp(player.Creature, hp), TimeSpan.FromSeconds(15), "set max hp");
        loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(player.Creature, hp), TimeSpan.FromSeconds(15), "set hp");
    }

    private static void ConfigureEnemies(CombatState combat, MainLoopContext loop, int keepCount, int hpPerEnemy, int bonusStrength)
    {
        for (int i = 0; i < combat.Enemies.Count; i++)
        {
            Creature enemy = combat.Enemies[i];
            if (i < keepCount)
            {
                if (enemy.MaxHp < hpPerEnemy)
                    loop.RunUntilCompleted(CreatureCmd.SetMaxHp(enemy, hpPerEnemy), TimeSpan.FromSeconds(15), "enemy max hp");
                loop.RunUntilCompleted(CreatureCmd.SetCurrentHp(enemy, hpPerEnemy), TimeSpan.FromSeconds(15), "enemy hp");
                if (bonusStrength > 0)
                {
                    PowerModel str = ModelDb.AllPowers.Single(p => p.Id.Entry == "STRENGTH_POWER").ToMutable();
                    str.Target = enemy;
                    loop.RunUntilCompleted(PowerCmd.Apply(new BlockingPlayerChoiceContext(), str, enemy, bonusStrength, enemy, null),
                        TimeSpan.FromSeconds(15), "enemy str");
                }
            }
            else
            {
                loop.RunUntilCompleted(CreatureCmd.Kill(enemy, force: true), TimeSpan.FromSeconds(15), "kill extra enemy");
            }
        }
    }

    private static bool _timeBypassed;
    private static readonly long StartedAt = Environment.TickCount64;

    private static bool TicksMsecPrefix(ref ulong __result)
    {
        __result = (ulong)(Environment.TickCount64 - StartedAt);
        return false;
    }

    private static bool TicksUsecPrefix(ref ulong __result)
    {
        __result = (ulong)(Environment.TickCount64 - StartedAt) * 1000UL;
        return false;
    }

    private static bool IsSingleplayerOrFakeMultiplayerPrefix(ref bool __result)
    {
        if (CombatManager.Instance.DebugOnlyGetState()?.Players.Count > 1)
        {
            __result = false;
            return false;
        }
        return true;
    }

    internal static void InstallTimeBypass()
    {
        if (_timeBypassed)
            return;
        _timeBypassed = true;
        HarmonyLib.Harmony harmony = GameBootstrap.Harmony;
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Godot.Time), nameof(Godot.Time.GetTicksMsec)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(CoopEvalSuite), nameof(TicksMsecPrefix)));
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Godot.Time), nameof(Godot.Time.GetTicksUsec)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(CoopEvalSuite), nameof(TicksUsecPrefix)));
        harmony.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(RunManager), nameof(RunManager.IsSingleplayerOrFakeMultiplayer)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(CoopEvalSuite), nameof(IsSingleplayerOrFakeMultiplayerPrefix)));
    }
}
