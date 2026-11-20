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

### v3 公平求解器：确定性预算、`WATERFALL_GIANT` 爆发与评估修正

- 预算：`InformationSetSearch` 的批次闸门原先以全部作业为最低数量，软预算从不生效（Thorough 单次决策 20–36 s，6 场整局超时）。现在 `(候选, 样本)` 作业按样本优先排列，首个样本对全部候选必跑；其后按节点数累计判定：只计入「按序号前缀累计节点 < `SoftTimeBudgetMs × 8`」的作业，并行完成顺序不影响结果，决策仍确定。根段求解、续搜回合与段求解另受 `SoftTimeBudgetMs × 2` 硬墙钟上限约束（只在极端局面触发；截断的作业整体丢弃）。
- 续搜：样本内续搜的段求解直接展开抽牌/随机分支（`expandReveals`），不再为同一回合另开两次子搜；同哈希的下回合状态原地替换，不再重复入列；开放队列只保留剩余弹出次数能取到的节点。
- `WATERFALL_GIANT`：`AboutToBlow`/`Explode` 阶段、以及无数字无限生命的存活敌人不再算作已击杀；段求解只在没有存活/复活敌人时判胜；来袭伤害包含强制 `EXPLODE_MOVE` 伤害，`SteamEruptionPower` 层数与已准备的爆发伤害按待承受威胁扣分。
- 结束回合选择：结束回合候选在各样本中的 `PlayerTurnEnd`/`EnemyTurn` 选择按同一键聚合，选均值最高的一组写回提交的 `EndTurn.TurnStartChoices`；`PlayerTurnStart` 选择仍留到抽牌后再定。
- 重算：执行端改用 `FairObservation.DescribePlanInvalidation`——单一可命中敌人时的目标 RNG 前进、以及抽到已知顶牌不再中断计划；随机目标是否无害改为同时检查动作前后的可命中敌人数（原先只看动作后，打死其一会被误判为无害）。`DescribeReveal` 的描述合同不变。
- 分配：去掉段求解根节点的多余 Fork、出队重复估值与提前哈希；评估/哈希热路径改为索引遍历并单次扫描 Power 列表；`StateHash` 加入敌人当前/强制意图。
- 评估：补充约 30 种己方 Power 与敌方仪式/人工制品/镀层/荆棘/`SteamEruptionPower` 的估值；状态/诅咒牌按剩余回合的预期废抽扣分；重放价值按「本牌被抽到的概率」折算（原先随克隆牌数量无上限增长，`real-09` 会因此拖到回合上限）。
- 验证（离线 harness，`OFFLINE_HARNESS_FAIR_PLAY=v3`、Thorough、8 并发、32 个实战局面）：完成 26→31（剩余 `real-06` 为 `MadScience` 镜像遇到 `TinkerTimeType=None` 抛异常，非超时），胜 22→26；同一批 26 场胜 22→23、平均损血 33.50→29.38；全部决策 p99 20002→10947 ms、最大 36300→11208 ms，平均分配 2157→1715 MB、p90 4501→3179 MB；每决策节点均值 35.5k→28.6k、最大 197k→60k。`OFFLINE_HARNESS_NEW_SOLVER_CHECKS` 13 组、`OFFLINE_HARNESS_FAIR_OBSERVATION_CHECKS` 全部通过；`OFFLINE_HARNESS_COOP_EVAL` v3 胜场 8/10→9/10。
- 未验证：可见游戏内的实机耗时；本轮基准运行时宿主另有游戏进程占用约 7 核，墙钟数字偏保守。

### 联机自动模式的节奏选择：自由行动／等待队友

