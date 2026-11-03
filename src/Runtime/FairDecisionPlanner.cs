namespace CombatSolver;

internal enum FairPlannerMode
{
    SingleSample,
    MultiSample,
}

internal sealed record FairPlannerSettings(
    int Samples,
    int MaxCandidates,
    double DeathPenalty,
    double UnfinishedEnemyHpWeight);

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
    int Solves);

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
            Profile = profile,
        };

    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot isolatedPolicy,
        IReadOnlyList<PlanAction>? prefix,
        CancellationToken token)
        => new CombatBeamSolver(
            root,
            names,
            damage,
            isolatedPolicy with { RequestWorkTotals = new SearchRequestWorkTotals() },
            token,
            searchProfile: isolatedPolicy.Profile,
            fixedPrefixActions: prefix).Solve();

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

    public static double Utility(SolverResult result, FairPlannerSettings settings)
    {
        SolverSnapshot snapshot = result.Snapshot;
        double utility = -result.ProjectedBattleHpLost;
        if (snapshot.PlayerDead)
            return utility - settings.DeathPenalty;
        if (!snapshot.AllEnemiesDead)
            utility -= settings.UnfinishedEnemyHpWeight * snapshot.EnemyHp;
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
        FairPlannerSettings settings,
        CancellationToken token)
    {
        int sampleCount = samples.Count;
        SolverResult[] own = new SolverResult[sampleCount];
        ParallelOptions parallel = new() { CancellationToken = token, MaxDegreeOfParallelism = sampleCount };
        Parallel.For(0, sampleCount, parallel, sample => own[sample] = solve(samples[sample], null));

        string?[] ownKeys = own.Select(result => result.BestNode.Actions.Count == 0
            ? null
            : CandidateKey(result.BestNode.Actions[0])).ToArray();
        var proposals = ownKeys
            .Select((key, sample) => (key, sample))
            .Where(item => item.key != null)
            .GroupBy(item => item.key!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(settings.MaxCandidates)
            .Select(group => (Key: group.Key, Action: own[group.First().sample].BestNode.Actions[0], Count: group.Count()))
            .ToArray();
        if (proposals.Length == 0)
            return null;

        double?[,] utilities = new double?[proposals.Length, sampleCount];
        SolverResult?[,] routes = new SolverResult?[proposals.Length, sampleCount];
        int solves = sampleCount;
        if (proposals.Length > 1)
        {
            int[] extra = new int[sampleCount];
            Parallel.For(0, sampleCount, parallel, sample =>
            {
                for (int candidate = 0; candidate < proposals.Length; candidate++)
                {
                    if (string.Equals(ownKeys[sample], proposals[candidate].Key, StringComparison.Ordinal))
                        continue;
                    PlanAction action = proposals[candidate].Action;
                    bool resolved = !HasRevealedChoice(action)
                        && TryEvaluate(sample, candidate, action, revealedFallback: true);
                    if (resolved)
                        continue;
                    IReadOnlyList<PlanAction> variants;
                    try
                    {
                        variants = revealedVariants(samples[sample], action);
                    }
                    catch (InvalidOperationException ex) when (!token.IsCancellationRequested)
                    {
                        LogInvalid(sample, proposals[candidate].Key, ex);
                        continue;
                    }
                    foreach (PlanAction variant in variants)
                        TryEvaluate(sample, candidate, variant, revealedFallback: false);
                }
            });
            solves += extra.Sum();

            bool TryEvaluate(int sample, int candidate, PlanAction action, bool revealedFallback)
            {
                extra[sample]++;
                try
                {
                    SolverResult result = solve(samples[sample], [action]);
                    double utility = Utility(result, settings);
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
                if (!string.Equals(ownKeys[sample], proposals[candidate].Key, StringComparison.Ordinal))
                    continue;
                routes[candidate, sample] = own[sample];
                utilities[candidate, sample] = Utility(own[sample], settings);
            }
        }

        List<FairCandidateScore> scores = [];
        for (int candidate = 0; candidate < proposals.Length; candidate++)
        {
            double[] values = Enumerable.Range(0, sampleCount)
                .Where(sample => utilities[candidate, sample].HasValue)
                .Select(sample => utilities[candidate, sample]!.Value)
                .ToArray();
            int deaths = Enumerable.Range(0, sampleCount)
                .Count(sample => routes[candidate, sample]?.Snapshot.PlayerDead == true);
            double mean = values.Length == 0 ? double.NegativeInfinity : values.Average();
            double deviation = values.Length == 0 ? 0 : Math.Sqrt(values.Average(value => (value - mean) * (value - mean)));
            scores.Add(new FairCandidateScore(
                proposals[candidate].Key,
                proposals[candidate].Action,
                proposals[candidate].Count,
                values.Length,
                mean,
                values.Length == 0 ? 1 : (double)deaths / values.Length,
                deviation));
        }
        int quorum = Math.Max(1, (sampleCount * 3 + 3) / 4);
        FairCandidateScore best = scores
            .Where(score => score.ValidSamples >= quorum)
            .DefaultIfEmpty(scores.MaxBy(score => score.ValidSamples)!)
            .OrderByDescending(score => Math.Round(score.MeanUtility, 6))
            .ThenBy(score => score.DeathRate)
            .ThenBy(score => score.StandardDeviation)
            .ThenByDescending(score => score.Proposals)
            .First();
        int bestIndex = scores.IndexOf(best);
        int routeSample = Enumerable.Range(0, sampleCount)
            .Where(sample => routes[bestIndex, sample] != null)
            .MaxBy(sample => utilities[bestIndex, sample]!.Value);
        return new FairDecision(best.Action, routes[bestIndex, routeSample]!, scores, solves);
    }
}
