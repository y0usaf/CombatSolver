---
name: ui-localization
description: 修改 CombatSolver 战斗内自动开关的文案与本地化时使用，维护中英双语和显示边界；创意工坊介绍与发包另用 release-gate。
---

# CombatSolver 中英界面维护

## 当前入口

- 玩家可见界面只有自动开关 `src/UI/SolverAutoToggle.cs` 与只读状态面板 `src/UI/SolverStatusPanel.cs`，由 `src/UI/SolverText.cs` 与内嵌 `src/UI/English.json` 翻译：`zhs` / `zht` 使用中文，其他语言使用英文。当前没有独立语言开关。
- 开关文案固定为「自动模式」（英文 `Auto Mode`），位置在原生顶部栏、Map 按钮左侧，纹理与字体取自原生 End Turn 按钮和顶部栏主题；状态只由颜色区分（关闭蓝、开启绿、开启但出错或未能行动红），因此不依赖中文字符宽度，也不因语言改变宽度。
- `SolverOverlayHost` 持有 Ctrl+F9（切换自动）与 Ctrl+F10（状态面板）；面板标题、状态词、上次决策、故障原因与最近事件标题都经 `SolverText`，事件明细保持诊断原文。
- 静态文案用 `SolverText.Get`，带参数的用 `SolverText.Format`（键必须是格式串，如 `自动模式：{0} {1}`）；每次 `SolverOverlay.RefreshControls` 与 15 帧轮询都会重设开关文本，切换游戏语言后下一次刷新即生效。
- `PerformanceRecording` 是默认关闭的诊断覆盖层，仍使用 `SolverText.Get`；其键保留在 `English.json`。

## 维护边界

翻译只在 UI 层读取当前语言；Search 不引用 `SolverText`，不读取实时语言，不逐分支加载资源或做本地化。搜索状态键、结构化事件 ID、协议和原始异常保持原口径。诊断日志与系统通知保持原文。

## 验证

只改开关文案时检查 `English.json` 键是否完整、`dotnet build CombatSolver.csproj -c Release` 是否通过；需要真实排版、位置或点击效果时再启动可见游戏。只更新本 skill 时检查 frontmatter、路径和职责，不启动游戏。

输入未变且已有成功证据时复用。Headless 不证明真实排版或帧率；用户明确交由自己做交互验收时按其要求执行。
