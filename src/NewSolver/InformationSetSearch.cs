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

    private sealed record EndTurnBranch(string Key, PlanAction Action, double Value, bool Died, bool Won);

    private readonly record struct WorldEvaluation(
        bool Ok,
        double Value,
        bool Died,
        bool Won,
        int Nodes,
        List<EndTurnBranch>? Branches = null);

    private const long NodesPerBudgetMillisecond = 8;
    private const long HardBudgetMultiplier = 2;

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
        long softBudgetMs = Math.Max(1, config.SoftTimeBudgetMs);
        long nodeBudget = softBudgetMs * NodesPerBudgetMillisecond;
        SearchDeadline hardDeadline = SearchDeadline.FromNow(softBudgetMs * HardBudgetMultiplier);

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
            token,
            hardDeadline);

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
        List<EndTurnBranch>?[,] branches = new List<EndTurnBranch>?[candidates.Count, worldCount];
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

        long spentNodes = det.NodesExpanded;

        void RunBatch(List<(int CandIdx, int WorldIdx)> batch, int mandatoryJobs)
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
            WorldEvaluation[] results = new WorldEvaluation[batch.Count];
            bool[] finished = new bool[batch.Count];
            object gate = new();
            int launched = 0;
            bool stopped = false;
            long budget = Math.Max(0, nodeBudget - spentNodes);

            int NextJob()
            {
                lock (gate)
                {
                    if (stopped || launched >= batch.Count || token.IsCancellationRequested || hardDeadline.IsExpired)
                        return -1;
                    if (launched >= mandatoryJobs)
                    {
                        long finishedBefore = 0;
                        for (int j = 0; j < launched; j++)
                        {
                            if (finished[j])
                                finishedBefore += results[j].Nodes;
                        }
                        if (finishedBefore >= budget)
                        {
                            stopped = true;
                            return -1;
                        }
                    }
                    return launched++;
                }
            }

            Parallel.For(0, Math.Max(1, dop), opts, _ =>
            {
                for (int idx = NextJob(); idx >= 0; idx = NextJob())
                {
                    (int cIdx, int wIdx) = batch[idx];
                    WorldEvaluation result = EvaluateCandidateInWorld(
                        worlds[wIdx], candidates[cIdx], player, names, baseline, initialKnownTop, config, hardDeadline, token);
                    Interlocked.Add(ref totalContNodes, result.Nodes);
                    lock (gate)
                    {
                        results[idx] = result;
                        finished[idx] = true;
                    }
                }
            });

            long prefix = 0;
            for (int idx = 0; idx < launched; idx++)
            {
                if (idx >= mandatoryJobs && prefix >= budget)
                    break;
                prefix += results[idx].Nodes;
                if (!results[idx].Ok)
                    continue;
                (int cIdx, int wIdx) = batch[idx];
                scores[cIdx, wIdx] = results[idx].Value;
                valid[cIdx, wIdx] = true;
                died[cIdx, wIdx] = results[idx].Died;
                won[cIdx, wIdx] = results[idx].Won;
                branches[cIdx, wIdx] = results[idx].Branches;
            }
            spentNodes += prefix;
        }

        List<(int CandIdx, int WorldIdx)> stage1Jobs = new(candidates.Count * pilotWorlds);
        int searchedCandidates = 0;
        for (int c = 0; c < candidates.Count; c++)
        {
            if (!candidates[c].IsImmediateVictory)
                searchedCandidates++;
        }
        for (int w = 0; w < pilotWorlds; w++)
        {
            for (int c = 0; c < candidates.Count; c++)
            {
                if (!candidates[c].IsImmediateVictory)
                    stage1Jobs.Add((c, w));
            }
        }
        RunBatch(stage1Jobs, searchedCandidates);

        if (pilotWorlds < worldCount)
        {
            NewSolverCandidate[] pilot = new NewSolverCandidate[candidates.Count];
            for (int c = 0; c < candidates.Count; c++)
                pilot[c] = AggregateCandidate(c, candidates[c], scores, valid, died, won, pilotWorlds);
            List<int> scoredIndices = Enumerable.Range(0, candidates.Count)
                .OrderByDescending(c => pilot[c].EvaluatedWorlds > 0)
                .ThenBy(c => Math.Round(pilot[c].DeathRate, 4))
                .ThenByDescending(c => Math.Round(pilot[c].CvarUtility, 4))
                .ThenBy(c => candidates[c].Key, StringComparer.Ordinal)
                .ToList();

            HashSet<int> survivors = new(scoredIndices.Take(config.Stage2Survivors));
            int? bestReveal = scoredIndices.FirstOrDefault(c => candidates[c].IsReveal && !survivors.Contains(c), -1);
            if (bestReveal >= 0 && candidates[bestReveal.Value].IsReveal)
                survivors.Add(bestReveal.Value);

            int[] stage2Candidates = [.. survivors.Where(c => !candidates[c].IsImmediateVictory).OrderBy(x => x)];
            List<(int CandIdx, int WorldIdx)> stage2Jobs = new(stage2Candidates.Length * (worldCount - pilotWorlds));
            for (int w = pilotWorlds; w < worldCount; w++)
            {
                foreach (int c in stage2Candidates)
                    stage2Jobs.Add((c, w));
            }
            RunBatch(stage2Jobs, 0);
        }

        for (int c = 0; c < candidates.Count; c++)
        {
            if (candidates[c].EndsTurn)
                candidates[c] = ResolveEndTurnChoices(c, candidates[c], branches, scores, valid, died, won, worldCount);
        }

        List<NewSolverCandidate> ranked = new(candidates.Count);
        for (int c = 0; c < candidates.Count; c++)
            ranked.Add(AggregateCandidate(c, candidates[c], scores, valid, died, won, worldCount));

        int maxEvaluated = ranked.Max(c => c.EvaluatedWorlds);
        if (maxEvaluated == 0)
        {
            CandidateSpec fallback = candidates
                .Where(c => !c.IsReveal)
                .DefaultIfEmpty(candidates[0])
                .OrderByDescending(c => c.ImmediateScore)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .First();
            return new NewSolverDecision(
                fallback.Actions.Count > 0 ? fallback.Actions : [new PlanAction(PlanActionKind.EndTurn, turn)],
                ranked,
                new NewSolverStats(det.NodesExpanded, totalContNodes, candidates.Count, worldCount, false, sw.ElapsedMilliseconds));
        }
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

        // Co-op: when every evaluated line dies in every sampled world, the idle-teammate rollouts carry no ranking
        // signal. Fall back to the one-turn evaluation with the pending enemy turn priced in: survive this round
        // with the most HP, block and summons.
        if (baseline.IsCoop && eligible.All(c => c.EvaluatedWorlds > 0 && c.DeathRate > 1 - 1e-6))
        {
            Dictionary<string, double> immediate = candidates.ToDictionary(c => c.Key, c => c.ImmediateScore, StringComparer.Ordinal);
            best = eligible
                .OrderByDescending(c => Math.Round(immediate[c.Key], 4))
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .First();
        }

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

    private static CandidateSpec ResolveEndTurnChoices(
        int c,
        CandidateSpec spec,
        List<EndTurnBranch>?[,] branches,
        double[,] scores,
        bool[,] valid,
        bool[,] died,
        bool[,] won,
        int worldCount)
    {
        Dictionary<string, (PlanAction Action, double Sum, int Count)> totals = new(StringComparer.Ordinal);
        for (int w = 0; w < worldCount; w++)
        {
            if (!valid[c, w] || branches[c, w] is not { } list)
                continue;
            foreach (EndTurnBranch branch in list)
            {
                totals.TryGetValue(branch.Key, out var t);
                totals[branch.Key] = (t.Action ?? branch.Action, t.Sum + branch.Value, t.Count + 1);
            }
        }
        if (totals.Count == 0 || totals.Count == 1 && totals.ContainsKey(""))
            return spec;

        int maxCount = totals.Values.Max(t => t.Count);
        string chosenKey = totals
            .Where(t => t.Value.Count == maxCount)
            .OrderByDescending(t => Math.Round(t.Value.Sum / t.Value.Count, 6))
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .First().Key;

        for (int w = 0; w < worldCount; w++)
        {
            if (!valid[c, w] || branches[c, w] is not { } list)
                continue;
            EndTurnBranch? match = list.FirstOrDefault(b => string.Equals(b.Key, chosenKey, StringComparison.Ordinal));
            if (match == null)
            {
                valid[c, w] = false;
                continue;
            }
            scores[c, w] = match.Value;
            died[c, w] = match.Died;
            won[c, w] = match.Won;
        }

        PlanAction chosen = totals[chosenKey].Action;
        PlanAction endTurn = new(PlanActionKind.EndTurn, chosen.Turn)
        {
            TurnStartChoices = chosen.TurnStartChoices?.Where(IsCommittedEndPhaseChoice).ToArray(),
        };
        PlanAction[] actions = [.. spec.Actions];
        actions[^1] = endTurn;
        return spec with { Actions = actions };
    }

    private static bool IsCommittedEndPhaseChoice(PlanCardChoice choice)
        => choice.Timing is PlanChoiceTiming.PlayerTurnEnd or PlanChoiceTiming.EnemyTurn;

    private static string EndPhaseChoiceKey(PlanAction endTurn)
    {
        if (endTurn.TurnStartChoices is not { Count: > 0 } choices)
            return "";
        return string.Join(';', choices.Where(IsCommittedEndPhaseChoice).Select(choice =>
            choice.Effect + ":" + string.Join(',', choice.Cards.Select(card =>
                $"{card.CardId}+{card.UpgradeLevel}#{card.OptionOccurrence}"))));
    }

    private static WorldEvaluation EvaluateCandidateInWorld(
        CombatRootSnapshot world,
        CandidateSpec cand,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int initialKnownTop,
        NewSolverConfig config,
        SearchDeadline deadline,
        CancellationToken token)
    {
        SimStepState state = CombatStepEngine.CreateRoot(world, initialKnownTop);
        int startTurn = state.Combat.GetPlayerTurnNumber(player);
        int nodes = 0;
        WorldEvaluation failed = new(false, double.NegativeInfinity, true, false, 0);

        int deterministicCount = cand.IsReveal ? cand.Actions.Count - 1 : cand.Actions.Count;
        for (int i = 0; i < deterministicCount; i++)
        {
            PlanAction act = cand.Actions[i];
            if (act.Kind == PlanActionKind.EndTurn)
            {
                List<StepOutcome> endOutcomes = CombatStepEngine.ExpandAction(
                    state, act, player, names, Math.Min(3, config.MaxChoiceBranches), baseline);
                if (endOutcomes.Count == 0)
                    return failed;
                List<EndTurnBranch> branchValues = new(endOutcomes.Count);
                EndTurnBranch? best = null;
                foreach (StepOutcome eo in endOutcomes)
                {
                    (double v, bool d, bool w, int n, bool truncated) = SolveContinuation(
                        eo.State, player, names, baseline, startTurn, config, deadline, token);
                    nodes += n;
                    if (truncated)
                        return failed with { Nodes = nodes };
                    string key = EndPhaseChoiceKey(eo.Action);
                    int existing = branchValues.FindIndex(b => string.Equals(b.Key, key, StringComparison.Ordinal));
                    EndTurnBranch branch = new(key, eo.Action, v, d, w);
                    if (existing < 0)
                        branchValues.Add(branch);
                    else if (v > branchValues[existing].Value)
                        branchValues[existing] = branch;
                    if (best == null || v > best.Value)
                        best = branch;
                }
                return new WorldEvaluation(true, best!.Value, best.Died, best.Won, nodes, branchValues);
            }

            if (!CombatStepEngine.TryReplayAction(state, act, player, out _, out _))
                return failed with { Nodes = nodes };
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
                return failed with { Nodes = nodes };

            double bestVal = double.NegativeInfinity;
            bool bestDead = true;
            bool bestWon = false;
            foreach (StepOutcome ro in revOutcomes)
            {
                (double v, bool d, bool w, int n, bool truncated) = SolveContinuation(
                    ro.State, player, names, baseline, startTurn, config, deadline, token);
                nodes += n;
                if (truncated)
                    return failed with { Nodes = nodes };
                if (v > bestVal)
                {
                    bestVal = v;
                    bestDead = d;
                    bestWon = w;
                }
            }
            return new WorldEvaluation(true, bestVal, bestDead, bestWon, nodes);
        }

        (double finalVal, bool finalDead, bool finalWon, int finalNodes, bool finalTruncated) = SolveContinuation(
            state, player, names, baseline, startTurn, config, deadline, token);
        if (finalTruncated)
            return failed with { Nodes = nodes + finalNodes };
        return new WorldEvaluation(true, finalVal, finalDead, finalWon, nodes + finalNodes);
    }

    private static (double Value, bool Died, bool Won, int Nodes, bool Truncated) SolveContinuation(
        SimStepState initialState,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int rootTurn,
        NewSolverConfig config,
        SearchDeadline deadline,
        CancellationToken token)
    {
        int totalNodes = 0;
        if (IsTerminal(initialState, player, out bool initDead, out bool initWon))
        {
            return (PartyEvaluator.EvaluateExactUtilityForAggregation(initialState, baseline),
                initDead, initWon, 0, false);
        }

        int lookahead = LookaheadTurns(config, baseline);
        int horizonTurn = rootTurn + lookahead;
        List<(SimStepState State, double Score)> frontier = [(initialState, double.NaN)];
        double bestOverall = double.NegativeInfinity;
        bool bestOverallDead = true;
        bool bestOverallWon = false;
        double bestVictory = double.NegativeInfinity;
        bool foundVictory = false;

        int maxTurnIterations = lookahead + 1;
        for (int iter = 0; iter < maxTurnIterations && frontier.Count > 0; iter++)
        {
            if (token.IsCancellationRequested)
                break;
            if (deadline.IsExpired)
                return (bestOverall, bestOverallDead, bestOverallWon, totalNodes, true);

            List<(SimStepState State, double Score)> nextTurnFrontier = [];
            Dictionary<ulong, int> nextTurnSeen = [];
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

            foreach ((SimStepState state, double knownScore) in frontier)
            {
                if (IsTerminal(state, player, out bool d0, out bool w0))
                {
                    double termScore = double.IsNaN(knownScore)
                        ? PartyEvaluator.EvaluateExactUtilityForAggregation(state, baseline)
                        : knownScore;
                    UpdateBest(termScore, d0, w0, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                    continue;
                }

                int curTurn = state.Combat.GetPlayerTurnNumber(player);
                if (curTurn >= horizonTurn)
                {
                    double hScore = double.IsNaN(knownScore)
                        ? PartyEvaluator.EvaluateExactUtilityForAggregation(state, baseline)
                        : knownScore;
                    UpdateBest(hScore, false, false, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                    continue;
                }

                (DeterministicEndpoint? win, List<SimStepState> endStates, int segNodes) = SolveSingleTurnInWorld(
                    state, player, names, baseline, segNodeCap, turnWidth, config.MaxChoiceBranches, deadline, token);
                totalNodes += segNodes;
                if (deadline.IsExpired)
                    return (bestOverall, bestOverallDead, bestOverallWon, totalNodes, true);

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
                        double evalScore = PartyEvaluator.EvaluateExactUtilityForAggregation(eo.State, baseline);
                        if (IsTerminal(eo.State, player, out bool ed, out bool ew))
                        {
                            UpdateBest(evalScore, ed, ew, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
                            continue;
                        }
                        ulong nh = StateHash.Compute(eo.State.Simulator, eo.State.Combat, player, eo.State.KnownTopRemaining);
                        if (!nextTurnSeen.TryGetValue(nh, out int seenIndex))
                        {
                            nextTurnSeen[nh] = nextTurnFrontier.Count;
                            nextTurnFrontier.Add((eo.State, evalScore));
                        }
                        else if (evalScore > nextTurnFrontier[seenIndex].Score)
                        {
                            nextTurnFrontier[seenIndex] = (eo.State, evalScore);
                        }
                    }
                }
            }

            frontier = nextTurnFrontier
                .OrderByDescending(s => s.Score)
                .Take(turnWidth)
                .ToList();
        }

        foreach ((SimStepState survivor, double survivorScore) in frontier)
        {
            bool sDead = survivor.Simulator.State.GetCreature(player.Creature).IsDead || survivor.Combat.BattlewornDummyTimedOut;
            bool sWon = !sDead && AllEnemiesDead(survivor.Simulator, survivor.Combat);
            double hScore = double.IsNaN(survivorScore)
                ? PartyEvaluator.EvaluateExactUtilityForAggregation(survivor, baseline)
                : survivorScore;
            UpdateBest(hScore, sDead, sWon, ref bestOverall, ref bestOverallDead, ref bestOverallWon, ref bestVictory, ref foundVictory);
        }

        if (foundVictory && bestVictory >= bestOverall)
            return (bestVictory, false, true, totalNodes, false);

        return (bestOverall, bestOverallDead, bestOverallWon, totalNodes, false);
    }

    // Rollouts simulate teammates as idle (they end their turn without playing). Over many turns that overstates
    // how long enemies live and how much damage reaches the party, so co-op rollouts are trusted for a short
    // horizon only; the horizon evaluation prices the rest.
    internal const int CoopLookaheadTurns = 2;

    private static int LookaheadTurns(NewSolverConfig config, PartyRootBaseline baseline)
        => baseline.IsCoop ? Math.Min(config.LookaheadTurns, CoopLookaheadTurns) : config.LookaheadTurns;

    private static (DeterministicEndpoint? Victory, List<SimStepState> EndStates, int Nodes) SolveSingleTurnInWorld(
        SimStepState startState,
        Player player,
        SolverDisplayNames names,
        PartyRootBaseline baseline,
        int nodeCap,
        int keepWidth,
        int maxChoiceBranches,
        SearchDeadline deadline,
        CancellationToken token)
    {
        DeterministicSegmentResult seg = DeterministicSegmentSolver.Solve(
            startState,
            player,
            names,
            baseline,
            nodeCap,
            maxEndTurnEndpoints: keepWidth,
            maxRevealBranches: 0,
            maxChoiceBranches: Math.Min(3, maxChoiceBranches),
            includePotions: false,
            token,
            deadline,
            expandReveals: true);

        List<SimStepState> kept = new(seg.EndTurnEndpoints.Count);
        foreach (DeterministicEndpoint ep in seg.EndTurnEndpoints.OrderByDescending(c => c.HeuristicScore))
            kept.Add(ep.State);

        if (kept.Count == 0 && seg.BestVictory == null)
            kept.Add(startState);

        return (seg.BestVictory, kept, seg.NodesExpanded);
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
