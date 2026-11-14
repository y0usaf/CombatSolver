using System.Diagnostics;

namespace CombatSolver;

internal sealed record FairPlannerSettings(
    int Samples,
    int MaxCandidates,
    int BeamWidth,
    int MaxExpandedNodes,
    int SolveBudgetMilliseconds,
    int MaxParallelism = 0)
{
    public const double DeathPenalty = 200;
    public const double UnfinishedEnemyHpWeight = 0.25;
    public const double CommitAgreement = 1;
    public const double TieStandardErrors = 1;
    public const double TieMarginHp = 1;

    public static FairPlannerSettings For(SolverSpeed speed) => speed switch
    {
        SolverSpeed.Fast => new(8, 3, 16, 800, 2500),
        SolverSpeed.Normal => new(12, 4, 20, 1500, 5000),
        SolverSpeed.Thorough => new(16, 5, 24, 2500, 10000),
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };

    public SolverSearchProfile Profile(SolverSearchProfile basis) => basis with
    {
        BeamWidth = Math.Min(basis.BeamWidth, BeamWidth),
        MaxExpandedNodes = Math.Min(basis.MaxExpandedNodes, MaxExpandedNodes),
        SoftTimeBudgetMilliseconds = Math.Min(basis.SoftTimeBudgetMilliseconds, SolveBudgetMilliseconds),
    };
}

internal sealed record FairCandidateScore(
    string Key,
    PlanAction Action,
    int Proposals,
    int ValidSamples,
    double MeanUtility,
    double DeathRate,
    double StandardDeviation);

internal sealed record FairDecision(
    PlanAction Action,
    SolverResult Route,
    IReadOnlyList<FairCandidateScore> Candidates,
    int Solves,
    long OwnPhaseMilliseconds,
    long SlowestSolveMilliseconds,
    int CommitCount = 1,
    int SafetyStops = 0);

internal sealed class FairSolveTimeoutException(string message) : InvalidOperationException(message);

internal static class FairDecisionPlanner
{
    public static SearchPolicySnapshot IsolatedPolicy(SearchPolicySnapshot policy, SolverSearchProfile profile)
        => policy with
        {
            MaxDegreeOfParallelism = 1,
            UseBeamWidthPortfolio = false,
            UseNoveltyPortfolio = false,
            VerifyIncrementalSearch = false,
            DisableSharedPrimaryIncumbentsForTesting = true,
            PrimaryIncumbents = null,
            Diagnostics = new SearchDiagnosticsSink(_ => { }, _ => { }),
            MemoryPressureSignal = new SearchMemoryPressureSignal(),
            Profile = profile,
        };

    public static ISearchGcScope EnterGcScope(long configuredBudgetBytes, CancellationToken token)
        => SearchGcPolicy.EnterSearchScope(false, configuredBudgetBytes, new SearchMemoryPressureSignal(), token);

    public static ulong VisibleSeed(MegaCrit.Sts2.Core.Combat.CombatState state, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(state, fair: FairObservation.FromSeed(0));
        string visible = ContinuationStamp.CapturePredicted(
            player, root.ForkSimulator(), root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        ulong hash = 14695981039346656037;
        foreach (char character in visible)
            hash = (hash ^ character) * 1099511628211;
        return hash;
    }

    public static bool IsForcedEndTurn(MegaCrit.Sts2.Core.Entities.Players.Player player)
        => player.PlayerCombatState is { } combat
            && combat.Hand.Cards.All(card => !card.CanPlay())
            && Enumerable.Range(0, player.PotionSlots.Count).All(slot => player.GetPotionAtSlotIndex(slot) == null);

    public static SearchPolicySnapshot ForcedEndTurnPolicy(SearchPolicySnapshot isolatedPolicy)
        => isolatedPolicy with
        {
            Profile = isolatedPolicy.Profile with
            {
                BeamWidth = 1,
                MaxExpandedNodes = Math.Min(isolatedPolicy.Profile.MaxExpandedNodes, 64),
            },
        };

    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot isolatedPolicy,
        IReadOnlyList<PlanAction>? prefix,
        CancellationToken token)
    {
        int hardLimit = Math.Max(8000, 4 * isolatedPolicy.Profile.SoftTimeBudgetMilliseconds);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(hardLimit);
        try
        {
            return new CombatBeamSolver(
                root,
                names,
                damage,
                isolatedPolicy with { RequestWorkTotals = new SearchRequestWorkTotals() },
                limit.Token,
                searchProfile: isolatedPolicy.Profile,
                fixedPrefixActions: prefix).Solve();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new FairSolveTimeoutException($"fair sample solve exceeded {hardLimit} ms");
        }
    }

