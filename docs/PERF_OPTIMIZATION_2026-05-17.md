# wpa-mcp 性能优化分析（2026-05-17）

> 本文档面向后续接手实现的人（含 Codex / 其他 AI 协作者）。所有结论来自一次真实使用场景下的实测，trace 上下文与代码定位均已写入文档，可冷读直接动手。
>
> 适配的代码不变量在 `CLAUDE.md` 和 `CONTRIBUTING.md` 中描述；本文不重复，但在每项改动里引用对应的约束（PerfView parity、source-based parser、`TraceCache.Get` 强制路径等），实现时**必须**回查那两份文件。
>
> **2026-05-17 修订**：初版经 Codex review 后已修订以下章节 —
> §1.1 修复条件 A 的逻辑错误 + 降级 B；§1.4 删除"必须 source-based parser"误判 + 并行从 P0 降到 P2；
> §1.5（新增）MCP `ReadOnly`/`Idempotent` 注解；§2.A 说明 `StackSourceSample` 无 PID 字段带来的缓存设计取舍；§3 P0/P1 重排。
> 详细变更见文末「修订记录」。
>
> **2026-05-17 实施状态**：P0 中的工具注解、interrupt 缺栈 capability/warning、`cpu_top_functions_batch` 单 pass multi-PID、`diagnose_high_wait` soft budget/partial return、`list_processes wait_ratio` 低 CPU 分母降噪已落地并通过全量测试。Opus post-review 后补了 batch per-PID 失败隔离、post-wait budget 语义澄清、wait_ratio 自适应低 CPU 下限，以及 interrupt 缺栈 warning 改按缺栈耗时占比触发。

---

## 0. 实测背景

- **Trace**：`C:\Users\admin3\Documents\WPR Files\LAPTOP-NL4LGTQH.08-11-2025.15-40-35.etl`
- **元信息**：durationUs `247,638,241`（约 247.6 s），eventCount `8,631,351`，eventsLost `0`，processCount `328`
- **能力位**：`hasCpuSamples`、`hasCSwitch`、`hasFileIo`、`hasDiskIo`、`hasImageLoad`、`hasHardFaults`、`hasStackWalks`、`hasReadyThread`、`hasInterrupt`、`hasThreadEvents` 均为 `true`；`hasVirtualAlloc`、`hasNetIo`、`hasRegistry`、`hasAlpc`、`hasClr*` 为 `false`
- **`_NT_SYMBOL_PATH`**：`SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols`

实测中暴露的四个具体故障：

| # | 调用 | 现象 |
|---|------|------|
| 1 | `list_processes orderBy=wait_ratio top=25` | `RuntimeBroker` pid 22964：`cpuUs=4000`、`wallUs=201,956,249`、`waitRatio=50489.06` 排到第一 |
| 2 | `diagnose_high_wait maxCandidates=8 topStacks=8 topReadyStacks=8` | 120 s MCP 默认超时未完成 |
| 3 | `interrupt_top_stacks summaryOnly=true top=20` | 第一行 `function="?!?"` 占 `exclusivePct=99.99932670814144` |
| 4 | `cpu_top_functions_batch pids=[13424,5964,25968,20548,6272,20636,4024,24168] top=15` | 单次调用 ≥ 3 分 49 秒未返回 |

候选 PID 信息（用于复现）：

- 13424 `ffmpeg`（CPU 43.5 s，wall 26.9 s — 多核满载）
- 5964 `MsMpEng`（CPU 39.9 s，trace-resident）
- 25968 / 20548 `IRMA Provider`（CPU 34.5 s / 24.8 s）
- 6272 `MSPCManagerService`、20636 / 24168 `AliedrSrv`、4024 `quark`

---

## 1. 四个具体优化项

### 1.1 `list_processes` 的 `wait_ratio` 排序键失真

**位置**

- `src\WpaMcp\Analyzers\ProcessProjection.cs:32` — 计算公式
- `src\WpaMcp\Tools\MetaTools.cs:589-624`（工具入口）、`:670-673`（排序键）
- `src\WpaMcp\Output\Records.cs:175-183` — `ProcessRow.WaitRatio` 字段
- `tests\WpaMcp.Tests\MetaToolsTests.cs:293-314` — 现有但**覆盖不到**本 bug 的测试

**根因**

```
WaitRatio = wallUs / cpuUs
```

唯一的兜底是 `TraceResident`（`ProcessProjection.cs:19`），判定窗口仅 1 ms。本次 pid 22964 寿命约 202 s 但起始时间距 trace 开头 > 1 ms，未命中"贴边"判定；同时被采样到的 CPU 仅 4 ms（采样精度噪声）。`202 s ÷ 4 ms ≈ 50,489` 直接霸榜。本质问题：

1. **分母塌缩**：`cpuUs` 可以小到采样误差量级
2. **residency 判定过严**：1 ms epsilon 对 247 s 的 trace 太小

**修复（按成本递增）**

