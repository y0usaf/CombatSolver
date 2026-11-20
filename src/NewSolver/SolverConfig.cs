using System.Diagnostics;

namespace CombatSolver.NewSolver;

internal readonly record struct SearchDeadline(long Timestamp)
{
    public bool IsExpired => Timestamp > 0 && Stopwatch.GetTimestamp() >= Timestamp;

    public static SearchDeadline FromNow(long milliseconds)
        => new(Stopwatch.GetTimestamp() + Math.Max(1, milliseconds) * Stopwatch.Frequency / 1000);
}

internal sealed record NewSolverConfig(
    int Samples,
    int PilotSamples,
    int Stage2Survivors,
    int DeterministicNodeCap,
    int MaxEndTurnCandidates,
    int MaxRevealCandidates,
    int LookaheadTurns,
    int ContinuationTurnWidth,
    int ContinuationNodeCap,
    int MaxChoiceBranches,
    int SoftTimeBudgetMs,
    int MaxParallelism = 0)
{
    public static NewSolverConfig For(SolverSpeed speed) => speed switch
    {
        SolverSpeed.Fast => new(
            Samples: 8,
            PilotSamples: 4,
            Stage2Survivors: 4,
            DeterministicNodeCap: 180,
            MaxEndTurnCandidates: 8,
            MaxRevealCandidates: 6,
            LookaheadTurns: 7,
            ContinuationTurnWidth: 2,
            ContinuationNodeCap: 42,
            MaxChoiceBranches: 4,
            SoftTimeBudgetMs: 1200),
        SolverSpeed.Normal => new(
            Samples: 12,
            PilotSamples: 4,
            Stage2Survivors: 6,
            DeterministicNodeCap: 300,
            MaxEndTurnCandidates: 12,
            MaxRevealCandidates: 8,
            LookaheadTurns: 12,
            ContinuationTurnWidth: 3,
            ContinuationNodeCap: 55,
            MaxChoiceBranches: 5,
            SoftTimeBudgetMs: 2500),
        SolverSpeed.Thorough => new(
            Samples: 16,
            PilotSamples: 6,
            Stage2Survivors: 6,
            DeterministicNodeCap: 420,
            MaxEndTurnCandidates: 12,
            MaxRevealCandidates: 10,
            LookaheadTurns: 14,
            ContinuationTurnWidth: 4,
            ContinuationNodeCap: 80,
            MaxChoiceBranches: 6,
            SoftTimeBudgetMs: 5000),
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };
}

internal sealed record NewSolverCandidate(
    string Key,
    IReadOnlyList<PlanAction> Actions,
    bool IsReveal,
    double MeanUtility,
    double CvarUtility,
    double DeathRate,
    double WinRate,
    int EvaluatedWorlds);

internal sealed record NewSolverStats(
    int DeterministicNodesExpanded,
    int ContinuationNodesExpanded,
    int CandidatesEvaluated,
    int WorldsEvaluated,
    bool ImmediateLethalFound,
    long ElapsedMilliseconds);

internal sealed record NewSolverDecision(
    IReadOnlyList<PlanAction> CommittedActions,
    IReadOnlyList<NewSolverCandidate> Candidates,
    NewSolverStats Stats);
