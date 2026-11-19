using System.Diagnostics;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.NewSolver;

internal static class InformationSetSearch
{
    private sealed record CandidateSpec(
        string Key,
        IReadOnlyList<PlanAction> Actions,
        bool IsReveal,
        bool IsImmediateVictory,
        double ImmediateScore,
        bool EndsTurn);

    public static NewSolverDecision Decide(
        IReadOnlyList<CombatRootSnapshot> worlds,
        SolverDisplayNames names,
        int initialKnownTop,
        NewSolverConfig config,
        CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        if (worlds.Count == 0)
            throw new ArgumentException("At least one world is required.", nameof(worlds));

        Player player = worlds[0].PlayerIdentity;
        SimStepState world0Root = CombatStepEngine.CreateRoot(worlds[0], initialKnownTop);
        PartyRootBaseline baseline = PartyRootBaseline.Capture(worlds[0], world0Root.Simulator);
        int turn = world0Root.Combat.GetPlayerTurnNumber(player);

        if (IsForcedEndTurn(world0Root, player))
        {
            PlanAction endTurn = new(PlanActionKind.EndTurn, turn);
            return new NewSolverDecision(
                [endTurn],
                [new NewSolverCandidate(CombatStepEngine.ActionKey(endTurn), [endTurn], false, 0, 0, 0, 0, 1)],
                new NewSolverStats(1, 0, 1, 1, false, sw.ElapsedMilliseconds));
        }

        bool allowPotions = worlds[0].SearchablePotionCount > 0;
        DeterministicSegmentResult det = DeterministicSegmentSolver.Solve(
            world0Root,
            player,
            names,
            baseline,
            config.DeterministicNodeCap,
            config.MaxEndTurnCandidates,
            config.MaxRevealCandidates,
            config.MaxChoiceBranches,
            includePotions: allowPotions,
            token);

        if (det.BestVictory is { } immediateWin && IsOptimalImmediateVictory(immediateWin, baseline))
        {
            string winKey = CombatStepEngine.PrefixKey(immediateWin.Actions);
            return new NewSolverDecision(
                immediateWin.Actions,
                [new NewSolverCandidate(winKey, immediateWin.Actions, false, immediateWin.HeuristicScore, immediateWin.HeuristicScore, 0, 1, 1)],
                new NewSolverStats(det.NodesExpanded, 0, 1, 1, true, sw.ElapsedMilliseconds));
        }

        List<CandidateSpec> candidates = BuildRootCandidates(det, turn);
        if (candidates.Count == 0)
        {
            PlanAction endTurn = new(PlanActionKind.EndTurn, turn);
            return new NewSolverDecision(
                [endTurn],
                [new NewSolverCandidate(CombatStepEngine.ActionKey(endTurn), [endTurn], false, 0, 0, 0, 0, 1)],
                new NewSolverStats(det.NodesExpanded, 0, 1, 1, false, sw.ElapsedMilliseconds));
        }

        if (candidates.Count == 1 && !candidates[0].IsReveal && candidates[0].IsImmediateVictory)
        {
            CandidateSpec only = candidates[0];
            return new NewSolverDecision(
                only.Actions,
                [new NewSolverCandidate(only.Key, only.Actions, false, only.ImmediateScore, only.ImmediateScore, 0, 1, 1)],
                new NewSolverStats(det.NodesExpanded, 0, 1, 1, true, sw.ElapsedMilliseconds));
        }

        int worldCount = worlds.Count;
        double[,] scores = new double[candidates.Count, worldCount];
        bool[,] valid = new bool[candidates.Count, worldCount];
        bool[,] died = new bool[candidates.Count, worldCount];
        bool[,] won = new bool[candidates.Count, worldCount];
        int totalContNodes = 0;

        for (int c = 0; c < candidates.Count; c++)
        {
            if (candidates[c].IsImmediateVictory)
            {
                for (int w = 0; w < worldCount; w++)
                {
                    scores[c, w] = candidates[c].ImmediateScore;
                    valid[c, w] = true;
                    won[c, w] = true;
                }
            }
        }

        int pilotWorlds = candidates.Count > config.Stage2Survivors && worldCount > config.PilotSamples
            ? config.PilotSamples
            : worldCount;

        void RunBatch(List<(int CandIdx, int WorldIdx)> batch, long budgetMs, int minJobsBeforeBudget)
        {
            if (batch.Count == 0)
                return;
            int dop = config.MaxParallelism > 0
                ? Math.Min(config.MaxParallelism, Math.Max(1, batch.Count))
                : Math.Min(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, batch.Count));
            ParallelOptions opts = new()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, dop),
            };
            Parallel.For(0, batch.Count, opts, idx =>
            {
                if (sw.ElapsedMilliseconds >= budgetMs && idx >= minJobsBeforeBudget)
                    return;
                (int cIdx, int wIdx) = batch[idx];
                (bool ok, double val, bool isDead, bool isWin, int nodes) = EvaluateCandidateInWorld(
                    worlds[wIdx], candidates[cIdx], player, names, baseline, initialKnownTop, config, token);
                Interlocked.Add(ref totalContNodes, nodes);
                if (ok)
                {
                    scores[cIdx, wIdx] = val;
                    valid[cIdx, wIdx] = true;
                    died[cIdx, wIdx] = isDead;
                    won[cIdx, wIdx] = isWin;
                }
            });
        }

        List<(int CandIdx, int WorldIdx)> stage1Jobs = new(candidates.Count * pilotWorlds);
        for (int c = 0; c < candidates.Count; c++)
        {
            if (candidates[c].IsImmediateVictory)
                continue;
            for (int w = 0; w < pilotWorlds; w++)
                stage1Jobs.Add((c, w));
        }
        long emergencyBudgetMs = Math.Max(config.SoftTimeBudgetMs * 4L, 12_000L);
        RunBatch(stage1Jobs, emergencyBudgetMs, stage1Jobs.Count);

        if (pilotWorlds < worldCount)
        {
            List<int> scoredIndices = Enumerable.Range(0, candidates.Count)
                .OrderByDescending(c => AggregateCandidate(c, candidates[c], scores, valid, died, won, pilotWorlds).EvaluatedWorlds > 0)
                .ThenBy(c => Math.Round(AggregateCandidate(c, candidates[c], scores, valid, died, won, pilotWorlds).DeathRate, 4))
                .ThenByDescending(c => Math.Round(AggregateCandidate(c, candidates[c], scores, valid, died, won, pilotWorlds).CvarUtility, 4))
                .ThenBy(c => candidates[c].Key, StringComparer.Ordinal)
                .ToList();

            HashSet<int> survivors = new(scoredIndices.Take(config.Stage2Survivors));
            int? bestReveal = scoredIndices.FirstOrDefault(c => candidates[c].IsReveal && !survivors.Contains(c), -1);
            if (bestReveal >= 0 && candidates[bestReveal.Value].IsReveal)
                survivors.Add(bestReveal.Value);

            List<(int CandIdx, int WorldIdx)> stage2Jobs = [];
            foreach (int c in survivors.OrderBy(x => x))
            {
                if (candidates[c].IsImmediateVictory)
                    continue;
                for (int w = pilotWorlds; w < worldCount; w++)
                    stage2Jobs.Add((c, w));
            }
            RunBatch(stage2Jobs, emergencyBudgetMs, stage2Jobs.Count);
        }

        List<NewSolverCandidate> ranked = new(candidates.Count);
        for (int c = 0; c < candidates.Count; c++)
            ranked.Add(AggregateCandidate(c, candidates[c], scores, valid, died, won, worldCount));

        int maxEvaluated = ranked.Max(c => c.EvaluatedWorlds);
        int quorum = Math.Max(1, (maxEvaluated * 3 + 3) / 4);
        NewSolverCandidate[] eligible = ranked.Where(c => c.EvaluatedWorlds >= quorum).ToArray();
        if (eligible.Length == 0)
            eligible = [.. ranked];

        NewSolverCandidate leader = eligible
            .OrderByDescending(c => c.EvaluatedWorlds > 0)
            .ThenBy(c => c.DeathRate > 0 ? 1 : 0)
            .ThenByDescending(c => Math.Round(c.CvarUtility, 4))
            .ThenBy(c => Math.Round(c.DeathRate, 4))
            .ThenByDescending(c => Math.Round(c.WinRate, 4))
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .First();

        NewSolverCandidate best = eligible
            .Where(c => c.EvaluatedWorlds > 0
                && Math.Abs(c.DeathRate - leader.DeathRate) <= 1e-6
                && leader.CvarUtility - c.CvarUtility <= 0.05)
            .OrderByDescending(c => c.IsReveal ? 1 : 0)
            .ThenBy(c => c.IsReveal ? c.Actions.Count : int.MaxValue)
            .ThenByDescending(c => Math.Round(c.CvarUtility, 4))
            .ThenByDescending(c => Math.Round(c.WinRate, 4))
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .First();

        IReadOnlyList<PlanAction> commit = best.Actions;
        if (commit.Count == 0)
            commit = [new PlanAction(PlanActionKind.EndTurn, turn)];

        return new NewSolverDecision(
            commit,
            ranked.OrderByDescending(c => c.EvaluatedWorlds >= quorum)
                .ThenBy(c => c.DeathRate > 0 ? 1 : 0)
                .ThenByDescending(c => Math.Round(c.CvarUtility, 4))
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ToArray(),
            new NewSolverStats(
                det.NodesExpanded,
                totalContNodes,
                candidates.Count,
                worldCount,
                det.BestVictory != null,
                sw.ElapsedMilliseconds));
    }

    private static NewSolverCandidate AggregateCandidate(
        int c,
        CandidateSpec spec,
        double[,] scores,
        bool[,] valid,
        bool[,] died,
        bool[,] won,
        int worldLimit)
    {
        List<double> vals = [];
        int deaths = 0;
        int wins = 0;
        for (int w = 0; w < worldLimit; w++)
        {
            if (!valid[c, w])
                continue;
            vals.Add(scores[c, w]);
            if (died[c, w])
                deaths++;
            if (won[c, w])
                wins++;
        }
        if (vals.Count == 0)
        {
            return new NewSolverCandidate(spec.Key, spec.Actions, spec.IsReveal,
                double.NegativeInfinity, double.NegativeInfinity, 1.0, 0.0, 0);
        }
        vals.Sort();
        double mean = vals.Average();
        int qCount = Math.Max(1, vals.Count / 4);
        double worstQuartile = vals.Take(qCount).Average();
        double cvar = 0.90 * mean + 0.10 * worstQuartile;
        return new NewSolverCandidate(
            spec.Key,
            spec.Actions,
            spec.IsReveal,
            mean,
            cvar,
            (double)deaths / vals.Count,
            (double)wins / vals.Count,
            vals.Count);
    }

    private static List<CandidateSpec> BuildRootCandidates(DeterministicSegmentResult det, int turn)
    {
        List<CandidateSpec> list = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        if (det.BestVictory is { } win)
        {
            string key = CombatStepEngine.PrefixKey(win.Actions);
            if (seen.Add(key))
            {
                list.Add(new CandidateSpec(key, win.Actions, IsReveal: false, IsImmediateVictory: true,
                    ImmediateScore: win.HeuristicScore, EndsTurn: false));
            }
        }

        foreach (RevealBranch rev in det.RevealBranches)
        {
            PlanAction[] seq = new PlanAction[rev.Prefix.Count + 1];
            for (int i = 0; i < rev.Prefix.Count; i++)
                seq[i] = rev.Prefix[i];
            seq[rev.Prefix.Count] = rev.RevealAction;
            string key = CombatStepEngine.PrefixKey(seq);
            if (seen.Add(key))
            {
                list.Add(new CandidateSpec(key, seq, IsReveal: true, IsImmediateVictory: false,
                    ImmediateScore: rev.HeuristicScore, EndsTurn: false));
            }
        }

        foreach (DeterministicEndpoint ep in det.EndTurnEndpoints)
        {
            PlanAction[] seq = new PlanAction[ep.Actions.Count + 1];
            for (int i = 0; i < ep.Actions.Count; i++)
                seq[i] = ep.Actions[i];
            seq[ep.Actions.Count] = new PlanAction(PlanActionKind.EndTurn, turn);
            string key = CombatStepEngine.PrefixKey(seq);
            if (seen.Add(key))
            {
                list.Add(new CandidateSpec(key, seq, IsReveal: false, IsImmediateVictory: false,
                    ImmediateScore: ep.HeuristicScore, EndsTurn: true));
            }
        }

        return list;
    }

    private static (bool Ok, double Value, bool Died, bool Won, int Nodes) EvaluateCandidateInWorld(
        CombatRootSnapshot world,
        CandidateSpec cand,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int initialKnownTop,
        NewSolverConfig config,
        CancellationToken token)
    {
        SimStepState state = CombatStepEngine.CreateRoot(world, initialKnownTop);
        int startTurn = state.Combat.GetPlayerTurnNumber(player);
        int nodes = 0;

        int deterministicCount = cand.IsReveal ? cand.Actions.Count - 1 : cand.Actions.Count;
        for (int i = 0; i < deterministicCount; i++)
        {
            PlanAction act = cand.Actions[i];
            if (act.Kind == PlanActionKind.EndTurn)
            {
                List<StepOutcome> endOutcomes = CombatStepEngine.ExpandAction(
                    state, act, player, names, Math.Min(3, config.MaxChoiceBranches), baseline);
                if (endOutcomes.Count == 0)
                    return (false, double.NegativeInfinity, true, false, nodes);
                double bestEndVal = double.NegativeInfinity;
                bool bestDead = true;
                bool bestWon = false;
                foreach (StepOutcome eo in endOutcomes)
                {
                    (double v, bool d, bool w, int n) = SolveContinuation(
                        eo.State, player, names, baseline, startTurn, config, token);
                    nodes += n;
                    if (v > bestEndVal)
                    {
                        bestEndVal = v;
                        bestDead = d;
                        bestWon = w;
                    }
                }
                return (true, bestEndVal, bestDead, bestWon, nodes);
            }

            if (!CombatStepEngine.TryReplayAction(state, act, player, out _, out _))
                return (false, double.NegativeInfinity, true, false, nodes);
        }

        if (cand.IsReveal)
        {
            PlanAction revealAct = cand.Actions[^1];
            PlanAction baseReveal = CombatStepEngine.HasRevealedChoice(revealAct)
                ? revealAct with { Choice = null, NestedChoices = null, NestedChoicesBeforePrimary = 0 }
                : revealAct;
            List<StepOutcome> revOutcomes = CombatStepEngine.ExpandAction(
                state, baseReveal, player, names, config.MaxChoiceBranches, baseline);
            if (revOutcomes.Count == 0)
                return (false, double.NegativeInfinity, true, false, nodes);

            double bestVal = double.NegativeInfinity;
            bool bestDead = true;
            bool bestWon = false;
            foreach (StepOutcome ro in revOutcomes)
            {
                (double v, bool d, bool w, int n) = SolveContinuation(
                    ro.State, player, names, baseline, startTurn, config, token);
                nodes += n;
                if (v > bestVal)
                {
                    bestVal = v;
                    bestDead = d;
                    bestWon = w;
                }
            }
            return (true, bestVal, bestDead, bestWon, nodes);
        }

        (double finalVal, bool finalDead, bool finalWon, int finalNodes) = SolveContinuation(
            state, player, names, baseline, startTurn, config, token);
        return (true, finalVal, finalDead, finalWon, nodes + finalNodes);
    }

    private static (double Value, bool Died, bool Won, int Nodes) SolveContinuation(
        SimStepState initialState,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int rootTurn,
        NewSolverConfig config,
        CancellationToken token)
    {
        int totalNodes = 0;
        if (IsTerminal(initialState, player, out bool initDead, out bool initWon))
        {
            return (PartyEvaluator.EvaluateExactUtilityForAggregation(initialState, baseline),
                initDead, initWon, 0);
        }

        int horizonTurn = rootTurn + config.LookaheadTurns;
        List<SimStepState> frontier = [initialState];
        double bestOverall = double.NegativeInfinity;
        bool bestOverallDead = true;
        bool bestOverallWon = false;
        double bestVictory = double.NegativeInfinity;
        bool foundVictory = false;

        int maxTurnIterations = config.LookaheadTurns + 1;
        for (int iter = 0; iter < maxTurnIterations && frontier.Count > 0; iter++)
        {
            if (token.IsCancellationRequested)
                break;

            List<SimStepState> nextTurnFrontier = [];
            Dictionary<ulong, double> nextTurnSeen = [];
            int turnWidth = iter switch
            {
                0 => config.ContinuationTurnWidth,
                1 => config.ContinuationTurnWidth,
                2 or 3 => Math.Min(2, config.ContinuationTurnWidth),
                _ => 1,
            };
            int segNodeCap = iter switch
            {
                0 => config.ContinuationNodeCap,
                1 => Math.Max(28, config.ContinuationNodeCap * 4 / 5),
                2 or 3 => Math.Max(24, config.ContinuationNodeCap * 3 / 5),
                _ => Math.Max(18, config.ContinuationNodeCap * 2 / 5),
            };

            foreach (SimStepState state in frontier)
            {
                if (IsTerminal(state, player, out bool d0, out bool w0))
                {
                    double termScore = PartyEvaluator.EvaluateExactUtilityForAggregation(state, baseline);
                    UpdateBest(termScore, d0, w0, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                    continue;
                }

                int curTurn = state.Combat.GetPlayerTurnNumber(player);
                if (curTurn >= horizonTurn)
                {
                    double hScore = PartyEvaluator.EvaluateExactUtilityForAggregation(state, baseline);
                    UpdateBest(hScore, false, false, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                    continue;
                }

                (DeterministicEndpoint? win, List<SimStepState> endStates, int segNodes) = SolveSingleTurnInWorld(
                    state, player, names, baseline, segNodeCap, turnWidth, config.MaxChoiceBranches,
                    allowMidTurnDraws: iter <= 2, token);
                totalNodes += segNodes;

                if (win != null)
                {
                    UpdateBest(win.HeuristicScore, false, true, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                }

                foreach (SimStepState preEnd in endStates)
                {
                    int preTurn = preEnd.Combat.GetPlayerTurnNumber(player);
                    PlanAction endAct = new(PlanActionKind.EndTurn, preTurn);
                    List<StepOutcome> afterEnd = CombatStepEngine.ExpandAction(
                        preEnd, endAct, player, names, Math.Min(2, config.MaxChoiceBranches), baseline);
                    foreach (StepOutcome eo in afterEnd)
                    {
                        if (IsTerminal(eo.State, player, out bool ed, out bool ew))
                        {
                            double tScore = PartyEvaluator.EvaluateExactUtilityForAggregation(eo.State, baseline);
                            UpdateBest(tScore, ed, ew, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                            continue;
                        }
                        ulong nh = StateHash.Compute(eo.State.Simulator, eo.State.Combat, player, eo.State.KnownTopRemaining);
                        double evalScore = PartyEvaluator.EvaluateExactUtilityForAggregation(eo.State, baseline);
                        if (!nextTurnSeen.TryGetValue(nh, out double prev) || evalScore > prev)
                        {
                            nextTurnSeen[nh] = evalScore;
                            nextTurnFrontier.Add(eo.State);
                        }
                    }
                }
            }

            frontier = nextTurnFrontier
                .OrderByDescending(s => PartyEvaluator.EvaluateExactUtilityForAggregation(s, baseline))
                .Take(turnWidth)
                .ToList();
        }

        foreach (SimStepState survivor in frontier)
        {
            bool sDead = survivor.Simulator.State.GetCreature(player.Creature).IsDead || survivor.Combat.BattlewornDummyTimedOut;
            bool sWon = !sDead && AllEnemiesDead(survivor.Simulator, survivor.Combat);
            double hScore = PartyEvaluator.EvaluateExactUtilityForAggregation(survivor, baseline);
            UpdateBest(hScore, sDead, sWon, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
        }

        if (foundVictory && bestVictory >= bestOverall)
            return (bestVictory, false, true, totalNodes);

        return (bestOverall, bestOverallDead, bestOverallWon, totalNodes);
    }

    private static (DeterministicEndpoint? Victory, List<SimStepState> EndStates, int Nodes) SolveSingleTurnInWorld(
        SimStepState startState,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int nodeCap,
        int keepWidth,
        int maxChoiceBranches,
        bool allowMidTurnDraws,
        CancellationToken token)
    {
        DeterministicSegmentResult seg = DeterministicSegmentSolver.Solve(
            startState,
            player,
            names,
            baseline,
            nodeCap,
            maxEndTurnEndpoints: keepWidth + 1,
            maxRevealBranches: allowMidTurnDraws ? 2 : 0,
            maxChoiceBranches: Math.Min(3, maxChoiceBranches),
            includePotions: false,
            token);

        int nodes = seg.NodesExpanded;
        DeterministicEndpoint? bestWin = seg.BestVictory;
        List<(SimStepState State, double Score)> candidates = [];

        foreach (DeterministicEndpoint ep in seg.EndTurnEndpoints)
            candidates.Add((ep.State, ep.HeuristicScore));

        if (allowMidTurnDraws && seg.RevealBranches.Count > 0)
        {
            int curTurn = startState.Combat.GetPlayerTurnNumber(player);
            int subCap = Math.Max(18, nodeCap / 2);
            foreach (RevealBranch rev in seg.RevealBranches.Take(2))
            {
                if (rev.PostRevealState.Combat.GetPlayerTurnNumber(player) != curTurn)
                    continue;
                DeterministicSegmentResult sub = DeterministicSegmentSolver.Solve(
                    rev.PostRevealState,
                    player,
                    names,
                    baseline,
                    subCap,
                    maxEndTurnEndpoints: keepWidth,
                    maxRevealBranches: 0,
                    maxChoiceBranches: Math.Min(2, maxChoiceBranches),
                    includePotions: false,
                    token);
                nodes += sub.NodesExpanded;
                if (sub.BestVictory != null && (bestWin == null || sub.BestVictory.HeuristicScore > bestWin.HeuristicScore))
                    bestWin = sub.BestVictory;
                foreach (DeterministicEndpoint ep in sub.EndTurnEndpoints)
                    candidates.Add((ep.State, ep.HeuristicScore));
            }
        }

        List<SimStepState> kept = [];
        HashSet<ulong> seen = [];
        foreach ((SimStepState st, _) in candidates.OrderByDescending(c => c.Score))
        {
            ulong h = StateHash.Compute(st.Simulator, st.Combat, player, st.KnownTopRemaining);
            if (seen.Add(h))
            {
                kept.Add(st);
                if (kept.Count >= keepWidth)
                    break;
            }
        }

        if (kept.Count == 0 && bestWin == null)
            kept.Add(startState);

        return (bestWin, kept, nodes);
    }

    private static bool IsOptimalImmediateVictory(DeterministicEndpoint win, PartyRootBaseline baseline)
    {
        CombatPredictionSimulator sim = win.State.Simulator;
        SimulatedCombatState combat = win.State.Combat;
        foreach (Player p in combat.Players)
        {
            SimCreatureState cs = sim.State.GetCreature(p.Creature);
            if (cs.IsDead)
                return false;
            uint id = p.Creature.CombatId ?? 0;
            if (cs.CurrentHp < baseline.InitialPlayerHp.GetValueOrDefault(id, cs.CurrentHp))
                return false;
        }
        foreach (var (slot, cost) in baseline.PotionStrategicCosts)
        {
            if (cost > 0 && !combat.IsPotionAvailable(baseline.LocalPlayer, slot))
                return false;
        }
        return true;
    }

    private static bool IsForcedEndTurn(SimStepState state, Player player)
    {
        SimPlayerCombatState ps = state.Simulator.State.GetPlayerCombatState(player);
        foreach (var card in ps.Hand.Cards)
        {
            if (state.Combat.CanPlayCard(state.Simulator, card))
                return false;
        }
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (state.Combat.IsPotionAvailable(player, slot) && state.Combat.GetPotionAtSlot(player, slot) != null)
                return false;
        }
        return true;
    }

    private static bool IsTerminal(SimStepState state, Player player, out bool dead, out bool won)
    {
        dead = state.Simulator.State.GetCreature(player.Creature).IsDead || state.Combat.BattlewornDummyTimedOut;
        won = !dead && AllEnemiesDead(state.Simulator, state.Combat);
        return dead || won || !state.Simulator.IsInProgress;
    }

    private static bool AllEnemiesDead(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        foreach (Creature enemy in combat.Enemies)
        {
            SimCreatureState es = sim.State.GetCreature(enemy);
            if (es.IsAlive || combat.EffectiveEnemyHp(enemy, es) > 0)
                return false;
        }
        return true;
    }

    private static void UpdateBest(
        double score,
        bool dead,
        bool won,
        ref double bestOverall,
        ref bool bestDead,
        ref bool bestWon,
        ref double bestVictory,
        ref bool foundVictory)
    {
        if (won && (!foundVictory || score > bestVictory))
        {
            foundVictory = true;
            bestVictory = score;
        }
        if (score > bestOverall)
        {
            bestOverall = score;
            bestDead = dead;
            bestWon = won;
        }
    }
}