> **2026-05-17 修订**：初版的 A `cpuUs < 50_000 && (wallUs - cpuUs) < 10_000` **不会触发** bug case。本 case 的 `wallUs - cpuUs ≈ 202 s`，远大于 10 ms 阈值，AND 拼接后永远 FALSE。
> 同样初版的 B（把 epsilon 放大到 `max(50 ms, traceDuration × 1%)`）对 247 s trace 也只到 ~2.47 s，而 pid 22964 起始时间在 trace start 后 ~45 s，仍不满足"贴边"判定 — 这个进程**本来就不是 `TraceResident` 语义要覆盖的**。

| 选项 | 文件 | 改动量 | 验收 |
|---|---|---|---|
| A（修订）. 排序键加 **单一 CPU 下限**：`cpuUs < 50_000`（50 ms）时排序返回 `double.NegativeInfinity`。理由：CPU 采样精度有限，4 ms 以下基本是噪声，作为 `WaitRatio` 分母无意义 | `MetaTools.cs:672` | < 10 行 | 新测：构造 pid 寿命 200 s、cpuUs=4 ms 的进程不进 top-25；构造 pid 寿命 10 s、cpuUs=100 ms 的进程仍能正常排序 |
| B（独立 hygiene，不修本 bug）. 把 `TraceResident` 的 epsilon 从 1 ms 改为 `max(50 ms, traceDurationUs * 0.01)` | `ProcessProjection.cs:19` | < 5 行 | 与 A 解耦：epsilon 放大解决的是"贴边但有亚毫秒 jitter"，不是本 bug case。可单独 PR 或合入 A |
| C. **语义级**：新增 `BlockedRatio = (wallUs - cpuUs) / wallUs ∈ [0, 1]` 字段（**新加字段**，不改 `WaitRatio`），并在 `orderBy=blocked_ratio` 时使用 | `ProcessProjection.cs:32`、`Records.cs:175-183`、`MetaTools.cs:589` 工具描述 | 中等 | 按 `CLAUDE.md` invariant #6（DTO 不可变）走"加字段"路径；旧字段保留兼容 |

**推荐**：A 立即做，单独修本 bug；B 后续 hygiene PR；C 作为长期方向，由 `orderBy=blocked_ratio` 提供有界排序键。

**新测必须覆盖的 case**（`MetaToolsTests.cs`）：
- pid 寿命 ≈ traceDuration 但起始时间 > epsilon 的进程（本次 bug case）
- pid 寿命短但 `cpuUs` 大的进程（不应误排到底）
- 全 trace 没有 CSwitch 时 `WaitRatio` 行为

---

### 1.2 `diagnose_high_wait` 超时

**位置**

- `src\WpaMcp\Tools\DiagnoseTools.cs:237-612` — 工具入口和候选循环
- `src\WpaMcp\Analyzers\WaitAnalysis.cs` — `WaitAnalysis.Analyze`（line 267-272 调用，`top=int.MaxValue`）
- `src\WpaMcp\Analyzers\BlockedTimeStackAnalysis.cs:60`、`:137-150` — 候选栈分析
- `src\WpaMcp\Analyzers\ReadyThreadStackAnalysis.cs` — 唤醒栈分析

**根因 — 单次调用的 trace pass 数量**

最坏情况（`maxCandidates=N`，每个候选都有 CSwitch 栈且 `SchedulerWaitPct ≥ 0.5`）：

```
1   次 WaitAnalysis.Analyze          (全 trace 聚合，top=int.MaxValue)
N   次 BlockedTimeStackAnalysis      (per 候选：raw → LookupWarmSymbols → BuildNormalized → CallTree)
N   次 ReadyThreadStackAnalysis      (per 候选)
─────────────────────────────────
1 + 2N 次全 trace pass
```

本次 N=8 → **17 次** pass，每次 8.6 M event 至少 8-15 s，且 `LookupWarmSymbols` 会触发 PDB 下载。

**`topStacks` / `topReadyStacks` 只影响最后的 `Take(top)`，完全不降低走读成本** —— 这点应该写进工具描述。

**修复（按成本递增）**

| 选项 | 改动 | 收益 |
|---|---|---|
| A. 默认 `maxCandidates: 5 → 3`，且 ReadyThread 分支由独立参数 `includeReadyStacks=false` 显式开启 | `DiagnoseTools.cs:237-260` 入参 + 默认值 | 多数 trace 在 120 s 内可完成 |
| B. 候选循环并行化（`Parallel.ForEach`） | `DiagnoseTools.cs:380-603` | 4 倍提速（受 CPU 核数限制） |
| C. **结构级**：`BlockedTimeStackAnalysis` 支持 PID 集合一次走读 | 新接口 `AnalyzeMultiPid(IReadOnlyCollection<int> pids)` 返回 `Dictionary<int, BlockedStackResult>` | `1+N` → `~2` 次 pass，N=8 时降到 12% 时间 |
| D. 工具入参加 `timeBudgetMs`（默认 100_000），内部预算耗尽时按已完成候选返回并加 warning | 跨 A/B/C | 不再被 120 s 硬砍 |

