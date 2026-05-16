# wpa-mcp 实施任务清单

> 本文把设计结论拆成可执行任务。
>
> **在文档集合中的位置：**
>
> - `docs/archive/OPTIMIZATION.md` — 已归档的头脑风暴 + 候选方向
> - `CAPABILITY_GAPS.md` — 决定**补什么**（4 层 punchlist，A/B/C/D 桶）
> - `MCP_SURFACE_DESIGN.md` — 决定**怎么补**（Tool / Resource / Prompt）、三层架构、annotation 分级
> - `MCP_IMPLEMENTATION_TASKS.md`（本文） — 决定**具体任务**，按 P0/P1/P2/P3 优先级
>
> 逻辑流：**brainstorm → what → how → do**。目标不是逐项复制 WPA / PerfView，而是在不增加 LLM 决策负担的前提下，优先补齐最影响分析正确性和可用性的能力。

## 原则

- **先做导航层，再扩能力面。** 当前已有 54 个工具，继续直接加工具会提高 LLM 选错工具的概率。
- **关键能力必须是 Tool。** LLM 需要自主调用的能力不能只放在 Resource 或 Prompt。
- **Resources / Prompts 只做增强层。** Resources 放稳定参考材料；Prompts 放人类启动的调查模板。
- **避免万能工具。** 不引入 `analyze_trace(mode=...)` 这类 catch-all 入口。
- **新增能力优先参数扩展和 composite。** 只有数据形态确实不同，才新增完整工具族。
- **扩展路由面前先度量。** 路由 helper 和 composite 应先证明能降低选错工具概率或减少调查调用轮次，再成为默认路径。
- **允许废弃，但必须有证据。** Layer-1 工具默认保持结构稳定；只有在持续低使用率且已有等价 composite / 替代路径时，才进入合并或移除评审。

## P0：MCP 使用面基础

### T0.1 修正 tool annotation 分类

- **状态：** ✅ 已完成 2026-05-15（`MCP_SURFACE_DESIGN.md` v4 + `.zh-CN.md` v4）。
- **范围：** 更新 `docs/MCP_SURFACE_DESIGN*.md`。
- **内容：** 将 `diagnose_symbols` 从 Tier C（环境配置，含 `set_symbol_path` / `add_symbol_server`）移到 Tier A（纯查询）。
- **原因：** 经 `SymbolTools.cs:61-90` 核实，`diagnose_symbols` 不修改 `_NT_SYMBOL_PATH`，也不主动下载符号——它只读取已加载 image 事件里的 `module.PdbName`。通过 `_cache.Get(path)` 可能触发 trace 加载 / `.etlx` 生成，但这是所有 Tier-A 工具的共同行为。
- **验收：** 文档准确区分环境变更、缓存生成、纯查询。✅ Tier C 现在只剩两个真正改 env 的工具。

### T0.2 MCP SDK surface spike

- **状态：** ✅ 已完成 2026-05-15（`MCP_SDK_SURFACE_SPIKE.md`，`McpSdkSurfaceTests`）。
- **范围：** 只选一个低风险 Tier-A 工具试点。
- **目标：** 验证 `ModelContextProtocol 1.2.0` 如何声明 `readOnlyHint` / `idempotentHint` / `openWorldHint`、tool `outputSchema`、structured result content、resource links。
- **结论：** 不需要升级 SDK。当前 attributed-tool 路径可直接在 `[McpServerTool]` 上声明 annotation 属性；`UseStructuredContent=true` 开启结构化输出；返回 `CallToolResult` 且需要显式 output schema 时使用 `OutputSchemaType`；resource link 对应 `ResourceLinkBlock`。
- **验收：**
  - ✅ 明确是否需要 SDK 升级。
  - ✅ 明确 annotation / output schema 是 attribute 写法还是 programmatic registration 写法。
  - ✅ 明确 attributed tools 是否能返回 `structuredContent` 和 `resource_link`。
  - ✅ spike 结论稳定前，不批量改工具，也不实现 `inspect_trace` 的 response wiring。