    public static IReadOnlyList<PlanAction> RevealedVariants(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot isolatedPolicy,
        PlanAction action,
        CancellationToken token)
    {
        string unchosen = UnchosenKey(action);
        return new CombatBeamSolver(
                root,
                names,
                damage,
                isolatedPolicy with { RequestWorkTotals = new SearchRequestWorkTotals() },
                token,
                searchProfile: isolatedPolicy.Profile)
            .BuildOpeningChoiceVariants(variant => string.Equals(UnchosenKey(variant), unchosen, StringComparison.Ordinal));
    }

    public static (int Length, int EnemyHp) RevealFreeLength(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot isolatedPolicy,
        IReadOnlyList<PlanAction> actions,
        CancellationToken token)
        => new CombatBeamSolver(
                root,
                names,
                damage,
                isolatedPolicy with { RequestWorkTotals = new SearchRequestWorkTotals() },
                token,
                searchProfile: isolatedPolicy.Profile)
            .RevealFreePrefixLength(actions);

    private static string UnchosenKey(PlanAction action)
        => Key(action with { Choice = null, NestedChoices = null, NestedChoicesBeforePrimary = 0 });

    private static bool HasRevealedChoice(PlanAction action)
        => action.GetActionChoicesInExecutionOrder().Any(choice => choice.Effect == PlanChoiceEffect.GenerateToHand);

    public static string CandidateKey(PlanAction action)
        => HasRevealedChoice(action) ? UnchosenKey(action) : Key(action);

    private static void LogInvalid(int sample, string candidate, InvalidOperationException ex)
        => Entry.Logger.Info(
            $"[CombatSolver/Fair] FAIR_CANDIDATE_INVALID sample={sample} candidate={candidate} " +
            $"error={ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");

    public static double Utility(SolverResult result)
    {
        SolverSnapshot snapshot = result.Snapshot;
        double utility = -result.ProjectedBattleHpLost;
        if (snapshot.PlayerDead)
            return utility - FairPlannerSettings.DeathPenalty;
        if (!snapshot.AllEnemiesDead)
            utility -= FairPlannerSettings.UnfinishedEnemyHpWeight * snapshot.EnemyHp;
        return utility;
    }

    public static string Key(PlanAction action)
    {
        string choices = string.Join(';', action.GetActionChoicesInExecutionOrder().Select(choice =>
            choice.Effect + ":" + string.Join(',', choice.Cards.Select(card =>
                $"{card.CardId}+{card.UpgradeLevel}#{card.OptionOccurrence}"))));
        return $"{action.Kind}|{action.CardStateKey}@{action.CardStateOccurrence}|{action.CardId}#{action.CardOccurrence}"
            + $"|{action.TargetCombatId}|{action.PotionSlot}|{choices}";
    }