**并行化的并发安全注意**

- `LookupWarmSymbols(50, ...)` 会写 `TraceLog` 的共享 module 符号缓存。并行前必须先在循环外 hoist 一次（一次 trace pass，把所有候选 PID 涉及的模块都 warm 完），或者在调用处加锁。
- `MutableTraceEventStackSource` 是 per-call 实例，安全。
- `trace.Events` 枚举本身是只读的，安全。

**新测**：
- 同时给定 5 个候选 PID（已合成 fixture），断言完成时间 < 60 s（在 CI windows-latest 上）
- 给定空 `pids` 时不应 walk trace（早返回）
- `includeReadyStacks=false` 下 wall-clock 至少减半

**PerfView parity 注意**：`BlockedTimeStackAnalysis` 改 multi-PID 时仍需保持每个 PID 的输出与现有单 PID 完全一致（同样的 `MutableTraceEventStackSource` 归一化、同样的 CallTree 折叠）。CLAUDE.md invariant #3 适用：no-stack 样本归 `?!?`，未解析地址归 `module!?`。

---

### 1.3 `interrupt_top_stacks` 99.99% 落到 `?!?`

**位置**

- `src\WpaMcp\Analyzers\InterruptStackAnalysis.cs:110-153` — DPC/ISR 事件处理
- `src\WpaMcp\Tools\InterruptTools.cs` — 工具入口
- `src\WpaMcp\Analyzers\TraceCapabilitiesDetector.cs:109-110` — 当前只统计全局 `hasStackWalks`
- `src\WpaMcp\Output\Warnings.cs` — warning 类型集合
- `src\WpaMcp\Analyzers\StackSourceTopN.cs:118-126` — `?!?` 合成根的来源（CLAUDE.md invariant #3 (a)）

**根因**

1. 默认 WPR profile（含 `CPU.light`、`CPU.verbose`）**不会**为 `PerfInfoDPC`/`PerfInfoISR` 启用 stack-walk。要拿到中断栈必须显式在 `.wprp` 里给 `PerfInfoDPC`/`PerfInfoISR` 加 stack-walk 标记。
2. 没有栈时，`data.CallStackIndex()` 返回 `Invalid`，按 PerfView-parity invariant 全部归入合成 `?!?` 根。
3. 当前 warning 只在 `totalCount == 0` 或符号解析率 < 0.8 时触发；"有样本但全无栈"这种最常见的中断诊断场景**没有任何提示**。
4. 即使没有栈，`DPCTraceData` / `ISRTraceData` 仍携带可用字段：`ProcessorNumber`、`Vector`（ISR）、`Routine`（驱动函数地址），可以走 `trace.CodeAddresses.ModuleFile(Routine)` 得到驱动名 — 但当前实现完全没利用。

**修复（按成本递增）**

| 选项 | 改动 | 验收 |
|---|---|---|
| A. 新 warning：检测 `noStackSampleCount / totalCount >= 0.5` 时发 `InterruptStacksMissing` warning，附 WPR profile 配置建议 | `Warnings.cs` 加类型 + `InterruptStackAnalysis.cs:153` 附近触发点 | 当前 fixture 上能看到 warning，文字给出"在 .wprp 里给 PerfInfoDPC/PerfInfoISR 加 stack-walk"的指引 |
| B. 新能力位：`hasInterruptStacks`，独立于全局 `hasStackWalks` | `TraceCapabilitiesDetector.cs:109-110` 加专门检测，`Output\Records.cs` 的 `Capabilities` 加字段 | `load_trace` 返回包含该位；客户端在调用前就能决定 |
| C. **结构级**：栈缺失时 `interrupt_top_stacks` 自动 fallback 到 `(ModuleFile(Routine), ProcessorNumber, Vector)` 三元组聚合，返回行的 `function` 字段填驱动名+函数地址 | 新分析模式（建议另起方法 `InterruptStackAnalysis.TopRoutines`），工具按 capability 自动选 | 当前 fixture 上能看出哪个驱动 routine 在哪个 CPU 上耗时 |

**推荐**：A 立即做（小改动、解决"无效输出"误导）；B 一起做（capability 透明化）；C 按用户反馈推动。

**`?!?` 不要误删**：那是 CLAUDE.md invariant #3 (a) 要求的合成根，PerfView 也这么处理。fallback 模式应该是**额外**的行，而不是取代 `?!?`。

---

### 1.4 `cpu_top_functions_batch` 串行+重复构建

**位置**

- `src\WpaMcp\Tools\CpuTools.cs:70-100`（batch 入口）、`:88-98`（串行 `foreach pids`）
- `src\WpaMcp\Analyzers\CpuAnalysis.cs:82-114`（`BuildNormalized`，PerfView parity 关键路径，CLAUDE.md invariant #3）
- `src\WpaMcp\Core\TraceCache.cs:60-65` — 只缓存 `Lazy<TraceLog>`，**没有缓存 stack source**

**根因 — 单次 batch 调用的成本**

每个 PID 独立做：

