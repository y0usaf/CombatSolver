# CombatSolver 测试入口

## v3 公平求解器离线合同

离线 harness（`tools/search/OfflineSearchHarness`，不启动游戏）。`OFFLINE_HARNESS_NEW_SOLVER_CHECKS=<组>`，公共参数 `--seed FB2 --ascension 0 --milestone M1 --budget-ms 60000 --beam 24 --nodes 2000 --dop 1`：

| 组 | 角色/人数/遭遇 | 断言 |
| --- | --- | --- |
| `fairness` `solo-equiv` `draw-first` `transfigure-early` `transfigure-late` | IRONCLAD 1P `NIBBITS_NORMAL` | 隐藏世界不变性、确定性斩杀、信息价值、变形时机 |
| `coop-legion` `coop-rally` `coop-lift` `coop-energy` `coop-new-mirrors` `un-end-turn` | 2P（`coop-legion` 另跑 4P） | 联机卡牌镜像与撤销结束回合监视器 |
| `setup-lethality` | NECROBINDER 2P `SKULKING_COLONY_ELITE` | 实机误判 a 的首回合：致命线被保留与估值，不选整局测得最差的致命+防+防 |
| `coop-legion-punch` | NECROBINDER 2P `PUNCH_CONSTRUCT_NORMAL` | 实机误判 b 的第 3 回合：军团线被评估、候选可区分，模拟预计的本回合掉血及每名玩家生命与实机执行一致 |
| `coop-gift-energy` / `-single` / `-manual` / `-retake` | IRONCLAD 2P `NIBBITS_NORMAL` | 队友在本地结束回合后打出「相信你」：Auto 与「执行本回合」结束的回合被撤销并花掉赠能；手动结束的回合、以及被玩家手动取消后重新结束的回合不撤销 |

`OFFLINE_HARNESS_FAIR_OBSERVATION_CHECKS=1` 与 `OFFLINE_HARNESS_COOP_EVAL=<场景>:v3`（10 个联机评估场景）同一入口。`OFFLINE_HARNESS_SETUP_PLAYOUT=<lethality-hang|legion-punch>:<solver|卡牌,...>` 从上述位置强制首回合线后用 v3 打完整局，用于建立首回合真值。这些合同依赖墙钟硬上限之外的确定性：高并发（同时多个 harness 进程）下 `fairness` 可能因硬上限截断而偶发不一致，合同应低并发运行。

## Ctrl+F9 求解器菜单

无长期无头合同；菜单只调用已有入口（`SolverAutoToggle.SetAuto`、`SetCoopPlayMode`、`ExecuteSingleTurn`、新增 `SetDeploymentFastMode`）。本轮在 Xvfb + llvmpipe（`opengl3`）下以非 `--headless` 方式启动隔离实例（一次性改写的启动器，未入库），用 `--keep-game-open --hold-after-initial-search` 停在 SMOKE-001 战斗中，xdotool 发送按键/鼠标并截图、OCR 与 `[CombatSolver/Auto]` 日志对账：Ctrl+F9 开关、Esc 关闭；↓↓↓ 后 →/← 依次写入 `deployment_fast_mode=Normal/Fast/Instant/Fast`；数字 2 与鼠标左键第 2 行分别写入 `coop_play_mode=Wait/FreePlay`；速度行右键回退到 `Normal`；回车/空格在第 1 行产生 `start user_toggle_on` / `stop user_toggle_off`；鼠标点击「执行本回合」产生 `single_turn` 并关闭菜单。战斗外（主菜单）同一菜单也可打开，执行本回合置灰。首次探针暴露 `RefreshControls` 对已释放的顶部栏开关调用 `SyncState`（`ObjectDisposedException`，旧 Ctrl+F11 路径同样可达），已改为逐个检查实例有效后复测为 0 次。

