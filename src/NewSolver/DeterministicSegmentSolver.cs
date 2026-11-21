using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.NewSolver;

internal sealed record DeterministicEndpoint(
    IReadOnlyList<PlanAction> Actions,
    SimStepState State,
    ulong CanonicalHash,
    double HeuristicScore,
    double ScalingScore,
    int RemainingEnemyHp,
    int MinAliveEnemyHp,
    int ProjectedPartyUnblockedDamage,
    int RemainingEnergy,
    bool IsVictory,
    string SetupKey = "");

internal sealed record RevealBranch(
    IReadOnlyList<PlanAction> Prefix,
    PlanAction RevealAction,
    SimStepState PostRevealState,
    int RemainingEnergyBeforeReveal,
    double HeuristicScore,
    string DedupeKey);

internal sealed record DeterministicSegmentResult(
    DeterministicEndpoint? BestVictory,
    IReadOnlyList<DeterministicEndpoint> EndTurnEndpoints,
    IReadOnlyList<RevealBranch> RevealBranches,
    int NodesExpanded);

internal static class DeterministicSegmentSolver
{
    private sealed record OpenNode(
        SimStepState State,
        IReadOnlyList<PlanAction> Actions,
        ulong Hash,
        double Score,
        int Depth);

    public static DeterministicSegmentResult Solve(
        SimStepState root,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int nodeCap,
        int maxEndTurnEndpoints,
        int maxRevealBranches,
        int maxChoiceBranches,
        bool includePotions,
        CancellationToken token,
        SearchDeadline deadline = default,
        bool expandReveals = false)
    {
        int startTurn = root.Combat.GetPlayerTurnNumber(player);
        Dictionary<Type, int> rootPowers = LocalPowerAmounts(root.Combat, player);
        Dictionary<ulong, double> visited = new(Math.Min(nodeCap * 2, 1024));
        PriorityQueue<OpenNode, (double Priority, long Sequence)> open = new();
        long sequence = 0;

        double rootScore = PartyEvaluator.EvaluateTerminalOrHorizon(root, baseline, includePendingIncomingThreat: true);
        ulong rootHash = StateHash.Compute(root.Simulator, root.Combat, player, root.KnownTopRemaining);
        visited[rootHash] = rootScore;
        open.Enqueue(new OpenNode(root, [], rootHash, rootScore, 0), (-rootScore, sequence++));

        DeterministicEndpoint? bestVictory = null;
        Dictionary<ulong, DeterministicEndpoint> endTurnByHash = [];
        Dictionary<string, RevealBranch> revealMap = new(StringComparer.Ordinal);
        int expanded = 0;

        while (open.Count > 0 && expanded < nodeCap)
        {
            if (token.IsCancellationRequested || deadline.IsExpired)
                break;

            OpenNode current = open.Dequeue();
            expanded++;

            CombatPredictionSimulator sim = current.State.Simulator;
            SimulatedCombatState combat = current.State.Combat;

            if (sim.State.GetCreature(player.Creature).IsDead || combat.BattlewornDummyTimedOut)
                continue;

            int enemyHp = TotalAliveEnemyHp(sim, combat);
            if (!PartyEvaluator.AnyEnemyStanding(sim, combat))
            {
                double winScore = PartyEvaluator.EvaluateTerminalOrHorizon(current.State, baseline, includePendingIncomingThreat: false);
                if (bestVictory == null || winScore > bestVictory.HeuristicScore)
                {
                    bestVictory = new DeterministicEndpoint(current.Actions, current.State, current.Hash, winScore, 0, 0, 0, 0, 0, IsVictory: true);
                }
                continue;
            }

            if (bestVictory != null && CannotBeatVictory(current.State, baseline, bestVictory.HeuristicScore))
                continue;

            int energyBefore = sim.State.GetPlayerCombatState(player).Energy;
            int unblocked = TotalProjectedUnblockedPartyDamage(sim, combat);
            double endScore = current.Score;
            if (!endTurnByHash.TryGetValue(current.Hash, out DeterministicEndpoint? prevEp)
                || endScore > prevEp.HeuristicScore + 1e-6
                || (Math.Abs(endScore - prevEp.HeuristicScore) <= 1e-6 && current.Actions.Count < prevEp.Actions.Count))
            {
                double scalingScore = PartyEvaluator.EvaluatePartyScaling(sim, combat, player, enemyHp);
                endTurnByHash[current.Hash] = new DeterministicEndpoint(
                    current.Actions, current.State, current.Hash, endScore, scalingScore, enemyHp, MinAliveEnemyHp(sim, combat), unblocked, energyBefore, IsVictory: false,
                    SetupKey: maxEndTurnEndpoints > SetupSlotMinimumLimit ? SetupKey(combat, player, rootPowers) : "");
            }

            if (current.Depth >= 18)
                continue;

            List<PlanAction> baseActions = CombatStepEngine.EnumerateBaseActions(current.State, player, names, includePotions);
            foreach (PlanAction baseAction in baseActions)
            {
                if (baseAction.Kind == PlanActionKind.EndTurn)
                    continue;

                List<StepOutcome> outcomes = CombatStepEngine.ExpandAction(
                    current.State, baseAction, player, names, maxChoiceBranches, baseline, skipCanPlayCheck: true);

                foreach (StepOutcome outcome in outcomes)
                {
                    PlanAction[] nextActions = new PlanAction[current.Actions.Count + 1];
                    for (int i = 0; i < current.Actions.Count; i++)
                        nextActions[i] = current.Actions[i];
                    nextActions[current.Actions.Count] = outcome.Action;

                    CombatPredictionSimulator nextSim = outcome.State.Simulator;
                    SimulatedCombatState nextCombat = outcome.State.Combat;

                    if (nextSim.State.GetCreature(player.Creature).IsDead || nextCombat.BattlewornDummyTimedOut)
                        continue;

                    if (!PartyEvaluator.AnyEnemyStanding(nextSim, nextCombat))
                    {
                        double winScore = PartyEvaluator.EvaluateTerminalOrHorizon(outcome.State, baseline, includePendingIncomingThreat: false);
                        if (bestVictory == null || winScore > bestVictory.HeuristicScore)
                        {
                            bestVictory = new DeterministicEndpoint(nextActions, outcome.State, 0, winScore, 0, 0, 0, 0, 0, IsVictory: true);
                        }
                        continue;
                    }

                    if (outcome.AdvancedTurn || nextCombat.GetPlayerTurnNumber(player) != startTurn)
                    {
                        double turnAdvScore = PartyEvaluator.EvaluateTerminalOrHorizon(outcome.State, baseline, includePendingIncomingThreat: false);
                        string advKey = $"ADV|{current.Hash:x16}|{CombatStepEngine.ActionKey(outcome.Action)}";
                        if (!revealMap.TryGetValue(advKey, out RevealBranch? existingAdv) || turnAdvScore > existingAdv.HeuristicScore)
                        {
                            revealMap[advKey] = new RevealBranch(
                                current.Actions, outcome.Action, outcome.State, energyBefore, turnAdvScore, advKey);
                        }
                        continue;
                    }

                    if (outcome.RevealedInformation && !expandReveals)
                    {
                        double revScore = PartyEvaluator.EvaluateTerminalOrHorizon(outcome.State, baseline, includePendingIncomingThreat: true);
                        string actionSig = CombatStepEngine.HasRevealedChoice(outcome.Action)
                            ? CombatStepEngine.UnchosenActionKey(outcome.Action)
                            : CombatStepEngine.ActionKey(outcome.Action);
                        string revKey = $"{current.Hash:x16}|{actionSig}";
                        double priority = revScore + energyBefore * 1.4 - current.Depth * 0.15;
                        if (!revealMap.TryGetValue(revKey, out RevealBranch? existing) || priority > existing.HeuristicScore)
                        {
                            revealMap[revKey] = new RevealBranch(
                                current.Actions, outcome.Action, outcome.State, energyBefore, priority, revKey);
                        }
                        continue;
                    }

                    ulong nextHash = StateHash.Compute(nextSim, nextCombat, player, outcome.State.KnownTopRemaining);
                    double nextScore = PartyEvaluator.EvaluateTerminalOrHorizon(outcome.State, baseline, includePendingIncomingThreat: true);
                    if (visited.TryGetValue(nextHash, out double seenScore) && seenScore >= nextScore - 1e-6)
                        continue;
                    visited[nextHash] = nextScore;

                    int nextEnergy = nextSim.State.GetPlayerCombatState(player).Energy;
                    double priorityScore = nextScore + nextEnergy * 0.4;
                    open.Enqueue(new OpenNode(outcome.State, nextActions, nextHash, nextScore, current.Depth + 1), (-priorityScore, sequence++));
                }
            }

            int remainingPops = nodeCap - expanded;
            if (open.Count > remainingPops * 2 + 16)
                TrimOpen(open, remainingPops);
        }

        IReadOnlyList<DeterministicEndpoint> selectedEndTurns = SelectDiverseEndpoints(endTurnByHash.Values.ToList(), maxEndTurnEndpoints);
        IReadOnlyList<RevealBranch> selectedReveals = revealMap.Values
            .OrderByDescending(r => r.HeuristicScore)
            .ThenByDescending(r => r.RemainingEnergyBeforeReveal)
            .ThenBy(r => r.DedupeKey, StringComparer.Ordinal)
            .Take(maxRevealBranches)
            .ToArray();

        return new DeterministicSegmentResult(bestVictory, selectedEndTurns, selectedReveals, expanded);
    }