1. 全 trace 8.6 M event 走读（`CpuAnalysis.cs:95`，未按 PID 在 event source 层过滤）
2. 独立 raw `MutableTraceEventStackSource` 构建
3. `LookupWarmSymbols` 查 PDB（**重复**：每个 PID 对相同模块都查一次）
4. `BuildNormalized` 构建第二个 `MutableTraceEventStackSource`（PerfView parity invariant）

N=8 → **8 次全 trace walk + 8 次 PDB 查询 + 16 次 stack source 构建**。batch 退化为"循环调单 PID"，完全没体现 batch 语义的目的。

**修复（按收益排序）**

> **2026-05-17 修订**：初版把 `Parallel.ForEach` 列为选项 A（"最便宜"）属于优先级错误。
> 单 pass multi-PID 比并行 N 次 full-trace walk 更快、风险更低（多线程并发 mmap 访问会抢页；`LookupWarmSymbols` 写 `TraceLog` 共享符号缓存，hoist 不是一行能做的）。**并行化降到 P2，作为单 pass 落地后的微优化**。

| 选项 | 改动 | 收益 |
|---|---|---|
| A（首选）. **单 pass multi-PID**：走一次 `trace.Events`，按 PID 路由到 `Dictionary<int, RawStackSource>`，然后串行 normalize + CallTree（normalize 占时短，先不并行） | `CpuAnalysis.cs` 加 `TopFunctionsMultiPid` 方法；`CpuTools.cs:88` 改调新方法 | 8 PID 从 8 次 walk 压到 1 次，预计 6-8× |
| B（横向，见 §2.A）. `TraceCache` 缓存 raw stack source | `TraceCache.cs` + cache key 设计 | 二次调用近零成本；但对**单次** batch 不直接降本 |
| C（延后，P2）. 在 A 之上把 normalize/CallTree 并行化 | `CpuAnalysis.TopFunctionsMultiPid` 内部 `Parallel.ForEach` | 再 ~2-4×，但 `LookupWarmSymbols` 共享符号缓存需要先加锁或预热 |

**实现 A 的注意事项**

- 必须保持 CLAUDE.md invariant #3 的两个 PerfView parity 约束：
  - (a) no-stack 样本归 `?!?` 合成根
  - (b) 未解析地址通过第二个 `MutableTraceEventStackSource` 折叠为 `module!?`
- `LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, ...)` 在所有 PID 的 raw stack source 都填好后调一次共享版本即可。但因为 `LookupWarmSymbols` 是按 source 内的样本统计 warm 模块，multi-PID 单 pass 时需要 per-PID source 各自调一次 — 或者额外建一个 union raw source 用作 warm 检测、然后在每个 per-PID source 上跳过 lookup（实现 trade-off，benchmark 选择）。
- 必须走 `TraceCache.Get`（CLAUDE.md invariant #1）。
- **不**需要改成 source-based parser pattern：当前 `CpuAnalysis.cs:95` 是直接 `foreach (var ev in trace.Events)` + `is not SampledProfileTraceData`，没有挂 `KernelTraceEventParser`，CLAUDE.md invariant #2 不适用。multi-PID 版本保持相同的直枚举即可（每事件多一个 `Dictionary<int, RawStackSource>` 路由）。

**验收**

- 单元测：8 PID batch 的输出与 8 次单 PID 调用逐字段一致（除可能的浮点尾部）
- perf 测：在 `small_cpu.etl` 上 8 PID batch 用时 < 单 PID 调用 × 2
- PerfView parity：在 `small_cpu.etl` 上跑 `cpu_top_functions_batch`，每个 PID 的 top-10 与 PerfView `SaveCPUStacksAsCsv` 同一 PID 的 top-10 满足 invariant #3 的接受标准（7/10 名字重叠、±10% 样本数、±15pp 百分比、grand total 在 ~1% 内）

---

### 1.5 缺失的 MCP 工具注解 — UX 问题（2026-05-17 新增）

**位置**：所有 `src\WpaMcp\Tools\*Tools.cs` 中的 `[McpServerTool]` 标注

**现状**（grep 验证）：

```
McpServerTool 标注：58 处
ReadOnly / Idempotent 标注：58 处（实施前只有 inspect_trace / diagnose_high_wait 2 处）
```

**症状**：本次实测会话里每次工具调用都会出现一条 `⚠ Automatic approval review approved (risk: low, authorization: unknown)` —— 这是 MCP 客户端的自动审批走默认路径产生的提示噪声。`load_trace`、`list_processes`、`cpu_top_functions_batch`、`diagnose_high_wait`、`interrupt_top_stacks` 全部触发，因为它们**没有自我声明是只读 / 幂等的**。

**根因**：MCP C# SDK 的 `[McpServerTool]` 支持 `ReadOnly = true` / `Idempotent = true` / `Destructive = false` 等注解，遵循审批策略的客户端会据此跳过低风险只读工具的审批流程。当前所有工具都退到默认（保守）类别。

**分类**：

