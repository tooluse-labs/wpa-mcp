# wpa-mcp — 能力缺口对比（vs WPA / PerfView）

> **当前状态（2026-08-02）：** 本文是人类可读的 delta ledger，不是 runtime
> catalog。validated development model 当前包含 61 个 active tools、51 个 declared
> capabilities、15 个 goals、15 个 workflows；其中 10 个 capability 被明确映射到
> `evaluator.declared_gap`。下表还包含尚未进入 catalog 的更广泛 WPA/PerfView 候选。
> `eng/capabilities.v1.json`、`eng/tool-contracts.v2.json`、Active Catalog validator
> 和 runtime Resource 才是权威。

> Working notes，不是 RFC。server 要完整暴露 declared capability map 以降低选择成本，
> 同时完整暴露 evidence gap，避免 LLM 把 unsupported/unmeasured 能力变成结论。该地图只
> 对 wpa-mcp 声明的 surface 完整，绝不代表整个 WPA/ETW universe；未列出表示
> `unknown_not_catalogued`，不表示已证明不存在。
>
> **2026-05 当时的文档集合分工**——三份历史规划文档，顺序约束的流水线：
>
> - **`CAPABILITY_GAPS.md`（本文）** ——**补什么**（稳定的清单）
> - **`MCP_SURFACE_DESIGN.md`** ——**怎么补**（Tool / Resource / Prompt、三层架构、annotation 分级）
> - **`MCP_IMPLEMENTATION_TASKS.md`** ——**优先级 + 具体任务**（P0–P3、Scope / Work / Acceptance）
>
> 本文识别的缺口在 `MCP_SURFACE_DESIGN.md` 完成表面分类、`MCP_IMPLEMENTATION_TASKS.md` 完成优先级排序之前**不可执行**。
>
> 早期 brainstorm 文档已归档到 `docs/archive/` 以供溯源。
>
> **修订说明：**
> - **v8 (2026-08-02)**：按 lean/full-contract 双投影对齐 discovery 与 release gap：静态完整的 61-tool catalog、250,000-byte lean `tools/list`、content-addressed contract lookup，以及仅 Contract 2.0 的结果兼容性；删除并不存在的 legacy-adapter 与具名客户端矩阵 release blocker，并通过已审查、自动校验的 artifact 关闭 corrected-active-baseline blocker。
> - **v7 (2026-08-01)**：按 validated 60-tool/51-capability catalog 对齐历史 inventory；记录 10 个 manifest-declared gap；把 CPU Precise、memory resources、system metadata、provider counts、capture diagnostics 从错误的“完全缺失”改成“core 已覆盖、剩余边界明确”。
> - **v6 (2026-08-01)**：加入 contract rollout 与客户端证据 release gap，并使用精确的机器可读 blocker code；历史 analyzer inventory 不变。
> - **v5 (2026-05-15)**：删除 4 层 punchlist；优先级现在只在 `MCP_IMPLEMENTATION_TASKS.md` 里。本文档只保留稳定的能力清单（A/B/C/D + 不该补 + UI vs data 误判 + 反模式 callout）。文档集合清理同时归档了 `OPTIMIZATION.md`。
> - **v4 (2026-05-15)**：把 punchlist 改写为 4 层优先级结构；明文加入反模式警告；重新定位文档集合为顺序约束；A-4 与 B-5 加风险注脚。
> - **v3 (2026-05-15)**：code review 后收紧多处事实陈述；新增 A 表三个缺口。
> - **v2 (2026-05-15)**：经 `grep` 核实修正 v1 的 time-window 过度描述；"采集侧"拆成"执行（不补）"vs"质量诊断（in-scope）"；新增 D. Trace lifecycle / preprocessing 小节。
> - **v1 (2026-05-15)**：初稿盘点。

## 分类框架

把 WPA / PerfView 能力剥掉 UI 壳子后，剩下大体四类：

- **A. 数据维度**——看的是什么
- **B. 切片 / 聚合**——怎么切
- **C. 元数据**——trace 自身的信息
- **D. Trace lifecycle / 预处理**——分析前后对 trace 文件本身的操作

### Runtime 权威与发现路径

- Tools-only client 调 `list_capabilities`；支持 Resource 的 client 跟完
  `wpa://capabilities/server`、`wpa://tools/server`、`wpa://workflows/server`
  链接的全部页。
- server 暴露静态完整的 active tool set。`tools/list` 是 aggregate 不超过 250,000
  bytes 的 lean projection：保留完整 input schema 和 content-addressed Contract 2.0
  URI/hash metadata，但不内嵌深层 output schema。历史约 2.5 MB inline catalog 只是
  before measurement，不是当前 discovery 成本。