### T0.3 实现 `inspect_trace(path)`

- **状态：** ✅ 已完成 2026-05-15（`MetaTools.InspectTrace`，`InspectTraceResponse`）。
- **依赖：** T0.2 先明确 SDK 对 `outputSchema`、`structuredContent`、tool annotations 的支持方式。
- **范围：** 新增或扩展 `MetaTools`，补充 response records。
- **返回形态：** 使用 MCP `outputSchema` 的结构化 tool 输出；如果 `ModelContextProtocol 1.2.0` 无法表达该形态，先明确记录 fallback，再实现。
- **实现：** 新增 attributed tool，设置 `UseStructuredContent=true`、`ReadOnly=true`、`Idempotent=true`、`OpenWorld=false`、`Destructive=false`。返回 trace 基础信息、capability flags、module-level symbol quality、结构化 quality warnings、orientation tools、capability-supported tool hints。
- **返回字段：**
  - trace 基础信息：duration、event count、lost events、process count
  - capability flags：CPU、CSwitch、FileIO、DiskIO、CLR、ALPC、Network 等
  - symbol quality：symbol path、resolution rate、unresolved module hints
  - capture quality warnings：missing keyword、missing stackwalk、events lost
  - orientation tools 与 capability-supported tools：不带单一全局 rank 的 `tool_name` + `reason` 记录；不要嵌入应由 `workflow-catalog` 承载的长篇 workflow 文本
- **边界：** `inspect_trace` 返回原始信号和推荐。带判断性的 yes/no verdict 归 `diagnose_trace_quality`（T1.2），避免两个工具重复漂移。
- **验收：**
  - ✅ LLM 只调用一次即可知道"这个 trace 能分析什么、不能分析什么、下一步该用哪些工具"。
  - ✅ 机器可读的 orientation 与 capability-supported tool hints 足够稳定，可被 `list_applicable_tools` 和 composite tools 消费。
  - ✅ 不改变既有工具行为；只新增一个导航工具。

### T0.4 为 `inspect_trace` 加测试

- **状态：** ✅ 已完成 2026-05-15（`InspectTraceTests`、`MetaToolsTests`、`TraceCapabilitiesDetector` event-family 对齐）。
- **依赖：** T0.3。
- **范围：** `tests/WprMcp.Tests`。
- **覆盖：**
  - ✅ capabilities 正确投影
  - ✅ events lost 产生 warning
  - ✅ symbol path 缺失产生建议
  - ✅ 缺少关键 provider 时给出重采集建议
  - ✅ capability projection 与下游 analyzer 在 fixture trace 上行为一致；detector 的 event subscriptions 已与 analyzer event families 对齐，覆盖 read/write、send/recv、alloc/free、DPC/ISR、ALPC send/receive、registry operation variants。
- **验收：** ✅ 测试能锁住 response shape 和核心诊断规则。

### T0.5 建立度量基线

