using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertSearchPortfolioSettings(CombatState combat)
    {
        SolverSettingsSnapshot defaults = SolverSettings.Capture();
        if (!defaults.UseBeamWidthPortfolio || defaults.UseNoveltyPortfolio || defaults.UseEarlyTurnExploration)
            throw new InvalidOperationException("固定搜索组合必须为多宽度开启、多策略与前两回合探索关闭。");
        if (SolverSettings.DeserializeForTesting(
                "{\"performancePreset\":\"VeryHigh\",\"enableNoGcRegion\":false,\"fairPlannerSamples\":3,\"potionDirectives\":[]}")
            != new SolverSettingsData())
            throw new InvalidOperationException("旧设置键没有在读取时被忽略。");
        SolverSettingsSnapshot portfolioSettings = SolverSettings.Capture();
        SearchPolicySnapshot portfolioEnabled = SolverController.CaptureSearchPolicy(
            portfolioSettings with
            {
                UseBeamWidthPortfolio = true,
                UseNoveltyPortfolio = true,
                UseEarlyTurnExploration = true,
            },
            combat,
            includeTurnSetup: false,
            theftPolicy: null);
        SearchPolicySnapshot portfolioDisabled = SolverController.CaptureSearchPolicy(
            portfolioSettings with
            {
                UseBeamWidthPortfolio = false,
                UseNoveltyPortfolio = false,
                UseEarlyTurnExploration = false,
            },
            combat,
            includeTurnSetup: false,
            theftPolicy: null);
        if (!portfolioEnabled.UseBeamWidthPortfolio || portfolioDisabled.UseBeamWidthPortfolio
            || !portfolioEnabled.UseNoveltyPortfolio || portfolioDisabled.UseNoveltyPortfolio
            || portfolioEnabled.EarlyTurnExplorationDepth != 2
            || portfolioEnabled.EarlyTurnExplorationBudgetMilliseconds != 2_400_000
            || portfolioDisabled.EarlyTurnExplorationDepth != 0)
            throw new InvalidOperationException("组合搜索设置没有按搜索请求冻结。");
        _completedChecks.Add("SearchPortfolios:FixedRefinementOn:NoveltyOff:DamageGuidanceThreshold8:SettingsRoundTrip:LegacyKeysIgnored:PolicySnapshot");
    }

    private async Task AssertControllerSessionLifecycleAsync(CombatState combat)
    {
        CombatBeamSolver.VerifyCycleTranspositionLeasePolicyForTesting();
        if (!NativeChoiceSurface.VerifyCoveredSurfaceWaitPolicyForTesting())
            throw new InvalidOperationException("原生选牌页面被其他覆盖层遮挡时仍消耗了缺失超时。");
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("控制器会话测试找不到 NGame。");
        if (SolverController.SolverDisabled)
            throw new InvalidOperationException("控制器会话测试要求求解器初始启用。");
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("药水策略 UI 测试找不到本地玩家。");
        (int Slot, PotionModel Potion)? strategyPotion = Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => (Slot: slot, Potion: player.GetPotionAtSlotIndex(slot)))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => (item.Slot, item.Potion!))
            .Cast<(int Slot, PotionModel Potion)?>()
            .FirstOrDefault();
        if (strategyPotion is { } forcedPotion)
        {
            SolverController.SetPotionDirectiveForTesting(
                combat,
                forcedPotion.Slot,
                forcedPotion.Potion.Id.Entry,
                SolverPotionDirective.Force);
            try
            {
                SolverSettingsSnapshot settings = SolverSettings.Capture();
                SearchInteractionState interaction = new();
                SearchPolicySnapshot forcedPolicy = SolverController.CaptureSearchPolicy(
                    settings,
                    combat,
                    includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
                {
                    Profile = settings.Profile with
                    {
                        MaxExpandedNodes = Math.Min(500, settings.Profile.MaxExpandedNodes),
                        SoftTimeBudgetMilliseconds = 5_000,
                    },
                    FixedBudget = true,
                    MaxDegreeOfParallelism = 4,
                    Interaction = interaction,
                };
                CombatRootSnapshot forcedRoot = CombatRootSnapshot.Capture(combat);
                SolverDisplayNames forcedDisplayNames = SolverDisplayNames.Capture(combat);
                BattleDamageSnapshot forcedBattleDamage = BattleDamageTracker.Observe(combat);
                new CombatBeamSolver(
                    forcedRoot,
                    forcedDisplayNames,
                    forcedBattleDamage,
                    forcedPolicy,
                    searchProfile: forcedPolicy.Profile with { BeamWidth = 1 })
                    .VerifyFinalPolicyQualificationRetentionForTesting(
                        forcedPotion.Potion.Id.Entry,
                        forcedPotion.Slot);
                bool forcedAdoptionRequested = false;
                SolverResult forcedResult = await Task.Run(() => CombatSearchCoordinator.Solve(
                    forcedRoot,
                    forcedDisplayNames,
                    forcedBattleDamage,
                    forcedPolicy,
                    CancellationToken.None,
                    progress =>
                    {
                        if (progress.CurrentBestResult != null
                            && !forcedAdoptionRequested)
                        {
                            forcedAdoptionRequested = true;
                            interaction.RequestApplyCurrentTurn();
                        }
                    }));
                if (!forcedAdoptionRequested
                    || !forcedResult.BestNode.Actions.Any(action =>
                        action.Kind == PlanActionKind.UsePotion
                        && action.PotionSlot == forcedPotion.Slot
                        && string.Equals(
                            action.PotionId,
                            forcedPotion.Potion.Id.Entry,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("强制用药的可采用路线没有使用指定槽位的指定药水。");
                }
            }
            finally
            {
                SolverController.SetPotionDirectiveForTesting(
                    combat,
                    forcedPotion.Slot,
                    forcedPotion.Potion.Id.Entry,
                    SolverPotionDirective.Smart);
            }
            await AssertBoundedSmartPotionAuditAsync(combat);
        }

        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching)
            throw new InvalidOperationException("控制器没有建立搜索会话。");

        // 进入战斗时模型注册表早已初始化，生命周期信号必须已送达；模组类型在此之前不得进缓存。
        if (!ModelDbGetIdCachePatch.ModelRegistryInitializedForTesting
            || !ModelDbGetIdCachePatch.ExerciseRegistrationGateForTesting(typeof(SolverController)))
        {
            throw new InvalidOperationException("ModelDb.GetId 缓存没有按模型注册表初始化时点区分模组类型。");
        }
        if (strategyPotion is { } potionEntry)
        {
            PotionStrategySnapshot initialStrategy = SolverController.CapturePotionStrategy(
                combat,
                SolverPotionPolicy.Smart);
            if (initialStrategy.Resolve(potionEntry.Slot, potionEntry.Potion.Id.Entry)
                != SolverPotionDirective.Smart)
            {
                throw new InvalidOperationException("新获得药水没有默认使用智能策略。");
            }
            SolverController.SetPotionDirectiveForTesting(
                combat,
                potionEntry.Slot,
                potionEntry.Potion.Id.Entry,
                SolverPotionDirective.Force);
            PotionStrategySnapshot forcedStrategy = SolverController.CapturePotionStrategy(
                combat,
                SolverPotionPolicy.Smart);
            PlanAction forcedUse = new(
                PlanActionKind.UsePotion,
                player.PlayerCombatState!.TurnNumber,
                PotionSlot: potionEntry.Slot,
                PotionId: potionEntry.Potion.Id.Entry);
            if (!forcedStrategy.HasForcedDirectives
                || !forcedStrategy.EvaluateForcedUses([forcedUse], renewablePotionShapedRock: false)
                    .AllForcedUsesSatisfied
                || forcedStrategy.EvaluateForcedUses([], renewablePotionShapedRock: false)
                    .AllForcedUsesSatisfied)
            {
                throw new InvalidOperationException("指定药水没有形成精确槽位和药水身份约束。");
            }
            SolverController.SetPotionDirectiveForTesting(
                combat,
                potionEntry.Slot,
                potionEntry.Potion.Id.Entry,
                SolverPotionDirective.Disabled);
            PotionStrategySnapshot disabledStrategy = SolverController.CapturePotionStrategy(
                combat,
                SolverPotionPolicy.Smart);
            if (disabledStrategy.AllowsExplicitUse(
                    potionEntry.Slot,
                    potionEntry.Potion.Id.Entry,
                    SolverPotionPolicy.Smart,
                    forceAllDisabled: false))
            {
                throw new InvalidOperationException("保护药水仍进入主动用药候选。");
            }
            SolverController.SetPotionDirectiveForTesting(
                combat,
                potionEntry.Slot,
                potionEntry.Potion.Id.Entry,
                SolverPotionDirective.Smart);
            if (SolverController.ResolvePotionDirectiveForTesting(potionEntry.Slot, potionEntry.Potion.Id.Entry)
                != SolverPotionDirective.Smart)
                throw new InvalidOperationException("恢复智能后仍保留了逐瓶策略覆盖项。");
        }
        AssertSearchPortfolioSettings(combat);
        AssertDefaultSearchParallelism();

        // A one-HP fixture can finish its first search during the awaited checks above, so
        // establish a fresh active session immediately before exercising the stop transition.
        if (!SolverController.IsSearching)
        {
            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            if (!SolverController.IsSearching)
                throw new InvalidOperationException("停止断言前无法重新建立活动搜索会话。");
        }
        SolverController.StopSearchByUser(host);
        if (SolverController.IsSearching
            || SolverController.IsDeploying
            || SolverController.FullAutoEnabled
            || !SolverController.AutomaticSearchPaused
            || SolverController.CurrentResultForBugReport != null)
        {
            throw new InvalidOperationException("用户停止搜索后仍残留活动会话、路线或自动计算状态。");
        }

        SolverController.RequestSearch(host, combat, SearchReason.AutoTurnStart);
        if (SolverController.IsSearching || !SolverController.AutomaticSearchPaused)
            throw new InvalidOperationException("用户停止后，自动回合入口重新启动了搜索。");

        SolverController.RecordManualProjectionComparisonForTesting(17, 9, 0, 1);
        if (SolverController.ManualRouteImprovementDetected
            || SolverController.LastManualProjectionComparisonForTesting is not
                { AdditionalPotionCount: 1, PotionAdjustedHpReduction: -1 })
            throw new InvalidOperationException("手操多用一瓶药只省8 HP时产生了更优世界线提示。");

        SolverController.RecordManualProjectionComparisonForTesting(18, 1, 0, 2);
        if (SolverController.ManualRouteImprovementDetected)
            throw new InvalidOperationException("手操多用两瓶药只省17 HP时产生了更优世界线提示。");

        SolverController.RecordManualProjectionComparisonForTesting(17, 8, 0, 1);
        if (!SolverController.ManualRouteImprovementDetected)
            throw new InvalidOperationException("手操多用一瓶药省9 HP时缺少更优世界线提示。");

        SolverController.RecordManualProjectionComparisonForTesting(7, 3);
        if (!SolverController.ManualRouteImprovementDetected
            || SolverController.LastManualProjectionComparisonForTesting?.Difference != -4)
        {
            throw new InvalidOperationException("手操降低预计战损后没有记录比较结果并显示绿色反馈提示。");
        }
        string liveDescription = SolverController.BuildBugReportDescription("玩家现场描述");
        if (!liveDescription.Contains("玩家现场描述", StringComparison.Ordinal)
            || !liveDescription.Contains("找到更优世界线", StringComparison.Ordinal)
            || !liveDescription.Contains("预计战损 7 → 3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("在线问题描述没有读取本场手操改线信号。");
        }

        AssertBugReportAutomaticClassification();
        await AssertBugReportUploadBoundariesAsync();

        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching || SolverController.AutomaticSearchPaused)
            throw new InvalidOperationException("重新计算没有恢复当前及后续回合搜索。");
        SolverController.CancelSearchForTesting();

        await NextFrameAsync();
        await NextFrameAsync();
        if (SolverController.IsSearching
            || SolverController.CurrentResultForBugReport != null
            || SolverController.LastSearchFailureForTesting != null)
        {
            throw new InvalidOperationException("已取消搜索的回调重新写入了控制器状态。");
        }

        long priorReleaseDeadline = System.Environment.TickCount64 + 30_000;
        while (SolverController.PendingSearchReferenceReleaseCountForTesting != 0)
        {
            if (System.Environment.TickCount64 >= priorReleaseDeadline)
                throw new TimeoutException("前一项取消搜索在 30 秒内没有释放 worker+callback 引用。");
            await NextFrameAsync();
        }

        int releasesScheduledBefore =
            SolverController.SearchReferenceReleaseScheduledCountForTesting;
        int releasesCompletedBefore =
            SolverController.SearchReferenceReleaseCompletedCountForTesting;
        int cancellationsDisposedBefore =
            SolverController.SearchCtsDisposeCountForTesting;
        TaskCompletionSource deferredVisualSetupCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool staleDeferredOperationRan = false;
        _ = SolverController.StartCombatDeferredOperation(async token =>
        {
            await deferredVisualSetupCompletion.Task;
            token.ThrowIfCancellationRequested();
            staleDeferredOperationRan = true;
        });
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching)
            throw new InvalidOperationException("搜索 A 没有建立控制器会话。");
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching)
            throw new InvalidOperationException("搜索 B 没有替换搜索 A。");
        int staleTurnSetupGeneration = SolverController.CombatLifecycleGeneration;
        SolverController.Reset("unattended_search_replacement_release");
        SolverController.ReleaseUnattendedResultReferencesForTesting();
        if (SolverController.LastCompletedResultForTesting != null
            || SolverController.LastTurnSetupResultForTesting != null
            || SolverController.LastSearchFailureForTesting != null)
        {
            throw new InvalidOperationException(
                "最终断言后仍由无人测试观察字段保留上一场 SolverResult 图。");
        }
        if (SolverController.RecordTurnSetupFailure(
                combat,
                staleTurnSetupGeneration,
                new InvalidOperationException("stale turn setup failure"))
            || SolverController.RecordTurnSetupStateMismatch(
                combat,
                staleTurnSetupGeneration,
                "stale turn setup mismatch"))
        {
            throw new InvalidOperationException(
                "Reset 后的旧回合准备完成仍写入了新控制器生命周期。");
        }
        Task referenceRelease = SolverController.LastCombatReferenceReleaseForTesting;
        long releaseDeadline = System.Environment.TickCount64 + 30_000;
        while (SolverController.SearchReferenceReleaseCompletedCountForTesting
               - releasesCompletedBefore < 2)
        {
            if (System.Environment.TickCount64 >= releaseDeadline)
                throw new TimeoutException("搜索 A/B 在 30 秒内没有释放 worker+callback 引用。");
            await NextFrameAsync();
        }
        if (referenceRelease.IsCompleted)
        {
            throw new InvalidOperationException(
                "Reset 引用屏障没有等待回合开始 visual-setup 延迟任务。");
        }
        deferredVisualSetupCompletion.TrySetResult();
        while (!referenceRelease.IsCompleted)
        {
            if (System.Environment.TickCount64 >= releaseDeadline)
            {
                throw new TimeoutException(
                    "搜索 A/B 在 Reset 后 30 秒内没有越过 worker+callback 引用释放屏障。");
            }
            await NextFrameAsync();
        }
        await referenceRelease;
        if (staleDeferredOperationRan)
            throw new InvalidOperationException("已取消的旧战斗延迟任务仍在 Reset 后执行。");
        int releasesScheduled =
            SolverController.SearchReferenceReleaseScheduledCountForTesting
            - releasesScheduledBefore;
        int releasesCompleted =
            SolverController.SearchReferenceReleaseCompletedCountForTesting
            - releasesCompletedBefore;
        int cancellationsDisposed =
            SolverController.SearchCtsDisposeCountForTesting
            - cancellationsDisposedBefore;
        if (releasesScheduled != 2
            || releasesCompleted != 2
            || cancellationsDisposed != 2)
        {
            throw new InvalidOperationException(
                $"搜索 A/B 的 Reset 引用释放不完整：scheduled={releasesScheduled} " +
                $"completed={releasesCompleted} cts_disposed={cancellationsDisposed}。");
        }

        SolverSettingsData settingsBeforeDelayCancellation = SolverSettings.Current;
        try
        {
            const double fullDelaySeconds = 3d;
            SolverSettings.ApplyForTesting(settingsBeforeDelayCancellation with
            {
                DeploymentInterActionDelaySeconds = fullDelaySeconds,
            });
            Task delayOperation = SolverController.StartCombatDeferredOperation(
                token => SolverController.WaitForTurnStartDeploymentDelayAsync(
                    host,
                    turn: -1,
                    token: token));
            if (delayOperation.IsCompleted)
            {
                throw new InvalidOperationException(
                    "3 秒回合开始延迟没有进入真实 SceneTreeTimer 等待。");
            }

            long cancellationStartedAt = Stopwatch.GetTimestamp();
            SolverController.Reset("unattended_deployment_delay_cancel");
            Task delayReferenceRelease = SolverController.LastCombatReferenceReleaseForTesting;
            try
            {
                await delayReferenceRelease.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "取消回合开始/动作间隔计时器后，战斗引用屏障仍等待完整 3 秒延迟。",
                    ex);
            }
            double cancellationElapsedMilliseconds =
                Stopwatch.GetElapsedTime(cancellationStartedAt).TotalMilliseconds;
            if (!delayOperation.IsCompletedSuccessfully
                || cancellationElapsedMilliseconds >= 1_000d)
            {
                throw new InvalidOperationException(
                    $"取消部署延迟后引用释放不够快：" +
                    $"operation_completed={delayOperation.IsCompletedSuccessfully} " +
                    $"elapsed_ms={cancellationElapsedMilliseconds:F1}。");
            }
        }
        finally
        {
            SolverSettings.ApplyForTesting(settingsBeforeDelayCancellation);
        }

        await SearchGcPolicy.CaptureRootSnapshotBarrier().WaitAsync(TimeSpan.FromSeconds(30));
        SearchGcPolicy.DetachCombatLifecyclePressure(
            "unattended_disabled_gc_reference_barrier_setup");
        TaskCompletionSource disabledReferenceReleaseGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task disabledDeferredOperation = Task.CompletedTask;
        try
        {
            SolverSettings.OverrideSnapshotForTesting(current => current with { EnableNoGcRegion = false });
            disabledDeferredOperation = SolverController.StartCombatDeferredOperation(async token =>
            {
                await disabledReferenceReleaseGate.Task;
                token.ThrowIfCancellationRequested();
            });
            SolverController.Reset("unattended_disabled_gc_reference_barrier");
            Task disabledReferenceRelease = SolverController.LastCombatReferenceReleaseForTesting;
            if (disabledReferenceRelease.IsCompleted)
            {
                throw new InvalidOperationException(
                    "关闭 NoGC 的 Reset 夹具没有建立故意延迟的旧图释放任务。");
            }
            Task disabledRootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
            if (!disabledRootCaptureBarrier.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    "全程关闭 NoGC 的战斗仍阻塞下一场根快照，未保持 CLR 常规回收语义。");
            }
            disabledReferenceReleaseGate.TrySetResult();
            await disabledReferenceRelease.WaitAsync(TimeSpan.FromSeconds(1));
            await disabledDeferredOperation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            disabledReferenceReleaseGate.TrySetResult();
            await disabledDeferredOperation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            SolverSettings.OverrideSnapshotForTesting(null);
        }

        var deploymentLifecycle =
            await SolverController.ExerciseDeploymentSessionLifecycleForTestingAsync();
        if (!deploymentLifecycle.StaleCompletionPreservedCurrentSession
            || !deploymentLifecycle.BarrierWaitedForBothOperations
            || deploymentLifecycle.ReleasesScheduled != 2
            || deploymentLifecycle.ReleasesCompleted != 2
            || deploymentLifecycle.CancellationsDisposed != 2)
        {
            throw new InvalidOperationException(
                $"部署 A/B 的 Reset 引用释放不完整：" +
                $"stale_preserved={deploymentLifecycle.StaleCompletionPreservedCurrentSession} " +
                $"barrier_waited={deploymentLifecycle.BarrierWaitedForBothOperations} " +
                $"scheduled={deploymentLifecycle.ReleasesScheduled} " +
                $"completed={deploymentLifecycle.ReleasesCompleted} " +
                $"cts_disposed={deploymentLifecycle.CancellationsDisposed}。");
        }
    }

    private static async Task AssertBoundedSmartPotionAuditAsync(CombatState combat)
    {
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchInteractionState interaction = new();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings,
            combat,
            includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            Profile = settings.Profile with
            {
                MaxExpandedNodes = 100_000,
                SoftTimeBudgetMilliseconds = 1_200,
            },
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            Interaction = interaction,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
        SearchPolicySnapshot thresholdPolicy = policy with
        {
            AcceptableBattleHpLoss = 100_000,
            Profile = policy.Profile with
            {
                BeamWidth = 1,
                MaxExpandedNodes = Math.Min(500, policy.Profile.MaxExpandedNodes),
                SoftTimeBudgetMilliseconds = 1_000,
            },
        };
        SolverResult thresholdResult = new CombatBeamSolver(
                root,
                displayNames,
                BattleDamageTracker.Observe(combat),
                thresholdPolicy,
                searchProfile: thresholdPolicy.Profile)
            .Solve();
        if (!CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(thresholdPolicy, thresholdResult))
        {
            throw new InvalidOperationException("可接受战损上限早停夹具没有返回满足阈值的完整胜利路线。");
        }
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("药水阶段文案测试找不到本地玩家。");
        (int Slot, PotionModel Potion) potion = Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => (Slot: slot, Potion: player.GetPotionAtSlotIndex(slot)))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => (item.Slot, item.Potion!))
            .First();
        (int Slot, PotionModel Potion)[] searchablePotions = Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => (Slot: slot, Potion: player.GetPotionAtSlotIndex(slot)))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => (item.Slot, item.Potion!))
            .ToArray();
        PlanAction potionAction = new(
            PlanActionKind.UsePotion,
            player.PlayerCombatState!.TurnNumber,
            PotionSlot: potion.Slot,
            PotionId: potion.Potion.Id.Entry);
        string potionName = displayNames.Potion(potion.Potion.Id.Entry);
        int expectedMinimumPotionCost = PotionUsePolicy.StrategicHpCost(
            potion.Potion,
            root.HasRenewablePotionShapedRock);
        int expectedMinimumPotionHpSaved = PotionUsePolicy.SmartRequiredHpSaved(
            expectedMinimumPotionCost,
            root.BossHpRelief);
        if (root.MinimumSearchablePotionStrategicCost != expectedMinimumPotionCost
            || !CombatSearchCoordinator.CanAnySmartPotionQualify(
                root,
                policy,
                potionFreeWon: true,
                potionFreeHpDeficit: Math.Max(0, expectedMinimumPotionHpSaved - 1))
            || !CombatSearchCoordinator.CanAnySmartPotionQualify(
                root,
                policy,
                potionFreeWon: true,
                potionFreeHpDeficit: expectedMinimumPotionHpSaved))
        {
            throw new InvalidOperationException(
                "Smart 药水补查错误地用当前血量空间排除了可能缩短战斗的路线。");
        }
        if (CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: false,
                strategicHpDeficit: 0,
                combatEndedTurn: null,
                earliestPossibleCombatEndedTurn: 1,
                provableStrategicHpFloor: 0)
            || CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 2,
                earliestPossibleCombatEndedTurn: 1,
                provableStrategicHpFloor: 0)
            || CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 1,
                earliestPossibleCombatEndedTurn: null,
                provableStrategicHpFloor: 0)
            || !CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 1,
                earliestPossibleCombatEndedTurn: 1,
                provableStrategicHpFloor: 0))
        {
            throw new InvalidOperationException(
                "开局能力补查错误地把未胜、较慢的零战损结果或未知回合下界当成可停止条件。");
        }
        // A wounded player can still end the fight higher than zero battle loss, so zero stops being the
        // provable floor and a zero-loss route no longer proves the search can stop.
        if (CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 1,
                earliestPossibleCombatEndedTurn: 1,
                provableStrategicHpFloor: -9)
            || !CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: -9,
                combatEndedTurn: 1,
                earliestPossibleCombatEndedTurn: 1,
                provableStrategicHpFloor: -9))
        {
            throw new InvalidOperationException(
                "可回复生命时，零战损被错误地当成了已证明的最优下界。");
        }
        if (ActEndingBossPolicy.PersistentValueOfRecoveredHp(10, BossHpRelief.None) != 10
            || ActEndingBossPolicy.PersistentValueOfRecoveredHp(10, BossHpRelief.ActClearHeal) != 2
            || ActEndingBossPolicy.PersistentValueOfRecoveredHp(10, BossHpRelief.RunEnding) != 0
            || ActEndingBossPolicy.PersistentValueOfRecoveredHp(0, BossHpRelief.None) != 0
            || ActEndingBossPolicy.StrategicHpDeficit(12, 3, 10, BossHpRelief.None) != 5
            || ActEndingBossPolicy.StrategicHpDeficit(0, 0, 9, BossHpRelief.None) != -9
            || ActEndingBossPolicy.StrategicHpDeficit(0, 0, 9, BossHpRelief.RunEnding) != 0)
        {
            throw new InvalidOperationException(
                "回复生命的跨战斗计价没有按幕末 Boss 的战后回复折算。");
        }
        PostCombatRelicHealProfile bloodOnly = new(
            UnconditionalHeal: 6,
            WoundedHeal: 0,
            WoundedHpPercent: 0);
        PostCombatRelicHealProfile bloodAndMeat = new(
            UnconditionalHeal: 6,
            WoundedHeal: 12,
            WoundedHpPercent: 50);
        if (bloodOnly.HealFor(finalHp: 70, finalMaxHp: 80) != 6
            || bloodOnly.HealFor(finalHp: 78, finalMaxHp: 80) != 2
            || bloodOnly.HealFor(finalHp: 80, finalMaxHp: 80) != 0
            || PostCombatRelicHealProfile.None.HealFor(finalHp: 10, finalMaxHp: 80) != 0)
        {
            throw new InvalidOperationException("无条件战后遗物回血没有被剩余生命上限裁剪。");
        }
        // Vanilla truncates the threshold, so half of 75 max HP is 37 and ending on 38 misses the heal.
        if (bloodAndMeat.HealFor(finalHp: 37, finalMaxHp: 75) != 18
            || bloodAndMeat.HealFor(finalHp: 38, finalMaxHp: 75) != 6
            || bloodAndMeat.HealFor(finalHp: 70, finalMaxHp: 75) != 5
            || bloodAndMeat.MonotoneHealFor(finalHp: 37, finalMaxHp: 75) != 6)
        {
            throw new InvalidOperationException("带阈值的战后遗物回血没有按原版截断判定，或进入了排序口径。");
        }
        if (ActEndingBossPolicy.RankedPostCombatRelicHeal(
                bloodAndMeat, completeVictory: true, finalHp: 37, finalMaxHp: 75) != 6
            || ActEndingBossPolicy.RankedPostCombatRelicHeal(
                bloodAndMeat, completeVictory: false, finalHp: 37, finalMaxHp: 75) != 0
            || ActEndingBossPolicy.RankedPostCombatRelicHeal(
                bloodOnly, completeVictory: true, finalHp: 0, finalMaxHp: 75) != 0)
        {
            throw new InvalidOperationException("战后遗物回血没有只在活着获胜的路线上计入。");
        }
        PrimarySearchIncumbent incumbent = new(
            StrategicHpDeficit: 5,
            CombatEndedTurn: 3);
        if (!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                strategicHpLowerBound: 6,
                turn: 2,
                incumbent: incumbent)
            || !CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                strategicHpLowerBound: 5,
                turn: 4,
                incumbent: incumbent)
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                strategicHpLowerBound: 5,
                turn: 3,
                incumbent: incumbent)
            // Even far past the incumbent turn, a branch below the incumbent's loss
            // remains eligible. Its current max-HP deficit is deliberately not an input:
            // later effects may recover max HP before combat ends.
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                strategicHpLowerBound: 4,
                turn: 99,
                incumbent: incumbent))
        {
            throw new InvalidOperationException(
                "主结果下界剪枝没有严格限制为不可逆累计战损与回合字典序。");
        }
        AssertPrimaryIncumbentFiltering();
        PotionFreePolicyBaseline auditedPotionFreeBaseline = new(
            Won: true,
            HpDeficit: 5,
            PlayerHp: 75,
            CombatEndedTurn: 3);
        PrimarySearchIncumbent? dynamicIncumbent = null;
        if (CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline: null,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 0,
                maximumPotionUses: 0,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 0,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 2,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: false,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: null,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: false,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 0,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 4,
                incumbent: ref dynamicIncumbent)
            || !CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || dynamicIncumbent != new PrimarySearchIncumbent(5, 2)
            || !CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 9,
                incumbent: ref dynamicIncumbent)
            || dynamicIncumbent != new PrimarySearchIncumbent(4, 9)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 1,
                incumbent: ref dynamicIncumbent))
        {
            throw new InvalidOperationException(
                "动态主结果下界没有限制为精确用药层中满足硬规则且严格优于无药审计的完整胜利。");
        }
        if (PotionUsePolicy.SmartRequiredHpSaved(
                SolverWeights.PotionMinimumHpSaved,
                BossHpRelief.ActClearHeal) != 45)
        {
            throw new InvalidOperationException("跨幕回复没有按 80% 缩放药水价值。");
        }
        if (ActEndingBossPolicy.DeathSavePremium(0) != 0
            || ActEndingBossPolicy.DeathSavePremium(40) != 360
            || ActEndingBossPolicy.DeathSaveBeamCost(40) != 400)
        {
            throw new InvalidOperationException(
                "一次性保命资源的复活没有按用掉它的代价计价。");
        }
        if (ActEndingBossPolicy.StrategicHpDeficit(20, 0, 56, BossHpRelief.None, 40) != 364
            || ActEndingBossPolicy.StrategicHpDeficit(20, 0, 56, BossHpRelief.ActClearHeal, 40) != 377
            || ActEndingBossPolicy.StrategicHpDeficit(20, 0, 56, BossHpRelief.RunEnding, 40) != 380)
        {
            throw new InvalidOperationException("路线治疗、战后回血与保命资源消耗的组合计价不一致。");
        }
        if (SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit: 100,
                candidateCombatEndedTurn: 9,
                currentCompleteVictory: true,
                currentStrategicHpDeficit: 0,
                currentCombatEndedTurn: 1,
                candidateDeathSaveUseCount: 0,
                currentDeathSaveUseCount: 1) >= 0
            || SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit: 100,
                candidateCombatEndedTurn: 9,
                currentCompleteVictory: false,
                currentStrategicHpDeficit: 0,
                currentCombatEndedTurn: null,
                candidateDeathSaveUseCount: 1,
                currentDeathSaveUseCount: 0) >= 0)
        {
            throw new InvalidOperationException(
                "完整胜利没有先保留一次性保命资源，或者无替代生还路线时拒绝复活。");
        }
        if (ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.ActClearHeal,
                BossHpStrategy.ProgressionFirst,
                BossHpStrategy.MinimizeHpLoss) != BossHpRelief.ActClearHeal
            || ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.ActClearHeal,
                BossHpStrategy.MinimizeHpLoss,
                BossHpStrategy.ProgressionFirst) != BossHpRelief.None
            || ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.RunEnding,
                BossHpStrategy.MinimizeHpLoss,
                BossHpStrategy.ProgressionFirst) != BossHpRelief.RunEnding
            || ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.RunEnding,
                BossHpStrategy.ProgressionFirst,
                BossHpStrategy.MinimizeHpLoss) != BossHpRelief.None
            || PotionUsePolicy.SmartRequiredHpSaved(
                SolverWeights.PotionMinimumHpSaved,
                BossHpRelief.None) != SolverWeights.PotionMinimumHpSaved)
        {
            throw new InvalidOperationException("两类幕末 Boss 的最低战损策略没有独立恢复正常血量权重。");
        }
        if (!CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(
                completeVictory: true,
                projectedBattleHpLost: 0,
                acceptableBattleHpLoss: 0)
            || !CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(
                completeVictory: true,
                projectedBattleHpLost: 5,
                acceptableBattleHpLoss: 5)
            || CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(
                completeVictory: false,
                projectedBattleHpLost: 0,
                acceptableBattleHpLoss: 5)
            || CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(
                completeVictory: true,
                projectedBattleHpLost: 6,
                acceptableBattleHpLoss: 5))
        {
            throw new InvalidOperationException("可接受战损上限只应在完整胜利且战损不超过阈值时触发。");
        }
        SearchablePotionSlotSnapshot[] allowedPotions = root.SearchablePotions
            .Where(potion => policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false))
            .ToArray();
        BossHpRelief strategicBossHpRelief = ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);
        int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            SolverWeights.PotionMinimumHpSaved,
            strategicBossHpRelief);
        int freePotionCount = allowedPotions.Count(potion => potion.StrategicHpCost == 0);
        if (new[]
            {
                0,
                Math.Max(0, paidPotionHpRequired - 1),
                paidPotionHpRequired,
                paidPotionHpRequired >= int.MaxValue / 8
                    ? paidPotionHpRequired
                    : paidPotionHpRequired * 2,
            }.Any(hpDeficit => CombatSearchCoordinator.MaximumSmartPotionUses(
                root,
                policy,
                potionFreeWon: true,
                potionFreeHpDeficit: hpDeficit) != (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                    ? allowedPotions.Length
                    : Math.Min(
                        allowedPotions.Length,
                        freePotionCount + (paidPotionHpRequired >= int.MaxValue / 4
                            ? 0
                            : hpDeficit / paidPotionHpRequired)))))
        {
            throw new InvalidOperationException(
                "Smart 药水梯度没有按每瓶战略 HP 成本限制搜索层数。");
        }
        if (searchablePotions.Length >= 2)
        {
            (int Slot, PotionModel Potion) disabled = searchablePotions[0];
            SolverController.SetPotionDirectiveForTesting(
                combat,
                disabled.Slot,
                disabled.Potion.Id.Entry,
                SolverPotionDirective.Disabled);
            try
            {
                SolverSettingsSnapshot restrictedSettings = SolverSettings.Capture();
                SearchPolicySnapshot restrictedPolicy = SolverController.CaptureSearchPolicy(
                    restrictedSettings,
                    combat,
                    includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat));
                int maximum = CombatSearchCoordinator.MaximumSmartPotionUses(
                    root,
                    restrictedPolicy,
                    potionFreeWon: false,
                    potionFreeHpDeficit: 0);
                if (maximum != searchablePotions.Length - 1)
                {
                    throw new InvalidOperationException(
                        $"禁用一瓶药后 Smart 仍会搜索过多药水层：maximum={maximum} " +
                        $"searchable={searchablePotions.Length}。");
                }
            }
            finally
            {
                SolverController.SetPotionDirectiveForTesting(
                    combat,
                    disabled.Slot,
                    disabled.Potion.Id.Entry,
                    SolverPotionDirective.Smart);
            }
        }
        if (SolverInterimResultOrdering.IsResourceTradeImprovement(
                candidateHpDeficit: 2,
                candidatePotionCost: 9,
                currentHpDeficit: 10,
                currentPotionCost: 0)
            || !SolverInterimResultOrdering.IsResourceTradeImprovement(
                candidateHpDeficit: 1,
                candidatePotionCost: 9,
                currentHpDeficit: 10,
                currentPotionCost: 0)
            || SolverInterimResultOrdering.IsResourceTradeImprovement(
                candidateHpDeficit: 10,
                candidatePotionCost: 0,
                currentHpDeficit: 10,
                currentPotionCost: 0))
        {
            throw new InvalidOperationException("搜索中间路线没有按每瓶 9 HP 成本保持严格递增优。");
        }
        SolverInterimResult noPotionTrade = new(
            Won: true,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 10,
            StrategicHpDeficit: 10,
            PotionStrategicCost: 0,
            ProjectedBattlePotionCount: 0,
            EnemyHp: 0,
            Score: 0d,
            CombatEndedTurn: 3);
        SolverInterimResult underpaidPotionTrade = noPotionTrade with
        {
            ProjectedBattleHpLost = 2,
            StrategicHpDeficit = 2,
            PotionStrategicCost = 9,
            ProjectedBattlePotionCount = 1,
            CombatEndedTurn = 1,
        };
        SolverInterimResult qualifiedPotionTrade = underpaidPotionTrade with
        {
            ProjectedBattleHpLost = 1,
            StrategicHpDeficit = 1,
        };
        if (SolverInterimResultOrdering.IsBetter(underpaidPotionTrade, noPotionTrade)
            || !SolverInterimResultOrdering.IsBetter(qualifiedPotionTrade, noPotionTrade)
            || CombatSearchCoordinator.IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon: true,
                candidateWon: true,
                hpSaved: 8,
                hpRequired: 9,
                protectsLoot: false)
            || !CombatSearchCoordinator.IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon: true,
                candidateWon: true,
                hpSaved: 9,
                hpRequired: 9,
                protectsLoot: false))
        {
            throw new InvalidOperationException("Smart 药水梯度或动态展示绕过了每瓶 9 HP 门槛。");
        }
        SolverInterimResult displayedVictory = new(
            Won: true,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 10,
            StrategicHpDeficit: 10,
            PotionStrategicCost: 0,
            ProjectedBattlePotionCount: 0,
            EnemyHp: 0,
            Score: 0d);
        SolverInterimResult higherDamageVictory = displayedVictory with
        {
            ProjectedBattleHpLost = 11,
            StrategicHpDeficit = 8,
        };
        SolverInterimResult lowerDamageVictory = displayedVictory with
        {
            ProjectedBattleHpLost = 9,
            StrategicHpDeficit = 9,
        };
        if (SolverInterimResultOrdering.CanPromoteDisplayedResult(
                higherDamageVictory,
                displayedVictory)
            || !SolverInterimResultOrdering.CanPromoteDisplayedResult(
                lowerDamageVictory,
                displayedVictory))
        {
            throw new InvalidOperationException("完整路线的动态预计战损没有保持单调递减。");
        }
        if (SolverInterimResultOrdering.IsCompleteVictory(
                actionCount: 1,
                allEnemiesDead: false,
                playerDead: false,
                projectedPlayerHp: 80)
            || !SolverInterimResultOrdering.IsCompleteVictory(
                actionCount: 1,
                allEnemiesDead: true,
                playerDead: false,
                projectedPlayerHp: 69))
        {
            throw new InvalidOperationException("未结束战斗的回合边界被错误发布为可采用路线。");
        }
        if (CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.Disabled,
                maximumPotionUses: 0,
                minimumPotionUses: 0,
                fixedPrefixActions: null) != "正在搜索无药路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 1,
                minimumPotionUses: 1,
                fixedPrefixActions: [potionAction]) != $"正在搜索使用 {potionName} 路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 2,
                minimumPotionUses: 2,
                fixedPrefixActions: [potionAction, potionAction])
                != $"正在搜索使用 {potionName} 和 {potionName} 路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 3,
                minimumPotionUses: 3,
                fixedPrefixActions: [potionAction, potionAction, potionAction])
                != $"正在搜索使用 {potionName}、{potionName} 和 {potionName} 路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 2,
                minimumPotionUses: 2,
                fixedPrefixActions: null) != "正在搜索恰好 2 瓶药路线")
        {
            throw new InvalidOperationException("药水补查没有生成无药与任意多药阶段文案。");
        }
        BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);
        List<long> elapsedSamples = [];
        bool adoptionRequested = false;
        int? displayedPotionCount = null;
        int? displayedHpLost = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        SolverResult adopted = await Task.Run(() => CombatSearchCoordinator.Solve(
            root,
            displayNames,
            battleDamage,
            policy,
            CancellationToken.None,
            progress =>
            {
                elapsedSamples.Add(progress.ElapsedMilliseconds);
                if (adoptionRequested
                    || progress.CurrentBestResult is not { } result)
                {
                    return;
                }
                displayedPotionCount = result.ProjectedBattlePotionCount;
                displayedHpLost = result.ProjectedBattleHpLost;
                adoptionRequested = interaction.RequestApplyCurrentTurn();
            }));
        stopwatch.Stop();
        if (stopwatch.ElapsedMilliseconds > 4_000)
        {
            throw new InvalidOperationException(
                $"Smart 药水补查超过单次请求预算：{stopwatch.ElapsedMilliseconds} ms。");
        }
        if (elapsedSamples.Zip(elapsedSamples.Skip(1), (left, right) => right >= left).Any(valid => !valid))
            throw new InvalidOperationException("药水补查的累计耗时发生倒退。");
        if (!adoptionRequested
            || displayedPotionCount != adopted.ProjectedBattlePotionCount
            || displayedHpLost != adopted.ProjectedBattleHpLost)
        {
            throw new InvalidOperationException("搜索中间结果没有显示用药、战损并在玩家采纳后成为最终路线。");
        }
    }

    private static void AssertPrimaryIncumbentFiltering()
    {
        int noHeal = ActEndingBossPolicy.StrategicHpDeficit(7, 0,
            StrategicHpRecoveryBound.OptimisticRecoveredHp(0, 40, 80, 0), BossHpRelief.None);
        int burningBlood = ActEndingBossPolicy.StrategicHpDeficit(3, 0,
            StrategicHpRecoveryBound.OptimisticRecoveredHp(0, 47, 80, 6), BossHpRelief.None);
        int unknownHeal = ActEndingBossPolicy.StrategicHpDeficit(7, 0,
            StrategicHpRecoveryBound.OptimisticRecoveredHp(0, 40, 80, int.MaxValue), BossHpRelief.None);
        if (noHeal != 7 || burningBlood != -3 || unknownHeal != -33
            || !CombatBeamSolver.ShouldPruneByPrimaryIncumbent(noHeal, 2, new(5, 3))
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(burningBlood, 2, new(-2, 3))
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(unknownHeal, 2, new(5, 3)))
        {
            throw new InvalidOperationException("回血上界错误地剪掉了遗物回血或未知治疗路线。");
        }

        PrimarySearchIncumbent incumbent = new(StrategicHpDeficit: 0, CombatEndedTurn: 3);
        // All nodes have zero accumulated loss; their turns exercise the second primary key.
        SimulationSnapshot snapshot = new(
            score: 0,
            stateKey: default,
            unorderedPileKey: default,
            cycleShapeKey: default,
            projectedShuffleOrderKey: default,
            projectedShuffleOrderValue: 0,
            hasRisk: false,
            playerDead: false,
            allEnemiesDead: false,
            playerHp: 1,
            playerMaxHp: 1,
            cumulativePlayerHpLost: 0,
            recoveredPlayerHp: 0,
            deathSaveRelicHpRestored: 0,
            longTermResourceValue: 0,
            angerCopiesGenerated: 0,
            projectedPlayerHp: 1,
            playerBlock: 0,
            enemyHp: 1,
            enemyBlock: 0,
            aliveEnemyCount: 1,
            aliveEnemyMask: 1,
            rawEnemyHp: 1,
            maxCurrentEnemyHp: 1,
            enemyCombatDistributionKey: default,
            enemyDurabilityByCombatId: default,
            revivingEnemyCount: 0,
            persistentBuffValue: 0,
            strategicEffects: default,
            persistentSetupTraits: default,
            latentSetupValue: 0,
            latentSetupTraits: default,
            focusTargetCombatId: null,
            focusTargetPressure: 0,
            focusTargetRemainingHp: 0,
            focusTargetCurrentThreat: 0,
            focusTargetVulnerableTurns: 0,
            mostVulnerableTargetCombatId: null,
            retainedAttackValue: 0,
            replayPotentialValue: 0,
            futureResourceValue: 0,
            ostyHp: 0,
            ostyMaxHp: 0,
            delayedDamageValue: 0,
            reactiveDamageValue: 0,
            enemyStrengthSuppression: 0,
            enemyWeakTurns: 0,
            enemyVulnerableTurns: 0,
            enemyControlDistributionKey: default,
            sandpitRemaining: 0,
            liveDeckClutter: 0,
            liveDeckSize: 0,
            outstandingStolenResource: 0,
            offensiveProgressValue: 0,
            energy: 0,
            stars: 0,
            historyEntryCount: 0,
            handCount: 0,
            reachableHandValue: 0,
            zeroCostPlayableCount: 0,
            canTriggerArtOfWarNextTurn: false,
            pocketwatchCardsPlayedThisTurn: 0,
            pocketwatchCardsPlayedLastTurn: 0,
            pocketwatchCardThreshold: -1,
            potionUseCount: 0,
            potionStrategicCost: 0,
            automaticPotionUseCount: 0,
            turn: 1,
            shufflesCrossed: 0,
            processedEnemyDeaths: new HashSet<uint>(),
            boundaryReason: SearchBoundaryReason.None,
            predictionGaps: [],
            simulator: null!);
        SearchNode keepFirst = new(
            Action: null,
            ActionCount: 0,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 2,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: default,
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: snapshot,
            CombatProgress: null!);
        SearchNode keepSecond = keepFirst with { Turn = 3 };
        SearchNode rejectFirst = keepFirst with { Turn = 4 };
        SearchNode rejectSecond = keepFirst with { Turn = 5 };

        foreach ((List<SearchNode> input, SearchNode[] expected) in new[]
                 {
                     (new List<SearchNode> { rejectFirst, rejectSecond, keepFirst },
                         new[] { keepFirst }),
                     (new List<SearchNode> { rejectFirst, rejectSecond },
                         Array.Empty<SearchNode>()),
                     (new List<SearchNode> { keepFirst, rejectFirst, keepSecond, rejectSecond },
                         new[] { keepFirst, keepSecond }),
                     (new List<SearchNode> { keepFirst, keepSecond },
                         new[] { keepFirst, keepSecond }),
                 })
        {
            SearchNode[] original = input.ToArray();
            List<SearchNode> result = CombatBeamSolver.ApplyPrimaryIncumbentBound(
                input,
                incumbent,
                out int pruned);
            if (!result.SequenceEqual(expected, ReferenceEqualityComparer.Instance)
                || !input.SequenceEqual(original, ReferenceEqualityComparer.Instance)
                || pruned != input.Count - expected.Length
                || pruned == 0 && !ReferenceEquals(input, result))
            {
                throw new InvalidOperationException(
                    "主结果下界过滤恢复了被拒绝的前缀、改变了顺序或原列表，或剪枝计数不一致。");
            }
        }
    }

    private static void AssertBugReportAutomaticClassification()
    {
        CombatBugReportIssueLedger issues = new();
        foreach (CombatBugReportIssueKind kind in Enum.GetValues<CombatBugReportIssueKind>())
            issues.Record(kind, "分类测试");
        CombatBugReportClassificationSnapshot snapshot = new(
            StateMismatchReplans: 1,
            DeploymentDriftReplans: 2,
            ContinuationMissingReplans: 3,
            PlanExhaustedReplans: 4,
            ManualDivergenceReplans: 5,
            issues.Snapshot());
        string description = CombatBugReportDescription.AppendAutomaticClassification(
            "玩家填写的问题描述",
            snapshot);
        string[] expectedClassifications =
        [
            "玩家填写的问题描述",
            "【CombatSolver 自动分类】",
            $"CombatSolver 版本：{CombatBugReportDescription.CurrentModVersion}",
            "计划外重算：3 次（状态不一致 1，执行漂移 2）",
            "续接路线缺失后重算：3 次",
            "本回合路线耗尽后重算：4 次",
            "手操偏离原路线后重算：5 次",
            "找到更优世界线",
            "手操后预计战损上升",
            "重算后预计战损上升",
            "搜索初始化失败",
            "第三方 Mod 不兼容",
            "计算失败",
            "搜索动作回放失败",
            "搜索内存或容量错误",
            "药水策略未满足",
            "计算期间状态变化，过期结果已丢弃",
            "自动执行中止",
            "回合准备选牌失败",
            "回合准备计划与实机状态不一致",
            "未计划的选牌",
            "选牌页面执行失败",
            "遗物标注回放与选中状态不一致",
            "等待游戏状态超时",
            "存在尚未支持的战斗语义",
            "全自动因重算后战损上升而暂停",
            "全自动因预计本回合死亡而暂停",
            "全自动因结束回合实机复核将死亡而暂停",
            "全自动因结束回合实机复核战损上升而暂停",
        ];
        foreach (string expected in expectedClassifications)
        {
            if (!description.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"在线问题描述缺少自动分类：{expected}。");
        }
    }

    private static void AssertDefaultSearchParallelism()
    {
        if (SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(1) != 1
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(2) != 2
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(3) != 2
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(4) != 4
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(8) != 4
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(15) != 4
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(16) != 8
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(32) != 8)
        {
            throw new InvalidOperationException("默认搜索并行度没有按逻辑处理器数量解析为 1/2/4/8。");
        }
        if (SolverSettings.Capture().SearchMaxDegreeOfParallelism
            != SolverWeights.DefaultSearchMaxDegreeOfParallelism)
            throw new InvalidOperationException("Search parallelism did not use the CPU default.");
    }
}
