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

当前没有待记录的行为改动。

### 战斗界面收敛为一个自动开关

- `src/UI` 从 26 个文件、约 8.2k 行收敛为 `SolverOverlay`（创建与定位）、`SolverAutoToggle`（开关）、`SolverText` 与 `English.json`。路线行、动作栏、动作胶囊、循环组、路线摘要、内存条、设置页、问题包对话框与 HUD 详情按钮随显示快照一起删除。
- 开关放在原生 End Turn 按钮上方（右下角），命中区域只覆盖按钮自身；无原生按钮时退回屏幕右下角固定边距。
- 自动状态由 `autoPlay` 持久化，战斗开始时据此设置 `FullAutoEnabled`；关闭时不再于回合开始自动搜索，因此没有界面时也不会做无人可见的计算。
- 策略值固定在 `SolverSettings.Capture`：速度 `Thorough`（16 采样/5 候选/beam 24/2500 节点/10 秒软预算）、药水 `Smart`、首领战损策略 `ProgressionFirst`、无战损目标停止、成长预算默认、遗物计数开启、多宽度组合搜索开启、新颖性与前两回合探索关闭、部署跟随游戏速度且无额外延迟。
- 停止条件固定为战斗结束、死亡回合、重算战损增加和实机收尾风险复核；搜索结束通知、旧版界面主题/透明度/尺寸、问题包联系人等仅服务于旧界面的设置键删除。
- `SolverController` 删除显示投影、消息格式化与界面状态刷新；`MonitorCombatPresence` 改用 `_combat.State == null` 作为重新接入边界。公共契约 `OVERLAY-VISIBILITY-LIFECYCLE`、`UI-LOCALIZATION`、`UI-COMPACT-QOL`、`UI-PRIORITY-FEEDBACK`、`ROUTE-ROW-REUSE`、`REQUEST-BUDGET-DISPLAY`、`MEMORY-DISPLAY-CONTRACT`、`DISPLAY-NAME-SUMMON`、`DEPLOYMENT-PRESENTATION-TURN` 与 `tools/testing/checks/LoopDisplayChecks` 删除。
- 验证：Release 构建通过；`tools/inspection/verify-refactor-boundaries.sh` 通过（新的 auto 开关边界）；文档与覆盖检查通过。真实战斗中的开关位置、点击与动画仍需可见游戏验收。