同一最终构建上 `COOP-AUTO-EXECUTE`（2 人）与 `BETA-DEPLOY-IRONCLAD-SENTINEL` Passed。`COOP-FREE-PLAY-EXECUTE`（2 人）在未改动的 a4560194 上同样失败（2/2，`The discarded free-play turn ended unexpectedly.`：夹具关闭 Auto 前重排后的路线已结束回合），不是本改动引入；未验证：真实可见 Steam 窗口下的排版与手柄、真实联机房间中「瞬间（联机不生效）」标注。

0.111 公共分支（`sts2-0.111.1`）拣选同一改动后 Release 构建 0 警告 0 错误；在 0.111.1 私有副本上 `COOP-AUTO-EXECUTE`（2 人）与 `SMOKE-001` Passed。可见探针只在 0.113 副本上运行，菜单代码两分支逐字相同。

## 联机自动模式节奏

`COOP-FREE-PLAY-EXECUTE` 覆盖「自由行动」节奏：设置层证明自由行动是缺省值且选择可回读；`WaitForCoopStableBoardAsync` 在队友未准备时立即返回；Auto 在队友仍在行动时就打出本地牌（队友手牌与出牌记录不变）；队友中途改变棋盘后丢弃剩余路线并立即重新求解，期间不进入队友等待；最后 `Ctrl+F8` 仍只执行一个本地回合并关闭 Auto，且同一卡实例不会被出两次。`COOP-AUTO-EXECUTE` 覆盖「等待队友」节奏：等待、切换成自由行动即解除等待、切回等待后重新阻塞、队友准备后在稳定根恢复、首次出牌后改棋盘丢弃余下路线、`Ctrl+F8` 只出本地牌且 Auto 保持关闭。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId COOP-FREE-PLAY-EXECUTE -PlayerCount 2 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -Seed COOPFREE1 -EnemyCurrentHp 60 -FixedSearchBudget -SearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 180 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id COOP-FREE-PLAY-EXECUTE --player-count 2 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed COOPFREE1 --enemy-current-hp 60 --fixed-search-budget --search-budget-override-milliseconds 1500 --timeout-seconds 180 --cleanup-instance-on-exit
```

检查项：`CoopFreePlay:Settings:DefaultFreePlayAndPersistedChoice`、`CoopFreePlay:DoesNotWaitForTeammates`、`CoopFreePlay:ActsWhileTeammateStillPlaying:LocalActions=1`、`CoopFreePlay:BoardChangeAfterFirstAction:StalePlanDroppedAndReplanned`、`CoopFreePlay:SingleTurnExecute:LocalActions=2:AutoStopped`、`CoopFreePlay:TeammateChangeMidTurn:ReplannedLocalActions=3`；`--player-count 4` 使用同一入口；本轮 0.111 副本 2 人与 0.113 副本 2／4 人各 Passed。队友中途改棋盘由夹具在原版生物上注入：单独探测过（Auto 关闭、只把一张防御注入队友手牌并让其走原生动作队列）原生动作路径直接崩溃（exit 139），同一夹具只注入不出牌则 Passed，因此这是单进程虚拟跑局的限制，而不是自由行动的并发问题；「Auto 出牌途中真实队友出牌」只能由真实联机验证；最后一步在下一个玩家回合重复该过程，并等 Auto 把丢弃后的回合走完。虚拟跑局仍用单人 net service 并把队友准备标记直接写进原版集合，真实联机语义由游戏自身的多人 net service 提供，尚未实测。

## 多人自动模式与单回合执行

虚拟 2／4 人跑局在单进程内为每名玩家建立 `Player`，无人测试场景 `COOP-AUTO-EXECUTE` 覆盖「等待队友」节奏：队友回合未结束时 Auto 等待、切换成自由行动立即解除该等待、切回等待后重新阻塞、队友准备后在稳定根恢复、首次本地出牌后修改队友棋盘只执行一张并丢弃余下路线、`Ctrl+F8` 单回合执行只出本地牌且 Auto 保持关闭。虚拟跑局仍使用单人 net service，因此夹具直接写入原版准备结束集合来观察 `IsPlayerReadyToEndTurn`；真实联机语义由游戏自身的多人 net service 提供，尚未实测。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId COOP-AUTO-EXECUTE -PlayerCount 2 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -Seed COOPAUTO1 -EnemyCurrentHp 60 -FixedSearchBudget -SearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 180 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id COOP-AUTO-EXECUTE --player-count 2 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed COOPAUTO1 --enemy-current-hp 60 --fixed-search-budget --search-budget-override-milliseconds 1500 --timeout-seconds 180 --cleanup-instance-on-exit
```

