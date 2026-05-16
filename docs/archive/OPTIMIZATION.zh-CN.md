# wpa-mcp 优化方向（review draft）

> Working notes，不是 RFC，也不是已确认的路线图。基于对 `README.md` / `CONTRIBUTING.md` / `docs/ARCHITECTURE.md` / `src/` 的浏览得出，意在向 reviewer 展示候选方向，便于优先级讨论。

## 现状速览

- 单 `WprMcp` csproj，54 个 MCP tool，Windows-only（kernel TraceEvent parser 不可移植）。
- 90%+ 工具是 `top-N + stacks + caller-callee` 三件套；composite 诊断只有 `diagnose_slow_startup` 一个。
- `Core/TraceCache.cs`：默认 LRU 容量 2（`WPRMCP_CACHE_SIZE` 覆盖），每项 mmap 200 MB–1.5 GB；缓存层已有 `TraceCache.Unload(path)`，但还没有 MCP tool 暴露它。
- 每个 analyzer 在 `Analyzers/*.cs` 里各自 `source.Process()` 全量扫一遍 ETLX——同 trace 上连续三个工具调用 = 三趟全量遍历。
- `Analyzers/CpuAnalysis.cs` 跑 `LookupWarmSymbols(50, …)`，top 50 之外的模块在 caller-callee 下钻时会掉回 `module!?`。
- 符号路径走**进程级** `_NT_SYMBOL_PATH`，`SymbolService` 改的是 `Environment.SetEnvironmentVariable`；trace 已加载后再改路径，目前需要重启 server，等 `unload_trace` 暴露后才能变成 `unload_trace` + `load_trace` 流程。

---

## P0 — 收益最高，建议先做

### O1. 暴露 `unload_trace` + 设计同 trace fused parser pass

- **现状**：长会话切多个 trace 时，MCP 客户端仍只能依赖 LRU 自动驱逐，因为内部 `TraceCache.Unload(path)` 还没有暴露成工具；同时 `CpuAnalysis`、`WaitAnalysis`、`FileIoAnalysis` 等独立 `source.Process()`，毫无共享。
- **提议**：
  1. 加 `MetaTools.UnloadTrace(path)`，以 MCP `unload_trace` 暴露已有的 `TraceCache.Unload(path)`，返回是否真的移除了 cache 项。
  2. 另行设计 batched-analysis 入口：把若干 analyzer 注册的 kernel callback 合并到**一次** `source.Process()`。`cpu_top_functions_batch` 已经验证了同 trace 多 PID 合并扫一遍的价值，把这个模式抽到通用层即可。
- **工作量**：MCP wrapper 几小时；fused pass 需要重设计 analyzer 接口（一周量级）。
- **风险**：这两个任务要拆开排。`unload_trace` 是低风险增量；fused pass 跟 MCP 的 per-call 模型不天然契合，需要新的 multi-tool 入口（比如 `analyze_trace_multi`），不能直接挂到现有 54 个工具上。

### O2. baseline vs regression diff 工具族

- **现状**：所有工具单 trace。`docs/CASE_STUDIES.md` 那个 50× fork-slow 案例本质是"对比基线"，但当前要 agent 手动跑两遍再人工 diff。
- **提议**：新增三个最常用的 diff：
  - `cpu_top_functions_diff(traceA, traceB, pid, top)`
  - `image_load_top_gaps_diff(traceA, traceB, pid)`
  - `wait_analysis_diff(traceA, traceB, pid)`
- 输出应带通用的 `MetricName` / `DeltaMetric`，再加 `DeltaPct` / `NewlyAppeared` / `Disappeared`；必要时提供领域别名（`DeltaSamples`、`DeltaBlockedUs`、`DeltaGapUs`）。CPU 是 sample 指标，wait 和 image-load gap 才是时间指标，统一叫 `DeltaUs` 会误导。
- **工作量**：每个 0.5–1 天，复用现有 analyzer 即可。
- **风险**：trace 间 PID 不稳。当前 process projection 暴露 name、parent PID、start/end time、CPU、image-load count，但没有 main-module hash。要么先用现有身份字段做 fuzzy matching（image name + 启动顺序 + parent PID），要么先补稳定进程身份字段，再承诺 hash-based diff。

### O3. `*_top_stacks` token 紧凑模式

- **现状**：`Tools/MarkerTools.cs` 已经用 `mode=count_by_event` 规避 token 爆炸，但 `*_top_stacks` 一族没推广同款思路。
- **提议**：所有 stack 类工具加两个可选参数：
  - `compactStacks=true`：每条栈截到深度 N（默认 8），尾部折成 `[+K more]`。
  - `summaryOnly=true`：只返回 leaf 函数 + 计数，丢弃栈本身。
- **工作量**：1–2 天，主要在 `Analyzers/StackSourceTopN.cs` 加格式化分支。
- **风险**：新增可选参数风险低，但响应 shape 要小心。不要往 positional record 构造器里插新的必填字段；优先用可空兼容字段，或新增 compact response wrapper。

---

## P1 — 中期演进

### O4. 更多 composite 诊断工具

模式同 `diagnose_slow_startup`：先挑候选进程 → 跑 N 个相关 analyzer → 一次返回。候选：