    public static FairDecision? Decide(
        IReadOnlyList<CombatRootSnapshot> samples,
        Func<CombatRootSnapshot, IReadOnlyList<PlanAction>?, SolverResult> solve,
        Func<CombatRootSnapshot, PlanAction, IReadOnlyList<PlanAction>> revealedVariants,
        Func<CombatRootSnapshot, IReadOnlyList<PlanAction>, (int Length, int EnemyHp)> revealFreeLength,
        FairPlannerSettings settings,
        CancellationToken token)
    {
        int sampleCount = samples.Count;
        SolverResult?[] own = new SolverResult?[sampleCount];
        ParallelOptions parallel = new() { CancellationToken = token, MaxDegreeOfParallelism = settings.MaxParallelism > 0
            ? Math.Min(settings.MaxParallelism, sampleCount)
            : sampleCount };
        long slowestSolve = 0;
        int safetyStops = 0;
        Stopwatch ownPhase = Stopwatch.StartNew();
        Parallel.For(0, sampleCount, parallel, sample =>
        {
            try
            {
                own[sample] = TimedSolve(samples[sample], null);
            }
            catch (FairSolveTimeoutException ex) when (!token.IsCancellationRequested)
            {
                LogInvalid(sample, "-", ex);
            }
        });
        ownPhase.Stop();

        SolverResult TimedSolve(CombatRootSnapshot root, IReadOnlyList<PlanAction>? prefix)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                SolverResult result = solve(root, prefix);
                if (result.TurnLayerTimeBudgetStops > 0)
                    Interlocked.Increment(ref safetyStops);
                return result;
            }
            catch (FairSolveTimeoutException)
            {
                Interlocked.Increment(ref safetyStops);
                throw;
            }
            finally
            {
                long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long seen = Interlocked.Read(ref slowestSolve);
                while (elapsed > seen && Interlocked.CompareExchange(ref slowestSolve, elapsed, seen) != seen)
                    seen = Interlocked.Read(ref slowestSolve);
            }
        }

        PlanAction[]?[] ownSegments = new PlanAction[]?[sampleCount];
        int[] ownSegmentEnemyHp = new int[sampleCount];
        Parallel.For(0, sampleCount, parallel, sample =>
        {
            if (own[sample] is not { } result || result.BestNode.Actions.Count == 0)
                return;
            IReadOnlyList<PlanAction> actions = result.BestNode.Actions;
            int length = HasRevealedChoice(actions[0]) ? 1 : revealFreeLength(samples[sample], actions).Length;
            int revealed = Enumerable.Range(1, Math.Max(0, length - 1)).FirstOrDefault(index => HasRevealedChoice(actions[index]));
            ownSegments[sample] = [.. actions.Take(revealed > 0 ? revealed : Math.Max(1, length))];
            ownSegmentEnemyHp[sample] = HasRevealedChoice(actions[0])
                ? 0
                : revealFreeLength(samples[sample], ownSegments[sample]!).EnemyHp;
        });
        string?[] ownKeys = ownSegments.Select(segment => segment == null
            ? null
            : segment.Length == 1 ? CandidateKey(segment[0]) : string.Join(" ; ", segment.Select(Key))).ToArray();
        var proposals = ownKeys
            .Select((key, sample) => (key, sample))
            .Where(item => item.key != null)
            .GroupBy(item => item.key!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(settings.MaxCandidates)
            .Select(group => (Key: group.Key, Action: own[group.First().sample]!.BestNode.Actions[0],
                Prefix: ownSegments[group.First().sample]!, EnemyHp: ownSegmentEnemyHp[group.First().sample],
                Count: group.Count()))
            .ToArray();
        if (proposals.Length == 0)
            return null;

        double?[,] utilities = new double?[proposals.Length, sampleCount];
        SolverResult?[,] routes = new SolverResult?[proposals.Length, sampleCount];
        bool[,] timedOut = new bool[proposals.Length, sampleCount];
        int solves = sampleCount;
        if (proposals.Length > 1)
        {
            int[] extra = new int[sampleCount];
            // The per-solve budget bounds one solve, but a decision runs the own pass plus up to
            // (candidates-1) comparison solves per sample; on a loaded machine each solve can also
            // overrun its soft budget, and the observed live decisions reached 16-18 s at Normal.
            // Stop launching new comparisons once the decision has spent its solve budget and keep
            // what the own pass proposed; an unevaluated pair stays unavailable rather than being
            // replaced by a fabricated worst-case value.
            Stopwatch comparisonClock = Stopwatch.StartNew();
            long comparisonBudget = settings.SolveBudgetMilliseconds;
            // Every (sample, candidate) pair is an independent solve over that sample's own root:
            // nothing is shared except the write-back slots and the counters. Dispatch the pairs as
            // one flat list instead of one chain per sample, so a sample no longer queues its
            // (candidates - 1) comparisons behind its first one while the other lanes sit idle.
            // The same pairs still run, still stop at the same comparison budget, and still write
            // the same slots; only their dispatch order changes, which is why a decision now
            // finishes in fewer wall-clock rounds and fits more pairs inside its comparison budget.
            List<(int Sample, int Candidate)> comparisonJobs = [];
            for (int sample = 0; sample < sampleCount; sample++)
            {
                for (int candidate = 0; candidate < proposals.Length; candidate++)
                {
                    if (!string.Equals(ownKeys[sample], proposals[candidate].Key, StringComparison.Ordinal))
                        comparisonJobs.Add((sample, candidate));
                }
            }
            // Lanes: twice the configured sample width, never more than three quarters of the
            // logical cores, never fewer than the sample width. The sample width leaves cores idle
            // on a wide machine, so the comparison pass may use more; one lane per job instead
            // oversubscribes it, and past that point every solve covers fewer nodes inside its own
            // budget: 32 lanes on a 32-thread host made the estimate noisy enough to change a route
            // for the worse on late-ir-decmil-t2-a, while 24 lanes kept every route.
            int comparisonSampleLanes = settings.MaxParallelism > 0
                ? Math.Min(settings.MaxParallelism, sampleCount)
                : sampleCount;
            int comparisonLanes = Math.Max(comparisonSampleLanes,
                Math.Min(Environment.ProcessorCount * 3 / 4, comparisonSampleLanes * 2));
            ParallelOptions comparisonOptions = new()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Min(comparisonJobs.Count, Math.Max(1, comparisonLanes)),
            };
            Parallel.For(0, comparisonJobs.Count, comparisonOptions, index =>
                ComparePair(comparisonJobs[index].Sample, comparisonJobs[index].Candidate));

            void ComparePair(int sample, int candidate)
            {
                if (comparisonClock.ElapsedMilliseconds >= comparisonBudget)
                    return;
                PlanAction action = proposals[candidate].Action;
                if (!HasRevealedChoice(action)
                    && TryEvaluate(sample, candidate, proposals[candidate].Prefix, revealedFallback: true))
                    return;
                IReadOnlyList<PlanAction> variants;
                try
                {
                    variants = revealedVariants(samples[sample], action);
                }
                catch (InvalidOperationException ex) when (!token.IsCancellationRequested)
                {
                    LogInvalid(sample, proposals[candidate].Key, ex);
                    return;
                }
                foreach (PlanAction variant in variants)
                    TryEvaluate(sample, candidate, [variant], revealedFallback: false);
            }
            solves += extra.Sum();

            bool TryEvaluate(int sample, int candidate, IReadOnlyList<PlanAction> prefix, bool revealedFallback)
            {
                Interlocked.Increment(ref extra[sample]);
                try
                {
                    SolverResult result = TimedSolve(samples[sample], prefix);
                    double utility = Utility(result);
                    if (utilities[candidate, sample] is not { } known || utility > known)
                    {
                        routes[candidate, sample] = result;
                        utilities[candidate, sample] = utility;
                    }
                    return true;
                }
                catch (InvalidPlannedChoiceBranchException) when (revealedFallback && !token.IsCancellationRequested)
                {
                    return false;
                }
                catch (FairSolveTimeoutException ex) when (!token.IsCancellationRequested)
                {
                    timedOut[candidate, sample] = true;
                    LogInvalid(sample, proposals[candidate].Key, ex);
                    return true;
                }
                catch (InvalidOperationException ex) when (!token.IsCancellationRequested)
                {
                    LogInvalid(sample, proposals[candidate].Key, ex);
                    return true;
                }
            }
        }
        for (int sample = 0; sample < sampleCount; sample++)
        {
            for (int candidate = 0; candidate < proposals.Length; candidate++)
            {
                if (!string.Equals(ownKeys[sample], proposals[candidate].Key, StringComparison.Ordinal)
                    || utilities[candidate, sample].HasValue)
                    continue;
                routes[candidate, sample] = own[sample];
                utilities[candidate, sample] = Utility(own[sample]!);
            }
        }

        int quorum = Math.Max(1, (sampleCount * 3 + 3) / 4);
        double?[,] values = new double?[proposals.Length, sampleCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double? worst = null;
            for (int candidate = 0; candidate < proposals.Length; candidate++)
            {
                if (utilities[candidate, sample] is { } utility)
                    worst = worst is { } known ? Math.Min(known, utility) : utility;
            }
            for (int candidate = 0; candidate < proposals.Length; candidate++)
                values[candidate, sample] = utilities[candidate, sample] ?? (timedOut[candidate, sample] ? worst : null);
        }
        int[] validCounts = Enumerable.Range(0, proposals.Length)
            .Select(candidate => Enumerable.Range(0, sampleCount).Count(sample => values[candidate, sample].HasValue))
            .ToArray();
        int[] eligible = Enumerable.Range(0, proposals.Length).Where(candidate => validCounts[candidate] >= quorum).ToArray();
        if (eligible.Length == 0)
            eligible = [Enumerable.Range(0, proposals.Length).MaxBy(candidate => validCounts[candidate])];
        int[] common = Enumerable.Range(0, sampleCount)
            .Where(sample => eligible.All(candidate => values[candidate, sample].HasValue))
            .ToArray();

        List<FairCandidateScore> scores = [];
        for (int candidate = 0; candidate < proposals.Length; candidate++)
        {
            int[] scored = eligible.Contains(candidate) && common.Length > 0
                ? common
                : Enumerable.Range(0, sampleCount).Where(sample => values[candidate, sample].HasValue).ToArray();
            double[] candidateValues = scored.Select(sample => values[candidate, sample]!.Value).ToArray();
            int deaths = scored.Count(sample => routes[candidate, sample]?.Snapshot.PlayerDead == true);
            double mean = candidateValues.Length == 0 ? double.NegativeInfinity : candidateValues.Average();
            double deviation = candidateValues.Length == 0
                ? 0
                : Math.Sqrt(candidateValues.Average(value => (value - mean) * (value - mean)));
            scores.Add(new FairCandidateScore(
                proposals[candidate].Key,
                proposals[candidate].Action,
                proposals[candidate].Count,
                validCounts[candidate],
                mean,
                candidateValues.Length == 0 ? 1 : (double)deaths / candidateValues.Length,
                deviation));
        }
        int leader = eligible
            .OrderByDescending(candidate => Math.Round(scores[candidate].MeanUtility, 6))
            .ThenBy(candidate => scores[candidate].DeathRate)
            .ThenByDescending(candidate => scores[candidate].Proposals)
            .ThenBy(candidate => proposals[candidate].Key, StringComparer.Ordinal)
            .First();
        int bestIndex = eligible
            .Where(candidate => candidate == leader
                || IsTied(leader, candidate, common, values))
            .OrderBy(candidate => scores[candidate].DeathRate)
            .ThenBy(candidate => proposals[candidate].EnemyHp)
            .ThenByDescending(candidate => scores[candidate].Proposals)
            .ThenByDescending(candidate => Math.Round(scores[candidate].MeanUtility, 6))
            .ThenBy(candidate => proposals[candidate].Key, StringComparer.Ordinal)
            .First();
        FairCandidateScore best = scores[bestIndex];
        int routeSample = Enumerable.Range(0, sampleCount)
            .Where(sample => routes[bestIndex, sample] != null)
            .OrderByDescending(sample => utilities[bestIndex, sample]!.Value)
            .ThenBy(sample => sample)
            .First();
        SolverResult route = routes[bestIndex, routeSample]!;
        return new FairDecision(best.Action, route, scores, solves,
            ownPhase.ElapsedMilliseconds, Interlocked.Read(ref slowestSolve),
            AgreedCommitCount(route, proposals[bestIndex].Prefix.Length, Enumerable.Range(0, sampleCount)
                .Select(sample => routes[bestIndex, sample])
                .OfType<SolverResult>()
                .ToArray(), sampleCount),
            Volatile.Read(ref safetyStops));
    }

    private static bool IsTied(int leader, int candidate, int[] common, double?[,] values)
    {
        if (common.Length == 0)
            return false;
        double[] differences = common
            .Select(sample => values[leader, sample]!.Value - values[candidate, sample]!.Value)
            .ToArray();
        double mean = differences.Average();
        if (mean <= Math.Max(1e-6, FairPlannerSettings.TieMarginHp))
            return true;
        if (differences.Length < 2)
            return false;
        double variance = differences.Sum(value => (value - mean) * (value - mean)) / (differences.Length - 1);
        return mean <= FairPlannerSettings.TieStandardErrors * Math.Sqrt(variance / differences.Length);
    }

    private static int AgreedCommitCount(SolverResult route, int segment, SolverResult[] sampleRoutes, int sampleCount)
    {
        IReadOnlyList<PlanAction> lead = route.BestNode.Actions;
        int commit = Math.Max(1, Math.Min(segment, lead.Count));
        int required = (int)Math.Ceiling(FairPlannerSettings.CommitAgreement * sampleCount);
        while (commit < lead.Count
            && lead[commit - 1].Kind != PlanActionKind.EndTurn
            && lead[commit].Turn == lead[0].Turn)
        {
            int agreeing = sampleRoutes.Count(sample =>
            {
                IReadOnlyList<PlanAction> actions = sample.BestNode.Actions;
                if (actions.Count <= commit)
                    return false;
                for (int index = 1; index <= commit; index++)
                {
                    if (!string.Equals(Key(actions[index]), Key(lead[index]), StringComparison.Ordinal))
                        return false;
                }
                return true;
            });
            if (agreeing < required)
                break;
            commit++;
        }
        return commit;
    }
}
