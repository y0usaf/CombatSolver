# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

0.51.4 的战斗逻辑与选牌续接修复见[历史卷 27](archive/development/volume-27.md)，玩家说明见[0.51.4 更新日志](releases/0.51.4-RELEASE_NOTES.md)。

## 下一版本（开发中）

### 公平决策的候选比较平铺派发

- 定位：一次公平决策 = 每个样本一次整搜（`Normal` 12 个样本、`Thorough` 16 个）+ 每个候选在每个样本里再各搜一次。旧比较阶段以样本为链：样本自己第一次比较之后的 `(候选数-1)` 次求解挂在同一链上，样本宽度条链同时跑时这些轮次被串行化，而在宽机器上样本宽度本身没有吃完逻辑核心。比较预算（与单次求解软预算同值）按墙钟判定，所以链越深、机器越慢，进入评分的候选越少。
- 改动：`FairDecisionPlanner.Decide` 把 `(样本, 候选)` 作为独立作业平铺派发。作业之间只共享写回槽与计数（每样本计数改为 `Interlocked.Increment`）；每个作业仍执行同样的求解、同样的软预算、同一个比较预算闸门，并写回同一个槽。
- 并发：`min(作业数, max(样本宽度, min(逻辑核心*3/4, 样本宽度*2)))`。整机全开会让每个求解在自己预算内少搜节点：32 并发在 `late-ir-decmil-t2-a` 第二轮把路线改差（损血 7→13），24 并发在同样的 8 次复跑里路线全部保持；比样本宽度更窄的机器继续沿用原并发。
- 未改动：采样数、候选数、beam、节点预算、软预算、评分、终局排序与准入；不读取隐藏或实时状态。公平性合同 `OFFLINE_HARNESS_FAIR_OBSERVATION_CHECKS=1` 100 项通过，`OFFLINE_HARNESS_FAIR_DETERMINISM=1` 0 次不一致。
- 离线 harness 数字（`late-run` 夹具、`DOTNET_gcServer=1`、单宿主串行、同根/同政策/同预算；A=改动前，B=改动后）：
  - `Thorough`（生产默认 16/5/beam 24/2500 节点/10 秒），`t1-a`+`t2-a` 各两轮：A 决策均值 9480 ms（8792/10167）、最大 17658 ms、求解 967 次；B 均值 7754 ms（6671/8837）、最大 16770 ms、求解 941 次；每次复跑损血都是 57。比较阶段占比越大收益越明显。
  - `Normal`（12/4/20/1500/5 秒）`holdout` 五个位置、每侧三次交错复跑：A 均值 4323 ms、p90 11456 ms、最大 12779 ms、168.6 s、1132 次求解；B 均值 4383 ms、p90 10454 ms、最大 12325 ms、170.9 s、1149 次求解——差异在观测漂移内，损血每次复跑都是 157，计划在 15 次位置复跑中 14 次逐项相同。
  - `Normal` `tune` 四个位置（比较阶段占比更大）：A 均值 4232 ms、p90 8283 ms、最大 13671 ms、93.1 s、747 次求解；B 均值 3393 ms、p90 7747 ms、最大 9747 ms、74.7 s、760 次求解；损血 210 与计划 8/8 完全一致。
- 未验证：可见游戏内的帧时间与体感延迟（本机并发满载，宿主外的其他 Mod/进程会同时压缩求解深度）；多核更少的机器只按公式保持原并发，未实测。

### 自动开关只由玩家关闭，界面移入顶部栏

- 关闭路径审计：`FULL_AUTO_STOP reason=combat_end_turn` 在真实战斗日志中反复出现在预测胜利的那一回合（`SolverController.StartFullAutoDeployment` 的预测结束回合判断），是「自动自己关掉」的直接原因；死亡回合、重算战损上升和实机收尾风险同一形状。四个自动停机条件现在默认关闭，只在无人测试显式打开时生效。
- 开关不再被搜索失败（旧 `PotionPolicyUnsatisfiedException`）、部署失败、原生选牌失败或「自动搜索已暂停」的后续搜索请求关闭；这些情况保留开关、跳过该回合、把回合交回玩家，并记录原因。开启开关时若当前状态不可求解，也保持开启并记录跳过原因。
- 开关从 End Turn 按钮上方移到原生顶部栏、Map 按钮左侧，纹理与字体取自原生 End Turn 按钮与主题；关闭为蓝、开启为绿、开启且出错或未能行动为红。Ctrl+F9 切换，Ctrl+F10 打开只读状态面板（当前状态、上次决策与耗时、最近事件）。
- 新增 `SolverAutoStatus`（Runtime）记录状态、跳过、出错与停止原因，事件同时写入游戏日志的 `[CombatSolver/Auto]` 前缀，便于从 `godot.log` 直接 grep；`SolverOverlayHost` 独占热键，`SolverStatusPanel` 只消费只读快照。
- 开关状态在战斗之间保持：`BeginCombat` 仍按 `autoPlay` 恢复，战斗结束的会话重建不再把开关当作关闭信号。
- 验证：Release 构建 0 警告 0 错误；`tools/inspection/verify-refactor-boundaries.sh` 通过；离线 harness 的 fair-play v2 同种子对照 SILENT/NIBBITS（victory，损血 19，8 次决策）与 FOOL/NIBBITS（victory，损血 9，6 次决策）改动前后完全一致。顶部栏位置、三种颜色与热键的真实画面仍需可见游戏验收。

### 战斗界面收敛为一个自动开关

- `src/UI` 从 26 个文件、约 8.2k 行收敛为 `SolverOverlay`（创建与定位）、`SolverAutoToggle`（开关）、`SolverText` 与 `English.json`。路线行、动作栏、动作胶囊、循环组、路线摘要、内存条、设置页、问题包对话框与 HUD 详情按钮随显示快照一起删除。
- 自动状态由 `autoPlay` 持久化，战斗开始时据此设置 `FullAutoEnabled`；关闭时不再于回合开始自动搜索，因此没有界面时也不会做无人可见的计算。
- 策略值固定在 `SolverSettings.Capture`：速度 `Thorough`（16 采样/5 候选/beam 24/2500 节点/10 秒软预算）、药水 `Smart`、首领战损策略 `ProgressionFirst`、无战损目标停止、成长预算默认、遗物计数开启、多宽度组合搜索开启、新颖性与前两回合探索关闭、部署跟随游戏速度且无额外延迟。
- 搜索结束通知、旧版界面主题/透明度/尺寸、问题包联系人等仅服务于旧界面的设置键删除。
- `SolverController` 删除显示投影、消息格式化与界面状态刷新；`MonitorCombatPresence` 改用 `_combat.State == null` 作为重新接入边界。公共契约 `OVERLAY-VISIBILITY-LIFECYCLE`、`UI-LOCALIZATION`、`UI-COMPACT-QOL`、`UI-PRIORITY-FEEDBACK`、`ROUTE-ROW-REUSE`、`REQUEST-BUDGET-DISPLAY`、`MEMORY-DISPLAY-CONTRACT`、`DISPLAY-NAME-SUMMON`、`DEPLOYMENT-PRESENTATION-TURN` 与 `tools/testing/checks/LoopDisplayChecks` 删除。
- 验证：Release 构建通过；`tools/inspection/verify-refactor-boundaries.sh` 通过（新的 auto 开关边界）；文档与覆盖检查通过。真实战斗中的开关位置、点击与动画仍需可见游戏验收。
