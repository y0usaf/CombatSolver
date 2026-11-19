using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static partial class SolverController
{
    // Unlike session counters, this only advances when a background search starts
    // and survives the combat-end reset.
    internal static int SearchGenerationForTesting => _nextSearchGeneration;
    internal static void SetFullAutoEnabledForTesting(bool enabled)
    {
        AssertMainThread();
        _combat.FullAutoEnabled = enabled;
    }
    internal static void CancelCoopWaitForTesting()
    {
        AssertMainThread();
        CancelCoopAutoWait();
    }
    /// <summary>Selects a co-op pace without writing the player's settings file.</summary>
    internal static SolverCoopPlayMode SetCoopPlayModeForTesting(SolverCoopPlayMode mode)
    {
        AssertMainThread();
        SolverCoopPlayMode previous = SolverSettings.Current.CoopPlayMode;
        SolverSettings.ApplyForTesting(SolverSettings.Current with { CoopPlayMode = mode });
        Entry.Logger.Info($"[CombatSolver/Test] COOP_PLAY_MODE_TEST policy={mode} previous={previous}");
        return previous;
    }
    internal static int DeployedActionCountForTesting => _deployedActionCount;
    internal static int UnEndTurnCountForTesting => _combat.UnEndTurnCountThisTurn;
    internal static string? LastUnEndTriggerFingerprintForTesting => _combat.LastUnEndTriggerFingerprint;

    internal static void BeginCombatForTesting(CombatState state, bool fullAuto)
    {
        _combat = new SolverCombatSession
        {
            State = state,
            FullAutoEnabled = fullAuto,
        };
    }

    internal static void ResetUnEndTurnStateForTesting()
    {
        _combat.ReadyToEndTurnTurn = null;
        _combat.ReadyToEndTurnBaselineFingerprint = null;
        _combat.LastUnEndTriggerFingerprint = null;
        _combat.UnEndTurnCountThisTurn = 0;
        _combat.UnEndTurnInFlight = false;
        _combat.EvaluatedUnEndFingerprintsThisTurn.Clear();
    }

    // Unattended validation installs a frozen result at the normal deployment boundary.
    internal static void StartPredictedRouteForTesting(NGame host, CombatState state, SolverResult result, Action<CombatState> onCombatEnd)
    {
        AssertMainThread();
        BeginCombat(state);
        if (CombatReplayRecording.TestCombatEndObserver != null)
            throw new InvalidOperationException("Test deployment cannot replace an active replay observer.");
        CombatReplayRecording.TestCombatEndObserver = onCombatEnd;
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        _combat.LatestResult = result;
        _combat.LatestStamp = LiveCombatStamp.Capture(state);
        _combat.ContinuationSource = result;
        _combat.FullAutoEnabled = true;
        _combat.SearchesStarted = 1;
        _combat.ReplanCounts[ReplanCause.InitialSearch] = 1;
        LastCompletedResultForTesting = result;
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlay.RefreshControls();
        StartFullAutoDeployment(host, state, result);
    }
}
