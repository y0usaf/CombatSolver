# 离线搜索宿主

不启动 Godot、在普通 .NET 9 进程里跑 CombatSolver 搜索的宿主。

完整说明（构建、单根与批量用法、plan 字段、产物、`Evaluate` 与 `Coordinator` 的口径差别、
Godot 绕过表、已知限制、验证证据）见 [`docs/OFFLINE_SEARCH_HARNESS.md`](../../../docs/OFFLINE_SEARCH_HARNESS.md)。

```
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release

# 单根
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
    --request <请求.json> --label R1 --out <产物目录> --profile VeryHigh

# 批量
python3 tools/search/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> --workers 3

# 两份结果逐字段比
python3 tools/search/OfflineSearchHarness/compare_results.py \
    --left <A>/runs --left-prefix A --right <B>/runs --right-prefix B --out cmp.json
```

文件：

| 文件 | 作用 |
|---|---|
| `Program.cs` | 命令行、分步时间线、产物落盘 |
| `AssemblyBootstrap.cs` | 运行期解析 `sts2` / `RitsuLib` / `CombatSolver` |
| `GameBootstrap.cs` | 游戏静态状态初始化与**全部** Godot 绕过（类头有表） |
| `ModRuntime.cs` | 模组侧初始化、离线会话、搜索正确性补丁、一次求解 |
| `GeneratedScenarioSetup.cs` | 生成场景开局（走模组自己的注入方法） |
| `OfflineCombat.cs` / `MainLoopContext.cs` | 建战斗、推进到玩家第一回合、消息循环 |
| `OfflineLocalization.cs` / `MemorySaveStore.cs` | 空表本地化、内存存档层 |
| `MemorySampler.cs` | 峰值托管堆与工作集采样 |
| `run_plan.py` / `compare_results.py` | 批量运行、逐字段比较 |
| `late-run/run_late_run.py` | 晚期固定状态夹具运行与 A/B 对比（`positions/`、`sets/`） |

`--request` 也接受本仓库的固定装备/初始战斗状态夹具，不再强制 generatedScenarioPath；仍不执行 fixture 的 expected 断言。

## 晚期决策夹具

`late-run/` 保存 9 个固定初始战斗状态，取自 2026-11-03 真实 16–18 秒决策的 Act 2 Decimillipede 精英战：27 张 Ironclad 牌、三段 `REATTACH_POWER`、最多六个玩家 Power，`initialPlayerHp` / 能量 / 手牌 / 弃牌 / 消耗 / 敌人生命与 Power 都按日志逐项恢复，隐藏洗牌仍由公平观测重新采样。`sets/tune.json`（4 个）与 `sets/holdout.json`（5 个）分开；holdout 只用于打分，不用于挑选参数或阈值。

```bash
python3 tools/search/OfflineSearchHarness/late-run/run_late_run.py --set holdout \
    --dll <CombatSolver.dll> --out .local/late-run/head --workers 4 --decision-turns 2
python3 tools/search/OfflineSearchHarness/late-run/run_late_run.py \
    --compare .local/late-run/head .local/late-run/candidate
```

脚本用 `OFFLINE_HARNESS_FAIR_PLAY=v2`、`Normal`、5000 ms 软预算，并在 `--decision-turns` 个玩家回合后停止；`--workers 4` 让 4 个宿主同时争用 CPU，复现真实整机负载下的决策尾部（单进程同时跑 12 个样本）。产物 `summary.json` 记录逐局决策毫秒、`hpLost`、`solves` 与 `FAIR_CANDIDATE_INVALID` 计数。

2026-11-03 的全部十个实时战斗日志里，`FAIR_CANDIDATE_INVALID` 共 265 次、候选求解共 2187 次（12.1%），全部是 `FairSolveTimeoutException: fair sample solve exceeded 12000 ms`，集中在三次战斗（119/405、99/415、47/193）。当时安装的构建没有把样本搜索夹到固定速度档：`WEIGHTS beam=135 budget_ms=300000`，而单次求解的软预算只有 3 s，软预算又只在回合层之间检查，强制前缀的比较求解可以越过它，直到 4×（12 s）硬上限被取消。`0f556c66` 起主线的 `FairPlannerSettings.Profile` 已把 beam/nodes 夹到 20/1500、软预算 5 s，本夹具在当前主线上的无效格降到 3/385（0.8%），且全部是 `PotionPolicyUnsatisfiedException`（比较一个用掉药水的候选后，Smart 政策找不到合规路线；该格不可用，不是超时）。日志里中间段 7 HP 的状态会在离线模拟触发未镜像的 `AfterDeath`（`REATTACH_POWER`）并 SIGSEGV，因此未纳入夹具。

搜索预算、预设与药水政策以宿主 CLI 为准，例如成长循环须显式传 `--potion-policy RequireAtLeastOne`。恢复快照、追加怪物、自定义规则不支持并明确拒绝；特殊 ScenarioId 的原生合同请使用无人游戏测试。`--stop-at-zero-loss` 启用生产零战损达标停止；`--verify-incremental` 对小根逐步完整回放，不用于性能测量。见[循环对照](../../../docs/archive/performance/loop-optimization-20260921.md)。

循环固定边界集使用 `run_loop_boundaries.py`：串行双 DLL A/B，120 秒进程上限，拒绝覆盖已有结果，显式检查 suite 的质量／结构条件并比较完整路线。按 suite 中各 case 的配置运行 Evaluate 或 Coordinator；4096 动作检查使用请求级共享额度，Coordinator 同时核对请求日志与总计数。计数优先从新 `TurnLayerTimeBudgetStops` / `TurnLayerNodeBudgetStops` 读取，旧 DLL 从完整诊断日志回退解析，二者与总数及日志相互核对。局部／全局时间边界或证据不足标为 Inconclusive、退出 2，原始差异／断言观察仍保留，不能用于有效性能均值；可比较差异、断言或宿主失败退出 1，等价退出 0。不自动把不同路线判作质量退化；见[19 根边界与预算审计](../../../docs/archive/performance/loop-boundaries-20260921.md)。
