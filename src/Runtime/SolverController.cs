using System.Diagnostics;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal enum SearchReason
{
    Manual,
    AutoTurnStart,
    Deploy,
    FullAuto,
    DeploymentDrift,
    PlanExhausted,
    InformationRevealed,
}

internal enum ReplanCause
{
    InitialSearch,
    StateMismatch,
    ManualDivergence,
    ContinuationMissing,
    DeploymentDrift,
    PlanExhausted,
    ExplicitRequest,
    InformationRevealed,
}

internal static partial class SolverController
{
    private static SolverCombatSession _combat = new();
    private static SolverSearchSession? _search;
    private static SolverDeploymentSession? _deployment;
    private static CombatBugReportClassificationSnapshot? _lastBugReportClassification;
    private static ManualProjectionComparison? _lastManualProjectionComparison;
    private static int _nextSearchGeneration;
    private static int _combatLifecycleGeneration;
    private static bool _solverDisabled;
    private static bool _stopFullAutoOnCombatEnd;
    private static bool _stopFullAutoOnDeathTurn;
    private static bool _stopFullAutoOnWorseRecalculation;
    private static readonly SearchFramePressureSignal FramePressureSignal = new();
    private static readonly HashSet<string> DeployedCardIdsForTesting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeployedPotionIdsForTesting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Task> PendingSearchReferenceReleases = [];
    private static readonly List<PotionSlotDirective> _potionDirectivesForTesting = [];
    private static readonly List<Task> PendingDeploymentReferenceReleases = [];
    private static readonly List<Task> PendingDeferredSearchReleases = [];
    private static readonly List<Task> PendingCombatDeferredOperations = [];
    private static int _searchReferenceReleaseScheduledCountForTesting;
    private static int _searchReferenceReleaseCompletedCountForTesting;
    private static int _searchCtsDisposeCountForTesting;
    private static int _deploymentReferenceReleaseScheduledCountForTesting;
    private static int _deploymentReferenceReleaseCompletedCountForTesting;
    private static int _deploymentCtsDisposeCountForTesting;
    private static CancellationTokenSource? _deferredSearchCts;
    private static int _deferredSearchId;
    private static CancellationTokenSource _combatDeferredOperationCancellation = new();

    public static bool IsSearching
        => _search != null
           || _deferredSearchCts != null
           || PlayerTurnSetupCoordinator.IsSearching;
    public static bool IsDeploying => _deployment != null;
    public static bool IsStoppingSearch
        => _search?.Interaction.StopRequested == true || PlayerTurnSetupCoordinator.IsStoppingSearch;
    public static bool SolverDisabled => _solverDisabled;
    public static bool CanApplyCurrentTurn
        => _search is { } search
               && search.Interaction.CurrentTakeoverRequest == null
               && search.Interaction.CanAcceptTakeover
               && Volatile.Read(ref search.Interaction.Progress)?.CurrentTurnPreview != null
           || PlayerTurnSetupCoordinator.CanApplyCurrentTurn;
    public static bool IsApplyingCurrentTurn
        => _search?.Interaction.IsApplyingCurrentTurn == true
           || PlayerTurnSetupCoordinator.IsApplyingCurrentTurn;
    public static bool CanAdoptCurrentRoute
        => _search is { } search
               && search.Interaction.CurrentTakeoverRequest == null
               && search.Interaction.RenderedRouteAdoptionSeed != null
               && search.Interaction.CanAcceptTakeover
           || HasCurrentStoppedRoute()
           || PlayerTurnSetupCoordinator.CanAdoptCurrentRoute;
    public static bool IsAdoptingCurrentRoute
        => _search?.Interaction.IsAdoptingRoute == true
           || PlayerTurnSetupCoordinator.IsAdoptingCurrentRoute;


    internal static void InvalidateRenderedRouteAdoptionSeed()
    {
        if (_search != null)
            _search.Interaction.RenderedRouteAdoptionSeed = null;
        PlayerTurnSetupCoordinator.InvalidateRenderedRouteAdoptionSeed();
    }