`--player-count 4` 使用同一入口；检查项为 `VirtualCoopRun:Players=2|4`、`CoopAuto:WaitPace:BlocksWhileTeammatePlays`、`CoopAuto:SwitchToFreePlay:ReleasedWait`、`CoopAuto:WaitForTeammates:StableBoard`、`CoopAuto:BoardChangeAfterFirstAction:RemainingPlanDiscarded`、`CoopAuto:SingleTurnExecute:LocalActions=2:AutoStopped`。本分支单人 `SMOKE-001` 同时 Passed（`NativeOutcome:combatEnded=True:turn=1:hp=80:maxHp=80`；0.113 副本用 `BETA-DEPLOY-IRONCLAD-SENTINEL`，同为第 1 回合结束、HP 80/80）。真实 Steam/ENet 双人房间、延迟、队友目标辅助牌和网络取消仍未验证。

## 0.51.4 战斗逻辑与选牌续接

按改动选择最小验证层，方法见 [无人测试](HEADLESS_TESTING.md) 与 [社区验收](community/testing-guide.md)。以下命令提供当前复跑入口，不表示本轮已执行。单人共享损血剪枝的新基线验证见[策略证据](strategy/hp-loss-pruning/README.md#单人共享损血剪枝2026-10-05)。

历史记录见 [归档索引](archive/testing/README.md)，0.48.1 的验证、失败与未验证项见 [历史卷 12](archive/testing/volume-12.md)。

PR #213 的贡献者分阶段验证见 [Q002 历史入口](issues/q002-route-quality.md#0494-重新验证2026-10-05)。当前 0.50.0 主线整合使用 O003、O004、O005 同根、同政策对照，结果与复跑预算见 [当前验收](issues/q002-route-quality.md#0500-主线整合2026-10-05)。

0.49.4 的额外回合镜像顺序、同根成长胜利续用、整场自动部署与上传引导验证见 [历史卷 16](archive/testing/volume-16.md)。

0.49.0 的行为验证沿用本页 PR #203、#204 合并验收与 [战斗状态修复验证](archive/testing/volume-13.md)。版本及发布文档调整采用 L0 检查和发布构建；原有未验证项保留。

0.49.3 的框架、局外 Mod 与 BaseLib 验证见 [历史卷 14](archive/testing/volume-14.md)。

移动运行库内存回收验证见 [历史卷 15](archive/testing/volume-15.md)。

## Ctrl+F9 面板可见性（PR #226）

快捷键、新图层、重置和监控合同的通过、失败与可见验证范围见[历史卷21](archive/testing/volume-21.md#ctrlf9-面板可见性pr-226)。

## 社区批次 Q010 与组合补搜

现行组合入口按共享节点与时间准入，执行期间在每批提交边界处理内存预约、回收和停止。原包 SearchOnly、Beam 组合检查及真实 CLR 合同的结果、失败与未验证项见 [历史卷 20](archive/testing/volume-20.md#社区批次-q010-与组合补搜)。

## 0.50.1 智能药水机会成本

终局单药／双药低收益拒绝与 Force、奖励抵扣、原价高收益哨兵由 `SMART-OPENING-POTION-ADMISSION` 覆盖；跨成员比较由 `python -B tools/testing/checks/BeamWidthPortfolioChecks/run.py` 覆盖（118项通过）。原生 runId `d9629992ac31423586c7465510c18f6d` Passed（23.63秒，含建局），实例已清理。失败基线、同根材料与未验证范围见[问题记录](issues/potion-opportunity-20261006.md)。

## 开局药水补搜准入

固定两槽满栏，使用原版奖励 RNG 捕获确定掉药／不掉药的搜索根。`SMART-OPENING-POTION-ADMISSION` 覆盖低损共同准入与换药抵扣，`SMART-OPENING-POTION-VALUE` 覆盖原价省血边界。失败基线、最终结果与验证范围见 [历史卷 20](archive/testing/volume-20.md#开局药水补搜准入)。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SMART-OPENING-POTION-ADMISSION -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id SMART-OPENING-POTION-ADMISSION --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
```

将 ScenarioId 改为 `SMART-OPENING-POTION-VALUE` 可单独执行原价省血边界。

## 0.49.1 日志站硬逻辑（2026-10-04）

强制结束回合选牌、群体 Power、死亡金币回调、延迟能量／等离子球、开局小刀、回合末自动出牌、行动意图、资源隔离、反应格挡和界面归属的原生差分见 [逐类验收](issues/0.49.1-hardbugs-20261004.md#原生验收证据)。该记录保留失败基线、runId、复跑入口、第三方边界及未验证项；生产部署合同包含增量验证和计划外重算断言。

0.49.2 内容性 Mod 失败分类的原生合同、完整命令及未验证范围见[历史卷19](archive/testing/volume-19.md)。

## 0.49.1 内存提交回归（2026-10-04）

`CombatSolver.GcPolicyChecks` 直接编译生产 Runtime 内存代码。`default-commit` 在未改生产代码时 Failed：显式回退后分配上限仍有效；修复后 2 项 Passed，覆盖普通检查点保留限额、不可分割提交完成回收后续行、后续请求重新建立限额与取消保持原准入。

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- default-commit
```

相邻真实 CLR 回归：`default-entry` 2 项、`recovery-lifecycle` 3 项 Passed，包含实际 NoGC 退出与恢复、显式退出持续生效及诊断失败清理。每项设有 15 或 20 秒截止时间。未重放 18 份原包整场，也没有可见 Steam 或低内存宿主性能结论；[排查记录](issues/0.49.0-memory-commit-regression-20261004.md)保留固定报告身份与触发窗口。

## PR #203 的机制合同

金币、最大生命回复与遗物间接回复的贡献者证据见 [专题记录](performance/gold-max-hp-healing-20261003.md)。独立场景为 `GOLD-HEALING-MECHANISMS`、`MAX-HP-HEALING-CALLBACKS`、`FEED-MAX-HP-CAP`、`RELIC-MAX-HP-HEALING-BOUNDS` 和 `AXEBOT-JOSS-DEFERRED-FORK`，均从平台原生无人入口运行，使用 IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、120 秒上限及实例清理。原作者结果与本轮合并验证分别记账。

金币和回复合同同时传 `-EvidenceDirectory .local/validation/pr203/<场景>`（Bash：`--evidence-directory`），初始生命为 50/80。金纸合同还传 `-RelicsPath coverage/fixtures/regressions/axebot/axebot-joss-deferred-relics.json`（Bash：`--relics-path`）。搜索阶段顺序合同为 `KNOWN-HEALING-MEMBERS` 和 `KNOWN-HEALING-OPENING`，使用 SILENT；受影响的固定前缀合同使用 IRONCLAD，强制结束回合牌合同使用 REGENT。

## 公平决策的候选比较派发（2026-11-14）

`FairDecisionPlanner.Decide` 的比较阶段改为 `(样本, 候选)` 平铺派发，并发上限 `min(作业数, max(样本宽度, min(逻辑核心*3/4, 样本宽度*2)))`。重跑入口（离线 harness，不启动可见游戏）：

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release

# 质量与耗时哨兵：同根、同预设、同预算；A/B 用 OFFLINE_HARNESS_COMBATSOLVER_DLL 换 DLL
OFFLINE_HARNESS_FAIR_PLAY=v2 OFFLINE_HARNESS_FAIR_SPEED=Normal DOTNET_gcServer=1 \
  dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request tools/search/OfflineSearchHarness/late-run/positions/late-ir-decmil-t2-a.json \
  --label ab --out .local/late-run/ab --milestone M1 --budget-ms 5000 --dop 1

# 公平性合同（隐藏状态只重采样、不读实时状态、不回收）
OFFLINE_HARNESS_FAIR_OBSERVATION_CHECKS=1 dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --character IRONCLAD --encounter FUZZY_WURM_CRAWLER_WEAK --seed FAIRCHK --label fair --out .local/fair-checks --milestone M1 --budget-ms 5000 --dop 1
```

实测（本机 32 逻辑核、服务端 GC、单宿主串行、其余 agent 同时占用 CPU）：`Thorough` 上 `t1-a`+`t2-a` 决策均值 9480→7754 ms、最大 17658→16770 ms、求解次数 967→941，损血每次都相同；`Normal` `holdout` 五个位置均值 4323→4383 ms（漂移内）、p90 11456→10454 ms、损血相同、计划 14/15 次逐项相同；`Normal` `tune` 四个位置均值 4232→3393 ms、最大 13671→9747 ms、损血与计划完全一致。32 并发对照在 `t2-a` 把路线改差（损失血 7→13），因此并发上限停在 3/4 核。数字与未验证项见[开发笔记](DEVELOPMENT_NOTES.md)。

## 当前矩阵命令

矩阵启动器只读取本节的平台原生命令；历史记录不作为自动执行清单。以下两项分别检查倾泻手空边界与相邻效果作用域，不运行整场搜索。需要当前游戏及 RitsuLib 路径，矩阵启动器统一管理实例并在结束时清理。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CASCADE-EMPTY-HAND-NATIVE -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId EFFECT-SCOPE-ADJACENT-CONTRACT -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id CASCADE-EMPTY-HAND-NATIVE --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
./tools/testing/run-unattended-test.sh --scenario-id EFFECT-SCOPE-ADJACENT-CONTRACT --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
```


## 0.48.0 硬错误机制合同

这些场景各自停止在共享首因或必要跨回合边界。完整runId、失败基线和未验证项见[历史卷13](archive/testing/volume-13.md)，逐包分类见[问题记录](issues/0.48.0-hardbugs-20261003.md)。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId NATIVE-CHOOSE-OPEN-GATE -CharacterId SILENT -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CALCULATED-GAMBLE-ORDERED-DISCARD -CharacterId SILENT -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FROZEN-LIGHTNING-CHANNELS -CharacterId DEFECT -EncounterId MECHA_KNIGHT_ELITE -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId REPORT-ROUND-IMBALANCED -CharacterId NECROBINDER -EncounterId BOWLBUGS_NORMAL -InitialPlayerHp 200 -InitialPlayerMaxHp 200 -ClearPlayerHand -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId SECOND-WIND-REPORT-ROOT -CharacterId IRONCLAD -EncounterId DECIMILLIPEDE_ELITE -InitialRoundNumber 3 -InitialPlayerTurnNumber 3 -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId GROUP-DEBUFF-REACTIVE-DRAW -CharacterId NECROBINDER -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CONSTRUCT-REAPER-ARTIFACT -CharacterId NECROBINDER -EncounterId CONSTRUCT_MENAGERIE_NORMAL -InitialRoundNumber 3 -InitialPlayerTurnNumber 3 -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FIXED-PREFIX-POTION-POLICY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FIXED-PREFIX-TURN-OUTCOMES -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId MIRRORED-HOOK-FILTER -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FROZEN-ROOT-LISTENERS -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CALCULATED-HISTORY-FREEZE -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId VOID-FORM-TURN-CHOICES -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FLATTEN-MUSIC-BOX-ENTRY -CharacterId NECROBINDER -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId SEEKER-ORDERED-OPTIONS -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId RITUAL-TEMPORARY-STRENGTH -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId NOXIOUS-RAMPART-ORDER -CharacterId IRONCLAD -EncounterId TURRET_OPERATOR_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId EVIL-EYE-EXHAUST-HISTORY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId BLOCK-SPEC-CARD-PLAY-IDENTITY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId ROUTE-ADOPTION-LIFETIME -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId PAELS-LEGION-FINISHED-REFERENCE -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId END-TURN-RISK-LOSS-ACCOUNTING -CharacterId DEFECT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId MAKE-IT-SO-FULL-HAND -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CARD-COST-IDENTITY-CONTRACT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId DAMPEN-REACTIVE-ROCKET-PUNCH -CharacterId DEFECT -EncounterId KNIGHTS_ELITE -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId DAMPEN-REACTIVE-MELANCHOLY -CharacterId NECROBINDER -EncounterId KNIGHTS_ELITE -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId RADIANT-PEARL-ENTRY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id NATIVE-CHOOSE-OPEN-GATE --character-id SILENT --encounter-id CHOMPERS_NORMAL --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CALCULATED-GAMBLE-ORDERED-DISCARD --character-id SILENT --encounter-id CHOMPERS_NORMAL --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FROZEN-LIGHTNING-CHANNELS --character-id DEFECT --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id REPORT-ROUND-IMBALANCED --character-id NECROBINDER --encounter-id BOWLBUGS_NORMAL --initial-player-hp 200 --initial-player-max-hp 200 --clear-player-hand --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id SECOND-WIND-REPORT-ROOT --character-id IRONCLAD --encounter-id DECIMILLIPEDE_ELITE --initial-round-number 3 --initial-player-turn-number 3 --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id GROUP-DEBUFF-REACTIVE-DRAW --character-id NECROBINDER --encounter-id CHOMPERS_NORMAL --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CONSTRUCT-REAPER-ARTIFACT --character-id NECROBINDER --encounter-id CONSTRUCT_MENAGERIE_NORMAL --initial-round-number 3 --initial-player-turn-number 3 --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FIXED-PREFIX-POTION-POLICY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FIXED-PREFIX-TURN-OUTCOMES --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id MIRRORED-HOOK-FILTER --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FROZEN-ROOT-LISTENERS --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CALCULATED-HISTORY-FREEZE --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id VOID-FORM-TURN-CHOICES --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FLATTEN-MUSIC-BOX-ENTRY --character-id NECROBINDER --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id SEEKER-ORDERED-OPTIONS --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id RITUAL-TEMPORARY-STRENGTH --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id NOXIOUS-RAMPART-ORDER --character-id IRONCLAD --encounter-id TURRET_OPERATOR_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id EVIL-EYE-EXHAUST-HISTORY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id BLOCK-SPEC-CARD-PLAY-IDENTITY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id ROUTE-ADOPTION-LIFETIME --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id PAELS-LEGION-FINISHED-REFERENCE --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id END-TURN-RISK-LOSS-ACCOUNTING --character-id DEFECT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id MAKE-IT-SO-FULL-HAND --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CARD-COST-IDENTITY-CONTRACT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id DAMPEN-REACTIVE-ROCKET-PUNCH --character-id DEFECT --encounter-id KNIGHTS_ELITE --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id DAMPEN-REACTIVE-MELANCHOLY --character-id NECROBINDER --encounter-id KNIGHTS_ELITE --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id RADIANT-PEARL-ENTRY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
```

## 社区批次 B013 回归夹具（2026-10-03，Refs #172）

夹具命令、运行结果与未验证项见[历史卷32](archive/testing/volume-32.md)。

## PR #203、#204 合并验收（2026-10-04）

场景 runId、真实 CLR 工具结果、失败记录与未验证范围见[历史卷32](archive/testing/volume-32.md)。

## 性能研究分支

既有组件、药水、魂枢及未达标原型的合同与历史结果见[历史卷18](archive/testing/volume-18.md)。PR #207最新上游整合、逐次ABBA、23根品质回归及NoGC波动/超时限制见[当前验收](performance/pr207-upstream-0494-integration-20261005.md)。

## 手牌上限状态一致性（PR #224）

原版根／Fork 合同与贡献者的动态上限及耗时证据见[历史卷21](archive/testing/volume-21.md#手牌上限状态一致性pr-224)。