| 类别 | 工具 | 注解 |
|---|---|---|
| 只读且幂等 | 全部 `*_top_*`、`*_caller_callee`、`*_analysis`、`list_processes`、`list_*`、`inspect_trace`、`thread_lifetime`、`memory_resource_analysis`、`process_create_timing`、`image_load_timing`、`find_marker`、`hard_fault_by_file`、`net_connections`、`diagnose_*` | `ReadOnly=true, Idempotent=true` |
| 只读且可能解析符号 | 所有会调 `LookupWarmSymbols` 的 stack-resolving 工具，以及组合调用这些工具的 `diagnose_*` | `OpenWorld=true`（`_NT_SYMBOL_PATH` 的 `SRV*...*https://...` 可触发 PDB 下载） |
| 改服务进程状态（符号路径，非幂等） | `set_symbol_path`（默认 `append=true`，重复调用会继续追加） | `ReadOnly=false, Idempotent=false` |
| 改服务进程状态（符号路径，幂等） | `add_symbol_server`（内部去重） | `ReadOnly=false, Idempotent=true` |
| 改服务进程状态（trace cache） | `load_trace` | `ReadOnly=false, Idempotent=true`（重复加载同一 trace 返回相同结果） |

**改动量**：58 个工具方法加属性，纯元数据；注意符号路径工具不是只读。

**验收**：

- `dotnet build` 通过
- 在 MCP inspector 里调用 `tools/list`，确认每个工具的 `annotations` 字段包含正确的 `readOnlyHint` / `idempotentHint`
- 在支持审批策略的客户端（claude-desktop / cursor 等）里复跑本次场景，确认只读工具不再触发审批噪声

**为何是 P0**：用户每次会话都感受得到，改动量极小，且没有任何兼容性风险。Codex 在 review 时把它列为新增 P0 — 完全合理。

---

## 2. 横向优化（多工具共享）

### 2.A 在 `TraceCache` 上加 stack source 缓存层 — 高收益但比初版描述的更复杂

> **2026-05-17 修订**：初版称这是"最大单点收益、2-3 天可落地"过于乐观。Codex 指出两个实际约束：
> ① `StackSourceSample` 结构（`StackSourceTopN.cs:118-126` 验证）**没有 PID 字段**，PID 编在 stack 的进程帧里，filter-by-PID 不是 O(1)。
> ② 状态机分析器（`BlockedTimeStackAnalysis`、`ReadyThreadStackAnalysis`）的缓存对象不是 raw events，而是 CSwitch 状态机走完后的 "blocked intervals attributed to stacks"，缓存设计本质不同。
>
> 因此本节重写为两种缓存形态，并按"分析器类别"分阶段落地。

**现状**：`src\WpaMcp\Core\TraceCache.cs:60-65` 只缓存 `Lazy<TraceLog>` + capabilities + metadata，LRU 默认 2。`MutableTraceEventStackSource`（构建成本占单次工具调用的 90%+）每次都从头做。

#### 形态 A：whole-trace raw source + stack-walk PID 过滤（PerfView 路线）

```
Key: (canonicalPath, sourceKind, options-hash)
Value: RawStackSource (LookupWarmSymbols 已跑过)
PID 过滤: 在使用时 walk 每个 sample 的 stack，匹配 "Process X (pid)" 帧
```

**适用**：仅适用于"事件 → 样本"直映射的分析器（**CPU samples、ImageLoad、HardFault、HeapAlloc、VirtualAlloc、Registry、NetIo、Alpc、Interrupt**）。这些分析器只是把事件 1:1 转成带 metric 的 sample，PID 是 stack 的一部分，不参与状态机。

**优点**：跨工具调用共享同一份 raw source（`cpu_top_functions` + `cpu_caller_callee` + `cpu_precise_analysis` 同 trace 同 PID 三次调用，仅第一次有构建成本）。

**缺点**：filter-by-PID 不是 O(1)。每次按 PID 投影都要 walk 每 sample 的 stack 找进程帧。在 8.6 M sample 的 trace 上 stack-walk 成本仍比"重做 raw source"低 1-2 个数量级，但不是"近零"。

#### 形态 B：per-(path, sourceKind, pid) 缓存

```
Key: (canonicalPath, sourceKind, pid OR null-for-whole-trace, options-hash)
Value: RawStackSource (LookupWarmSymbols 已跑过)
```

**适用**：同上的"直映射"类分析器，且明确知道访问模式是"重复查同一 PID"。

**优点**：filter 是 O(1)（不需要 stack-walk）。

**缺点**：同 trace 多 PID 时多份缓存，内存压力线性增长；如果用户访问的是"看 3 个 PID 各一次"，比形态 A 还差。

**结论**：实践中**形态 A + LRU** 几乎总是更好。形态 B 仅在"perf 测发现某些热路径反复同 PID"时局部启用。

#### 状态机分析器（BlockedTime / ReadyThread）— 单独设计

`BlockedTimeStackAnalysis` 的工作流是：

```
CSwitch events → 状态机 (per thread, on-CPU vs blocked) → blocked intervals → AddSample(blockingStack, durationUs)
```