- **范围：** Server-side observability、synthetic evaluation、CI guardrails。不要记录原始参数、trace path、payload contents 或私有 trace metadata。
- **内容：**
  - ✅ 增加结构化 per-call telemetry：tool name、salted argument hash 或 session trace id、latency、response byte count、error flag、cache-hit flag。
  - telemetry 实现约束：
    - ✅ 运行时持久 telemetry 通过 `WPRMCP_TELEMETRY=1` opt-in；全新安装默认不发 telemetry。CI benchmark 和本地 measurement 命令是显式运行的验证路径，不是常驻运行时 telemetry。
    - ✅ server 启动时生成 per-session salt，且不写入磁盘或日志。如需 hash 参数，使用 `HMAC(session_salt, args_json)`；禁止 deterministic 或 process-lifetime path hashes。
    - ✅ telemetry 只能写 stderr 或 `%LocalAppData%\WprMcp\Logs\` 下的专用文件。stdout 保留给 MCP JSON-RPC framing，必须保持干净。
  - ✅ 启动时记录 `tools/list` payload size，并增加 CI guard：超过批准的 baseline 阈值则失败。
  - ✅ 定义 10 个标准 synthetic 调查场景及可接受工具调用序列，包括关闭 Resources / Prompts 的 tools-only 模式。见 `MCP_MEASUREMENT_BASELINE.md`。
  - ✅ 跟踪 `MCP_SURFACE_DESIGN.md` 的 6 个成功指标：wrong-tool selection、mean tool calls per investigation、`tools/list` size、human Prompt invocation、agent Prompt invocation、`inspect_trace` adoption。
- **验收：**
  - ✅ 每个 P0/P1 变更都能引用相对 baseline 的 delta。
  - ✅ benchmark 和 telemetry 输出不泄露隐私，也不污染 MCP stdio。
  - ✅ privacy review 通过：没有 raw paths、没有 deterministic path hashes、没有 payload contents，并验证 per-session salt 每次唯一。
  - ✅ transport review 通过：stdout 只包含 MCP JSON-RPC frames；日志确认写到 stderr 或专用文件。
  - ✅ 在未设置 `WPRMCP_TELEMETRY` 环境变量时，验证运行时 telemetry 默认关闭。

### T0.6 增加 token-compact stack responses

- **范围：** `*_top_stacks` 工具族，以及任何内嵌 stack rows 的 composite。
- **内容：**
  - ✅ 增加 `compactStacks=true` 请求 lossy compact stack output，供 token 受限客户端使用。当前 `*_top_stacks` rows 已经是 frame-level summaries，不包含完整 stack arrays；因此 compact mode 按文档化上限 25 截断 rows。见 `MCP_STACK_RESPONSE_COMPACTNESS.md`。
  - ✅ 增加 `summaryOnly=true`，通过同样的 row cap 返回 lossy 的更小 leaf / metric summary。需要 long-tail detail 时应不带 compact flags 重跑。
  - ✅ 默认保留现有详细输出；只有度量显示 compact 形态应成为 composite 首选路径时才调整默认路径。
- **验收：**
  - ✅ compact-mode defaults 锚定 Claude Code 已文档化的 MCP 输出行为：超过 10,000 tokens 警告，默认最大 25,000 tokens。代表性默认 stack responses 由测试守在 10,000-token warning threshold 的近似字节预算内。
  - ✅ sizing tests 覆盖代表性的已提交 stack fixtures，并增加结构性 guard，防止 row DTO 意外引入完整 stack arrays。
  - ✅ 截断是显式且可操作的；调用方可提高 `top` 或对具体 focus frame 使用 caller/callee drill-down。

## P1：LLM 路由与工作流压缩

### T1.1 实现 `list_applicable_tools(path, goal?)`

- **依赖：** `inspect_trace`（T0.3）和 T0.5 度量。只有数据证明 `inspect_trace` 的 orientation / capability-supported tool hints 不足以支持 goal-directed routing 时才实现。
- **输入：** trace path，目标可选：`cpu`、`startup`、`memory`、`gc`、`io`、`symbols`、`wait`。
- **返回：** 排序后的工具建议、适用原因、不适用原因。
- **验收：** 不动态修改 `tools/list`，只返回推荐列表。

### T1.2 增加高频 composite tools

- **优先顺序：**
  1. `diagnose_high_wait(path, focus="general|lock|io|sync")`
  2. `diagnose_image_load_blocker`
  3. `diagnose_gc_pressure`
  4. `diagnose_trace_quality` —— 按维度返回结构化 verdict：capture coverage、symbol resolution、lost events、stackwalk completeness。每个维度包含 `status: "ok|warn|fail"`、reason、actionable next step。overall verdict 由各维度 status 推导，而不是自由文本。
  5. 暂缓单独的 `diagnose_lock_contention`，除非数据证明 `focus="lock"` 路径不够。如果单独实现，它只覆盖 CLR managed locks（`clr_contention_top_stacks`），避免和 `diagnose_high_wait` 重复。
- **原则：** 每个 composite 内部组合 3-5 个现有 Layer-1 工具。任何内嵌 stack section 默认使用 `summaryOnly=true` 或 `compactStacks=true`；详细 drill-down 仍通过底层 Layer-1 工具提供。
- **验收：** 减少常见调查的工具调用轮次，而不是隐藏底层工具。

### T1.3 增加 Resources 与 Prompts

- **Resources：**
  - `capability-matrix`
  - `tool-catalog`：长篇使用指导、反模式、相关工具、示例；不复制 `tools/list`
  - `workflow-catalog`：可复用 workflow 文本，`inspect_trace` 和 composite tools 只通过结构化 pointer 引用，不复制正文
- **Prompts：**
  - `slow_startup`
  - `missing_symbols`
  - `high_wait`
  - `gc_pressure`
  - `baseline_regression`
- **验收：**
  - Tools-only 客户端仍能完成关键调查。
  - 每个 Prompt 及其 sibling composite Tool 都源自同一个 source-of-truth workflow artifact，例如 `workflows/<name>.json` 或 `workflows/<name>.md`。Prompt messages 从该文件生成或锚定到该文件；composite Tool 的 argument schema 和 step list 引用同一 artifact。
  - 当 Prompt 或 composite Tool 与 source workflow artifact 漂移时，CI 失败。如果 generator 暂缓，两边都必须包含指向 source artifact 的可审计 metadata，直到 CI enforcement 落地。
  - Tools-only 客户端通过调用 composite Tool，并传入 source artifact 命名的参数，达到同样结果。
  - agent-only Prompt invocation 接近 0 是预期行为，不视为失败。
  - Resources / Prompts 只改善支持它们的客户端体验。

## P2：低风险、高价值能力缺口

### T2.1 Trace quality 与 System Configuration

- **状态：** ✅ 已完成 2026-05-15（`TraceMetadataAnalysis`，`InspectTraceResponse.Metadata`）。
- **内容：**
  - ✅ 通过 `inspect_trace` 暴露 trace system metadata：machine name、OS name/build/version、processor count、CPU speed、boot time、UTC offset、metadata source。
  - ✅ 从 trace module table 暴露 driver modules，按最多 50 个 `.sys` 条目封顶，并在可用时带上 version/product metadata。
  - ✅ 暴露 provider event counts、total provider count、top providers、被截断到 other 的事件数、per-provider stack coverage、整体 event stack coverage。
  - ✅ 暴露 stackwalk completeness：是否具备 StackWalk capability、观测到的 StackWalk event count、带 call stack 的事件数、覆盖率。
  - ✅ `CpuModel` 保持 nullable，并返回 `cpu_model_not_available_from_trace_metadata` limitation，不回退到当前宿主机信息。TraceEvent 在这些 fixture 上稳定暴露 CPU count/speed，但不携带 CPU model 字符串。
- **关系：** 这些字段已优先接入 `inspect_trace`；`load_trace` 继续保持轻量 cache/orientation 调用。
- **验收：** ✅ LLM 可以判断 trace 是否可信，以及分析结论是否受采集质量限制。

### T2.2 ROI / time-window 语义统一

- **内容：**
  - 审计所有缺少 `startUs` / `endUs` 的工具。
  - 统一边界语义。
  - 增加 clip-boundary correctness tests。
  - 设计共享 ROI context，但不依赖动态工具表。
- **验收：**
  - 边界语义定义为半开区间：仅当 `startUs <= timestamp < endUs` 时包含事件。
  - conformance fixture 覆盖正好落在边界上的事件。
  - 所有支持时间窗的 analyzer 遵循同一边界规则；trace-global 工具文档化说明为什么不接受时间窗。

### T2.3 CPU Usage Precise 与 scheduler 分析

- **内容：** 基于 CSwitch 计算 on-CPU us、ready latency、per-core attribution、priority / quantum 相关信号。
- **验收：** 能回答 sampled CPU 无法回答的问题：线程实际运行多久、等了多久才被调度、跑在哪些 core 上。

### T2.4 Memory resource views

- **先验证（风险闸门）：** 确认现有 wpr profile 是否实际采集 working-set、commit、paged / non-paged pool、handle 计数器。如需新增 wpr keyword，本条降到 **P2.5**，且 keyword 工作先于 analyzer 工作。详见 `CAPABILITY_GAPS.md` v4 A-4 风险注脚——analyzer 无法恢复从未被记录的事件。
- **验证失败时的 fallback：** 在 `MmapCapture.wprp` 旁新增 `MemoryCapture.wprp`，采集能通过的 fixture，并先在 `docs/WPR_PROFILE.md` 记录 keyword 要求，再实现 analyzer。
- **内容（数据源确认后）：** 暴露 working set、commit、private bytes、paged / non-paged pool、handle count。
- **验收：** 能回答 resident footprint / pool exhaustion / handle leak，而不是只看 allocation events。

## P3：高价值但高风险能力

### T3.1 Cross-trace diff

- **内容：** CPU、wait、image-load gap 的 baseline vs regression diff。
- **前置：** 先定义 process identity matching 和 metric schema。复用已归档的 `docs/archive/OPTIMIZATION.md` O2 分析，比较 image name + spawn order + parent PID 与新增 stable identity field 等方案。
- **验收：** 输出 `MetricName` / `DeltaMetric` / `DeltaPct` / appeared-disappeared，不使用错误的通用 `DeltaUs`。

### T3.2 `.gcdump` 与 retention path

- **内容：** 加载 `.gcdump`、对象引用图、retention path。
- **验收：** 能回答"谁还持有这些对象"，补齐 ETW GC event 无法覆盖的内存泄漏调查。

### T3.3 Async / Task chain stitching

- **内容：** 重组 CLR Task continuation，跨线程恢复 async 调用链。
- **验收：** 能把分散在线程间的 async 工作流拼成可解释链路。

### T3.4 Generic event group_by / pivot

- **工程风险：** 低（在 `generic_event_top_stacks` 上扩参数，不是新工具）。
- **价值不确定性：** LLM 是否能驱动受限 pivot DSL 尚未证实。详见 `CAPABILITY_GAPS.md` v4 B-5 风险注脚。
- **内容——两阶段：**
  1. **最小可用（2 轴）：** 在现有工具上加 task + opcode 的 `group_by`。不新建工具、不扩 DSL 表面。
  2. **用 1–2 个真实场景验证** 再扩展轴集合（event_id、payload field）。如果阶段 1 LLM 零使用，**不扩展**。
- **验收：** 阶段 1 提供 WPA pivot 的核心价值，但不开放任意查询面；扩展仅在有验证数据支持的情况下进行。

## 暂不实施

- 纯 UI 渲染：flamegraph 图片、timeline、heatmap、bar chart、主题、截图、HTML report。
- 捕获执行：启动 / 停止 ETW session、实时 provider 管理、`wpr -start`。
- 动态 `tools/list` 过滤：会破坏 prompt prefix caching，客户端兼容性也不稳定。
- 将现有 54 个工具合并成一个万能入口：这是 breaking change，且会把决策负担转移到参数层。
- 长期迁移到每个 domain 一个工具，并用 `view=top|stacks|caller_callee` 和 `metric=` 参数切换视图：这是已归档 O6 的 breaking variant。除非使用数据证明值得重开设计，否则只通过 Layer-3 composites 做整合。

## 推荐实施顺序

1. ✅ T0.1 修正文档分类。
2. ✅ T0.2 完成 SDK surface spike。
3. ✅ T0.3 + T0.4 实现并测试 `inspect_trace`。
4. ✅ T0.5 建立度量基线。
5. ✅ T0.6 增加 token-compact stack responses。
6. ✅ T2.1 补 trace quality / system metadata。
7. T1.2 增加 2-3 个 composite tools，从 `diagnose_high_wait` 开始。Composites 先作为 "preview" routing targets 发布；只有 T0.5 benchmark 证明它们相比 Layer-1-only baseline 降低 wrong-tool selection 或 mean calls per investigation 后，`inspect_trace` 的 capability-supported tool hints 才应把 composites 与相关 Layer-1 工具一起列出。
8. 只有 T0.5 显示 `inspect_trace` 不足时，才实现 T1.1 `list_applicable_tools`。
9. T2.2 统一 ROI / time-window 语义。
10. T2.3 / T2.4 开始补 CPU Precise 和 memory resource views（T2.4 受"先验证"闸门约束）。
11. P3 项根据真实使用数据和正确性风险逐项启动。

## 完成标准

- synthetic benchmark 中 wrong-tool selection 相比 v1 baseline 增幅不超过 2 个百分点（覆盖 10 个标准场景）。
- T1.2 发布后，mean tool calls per investigation 相比 v1 baseline 至少下降 10%；否则重评 composite rollout。
- Tools-only MCP 客户端仍能完成核心调查，并在关闭 Resources / Prompts 的 test harness 中验证。
- 每个新增工具都有明确的"不适用场景"描述。
- 高风险分析能力在实现前先有 metric schema 和测试样例。
- 文档、工具描述、测试用例三者保持一致。

---

## 修订历史

- **v8 (2026-05-15)**：同步 `inspect_trace` 最终 P0 schema：用 orientation tools 与 capability-supported tool hints 取代旧的单一排序推荐字段。
- **v7 (2026-05-15)**：完成 T2.1 trace quality / system metadata：`inspect_trace` 现在包含 system metadata、driver module summary、provider event counts、stackwalk completeness。CPU model 保持 nullable，并显式标注 limitation，不使用宿主机兜底。
- **v6 (2026-05-15)**：完成 T0.6 stack response compactness：为所有 `*TopStacks` 增加 `compactStacks` / `summaryOnly`，加入 compact row cap 与 sizing/shape tests。
- **v5 (2026-05-15)**：完成 T0.5 度量基线：默认关闭且隐私安全的 telemetry、启动时 `tools/list` payload 日志与 guard、10 个标准 synthetic 调查场景。
- **v4 (2026-05-15)**：将 P0 任务编号改为依赖顺序：SDK surface spike（`T0.2`）先于 `inspect_trace` 实现（`T0.3`）和测试（`T0.4`）；同步更新实施顺序和任务引用。
- **v3 (2026-05-15)**：吸收第二轮 review 的收口建议：明确 telemetry privacy / transport 约束，为 Prompts 与 composites 增加 workflow source-of-truth 和 drift checks，以 Claude Code 输出限制锚定 compact stacks，为 composite stack sections 默认 compact，定义结构化 `diagnose_trace_quality` verdict，并要求 composite promotion 由 benchmark 闸门控制。
- **v2 (2026-05-15)**：吸收 `MCP_IMPLEMENTATION_TASKS_REVIEW.md` 中有仓库事实支撑的建议：SDK surface spike 先于 `inspect_trace`、结构化输出要求、度量基线、token-compact stack responses、composite 优先级调整、精确定义 time-window 语义、memory capture fallback、可验证完成标准。
- **v1 (2026-05-15)**：从 `CAPABILITY_GAPS.md` v4 + `MCP_SURFACE_DESIGN.md` v3 抽取出的初稿任务清单。T0.1 反过来把修正喂回 `MCP_SURFACE_DESIGN.md`（升级到 v4）。T2.4 和 T3.4 分别承接 `CAPABILITY_GAPS.md` v4 A-4 和 B-5 的风险注脚（先验证闸门；两阶段 DSL 推出）。文档头部引用全部四份文档。
