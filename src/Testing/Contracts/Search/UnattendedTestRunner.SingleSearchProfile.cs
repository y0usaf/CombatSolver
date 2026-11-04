using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSingleSearchProfileAsync(CombatState combat)
    {
        AssertRequiredPotionAuditSelectionAndTotals();

        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        policy = policy with { Profile = policy.Profile with { MaxExpandedNodes = 128, SoftTimeBudgetMilliseconds = 1500 },
            FixedBudget = true, BudgetOverrideMilliseconds = 1500, VerifyIncrementalSearch = false };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        List<string> phases = [];
        SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy,
            CancellationToken.None, progressCallback: progress => phases.Add(progress.Phase)));
        if (phases.Count == 0 || phases.Any(phase => phase.Contains("快速搜索") || phase.Contains("深化搜索"))
            || result.TotalExpandedNodes < result.ExpandedNodes || result.TotalSearchElapsed < result.Elapsed)
            throw new InvalidOperationException("Unified search progress or cumulative totals are invalid.");
        _completedChecks.Add("SingleSearch:OneProgressPhase:TotalWork:FixedBudget");
    }
}