可缓存的对象不是"events"，而是"完整跑完状态机后的 RawStackSource"。这意味着：

- 形态 A 仍然能用：cache key 是 `(path, "BlockedTime", options-hash)`，value 是状态机已跑完的 `RawStackSource`，PID 过滤同样靠 stack-walk
- 但**首次构建的代价比 CPU samples 高**：要走完 CSwitch 状态机
- 如果同一 trace 上对 5 个不同候选都要 blocked stacks（`diagnose_high_wait` 主路径），缓存一次受益 5 次

`ReadyThreadStackAnalysis` 同理。

#### 生命周期与配置

- 与 `TraceLog` 绑定：trace 从 cache evict 时，对应 stack source 一并清掉
- 内存压力大：每个 raw source 估计 50-300 MB；需要新加 `WPAMCP_STACK_CACHE_MB` 上限（默认 `1024`），超过时按 LRU 逐项 evict
- mtime invalidation：复用 `TraceCache` 现有逻辑

#### 分阶段落地

| 阶段 | 范围 | 估时 |
|---|---|---|
| 阶段 1 | 形态 A + LRU + 内存上限基础设施；先接入 CPU samples 这一种 `sourceKind` | 3-5 天 |
| 阶段 2 | 把其他"直映射"分析器（ImageLoad、HardFault、HeapAlloc、VirtualAlloc 等）逐个接入；每个分析器一次小 PR | 每个 0.5-1 天 |
| 阶段 3 | 接入 BlockedTime / ReadyThread（PR 需要 PerfView parity 全量回归） | 3-5 天 |

**收益分布**：`cpu_top_functions` / `cpu_caller_callee` / `cpu_precise_analysis` 同 trace 同 PID 连续调用 — 近 10× 提速。`diagnose_high_wait` 5 候选共享 CSwitch raw source — 5× 提速。但**对单次 batch 调用本身没有降本** —— 那一块由 §1.4 单 pass multi-PID 解决。

### 2.B 抽出 `MultiPidStackAnalysis<TEvent>` 公共基类

下列分析器都有"per-PID 全走读"的成本结构：

- `Analyzers\BlockedTimeStackAnalysis.cs`
- `Analyzers\CpuAnalysis.cs`（CPU samples）
- `Analyzers\FileIoStackAnalysis.cs`
- `Analyzers\HeapAllocStackAnalysis.cs`
- `Analyzers\VirtualAllocStackAnalysis.cs`
- `Analyzers\PageFaultStackAnalysis.cs`
- `Analyzers\ReadyThreadStackAnalysis.cs`

抽公共基类 `MultiPidStackAnalysis<TEvent>` 接口大概：

```csharp
abstract class MultiPidStackAnalysis<TEvent> where TEvent : TraceEvent
{
    public Dictionary<int, RawStackSource> Analyze(
        TraceLog trace,
        IReadOnlyCollection<int> pids,
        AnalysisOptions options);

    protected abstract void Subscribe(TraceEventSource source, Action<TEvent> handler);
    protected abstract long GetSampleMetric(TEvent data);  // CPU=1, IO=size, alloc=bytes...
}
```

复用方：所有 `*_batch` 工具、`diagnose_high_wait`、未来的"多进程对比"工具。

### 2.C 长任务的进度/早停/超时自适应

**现状缺口**

- MCP 默认 120 s 超时硬卡，工具内部对此无感知
- 没有任何工具发 `notifications/progress`
- 没有 `timeBudgetMs` 入参
- 工具不会根据 `load_trace` 时已知的 `eventCount` 做 cost preflight

**建议基础设施（新文件 `Core\AnalysisBudget.cs`）**

```csharp
sealed class AnalysisBudget
{
    public TimeSpan Remaining { get; }
    public void ReportProgress(double fraction, string stage);  // -> MCP progress notification
    public bool ShouldEarlyExit { get; }                         // 预算耗尽信号
}
```

每个 diagnose / batch 工具入口接受 `timeBudgetMs`（默认 100_000，留 20 s 给序列化），传 `AnalysisBudget` 到分析器，在每个候选/PID 完成后 check `ShouldEarlyExit`，超预算时按已完成部分返回并带 warning。

### 2.D 部分能力缺失时的输出降级

1.3 的 interrupt fallback 是这类问题的一个实例。同类问题分布在：

- `wait_analysis`：依赖 CSwitch keyword
- `virtual_alloc_*`：依赖 VirtualAlloc keyword
- `net_*`：依赖 NetworkTrace keyword
- `registry_*`：依赖 Registry keyword
- `alpc_*`：依赖 ALPC keyword
- `heap_alloc_*`：依赖 NT-heap keyword

**模式**：每个 stack-based 工具入口

1. 调 `TraceCapabilitiesDetector` 检查所需的 capability + stack walk 位
2. 若缺，返回"降级聚合"（不需要栈的等价信息）+ 一条"想要栈，请用 X 的 WPR 模板"的 warning
3. 在 `docs\WPR_PROFILE.md` 里维护工具 → 需要的 keyword 表