| 工具 | 组合的 analyzer |
| --- | --- |
| `diagnose_high_wait` | `wait_analysis` + `ready_thread_top_stacks` + 同 frame 的 `wait_caller_callee` |
| `diagnose_gc_pressure` | `clr_gc_analysis` + `clr_gc_heap_stats` + `clr_alloc_top_stacks` |
| `diagnose_lock_contention` | `clr_contention_top_stacks` + 关键线程的 `wait_analysis` 交叉 |
| `diagnose_image_load_blocker` | `image_load_timing` + `image_load_top_gaps` + 大 gap 窗口内的 `wait_top_stacks` |

**工作量**：每个 1–2 天。

### O5. 进度式符号解析

- **现状**：`CpuAnalysis.TopFunctions` 调 `LookupWarmSymbols(50, …)` 是常量 50。agent 在 caller-callee 下钻到 top-50 外时频繁吃 `module!?`。
- **提议**：把"warm 集"做成 per-`TraceLog` 增量集合；每次 caller-callee 调用先把响应里**实际出现**的未解析模块加进 warm 集再解析。或者至少把 50 做成可调参数。
- **工作量**：1–2 天，需要测好 `SymbolReader` 的线程安全（CONTRIBUTING.md 提到 PerfView 共享 `C:\Symbols` 会有 PDB-lock 冲突，所以默认走 `%LocalAppData%\WprMcp\Symbols`——这里也要注意）。

### O6. 工具表面收敛

- **现状**：54 个工具，每个领域都是 top + stacks + caller-callee 三件套，schema 高度重复，对 LLM 客户端是决策疲劳源。
- **提议**：
  - **短期（低风险）**：保持 `tools/list` 静态，新增 `list_applicable_tools(path)`，或让 `load_trace` 返回一组基于 `Capabilities` 的推荐工具。这样能减少决策噪音，又不依赖动态 MCP tool 注册。
  - **中期（兼容性实验）**：如果 MCP SDK 和主流客户端确认支持动态 tool list 且不会缓存旧 schema，再按当前 trace 的 `Capabilities` 过滤 `tools/list`。
  - **长期（breaking）**：每个领域折成单工具 + `view=top_functions|top_stacks|caller_callee` + `metric=` 枚举。
- **工作量 / 风险**：推荐工具 helper 是干净收益；动态 `tools/list` 不一定对客户端安全；长期方案是 breaking change，需要兼容层和迁移窗口。

---

## P2 — 长期 / 架构层

### O7. 跨平台子集（ETLX-only 分析）

- TraceEvent 的 kernel parser 是 Windows-only，但已生成的 `.etlx` 是平台中立。
- 把 csproj 按 `docs/ARCHITECTURE.md` 已写的 deferred split 拆成 `WprMcp.Analyzers`（ETLX-only，跨平台）+ `WprMcp.Capture`（Windows-only），Linux CI runner 也能跑回归对比。
- **前置**：要有 Linux CI 的明确需求；否则收益不抵架构成本。

### O8. 重构 `Analyzers/StackSourceTopN.cs`

- 22 KB 单文件，多个 `*StackAnalysis.cs` 在重复样板（装 parser + 建 CallTree + 取 topN + when-buckets 直方图）。
- 抽 `StackAnalysisHarness<TEvent>` 后能砍 ~30% 代码，新增 metric 维度（比如 O2 的 diff）也更轻。建议跟 O6 一起做。

### O9. 性能 / Token 体积基准 harness

- `CpuAnalysis` 的 PerfView-parity 不变量目前靠人工 7/10 对照（`CONTRIBUTING.md` 那条 acceptance criteria）。
- 加 `tests/WprMcp.Bench/` 用 `small_cpu.etl` 跑 BenchmarkDotNet + 输出快照对比，CI 上对每个 PR 做时延 / token 体积回归。
- **工作量**：2–3 天搭基础，长期维护成本不低（fixture 更新会牵连快照）。

### O10. 把 `tools/etlshrink/` 暴露为 MCP 工具

- 现在 `tools/etlshrink/` 是独立 csproj。Agent 完全可以在 load 之前先调 `shrink_trace(path, pid_list)` 减少 mmap 压力。
- **工作量**：较小（拷现有逻辑封装成 `[McpServerTool]`，注意输出写到只读位置时的失败语义）。

---

## 推荐取舍

只投三个的话，按收益排：

1. **O1a**（暴露 MCP `unload_trace`）—— 最小的正确性缺口，同时修复内存控制和改符号路径后的重载指引。
2. **O3**（`*_top_stacks` token 紧凑模式）—— 让 agent 一次会话能问更多次而不爆 context。
3. **O2**（baseline vs regression diff 工具族）—— 用户价值高，但要先定好 diff schema 和跨 trace 进程匹配策略。

**建议暂缓**：

- **O7（跨平台子集）**：除非有 Linux CI 的明确需求，否则架构改动不值。
- **O1b fused pass**：继续设计，但不要让一周量级的 analyzer contract 变更阻塞低风险的 `unload_trace` wrapper。
- **O6 动态 / 长期方案**：推荐工具 helper 先拿到大部分收益；动态 tool 过滤和领域合并都等确认 tool 数量真的伤害客户端后再做。