- 支持 Resource 的 client 从 `wpa://contracts/tools/{toolName}/{sha256}` 及其 page
  重组所选 schema；Tools-only client 调 `get_tool_contract(toolName, page)`。两条路径
  返回 server 用于结果验证的同一份 canonical bytes。
- MCP host/client 负责跟完协议分页，并可向 LLM 渐进注入 task-relevant descriptors；
  这不会在 server 上动态激活工具，也不存在万能 dispatcher tool。
- 选定工具后，从 `wpa://tools/{toolName}/sections` 及其所有 page 读取完整 evidence
  和 result-section 语义。gap ledger 不能替代 runtime ordering、truncation、precision
  与 conclusion boundary。
- `inspect_trace` 为一个已加载 generation 提供 Trace Evidence Map。server declaration、
  trace evidence availability 与实际 query outcome 是三种不同状态。
- 当前 10 个 manifest-declared gap 是：`symbols.configuration.path`、
  `symbols.configuration.server`、`symbols.diagnostics.metadata`、
  `scheduler.ready.causality`、`security.scanner.attribution`、
  `symbols.frame_resolution.measured`、`trace.raw_event_count.external`、
  `attribution.cross_domain.causal`、`lifecycle.trace.handle`、
  `lifecycle.trace.artifact_peak_bound`。

其中有些 gap 故意声明“缺少独立能力”，即使更窄的事实已在别处存在。例如
`prepare_symbols` 可以报告 verified readiness，load/query/unload 也已实现 handle
lifecycle；但当前 context-bound frame resolver 不可用，也没有独立的
handle-status/inventory tool。catalog 保留这些区别，避免模型把间接事实扩大解释。

---

## A. 数据维度缺口

| 缺口 | 价值 | 备注 |
|---|---|---|
| **CPU Usage Precise**（基于 CSwitch 的 on-CPU µs，区别于 Sampled 统计采样） | core 已覆盖 | `cpu_precise_analysis` 已按可重放 process/thread instance 报告精确 CSwitch on-CPU time、ready-to-run latency、per-core attribution 和 quantum/preemption count。本行作为 closure evidence 保留，不再是 open capability。 |
| **Scheduler / core / priority 分析**（CPU migration、priority inversion、更丰富 priority timeline） | 中高，部分覆盖 | `cpu_precise_analysis` 已覆盖 per-core attribution、ready latency、quantum/preemption count；`ready_thread_*` 明确只是 association evidence。专用 migration summary、priority timeline 和 mechanistic priority-inversion proof 仍缺失。`scheduler.ready.causality` 保持 declared gap，防止把 readier stack 过度解释成“谁唤醒”。 |
| **GC heap dump (`.gcdump`) 加载 + 对象引用图 + retention path** | 高 | 内存泄漏调研必备。现 `clr_gc_*` 只在 ETW 事件层，回答不了"谁还持有这堆 `byte[]`" |
| **内存资源视图**（working set、commit、private bytes、pool/handle activity） | core 已覆盖，retention gap 仍在 | `memory_resource_analysis` 已投影 `Memory/ProcessMemInfo` snapshot，以及观测到的 handle create/close、pool allocation/free delta，并明确 capture requirement 与 scope。这些 delta 不是 absolute current counter，趋势也不能证明 leak 或 retention path；`.gcdump` object graph 与权威 retained-object attribution 仍未覆盖。 |
| **Async / Task chain stitching**（CLR Task 跨线程续帧重组） | 高 | `clr_*` 工具按事件分散返回，async 调用链拼不起来；PerfView 有 task tracing 做这件事 |
| **UI 响应性 / 输入延迟 / frame pacing**（input-to-render 延迟、DWM frame pacing、compositor 卡顿） | 中高 | 用户感知性能的维度。下文的 `Window-in-focus` 只回答"哪个窗口在前台"，不回答"从按键到出画面多久"。桌面 / UI 应用调研刚需 |
| **GPU profiling**（Compute Graphics / GPU Work） | 中 | 场景窄但 GPU-bound 场景无替代 |
| **Power profiling**（CPU C-state、frequency、battery transitions） | 中 | 笔记本、移动场景刚需 |
| **Window-in-focus / 前台进程事件** | 中 | 把性能事件挂到"用户当时在看哪个窗口"上 |
| **Boot trace phase 分析**（PreSession / SMSSInit / WinLogonInit 等） | 中 | wpa-mcp 现把 boot trace 当 normal trace 看，丢失分阶段视角 |
| **Audio glitches** | 低 | 媒体场景专用 |