- 问题：联机 Auto 固定「最后出牌」——必须等每名队友结束本回合、动作队列空闲后才求解并部署。真实对局里这不像在玩牌，像在排队。
- 选择：新增 `SolverCoopPlayMode` 二值设置 `FreePlay`／`Wait`，写入 `user://combat_solver_settings.json` 的 `coopPlayMode`，随游戏重启保留。缺该字段的旧设置文件按 `FreePlay` 读取，因此自由行动是默认值；`Wait` 与旧行为逐字一致。单人战斗不受影响（节奏只在 2–4 人战斗里生效）。
- 自由行动：不做队友等待。`WaitForCoopStableBoardAsync` 直接判定棋盘可用，`SolverDeploymentSession.RequireCoopStableBoard` 为否，因此部署只受「原生动作队列空闲 + `CoopBoardStamp` 未变」约束。每次出牌前和结束回合前仍逐项比较 `CoopBoardStamp`；队友改变棋盘时丢弃剩余路线并立即重新求解（同一回合内重排，而不是等队友准备）。没有任何复制牌或代打路径：动作仍只来自本地玩家的手牌／药水与 `EndPlayerTurnAction`，卡实例由 `FindCardForDeployment` 按出现序号匹配。
- 等待队友：与原行为一致——`SetFullAuto`、战斗重新挂载、命中过期结果、丢弃路线和回合开始的自动搜索都先等稳定棋盘。
- 立即生效：已在等待中的 `WaitForCoopStableBoardAsync` 每帧重读节奏；玩家切成自由行动后它在下一帧返回，挂起的搜索或部署继续，不需要再点一次 Auto。`SetCoopPlayMode` 只负责持久化与刷新。
- 界面：顶部栏新增「自由行动／等待队友」按钮（在「执行本回合」左侧），`Ctrl+F11` 同义；状态面板显示当前节奏。`Ctrl+F7`／`Ctrl+F8`／`Ctrl+F9`／`Ctrl+F10` 语义不变。
- 验证：本 0.111 副本上虚拟双人 `COOP-FREE-PLAY-EXECUTE`（自由行动：队友仍在行动时先出本地牌、队友中途改棋盘后丢弃路线并在同回合重排、重排后把该回合剩余的本地手牌打完（`CoopFreePlay:TeammateChangeMidTurn:ReplannedLocalActions`，无重复卡实例、无代打）、`Ctrl+F8` 只出本地牌且 Auto 保持关闭）与 `COOP-AUTO-EXECUTE`（等待节奏：阻塞、切自由行动解除、切回再阻塞、稳定根、丢弃余下路线、单回合）均 Passed，队友改变棋盘由夹具注入：探测确认单进程虚拟跑局无法让其他玩家出牌（Auto 关闭时注入并原生出牌 exit 139，只注入不出牌 Passed），所以「Auto 部署途中真实队友出牌」仍属未验证项，留给真实联机。`SMOKE-001` 单人第 1 回合结束（HP 80/80）；0.113 副本同批 2／4 人与单人也都 Passed。跑局停在夹具第 2 回合终点，战斗未结束。真实 Steam 双人房间仍未验证。重跑入口见[测试矩阵](TEST_MATRIX.md#联机自动模式节奏)。

### 公共分支 0.111 的兼容构建

- 用途：Steam 公共分支 v0.111.1（以及仍在该版本上的队友）使用本分支联机；0.113 的 `fair-mode` 主线不受影响。分支名 `fair/0.111-public`，基线取 0.113 合并前的 fair 提交 `b38fda76`，再拣选 0.113 合并后的 9 个 fair 提交。
- 差异：该分支没有 0.113 才引入的 `RunModel`／Dread／Blitz／Fool 箭系统与 `TemporaryThornsPower`。拣选时补回 0.113 合并带进调用侧的 `ContinuationStamp.AppendPets`，把 `SolverController` 的部署 `queuedAction` 提到分支外，虚拟跑局改用 0.111 的 `RunState.CreateForNewRun` 参数（无游戏模式／隐藏局参数），并删去 10 项 0.113 独有的无人测试断言（Fool／Dread／Blitz／Aim／模式隔离）。
- 清单：`CombatSolver.json` 的 `min_game_version` 为 `0.111.1`，依赖 RitsuLib ≥ 0.6.0（实测 0.6.14，compat 分支 0.111.0）。
- 验证：RitsuLib 0.6.14 与 CombatSolver 同时加载，CombatSolver 76/76 补丁应用成功、0 失败，无版本或引用错误；`SMOKE-001` 单人 1 回合结束（HP 80/80）；`COOP-AUTO-EXECUTE` 虚拟双人 Passed（等待队友稳定根、首次出牌后棋盘变化丢弃余下路线、单回合执行只出本地两张牌且 Auto 保持关闭）。
- 未验证：真实 Steam 联机房间、网络延迟与队友目标辅助牌；测试用私有副本仅把 ELF 解释器指向本机 glibc（NixOS 无 `/lib64/ld-linux-x86-64.so.2`），游戏文件与 Mod 均与交付副本逐字节相同。

### 多人联机自动模式与单回合执行

- 行为：`CanSolve`、搜索根和战斗开始恢复允许 1–4 名玩家；Auto 只在本地玩家的战斗里工作，绝不会代打队友手牌。「等待队友」节奏下，多人回合开始后先等待，直到每名队友都已准备结束回合、原生动作队列空闲且没有结束回合阶段（与问题包稳定检查共用 `CombatStability.IsPlayerTurnStable`），才在当前稳定根上求解并部署本地手牌；「自由行动」节奏见上节。
- 状态：`ContinuationStamp` 的实时/预测文本为其他玩家追加公开的 HP／最大 HP／格挡；`CoopBoardStamp` 另含每名玩家的回合、阶段和可见状态。搜到结果后每次出牌前、结束回合前都比较该文本；队友改变棋盘时丢弃剩余路线，Auto 重新等待稳定根，单回合执行则停止并把回合交回玩家。
- 单人回合执行：顶部栏新增“执行本回合”按钮，`Ctrl+F8` 同义。它关闭 Auto，立即搜索并部署当前回合（可复用已有同根结果或进行中的搜索），结束后保持 Auto 关闭。原来 `Ctrl+F8` 的性能录制内存现场改为 `Ctrl+F7`。
- 联机建模（廉价口径）：根捕获沿用实时多人缩放后的敌人 HP；预测召唤怪沿用 `ScaleMonsterHpForMultiplayer`；敌人攻击按原版“每个存活玩家都是目标”的语义同时结算队友（只评分本地玩家）；`MultiplayerScalingModel` 的格挡倍率与 `ShouldScaleInMultiplayer` 的首次 Power 施加按原版公式还原；队友未来出牌、指向队友的辅助牌和非伤害怪物效果仍不建模。战损账本、历史计数和录像观测统一取本地玩家。
- 验证：虚拟双人/四人 `COOP-AUTO-EXECUTE` 原生无人测试 Passed（等待队友、稳定根、首次出牌后改棋盘丢弃余下路线、`Ctrl+F8` 只出本地两张牌且 Auto 保持关闭）；单人 `BETA-DEPLOY-IRONCLAD-SENTINEL` Passed（第三回合结束，HP 80/80）；离线 harness 双人搜索取得非空本地路线；Release 构建 0 警告 0 错误、结构门禁通过。真实 Steam 双人房间、网络延迟、队友目标辅助牌和四端 ENet 仍未验证。

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