    public static bool CanExecuteCurrentTurn
    {
        get
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null || !CombatManager.Instance.IsInProgress)
                return false;
            return _combat.LatestResult != null
                    && _combat.LatestStamp == LiveCombatStamp.Capture(state)
                || !_combat.RouteFrozen && PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state);
        }
    }

    /// <summary>
    /// True whenever the current run is a networked multiplayer session (host or client).
    /// The solver must stay fully inert in this case: the game's own multiplayer turn
    /// synchronization has no concept of a client silently auto-planning another player's turn.
    /// </summary>
    public static bool IsMultiplayerSession
        => RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type.IsMultiplayer();

    public static bool FullAutoEnabled => _combat.FullAutoEnabled;
    public static bool AutoRequested => !_solverDisabled && SolverSettings.Current.AutoPlay;
    public static SolverAutoStatusSnapshot AutoStatus => SolverAutoStatus.Capture(FullAutoEnabled);
    public static bool RouteFrozen => _combat.RouteFrozen;
    public static bool HasRetainedRoute => _combat.LatestResult != null || _combat.ContinuationSource != null;
    public static bool AutomaticSearchPaused => _combat.AutomaticSearchPaused;
    internal static bool ShouldAutomaticallySearchNextTurn
        => FullAutoEnabled && !_combat.RouteFrozen;

    public static void SetRouteFrozen(NGame host, CombatState state, bool frozen)
    {
        AssertMainThread();
        if (_solverDisabled || IsSearching || IsDeploying || !ReferenceEquals(_combat.State, state))
            return;
        if (frozen && !HasRetainedRoute)
            return;
        _combat.RouteFrozen = frozen;
        if (frozen)
        {
            _combat.FullAutoEnabled = false;
            SolverAutoStatus.Record("stop", "route_frozen");
            CancelDeferredSearch();
        }
        else if (FullAutoEnabled
                 && _combat.LatestStamp != LiveCombatStamp.Capture(state)
                 && CanSolve(state, out _))
        {
            RequestSearch(host, state, SearchReason.FullAuto);
        }
        SolverOverlay.RefreshControls();
    }
    internal static int SearchesStartedForTesting => _combat.SearchesStarted;
    public static bool StopFullAutoOnCombatEnd => _stopFullAutoOnCombatEnd;
    public static bool StopFullAutoOnDeathTurn => _stopFullAutoOnDeathTurn;
    public static bool StopFullAutoOnWorseRecalculation => _stopFullAutoOnWorseRecalculation;
    public static SolverTheftPolicy? TheftPolicy => _combat.TheftPolicy;
    internal static long ReviewedWorldlinesTotal => _combat.ReviewedWorldlinesTotal;
    internal static SolverResult? CurrentResultForBugReport => _combat.LatestResult ?? _combat.ContinuationSource;
    internal static string ReplanAuditForBugReport => DescribeReplanAudit();
    internal static string BuildBugReportDescription(string playerDescription)
        => CombatBugReportDescription.AppendAutomaticClassification(
            playerDescription,
            CombatManager.Instance.IsInProgress || _lastBugReportClassification == null
                ? CaptureBugReportClassification()
                : _lastBugReportClassification);
    internal static int UnexpectedReplanCount
        => _combat.ReplanCounts.GetValueOrDefault(ReplanCause.StateMismatch)
           + _combat.ReplanCounts.GetValueOrDefault(ReplanCause.DeploymentDrift);
    internal static string ControlModeForBugReport
        => _combat.ManualControlObserved
            ? "manual_plus_solver"
            : "solver_only";
    internal static int? LastSolverDeployedTurnForBugReport => _combat.LastSolverDeployedTurn;
    internal static SolverResult? LastCompletedResultForTesting { get; private set; }
    internal static bool HasActiveSearchSessionForTesting => _search != null;
    internal static SolverResult? LastTurnSetupResultForTesting { get; private set; }
    internal static Exception? LastSearchFailureForTesting { get; private set; }
    internal static Exception? LastTurnSetupFailureForTesting { get; private set; }
    internal static bool LastFullAutoStoppedForWorseRecalculationForTesting { get; private set; }
    internal static bool LastFullAutoStoppedAtLiveRiskForTesting { get; private set; }
    internal static int? LastReusedTurnForTesting { get; private set; }
    internal static int? LastReusedProjectedBattleHpLostForTesting { get; private set; }
    internal static int UnexpectedReplanCountForTesting
        => UnexpectedReplanCount;
    internal static int ManualDivergenceCountForTesting
        => _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ManualDivergence);
    internal static bool ManualRouteImprovementDetected
        => _combat.ManualRouteImprovementDetected;
    internal static ManualProjectionComparison? LastManualProjectionComparisonForTesting
        => _combat.LastManualProjectionComparison;
    internal static int NoGcRegionRolloverCountForTesting
        => SearchGcPolicy.RolloverCountForTesting;
    internal static SearchMemoryUsageSnapshot CaptureSearchMemoryUsage()
    {
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        long startedAt = System.Environment.TickCount64;
        SearchMemoryPressureSignal? signal = _search?.MemoryPressureSignal
            ?? PlayerTurnSetupCoordinator.CurrentMemoryPressureSignal;
        SearchMemoryPressureUsage pressure = signal?.CaptureUsage()
            ?? SearchMemoryPressureUsage.Disabled;
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        PhysicalMemoryUsage physicalMemory = PhysicalMemoryUsage.Capture(memory);
        long systemMemoryLimit = pressure.SystemMemoryLimitBytes == long.MaxValue
            ? SearchGcPolicy.ResolveSystemMemoryLimit(memory)
            : pressure.SystemMemoryLimitBytes;
        if (physicalMemory.TotalBytes > 0)
            systemMemoryLimit = Math.Min(systemMemoryLimit, physicalMemory.TotalBytes);
        return new SearchMemoryUsageSnapshot(
            System.Environment.WorkingSet,
            physicalMemory.UsedBytes,
            settings.NoGcRegionBudgetBytes,
            IsSearching,
            pressure.AllocatedBytes,
            pressure.AllocationLimitBytes,
            pressure.ProjectedMemoryLoadBytes,
            systemMemoryLimit,
            pressure.Reclaiming,
            SearchGcPolicy.IsBackgroundReclaiming)
        {
            PhysicalMemoryTotalBytes = physicalMemory.IsPhysicalSample ? physicalMemory.TotalBytes : 0,
            SampledAtUtc = sampledAt,
            SampleDurationMilliseconds = System.Environment.TickCount64 - startedAt,
            IsServerGc = GCSettings.IsServerGC,
            GcMemoryInfoIndex = memory.Index,
            GcHighMemoryLoadThresholdBytes = memory.HighMemoryLoadThresholdBytes,
            GcTotalAvailableMemoryBytes = memory.TotalAvailableMemoryBytes,
        };
    }
    internal static void LogSearchMemoryDisplayState(
        SearchMemoryUsageSnapshot snapshot,
        string displayState,
        double displayRatio)
    {
        Entry.Logger.Info(
            $"[CombatSolver/Test] MEMORY_MONITOR_DISPLAY state={displayState} " +
            $"display_ratio={displayRatio:F3} search_active={snapshot.SearchActive.ToString().ToLowerInvariant()} " +
            $"foreground_reclaim={snapshot.Reclaiming.ToString().ToLowerInvariant()} " +
            $"background_reclaim={snapshot.BackgroundReclaiming.ToString().ToLowerInvariant()} " +
            $"search_allocated={snapshot.SearchAllocatedBytes} search_limit={snapshot.SearchAllocationLimitBytes} " +
            $"projected_memory_load={snapshot.ProjectedSystemMemoryLoadBytes} " +
            $"system_memory_limit={snapshot.SystemMemoryLimitBytes} " +
            $"physical_memory_used={snapshot.PhysicalMemoryUsedBytes} " +
            $"physical_memory_total={snapshot.PhysicalMemoryTotalBytes} " +
            $"physical_memory_available={snapshot.PhysicalMemoryAvailableBytes?.ToString() ?? "unknown"} " +
            $"physical_sample={snapshot.HasPhysicalMemorySample} " +
            $"system_occupied={snapshot.SystemOccupiedBytes} " +
            $"system_segment={snapshot.SystemSegmentRatio:F3} " +
            $"process_segment={snapshot.ProcessSegmentRatio:F3} " +
            $"allocation_pressure={snapshot.AllocationPressureRatio:F3} " +
            $"system_pressure={snapshot.SystemPressureRatio:F3} " +
            $"system_pressure_dominates={snapshot.SystemPressureDominates.ToString().ToLowerInvariant()} " +
            $"configured_budget={snapshot.ConfiguredMemoryBudgetBytes} " +
            $"working_set={snapshot.ProcessWorkingSetBytes} server_gc={snapshot.IsServerGc} " +
            $"gc_info_index={snapshot.GcMemoryInfoIndex} high_memory_threshold={snapshot.GcHighMemoryLoadThresholdBytes} " +
            $"gc_total_available={snapshot.GcTotalAvailableMemoryBytes} " +
            $"sample_utc={snapshot.SampledAtUtc:O} sample_duration_ms={snapshot.SampleDurationMilliseconds}");
    }
    internal static long LastDeployedActionStartedAtMillisecondsForTesting { get; private set; }
    internal static Task LastCombatReferenceReleaseForTesting { get; private set; } = Task.CompletedTask;
    internal static int CombatLifecycleGeneration
        => Volatile.Read(ref _combatLifecycleGeneration);
    internal static int SearchReferenceReleaseScheduledCountForTesting
        => Volatile.Read(ref _searchReferenceReleaseScheduledCountForTesting);
    internal static int SearchReferenceReleaseCompletedCountForTesting
        => Volatile.Read(ref _searchReferenceReleaseCompletedCountForTesting);
    internal static int SearchCtsDisposeCountForTesting
        => Volatile.Read(ref _searchCtsDisposeCountForTesting);
    internal static int PendingSearchReferenceReleaseCountForTesting
        => PendingSearchReferenceReleases.Count(static task => !task.IsCompleted);
    internal static bool WasCardDeployedForTesting(string cardId)
        => DeployedCardIdsForTesting.Contains(cardId);
    internal static bool WasPotionDeployedForTesting(string potionId)
        => DeployedPotionIdsForTesting.Contains(potionId);

    internal static void CancelSearchForTesting()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("搜索会话取消入口只能在无人测试中使用。");
        CancelSearch();
    }


    private static bool HasCurrentStoppedRoute()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        return state != null
            && CombatManager.Instance.IsInProgress
            && _combat.StoppedSearch is { StoppedResult: not null, StoppedStamp: { } stamp }
            && stamp == LiveCombatStamp.Capture(state);
    }

    private static bool TryAdoptStoppedRoute()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        NGame? host = NGame.Instance;
        if (state == null || host == null || _combat.StoppedSearch is not { } stopped)
            return false;
        LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
        SolverResult? result = stopped.TakeStoppedResult(stamp);
        _combat.StoppedSearch = null;
        if (result == null)
            return false;

        _combat.LatestResult = result;
        _combat.LatestStamp = stamp;
        _combat.ContinuationSource = result;
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] STOPPED_ROUTE_ADOPTED turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count}");
        return true;
    }

    internal static Task StartCombatDeferredOperation(
        Func<CancellationToken, Task> operationFactory)
    {
        AssertMainThread();
        ArgumentNullException.ThrowIfNull(operationFactory);
        CancellationToken token = _combatDeferredOperationCancellation.Token;
        Task operation = RunCombatDeferredOperationAsync(operationFactory, token);
        PendingCombatDeferredOperations.RemoveAll(static task => task.IsCompleted);
        if (!operation.IsCompleted)
            PendingCombatDeferredOperations.Add(operation);
        return operation;
    }

    private static async Task RunCombatDeferredOperationAsync(
        Func<CancellationToken, Task> operationFactory,
        CancellationToken token)
    {
        try
        {
            await operationFactory(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Reset owns cancellation. The operation remains in the reference-release barrier
            // until its state machine has dropped every captured combat/result reference.
        }
    }

    internal static async Task<(
        bool StaleCompletionPreservedCurrentSession,
        bool BarrierWaitedForBothOperations,
        int ReleasesScheduled,
        int ReleasesCompleted,
        int CancellationsDisposed)> ExerciseDeploymentSessionLifecycleForTestingAsync()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("部署会话生命周期测试只能在无人测试中使用。");
        PendingDeploymentReferenceReleases.RemoveAll(static task => task.IsCompleted);
        if (_deployment != null || PendingDeploymentReferenceReleases.Count != 0)
            throw new InvalidOperationException("部署会话生命周期测试要求控制器初始空闲。");

        int releasesScheduledBefore = Volatile.Read(
            ref _deploymentReferenceReleaseScheduledCountForTesting);
        int releasesCompletedBefore = Volatile.Read(
            ref _deploymentReferenceReleaseCompletedCountForTesting);
        int cancellationsDisposedBefore = Volatile.Read(
            ref _deploymentCtsDisposeCountForTesting);
        TaskCompletionSource firstCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SolverDeploymentSession first = new() { Operation = firstCompletion.Task };
        _deployment = first;
        CancelDeployment();
        SolverDeploymentSession second = new() { Operation = secondCompletion.Task };
        _deployment = second;
        CompleteDeployment(first);
        bool staleCompletionPreservedCurrentSession = ReferenceEquals(_deployment, second);
        CancelDeployment();
        Task referenceRelease = DrainDeploymentReferenceReleases();
        bool barrierWaitedForBothOperations = !referenceRelease.IsCompleted;
        firstCompletion.TrySetResult();
        barrierWaitedForBothOperations &= !referenceRelease.IsCompleted;
        secondCompletion.TrySetResult();
        await referenceRelease.ConfigureAwait(false);

        return (
            staleCompletionPreservedCurrentSession,
            barrierWaitedForBothOperations,
            Volatile.Read(ref _deploymentReferenceReleaseScheduledCountForTesting)
                - releasesScheduledBefore,
            Volatile.Read(ref _deploymentReferenceReleaseCompletedCountForTesting)
                - releasesCompletedBefore,
            Volatile.Read(ref _deploymentCtsDisposeCountForTesting)
                - cancellationsDisposedBefore);
    }

    internal static void RecordManualProjectionComparisonForTesting(
        int previousProjectedBattleHpLost,
        int currentProjectedBattleHpLost,
        int previousProjectedBattlePotionCount = 0,
        int currentProjectedBattlePotionCount = 0)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("手操战损比较入口只能在无人测试中使用。");
        RecordManualProjectionComparison(
            new ManualProjectionBaseline(1, previousProjectedBattleHpLost,
                previousProjectedBattlePotionCount, "test_manual_state_change"),
            currentTurnNumber: 1,
            currentProjectedBattleHpLost,
            currentProjectedBattlePotionCount);
    }

    internal static bool IsCurrentCombatLifecycle(
        CombatState combat,
        int lifecycleGeneration)
        => lifecycleGeneration == Volatile.Read(ref _combatLifecycleGeneration)
           && CombatManager.Instance.IsInProgress
           && !CombatManager.Instance.IsOverOrEnding
           && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat);

    internal static bool RecordTurnSetupFailure(
        CombatState combat,
        int lifecycleGeneration,
        Exception exception)
    {
        AssertMainThread();
        if (!IsCurrentCombatLifecycle(combat, lifecycleGeneration))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_FAILURE_IGNORED reason=stale_lifecycle " +
                $"operation_epoch={lifecycleGeneration} " +
                $"current_epoch={Volatile.Read(ref _combatLifecycleGeneration)}");
            return false;
        }
        _combat.BugReportIssues.RecordFailure(
            CombatBugReportIssueKind.TurnSetupFailure,
            exception);
        LastTurnSetupFailureForTesting = exception;
        Entry.Logger.Error(
            $"[CombatSolver/Test] TURN_SETUP_FAILURE exception={exception.GetBaseException()}");
        return true;
    }

    internal static bool RecordTurnSetupStateMismatch(
        CombatState combat,
        int lifecycleGeneration,
        string difference)
    {
        AssertMainThread();
        if (!IsCurrentCombatLifecycle(combat, lifecycleGeneration))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_MISMATCH_IGNORED reason=stale_lifecycle " +
                $"operation_epoch={lifecycleGeneration} " +
                $"current_epoch={Volatile.Read(ref _combatLifecycleGeneration)}");
            return false;
        }
        _combat.BugReportIssues.Record(
            CombatBugReportIssueKind.TurnSetupStateMismatch,
            difference);
        SolverOverlay.RefreshControls();
        return true;
    }

    public static void ApplyPersistentSettings(SolverSettingsSnapshot settings)
    {
        _solverDisabled = settings.SolverDisabled;
    }

    /// <summary>
    /// 显示服务器名字的取值口。游戏内一律是默认值（直接问 Godot），
    /// 只有 tools/search/OfflineSearchHarness 这种不启动 Godot 的进程会把它换成固定的 "headless"。
    /// </summary>
    internal static Func<string> DisplayServerNameProvider { get; set; } = static () => DisplayServer.GetName();

    internal static SearchPolicySnapshot CaptureSearchPolicy(
        SolverSettingsSnapshot settings,
        CombatState state,
        bool includeTurnSetup,
        SolverTheftPolicy? theftPolicy,
        SearchInteractionState? interaction = null)
    {
        FramePressureSignal.ResetPressure(
            recoveryEnabled: !string.Equals(
                DisplayServerNameProvider(),
                "headless",
                StringComparison.OrdinalIgnoreCase));
        int maxDegreeOfParallelism = UnattendedTestRunner.SearchMaxDegreeOfParallelismOverride
            ?? settings.SearchMaxDegreeOfParallelism;
        if (maxDegreeOfParallelism < 1
            || maxDegreeOfParallelism > SolverWeights.MaximumSearchMaxDegreeOfParallelism)
        {
            throw new InvalidOperationException(
                $"搜索并行度必须在 1..{SolverWeights.MaximumSearchMaxDegreeOfParallelism} 之间，" +
                $"实际为 {maxDegreeOfParallelism}。");
        }
        SolverSearchProfile profile = UnattendedTestRunner.CheckpointProfileOverride ?? settings.Profile;
        SearchPolicySnapshot policy = new(
            UnattendedTestRunner.BeamWeightPerturbationOverride is { } beamWeightPerturbation
                ? profile with { BeamWeightPerturbation = beamWeightPerturbation }
                : profile,
            settings.PotionPolicy,
            CapturePotionStrategy(state, settings.PotionPolicy),
            settings.EnableDetailedDiagnosticLogs,
            UnattendedTestRunner.VerifyIncrementalSearch,
            UnattendedTestRunner.FixedSearchBudget,
            UnattendedTestRunner.MeasureSearchPhases,
            maxDegreeOfParallelism,
            UnattendedTestRunner.SearchBudgetOverrideMilliseconds,
            includeTurnSetup,
            theftPolicy,
            settings.ActTransitionBossHpStrategy,
            settings.FinalBossHpStrategy,
            settings.AcceptableBattleHpLoss,
            new SearchDiagnosticsSink(
                Entry.Logger.Journal.Bind("info"),
                Entry.Logger.Journal.Bind("debug")),
            FramePressureSignal,
            new SearchMemoryPressureSignal())
        {
            Interaction = interaction,
            EarlyTurnExplorationDepth = UnattendedTestRunner.EarlyTurnExplorationDepth > 0
                ? UnattendedTestRunner.EarlyTurnExplorationDepth
                : settings.UseEarlyTurnExploration ? 2 : 0,
            EarlyTurnExplorationBudgetMilliseconds =
                UnattendedTestRunner.EarlyTurnExplorationDepth > 0
                    ? UnattendedTestRunner.EarlyTurnExplorationBudgetMilliseconds
                    : settings.UseEarlyTurnExploration ? 2_400_000 : 0,
            DevelopmentStrategy = UnattendedTestRunner.CurrentDevelopmentStrategy,
            UseNoveltyPortfolio = settings.UseNoveltyPortfolio
                || UnattendedTestRunner.UseNoveltyPortfolioOverride,
            UseBeamWidthPortfolio = settings.UseBeamWidthPortfolio
                || UnattendedTestRunner.UseBeamWidthPortfolioOverride,
            PredictPotionReward = settings.PredictPotionReward,
            BeamWidthPortfolioWidths = UnattendedTestRunner.BeamWidthPortfolioWidthsOverride,
            BeamWidthPortfolioPlainBaselineMember =
                UnattendedTestRunner.BeamWidthPortfolioPlainBaselineMemberOverride,
            PileOrderInvariantMask = UnattendedTestRunner.PileOrderInvariantMaskOverride,
            StateKeySalt = UnattendedTestRunner.StateKeySaltOverride,
            TranspositionPruningDisabledMask =
                UnattendedTestRunner.TranspositionPruningDisabledMaskOverride,
            TranspositionEntryLimit = UnattendedTestRunner.TranspositionEntryLimitOverride
                ?? SearchPolicySnapshot.DefaultTranspositionEntryLimit,
            MemoryNoProgressRecoveryLimit =
                UnattendedTestRunner.MemoryNoProgressRecoveryLimitOverride,
            PortfolioExperiment = PortfolioSelectorRuntime.Capture(),
            Act3BossStrategy = UnattendedTestRunner.Act3BossStrategyOverride != false
                && SearchPolicySnapshot.IsAct3BossEncounter(state.RunState.CurrentActIndex, state.Encounter?.Id.Entry),
            // 这里记的是玩家填的原始值；「不考虑局外收益」的折算交给快照上的 Effective* 一处做，
            // 免得两边各判一次而走岔。问题包里两样都在，方便看出当时是填了额度还是开了开关。
            GrowthBudgets = settings.GrowthBudgets,
            RelicTargets = RelicCounterCatalog.Capture(state, settings.RelicStrategyEnabled, settings.RelicCounterRules),
            StopAtAcceptableBattleHpLoss = settings.StopAtAcceptableBattleHpLoss,
            BrightestFlameMaxHpLossLimit = settings.BrightestFlameMaxHpLossLimit,
            GrowthOpportunityTargets = GrowthOpportunityPolicy.Capture(state),
            IgnoreLongTermRewards = settings.IgnoreLongTermRewards,
        };
        CombatBugReportExporter.RecordSearchPolicy(state, policy);
        return policy;
    }

    public static void BeginCombat(ICombatState? state)
    {
        AssertMainThread();
        ResetCore("combat_starting");
        SolverAutoStatus.ResetCombat();
        _combat.FullAutoEnabled = !_solverDisabled
            && state is CombatState { Players.Count: 1 }
            && SolverSettings.Current.AutoPlay;
        SolverAutoStatus.Record(
            "combat_start",
            $"auto={_combat.FullAutoEnabled.ToString().ToLowerInvariant()} " +
            $"requested={SolverSettings.Current.AutoPlay.ToString().ToLowerInvariant()}");
        DeployedCardIdsForTesting.Clear();
        DeployedPotionIdsForTesting.Clear();
        LastDeployedActionStartedAtMillisecondsForTesting = 0;
        NativeChoiceRuntime.ResetTraceForTesting();
        LastTurnSetupResultForTesting = null;
        LastReusedTurnForTesting = null;
        LastReusedProjectedBattleHpLostForTesting = null;
        SearchGcPolicy.ResetCountersForTesting();
        BattleDamageTracker.Begin(state);
        CombatShowcaseCollector.BeginCombat();
        CombatBugReportExporter.BeginCombat(state);
        _combat.TheftPolicy = state is CombatState combat && TheftEncounterStrategy.IsApplicable(combat)
            ? SolverTheftPolicy.PreserveResources
            : null;
        Entry.Logger.Info(
            $"[CombatSolver/Test] THEFT_POLICY_INIT policy={_combat.TheftPolicy?.ToString() ?? "-"}");
    }

    private static void RecordReviewedWorldlines(SolverResult result)
    {
        if (result.WasRestoredFromCache)
            return;
        if (!_combat.ReviewedWorldlineResults.Add(result))
            return;
        long reviewed = result.TotalExpandedNodes;
        _combat.ReviewedWorldlinesTotal = checked(_combat.ReviewedWorldlinesTotal + reviewed);
    }

    internal static bool ActivateTurnSetupResult(
        NGame host,
        CombatState state,
        int lifecycleGeneration,
        SolverResult result)
    {
        AssertMainThread();
        Player? player = LocalContext.GetMe(state);
        if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
            || _solverDisabled
            || _combat.AutomaticSearchPaused
            || !CombatManager.Instance.IsInProgress
            || state.Players.Count != 1
            || state.CurrentSide != CombatSide.Player
            || player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || result.TurnSetupPlayState is not { } expected
            || ContinuationStamp.CaptureLive(state) != expected)
        {
            Entry.Logger.Info("[CombatSolver/Test] TURN_SETUP_RESULT_REJECT reason=live_state_changed");
            return false;
        }

        LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
        CancelSearch();
        RecordReviewedWorldlines(result);
        _combat.State = state;
        _combat.LatestResult = result;
        _combat.LatestStamp = stamp;
        _combat.ContinuationSource = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? null
            : result;
        if (!result.WasRestoredFromCache)
        {
            _combat.SearchesStarted++;
            _combat.ReplanCounts[ReplanCause.InitialSearch] = _combat.ReplanCounts.GetValueOrDefault(ReplanCause.InitialSearch) + 1;
        }
        else
            _combat.RoutesRestored++;
        if (UnattendedTestRunner.IsActive)
        {
            LastCompletedResultForTesting = result;
            LastTurnSetupResultForTesting = result;
        }
        BattleDamageTracker.RegisterPlan(state, result);
        CombatBugReportExporter.RecordCheckpoint(
            state,
            result.ResultScope switch
            {
                SolverResultScope.CurrentTurnAdoption => "turn_setup_current_turn_adopted",
                SolverResultScope.RouteAdoption => "turn_setup_route_adopted",
                _ => "turn_setup_result",
            },
            result,
            DescribeReplanAudit());
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_ACCEPTED turn={player.PlayerCombatState.TurnNumber} " +
            $"scope={result.ResultScope}");
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        if (_combat.FullAutoEnabled
            && result.ResultScope != SolverResultScope.CurrentTurnAdoption)
        {
            Task fullAutoTask = StartCombatDeferredOperation(
                token => StartFullAutoAfterTurnSetupAsync(host, state, result, token));
            if (UnattendedAsyncActivityTracker.IsRequestActive)
                fullAutoTask = UnattendedAsyncActivityTracker.Track(fullAutoTask);
            TaskHelper.RunSafely(fullAutoTask);
        }
        return true;
    }

    internal static void RecordTurnSetupResultPreview(SolverResult result)
    {
        AssertMainThread();
        RecordReviewedWorldlines(result);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_PREVIEW turn={result.StartTurnNumber} " +
            $"scope={result.ResultScope} native_choice_pending=true");
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
    }

    internal static void RecordTurnSetupContinuationPreview(
        CombatState state,
        int turn)
    {
        AssertMainThread();
        if (!ReferenceEquals(_combat.State, state)
            || _combat.ContinuationSource is null)
        {
            throw new InvalidOperationException("回合准备页面没有可显示的既有跨回合路线。");
        }
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_PREVIEW turn={turn} " +
            "source=continuation native_choice_pending=true");
    }

    internal static void StartDeploymentAfterTurnSetup(
        NGame host,
        CombatState state,
        SolverResult result)
    {
        Task deploymentTask = StartCombatDeferredOperation(
            token => StartDeploymentAfterTurnSetupAsync(host, state, result, token));
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deploymentTask = UnattendedAsyncActivityTracker.Track(deploymentTask);
        TaskHelper.RunSafely(deploymentTask);
    }

    internal static bool TryGetPlannedTurnSetupChoices(
        CombatState state,
        int turn,
        out IReadOnlyList<PlanCardChoice>? choices)
    {
        AssertMainThread();
        choices = null;
        if (FairObservation.Enabled
            || !ReferenceEquals(_combat.State, state)
            || _combat.ContinuationSource is not { } source
            || !source.Continuations.Any(item => item.StartTurnNumber == turn))
        {
            return false;
        }

        PlanAction? previousEndTurn = source.BestNode.Actions.FirstOrDefault(action =>
            action.Turn == turn - 1
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
        PlanCardChoice[] planned = previousEndTurn?.TurnStartChoices?
            .Where(choice => choice.Timing == PlanChoiceTiming.PlayerTurnStart)
            .ToArray() ?? [];
        if (planned.Length == 0)
            return false;
        choices = planned;
        return true;
    }

    internal static async Task ResumeAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        int lifecycleGeneration,
        int turn,
        bool deployWhenReady = false,
        bool waitForDeploymentDelay = false,
        CancellationToken token = default)
    {
        SolverCombatSession session = _combat;
        session.TurnSetupResumeState = state;
        bool searchRequested = false;
        try
        {
            token.ThrowIfCancellationRequested();
            long deadline = System.Environment.TickCount64 + 30_000;
            while (CombatManager.Instance.IsInProgress
                   && !CombatManager.Instance.IsOverOrEnding
                   && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                   && (LocalContext.GetMe(state)?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
                       || CombatManager.Instance.PlayerActionsDisabled
                       || _deployment != null))
            {
                if (System.Environment.TickCount64 >= deadline)
                    throw new TimeoutException("回合准备页面完成后 30 秒内没有进入可复用状态。");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
            }

            if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
                || _solverDisabled
                || !CombatManager.Instance.IsInProgress
                || CombatManager.Instance.IsOverOrEnding
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || LocalContext.GetMe(state)?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } playerState
                || playerState.TurnNumber != turn)
            {
                return;
            }

            if (waitForDeploymentDelay)
                await WaitForTurnStartDeploymentDelayAsync(host, turn, token);

            if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
                || _solverDisabled
                || !CombatManager.Instance.IsInProgress
                || CombatManager.Instance.IsOverOrEnding
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || LocalContext.GetMe(state)?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } resumedState
                || resumedState.TurnNumber != turn)
            {
                return;
            }

            SearchReason resumedReason = session.ManualSearchAfterTurnSetupRequested
                ? SearchReason.Manual
                : SearchReason.AutoTurnStart;
            session.ManualSearchAfterTurnSetupRequested = false;
            session.TurnSetupResumeState = null;
            if (resumedReason == SearchReason.AutoTurnStart && !ShouldAutomaticallySearchNextTurn)
                return;
            searchRequested = true;
            RequestSearch(host, state, resumedReason, deployWhenReady);
        }
        finally
        {
            if (ReferenceEquals(session.TurnSetupResumeState, state))
                session.TurnSetupResumeState = null;
            if (!searchRequested && ReferenceEquals(_combat, session))
                session.ManualSearchAfterTurnSetupRequested = false;
        }
    }

    internal static bool ManualSearchAfterTurnSetupRequested
        => _combat.ManualSearchAfterTurnSetupRequested;

    internal static void QueueManualSearchAfterTurnSetup()
    {
        AssertMainThread();
        _combat.ManualSearchAfterTurnSetupRequested = true;
    }

    private static async Task StartFullAutoAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        SolverResult result,
        CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + 30_000;
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsOverOrEnding
               && (_deployment != null || CombatManager.Instance.PlayerActionsDisabled))
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("回合准备完成后 30 秒内没有进入可部署状态。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
        }
        await WaitForTurnStartDeploymentDelayAsync(host, result.StartTurnNumber, token);
        token.ThrowIfCancellationRequested();
        if (_combat.FullAutoEnabled
            && ReferenceEquals(_combat.LatestResult, result)
            && IsSamePlayableTurn(state, result.StartTurnNumber))
        {
            StartFullAutoDeployment(host, state, result);
        }
    }

    private static async Task StartDeploymentAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        SolverResult result,
        CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + 30_000;
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsOverOrEnding
               && (_deployment != null || CombatManager.Instance.PlayerActionsDisabled))
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("回合准备完成后 30 秒内没有进入可部署状态。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
        }
        await WaitForTurnStartDeploymentDelayAsync(host, result.StartTurnNumber, token);
        token.ThrowIfCancellationRequested();
        if (!_combat.FullAutoEnabled
            && ReferenceEquals(_combat.LatestResult, result)
            && IsSamePlayableTurn(state, result.StartTurnNumber))
        {
            StartDeployment(host, state, result);
        }
    }

    public static void RequestSearch(NGame host, CombatState state, SearchReason reason, bool deployWhenReady = false)
    {
        AssertMainThread();
        if (reason == SearchReason.Manual)
            _combat.RouteFrozen = false;
        if (_combat.ShowcaseMode && reason != SearchReason.AutoTurnStart)
        {
            StopShowcaseRoute(host, "战斗状态与录像路线不一致，已停止执行。");
            return;
        }
        if (reason == SearchReason.AutoTurnStart && !ShouldAutomaticallySearchNextTurn)
            return;
        int? searchTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber;
        // Turn setup and TurnStarted can complete in either order. The first accepted
        // request owns this turn; a late automatic callback must keep its active plan.
        if (reason == SearchReason.AutoTurnStart
            && (ReferenceEquals(_combat.State, state)
                    && (_combat.LatestResult?.StartTurnNumber == searchTurn || _combat.LastSolverDeployedTurn == searchTurn)
                || _search is { } active && ReferenceEquals(active.State, state) && active.StartTurnNumber == searchTurn
                || _deployment is { } deploying && ReferenceEquals(deploying.State, state) && deploying.StartTurnNumber == searchTurn))
        {
            Entry.Logger.Info($"[CombatSolver/Test] AUTO_SEARCH_ALREADY_OWNED turn={searchTurn}");
            return;
        }
        _combat.StoppedSearch = null;
        SolverDispatcher.Ensure(host);
        if (_combat.DeployAfterTurnSetupTurn == searchTurn)
        {
            deployWhenReady = true;
            _combat.DeployAfterTurnSetupTurn = null;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_RESUME reason=turn_setup_completed");
        }
        else if (_combat.DeployAfterTurnSetupTurn != null)
        {
            _combat.DeployAfterTurnSetupTurn = null;
        }
        Task rootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
        if (!rootCaptureBarrier.IsCompleted)
        {
            DeferSearchUntilRootCaptureBarrier(
                host,
                state,
                reason,
                deployWhenReady,
                rootCaptureBarrier);
            return;
        }
        // A direct request can arrive after the barrier opened but before the deferred
        // callback reached the dispatcher. The newest request owns the search slot.
        CancelDeferredSearch();
        if (reason == SearchReason.Manual)
        {
            if (_combat.AutomaticSearchPaused)
                Entry.Logger.Info("[CombatSolver/Test] AUTOMATIC_SEARCH_RESUMED reason=manual_recalculate");
            _combat.AutomaticSearchPaused = false;
            if (PlayerTurnSetupCoordinator.TryRecalculatePendingChoice(host, state))
            {
                _combat.PendingCompleteProjectionBaseline = null;
                _combat.PendingManualProjectionBaseline = null;
                Entry.Logger.Info(
                    "[CombatSolver/Test] SEARCH_RESTARTED reason=manual_recalculate_pending_turn_setup");
                return;
            }
            bool queuedAfterTurnSetup = PlayerTurnSetupCoordinator.TryQueueManualRecalculation(state);
            if (!queuedAfterTurnSetup && ReferenceEquals(_combat.TurnSetupResumeState, state))
            {
                QueueManualSearchAfterTurnSetup();
                queuedAfterTurnSetup = true;
                Entry.Logger.Info(
                    "[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_QUEUED phase=resuming_play");
            }
            if (queuedAfterTurnSetup)
            {
                _combat.PendingCompleteProjectionBaseline = null;
                _combat.PendingManualProjectionBaseline = null;
                Entry.Logger.Info(
                    "[CombatSolver/Test] SEARCH_DEFERRED reason=manual_recalculate_after_turn_setup");
                return;
            }
        }
        else if (_combat.AutomaticSearchPaused)
        {
            SolverAutoStatus.Record(
                "skip",
                $"automatic_search_paused request={reason} turn={searchTurn?.ToString() ?? "-"}");
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REJECT reason=automatic_search_paused request={reason}");
            SolverOverlay.RefreshControls();
            return;
        }
        // Queue completion includes post-action victory checks; a paused choice also keeps
        // its action completion pending even when the queue temporarily has no ready work.
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        Task nativeActionBarrier = actionExecutor.CurrentlyRunningAction is { } runningAction
            ? Task.WhenAll(actionExecutor.FinishedExecutingActions(), runningAction.CompletionTask)
            : actionExecutor.FinishedExecutingActions();
        if (!nativeActionBarrier.IsCompleted)
        {
            DeferSearchUntilRootCaptureBarrier(host, state, reason, deployWhenReady, nativeActionBarrier);
            return;
        }
        nativeActionBarrier.GetAwaiter().GetResult();
        ReplanCause replanCause = reason switch
        {
            SearchReason.AutoTurnStart => ReplanCause.InitialSearch,
            SearchReason.DeploymentDrift => ReplanCause.DeploymentDrift,
            SearchReason.PlanExhausted => ReplanCause.PlanExhausted,
            SearchReason.InformationRevealed => ReplanCause.InformationRevealed,
            _ => ReplanCause.ExplicitRequest,
        };
        SearchBoundaryReason? previousBoundary = _combat.ContinuationSource?.BoundaryReason;
        if (reason != SearchReason.AutoTurnStart)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
        }
        if (!CanSolve(state, out string rejection))
        {
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REJECT reason={rejection}");
            return;
        }
        CombatShowcaseCollector.TryCaptureInitialRoot(state, reason);
        CombatBugReportExporter.RecordCheckpoint(
            state,
            $"search_request_{reason}",
            CurrentResultForBugReport,
            DescribeReplanAudit());

        string setupStage = "battle_damage";
        try
        {
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(state);
            setupStage = "live_stamp";
            LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
            if (reason is not (SearchReason.AutoTurnStart or SearchReason.InformationRevealed)
                && _combat.LatestResult is { } previousResult
                && _combat.LatestStamp is { } previousStamp
                && previousStamp != stamp)
            {
                replanCause = ReplanCause.ManualDivergence;
                _combat.PendingManualProjectionBaseline = new ManualProjectionBaseline(
                    previousResult.StartTurnNumber,
                    previousResult.ProjectedBattleHpLost,
                    previousResult.ProjectedBattlePotionCount,
                    "field=live_combat_stamp expected={solver_result} actual={manual_state_change}",
                    CombatBugReportExporter.LastCompletedSearchRootId);
            }
            if (_combat.ShowcaseMode)
            {
                StopShowcaseRoute(host, "新回合状态与预计算续接点不一致。");
                return;
            }
            _combat.ContinuationSource = null;
            CancelSearch();
            SolverSearchSession search = new(
                ++_nextSearchGeneration,
                state,
                stamp,
                deployWhenReady)
            {
                ReplanCause = replanCause,
                StartTurnNumber = searchTurn!.Value,
            };
            _search = search;
            CancellationToken token = search.Cancellation.Token;
            int generation = search.Generation;
            _combat.SearchesStarted++;
            _combat.ReplanCounts[replanCause] = _combat.ReplanCounts.GetValueOrDefault(replanCause) + 1;
            if (replanCause == ReplanCause.ManualDivergence)
                MarkManualControlObserved("continuation_divergence");
            setupStage = "display_names";
            SolverDisplayNames displayNames = SolverDisplayNames.Capture(state);
            setupStage = "settings";
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverTheftPolicy? theftPolicy = ResolveTheftPolicy(state);
            SearchPolicySnapshot searchPolicy = CaptureSearchPolicy(
                settings,
                state,
                includeTurnSetup: false,
                theftPolicy: theftPolicy,
                interaction: search.Interaction);
            search.MaxDegreeOfParallelism = searchPolicy.MaxDegreeOfParallelism;
            search.MemoryPressureSignal = searchPolicy.MemoryPressureSignal;
            setupStage = "combat_root_snapshot";
            long rootCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            CombatRootSnapshot rootSnapshot;
            ulong fairSeed = FairDecisionPlanner.VisibleSeed(state, LocalContext.GetMe(state)!);
            try
            {
                rootSnapshot = CombatRootSnapshot.Capture(state, settings.PredictPotionReward,
                    FairObservation.FromSeed(fairSeed));
            }
            finally
            {
                SearchGcPolicy.ReportCombatLifecycleAllocation(
                    Math.Max(
                        0,
                        GC.GetTotalAllocatedBytes(precise: false) - rootCaptureAllocatedAtStart),
                    "combat_root_snapshot",
                    settings.EnableNoGcRegion);
            }
            bool fairForcedEndTurn = FairDecisionPlanner.IsForcedEndTurn(LocalContext.GetMe(state)!);
            CombatRootSnapshot[] fairSamples = [rootSnapshot, .. Enumerable.Range(1,
                    fairForcedEndTurn ? 0 : FairPlannerSettings.For(settings.Speed).Samples - 1)
                .Select(index => CombatRootSnapshot.Capture(state, settings.PredictPotionReward,
                    FairObservation.FromSeed(fairSeed + (ulong)index)))];
            searchPolicy = searchPolicy with
            {
                PrimaryIncumbents = _combat.AcquirePrimaryIncumbents(
                    rootSnapshot, searchPolicy, battleDamage),
            };
            Entry.Logger.Info(
                $"[CombatSolver/Test] COMBAT_ROOT_CAPTURE generation={generation} " +
                $"elapsed_ms={rootSnapshot.CaptureElapsedMilliseconds:F3} " +
                $"cards={rootSnapshot.CapturedCardCount} powers={rootSnapshot.CapturedPowerCount} " +
                $"listeners={rootSnapshot.CapturedHookListenerCount} " +
                $"run_mod_subscribers={rootSnapshot.CapturedRunModSubscriberCount} " +
                $"combat_mod_subscribers={rootSnapshot.CapturedCombatModSubscriberCount} " +
                $"base_lib_card_modifiers={rootSnapshot.CapturedBaseLibCardModifiers} " +
                $"strategic_hp_recovery_bound={(rootSnapshot.HasOnlyPostCombatHealing ? "certified" : "bypassed")} " +
                $"known_native_healing_policy={rootSnapshot.UsesKnownNativeHealingPolicy} " +
                $"strategic_hp_recovery_bound_reason={rootSnapshot.HealingBoundCertificationReason} " +
                $"strategic_hp_recovery_bound_source={Uri.EscapeDataString(rootSnapshot.HealingBoundCertificationSourceId ?? "-")} " +
                $"strategic_hp_recovery_bound_postcombat_heal_hp={(rootSnapshot.HasOnlyPostCombatHealing ? Math.Max(0, rootSnapshot.PostCombatRelicHeal.UnconditionalHeal).ToString() : "unbounded")} " +
                $"potion_reward={rootSnapshot.PotionRewardOutlook.Forecast}" +
                $"/{rootSnapshot.PotionRewardOutlook.ForecastPotionId ?? "-"}" +
                $"/credit={rootSnapshot.PotionRewardOutlook.ReplacementHpCredit}");
            search.PotionRewardOutlook = rootSnapshot.PotionRewardOutlook;
            _combat.State = state;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            LastCompletedResultForTesting = null;
            LastSearchFailureForTesting = null;
            LastFullAutoStoppedForWorseRecalculationForTesting = false;
            LastFullAutoStoppedAtLiveRiskForTesting = false;

            Player player = LocalContext.GetMe(state)!;
            int turn = player.PlayerCombatState!.TurnNumber;
            RunStatistics.Activity(state);
            SolverOverlay.RefreshControls();
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_REQUEST generation={generation} reason={reason} " +
                $"cause={CauseToken(replanCause)} previous_boundary={previousBoundary?.ToString() ?? "-"} " +
                $"turn={turn} deploy_when_ready={deployWhenReady} " +
                $"theft_policy={theftPolicy?.ToString() ?? "-"} " +
                $"act_transition_boss_hp_strategy={searchPolicy.ActTransitionBossHpStrategy} " +
                $"final_boss_hp_strategy={searchPolicy.FinalBossHpStrategy} " +
                $"frame_baseline_samples={FramePressureSignal.BaselineSampleCount} " +
                $"frame_baseline_ms={FramePressureSignal.BaselineFrameGapMilliseconds:F1} " +
                $"frame_pressure_threshold_ms={FramePressureSignal.PressureFrameGapMilliseconds:F1} " +
                $"frame_recovery_enabled={FramePressureSignal.RecoveryEnabled} " +
                $"no_gc_enabled={settings.EnableNoGcRegion.ToString().ToLowerInvariant()} " +
                $"no_gc_budget_bytes={settings.NoGcRegionBudgetBytes} " +
                $"max_dop={searchPolicy.MaxDegreeOfParallelism}");
            Entry.Logger.Info(SolverDiagnostics.DescribeStart(
                state,
                settings.Profile));

            setupStage = "worker_schedule";
            Task<SolverResult> solveTask = Task.Run(() =>
            {
                Entry.Logger.Info($"[CombatSolver/Test] SEARCH_WORKER_START generation={generation} thread={System.Environment.CurrentManagedThreadId} main_thread={NGame.IsMainThread()}");
                Thread worker = Thread.CurrentThread;
                ThreadPriority previousPriority = worker.Priority;
                worker.Priority = ThreadPriority.BelowNormal;
                ISearchGcScope? gcPolicy = null;
                SolverResult? finalizedResult = null;
                try
                {
                    using ISearchGcScope admittedGcPolicy = gcPolicy =
                        FairDecisionPlanner.EnterGcScope(settings.NoGcRegionBudgetBytes, token);
                    SolverResult result = SolveFairDecision(
                        fairSamples, fairForcedEndTurn, settings.Speed, displayNames, battleDamage, searchPolicy, generation, token);
                    finalizedResult = search.Interaction.FinalizeWorkerResult(result);
                    token.ThrowIfCancellationRequested();
                    return finalizedResult;
                }
                finally
                {
                    worker.Priority = previousPriority;
                    if (gcPolicy?.IsLifecycleCompleted == true)
                    {
                        SearchGcLifecycleSnapshot gcLifecycle = gcPolicy.Lifecycle;
                        if (finalizedResult != null)
                        {
                            finalizedResult.GcLifecycle = gcLifecycle;
                            finalizedResult.GcLifecycleAttribution = gcPolicy.LifecycleAttribution;
                        }
                        Entry.Logger.Info(
                            $"[CombatSolver/Test] SEARCH_GC_LIFECYCLE generation={generation} " +
                            $"completed={(finalizedResult != null).ToString().ToLowerInvariant()} " +
                            $"attribution={gcPolicy.LifecycleAttribution} " +
                            gcLifecycle.ToDiagnosticString());
                    }
                }
            }, token);
            search.WorkerCompletion = solveTask;
            if (!UnattendedAsyncActivityTracker.IsRequestActive)
            {
                // Preserve the production continuation path exactly. The extra lifecycle
                // ownership is needed only while a reusable unattended request is active.
                solveTask.ContinueWith(task =>
                {
                    SolverDispatcher.Post(() => CompleteSearch(host, search, task));
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                search.CallbackScheduled = true;
                return;
            }

            IDisposable? unattendedActivity = UnattendedAsyncActivityTracker.BeginActivity();
            solveTask.ContinueWith(task =>
            {
                try
                {
                    SolverDispatcher.Post(() =>
                    {
                        try
                        {
                            CompleteSearch(host, search, task);
                        }
                        finally
                        {
                            unattendedActivity?.Dispose();
                        }
                    });
                }
                catch
                {
                    unattendedActivity?.Dispose();
                    throw;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            search.CallbackScheduled = true;
        }
        catch (Exception ex)
        {
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchSetupFailure, ex);
            CancelSearch();
            _combat.State = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            _combat.ContinuationSource = null;
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            SolverOverlay.RefreshControls();
            Entry.Logger.Error(
                $"[CombatSolver/Test] SEARCH_SETUP_FAILURE stage={setupStage} " +
                $"reason={reason} exception={ex}");
        }
    }

    public static void RequestDeploy(NGame host, CombatState state)
    {
        AssertMainThread();
        SolverDispatcher.Ensure(host);
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_REJECT reason=already_deploying");
            return;
        }
        if (_combat.RouteFrozen && !CanExecuteCurrentTurn)
        {
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_REJECT reason=frozen_route_outdated");
            return;
        }
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        if (PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                host,
                state,
                deployAfterSetup: true))
        {
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_WAIT reason=turn_setup_choice");
            return;
        }
        Player? turnStartPlayer = LocalContext.GetMe(state);
        if (state.CurrentSide == CombatSide.Player
            && turnStartPlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Start)
        {
            _combat.DeployAfterTurnSetupTurn = turnStartPlayer.PlayerCombatState.TurnNumber;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_WAIT reason=turn_setup_pending");
            return;
        }
        if (!CanSolve(state, out string rejection))
        {
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_REJECT reason={rejection}");
            return;
        }

        LiveCombatStamp current = LiveCombatStamp.Capture(state);
        if (_combat.LatestResult != null && _combat.LatestStamp == current)
        {
            StartDeployment(host, state, _combat.LatestResult);
            return;
        }

        if (_search is { } search
            && ReferenceEquals(search.State, state)
            && search.Stamp == current)
        {
            search.DeployWhenReady = true;
            SolverOverlay.RefreshControls();
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_WAIT generation={search.Generation}");
            return;
        }

        if ((_combat.LatestResult != null || _search != null) && _combat.LatestStamp != current)
            MarkManualControlObserved("deploy_after_live_state_change");

        RequestSearch(host, state, SearchReason.Deploy, deployWhenReady: true);
    }

    public static void SetFullAuto(NGame host, CombatState state, bool enabled)
    {
        AssertMainThread();
        SolverDispatcher.Ensure(host);
        if (!enabled)
        {
            _combat.FullAutoEnabled = false;
            CancelDeployment();
            SolverAutoStatus.Record("stop", "user_toggle_off");
            Entry.Logger.Info("[CombatSolver/Test] FULL_AUTO enabled=false reason=user");
            SolverOverlay.RefreshControls();
            return;
        }

        _combat.RouteFrozen = false;

        if (_combat.AutomaticSearchPaused)
        {
            _combat.AutomaticSearchPaused = false;
            _combat.AutomaticSearchPausedTurn = null;
            Entry.Logger.Info("[CombatSolver/Test] AUTOMATIC_SEARCH_RESUMED reason=explicit_full_auto");
        }

        if (PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state))
        {
            _combat.FullAutoEnabled = true;
            Entry.Logger.Info(
                $"[CombatSolver/Test] FULL_AUTO enabled=true reason=turn_setup_takeover " +
                $"stop_on_combat_end={_stopFullAutoOnCombatEnd} " +
                $"stop_on_death_turn={_stopFullAutoOnDeathTurn} " +
                $"stop_on_worse_recalculation={_stopFullAutoOnWorseRecalculation}");
            SolverOverlay.RefreshControls();
            if (!PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                    host,
                    state,
                    deployAfterSetup: false))
            {
                InvalidOperationException failure = new("回合开始选牌页在全自动接管时失去活动状态。");
                _ = RecordTurnSetupFailure(
                    state,
                    Volatile.Read(ref _combatLifecycleGeneration),
                    failure);
                throw failure;
            }
            return;
        }

        if (!CanSolve(state, out string rejection))
        {
            _combat.FullAutoEnabled = true;
            SolverAutoStatus.Record("skip", $"cannot_solve reason={rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_REJECT reason={rejection}");
            SolverOverlay.RefreshControls();
            return;
        }

        int currentTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0;
        if (currentTurn > 1 && _combat.LastSolverDeployedTurn != currentTurn - 1)
            MarkManualControlObserved("full_auto_after_manual_turn");

        _combat.FullAutoEnabled = true;
        Entry.Logger.Info(
            $"[CombatSolver/Test] FULL_AUTO enabled=true stop_on_combat_end={_stopFullAutoOnCombatEnd} " +
            $"stop_on_death_turn={_stopFullAutoOnDeathTurn} " +
            $"stop_on_worse_recalculation={_stopFullAutoOnWorseRecalculation}");
        SolverAutoStatus.Record("start", "user_toggle_on");
        SolverOverlay.RefreshControls();

        LiveCombatStamp current = LiveCombatStamp.Capture(state);
        if (_combat.LatestResult != null && _combat.LatestStamp == current)
        {
            StartFullAutoDeployment(host, state, _combat.LatestResult);
            return;
        }
        if (_search == null && _deployment == null)
            RequestSearch(host, state, SearchReason.FullAuto);
    }

    public static bool PrepareAutomaticSearchForTurn(NGame host, CombatState state)
    {
        AssertMainThread();
        if (CombatShowcaseRuntime.ImportInProgress)
            return false;
        if (_combat.ShowcaseMode)
            return !_combat.AutomaticSearchPaused;
        int turn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? -1;
        if (_combat.AutomaticSearchPaused
            && _combat.AutomaticSearchPausedTurn is { } stoppedTurn
            && stoppedTurn != turn)
        {
            _combat.AutomaticSearchPaused = false;
            _combat.AutomaticSearchPausedTurn = null;
            Entry.Logger.Info(
                $"[CombatSolver/Test] AUTOMATIC_SEARCH_STOP_CLEARED stopped_turn={stoppedTurn} current_turn={turn}");
        }
        if (!ShouldAutomaticallySearchNextTurn || _combat.AutomaticSearchPaused)
            return false;
        return true;
    }

    public static void SetSolverDisabled(bool disabled, bool persist = true)
    {
        AssertMainThread();
        _solverDisabled = disabled;
        RunStatistics.SettingsChanged();
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { SolverDisabled = disabled });

        Entry.Logger.Info($"[CombatSolver/Test] SOLVER_DISABLED disabled={disabled}");
        if (disabled)
        {
            PlayerTurnSetupCoordinator.CancelForSolverDisabled();
            CancelDeferredSearch();
            CancelSearch();
            CancelDeployment();
            _combat.FullAutoEnabled = false;
            SolverAutoStatus.Record("stop", "solver_disabled");
            _combat.State = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            _combat.ContinuationSource = null;
            _combat.PendingCompleteProjectionBaseline = null;
            if (NGame.Instance is { } host)
                SolverOverlay.ShowDisabled(host);
            else
                SolverOverlay.RefreshControls();
            return;
        }

        SolverOverlay.RefreshControls();
    }

    public static void StopSearchByUser(NGame host)
    {
        AssertMainThread();
        if (!IsSearching)
            return;

        bool controllerSearchCanceled = _search != null || _deferredSearchCts != null;
        int? generation = _search?.Generation;
        _combat.FullAutoEnabled = false;
        SolverAutoStatus.Record("stop", $"user_stop_search generation={generation?.ToString() ?? "-"}");
        _combat.AutomaticSearchPaused = true;
        _combat.AutomaticSearchPausedTurn = LocalContext.GetMe(
            CombatManager.Instance.DebugOnlyGetState())?.PlayerCombatState?.TurnNumber;
        CancelDeferredSearch();
        bool stoppingControllerAtCandidate = _search is { } search
            && search.Interaction.RenderedRouteAdoptionSeed is { } seed
            && search.Interaction.RequestAdoptRoute(seed, stopAfterResult: true);
        bool stoppingSetupAtCandidate = _search == null
            && PlayerTurnSetupCoordinator.TryStopSearchAtCurrentRoute();
        bool stoppingAtCandidate = stoppingControllerAtCandidate || stoppingSetupAtCandidate;
        if (!stoppingAtCandidate)
            CancelSearch();
        bool stoppedTurnSetupSearch = stoppingSetupAtCandidate
            || !stoppingAtCandidate && PlayerTurnSetupCoordinator.StopSearchByUser();
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_STOPPED_BY_USER generation={generation?.ToString() ?? "-"} " +
            $"turn_setup={stoppedTurnSetupSearch.ToString().ToLowerInvariant()} " +
            $"candidate_preserved={stoppingAtCandidate.ToString().ToLowerInvariant()} automatic_search_paused=true");
    }

    public static void ApplyCurrentTurn()
    {
        AssertMainThread();
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        _combat.FullAutoEnabled = false;
        SolverAutoStatus.Record("stop", "user_applied_current_turn");
        if (_search is not { } search)
        {
            PlayerTurnSetupCoordinator.ApplyCurrentTurn();
            return;
        }
        if (search.Interaction.CurrentTakeoverRequest != null
            || Volatile.Read(ref search.Interaction.Progress)?.CurrentTurnPreview == null)
        {
            return;
        }

        search.DeployWhenReady = true;
        if (!search.Interaction.RequestApplyCurrentTurn())
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=apply_current_turn");
    }

    public static void AdoptCurrentRoute()
    {
        AssertMainThread();
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        if (_search is not { } search)
        {
            if (TryAdoptStoppedRoute())
                return;
            PlayerTurnSetupCoordinator.AdoptCurrentRoute();
            return;
        }
        SolverRouteAdoptionSeed? seed = search.Interaction.RenderedRouteAdoptionSeed;
        if (seed == null || !search.Interaction.RequestAdoptRoute(seed))
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=adopt_current_route " +
            $"candidate_version={seed.CandidateVersion}");
    }

    public static void SetStopFullAutoOnCombatEnd(bool enabled)
    {
        AssertMainThread();
        _stopFullAutoOnCombatEnd = enabled;
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_COMBAT_END_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetStopFullAutoOnDeathTurn(bool enabled)
    {
        AssertMainThread();
        _stopFullAutoOnDeathTurn = enabled;
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_DEATH_TURN_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetStopFullAutoOnWorseRecalculation(bool enabled)
    {
        AssertMainThread();
        _stopFullAutoOnWorseRecalculation = enabled;
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_WORSE_RECALCULATION_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    internal static PotionStrategySnapshot CapturePotionStrategy(
        CombatState state,
        SolverPotionPolicy defaultPolicy)
    {
        AssertMainThread();
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        List<PotionSlotDirective> directives = [];
        foreach (PotionSlotDirective directive in _potionDirectivesForTesting)
        {
            PotionModel? potion = GetPotionAtExistingSlot(player, directive.Slot);
            if (potion != null
                && string.Equals(potion.Id.Entry, directive.PotionId, StringComparison.Ordinal))
                directives.Add(directive);
        }
        return new PotionStrategySnapshot(defaultPolicy, directives);
    }

    internal static void SetPotionDirectiveForTesting(
        CombatState state,
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("药水策略测试覆盖只能在无人测试中使用。");
        PotionModel potion = LocalContext.GetMe(state)?.GetPotionAtSlotIndex(slot)
            ?? throw new InvalidOperationException($"测试药水槽位 {slot} 为空。");
        if (!string.Equals(potion.Id.Entry, potionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"测试药水槽位 {slot} 与 {potionId} 不一致。");
        ApplyPotionDirectiveForTesting(new PotionSlotDirective(slot, potionId, directive));
    }

    internal static void ApplyPotionDirectiveForTesting(PotionSlotDirective directive)
    {
        _potionDirectivesForTesting.RemoveAll(item => item.Slot == directive.Slot);
        if (directive.Directive != SolverPotionDirective.Smart)
            _potionDirectivesForTesting.Add(directive);
    }

    internal static SolverPotionDirective ResolvePotionDirectiveForTesting(int slot, string potionId)
        => _potionDirectivesForTesting.FirstOrDefault(item => item.Slot == slot
            && string.Equals(item.PotionId, potionId, StringComparison.Ordinal)) is { PotionId: not null } found
            ? found.Directive
            : SolverPotionDirective.Smart;

    internal static void ClearPotionDirectivesForTesting() => _potionDirectivesForTesting.Clear();

    private static PotionModel? GetPotionAtExistingSlot(Player player, int slot)
        => slot >= 0 && slot < player.PotionSlots.Count
            ? player.PotionSlots[slot]
            : null;

    internal static SolverTheftPolicy? ResolveTheftPolicy(CombatState state)
        => TheftEncounterStrategy.IsApplicable(state)
            ? _combat.TheftPolicy ?? SolverTheftPolicy.PreserveResources
            : null;

    internal static void SetTheftPolicyForTesting(CombatState state, SolverTheftPolicy policy)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("偷窃策略测试覆盖只能在无人测试中使用。");
        if (!TheftEncounterStrategy.IsApplicable(state))
            throw new InvalidOperationException("测试战斗不是偷窃资源战斗。");
        _combat.TheftPolicy = policy;
        Entry.Logger.Info($"[CombatSolver/Test] THEFT_POLICY_TEST_OVERRIDE policy={policy}");
        SolverOverlay.RefreshControls();
    }

    public static void Reset(string reason = "unspecified")
    {
        AssertMainThread();
        ResetCore(reason);
    }

    private static void ResetCore(string reason)
    {
        int lifecycleGeneration = Interlocked.Increment(ref _combatLifecycleGeneration);
        CancelDeferredSearch();
        Task deferredSearchRelease = DrainDeferredSearchReleases();
        Task combatDeferredRelease = CancelAndDrainCombatDeferredOperations();
        Task turnSetupRelease = PlayerTurnSetupCoordinator.Reset(reason);
        long forensicCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        Task forensicCaptureRelease = CombatBugReportExporter.CompleteCombat(
            reason,
            CurrentResultForBugReport,
            DescribeReplanAudit());
        bool automaticGcLifecycleUsed = SearchGcPolicy.AutomaticGcLifecycleUsed;
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            Math.Max(
                0,
                GC.GetTotalAllocatedBytes(precise: false) - forensicCaptureAllocatedAtStart),
            "combat_forensic_capture",
            automaticGcLifecycleUsed);
        SearchGcPolicy.CombatLifecyclePressure lifecyclePressure =
            SearchGcPolicy.DetachCombatLifecyclePressure(reason);
        bool hadState = _search != null
            || _deployment != null
            || _combat.State != null
            || _combat.LatestResult != null
            || SolverOverlay.IsVisible;
        if (_combat.SearchesStarted > 0 || _combat.ContinuationsReused > 0)
            Entry.Logger.Info($"[CombatSolver/Test] REPLAN_SUMMARY reason={reason} {DescribeReplanCounts()}");
        _lastBugReportClassification = CaptureBugReportClassification();
        _lastManualProjectionComparison = _combat.LastManualProjectionComparison;
        CancelSearch();
        Task searchReferenceRelease = DrainSearchReferenceReleases();
        CancelDeployment();
        Task deploymentReferenceRelease = DrainDeploymentReferenceReleases();
        _combat = new SolverCombatSession();
        LastFullAutoStoppedForWorseRecalculationForTesting = false;
        LastFullAutoStoppedAtLiveRiskForTesting = false;
        LastSearchFailureForTesting = null;
        BattleDamageTracker.Reset();
        SolverOverlay.RefreshControls();
        bool unattendedRequestActive = UnattendedAsyncActivityTracker.IsRequestActive;
        Task regionExit = unattendedRequestActive
            ? Task.CompletedTask
            : SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(reason);
        // The unattended protocol performs one reclaim only after the game and every tracked
        // callback are quiescent. Starting a fire-and-forget reclaim here would make the 0.18
        // serialized reclaim chain request a second collection when the protocol joins it.
        Task referenceRelease = Task.WhenAll(
            searchReferenceRelease,
            deploymentReferenceRelease,
            deferredSearchRelease,
            combatDeferredRelease,
            turnSetupRelease,
            forensicCaptureRelease,
            regionExit);
        LastCombatReferenceReleaseForTesting = referenceRelease;
        if (unattendedRequestActive)
        {
            TaskHelper.RunSafely(UnattendedAsyncActivityTracker.Track(referenceRelease));
        }
        else if (automaticGcLifecycleUsed
                 && (hadState
                     || !referenceRelease.IsCompletedSuccessfully
                     || lifecyclePressure.AllocatedBytes > 0))
        {
            Stopwatch referenceReleaseStopwatch = Stopwatch.StartNew();
            TaskHelper.RunSafely(SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                reason,
                lifecyclePressure.RequiresCollection,
                includeCombatLifecyclePressure: false,
                referenceRelease,
                () => LogCombatReferenceBarrier(
                    reason,
                    lifecycleGeneration,
                    lifecyclePressure,
                    referenceReleaseStopwatch)));
        }
        if (hadState)
            Entry.Logger.Info($"[CombatSolver/Test] RESET reason={reason}");
    }

    private static async Task ReleaseSearchSessionAsync(SolverSearchSession? search)
    {
        if (search == null)
            return;
        try
        {
            await search.WorkerCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await search.CallbackCompletion.Task.ConfigureAwait(false);
            search.WorkerCompletion = Task.CompletedTask;
        }
        finally
        {
            DisposeSearchCancellationOnce(search);
            Interlocked.Increment(ref _searchReferenceReleaseCompletedCountForTesting);
        }
    }

    private static void QueueSearchReferenceRelease(SolverSearchSession search)
    {
        if (Interlocked.Exchange(ref search.ReferenceReleaseState, 1) != 0)
            return;
        PendingSearchReferenceReleases.RemoveAll(static task => task.IsCompleted);
        Interlocked.Increment(ref _searchReferenceReleaseScheduledCountForTesting);
        PendingSearchReferenceReleases.Add(ReleaseSearchSessionAsync(search));
    }

    private static Task DrainSearchReferenceReleases()
    {
        if (PendingSearchReferenceReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = [.. PendingSearchReferenceReleases];
        PendingSearchReferenceReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void DisposeSearchCancellationOnce(SolverSearchSession search)
    {
        if (Interlocked.Exchange(ref search.CancellationDisposeState, 1) != 0)
            return;
        search.Cancellation.Dispose();
        Interlocked.Increment(ref _searchCtsDisposeCountForTesting);
    }

    private static void LogCombatReferenceBarrier(
        string reason,
        int lifecycleGeneration,
        SearchGcPolicy.CombatLifecyclePressure lifecyclePressure,
        Stopwatch stopwatch)
    {
        int currentLifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        Entry.Logger.Info(
            $"[CombatSolver/Test] HEAP_REFERENCE_BARRIER reason={reason} " +
            $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
            $"lifecycle_epoch={lifecycleGeneration} " +
            $"current_lifecycle_epoch={currentLifecycleGeneration} " +
            $"crossed_combat={(currentLifecycleGeneration != lifecycleGeneration).ToString().ToLowerInvariant()} " +
            $"lifecycle_allocated_bytes={lifecyclePressure.AllocatedBytes} " +
            $"lifecycle_requires_gen2={lifecyclePressure.RequiresCollection.ToString().ToLowerInvariant()} " +
            $"managed_live_bytes={GC.GetTotalMemory(forceFullCollection: false)}");
    }

    internal static void SimulateDeploymentCompletionForTesting(NGame host, CombatState state)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive
            || !ReferenceEquals(_combat.State, state)
            || _combat.LatestResult is not { } result
            || _combat.LatestStamp != LiveCombatStamp.Capture(state))
        {
            throw new InvalidOperationException("无人测试无法模拟消费当前就绪路线。");
        }
        int actionCount = result.BestNode.Actions.Count(action =>
            action.Turn == result.StartTurnNumber && action.IsExecutable);
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        SolverOverlay.RefreshControls();
    }

    internal static void ReleaseUnattendedResultReferencesForTesting()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "无人测试结果引用只能在活动请求的最终清理阶段释放。");
        }
        // Executor assertions intentionally inspect the final result after combat teardown.
        // Release it once those assertions are finished and before the protocol's reuse Gen2;
        // IntentForecast can otherwise retain live creatures and move states from the old fight.
        LastCompletedResultForTesting = null;
        LastTurnSetupResultForTesting = null;
        LastSearchFailureForTesting = null;
        LastTurnSetupFailureForTesting = null;
    }

    public static void MonitorCombatPresence()
    {
        AssertMainThread();
        CombatState? current = CombatManager.Instance.DebugOnlyGetState();
        if (!CombatManager.Instance.IsInProgress || current == null)
        {
            if (_combat.State != null || SolverOverlay.IsVisible)
                Reset("combat_inactive");
            return;
        }

        if (_combat.State != null && !ReferenceEquals(current, _combat.State))
        {
            BeginCombat(current);
        }
        BattleDamageTracker.Observe(current);
        // SL may replace the combat after TurnStarted. Reattach at the playable boundary.
        if (_combat.State == null && !IsSearching && !IsDeploying
            && !PendingCombatDeferredOperations.Any(task => !task.IsCompleted)
            && !PlayerTurnSetupCoordinator.IsManaging(current)
            && current.Players.Count == 1
            && LocalContext.GetMe(current)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && NGame.Instance is { } host)
        {
            _combat.State = current;
            if (_solverDisabled)
                SolverOverlay.ShowDisabled(host);
            else if (!_combat.AutomaticSearchPaused
                     && ShouldAutomaticallySearchNextTurn
                     && UnattendedTestRunner.AutomaticTurnSearchEnabled
                     && CanSolve(current, out _))
                RequestSearch(host, current, SearchReason.AutoTurnStart);
            else
                SolverOverlay.RefreshControls();
        }
    }

    public static void ObserveMainThreadFrameGap(TimeSpan gap)
    {
        AssertMainThread();
        double milliseconds = gap.TotalMilliseconds;
        SolverSearchSession? search = _search;
        bool frameRecoveryAllowed = !string.Equals(
                DisplayServer.GetName(),
                "headless",
                StringComparison.OrdinalIgnoreCase)
            && DisplayServer.WindowIsFocused();
        FramePressureSignal.ObserveFrame(
            milliseconds,
            searchActive: IsSearching,
            frameRecoveryAllowed);
        if (search == null)
            return;
        search.ObserveFrame(milliseconds);
        if (milliseconds >= 100d)
        {
            SolverProgress? progress = Volatile.Read(ref search.Interaction.Progress);
            Entry.Logger.Info(
                $"[CombatSolver/Test] MAIN_THREAD_LONG_FRAME gap_ms={milliseconds:F1} " +
                $"frame={search.FrameCount} expanded={progress?.ExpandedNodes ?? -1} " +
                $"process_allocated_delta={GC.GetTotalAllocatedBytes(precise: false) - search.ProcessAllocatedBytesAtStart} " +
                $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - search.ProcessGcPauseAtStart).TotalMilliseconds:F1}");
        }
    }
    internal static SolverProgress? CaptureDevelopmentMonitorProgress()
    {
        SolverSearchSession? search = Volatile.Read(ref _search);
        return search == null ? null : Volatile.Read(ref search.Interaction.Progress);
    }

    private static string DescribeReplanAudit()
    {
        string differences = _combat.LastContinuationDifferences.Count == 0
            ? "-"
            : string.Join(System.Environment.NewLine, _combat.LastContinuationDifferences);
        string manualComparison = _combat.LastManualProjectionComparison is { } comparison
            ? $"previous={comparison.PreviousProjectedBattleHpLost} current={comparison.CurrentProjectedBattleHpLost} " +
              $"difference={comparison.Difference} original_turn={comparison.OriginalTurnNumber} " +
              $"current_turn={comparison.CurrentTurnNumber}"
            : "-";
        return DescribeReplanCounts() +
               System.Environment.NewLine + "last_state_differences=" + differences +
               System.Environment.NewLine + "last_manual_projection_comparison=" + manualComparison;
    }

    private static string DescribeReplanCounts()
    {
        string counts = string.Join(' ', Enum.GetValues<ReplanCause>()
            .Select(cause => $"{CauseToken(cause)}={_combat.ReplanCounts.GetValueOrDefault(cause)}"));
        return $"searches={_combat.SearchesStarted} reused={_combat.ContinuationsReused} restored={_combat.RoutesRestored} {counts} " +
               $"control_mode={ControlModeForBugReport} " +
               $"last_solver_deployed_turn={_combat.LastSolverDeployedTurn?.ToString() ?? "-"}";
    }

    private static void MarkManualControlObserved(string reason)
    {
        if (_combat.ManualControlObserved)
            return;
        _combat.ManualControlObserved = true;
        Entry.Logger.Info($"[CombatSolver/Test] CONTROL_MODE_CHANGED mode=manual_plus_solver reason={reason}");
    }

    internal static void ObserveCombatInput(string origin)
    {
        _search?.ObserveInput(origin);
        if (_search is { PlayerInputObserved: true })
            MarkManualControlObserved("search_player_input");
    }

    internal static void RecordSearchResultStale(SolverSearchSession search)
        => _combat.BugReportIssues.Record(
            search.PlayerInputObserved
                ? CombatBugReportIssueKind.ManualSearchResultStale
                : CombatBugReportIssueKind.SearchResultStale,
            $"第 {search.StartTurnNumber} 回合");

    private static string CauseToken(ReplanCause cause)
        => cause switch
        {
            ReplanCause.InitialSearch => "initial_search",
            ReplanCause.StateMismatch => "state_mismatch",
            ReplanCause.ManualDivergence => "manual_divergence",
            ReplanCause.ContinuationMissing => "continuation_missing",
            ReplanCause.DeploymentDrift => "deployment_drift",
            ReplanCause.PlanExhausted => "plan_exhausted",
            ReplanCause.ExplicitRequest => "explicit_request",
            ReplanCause.InformationRevealed => "information_revealed",
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
        };

    private static SolverResult SolveFairDecision(
        IReadOnlyList<CombatRootSnapshot> samples,
        bool forcedEndTurn,
        SolverSpeed speed,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot searchPolicy,
        int generation,
        CancellationToken token)
    {
        FairPlannerSettings plannerSettings = FairPlannerSettings.For(speed) with { Samples = samples.Count };
        SearchPolicySnapshot isolated = FairDecisionPlanner.IsolatedPolicy(
            searchPolicy, plannerSettings.Profile(searchPolicy.Profile));
        if (forcedEndTurn)
            isolated = FairDecisionPlanner.ForcedEndTurnPolicy(isolated);
        Stopwatch stopwatch = Stopwatch.StartNew();
        long allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan gcPauseAtStart = GC.GetTotalPauseDuration();
        FairDecision? decision = FairDecisionPlanner.Decide(
            samples,
            (root, prefix) => FairDecisionPlanner.Solve(root, displayNames, battleDamage, isolated, prefix, token),
            (root, action) => FairDecisionPlanner.RevealedVariants(root, displayNames, battleDamage, isolated, action, token),
            (root, actions) => FairDecisionPlanner.RevealFreeLength(root, displayNames, battleDamage, isolated, actions, token),
            plannerSettings,
            token);
        SolverResult route = decision?.Route
            ?? FairDecisionPlanner.Solve(samples[0], displayNames, battleDamage, isolated, null, token);
        route.FairCommitActionLimit = decision?.CommitCount ?? 1;
        Entry.Logger.Info(
            $"[CombatSolver/Fair] MULTI_SAMPLE_DECISION generation={generation} speed={speed} samples={samples.Count} " +
            $"solves={decision?.Solves ?? 1} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F0} " +
            $"own_phase_ms={decision?.OwnPhaseMilliseconds ?? 0} slowest_solve_ms={decision?.SlowestSolveMilliseconds ?? 0} " +
            $"allocated_mb={(GC.GetTotalAllocatedBytes(precise: false) - allocatedAtStart) / (1024 * 1024)} " +
            $"gc_pause_ms={(GC.GetTotalPauseDuration() - gcPauseAtStart).TotalMilliseconds:F0} forced_end_turn={forcedEndTurn} " +
            $"commit_actions={route.FairCommitActionLimit} safety_stops={decision?.SafetyStops ?? 0} " +
            $"chosen={(decision == null ? "-" : FairDecisionPlanner.Key(decision.Action))} candidates=" +
            string.Join(' ', decision?.Candidates.Select(score =>
                $"[{score.Key} mean={score.MeanUtility:F1} death={score.DeathRate:F2} sd={score.StandardDeviation:F1} " +
                $"proposals={score.Proposals} valid={score.ValidSamples}]") ?? []));
        return route;
    }

    private static void CompleteSearch(
        NGame host,
        SolverSearchSession search,
        Task<SolverResult> task)
    {
        try
        {
            CompleteSearchCore(host, search, task);
        }
        finally
        {
            search.CallbackCompletion.TrySetResult();
            DisposeSearchCancellationOnce(search);
        }
    }

    private static void CompleteSearchCore(
        NGame host,
        SolverSearchSession search,
        Task<SolverResult> task)
    {
        AssertMainThread();
        if (!ReferenceEquals(_search, search))
            return;
        _search = null;
        Volatile.Write(ref search.Interaction.Progress, null);
        int generation = search.Generation;
        Entry.Logger.Info($"[CombatSolver/Test] SEARCH_CALLBACK generation={generation} thread={System.Environment.CurrentManagedThreadId} main_thread={NGame.IsMainThread()}");
        Entry.Logger.Info(
            $"[CombatSolver/Test] MAIN_THREAD_FRAMES generation={generation} frames={search.FrameCount} " +
            $"p95_gap_ms={search.FramePercentile(0.95d):F1} p99_gap_ms={search.FramePercentile(0.99d):F1} " +
            $"max_gap_ms={search.MaxFrameGapMilliseconds:F1} over_33ms={search.FramesOver33Milliseconds} " +
            $"over_50ms={search.FramesOver50Milliseconds} over_100ms={search.FramesOver100Milliseconds}");

        if (task.IsCanceled)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_CANCELED generation={generation}");
            return;
        }
        if (task.IsFaulted)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            Exception ex = task.Exception?.GetBaseException() ?? new InvalidOperationException("后台搜索失败但没有异常对象。");
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchFailure, ex);
            LastSearchFailureForTesting = ex;
            SolverAutoStatus.RecordError("search_failure", SolverAutoStatus.Describe(ex));
            SolverOverlay.RefreshControls();
            Entry.Logger.Error($"[CombatSolver/Test] SEARCH_FAILURE generation={generation} exception={ex}");
            return;
        }

        CombatState searchedState = search.State;
        LiveCombatStamp searchedStamp = search.Stamp;
        CombatState? currentState = CombatManager.Instance.DebugOnlyGetState();
        if (!ReferenceEquals(currentState, searchedState)
            || !CanSolve(searchedState, out _)
            || LiveCombatStamp.Capture(searchedState) != searchedStamp)
        {
            RecordSearchResultStale(search);
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            SolverOverlay.RefreshControls();
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_STALE generation={generation} player_input={search.PlayerInputObserved}");
            return;
        }

        SolverResult result = task.Result;
        if (result.WasRestoredFromCache)
        {
            _combat.SearchesStarted--;
            _combat.RoutesRestored++;
            _combat.ReplanCounts[search.ReplanCause]--;
            Entry.Logger.Info($"[CombatSolver/Test] ROUTE_CACHE_HIT turn={result.StartTurnNumber} validation=exact_root");
        }
        bool stopped = search.Interaction.StopRequested;
        bool currentTurnAdopted = result.ResultScope == SolverResultScope.CurrentTurnAdoption;
        bool routeAdopted = result.ResultScope == SolverResultScope.RouteAdoption;
        if (stopped || currentTurnAdopted)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
        }
        else
        {
            ApplyProjectionBaselines(result);
        }
        ApplySearchFrameMetrics(result, search);
        RecordReviewedWorldlines(result);

        if (stopped)
        {
            result.ResultScope = SolverResultScope.RouteAdoption;
            search.Interaction.PreserveStoppedResult(result, searchedStamp);
            _combat.StoppedSearch = search.Interaction;
            SolverOverlay.RefreshControls();
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_STOPPED_RESULT_PRESERVED generation={generation} " +
                $"actions={result.BestNode.Actions.Count}");
            return;
        }

        _combat.LatestResult = result;
        _combat.LatestStamp = searchedStamp;
        _combat.ContinuationSource = currentTurnAdopted ? null : result;
        if (UnattendedTestRunner.IsActive)
            LastCompletedResultForTesting = result;
        BattleDamageTracker.RegisterPlan(searchedState, result);
        CombatBugReportExporter.RecordCheckpoint(
            searchedState,
            currentTurnAdopted
                ? "search_current_turn_adopted"
                : routeAdopted
                    ? "search_route_adopted"
                    : "search_completed",
            result,
            DescribeReplanAudit());
        SolverOverlay.RefreshControls();
        if (currentTurnAdopted)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_CURRENT_TURN_ADOPTED generation={generation} " +
                $"turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count}");
        }
        else if (routeAdopted)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_ROUTE_ADOPTED generation={generation} " +
                $"actions={result.BestNode.Actions.Count} continuations={result.Continuations.Count}");
        }
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        if (currentTurnAdopted || search.DeployWhenReady)
            StartDeployment(host, searchedState, result);
        else if (_combat.FullAutoEnabled)
            StartFullAutoDeployment(host, searchedState, result);
        // 录像打包放在路线显示和自动执行启动之后，打包慢或失败都不耽误路线。
        if (!currentTurnAdopted && !routeAdopted)
            CombatShowcaseCollector.TryQueueCompletedRoute(searchedState, result);
    }

    private static void ApplyProjectionBaselines(SolverResult result)
    {
        CompleteProjectionBaseline? recalculationBaseline = _combat.PendingCompleteProjectionBaseline;
        ManualProjectionBaseline? manualBaseline = _combat.PendingManualProjectionBaseline;
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        if (recalculationBaseline != null
            && result.ProjectedBattleHpLost > recalculationBaseline.ProjectedBattleHpLost)
        {
            result.RecalculatedAfterCompleteProjection = true;
            result.PreviousProjectedBattleHpLost = recalculationBaseline.ProjectedBattleHpLost;
            result.RecalculationStateDifference = recalculationBaseline.StateDifference;
            Entry.Logger.Warn(
                $"[CombatSolver/Test] COMPLETE_ROUTE_RECALCULATION_WORSENED " +
                $"original_turn={recalculationBaseline.StartTurnNumber} current_turn={result.StartTurnNumber} " +
                $"previous_projected_battle_hp_lost={recalculationBaseline.ProjectedBattleHpLost} " +
                $"current_projected_battle_hp_lost={result.ProjectedBattleHpLost} " +
                $"increase={result.ProjectedBattleHpLossIncrease} {recalculationBaseline.StateDifference}");
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.RecalculationHpLossIncreased,
                $"预计战损 {recalculationBaseline.ProjectedBattleHpLost} → {result.ProjectedBattleHpLost}，" +
                $"增加 {result.ProjectedBattleHpLossIncrease} HP");
        }
        if (manualBaseline != null)
        {
            RecordManualProjectionComparison(
                manualBaseline,
                result.StartTurnNumber,
                result.ProjectedBattleHpLost,
                result.ProjectedBattlePotionCount);
        }
    }

    private static void ApplySearchFrameMetrics(SolverResult result, SolverSearchSession search)
    {
        result.MainThreadFrameCount = search.FrameCount;
        result.MainThreadFramesOver33Milliseconds = search.FramesOver33Milliseconds;
        result.MaxMainThreadFrameGapMilliseconds = search.MaxFrameGapMilliseconds;
        result.P95MainThreadFrameGapMilliseconds = search.FramePercentile(0.95d);
        result.P99MainThreadFrameGapMilliseconds = search.FramePercentile(0.99d);
        result.MainThreadFramesOver50Milliseconds = search.FramesOver50Milliseconds;
        result.MainThreadFramesOver100Milliseconds = search.FramesOver100Milliseconds;
    }

    internal static int SearchesStartedForShowcase => _combat.SearchesStarted;

    internal static void AcceptShowcaseRoute(NGame host, CombatState state, SolverResult result)
    {
        AssertMainThread();
        CancelSearch();
        CancelDeployment();
        _combat.State = state;
        _combat.ShowcaseMode = true;
        _combat.LatestResult = result;
        _combat.LatestStamp = LiveCombatStamp.Capture(state);
        _combat.ContinuationSource = result;
        _combat.AutomaticSearchPaused = false;
        _combat.FullAutoEnabled = false;
        SolverAutoStatus.Record("stop", "showcase_route_accepted");
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlay.RefreshControls();
        Entry.Logger.Info($"[CombatSolver/Showcase] ROUTE_ACCEPTED turn={result.StartTurnNumber} end_turn={result.CombatEndedTurn} local_searches=0");
    }

    private static void StopShowcaseRoute(NGame host, string message)
    {
        _combat.FullAutoEnabled = false;
        SolverAutoStatus.Record("stop", $"showcase_route_mismatch message={message}");
        _combat.AutomaticSearchPaused = true;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        _combat.ContinuationSource = null;
        SolverOverlay.RefreshControls();
        Entry.Logger.Warn($"[CombatSolver/Showcase] ROUTE_MISMATCH stopped=true replan=false message={message}");
    }

    private static void StartFullAutoDeployment(NGame host, CombatState state, SolverResult result)
    {
        if (!result.WasReused && result.ProjectedBattleHpLossIncrease > 0)
        {
            SolverAutoStatus.Record(
                "worse_recalculation",
                $"turn={result.StartTurnNumber} previous_hp_lost={result.PreviousProjectedBattleHpLost} " +
                $"hp_lost={result.ProjectedBattleHpLost} increase={result.ProjectedBattleHpLossIncrease} " +
                $"continuing={(!_stopFullAutoOnWorseRecalculation).ToString().ToLowerInvariant()}");
            if (_stopFullAutoOnWorseRecalculation)
            {
                _combat.BugReportIssues.Record(
                    CombatBugReportIssueKind.FullAutoStoppedAfterWorseRecalculation,
                    $"第 {result.StartTurnNumber} 回合，预计战损 {result.PreviousProjectedBattleHpLost} → {result.ProjectedBattleHpLost}");
                LastFullAutoStoppedForWorseRecalculationForTesting = true;
                StopFullAutoForTestOverride("worse_recalculation", result.StartTurnNumber);
                return;
            }
        }
        if (result.CombatEndedTurn == result.StartTurnNumber)
        {
            SolverAutoStatus.Record(
                "plan",
                $"turn={result.StartTurnNumber} predicted_combat_end=true " +
                $"continuing={(!_stopFullAutoOnCombatEnd).ToString().ToLowerInvariant()}");
            if (_stopFullAutoOnCombatEnd)
            {
                StopFullAutoForTestOverride("combat_end_turn", result.StartTurnNumber);
                return;
            }
        }
        if (result.DeathTurn == result.StartTurnNumber)
        {
            SolverAutoStatus.Record(
                "plan",
                $"turn={result.StartTurnNumber} predicted_player_death=true " +
                $"continuing={(!_stopFullAutoOnDeathTurn).ToString().ToLowerInvariant()}");
            if (_stopFullAutoOnDeathTurn)
            {
                _combat.BugReportIssues.Record(
                    CombatBugReportIssueKind.FullAutoStoppedAtDeathTurn,
                    $"第 {result.StartTurnNumber} 回合");
                StopFullAutoForTestOverride("death_turn", result.StartTurnNumber);
                return;
            }
        }

        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_DEPLOY turn={result.StartTurnNumber}");
        StartDeployment(host, state, result);
    }

    private static void StopFullAutoForTestOverride(string reason, int turn)
    {
        _combat.FullAutoEnabled = false;
        SolverAutoStatus.Record("stop", $"reason={reason} turn={turn} source=test_override");
        SolverOverlay.RefreshControls();
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_STOP reason={reason} turn={turn} source=test_override");
    }

    private static void StartDeployment(NGame host, CombatState state, SolverResult result)
    {
        bool hasCurrentTurnPlan = result.BestNode.Actions.Any(action =>
            action.Turn == result.StartTurnNumber
            && (action.IsExecutable || action.Kind == PlanActionKind.EndTurn));
        if (!hasCurrentTurnPlan)
        {
            _combat.ContinuationSource = null;
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={result.StartTurnNumber} reason=turn_plan_exhausted");
            RequestSearch(
                host,
                state,
                SearchReason.PlanExhausted,
                deployWhenReady: !_combat.FullAutoEnabled);
            return;
        }

        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        SolverAutoStatus.RecordDecision(
            result.StartTurnNumber,
            result.TotalSearchElapsed,
            result.ExpandedNodes,
            result.ProjectedBattleHpLost,
            result.PotionUses.Count,
            result.WasReused);
        CancelDeployment();
        SolverDeploymentSession deployment = new() { State = state, StartTurnNumber = result.StartTurnNumber };
        _deployment = deployment;
        SolverSettingsSnapshot deploymentSettings = SolverSettings.Capture();
        SolverOverlay.RefreshControls();
        Task deploymentTask = DeployCurrentTurn(
            host,
            state,
            result,
            deploymentSettings,
            deployment,
            deployment.Cancellation.Token);
        deployment.Operation = deploymentTask;
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deploymentTask = UnattendedAsyncActivityTracker.Track(deploymentTask);
        TaskHelper.RunSafely(deploymentTask);
    }

    internal static List<PlanCardChoice> CaptureDeploymentActionChoices(PlanAction action)
        => [.. action.GetActionChoicesInExecutionOrder()];

    internal static async Task DeployResolvedTurnForTesting(NGame host, CombatState state, SolverResult result)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive || _deployment != null)
            throw new InvalidOperationException("Resolved-turn deployment requires an idle unattended request.");
        SolverDeploymentSession deployment = new() { State = state, StartTurnNumber = result.StartTurnNumber };
        _deployment = deployment;
        deployment.Operation = DeployCurrentTurn(host, state, result, SolverSettings.Capture(), deployment, deployment.Cancellation.Token);
        await deployment.Operation;
    }

    internal static PlanCardChoice[] CaptureDeploymentEndTurnChoices(PlanAction action)
        => action.TurnStartChoices?.Where(choice =>
            choice.Timing is PlanChoiceTiming.PlayerTurnEnd or PlanChoiceTiming.EnemyTurn).ToArray() ?? [];

    internal static async Task AwaitDeploymentActionChoicesAsync(NativeChoiceSession session, Task actionCompletion,
        int endTurnChoiceCount, CancellationToken token)
    {
        if (endTurnChoiceCount == 0)
        {
            await session.AwaitProducerAndCompleteAsync(actionCompletion);
            return;
        }
        await session.AwaitPhaseAsync(actionCompletion);
        await session.WaitForAllPlansConsumedAsync(token);
        await session.CompleteAndDetachAsync();
    }

    private static async Task DeployCurrentTurn(
        NGame host,
        CombatState state,
        SolverResult result,
        SolverSettingsSnapshot deploymentSettings,
        SolverDeploymentSession deployment,
        CancellationToken token)
    {
        AssertMainThread();
        bool measureDeploymentTiming = UnattendedTestRunner.IsActive;
        long deploymentStartedAt = measureDeploymentTiming
            ? Stopwatch.GetTimestamp()
            : 0;
        int turn = result.StartTurnNumber;
        List<PlanAction> actions = result.BestNode.Actions
            .Where(action => action.Turn == turn && action.IsExecutable)
            .ToList();
        PlanAction? plannedEndTurn = result.BestNode.Actions
            .FirstOrDefault(action => action.Turn == turn && action.Kind == PlanActionKind.EndTurn);
        FastModeType originalFastMode = SaveManager.Instance.PrefsSave.FastMode;
        FastModeType? overrideFastMode = ResolveDeploymentFastMode(deploymentSettings.DeploymentFastMode);
        try
        {
            if (overrideFastMode is { } requestedFastMode)
                SaveManager.Instance.PrefsSave.FastMode = requestedFastMode;
            Entry.Logger.Info(
                $"[CombatSolver/Test] DEPLOY_START turn={turn} action_count={actions.Count} " +
                $"fast_mode={deploymentSettings.DeploymentFastMode} " +
                $"inter_action_delay_seconds={deploymentSettings.DeploymentInterActionDelaySeconds:0.###}");
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                PlanAction action = actions[actionIndex];
                token.ThrowIfCancellationRequested();
                if (!IsSamePlayableTurn(state, turn))
                    throw new InvalidOperationException("部署途中已不再是原玩家回合。");

                Player player = LocalContext.GetMe(state)!;
                FairObservation.RevealCursor revealCursor = FairObservation.CaptureReveal(state, player);
                Creature? target = state.GetCreature(action.TargetCombatId);
                List<PlanCardChoice> actionChoices = CaptureDeploymentActionChoices(action);
                PlanCardChoice[] phaseChoices = CaptureDeploymentEndTurnChoices(action);
                actionChoices.AddRange(phaseChoices);
                if (actionChoices.Count > 0)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_CHOICE_PLAN turn={turn} card={action.CardId} " +
                        $"count={actionChoices.Count} sources={string.Join(',', actionChoices.Select(choice =>
                            string.IsNullOrEmpty(choice.SourceId) ? choice.Effect.ToString() : choice.SourceId))} " +
                        $"cards={string.Join(';', actionChoices.Select(choice =>
                            $"{choice.Effect}:{string.Join(',', choice.Cards.Select(card =>
                                $"{card.CardId}+{card.UpgradeLevel}#src{card.SourceOccurrence}/opt{card.OptionOccurrence}"))}"))}");
                }
                using NativeChoiceSession choiceSession = NativeChoiceRuntime.Begin(
                    state,
                    player,
                    $"deployment:{turn}:{actionIndex}:{action.CardId ?? action.PotionId}");
                choiceSession.SetPlanAndStartDriving(host, actionChoices, token,
                    detachOnLastSelection: phaseChoices.Length > 0);
                long actionStartedAt = measureDeploymentTiming
                    ? Stopwatch.GetTimestamp()
                    : 0;
                GameAction queuedAction;
                Task actionCompletion;
                if (action.Kind == PlanActionKind.UsePotion)
                {
                    PotionModel? potion = player.GetPotionAtSlotIndex(action.PotionSlot);
                    if (potion is null)
                    {
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=potion_missing " +
                            $"potion={action.PotionId} slot={action.PotionSlot}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                    {
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=potion_mismatch " +
                            $"slot={action.PotionSlot} actual={potion.Id.Entry} expected={action.PotionId}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    queuedAction = await EnqueueAndCaptureActionAsync(
                        candidate => candidate is UsePotionAction usePotion
                            && ReferenceEquals(usePotion.Player, player)
                            && usePotion.PotionIndex == (uint)action.PotionSlot,
                        () => potion.EnqueueManualUse(target),
                        token);
                    actionCompletion = queuedAction.CompletionTask;
                    LastDeployedActionStartedAtMillisecondsForTesting = System.Environment.TickCount64;
                    DeployedPotionIdsForTesting.Add(action.PotionId);
                    Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_ACTION turn={turn} potion={action.PotionId} slot={action.PotionSlot} target_index={action.TargetIndex} target_combat_id={action.TargetCombatId?.ToString() ?? "-"}");
                }
                else
                {
                    List<CardModel> hand = player.PlayerCombatState!.Hand.Cards.ToList();
                    CardModel card = FindCardForDeployment(hand, action);
                    if (!card.CanPlayTargeting(target))
                    {
                        bool targetValid = card.IsValidTarget(target);
                        bool cardPlayable = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=card_unplayable " +
                            $"card={action.CardId} occurrence={action.CardOccurrence} target_valid={targetValid} " +
                            $"can_play={cardPlayable} unplayable_reason={reason} " +
                            $"preventer={preventer?.Id.Entry ?? "-"} energy={player.PlayerCombatState.Energy} " +
                            $"stars={player.PlayerCombatState.Stars} energy_cost={card.EnergyCost.GetAmountToSpend()} " +
                            $"star_cost={card.GetStarCostWithModifiers()}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    queuedAction = await EnqueueAndCaptureActionAsync(
                        candidate => candidate is PlayCardAction playCard
                            && ReferenceEquals(playCard.NetCombatCard.ToCardModelOrNull(), card),
                        () =>
                        {
                            if (!card.TryManualPlay(target))
                                throw new InvalidOperationException($"部署卡牌 {action.CardId} 在入队时失去可用状态。");
                        },
                        token);
                    actionCompletion = queuedAction.CompletionTask;
                    LastDeployedActionStartedAtMillisecondsForTesting = System.Environment.TickCount64;
                    DeployedCardIdsForTesting.Add(card.Id.Entry);
                    Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_ACTION turn={turn} card={action.CardId} target_index={action.TargetIndex} target_combat_id={action.TargetCombatId?.ToString() ?? "-"} choice={action.Choice?.Effect.ToString() ?? "-"}");
                }
                try
                {
                    await AwaitDeploymentActionChoicesAsync(choiceSession, actionCompletion, phaseChoices.Length, token);
                    // Native CompletionTask also completes successfully when ExecuteAction faults.
                    if (queuedAction.Exception is { } actionFailure)
                        throw new InvalidOperationException(
                            $"Native deployment action failed: turn={turn} action_index={actionIndex} " +
                            $"action={action.CardId ?? action.PotionId} target={action.TargetCombatId}.",
                            actionFailure);
                    RunStatistics.Activity(state, execution: true, auto: _combat.FullAutoEnabled);
                }
                catch (NativeChoicePlanMismatchException)
                {
                    choiceSession.ReleaseVisibleSurface();
                    throw;
                }
                catch (NativeChoiceSurfaceMismatchException)
                {
                    choiceSession.ReleaseVisibleSurface();
                    throw;
                }
                if (measureDeploymentTiming)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_ACTION_COMPLETE turn={turn} action_index={actionIndex} " +
                        $"action={action.CardId ?? action.PotionId ?? action.Kind.ToString()} " +
                        $"elapsed_ms={Stopwatch.GetElapsedTime(actionStartedAt).TotalMilliseconds:F1}");
                }
                {
                    PlayerCombatState liveState = player.PlayerCombatState!;
                    Entry.Logger.Info(
                        $"[CombatSolver/Debug] DEPLOY_STATE turn={turn} action_index={actionIndex} " +
                        $"action={action.CardId ?? action.PotionId ?? action.Kind.ToString()} " +
                        $"target={action.TargetCombatId} hp={player.Creature.CurrentHp} block={player.Creature.Block} " +
                        $"energy={liveState.Energy} hand={string.Join(',', liveState.Hand.Cards.Select(card => card.Id.Entry))} " +
                        $"draw={string.Join(',', liveState.DrawPile.Cards.Select(card => card.Id.Entry))} " +
                        $"discard={string.Join(',', liveState.DiscardPile.Cards.Select(card => card.Id.Entry))} " +
                        $"exhaust={string.Join(',', liveState.ExhaustPile.Cards.Select(card => card.Id.Entry))} " +
                        $"enemies={string.Join(',', state.Enemies.Select(enemy =>
                            $"{enemy.Monster?.Id.Entry ?? "null"}:{enemy.CurrentHp}/{enemy.Block}"))} " +
                        $"powers={string.Join(',', player.Creature.Powers.Select(power =>
                            $"{power.Id.Entry}:{power.Amount}/{power.AmountOnTurnStart}"))}");
                }
                if (CombatManager.Instance.IsInProgress
                    && IsSamePlayableTurn(state, turn)
                    && (FairObservation.DescribeReveal(revealCursor, state, player)
                        ?? (actionIndex + 1 >= result.FairCommitActionLimit
                            ? $"commit_limit={result.FairCommitActionLimit}"
                            : null)) is { } revealed)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=information_revealed " +
                        $"action_index={actionIndex} {revealed}");
                    _combat.ContinuationSource = null;
                    CompleteDeployment(deployment);
                    RequestSearch(
                        host,
                        state,
                        SearchReason.InformationRevealed,
                        deployWhenReady: !_combat.FullAutoEnabled);
                    return;
                }
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (actionIndex + 1 < actions.Count
                    && deploymentSettings.DeploymentInterActionDelaySeconds > 0d)
                {
                    await WaitForDeploymentDelayAsync(
                        host,
                        deploymentSettings.DeploymentInterActionDelaySeconds,
                        token);
                }
            }

            if (CombatManager.Instance.IsInProgress && IsSamePlayableTurn(state, turn))
            {
                if (plannedEndTurn == null)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=turn_plan_exhausted " +
                        $"executed_actions={actions.Count}");
                    _combat.ContinuationSource = null;
                    CompleteDeployment(deployment);
                    RequestSearch(
                        host,
                        state,
                        SearchReason.PlanExhausted,
                        deployWhenReady: !_combat.FullAutoEnabled);
                    return;
                }
                Player player = LocalContext.GetMe(state)!;
                if (_combat.FullAutoEnabled)
                {
                    await UnattendedTestRunner.ApplyScheduledPreEndTurnDriftAsync(state, turn);
                    LiveEndTurnRiskProjection liveRisk = LiveEndTurnRiskEvaluator.EvaluateFair(
                        state,
                        plannedEndTurn.TurnStartChoices,
                        FairPlannerSettings.For(SolverSettings.Capture().Speed).Samples);
                    int plannedHpLoss = result.RequireHpLostForTurn(turn);
                    if (liveRisk.PlayerDead)
                    {
                        _combat.BugReportIssues.Record(
                            CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskDeath,
                            $"第 {turn} 回合，路线预计 {plannedHpLoss} HP，实机复核 {liveRisk.HpLost} HP");
                        _combat.ContinuationSource = null;
                        SolverAutoStatus.RecordError(
                            "live_end_turn_risk",
                            $"turn={turn} planned_hp_lost={plannedHpLoss} live_hp_lost={liveRisk.HpLost} " +
                            $"hp_before={liveRisk.HpBefore} hp_after={liveRisk.HpAfter} " +
                            $"moves={liveRisk.MonsterMoves} handed_back_turn=true");
                        if (_stopFullAutoOnDeathTurn)
                        {
                            LastFullAutoStoppedAtLiveRiskForTesting = true;
                            _combat.FullAutoEnabled = false;
                        }
                        else
                        {
                            _combat.AutomaticSearchPaused = true;
                            _combat.AutomaticSearchPausedTurn = turn;
                        }
                        SolverOverlay.RefreshControls();
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] FULL_AUTO_HAND_BACK reason=live_end_turn_risk turn={turn} " +
                            $"planned_hp_lost={plannedHpLoss} live_hp_lost={liveRisk.HpLost} " +
                            $"hp_before={liveRisk.HpBefore} hp_after={liveRisk.HpAfter} " +
                            $"player_dead={liveRisk.PlayerDead} moves={liveRisk.MonsterMoves} " +
                            $"full_auto_stopped={_stopFullAutoOnDeathTurn.ToString().ToLowerInvariant()}");
                        return;
                    }
                }
                _combat.LastSolverDeployedTurn = turn;
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
                PlanCardChoice[] endTurnChoices = CaptureDeploymentEndTurnChoices(plannedEndTurn);
                if (endTurnChoices.Length > 0)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_END_TURN_CHOICE_PLAN turn={turn} " +
                        $"count={endTurnChoices.Length} sources={string.Join(',', endTurnChoices.Select(choice => choice.SourceId))}");
                    using NativeChoiceSession choiceSession = NativeChoiceRuntime.Begin(
                        state,
                        player,
                        $"deployment_end_turn:{turn}");
                    choiceSession.SetPlanAndStartDriving(host, endTurnChoices, token, detachOnLastSelection: true);
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                    try
                    {
                        await choiceSession.WaitForAllPlansConsumedAsync(token);
                    }
                    catch (NativeChoicePlanMismatchException)
                    {
                        choiceSession.ReleaseVisibleSurface();
                        throw;
                    }
                    catch (NativeChoiceSurfaceMismatchException)
                    {
                        choiceSession.ReleaseVisibleSurface();
                        throw;
                    }
                    await choiceSession.CompleteAndDetachAsync();
                }
                else
                {
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                }
                SolverOverlay.RefreshControls();
                string deploymentEndMessage =
                    $"[CombatSolver/Test] DEPLOY_END turn={turn} action_count={actions.Count} end_turn=true " +
                    $"forecast_turn_start_choices={plannedEndTurn.TurnStartChoices?.Count ?? 0} " +
                    $"end_turn_choices={endTurnChoices.Length}";
                if (measureDeploymentTiming)
                {
                    deploymentEndMessage +=
                        $" elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}";
                }
                Entry.Logger.Info(deploymentEndMessage);
                _combat.LastSolverDeployedTurn = turn;
            }
            else
            {
                SolverOverlay.RefreshControls();
                string deploymentEndMessage =
                    $"[CombatSolver/Test] DEPLOY_END turn={turn} action_count={actions.Count} " +
                    $"end_turn=false combat_or_turn_finished=true";
                if (measureDeploymentTiming)
                {
                    deploymentEndMessage +=
                        $" elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}";
                }
                Entry.Logger.Info(deploymentEndMessage);
                _combat.LastSolverDeployedTurn = turn;
            }
        }
        catch (OperationCanceledException)
        {
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_CANCELED turn={turn}");
        }
        catch (InvalidOperationException ex) when (IsMissingDeploymentCard(ex))
        {
            _combat.ContinuationSource = null;
            CompleteDeployment(deployment);
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=card_missing " +
                $"message={ex.Message}");
            RequestSearch(
                host,
                state,
                SearchReason.DeploymentDrift,
                deployWhenReady: !_combat.FullAutoEnabled);
        }
        catch (InvalidOperationException ex) when (IsDeploymentTurnDrift(ex))
        {
            _combat.ContinuationSource = null;
            CompleteDeployment(deployment);
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=turn_drift " +
                $"message={ex.Message}");
            RequestSearch(
                host,
                state,
                SearchReason.DeploymentDrift,
                deployWhenReady: !_combat.FullAutoEnabled);
        }
        catch (NativeChoicePlanMismatchException ex)
        {
            PauseAfterNativeChoiceFailure(host, deployment, turn, ex);
        }
        catch (NativeChoiceSurfaceMismatchException ex)
        {
            PauseAfterNativeChoiceFailure(host, deployment, turn, ex);
        }
        catch (Exception ex)
        {
            _combat.ContinuationSource = null;
            _combat.AutomaticSearchPaused = true;
            _combat.AutomaticSearchPausedTurn = turn;
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.DeploymentFailure, ex);
            SolverAutoStatus.RecordError(
                "deployment_failure",
                $"turn={turn} {SolverAutoStatus.Describe(ex)} handed_back_turn=true");
            SolverOverlay.RefreshControls();
            Entry.Logger.Error($"[CombatSolver/Test] DEPLOY_FAILURE turn={turn} exception={ex}");
        }
        finally
        {
            try
            {
                if (overrideFastMode.HasValue)
                {
                    SaveManager.Instance.PrefsSave.FastMode = originalFastMode;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_SPEED_RESTORED turn={turn} restored={originalFastMode}");
                }
                if (measureDeploymentTiming)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_FINISH turn={turn} " +
                        $"elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}");
                }
            }
            finally
            {
                CompleteDeployment(deployment);
                DisposeDeploymentCancellationOnce(deployment);
                SolverOverlay.RefreshControls();
            }
        }
    }

    private static void PauseAfterNativeChoiceFailure(NGame host, SolverDeploymentSession deployment, int turn, Exception failure)
    {
        _combat.ContinuationSource = null;
        _combat.AutomaticSearchPaused = true;
        _combat.AutomaticSearchPausedTurn = turn;
        _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.DeploymentFailure, failure);
        SolverAutoStatus.RecordError(
            "native_choice_failure",
            $"turn={turn} {SolverAutoStatus.Describe(failure)} handed_back_turn=true");
        CompleteDeployment(deployment);
        SolverOverlay.RefreshControls();
        Entry.Logger.Error($"[CombatSolver/Test] DEPLOY_CHOICE_PAUSED turn={turn} exception={failure}");
    }

    internal static CardModel FindCardForDeployment(
        IReadOnlyList<CardModel> hand,
        PlanAction action)
    {
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            CardModel? stateMatch = hand
                .Where(card => string.Equals(
                    CardChoiceSupport.ChoiceCardKey(card),
                    action.CardStateKey,
                    StringComparison.Ordinal))
                .Skip(action.CardStateOccurrence)
                .FirstOrDefault();
            return stateMatch ?? throw new InvalidOperationException(
                $"部署时找不到计划中的手牌状态 {action.CardId}@{action.CardStateOccurrence}；" +
                $"当前手牌={string.Join(',', hand.Select(card => CardChoiceSupport.ChoiceCardKey(card)))}。");
        }

        return hand
            .Where(card => string.Equals(card.Id.Entry, action.CardId, StringComparison.Ordinal))
            .Skip(action.CardOccurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"部署时找不到手牌 {action.CardId}#{action.CardOccurrence}；" +
                $"当前手牌={string.Join(',', hand.Select(card => card.Id.Entry))}。");
    }

    private static bool IsMissingDeploymentCard(InvalidOperationException exception)
        => exception.Message.StartsWith("部署时找不到计划中的手牌状态 ", StringComparison.Ordinal)
            || exception.Message.StartsWith("部署时找不到手牌 ", StringComparison.Ordinal);

    private static bool IsDeploymentTurnDrift(InvalidOperationException exception)
        => string.Equals(
            exception.Message,
            "部署途中已不再是原玩家回合。",
            StringComparison.Ordinal);

    internal static async Task<GameAction> EnqueueAndCaptureActionAsync(
        Func<GameAction, bool> matches,
        Action enqueue,
        CancellationToken token)
    {
        TaskCompletionSource<GameAction> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        void OnBeforeActionExecuted(GameAction action)
        {
            if (matches(action))
                captured.TrySetResult(action);
        }

        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            enqueue();
            return await captured.Task.WaitAsync(token);
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    internal static async Task WaitForTurnStartDeploymentDelayAsync(
        NGame host,
        int turn,
        CancellationToken token = default)
    {
        double seconds = SolverSettings.Capture().DeploymentInterActionDelaySeconds;
        if (seconds <= 0d)
            return;
        long startedAt = System.Environment.TickCount64;
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_START_DEPLOY_DELAY turn={turn} seconds={seconds:0.###}");
        await WaitForDeploymentDelayAsync(host, seconds, token);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_START_DEPLOY_DELAY_COMPLETE turn={turn} " +
            $"elapsed_ms={System.Environment.TickCount64 - startedAt}");
    }

    private static async Task WaitForDeploymentDelayAsync(
        NGame host,
        double seconds,
        CancellationToken token)
    {
        SceneTreeTimer delay = host.GetTree().CreateTimer(
            seconds,
            processAlways: false,
            processInPhysics: false,
            ignoreTimeScale: false);
        await AwaitSceneTreeTimerAsync(host, delay).WaitAsync(token);
    }

    private static async Task AwaitSceneTreeTimerAsync(NGame host, SceneTreeTimer delay)
        => await host.ToSignal(delay, SceneTreeTimer.SignalName.Timeout);

    private static void DeferSearchUntilRootCaptureBarrier(
        NGame host,
        CombatState state,
        SearchReason reason,
        bool deployWhenReady,
        Task barrier)
    {
        CancelDeferredSearch();
        CancellationTokenSource cancellation = new();
        _deferredSearchCts = cancellation;
        int requestId = ++_deferredSearchId;
        int lifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_ROOT_CAPTURE_DEFERRED reason={reason} " +
            $"lifecycle_epoch={lifecycleGeneration}");
        PendingDeferredSearchReleases.RemoveAll(static task => task.IsCompleted);
        Task operation = ResumeDeferredSearchAsync(
            host,
            state,
            reason,
            deployWhenReady,
            barrier,
            cancellation,
            requestId,
            lifecycleGeneration);
        PendingDeferredSearchReleases.Add(operation);
        TaskHelper.RunSafely(operation);
    }

    private static async Task ResumeDeferredSearchAsync(
        NGame host,
        CombatState state,
        SearchReason reason,
        bool deployWhenReady,
        Task barrier,
        CancellationTokenSource cancellation,
        int requestId,
        int lifecycleGeneration)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await barrier.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TaskCompletionSource callbackCompleted = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SolverDispatcher.Post(() =>
            {
                try
                {
                    bool ownsDeferredRequest = requestId == _deferredSearchId
                        && ReferenceEquals(_deferredSearchCts, cancellation);
                    if (token.IsCancellationRequested
                        || !ownsDeferredRequest
                        || Volatile.Read(ref _combatLifecycleGeneration) != lifecycleGeneration
                        || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
                    {
                        if (ownsDeferredRequest)
                            _deferredSearchCts = null;
                        return;
                    }
                    _deferredSearchCts = null;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] SEARCH_ROOT_CAPTURE_RESUMED reason={reason} " +
                        $"lifecycle_epoch={lifecycleGeneration}");
                    RequestSearch(host, state, reason, deployWhenReady);
                }
                finally
                {
                    callbackCompleted.TrySetResult();
                }
            });
            // Even a canceled request waits for its queued callback. That callback owns the
            // final references to the old combat graph, so the combat-end barrier must not
            // declare quiescence merely because its token was canceled.
            await callbackCompleted.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Reset/newer request owns the deferred-search cancellation.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void CancelDeferredSearch()
    {
        CancellationTokenSource? cancellation = _deferredSearchCts;
        _deferredSearchCts = null;
        if (cancellation == null)
            return;
        _deferredSearchId++;
        cancellation.Cancel();
    }

    private static Task DrainDeferredSearchReleases()
    {
        if (PendingDeferredSearchReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = PendingDeferredSearchReleases.ToArray();
        PendingDeferredSearchReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static Task CancelAndDrainCombatDeferredOperations()
    {
        CancellationTokenSource cancellation = _combatDeferredOperationCancellation;
        _combatDeferredOperationCancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (PendingCombatDeferredOperations.Count == 0)
        {
            cancellation.Dispose();
            return Task.CompletedTask;
        }
        Task[] operations = PendingCombatDeferredOperations.ToArray();
        PendingCombatDeferredOperations.Clear();
        return ReleaseCombatDeferredOperationsAsync(operations, cancellation);
    }

    private static async Task ReleaseCombatDeferredOperationsAsync(
        Task[] operations,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(operations)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void CancelSearch()
    {
        SolverSearchSession? search = _search;
        _search = null;
        if (search == null)
            return;
        search.Cancellation.Cancel();
        if (!search.CallbackScheduled)
            search.CallbackCompletion.TrySetResult();
        QueueSearchReferenceRelease(search);
    }

    private static void RecordManualProjectionComparison(
        ManualProjectionBaseline baseline,
        int currentTurnNumber,
        int currentProjectedBattleHpLost,
        int currentProjectedBattlePotionCount)
    {
        ManualProjectionComparison comparison = new(
            baseline.StartTurnNumber,
            currentTurnNumber,
            baseline.ProjectedBattleHpLost,
            currentProjectedBattleHpLost,
            baseline.ProjectedBattlePotionCount,
            currentProjectedBattlePotionCount,
            baseline.StateDifference,
            baseline.OriginalCheckpointId,
            CombatBugReportExporter.CurrentSearchRootId);
        _combat.LastManualProjectionComparison = comparison;
        CombatBugReportExporter.RecordComparisonReference(baseline.OriginalCheckpointId);
        string direction;
        if (comparison.IsImprovement)
        {
            direction = "IMPROVED";
            _combat.ManualRouteImprovementDetected = true;
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.BetterWorldline,
                $"预计战损 {comparison.PreviousProjectedBattleHpLost} → {comparison.CurrentProjectedBattleHpLost}，" +
                $"下降 {-comparison.Difference} HP；多用药 {comparison.AdditionalPotionCount} 瓶，" +
                $"折算优化 {comparison.PotionAdjustedHpReduction} HP");
        }
        else if (comparison.Difference > 0)
        {
            direction = "WORSENED";
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.ManualHpLossIncreased,
                $"预计战损 {comparison.PreviousProjectedBattleHpLost} → {comparison.CurrentProjectedBattleHpLost}，" +
                $"增加 {comparison.Difference} HP");
        }
        else if (comparison.Difference < 0)
        {
            direction = "POTION_TRADE";
        }
        else
        {
            direction = "UNCHANGED";
        }

        Entry.Logger.Info(
            $"[CombatSolver/Test] MANUAL_ROUTE_{direction} " +
            $"original_turn={comparison.OriginalTurnNumber} current_turn={comparison.CurrentTurnNumber} " +
            $"previous_projected_battle_hp_lost={comparison.PreviousProjectedBattleHpLost} " +
            $"current_projected_battle_hp_lost={comparison.CurrentProjectedBattleHpLost} " +
            $"difference={comparison.Difference} " +
            $"previous_projected_battle_potion_count={comparison.PreviousProjectedBattlePotionCount} " +
            $"current_projected_battle_potion_count={comparison.CurrentProjectedBattlePotionCount} " +
            $"additional_potion_count={comparison.AdditionalPotionCount} " +
            $"potion_hp_cost={comparison.PotionHpCost} " +
            $"potion_adjusted_hp_reduction={comparison.PotionAdjustedHpReduction} " +
            $"{comparison.StateDifference}");
    }

    private static void CancelDeployment()
    {
        SolverDeploymentSession? deployment = _deployment;
        _deployment = null;
        if (deployment == null)
            return;
        deployment.Cancellation.Cancel();
        QueueDeploymentReferenceRelease(deployment);
    }

    private static async Task ReleaseDeploymentSessionAsync(SolverDeploymentSession deployment)
    {
        try
        {
            await deployment.Operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            deployment.Operation = Task.CompletedTask;
        }
        finally
        {
            DisposeDeploymentCancellationOnce(deployment);
            Interlocked.Increment(ref _deploymentReferenceReleaseCompletedCountForTesting);
        }
    }

    private static void QueueDeploymentReferenceRelease(SolverDeploymentSession deployment)
    {
        if (Interlocked.Exchange(ref deployment.ReferenceReleaseState, 1) != 0)
            return;
        PendingDeploymentReferenceReleases.RemoveAll(static task => task.IsCompleted);
        Interlocked.Increment(ref _deploymentReferenceReleaseScheduledCountForTesting);
        PendingDeploymentReferenceReleases.Add(ReleaseDeploymentSessionAsync(deployment));
    }

    private static Task DrainDeploymentReferenceReleases()
    {
        if (PendingDeploymentReferenceReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = [.. PendingDeploymentReferenceReleases];
        PendingDeploymentReferenceReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void DisposeDeploymentCancellationOnce(SolverDeploymentSession deployment)
    {
        if (Interlocked.Exchange(ref deployment.CancellationDisposeState, 1) != 0)
            return;
        deployment.Cancellation.Dispose();
        Interlocked.Increment(ref _deploymentCtsDisposeCountForTesting);
    }

    private static CombatBugReportClassificationSnapshot CaptureBugReportClassification()
        => new(
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.StateMismatch),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.DeploymentDrift),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ContinuationMissing),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.PlanExhausted),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ManualDivergence),
            _combat.BugReportIssues.Snapshot());

    internal static CombatBugReportClassificationSnapshot CaptureBugReportClassificationForExport()
        => CombatManager.Instance.IsInProgress ? CaptureBugReportClassification()
            : _lastBugReportClassification ?? CaptureBugReportClassification();
    internal static ManualProjectionComparison? ManualProjectionComparisonForExport
        => CombatManager.Instance.IsInProgress ? _combat.LastManualProjectionComparison : _lastManualProjectionComparison;

    private static void CompleteDeployment(SolverDeploymentSession deployment)
    {
        if (ReferenceEquals(_deployment, deployment))
            _deployment = null;
    }

    private static FastModeType? ResolveDeploymentFastMode(SolverDeploymentFastMode mode)
        => mode switch
        {
            SolverDeploymentFastMode.FollowGame => null,
            SolverDeploymentFastMode.Normal => FastModeType.Normal,
            SolverDeploymentFastMode.Fast => FastModeType.Fast,
            SolverDeploymentFastMode.Instant => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    private static bool CanSolve(CombatState state, out string rejection)
    {
        Player? player = LocalContext.GetMe(state);
        if (_solverDisabled)
            rejection = "求解器已在设置中禁用。";
        else if (!CombatManager.Instance.IsInProgress)
            rejection = "当前没有进行中的战斗。";
        else if (state.Players.Count != 1)
            rejection = "第一版只支持单人战斗。";
        else if (state.CurrentSide != CombatSide.Player || player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            rejection = "当前不是玩家出牌阶段。";
        else if (CombatManager.Instance.PlayerActionsDisabled)
            rejection = "玩家操作当前被游戏禁用。";
        else
        {
            rejection = string.Empty;
            return true;
        }
        return false;
    }

    private static bool IsSamePlayableTurn(CombatState state, int turn)
    {
        Player? player = LocalContext.GetMe(state);
        return ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
            && state.CurrentSide == CombatSide.Player
            && player?.PlayerCombatState?.TurnNumber == turn
            && player.PlayerCombatState.Phase == PlayerTurnPhase.Play;
    }

    private static void AssertMainThread()
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("CombatSolver 的主线程控制器被后台线程调用。");
    }
}