### 2.E CLI 基准回归

**位置**：`src\WpaMcp\Cli\CliRunner.cs` 已能直接吐 JSON。

**建议**：新建 `tests\WpaMcp.Perf\` 项目，跑固定 fixture（`small_cpu.etl` + 一个 multi-PID 合成 fixture）上的 wall-time 阈值断言。CI 矩阵加一个 `perf` job，回归时直接看到"今天的改动让 `cpu_top_functions_batch` 8 PID 从 240 s 降到 30 s"。

注意：当前 xUnit assembly 级并行**已被禁用**（`tests\WpaMcp.Tests\AssemblyInfo.cs`，理由见 CLAUDE.md "Test fixtures" 节），perf 项目要独立 csproj 避免冲突。

---

## 3. 实施优先级

> **2026-05-17 修订**：按 Codex review 重排 — ① 新增 §1.5 (MCP 注解) 为 P0 第一；② §1.4 单 pass multi-PID 从 P1 提到 P0（最大单点性能收益）；③ §1.2 预算/部分返回从 P2 提到 P0；④ §1.1 删除错误的 B 选项；⑤ 并行化（原 §1.4 选项 A）整体降到 P2。

按"影响 × 1 / 成本"排序：

### P0 — 本周可做（小改动、立即解锁）

1. **§1.5（新增）**：所有只读工具加 `[McpServerTool(ReadOnly=true, Idempotent=true)]` 注解
   - 状态：已实施；`set_symbol_path` 标为 `ReadOnly=false, Idempotent=false`，`add_symbol_server` / `load_trace` 标为 `ReadOnly=false, Idempotent=true`
   - 验证：全量 `dotnet test WpaMcp.sln -c Release --no-restore`
   - 收益：用户每次会话都受益（消除自动审批噪声）
2. **§1.3 选项 A + B**：interrupt 缺栈 warning + `hasInterruptStacks` capability
   - 状态：已实施；新增 `TraceCapabilities.HasInterruptStacks` 和 DPC/ISR 缺栈 warning
   - Post-review 修正：warning 阈值按缺栈 interrupt time 占比判断，而不是事件数量占比
   - 真实 ETL 验证：见 `tests/manual/interrupt_missing_stack_time_validation.md`；本地 relogged trace 证明 `noStackCount=4/11` 但 `noStackUs=1958/1969us` 时会触发 warning
3. **§1.4 选项 A**：`cpu_top_functions_batch` 单 pass multi-PID
   - 状态：已实施；新增 `CpuAnalysis.TopFunctionsMultiPid`，batch 只遍历一次 `trace.Events`
   - Post-review 修正：单 PID 符号/归一化失败不再拖垮整个 batch，warning 保留 `pid {p}:` 归属
   - 注意：实测大 trace 的 30-40 s 目标仍需用原始 ETL 复测确认
4. **§1.2 + §2.C 特化**：`diagnose_high_wait` 加 `timeBudgetMs` 入参 + 候选按预算分配，超预算时按已完成部分返回 + warning
   - 状态：已实施；默认 `timeBudgetMs=100_000`、`includeReadyStacks=false`，默认 `maxCandidates` 保持 5 以避免静默降低候选覆盖
   - 语义：预算只限制 wait_analysis 之后的 stack fan-out；已返回证据不降精度，未完成部分通过 `Warnings` / `NotConcluded` 标记为 partial
5. **§1.1 选项 A（修订版）**：`MetaTools.cs` 加自适应近零 CPU 排序下限
   - 状态：已实施；这是排序降噪，不改变 `WaitRatio` 字段含义
   - Post-review 修正：下限从固定 50ms 收窄为 `max(5ms, wallUs * 1e-5)`，避免误伤真实 high-wall/low-CPU IPC 等待

### P1 — 下两周（结构性、跨工具收益）

6. **§2.A 阶段 1**：`TraceCache` + LRU + 内存上限基础设施 + CPU samples 接入
   - 预计：3-5 天
7. **§1.3 选项 C**：interrupt 缺栈时 fallback 到 driver+CPU 聚合
   - 预计：1 天
8. **§1.2 选项 C**：`BlockedTimeStackAnalysis` 单 pass multi-PID
   - 预计：2-3 天

### P2 — 持续投入（多工具共享 / 微优化）

9. **§2.A 阶段 2 / 3**：其他直映射分析器接入缓存；BlockedTime / ReadyThread 接入
10. **§2.B**：抽 `MultiPidStackAnalysis<T>` 基类
11. **§2.C**：`AnalysisBudget` 基础设施 + 工具入参全面接入
12. **§2.D**：所有 stack-based 工具的能力降级表
13. **§2.E**：`tests\WpaMcp.Perf\` 项目 + CI perf job
14. **§1.1 选项 B**：`TraceResident` epsilon 放大（独立 hygiene PR，不修本 bug）
15. **§1.4 选项 C**：在单 pass 之上并行化 normalize / CallTree（先 benchmark 验证收益再做）

---

## 4. 实施时必读的仓库约束

实现前必读 `CLAUDE.md`（项目根目录）和 `CONTRIBUTING.md`。本次涉及的关键不变量索引：

- invariant #1（`TraceCache.Get` 强制路径）：§1.4 / §2.A 直接相关
- invariant #2（`KernelTraceEventParser` 必须挂到 `trace.Events.GetSource()`）：**仅在使用 parser 时适用**。§1.4 选项 A 保持当前 `trace.Events` 直枚举，**不**受此约束。若未来重构改用 parser 接入则需要遵守。
- invariant #3（CPU PerfView parity：`?!?` 合成根 + `module!?` 折叠）：§1.3 / §1.4 直接相关
- invariant #5（符号路径走进程环境变量，分析器直接读 `_NT_SYMBOL_PATH`）：§1.4 的 `LookupWarmSymbols` hoist 不要改这条路径
- invariant #6（`Output\Records.cs` DTO 不可变，加字段不改字段）：§1.1 选项 C 受此约束
- invariant #7（每个工具方法走 `TraceCache.Get` + `Core\Validation.cs`）：所有新工具入参遵守

测试相关：

- `tests\WpaMcp.Tests\AssemblyInfo.cs` 禁用了 assembly 级并行，不要改
- `.etl` fixture 默认 gitignored，新增 fixture 需在 `tests\WpaMcp.Tests\fixtures\capture_all.ps1` 里加捕获脚本（Administrator PowerShell 才能跑）
- `perfview_gcevents.etl` 是 committed 第三方 fixture，不要重新生成

PerfView parity 验证：见 `tests\manual\perfview_compare.md`，CPU/wait/image-load/IO/alloc/network/registry/ALPC/interrupt/CLR 工具改完都要跑一遍。

---

## 5. 当前没有但应该加的代码标记

仓库当前 `src\WpaMcp\` 下没有任何 `TODO`/`HACK`/`FIXME` 注释（grep 验证）。建议本次落地的修复在代码处用统一形式：

```csharp
// PERF(2026-05-17): single-pass multi-PID; see docs/PERF_OPTIMIZATION_2026-05-17.md §1.4
```

便于后续回查决策依据。

---

## 6. 修订记录

### 2026-05-17（v2，Codex review 后修订）

按 Codex 对初版的 review 应用以下修改：

| 章节 | 变更类型 | 说明 |
|---|---|---|
| §0 | 新增 | 头部加 2026-05-17 修订摘要 |
| §1.1 | 修复 | 初版选项 A 的条件 `cpuUs < 50_000 && (wallUs - cpuUs) < 10_000` AND 拼接逻辑错误，bug case `wallUs - cpuUs ≈ 202 s ≫ 10 ms` 永远不触发；改为单一 CPU 下限 `cpuUs < 50_000` |
| §1.1 | 降级 | 初版选项 B（`TraceResident` epsilon 放大）对 bug case 无效（pid 22964 起始时间 ~45 s ≫ 2.47 s epsilon），降为独立 hygiene PR，从 P0 移到 P2 |
| §1.1 | 重写 | 选项 C 改为"加 `BlockedRatio` 新字段"路径，符合 invariant #6 |
| §1.4 | 删除 | 实现注意里"必须用 source-based parser pattern" — 验证 `CpuAnalysis.cs:95` 是直枚举 `trace.Events`，未使用 `KernelTraceEventParser`，invariant #2 不适用 |
| §1.4 | 降级 | `Parallel.ForEach` 从选项 A（"最便宜"）降到选项 C（P2），理由：mmap 抢页 + 符号缓存共享写需要先加锁；单 pass multi-PID 比并行 N 次 walk 更快且更安全 |
| §1.5 | 新增 | Codex 指出大多数工具缺 `ReadOnly`/`Idempotent` 注解（实施前 58 个 `McpServerTool` 中只有 2 个 annotation），导致本次会话每次工具调用都有审批噪声 — 列为 P0 第一 |
| §2.A | 重写 | 初版"2-3 天最大单点收益"过于乐观。验证 `StackSourceSample` 无 PID 字段（`StackSourceTopN.cs:118-126`），改为两种 cache key 形态对比 + 状态机分析器单独处理 + 三阶段落地 |
| §3 | 重排 | P0/P1/P2 整体调整：MCP 注解 → 1st P0；CPU batch 单 pass 从 P1 升 P0；diagnose 预算/部分返回从 P2 升 P0；并行化降 P2 |
| §4 | 修正 | invariant #2 的适用范围说明（"仅在使用 parser 时适用"） |

### 接受 Codex 但保留我的判断的点

- **§2.A 缓存仍值得做**：Codex 描述偏负面，但事实是即使 PID 过滤靠 stack-walk，比"重做 raw source"仍快 1-2 个数量级。重写为分阶段落地，没有取消。
- **§1.2 默认 maxCandidates 调整保留**：Codex 提示"不要只靠这个"，但作为预算/部分返回的并行手段（不是唯一手段），仍有价值。已在 P0 第 4 项里和预算一起做。