    private static void TrimOpen(PriorityQueue<OpenNode, (double Priority, long Sequence)> open, int keep)
    {
        (OpenNode Node, (double Priority, long Sequence) Key)[] items = [.. open.UnorderedItems];
        Array.Sort(items, static (a, b) => a.Key.CompareTo(b.Key));
        open.Clear();
        for (int i = 0; i < keep && i < items.Length; i++)
            open.Enqueue(items[i].Node, items[i].Key);
    }

    private static IReadOnlyList<DeterministicEndpoint> SelectDiverseEndpoints(
        List<DeterministicEndpoint> pool,
        int limit)
    {
        if (pool.Count == 0 || limit <= 0)
            return [];

        List<DeterministicEndpoint> chosen = new(limit);
        HashSet<ulong> usedHashes = [];

        void TryAdd(DeterministicEndpoint ep)
        {
            if (chosen.Count >= limit)
                return;
            if (usedHashes.Add(ep.CanonicalHash))
                chosen.Add(ep);
        }

        DeterministicEndpoint[] byScore = pool
            .OrderByDescending(e => e.HeuristicScore)
            .ThenBy(e => e.ProjectedPartyUnblockedDamage)
            .ThenBy(e => e.RemainingEnemyHp)
            .ThenBy(e => e.RemainingEnergy)
            .ThenBy(e => e.Actions.Count)
            .ToArray();

        TryAdd(byScore[0]);

        DeterministicEndpoint bestDef = pool
            .OrderBy(e => e.ProjectedPartyUnblockedDamage)
            .ThenByDescending(e => e.HeuristicScore)
            .ThenBy(e => e.RemainingEnemyHp)
            .First();
        TryAdd(bestDef);

        DeterministicEndpoint bestOff = pool
            .OrderBy(e => e.RemainingEnemyHp)
            .ThenBy(e => e.ProjectedPartyUnblockedDamage)
            .ThenByDescending(e => e.HeuristicScore)
            .First();
        TryAdd(bestOff);

        DeterministicEndpoint bestFocus = pool
            .OrderBy(e => e.MinAliveEnemyHp)
            .ThenBy(e => e.RemainingEnemyHp)
            .ThenBy(e => e.ProjectedPartyUnblockedDamage)
            .ThenByDescending(e => e.HeuristicScore)
            .First();
        TryAdd(bestFocus);

        DeterministicEndpoint bestScaling = pool
            .OrderByDescending(e => e.ScalingScore)
            .ThenByDescending(e => e.HeuristicScore)
            .ThenBy(e => e.ProjectedPartyUnblockedDamage)
            .First();
        TryAdd(bestScaling);

        // Setup lines (a power gained or stacked this turn) pay off in later turns, so their same-turn heuristic
        // understates them. Keep the best line of every distinct setup so rollouts, which simulate those later
        // turns, decide between setup and tempo instead of the one-turn heuristic.
        foreach (DeterministicEndpoint ep in byScore
                     .Where(e => e.SetupKey.Length > 0)
                     .GroupBy(e => e.SetupKey, StringComparer.Ordinal)
                     .Select(g => g.First()))
        {
            if (chosen.Count >= limit)
                break;
            TryAdd(ep);
        }

        int minEnergy = pool.Min(e => e.RemainingEnergy);
        foreach (DeterministicEndpoint ep in byScore.Where(e => e.RemainingEnergy == minEnergy))
        {
            if (chosen.Count >= limit)
                break;
            TryAdd(ep);
        }

        foreach (DeterministicEndpoint ep in byScore)
        {
            if (chosen.Count >= limit)
                break;
            TryAdd(ep);
        }

        return chosen;
    }