## B. 切片 / 聚合缺口

| 缺口 | 价值 | 备注 |
|---|---|---|
| **跨调用共享 ROI context** | 中高，部分覆盖 | per-call 半开区间 `startUs <= t < endUs` 与边界测试已广泛实现；lifecycle-relative 工具有意采用不同 scope，admitted timeline/inventory result 可以发布绑定 cursor。仍没有跨独立调用共享的 first-class immutable ROI object，因此客户端必须原样重放 window 与 identity selector。 |
| **跨 trace diff**（baseline vs regression） | 高 | 瓶颈是跨 capture 的 process-identity 匹配 |
| **灵活 group-by / pivot**（按 module / namespace / 线程池 / payload 字段重聚合） | 中高 | WPA 拖列即可，PerfView 有 GroupPats；wpa-mcp 输出 schema 固定 |
| **Aggregation mode 切换**（sum / avg / max / min / count / weighted） | 中 | 多数列锁死 sum 或 count |
| **Generic event 字段 group_by**（受限聚合 DSL，按 task / opcode / event_id / payload 字段切） | 中高 | **v1 现状描述纠错。** `generic_event_top_stacks` **已经**支持按 provider、event-name substring、pid、time window 过滤，还有 `whenBuckets` 时间直方图选项。**真正缺**的是按 event task、opcode、event_id 或 payload 字段做 group / pivot。<br/>**风险-价值注脚 (v4)**：工程风险其实低（在现有工具上扩参数，不是新工具）。不确定的是**价值**——LLM 是否真能驱动受限 pivot DSL。建议先发布最小 2-axis 版本（task + opcode），用 1–2 个真实场景验证后再扩展 |
| **Storage stack 细分**（LayoutComplete / FlushComplete / Volume Flush 分层延迟） | 中 | 现 `disk_io_*` 是合并后的粗粒度 |
| **多 stack-type 混合 CallTree**（CPU + Wait fold 在一棵树里） | 中 | PerfView 强项；wpa-mcp 必须分开查再人脑合并 |

## C. 元数据缺口

| 缺口 | 价值 | 备注 |
|---|---|---|
| **System Configuration**（OS build、CPU model/topology、core count、boot config、driver list） | core 已覆盖，source-nullable gap 仍在 | `inspect_trace` 已投影 trace-derived system metadata 与 driver summary。trace 未携带的字段保持 nullable，绝不拿 host machine 值填充；power-state timeline 仍是另一项 gap。 |
| **Per-provider trace statistics**（event count、buffer config、dropped-event provenance） | count 已覆盖，raw/session 细节仍缺 | `inspect_trace` 已报告 parser-materialized provider event count 与带 provenance 的 trace-level loss metadata。不会推断 raw external record count、provider-specific loss attribution 或完整 ETW buffer config；`trace.raw_event_count.external` 是 declared gap。 |
| **采集质量诊断**（capability evidence、stack completeness、symbol boundary） | core 已覆盖，resolution/recapture gap 仍在 | `inspect_trace` 已暴露 Trace Evidence Map、同域 stack coverage、quality warning、PDB-identity state 与 next-step boundary。`prepare_symbols` 测量 verified local readiness，但当前 context-bound frame resolver fail closed，`symbols.frame_resolution.measured` 仍是 declared gap。这些信息不能证明原始 keyword 配置，也不会自动重采。 |
| **Symbol source lookup**（返回 `file:line`） | 中 | LLM 拿到行号可对照源码 |
| **Inline frame expansion** | 中 | inline 函数现被外层吞掉，深度 drilldown 会失真 |

---

## D. Trace lifecycle / 预处理

secure core lifecycle 已实现：`load_trace` 是唯一 raw source 入口，返回
principal-scoped immutable TraceId；`unload_trace` retire handle，但不宣称 artifact
已删除。下表剩余内容是新的 artifact-producing transformation，不是 core lifecycle
的缺失部分。

| 缺口 | 价值 | 备注 |
|---|---|---|
| **`shrink_trace(path, pid_list)`** | 中高 | 把 `tools/etlshrink/` wrap 成 MCP 工具 |
| **`slice_trace(path, startUs, endUs)`** | 中 | 按时间窗 subset 导出——和 B-1 的 time-window 统一工作天然成对 |
| **`redact_trace(path, ...)`** | 低中 | 去除特定 process / image / payload 字段 |
| **Folded-stack / call-tree artifact 导出**（`*_top_stacks` 加 `format=folded` 模式） | 低中 | 不是 UI flamegraph；外部工具的导出格式。现有工具的可选模式 |
| **多 trace merge / stitching** | 低 | 偏采集端 |