    // Root candidate lists are wide enough for setup slots; continuation turns keep 1-4 endpoints and skip them.
    private const int SetupSlotMinimumLimit = 4;

    private static Dictionary<Type, int> LocalPowerAmounts(SimulatedCombatState combat, Player player)
    {
        Dictionary<Type, int> amounts = [];
        IReadOnlyList<MegaCrit.Sts2.Core.Models.PowerModel> powers = combat.EffectivePowers();
        for (int i = 0; i < powers.Count; i++)
        {
            if (ReferenceEquals(powers[i].Owner, player.Creature))
                amounts[powers[i].GetType()] = amounts.GetValueOrDefault(powers[i].GetType()) + powers[i].Amount;
        }
        return amounts;
    }

    private static string SetupKey(SimulatedCombatState combat, Player player, Dictionary<Type, int> rootPowers)
    {
        Dictionary<Type, int> now = LocalPowerAmounts(combat, player);
        List<string>? gained = null;
        IReadOnlyList<MegaCrit.Sts2.Core.Models.PowerModel> powers = combat.EffectivePowers();
        for (int i = 0; i < powers.Count; i++)
        {
            MegaCrit.Sts2.Core.Models.PowerModel power = powers[i];
            if (!ReferenceEquals(power.Owner, player.Creature)
                || power.TypeForCurrentAmount != MegaCrit.Sts2.Core.Entities.Powers.PowerType.Buff)
                continue;
            Type type = power.GetType();
            if (now[type] > rootPowers.GetValueOrDefault(type) && !(gained?.Contains(type.Name) ?? false))
                (gained ??= []).Add(type.Name);
        }
        if (gained == null)
            return "";
        gained.Sort(StringComparer.Ordinal);
        return string.Join('+', gained);
    }