---

## 明确不该补的能力

### 纯 UI 渲染（对 LLM 零价值）

- Flamegraph / Icicle / Heatmap / 时间轴图 / Bar chart **图像本身**
- 颜色 / 主题 / 字体 / 布局
- 截图、图片导出
- HTML report 生成
- 多视图 tab / 窗口管理
- UI pagination 语义（API 仍然需要 `top` 上限 / cursor / 截断元数据——这是 API 设计问题，不是 UI 移植）

### 配置 / 状态持久化（弱必要）

- `.wpaProfile` / `.perfView` 视图配置文件——tool call 是 request / JSON 导向
- 用户偏好 / 历史会话恢复

### 采集执行（不在 scope）

需要 admin 权限的写接口：

- 启停 ETW session
- 实时 provider enable / disable
- Heap snapshot 触发
- `wpr -start` 等价物

`wpr` / PerfView 拥有这些职责。采集*质量诊断*（C 表）是独立的 in-scope 能力。

---

## 常见误判：UI 形式 vs 数据能力

1. **Time-range selection** 是 **time-window filtering** 的交互形式。已经在大多数工具上以 `startUs` / `endUs` 参数暴露（B-1）
2. **Cross-view linking** 是"多个 analyzer 共享同一时间窗 / 进程过滤上下文"——见 B-1 中"共享 ROI context"那半
3. **Drill-down** 是"上一查询的结果能作为下一查询的输入"。wpa-mcp 现让 LLM 手动重传 `module!func` 字符串，是 drill-down 的弱化版本

---

## Contract rollout 与客户端证据缺口

以下是 release blocker，不是被隐藏的 analyzer capability：

| 缺口 | 机器可读状态 | 后果 |
|---|---|---|
| 完整 0.5.x 弃用窗口与 usage review | `release_blocked:no_reviewed_full_0.5.x_window_or_usage_telemetry_evidence` | 在仓库内审查这份证据之前，1.0 不得删除 raw-path compatibility；环境变量不能绕过发布历史门禁。 |
| artifact materialization 物理峰值 | `release_blocked:retained_quota_only;single_materialization_checkpoint_budget;opaque_converter_transient_peak_unproven` | retained-store quota 与 checkpoint 不能证明 opaque converter 的瞬态磁盘峰值。发布必须有通过的 `artifact-materialization-budget.v1.json`，不能把推测冒充 hard cap。 |

这里没有“经审查的 legacy projection”缺口：此前没有 released version 把 Phase 0
snapshot 建立为受支持的 result wire contract，因此 0.4.x 的结果契约只有 Contract 2.0。
`legacy` fail closed 是为了防止错误标记；缺少未发布的 adapter 不是 release blocker。
具名客户端的 paging/token/cache 测量仍是有价值的 compatibility observation，但不是
全局发布门禁；只读第一页的 host 依然不兼容，因为 host pagination 是必需协议行为。

corrected active-tool、DTO/stdio、lean-payload、pagination 与 full-contract registry
baseline 已在本次变更中生成和审查，自动测试把它们绑定到 active manifest/profile。
因此原 `corrected_active_contract_baselines_not_release_approved` blocker 已关闭；这不等于
提前宣称所有无关的 full-suite/package gate 均已通过。

本次启动选择的真实 profile 位于 `wpa://runtime/profile`；缺少 release gate
绝不能伪装成 analyzer 已支持。详见 `CLIENT_COMPATIBILITY.zh-CN.md` 与
`CONTRACT_MIGRATION.zh-CN.md`。

---

## 优先级

**反模式：** 不要从上到下照表补功能，也不要为了省 prompt 隐藏已有专用工具。候选必须
先具备稳定 CapabilityId、answered/not-answered question、required evidence/stack、
scope/cost/symbol requirement、maximum relationship、runtime evaluator 或显式 gap
evaluator、tool/section contract、benchmark 与 compatibility decision，之后才能由批准的
task/ADR 排期。

历史 `MCP_IMPLEMENTATION_TASKS.md` 不是当前 backlog。accepted architecture 与 Phase
0–7 的实施/release gate 见 `MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md`
以及 ADR 0002–0005。

---

最后修订：2026-08-02 (v8)。