    private static bool CannotBeatVictory(
        SimStepState state,
        PartyRootBaseline baseline,
        double bestVictoryScore)
    {
        CombatPredictionSimulator sim = state.Simulator;
        SimulatedCombatState combat = state.Combat;
        double optimistic = 0;
        IReadOnlyList<Player> players = combat.Players;
        for (int i = 0; i < players.Count; i++)
        {
            SimCreatureState cs = sim.State.GetCreature(players[i].Creature);
            if (cs.IsDead)
                optimistic += PartyEvaluator.TeammateImmediateDeathPenalty;
        }
        return optimistic + 15.0 < bestVictoryScore;
    }

    private static int TotalAliveEnemyHp(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int hp = 0;
        IReadOnlyList<Creature> enemies = combat.Enemies;
        for (int i = 0; i < enemies.Count; i++)
            hp += PartyEvaluator.KillableHp(combat, enemies[i], sim.State.GetCreature(enemies[i]));
        return hp;
    }

    private static int MinAliveEnemyHp(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int min = int.MaxValue;
        IReadOnlyList<Creature> enemies = combat.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            int hp = PartyEvaluator.KillableHp(combat, enemies[i], sim.State.GetCreature(enemies[i]));
            if (hp > 0 && hp < min)
                min = hp;
        }
        return min == int.MaxValue ? 0 : min;
    }

    private static int TotalProjectedUnblockedPartyDamage(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int total = 0;
        IReadOnlyList<Player> players = combat.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            SimCreatureState cs = sim.State.GetCreature(p.Creature);
            if (cs.IsDead)
                continue;
            int incoming = PartyEvaluator.EstimateIncomingDamage(sim, combat, p.Creature);
            int shield = cs.Block;
            if (combat.GetOsty(p) is { } osty)
            {
                SimCreatureState os = sim.State.GetCreature(osty);
                if (os.IsAlive)
                    shield += os.CurrentHp;
            }
            total += Math.Max(0, incoming - shield);
        }
        return total;
    }
}
