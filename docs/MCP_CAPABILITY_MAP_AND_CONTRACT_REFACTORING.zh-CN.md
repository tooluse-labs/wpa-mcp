# wpa-mcp 能力地图与证据契约重构设计

**日期：** 2026-08-02

**状态：** Accepted design amendment；Phase 0–7 runtime implementation 已大范围落地；最终 release gates 尚未关闭

**接受日期：** 2026-08-01

**接受依据：** 用户明确要求使用 `/goal` 开始实施

**适用范围：** MCP 公开能力发现、工具目录、结构化结果、证据边界、标识符精度、分页/截断、副作用分层、路由与 composite 执行

**当前实现基线：** 以运行时 validated Active Catalog、公开 schema、测试与 release gate 为准。2026-08-02 当前快照为 **61 个 active tools、51 个 declared capabilities、15 个 goals、15 个 workflows**；其中 declared capability 包含显式 gap。这些数字用于核对此次实施，不得成为跳过 catalog 验证的长期手写常量。

## 1. 文档定位与优先级

本文是对现有生产整改设计的“能力地图与公开契约”已接受目标修订。它优先复用已批准的路径安全、Trace Registry、lease、取消、worker isolation、隐私和发布机制；整体方向已获准实施，但涉及开放协议选择的部分仍必须先通过下表列出的 ADR/计划同步，不能把“开始实施”误写成所有细节均已决定。

文档角色必须按状态解释，不能只按日期或文件名判断：

| 文档/事实源 | 角色 | 当前权威范围 |
| --- | --- | --- |
| runtime active catalog、测试、当前 README/ARCHITECTURE | current implementation evidence | 描述仓库现在实际会什么；文档与运行时冲突时必须重新验证，不能自行选择有利版本 |
| `docs/superpowers/specs/2026-07-29-wpa-mcp-production-remediation-design.md` 及其 implementation plans | approved target baseline | 未被本 amendment 明确修改的条款仍是实施权威 |
| 本文与 `docs/decisions/0002-capability-map-evidence-contract.md` | accepted target amendment | 授权按 Phase 0–7 实施；§19 已由 ADR 0003–0005 锁定，但不是实现完成声明 |
| `MCP_SURFACE_DESIGN*`、`MCP_IMPLEMENTATION_TASKS*`、`CAPABILITY_GAPS*` | historical planning snapshots | 保留当时的设计动机；状态、数量、SDK 和实施顺序不得作为当前事实 |

因此，本文已接受的方向覆盖 §1.1 明确列出的旧目标；具体 wire/lifecycle 选择只有写入后续 ADR 并同步到 owning plan 后才能实施。未触及的生产整改要求继续有效，任何阶段都必须区分 accepted target、current runtime 和 implementation complete。

### 1.1 已接受 amendment 与后续 ADR gates

| 既有条款 | 已接受目标方向 | 后续 ADR gate/需同步 owner |
| --- | --- | --- |
| `tools/list` 为每个工具内嵌完整 output schema | 改为同源双投影：lean descriptor 保留完整 input schema 与 content-addressed contract metadata；完整 output schema 由 Resource 或 `get_tool_contract` 按需读取，server 仍用同一 schema 做结果验证 | Catalog/contract ADR；同步 payload/registry baseline、stdio 与 package gate |
| 把 Phase 0 snapshot 当作待实现 legacy wire floor | snapshot 仅是历史回归证据；此前没有 released legacy result contract，0.4.x 只发布 Contract 2.0 result shape，不实现未发布的 adapter | Rollout ADR；同步 migration、compatibility、README 与 release blockers |
| `tools/list` 按 tool name ordinal 排序，cursor 仅按既有 mode/index 设计 | 使用 `DiscoveryPriority + domain + tool name` 的稳定顺序；cursor 绑定 catalog hash/mode/server instance 并防篡改，同时仍完整分页 | Catalog/discovery ADR；同步 contract plan、MCP 协议错误和 snapshots |
| 每次显式 load 可产生新 trace ID | 要么同主体同 generation 返回 canonical ID，要么保留新 ID 但把 `idempotentHint` 设为 false；底层仍必须 generation-level single-flight | Trace lifecycle ADR；同步 trace registry/lease plan |
| 最后 artifact lease/handle 可触发派生产物删除 | handle/backend reference retirement 与 immutable artifact retention 解耦；ETLX 由独立 quota/LRU 回收 | Trace/artifact lifecycle ADR；同步 trace lifecycle 和 artifact ownership plan |
| `ToolEnvelope<T>` 顶层字段集合固定 | 在下一 versioned contract 中保留既有字段并增加统一 `ToolRef/TraceRef/Scope/CapabilityEvidence/Completeness/EvidenceBoundary/NoData/Precision`；failed scope candidates 留在公共 header，`Data` 继续是工具专属强类型 | Contract ADR 和新 schema version；同步 contract plan，禁止未版本化扩展 |
| symbol context 仅是内部实现状态 | 新增公开 `prepare_symbols` 和 immutable `SymbolContextId`，让符号网络/缓存副作用与 query 分离 | Symbol lifecycle ADR；同步 symbol access 与 trace lifecycle plans |
| 既有 registry/cache 计划未定义 principal-scoped handle 与 symbol-aware result cache | 增加 principal/session registry key、locator 泄露防护、symbol revision cache key 和 raw-path compatibility 复用约束 | Runtime/security ADR；分别同步 trace lifecycle、symbol access、cache/worker plans |
| 既有错误契约未覆盖新的 cursor/symbol-context 生命周期 | `tools/list` cursor 使用 MCP/JSON-RPC 协议错误；timeline tool 使用 versioned `invalid_cursor`；symbol artifact 无法维持时使用 `symbol_context_expired` | Contract/symbol ADR；同步各自 error registry、schema snapshots 和 stdio tests，禁止混用 response shape |

`docs/MCP_SERVER_BEST_PRACTICES.zh-CN.md` 是 informative 原则和审查清单输入，不是 versioned wire contract；其中示例字段、状态码和 DTO 形状只有在进入本文定义的 manifest、error registry 与 contract snapshot 后才成为规范。本文把其中第 4–7 节的实例身份、目标事件域、真实符号状态和 annotation 原则与截断、标识符、生命周期及 routing 统一落地。

本文不把 `docs/superpowers/` 中的计划当作已完成事实。所有状态必须区分为 `current`、`target` 和 `migration`。

### 1.2 阅读路径

- 设计与 ADR 审查：§1–5、§18–20，重点检查 accepted amendment、不可变式和开放选择。
- analyzer 正确性实施：§5.3、Phase 0/1 和 §17.3–17.5。
- MCP contract/catalog 实施：§7–13、Phase 2–5 和 §17.1–17.3。
- routing/LLM 评估：§7、§14–15、Phase 5–6 和 §17.6。

## 2. 北极星原则

> 完整暴露能力，用能力地图降低选择成本；完整暴露证据边界，用结构化契约阻止 LLM 过度解释。

这一原则分成两个同等重要的约束：

- **能力不得被静默隐藏。** 低频不等于低价值，当前 trace 不支持某能力也不等于服务器没有该能力。
- **证据不得被静默美化。** 执行成功不等于证据可用，Top-N 不等于完整集合，关联不等于因果，PDB identity 不等于符号已解析。

目标不是让 LLM 看到更少，而是让它以更低的选择成本看到完整、分层且不会过度承诺的能力。

## 3. 当前基线与统一问题

重构开始时的历史审查基线是：

- active catalog 当时暴露 61 个工具；该数字只属于 Phase 0 pre-refactor snapshot。
- 只有 5 个工具启用 `UseStructuredContent=true`，其余工具没有可供客户端验证的完整 `outputSchema/structuredContent`。
- Release 配置下 `tools/list` 约为 178,923 UTF-8 bytes，距离现有 180,000-byte CI guard 只剩约 1.1 KiB。
- 多数 Top-N 响应没有 `TotalGroupCount`、`HasMore`、排序和截断原因。
- `NetConnectionRow.ConnId` 等 opaque 64 位标识仍可能作为 JSON number 暴露。
- 查询工具可能隐式转换 ETL、写 ETLX、修改符号状态或下载符号，因此 annotations 只能按最坏路径保守声明。
- `inspect_trace`、metadata、capability detector 和 composite 仍存在重复全 trace 扫描。

这些不是四个独立问题：

1. 给全部工具增加精确 output schema 会进一步放大 `tools/list`。
2. 增加分页、精度和证据字段会同时增大 Schema 与调用响应。
3. 拆分隐式副作用会增加生命周期工具和迁移契约。
4. 如果 capability、tool、description、schema、routing 和 composite 各有一份手写规则，它们必然继续漂移。
5. 重复扫描会放大取消、预算和 partial-result 契约的复杂度。

当前实现的 active surface 为 61 个工具（原 60 个能力/分析/生命周期工具，加上
Tools-only contract fallback `get_tool_contract`），并由 validated model 投影出
51 个 declared capabilities、15 个 goals、15 个 workflows、全部 input/output schema、
`tools/list`、`list_capabilities`、Trace Evidence Map 与 byte-budgeted Resources。
因此，本次重构的对象始终是一个统一的 **Capability and Contract Runtime**，
而不是逐工具追加字段；历史数字不能覆盖当前 catalog truth。
当前与 Phase 0 总数同为 61 只是数值巧合：tool identity 已变化，不能把历史 snapshot
当作当前目录或 legacy compatibility floor。

Contract 2.0 全量接线后，把每个深层 output schema 都内嵌进目录的历史测量约为
2.5 MB。该数字是同源双投影之前的 before measurement，不是当前默认
`tools/list` 大小，也不能直接当作 LLM prompt 成本。当前 gate 分别度量 aggregate
lean discovery（不超过 250,000 bytes）和完整 schema registry；二者不能混报。

**实际 ETL 证据纠错：** 对本轮指定 ETL 使用当前 exact ETL/TraceLog identity
projection 复核后，PID 4024 只对应 **1 个进程生命周期**，整条 trace 未观测到 PID
reuse。此前流传的“PID 4024 有 9 个生命周期”不是这份 ETL 的可复现实证，必须撤销，
也不能用它证明 process-instance 修复已在该 trace 上触发。PID reuse 正确性仍由专用
fixture/golden 验证；真实 trace 只能证明它实际包含的 evidence。

该实际 ETL 同时形成以下对抗证据。修复后的当前 development executable 已在同一
文件上重跑下列查询；这证明对应 query-level 行为，但仍不等于 exact packaged
executable、同一 commit/package hash、具名客户端观测和外部 release matrix 已通过：

- 该 trace 是系统/工作负载 trace，未包含可精确选中的 WpaMcp/dotnet server 实例，
  因而不能用于 MCP self-performance attribution。
- 当前直接 TraceLog probe 物化 8,631,351 个事件，其中 5,354,210 个带 stack，
  即 62.03211988482452%；ordinary CSwitch 域有 2,541,308 个 eligible events，
  其中 1,813,653 个 `CallStack` 非空，即 71.36691%。此前 71.55% 属于旧外部口径，
  不再作为当前 exact fact。两组比例都必须保留事件域、scope、eligible/stacked
  分子分母；全事件覆盖率不能替代 CSwitch 或其他 domain 的覆盖率，而 ordinary
  CSwitch `CallStack` coverage 也不能替代 Wait analyzer 的 blocking-stack coverage。
- 既有约 1.69% native frame resolution 是特定外部/offline lookup 环境下的测量；当前
  active runtime 的 context-bound TraceEvent resolver 尚不可用，不能把 1.69% 归因给
  `prepare_symbols` 或当前 MCP symbol pipeline。当前重跑的 `resolveSymbols=true`
  请求以 5,416-byte 完整响应稳定返回 `symbol_resolution_unavailable`，证明没有退回
  ambient resolver；它仍不提供 frame-resolution measurement。
- `list_processes(top=1000)` 现在通过绑定的 `qrc_` cursor 返回 4 页，页行数依次为
  99/101/104/24，共 328 个 distinct process lifetimes；最大完整 frame 为 99,772 bytes。
  因此单页 fitting 不再把后续进程静默丢失，完整性仍要求客户端跟完全部页。
- 窄窗 `[1,2)` all-process FileIO 现在成功返回 `no_events_in_scope`；scope 的
  `includedTotal=292` 保持裁剪前 exact，identity detail arrays 明确标记为
  budget-omitted，完整响应为 33,000 bytes。这是“范围已解析但窗内无目标事件”，
  不是 PID 不存在、能力缺失或 terminal `response_too_large`。
- trace 内可解析 CLR provider 事件为 31,218 条，而旧 warning 曾误报 CLR runtime 未观测。
  这证明 warning/evaluator join 存在缺陷，不自动证明每个 CLR 子能力可用；修复必须按
  typed event class、scope 与 stack requirement 分别评估，并用原 trace 回归。

## 4. 目标与非目标

### 4.1 目标

1. 默认完整、静态、稳定地暴露所有启用的公开能力和工具。
2. 为每项服务器能力提供稳定 `CapabilityId`、问题边界、证据要求、实现工具和成熟度证据。
3. 为当前 trace 提供逐 capability 的可用性、覆盖率、符号质量和不支持原因。
4. 让每个工具返回 Schema-valid `structuredContent`，并从同一对象派生兼容 text content。
5. 让每个结果机器可读地回答范围、能力、完整性、精度和三维证据边界。
6. 消除不可观察截断和 JavaScript unsafe integer 标识符。
7. 将 trace 加载、符号准备和只读分析拆成边界清晰的生命周期。
8. 让 capability map、catalog、routing、文档表格、Schema snapshot 和 composite 计划共享同一来源。
9. 在不改变分析语义的前提下减少物理 trace pass。

### 4.2 非目标

- 不删除低频底层工具来降低 token 成本。
- 不把所有能力合并成 `analyze_trace(mode=...)` mega-tool。
- 不根据当前 trace 动态增加、删除或重排 `tools/list`。
- 不增加 session-time activate/deactivate API，也不让 host 的渐进注入反向修改 server catalog。
- 不增加万能 dispatcher，不把原生工具名或调用参数收敛成一个自由格式入口。
- 不为从未发布的 result wire shape 实现 legacy adapter。
- 不把默认 `core profile` 当作隐藏完整能力的捷径。
- 不把关键能力只放在 Resources 或 Prompts。
- 第一阶段不重写已经通过正确性测试的 ETW 算法。
- 不以性能优化为理由改变 scope、分母、窗口、排序或证据语义。
- 不宣称完整 WPA/PerfView parity。
- 不以自然语言 warning 代替机器状态。
- 不在没有独立 SDK/协议验证的情况下强行升级到预发布依赖。

## 5. 术语与不可变式

### 5.1 必须分开的四种状态

| 维度 | 含义 | 建议值 |
| --- | --- | --- |
| `ProductMaturity` | 产品是否真正实现并验证能力 | `supported / preview / gap` |
| `TraceCapabilityStatus` | 当前 trace 对该能力的证据可用性 | `available / partial / unavailable / unknown / disabled_by_policy` |
| `ToolCompletionStatus` | 本次调用是否完成请求的工作 | `succeeded / partial / failed` |
| `CompletenessStatus` | 返回集合是否完整 | `complete / top_n / paged / truncated / resource_limited / unknown` |

证据语义不能压成一条虚假“强弱等级”，必须使用三个正交维度：

```text
MeasurementBasis: direct / derived / heuristic
Relationship: descriptive / association / causal
ConclusionStatus: supported / not_concluded
```

例如，直接观测到一个 ReadyThread 事件仍只能支持 association；heuristic 描述的是识别方法，不自动决定关系强度。`causal` 只有 capability manifest 注册了明确因果规则且运行时满足全部前提时才允许返回。

禁止用一个通用 `partial` 同时表示“trace 只有部分堆栈”“本次工作失败了一部分”和“Top-N 省略了有效行”。

### 5.2 不可变式

- 工具存在不等于当前 trace 支持该能力。
- 当前 trace 有某类事件不等于目标 scope 有匹配事件。
- 全局有堆栈不等于目标事件域有堆栈。
- 有 PDB name/GUID/Age 不等于本地 PDB ready，更不等于 frame resolved。
- 工具执行成功不等于结果完整。
- `rows=[]` 不等于“没有问题”。
- `rows.Count == top` 不足以证明 `HasMore=true`，也不足以证明结果完整。
- `MatchedEventCount` 不能代替截断前的聚合项总数。
- 关联或 readier stack 不能自动升级为根因。
- opaque identifier 不能依赖客户端保留 JSON number 的 64 位精度。
- 客户端不需要阅读 description 才能判断 scope、capability、completeness 和 precision。

### 5.3 已知正确性问题闭环

此前审查发现不能只被总体架构“间接覆盖”。下表是必须逐项关闭的 migration ledger；Phase 0 要把每项绑定到实际 tool/DTO/analyzer，Phase 1 修复语义，后续阶段只能迁移载体，不能把问题重新引入。

| Issue ID | 已知问题 | 固定目标契约 | 强制阶段与验收 |
| --- | --- | --- | --- |
| `COR-SCOPE-001` | ImageLoad、MemoryResource、CLR、IO、HardFault、VirtualMemory 等进程级路径仍可能只按 PID 聚合 | 所有适用工具共享 `pid + processStartUs` 选择器和 `ProcessInstanceKey`；PID 多实例时要么失败为 `process_start_required`，要么由工具明确允许 `ScopeMode=pid_aggregate`；返回 `SelectedProcess`、`IncludedProcesses`、`PidReuseObserved` | Phase 0 inventory，Phase 1 修复；manifest 中所有 process/thread-scoped 工具参数化运行 PID/TID 复用 fixture，验证不跨生命周期静默合并 |
| `COR-SCOPE-002` | `diagnose_high_wait` candidate 只有 PID，不能安全重放 | candidate 和 NextTool 参数携带 `processStartUs`；如果候选来自显式 PID aggregate，必须保留 aggregate scope，不能伪造单实例 | Phase 1；candidate replay 与原 scope identity 完全相等 |
| `COR-STACK-001` | 全局 `HasStackWalks` 被当成目标事件有堆栈 | 每个 capability 返回同域、同 scope 的 eligible/stacked counts、coverage 和 stack semantics；`?!?` 标记为 synthetic unknown bucket，不能展示为捕获的调用链 | Phase 1/5；从 manifest 参数化遍历所有 `RequiredEventStacks` capability；CPU 有 stacks 但目标域无 stacks 时必须 unavailable/partial |
| `COR-SYMBOL-001` | PDB identity 被表述为“符号已解析”，`FrameCount=0` 仍像有效测量 | 使用 `ModulesWithPdbIdentity`、local readiness、`FrameResolutionMeasurementState` 和实际 lookup 的 frame resolution 四个独立概念；没有测量时值为 unavailable/null，不能用 0% 或 metadata 代替 | Phase 1/5；低解析率、未测量和无 PDB 三类 golden 互不混淆 |
| `COR-WAIT-001` | Wait 的全 trace CSwitch 分母和任意线程 stack 状态被用于 scoped rows | 分开 `TraceCSwitchCount`、`ScopedCSwitchCount`、`ScopedStackedCSwitchCount`、`ScopedStackCoveragePct` 和 `HasScopedBlockingStacks`；后四项与 rows 使用同一 pid/process/thread/window predicate | Phase 1；加入大量 scope 外 CSwitch/stacks 后，scoped count、coverage、stack 状态和 rows 不变，只有 trace count 可变 |
| `COR-NODATA-001` | 空数组无法区分能力缺失、selector 错误、scope 无数据和解析失败 | selector/scope/执行错误为 `failed + Error`；成功但无领域数据为 `succeeded + NoData`；所有分支返回 capability、matched count 和稳定 reason | Phase 1/4；每个原因至少一个 contract test，禁止裸 `rows=[]` |
| `COR-VM-001` | VirtualAlloc 把 Alloc 和 Free 的 Length 都作为正数，容易被解释为净分配 | 分开 operation traffic、allocated bytes、freed bytes、signed net delta 和可证明时才返回的 outstanding；字段名、单位、符号与窗口边界写入 Schema，不能把 traffic 命名为 allocation/net growth | Phase 1；alloc/free 对称 fixture 的 net 为 0、traffic 为两者之和；alloc-only、free-only、跨窗口和 missing-length 分别返回正确值或 measurement limitation |
| `COR-FILEMAP-001` | FileObject/FileKey 使用全 trace 最终映射，key 重用会把历史 IO 归到后来的文件 | resolver 使用含 create/bind/release sequence 和有效区间的 versioned mapping；每个 IO 只关联事件发生时有效的对象名，ambiguous/unresolved 保留机器状态，不能回填未来名称 | Phase 1；同 key 先绑定 A、发生 IO、释放、复用为 B、再发生 IO，历史事件必须分别归因且区间外 unresolved |
| `COR-PRECISION-001` | stack metric 经 `float` 中转后再输出 `long`，大字节/微秒值看似精确实则丢位 | 权威聚合值使用 checked 64-bit integer/decimal，不能从 `StackSourceSample.Metric` 回转；若 StackSource API 强制 binary32，则使用并行 exact accumulator，或把树权重明确标为 rounded/estimated 并给出 scale/error bound，绝不能 cast 回 `long` 冒充 exact | Phase 1；覆盖 `2^24-1`、`2^24`、`2^24+1`、`2^53`、大 duration/bytes 和 overflow 边界 |
| `COR-SIDEFX-001` | 首次查询隐式修改进程 `_NT_SYMBOL_PATH`，read-only annotation 与真实行为不符 | query 与 load/prepare-symbols 分层；secure-default query 不修改环境、不下载、不转换；compatibility annotation 按真实最坏行为声明 | Phase 3；环境前后值、文件写入和网络 egress 有副作用测试 |
| `COR-CACHE-001` | LRU 锁外构造导致同一大型 trace 并发重复 conversion/open | 按 `TraceGenerationKey` single-flight，backend/artifact 共享，调用各持有 lease；失败/取消不发布半初始化 entry | Phase 3/6；用 barrier 同时启动 N 个 load/path-query/composite，断言仅一次 conversion、sidecar publish 和 backend construction |
| `COR-CAUSAL-001` | ready-thread/readier stack 被描述为“who unblocked”或闭合因果链 | `MeasurementBasis=direct` 可以成立，但 `Relationship=association`，`DoesNotProve` 明确不能单独识别责任组件或根因；description、schema 和 composite 结论使用一致措辞 | Phase 1/5；对 description、capability boundary 和 runtime evidence 运行因果措辞 linter，并执行 LLM overclaim benchmark |
| `COR-HEURISTIC-001` | SecurityScan 仅通过名称关键字启发式识别，却可能被当作已确认杀毒扫描 | 返回 `MeasurementBasis=heuristic`、规则/信号来源、confidence 和可能的替代解释；没有独立 provider/process identity 证据时不得升级为 confirmed security product | Phase 1/5；相似事件名 false-positive fixture 的 structured result 保持 heuristic、provenance、confidence 与 `DoesNotProve` |
| `COR-COUNT-001` | `TraceMeta.EventCount`/provider count 是解析器观察口径，可能被误比作 ETL 原始/外部事件总数 | 字段名和 provenance 明确 `ParsedEventCount`、`ParsedEventCountSource`、parser/version/filter；外部/raw count 若可得单列 `RawTraceEventCount` 和 state，未知为 unavailable，二者不允许共享 denominator | Phase 1/5；同一 ETL 的独立 raw 统计与 parsed count 同时出现时不互相覆盖，所有百分比声明分母 |
| `COR-PAGING-001` | Top-N、budget fitting 或客户端裁剪不可观察 | 所有集合返回 section completeness、total state、排序、tie-breaker、`HasMore` 和 truncation reason；完整 frame 在发送前精确计量 | Phase 1/4；`top+1`、cap/cap+1、多字节 UTF-8 与客户端 E2E |
| `COR-TRUNCATION-001` | composite 把已截断子结果中的样本混入自由文本 annotations，LLM 难以区分样本完整性、代表性与 aggregate metric 归属 | 样本与 annotations 分离；样本使用强类型 DTO，明确 `Representative`、`MetricAttributable`、`SampleScope`，并由独立 `SamplesBoundary` 披露 total/more/truncation state | Phase 4；security duration/presence 的 top=1 与全量对照、validator hostile fixture、reachable-collection closure gate |
| `COR-ID-001` | ConnId、FileKey/FileObject、地址/句柄等 64 位 ID 作为 JSON number | 权威值使用规范字符串；unsafe legacy numeric 为空并返回 precision/deprecation 状态 | Phase 1/4；JavaScript `2^53`/`ulong.MaxValue` 往返与 schema linter |
| `COR-ATTRIBUTION-001` | 用系统/工作负载 ETL 推断 MCP 自身性能 | trace facts 声明 observed workloads/process instances；只有 trace 中存在并精确选中 MCP server/worker 实例时，结果才能用于 MCP self-performance 结论 | Phase 5/6；不含 MCP 进程的 fixture 返回 `not_concluded`，不生成 self-performance recommendation |

## 6. 目标架构

```text
Static Active Tool Catalog ───────► Server Capability Map
          │                                  │
          │                                  ├── list_capabilities
          │                                  └── wpa://capabilities（同源镜像）
          │
load_trace(path) ──► traceId + TraceFactsSnapshot
                                  │
                    ┌─────────────┴─────────────┐
                    │                           │
            inspect_trace(traceId)      prepare_symbols(traceId)
            Trace Evidence Map          显式网络/缓存副作用
                    │                           │
                    └─────────────┬─────────────┘
                                  ▼
                       Query Planner / Analyzer Catalog
                       单次 dispatcher，多 accumulator
                                  │
                                  ▼
                         ToolEnvelope<TData>
                    scope / capability / completeness /
                    precision / evidence / paging / data
                                  │
                                  ▼
                  privacy → exact wire fitting → schema validation
                                  │
                                  ▼
                       content + structuredContent
```

`Query Planner` 是内部执行层，不是新的 mega-tool。底层工具继续直接可调用，composite 也必须返回其逻辑步骤和物理扫描 provenance。

## 7. 两层能力地图

### 7.1 Server Capability Map

Server Capability Map 回答“服务器理论上会什么”，不能依赖某条 trace。

地图顶层必须先限定“完整”的宇宙，防止 LLM 把仓库声明的能力误认为 WPA/PerfView 全能力全集：

```text
CatalogScope: wpa_mcp_declared_capabilities
ExhaustiveForWpa: false
UnlistedCapabilityMeaning: unknown_not_catalogued
CatalogVersion
TotalCapabilities
```

这里的“完整”只表示当前 catalog 声明的所有 supported/preview/gap 能力均被列出；未列出的 WPA 能力表示尚未登记或评估，绝不等于已支持或确认不支持。

建议每项能力使用以下稳定结构：

```text
CapabilityId
Domain
Title
Summary
QuestionsAnswered[]
QuestionsNotAnswered[]
ConclusionBoundaryCodes[]
RequiredEvents[]
RequiredEventStacks[]
OptionalEvidence[]
SymbolRequirement
SupportedScopes[]
SupportedScopesSemantics
ToolNames[]
WorkflowIds[]
CostClass
SideEffectClass
ProductMaturity
EvidenceReferences[]
ContractVersion
```

要求：

- `CapabilityId` 使用稳定、小写、点分层级，例如 `scheduler.blocked_time`、`io.file.stacks`。
- 每个公开工具至少引用一个 capability。
- 每个 `supported` 或 `preview` capability 至少有一个实现工具。
- `gap` 仍可出现在完整能力地图中，但不得出现在可调用工具集合中。
- 管理员禁用的能力仍显示为 `disabled_by_policy`，不能伪装成未实现或从能力地图消失。
- `QuestionsNotAnswered` 必须包括容易被 LLM 过度解释的边界。
- capability 的 `SupportedScopes[]` 是映射工具的集合适用性：对已实现 capability，每个值只保证至少一个 mapped tool 可公开选择该 scope，不保证所有 mapped tool 都支持；`gap` capability 只声明预期 scope。响应必须同时返回稳定的 `SupportedScopesSemantics`，避免 LLM 将集合能力误套到单个工具。
- 机器判断使用稳定 `ConclusionBoundaryCodes`；自然语言问题说明只是同一 code 的展示文本，不能成为唯一契约。
- `supported / preview / gap` 必须链接自动化测试、golden case、benchmark 或明确的人工证据；产品成熟度不是运行时 trace 状态。

### 7.2 Trace Evidence Map

Trace Evidence Map 回答“当前 trace 实际能支持什么证据”。`inspect_trace(traceId)` 应按 capability 返回 assessment，而不是只返回一组全局 bool。

当前每个 capability assessment 的 live 字段是：

```text
CapabilityId
EvaluatorId
TraceStatus
TraceEligibleEventCount
CountRepresentation
StackCoverage
UnavailableReason
Warnings[]
MeasurementBasis
Relationship
ConclusionStatus
CaptureIntegrity
CallableTools[]
DoesNotProve[]
DetailsResourceUri
TraceCompletedEvidenceCount
TraceUnmatchedEvidenceCount
TraceBoundaryEvidenceCount
EvidenceCompletionState
```

其中 `StackCoverage` 内含同域 event/metric stacked 分子分母、百分比、metric
accounting、stack semantics 和 synthetic-unknown 状态。map 顶层另有 `Capture`、
`Symbols`、`SelfAttribution`、catalog/filter/ordering/totals 与 workflow assessments。
当前不存在 per-capability `SymbolContextRef`、measured frame-resolution rate 或
affected-process exact count 字段；如需新增必须走版本化 schema/manifest gate，不能让
客户端按早期设计草案猜字段。

约束：

- `inspect_trace` 默认评估整条 immutable generation；它不使用容易与工具 scope 混淆的 `MatchedEventCount`。工具结果另行返回 `ScopedMatchedEventCount` 及其 scope。
- event coverage 和 metric-weighted coverage 必须同时保留各自分子、分母、单位和 accounting；percentage 规范范围固定为 `[0,100]`，不能和旧 `[0,1]` ratio 字段混用。
- `TraceEligibleEventCount` 和 `TraceStackedEventCount` 必须属于同一事件域与 `CountRepresentation`；identity-unresolved、未配对 interval、lost-event 和 parser uncertainty 单列，不能悄悄进入精确分母。
- 全局 `HasStackWalks` 只能作为 capture 元数据，不能使 FileIO/CLR/HardFault 等 capability 自动可用。
- PDB identity 是 generation 固有事实；当前 `inspect_trace` 不接受
  `SymbolContextId`，其 map-level local readiness 与 frame resolution 保持
  `unmeasured`，也不隐式探测本地磁盘/网络或从 metadata 推断。当前 runtime 只实现
  独立 `prepare_symbols` readiness assessment，未实现 context-bound frame-resolution
  assessment；在 adapter 与真实 trace gate 通过前必须返回稳定 unavailable error，
  不能因为 stack-query 参数/schema 已暴露就宣称能力可用。
- `event_class_not_observed` 只表示当前 materialized/parser-observed trace 中未观察到，不证明 capture keyword 明确关闭。若该域受 event loss、身份未解析或 parser coverage unknown 影响，状态必须为 `unknown/partial`，不能武断地标为 unavailable。
- 未来若加入 `AffectedProcessInstanceCount`，在身份不完整时必须是 lower bound 或
  unknown；当前 map 没有该字段，不能从 inventory 长度或 PID 数量自行构造。
- `unavailable` 必须给稳定原因和充分 provenance，例如 `target_event_has_no_stacks`、`symbol_policy_denied`；只有 evaluator 能排除 capture-integrity uncertainty 时才能确定 unavailable。
- `inspect_trace` 推荐必须来自 capability requirements 与 assessment 的 join，不能维护第二套手写 if/else 目录。

### 7.3 能力发现入口

关键能力发现必须在 Tools-only 客户端可用：

- `list_capabilities(domain?, goal?, cursor?)`：完整服务器能力地图的可筛选视图。
- `inspect_trace(traceId, domain?, goal?, cursor?)`：当前 trace 的 capability/workflow
  assessment。当前 schema 不接受 `SymbolContextId`，不执行 local readiness 或 frame
  lookup；符号 readiness 由独立的 `prepare_symbols` 返回。

当前没有 active `list_applicable_tools`。适用工具已经作为 `inspect_trace` assessment
和 workflow projection 的一部分返回；若未来 benchmark 证明独立排序入口有增益，必须
先加入 Active Catalog，不能把设计名称写成可调用工具。

所有筛选视图都返回 normalized filter、过滤前总数、过滤后总数、`HasMore` 和 cursor；过滤结果为空不能被解释成服务器没有其他未请求 domain/goal 的能力。

Resources 只提供同源镜像：

```text
wpa://runtime/profile
wpa://capabilities/server
wpa://capabilities/domain/{domain}[/{page}]
wpa://tools/server
wpa://tools/domain/{domain}[/{page}]
wpa://tools/{toolName}/sections[/{page}]
wpa://workflows/server
wpa://workflows/{workflowId}
```

当前不暴露 trace-scoped Resource，也不通过 `resources/list` 枚举 registry 中的 concrete
TraceId；trace-specific evidence 只通过 `inspect_trace` Tool 获取。未来若新增 trace-scoped
Resource Template，必须由调用者主动提供 TraceId，并复用与 Tool 相同的 principal/session
lookup、生命周期错误和日志脱敏；在客户端/SDK 不能证明这条边界时仍不得开放。

不能要求客户端必须读取 Resource 才能发现关键工具或理解证据边界。

## 8. Manifest 驱动的 Active Catalog

### 8.1 三份数据的职责

避免建立三份互相复制的 capability matrix：

| 文件/模型 | 唯一职责 |
| --- | --- |
| `capabilities.v1.json` | 稳定能力语义、问题边界、证据要求和 CapabilityId |
| `tool-contracts.vN.json` | 下一版工具契约（实际版本号由 Contract ADR 决定）与 capability 的映射、annotations、排序、可分页 section、输出类型和迁移状态 |
| `benchmarks/capability-matrix.v1.json` | `supported/preview/gap` 与可执行证据引用 |

运行时只使用经过校验的 `ActiveToolCatalog`。它把 typed tool methods、contract manifest 和 capability definitions join 成一个不可变 catalog。其他组件不得再次独立扫描 assembly 并推导自己的工具列表。

### 8.2 每个工具的语义 manifest

每项至少声明：

```text
ToolName
ContractVersion
InputType
OutputType
CapabilityIds[]
RequiredCapabilities[]
SelectableScopes[]
Annotations
SideEffects[]
CostClass
DiscoveryPriority
DefaultOrdering
TieBreakers[]
PageableSections[]
PaginationMode
Deprecation
InternalAnalyzerOperations[]
AllowedMeasurementBases[]
MaximumRelationship
ConclusionRules[]
DoesNotProve[]
```

`SelectableScopes[]` 只声明调用者能通过该工具公开 input schema 直接选择的分析 scope，不代表输出粒度、证据完整度或结论强度。它是工具级唯一真相，不能再并列保留语义含混的 `SupportedScopes[]` alias。所有 Tool catalog/resource 投影同时返回稳定的 `SelectableScopesSemantics`。

Active Catalog 必须通过 typed method/public schema 逐项验证声明：`thread` 要求 `tid`；`time_window` 同时要求 `startUs` 与 `endUs`；`focus_frame` 要求 `focusFunction` 或 `function`；`provider` 要求公开 provider selector；`process` 要求 PID-class selector，确有必要的受审 process-name selector 必须显式列入 allow-list。相对时长、输出中的 thread 行或 stack 数据都不能冒充调用者可选择的 scope。对已实现 capability，`SupportedScopes[]` 必须等于 mapped tools 的 `SelectableScopes[]` 并集：每个 capability scope 至少由一个工具提供，任何工具已提供的 scope 也不能从能力地图遗漏。

由 Active Catalog 生成或验证：

- `tools/list`
- lean `tools/list` input schema/contract metadata 与完整 output contract registry
- structured result wrapper
- annotations
- Server Capability Map
- Trace Evidence Map 的 requirements
- applicable-tool routing
- 响应可截断 section
- composite 执行计划
- Resources/文档表格
- contract snapshots

启动时必须 fail closed：工具缺 manifest、manifest 指向不存在的工具、capability 没有定义、selectable scope 没有对应公开 selector、capability scope 无 mapped tool、pageable JSON pointer 不是数组、annotation 与 active profile 冲突，均不得启动 MCP transport。

## 9. 完整工具目录与规模控制

### 9.1 静态完整暴露规则

- 默认 active catalog 是完整目录，不使用默认 `core profile` 隐藏工具。
- `tools/list` 对同一启动配置保持静态，不因加载哪条 trace、已调用哪个工具或 LLM 当前问题而变化。
- 所有页面合并后，每个启用工具恰好出现一次；host 只注入子集时不得把未注入工具报告为 server 不支持。
- profile 只能表达管理员明确的安全或部署策略，不作为 token 优化手段；被禁用能力必须在能力地图中返回 `disabled_by_policy`。
- `list_capabilities` 与 `get_tool_contract` 是不可隐藏的 bootstrap Tools；任何仍提供 trace analysis 的 profile 也必须保留 `inspect_trace`。否则 Tools-only client 无法完成能力与契约发现，启动应 fail closed。
- server 不提供 session-time activation/deactivation，也不提供一个替代原生工具的万能 dispatcher。工具名、完整 input schema 与调用参数保持原样。

### 9.2 同源双投影

validated Active Catalog 为每个工具只生成一次完整、闭合的 Contract 2.0 output
schema。该同一份 immutable schema 同时用于 server 发送前验证、contract snapshot、
content-addressed Resource 与 Tools-only fallback；任何投影的 hash/byte 不一致都 fail
closed。

默认 `tools/list` 只返回 lean discovery descriptor：工具名、description、完整
`inputSchema`、annotations，以及 `_meta["wpa-mcp/outputContract"]` 中的
`contractVersion/schemaDialect/uri/sha256/mediaType/utf8Bytes/representation`。深层
`outputSchema` 不在 descriptor 中内嵌；这只是发现投影瘦身，不会削弱调用结果或 server
validation 语义。

完整 schema 有两条等价按需路径：

1. 支持 Resource 的 client 读取
   `wpa://contracts/tools/{toolName}/{sha256}` 小型 index，跟完
   `.../pages/{page}`，按 page/start-byte 顺序无分隔符、无 normalization 地拼接
   `schemaFragment` UTF-8 bytes。
2. Tools-only client 调 `get_tool_contract(toolName, page)`；页号从 1 开始，每页最多
   8,192 UTF-8 bytes，跟随 `nextPage` 直到 null，并按相同规则重组。

两条路径读取同一个固定 8,192 UTF-8-byte page registry；Resource index/page URI、
pageCount、start offset 与 fragment 不随实例 frame cap 改变。启动必须在 stdin 前用最大
合法 serialized request ID 逐页测量真实 Resources/read frame 与 Contract 2.0 镜像 Tool
frame，并以两者和 `tools/list` minimum 的最大值作为统一下限。当前 catalog 的 Tool
最大帧为 35,858 bytes、Resource 最大帧为 15,911 bytes；这些是 baseline 实测值，不是
长期硬编码协议常量，catalog 改变后必须重新计算。

两条路径都必须核对 tool name、contract version、canonical byte count 与 lowercase
SHA-256。Schema dialect 是 JSON Schema 2020-12；仅允许安全的同文档
`#/$defs/<safe-id>` reference，外部 `$ref` 禁止且绝不触发网络读取。contract URI 是
locator，不是 JSON Schema external `$ref`。

### 9.3 排序、分页与 host 注入

- 使用 MCP cursor 对 `tools/list` 分页；一个 lean descriptor 是不可拆分单元。
- 使用稳定 `DiscoveryPriority`：先暴露 capability/contract/orientation tools，再按 domain 与 tool name 排序；同一 catalog version 的顺序完全确定。
- cursor 绑定 catalog hash/version、contract mode、server instance 和下一索引，并由 server-side registry 的随机 locator 防篡改。非法、篡改、过期或跨 instance cursor 按 MCP `tools/list`/JSON-RPC 协议错误返回，不进入 `ToolEnvelope`。
- 单个完整 descriptor 无法放入 frame cap 时启动失败；不能删减 input schema、contract locator 或隐藏工具。
- MCP host/client（不是 LLM）必须跟完全部 `nextCursor`。host 可缓存静态目录，用 capability map 与当前任务选择相关 descriptors 渐进注入 LLM；这不会改变 server catalog，也不要求模型一次看到全部 61 个工具。
- 只读取第一页的 host 不兼容。具名第三方 client 的 token/cache 测量可改进兼容指导，但不是全局 release blocker，除非未来 ADR 明确承诺该 client/version。

预算与 release evidence 分为三个独立 gate：

- 每个完整 JSON-RPC page frame 的 hard cap；
- 所有页面合并后的 aggregate lean `tools/list` 不超过 **250,000 bytes**；
- 完整 Contract 2.0 registry 的逐工具 URI/hash/bytes 与 aggregate hash 闭合。

历史约 2.5 MB inline catalog 只记录“全部深层 schema 随 descriptor 广播”的旧设计。
分页本身只降低 frame 峰值；双投影才把完整 schema 从默认 discovery 移到按需读取。
CI baseline 必须分别冻结 lean catalog hash/aggregate/page bytes 与 full registry
hash/bytes，不能把 Resource 体积算成默认 LLM context，也不能以删工具、弱化 input
schema、遗漏 contract locator 或省略后续页来过 gate。host 实际注入 token 与 cache
命中作为独立观测记录。

## 10. 统一结构化结果契约

### 10.1 复用现有 ToolEnvelope

不得再造一套和生产整改设计平行的 envelope。当前 Contract 2.0
`ToolEnvelope<T>` 承担：

```text
ContractVersion
Status
Data
Error
FailedSections
Sections
Warnings
HasMore
ToolRef
TraceRef
Scope
CapabilityEvidence[]
Completeness
EvidenceBoundary
NoData
Precision
```

这些字段是**同一个版本化顶层公共 header**，不是 `Data` 中的第二 envelope，也不要求
domain DTO 继承巨大基类。`Data` 只保存工具专属强类型结果。`ToolRef`、
`CapabilityEvidence[]`、`Completeness`、`EvidenceBoundary` 和 `Precision` 对所有结果必需；
`TraceRef` 按 server/trace 适用性 required-nullable；`Scope` 对成功/部分结果必需，只有
terminal failure（例如最小可信结果无法装入预算）可以为 null。具体 nullability 由 live
output schema 锁定。

当前稳定公共字段：

```text
ToolRef
TraceRef
Scope
CapabilityEvidence[]
Completeness
EvidenceBoundary
NoData
Precision
```

公共 header 的 required/nullability 统一如下：

| 字段 | 当前 Contract 2.0 形状 | 权威语义 |
| --- | --- | --- |
| `ToolRef` | required object | tool name、contract version、CapabilityIds |
| `TraceRef` | required nullable object | caller-scoped trace ID、required-null `generationAlias` compatibility slot、可选 symbol context、canonical/ephemeral ref kind；不暴露内部 identity |
| `Scope` | required nullable object | requested selector/window、resolution status/mode、selected process/thread、candidate/included totals/details、reuse/identity-unresolved 状态；不适用时 `Status=not_applicable`，terminal delivery failure 时为 null |
| `CapabilityEvidence[]` | required array | 每 capability 的 trace/scoped status、counts、capture integrity 和 evidence IDs |
| `Completeness` | required object | 顶层集合状态；细节由 `Sections[]` 给出 |
| `EvidenceBoundary` | required object/array | 按 section/evidence ID 返回三维证据语义、provenance 和 `DoesNotProve` |
| `NoData` | required nullable object | 只有全部请求 domain section 无 data 时非 null；局部空结果放 `Sections[].NoData` |
| `Precision` | required object | identifier/metric precision、rounding、accounting 和 denominator |

所有 public properties 在 `RequireAllProperties` 模式下都出现；“可选”表示 required nullable 或内部子字段按 schema 明确 nullable，不表示运行时随意 omit。

wire property 名固定使用 camelCase：`ToolRef -> toolRef`、`TraceRef -> traceRef`、`Scope -> scope`、`CapabilityEvidence -> capabilityEvidence`、`Completeness -> completeness`、`EvidenceBoundary -> evidenceBoundary`、`NoData -> noData`、`Precision -> precision`。类型名与 wire 名不得再各自派生别名。

本文不再维护可复制的手写 JSON payload：它会与 required-null、enum registry、section
role 和工具专属 schema 漂移。current active runtime 已为全部 61 个工具返回 closed
Contract 2.0 envelope；精确字段、section 集合和排序以 live output schema、
`wpa://tools/{toolName}/sections` 的全部页及 runtime result 为准。`scope.requested` 与
`scope.selected` 是嵌套对象，不能扁平化成一个 PID；`generationAlias` 当前必须为 null，
因为 canonical TraceId 已绑定 immutable generation，Contract 2.0 不公开另一枚 generation
locator。此前没有 released legacy result wire contract；`legacy` 输入当前 fail closed，
避免把 Contract 2.0 envelope 错标为 legacy。缺少一个从未发布的 adapter 不是 0.4.x
release blocker。

### 10.2 Envelope 不变量

- `succeeded`：有合法 data；没有 top-level error；可以是完整或 Top-N。
- `partial`：已有可用 data，但请求的一部分工作失败、取消、跳过或耗尽分析预算。
- `failed`：没有可用 domain data；有稳定 error；MCP `IsError=true`。公共 `Scope`/`TraceRef` header 仍可携带安全的请求回显、resolution status 和候选实例，确保 ambiguity failure 能重放；候选不塞进 `Data`，也不得包含越权身份。
- Top-N 或分页本身不把 `Status` 改成 `partial`；它只改变 completeness/section paging。
- `structuredContent` 与唯一 text content block 必须从同一个已经脱敏、已经分页、已经拟合预算的最终 envelope 序列化，并在 JSON 语义上完全相等；二者共同暴露同一事实，不允许 summary、pointer 或 structured-only 降级形成第二套契约。
- output schema 必须保留 `Data` 的工具专属强类型，禁止退化为 arbitrary object。
- 发送前验证 structured content 与 active output schema。
- Error、warning、progress 和 text 均不能包含未脱敏 exception/path/host 信息。
- server-wide 或 lifecycle 工具没有 trace/process scope 时，Schema 使用 required nullable 字段和稳定 `not_applicable` state；不能随工具任意省略公共字段。
- 迁移期公共 `Scope/NoData/Completeness` 与旧 `Data` 内同义字段并存时，公共
  Contract 2.0 header 是权威值；适配器必须从同一内部对象投影并逐字段断言等价，
  旧字段带删除版本，不能出现双权威。
- 一个工具含多种证据语义时，`EvidenceBoundary` 必须按稳定 section/evidence ID 返回 `MeasurementBasis + Relationship + ConclusionStatus`；不能用较强的顶层 relationship 覆盖其中的 association/not-concluded 项。manifest 声明允许的 basis 和因果规则，运行时不得凭 description 自行“升级”。

### 10.3 空结果

空结果和失败必须分开。以下 selector、scope 和执行问题返回 `failed` 及稳定 error code，不能伪装成空数据：

```text
invalid_argument
process_instance_not_found
process_start_required
ambiguous_process_instance
thread_instance_not_found
ambiguous_thread_instance
trace_not_loaded
trace_access_denied
analysis_failed
budget_exceeded（没有任何可用数据时）
```

非法或越界 scope 统一映射为既有 `invalid_argument`；如果未来需要单独公开 `invalid_scope`，必须先进入版本化 error registry，不能只在某个工具中临时增加。

只有调用完成、scope 已解析但没有可用领域数据时，才返回 `succeeded` 加结构化 `NoData`：

```text
event_class_not_observed
no_events_in_scope
stacks_unavailable
symbols_unresolved
focus_not_found
```

`stacks_unavailable` 和 `symbols_unresolved` 只在请求的数据本身依赖 stack/symbol 时表示 NoData；如果工具还能返回不依赖它们的直接计数，调用应保留这些 data，并把相应 capability/section 降级为 `partial` 或 unavailable，不能把整次调用清空。

- `symbols_unresolved` 不能抹掉地址、module 或 `module!?` 等仍可用的 degraded rows。
- `focus_not_found` 只有在目标 scope 的 eligible stacked evidence 已实际扫描完成后成立；没有目标 stacks 应返回 stack capability 原因。
- `event_class_not_observed` 只陈述 materialized/parser-observed 结果，必须同时携带 capture-integrity/parser provenance。
- `not_concluded` 只能出现在 `ConclusionStatus`；`NoData` 必须给更具体的 stable reason/boundary code，不能把它当万能原因。
- composite 每个 section/evidence ID 可以有独立 `NoData`；顶层 `NoData` 只在所有请求 section 都无 domain data 时使用。

operation-local deadline/cancellation 且 transport 仍允许发送响应时，有可用 section 可以返回 `partial`，没有可用 section返回 `failed`。如果 negotiated MCP transport cancellation 已生效，服从 approved spec：不得再发送迟到的 success/error terminal frame。任何分支都不能只返回干净空数组。

## 11. 精确标识符与数值契约

### 11.1 Opaque identifier

规则：

- PID、TID 等由协议和平台保证在 JavaScript safe integer 范围内的选择器可以使用 JSON integer。
- `ulong`、ConnId、FileKey、FileObject、虚拟地址、句柄和 server-minted handle 一律使用规范字符串。
- 规范格式由 ADR 固定；地址类优先固定宽度小写十六进制，协议/业务 ID 可使用十进制字符串。
- 一个字段只能有一个 authoritative value。显示字段可以派生，但不能形成 numeric/string 双权威值。
- public-schema linter 禁止未经批准的 `ulong`，也禁止名称或 annotation 表明为 identifier 的不受限 `long`。

兼容阶段：

- 新 string 字段立即成为权威字段。
- 旧 numeric 字段仅在值 `<= 2^53-1` 时返回精确值；超出时返回 required nullable `null`，并附稳定 deprecation/precision 状态。目标 Schema 使用 `RequireAllProperties` 时禁止按工具自行省略该字段。
- 下一 breaking contract 删除旧 numeric 字段。
- 禁止为了保持旧字段非空而发送已经舍入的值。

### 11.2 Metric

每项 metric 必须从 Schema 或 result 直接得到：

```text
Value
Unit
Precision: exact / estimated / rounded
Aggregation
Denominator（百分比和比例必需）
```

任何可能超过 safe integer 的计量值必须有经过验证的上界，或使用 exact decimal string。不能用含义不明的 `Count`、`Duration`、`Bytes`，也不能把 trace-global 计数作为 scoped 分母。

## 12. Top-N、分页、截断与 wire budget

### 12.1 Section paging 扩展

复用现有 `ToolSectionPage`，以兼容字段扩展：

```text
Section
Mode: none / top_n / cursor
Requested
Returned
TotalAvailable
TotalState: exact / lower_bound / unknown
HasMore
MoreState: present / absent / unknown
SortKey
SortDirection
TieBreakers[]
NextCursor
ContinuationAvailable
TruncationReason
NoData
```

### 12.2 Top-N 聚合

- **优先计算完整聚合集合和独立分母，再排序、截断。** 如果 analyzer 确实完成了全量聚合，必须返回 public `top` 与 wire-budget 裁剪之前的精确聚合项数量；不能用 raw event count、返回行数或裁剪后的行数代替。
- “优先精确”不授权伪造精确总量。若 analyzer 因固定 source cap、内部候选上限或预算而没有枚举完整聚合集合，只能返回经证明的 lower bound，或 `TotalAvailable=null + TotalState=unknown`；不得根据 `rows.Count == limit` 推断总量、存在下一行或已经 terminal。
- `HasMore=true` 表示已证明还有至少一项，必须有精确 total 或 `top+1` probe 等正证据。`HasMore=false` 只表示“没有已证明的额外项”；当 `MoreState=unknown` 时绝不表示完整或 terminal。
- 当前 reviewed adapter 的证明模式按下表解释；模式名是实现审计词汇，不是允许客户端猜测的隐式契约：

| Proof mode | 允许发布的 total/more 语义 | 禁止推断 |
| --- | --- | --- |
| `Exhaustive` | 已证明投影穷尽；`TotalState=exact`，total 为裁剪前聚合项数，`MoreState=absent` | 不能把未经审查的普通数组自动归为 exhaustive |
| `FixedLimitExactTotal` | analyzer 另行提供并交叉校验裁剪前 exact total；固定展示上限可以产生 `MoreState=present` | 固定上限本身不能证明 exact total |
| `TopPlusOne` | 返回不超过请求的 N 行；观测到第 N+1 行时只发布 `lower_bound` 和 `MoreState=present`，不足 N+1 时才可证明 exact/absent | N+1 是 lower bound，不是精确总量；没有 cursor 时不能伪造 continuation |
| `ConservativeLimit` / `FixedLimitConservative` | 未饱和时可证明 exact/absent；恰好饱和时发布 `TotalState=unknown`、`TotalAvailable=null`、`MoreState=unknown`、`TruncationReason=source_limit_saturated` | 饱和不证明下一项存在，也不证明源已耗尽；compatibility `HasMore=false` 不得被读成 terminal |
| `ExactRequestedCount` | 仅用于输出基数由请求结构本身定义、且 runtime 验证返回数恰好等于该基数的 section，例如固定数量时间桶；可发布 exact/absent | 不能把它用于任意 Top-N，也不能用请求上限冒充潜在候选总量 |

- 排序方向和稳定 tie-breaker 必须机器可读。
- 改变 `top` 不得改变统计总量、百分比分母或 focus 是否存在。
- 没有可重放 continuation 的聚合结果可以返回 `MoreState=present/unknown` 并给出增大 `top` 或改用其他工具的重放建议，但必须同时返回 `ContinuationAvailable=false`、`NextCursor=null`；绝不能制造 cursor。只有 `MoreState=absent` 且 total/穷尽证明一致时，section 才是 terminal。

### 12.3 时间线分页

- 使用 opaque keyset cursor，不使用不稳定 offset。
- cursor 由服务器签发和解释，客户端只负责原样回传，不能拥有、修改或从字段中自行构造 continuation。cursor 绑定 principal/session、tool/contract version、opaque trace generation、`SymbolContextId`、privacy profile、规范化 query hash、scope、排序和最后键，并使用 server-side registry 或 MAC 防篡改。
- 相同时间戳使用稳定 sequence/tie-breaker。
- 时间线/inventory 页只有在分页前 exact total、当前 start/index 或 last key、返回数和全局 `HasMore/NextCursor` 相互一致时才能声明 exact/terminal。`ContinuationAvailable=false` 或 `NextCursor=null` 本身不能把 `MoreState=unknown` 升级为 terminal；这种状态属于不可继续但仍不完整性未知，而不是“已返回全部”。
- trace 替换、卸载、过期或 query/scope/symbol/privacy context 改变时返回拟新增的稳定 timeline-tool `invalid_cursor`，绝不跨 generation 复用；该 code 必须在实现前加入版本化 tool error registry 和所有 contract snapshots。它与 §9.2 的 `tools/list` 协议分页错误属于不同 response shape，E2E 必须分别锁定。
- cursor 不包含原始路径或敏感字段。
- `qrc_` 是通用 query-result locator domain，也用于 `inspect_trace`。binding 固定包含 principal、TraceId、immutable generation、catalog/tool version、contract、显式 `SymbolContextId=null`、privacy profile、规范化 domain/goal query hash、排序、phase/index 和 last key。
- `inspect_trace` 的全局顺序固定为 capability `(domain asc, capabilityId asc)`，随后 workflow `(workflowId asc)`；首屏携带完整 orientation，续页只携带 evidence continuation，但 capture/symbol/self-attribution evidence boundary 每页保留。完整遍历必须恰好闭包 51 个 active capability 与全部 workflow，无重复、遗漏或重排。
- 全局 `HasMore/NextCursor` 与 section continuation 分开：capability 中间页只给 capability section cursor；能力到工作流的切换页只给全局 cursor；workflow 中间页只给 workflow section cursor；终页全部为 false/null。禁止把全局 cursor 复制到未开始或已耗尽的 section。

### 12.4 精确响应预算

继续采用现有生产整改设计的完整 JSON-RPC frame 预算，不定义第二套大小上限。出口处理顺序固定为：

1. 从唯一 structured object 构建 result 并应用隐私策略。
2. 更新 paging/completeness/truncation metadata。
3. 从当前最终化 envelope 重新生成兼容 text content。
4. 验证 structured schema，并计算包含 request ID、text、structured content 和 framing 的完整 UTF-8 frame。
5. 若超限，只在 manifest 声明的 row/section 边界确定性缩减，然后回到步骤 2；每次缩减后都必须重新生成 text、重新验证 schema、重新计量完整 frame。
6. 若最小合法成功 envelope 仍无法容纳，丢弃原 result，重新构建并计量固定 `response_too_large` failure；不得发送半截 frame 或保留旧 text。若原结果已经是失败，则保留原稳定 `error.code`，仅按公开规则压缩消息和预算证据。

wire-budget fitting 只能减少公开 detail，不能把裁剪后的 `rows.Count` 回写成伪造的 pre-truncation exact total，也不能把 `unknown/lower_bound` 美化为 `exact`。对于 candidate、evidence、section summary、executed-call provenance 或其他存在跨 section 引用的 composite，默认把相互引用的集合视为一个原子 fitting 单元：要么一起保留并通过引用闭包校验，要么一起省略并明确 completeness/budget 原因。只有 manifest 明确声明可独立裁剪、且裁剪后不存在悬空 evidence ID、错误 total、错误控制流或被放大的 conclusion 时，才允许单独缩减其中一个 section；否则最小可信 composite 放不下就返回完整 `response_too_large` failure，不能发送内部自相矛盾的 partial success。

`ToolScope` 的 `candidateTotal/includedTotal` 始终是裁剪前精确计数。响应预算确实省略候选细节时，`Candidates/Included` 必须同时为空且 `detailCompleteness=omitted_due_to_response_budget`；没有任何候选细节可省略时保持 `complete`。禁止保留一条样本却声称 complete。4,096-byte 是单个 tool-envelope fitter 的静态下界，不是当前完整生产 catalog 的可启动下界；统一 discovery preflight 当前要求至少 35,858 bytes。对独立 fitter 测试或不暴露完整契约发现面的嵌入式 runtime，若该下界连一条 mirrored 成功页也物理不可容纳，应返回完整 mirrored `response_too_large`，不能启用 summary 或提高 hard cap 来掩盖事实。

禁止依赖 MCP host、客户端或模型上下文自行裁剪；客户端静默裁剪会把可用性问题升级为正确性问题。

## 13. Trace/Symbol 生命周期与 annotations

本节首先定义公开分层。路径验证、artifact ownership、lease/drain、symbol egress 和 worker isolation 默认继续服从现有生产整改计划；principal-scoped handle、generation single-flight、artifact retention、symbol-context canonicalization 和 cache-key 的目标方向已由 §1.1 接受，但其具体算法和 wire 选择在相应 ADR 与 owning plan 同步前仍不是可猜测的实现细节。

### 13.1 目标公开生命周期

```text
load_trace(path)
    -> 路径策略、source snapshot、ETL/ETLX conversion
    -> traceId + TraceFactsSnapshot

prepare_symbols(traceId, symbolPolicyRef?)
    -> 只检查启动时批准的本地 candidate root，并写入 private verified store
    -> 返回 immutable symbolContextId
    -> 不修改 trace handle 或进程全局 _NT_SYMBOL_PATH

analysis_tool(traceId, scope, symbolContextId?, ...)
    -> 只读已验证 trace generation
    -> 不隐式转换、不隐式下载、不接受任意路径

unload_trace(traceId)
    -> 显式 retirement/drain/dispose
```

必须区分四种身份，不能把它们压成一个 token：

```text
TraceGenerationKey   内部不可变 source/artifact/backend 身份
TraceId              调用者作用域内的 opaque 引用
SymbolContextId      不可变符号策略和 readiness 快照
ArtifactKey          可跨 handle 复用、由 quota/LRU 独立治理的派生产物
```

trace ID 继续服从既有设计：CSPRNG、保留前缀、未知 ID 不回退为路径、绑定 immutable source generation、进程内作用域；未来共享 host 还必须绑定认证主体。Registry 的逻辑键必须是 `(principal/session, traceId)`；stdio 模式也要显式定义单一 session principal，不能把“本机进程”当成没有隔离模型。trace ID 不是授权凭据，但仍按 bearer-like 敏感 locator 处理：默认不写普通日志、telemetry 或错误文本，并受每主体 handle 数、创建速率、TTL 和 tombstone 上限约束。

同一主体已知 handle 的 `expired`、`unloaded` 和 `unknown` 可以通过稳定、Schema 化的 lifecycle detail 区分；跨主体查找必须与随机未知 ID 对外不可区分，避免形成存在性 oracle。它们都是调用失败，不得返回 `rows=[]`。当前没有 active `trace_cache_status` 工具，`lifecycle.trace.handle` 保持 declared gap；若未来新增此类 inventory/status 工具，默认也只能返回调用主体自己的聚合状态和经过脱敏的 handle 状态，列出其他主体、原始路径或全局 registry 需要独立管理员权限。

同一主体在同一服务器进程中重复 `load_trace` 同一个未变化 source generation 时，必须返回同一 canonical trace ID，或者把 `idempotentHint` 明确设为 false。无论选择哪种公开语义，底层 conversion/backend 必须按 `TraceGenerationKey` single-flight 和共享，不能为每个 handle 重载同一 2 GB trace。

兼容阶段仍接受 raw path 的 query 时，resolver 不能仅凭 canonical path/mtime 猜测 immutable generation。固定顺序是 `policy validation -> 安全打开 source handle -> same-object/source snapshot identity -> TraceGenerationKey single-flight -> backend lease`；确认同一 generation 后才可复用已有 conversion/backend。它不得绕过 Registry 另开一份 trace，也不得因一次 query 永久制造未返回给调用者的 handle。一个 composite 调用只解析一次 source、获取一个 generation lease，所有子分析共享该 lease 和 facts snapshot。

raw-path compatibility result 仍必须有可追溯 `TraceRef`：ADR 要么规定返回并计入 quota 的 canonical trace ID，使调用者可以继续使用；要么返回明确 `ephemeral`、仅本请求有效且不可重放的 opaque reference。禁止创建永久但不返回的 handle，也禁止让 path result 的 provenance 为空。

`unload_trace` 只退休调用者 handle/registry binding，并在 lease drain 后释放对应 backend 引用；最后一个 handle 消失不等于立即删除 immutable ETLX artifact。artifact retention/eviction 由独立 quota/LRU 策略决定，避免下一次 load 无条件重复转换。显式永久删除 artifact 如有需要，应是独立管理操作。

`prepare_symbols` 返回新的 immutable `SymbolContextId`；它不能原地改变现有 trace ID 的查询语义。analysis/inspect 的符号质量必须指明使用的 symbol context revision。未指定时 `symbolContextId=null`，只返回 trace 固有 PDB identity，所有 local readiness/frame lookup 均为 `unmeasured`，不探测磁盘或网络；需要 local-only symbols 也必须显式调用 `prepare_symbols` 创建 context。相同 generation、规范化 symbol policy、resolver 版本和已验证 symbol inputs 可以 canonicalize 为同一 context；任何变化都创建新 context。

`SymbolContextId` 与 trace ID 使用相同的 principal/session binding、CSPRNG opaque token、TTL、quota、unknown-equivalence 和日志脱敏规则。旧 context 若承诺可重复，必须 lease/pin 它引用的已验证 symbol artifacts；artifact 无法继续保留时返回拟注册的 `symbol_context_expired`，不能静默切换到当前 cache 中更强或更弱的文件。

所有依赖符号的结果、readiness 和负缓存，其 key 至少包含：

```text
TraceGenerationKey
SymbolContextId/revision
resolver version
PDB identity（name/GUID/age）
module/image identity + architecture
module base/RVA 或规范化 lookup address
symbol artifact content identity
privacy/profile
contract version + normalized query/scope（分析结果适用）
```

创建更强的新 symbol context 必须使该 context 下的 unresolved/negative lookup 重新求值，不能复用旧 context 的低解析率结论，也不能反向改变旧 context 的可重复结果。

### 13.2 annotation 阶段矩阵

下表是 rollout 阶段语义，不是三个同时可用的 profile。当前 0.3 development
runtime 位于最后一行；中间 compatibility window 仍被 release policy 阻断，第一行
只保留重构前风险证据。

| 阶段 | 查询输入 | 查询实际行为 | annotation 要求 |
| --- | --- | --- | --- |
| 历史 pre-refactor | raw path | 可能加载、转换、写 cache、符号外联 | 按最坏路径保守 |
| 计划 compatibility window（release-blocked） | path 或 trace ID | path 仍可触发副作用；ID-only 调用可只读 | 如果 SDK 不能按启动模式变化，继续保守 |
| 当前 0.3 development ID-only | query 只接受 trace ID | 不转换、不下载、不修改全局状态 | query 声明 read-only/closed-world/non-destructive |

目标工具类别：

| 类别 | ReadOnly | OpenWorld | Destructive | 说明 |
| --- | ---: | ---: | ---: | --- |
| `load_trace` | false | false | false | 只在允许根和服务器 artifact store 内创建派生产物 |
| `prepare_symbols` | false | true | false | 只读取启动批准的本地 candidate root，并可能写 private verified store；不访问网络 |
| analysis/query | true | false | false | 只读已加载 generation |
| `unload_trace` | false | false | true | 释放服务器状态；若最终规范解释不同，由 ADR 和真实行为测试锁定 |

annotations 只是 hint；路径、网络、权限和配额仍由代码强制。

`IdempotentHint` 必须与 §1.1 ADR 一起锁定并由重复调用测试验证：canonical `load_trace` 才能标 true；每次 mint 新 handle 时为 false；`prepare_symbols` 只有规范化输入返回同一 context 时才为 true；重复 `unload_trace` 若返回稳定 `already_unloaded` 且无额外副作用可标 true。compatibility query 的 annotations 按整个启动 profile 可接受输入的最坏行为声明，不能因某次参数恰好是 trace ID 就动态美化。

## 14. Routing、Composite 与 Query Planner

### 14.1 TraceFactsSnapshot

首次成功加载时构建轻量、不可变、generation-bound facts：

```text
Trace metadata
Provider/event counts
Process/thread instance index
Per-domain stack coverage
PDB identity summary（仅 trace 固有 metadata）
Trace generation/stamp
```

约束：

- 合并 capability detector 与 metadata 的公共扫描。
- 不默认预索引全部事件或物化所有 analyzer 结果。
- snapshot 只能复用到同一 immutable generation。
- local symbol readiness、frame resolution 和 negative lookup 不属于 generation facts；它们只存在于显式 `SymbolContextId` assessment/cache。
- snapshot 建造本身必须受取消、预算和 worker policy 约束。

### 14.2 Query Planner

Planner 从 Active Catalog 获取 capability requirements 和 analyzer operations：

- 只有同域 evaluator 在 capture-integrity、identity 和 parser coverage 足够时确定为 `unavailable`，Planner 才能把 analyzer 当作“不可能产生证据”而跳过。`unknown`、loss-affected、identity-unresolved 或 parser coverage unknown 不得按 unavailable 优化；若因预算仍未执行，必须在 provenance/NotConcluded 中记录资源原因。
- composite 在一次 dispatcher 中注册多个兼容 accumulator。
- 同一聚合可派生多个排序视图，例如 hard-fault bytes 与 latency。
- 全部 analyzer 共享 scope resolver、operation context、deadline 和 work counters。
- 逻辑工具调用与物理 trace pass 分开记录。

每个 composite 返回：

```text
LogicalAnalyzersExecuted[]
PhysicalTracePassCount
ScannedEventCount
MatchedEventCount
PhaseDurations
BudgetTerminationReason
ExecutedToolCalls / provenance
```

优化前后的单工具范围、计数、排序、三维证据边界和 completeness 必须等价。性能提升不能通过少扫描一部分事件或改变分母实现。

## 15. 工具描述、Resources 与 Prompts

每个工具 description 保留一个短而完整的选择契约：

```text
Answers
Requires
Scope
Ordering
Completeness
DoesNotProve
SideEffects
Cost
```

不得从 tool description 移走以下关键内容：

- 是否跨进程/线程生命周期聚合；
- 时间窗口和单位；
- 默认排序和是否可能 Top-N；
- 是否依赖目标事件堆栈或符号；
- measurement basis、relationship 和 conclusion status 分别是什么；
- 是否能支持因果结论；
- 是否会进行文件或网络访问。

长示例、WPA/PerfView 导航映射、完整 workflow 和反模式放入同源 Resources。Prompts 只服务 human-in-the-loop；Tools-only 客户端仍必须完成全部关键调查。

## 16. 迁移策略

### Phase 0：冻结并建立可追溯基线

实施状态（2026-08-02）：**当前实现基线已生成并审查，自动 gate 保持其闭合**。
pre-refactor snapshot 只保留迁移前事实；corrected active snapshots 已按当前 61-tool
catalog 生成，原
`release_blocked:corrected_active_contract_baselines_not_release_approved` 已关闭。baseline
closure 不等于提前宣称所有 full-suite/package gate 已通过；这些结果仍由自动测试分别
报告。已落地的可执行证据为：

- `tests/WpaMcp.Tests/ContractBaselines/legacy-active-tools.v1.json` 与同目录 builder/test：尽管保留了历史文件名，它冻结的是 pre-refactor regression evidence，不是 released compatibility floor；其中记录当时 61 个 active tools、5 个既有 structured tools及逐工具 description/annotations/参数/default/schema hash/byte。基线另保留 commit `2dfb459` 的 61/5/178,923-byte 观测，避免把并行正确性修复后的 179,107-byte 目录误称为初始值。
- `tests/WpaMcp.Tests/ContractBaselines/legacy-dto-inventory.v1.json`、同目录 builder 与 `tests/WpaMcp.Tests/LegacyDtoInventorySnapshotTests.cs`：冻结 132 个 public Output DTO 类型、44 个 response、1,618 个 public property 及 JSON 名称/CLR 类型/nullability/default/description/`JsonIgnore`，并显式列出 public `ulong`、ID-like integer、collection、Top-N 与 timeline 审查候选；候选分类是审查路由，不冒充运行时能力事实。
- `eng/contract-baselines/correctness-disposition.v1.json`：把 §5.3 的 17 项逐项绑定到 source/test、兼容处置和迁移规则。
- `eng/contract-baselines/correctness-field-matrix.v1.json`：冻结适用条件、权威来源、单位、legacy 路径、Contract 2.0 语义槽和不可弱化规则；文件中的历史属性名 `vNextSemanticSlot` 只记录迁移来源，不代表当前 contract 版本仍未决定。
- `tests/WpaMcp.Tests/Phase0CorrectnessBaselineTests.cs`：校验 issue/field-group 完整性、分类数量与源码追溯路径。
- `eng/contract-baselines/side-effect-inventory.v1.json` 与 `tests/WpaMcp.Tests/SideEffectInventoryTests.cs`：从同一 active catalog 对每个当前工具逐项冻结 ID-only 与 raw compatibility profile 下的 path/disk/network/process-state/external-storage 最坏可达副作用；`load_trace` 是唯一 raw source 入口，`prepare_symbols` 是唯一 verified-symbol store mutation 边界，普通 ID-only query 只读取 owned artifact/pinned verified symbols。closed-state、source evidence 和 annotations 不低报由测试校验。
- `tests/WpaMcp.Tests/ContractBaselines/legacy-structured-stdio.v1.json` 与 `LegacyStructuredStdioGoldenTests.cs`：通过真实 `WpaMcp.Program` stdio 完成 initialize、`tools/list` 和 5 个既有 structured tools 的 success/failure/boundary 记录。该 immutable legacy evidence 的 SHA-256 为 `055be9ddde2c21effad7a9d3c27c6630977b600c7c4fc1be3f891392a662e2b7`；它明确标注 `LEGACY-STDIO-SCHEMA-001`，不把已知错误冻结为正确行为。
- `eng/contract-baselines/observed-contract-defects.v1.json`：记录真实 stdio 递归校验发现的 `WIRE-SCHEMA-001`。旧 SDK 输出在 10 个 success/boundary case 中累计缺少 286 个 schema-required nullable 路径；生产 filter 只补齐 schema 允许为 `null` 的 required property，绝不为缺失的 non-null 事实造值，并保持 text JSON 与 `structuredContent` 同源。

corrected active snapshot 位于
`tests/WpaMcp.Tests/ContractBaselines/active-tools.v1.json`、
`active-dto-inventory.v1.json` 与 `active-structured-stdio.v1.json`；lean payload、
pagination 和 full-contract registry 分别由
`tool-list-payload-budget.v1.json`、`tools-list-pagination.v1.json` 与
`tool-output-contract-registry.v1.json` 锁定。本轮把它们与 active manifest/profile 一起
生成、审查并纳入自动 gate，因此 Phase 0 baseline blocker 已关闭。exact package stdio
和完整测试套件仍是独立验证项，不能从 baseline 文件存在反推其结果。

交付：

- 从真实 active catalog 生成完整工具、input schema、output schema/null、description、annotations snapshot。
- 生成 capability/tool/DTO inventory。
- 清点所有 public `ulong`、ID-like `long`、Top-N、时间线、无界 collection 和隐式副作用。
- 保存 Phase 0 pre-refactor stdio 行为和工具目录 byte baseline。
- 对当时 5 个 structured 工具逐个保存成功、失败和边界结果 golden；历史回归证据按工具名和实际 schema 判断，不能只保存“5”这个数量。

Phase 0 还必须生成“正确性字段非回退矩阵”。对当前已经适用并公开的语义，后续统一 envelope 只能归一化或增强，不能删除、改弱或用泛化 warning 取代。至少清点并锁定：

```text
ProcessStartUs / ThreadStartUs / ThreadGeneration
ScopeStatus / ScopeMode / SelectedProcess / IncludedProcesses / PidReuseObserved
CapabilityStatus / MatchedEventCount / MatchedIntervalCount / NoDataReason
Trace* / Scoped* counts and denominator scope
identity-unresolved / unmatched-interval / event-loss / parser-coverage states
per-domain event-weighted and metric-weighted StackCoverage
StackMetricName / MetricAccounting / StackSemantics / synthetic-unknown state
FrameResolutionMeasurementState / FrameResolution
MetricPrecision / RowMetricAccounting / ExactTotalAccounting
VirtualAlloc allocated/freed/operation-traffic/net-observed semantics
temporal FileObject/FileKey resolution state
MeasurementBasis / Relationship / ConclusionStatus / confidence / provenance / DoesNotProve
```

并非每个工具都需要所有字段；矩阵必须记录字段适用条件、权威来源、单位、默认值、旧 JSON 路径和 Contract 2.0 JSON 路径。迁移校验比较语义而不只比较字段名，尤其不能让 `threadGeneration`、process instance scope 或 scoped/domain coverage 在包进通用 envelope 后消失。

每个 Phase 0 snapshot 差异和 §5.3 Issue ID 必须标记兼容处置，不能把 pre-refactor golden 误当成“旧行为永远正确”：

```text
preserve                    正确且仍受支持，Contract 2.0 compatibility projection 不得回退
normalize_only              只改变载体/命名，权威值与语义保持等价
known_incorrect_must_change 已知会误导或给出错误事实，必须版本化修正，旧 golden 仅作缺陷证据
deprecated                  暂时保留兼容投影，给出删除版本和替代字段
```

§5.3 的正确性债务默认属于 `known_incorrect_must_change`，除非 Phase 0 证据证明当前实现已经修复；已经修复的项转为 `preserve` 回归约束。snapshot gate 必须允许经过 ADR 批准、带专用 golden 的预期正确性变化，不能迫使新实现复制旧错误。

退出条件：任意工具增删、参数/default、annotation 或 DTO 字段变化都会触发显式 snapshot diff。

### Phase 1：优先消除静默正确性错误

实施状态（2026-08-01）：**主要 analyzer/source-fact 修复及其 Contract 2.0
投影已经落地；最终全量与 large-trace gate 仍需通过。** 统一 envelope、section
completeness、full-frame fitting 和全工具 live schema 已由 Phase 4 接线，因此下面的
旧“等待 Phase 4”备注只保留为实施历史，不再代表当前 runtime。任何尚未通过完整
test/package/真实 ETL 的边界继续保持可验证 gate，不能用 focused tests 冒充发布证明。

- `COR-PRECISION-001` 的 analyzer 事实修复已完成定向验证：每个 stack sample 以稳定 token 绑定 checked `Int64` 权重，raw/normalized 的 `DoneAddingSamples()` 排序不再使 metric 与 stack 错配；17 个 top-stack 与 caller/callee 使用 exact accumulator，并以 function ordinal 处理并列。测试覆盖乱序、同时间、递归、`2^24`、`2^53`、`long.MaxValue` 与 overflow。首版裸并行列表曾被独立对抗审查判为 P0 并拒绝集成，当前版本是修正后的实现。
- `COR-ID-001` 已接入 live Contract 2.0：`NetConnectionRow.ConnIdText` 是 invariant unsigned-decimal 权威值；deprecated numeric `ConnId` 仅在 JavaScript safe integer 范围投影，否则 required-null，并携带 precision/deprecation state。其他 reviewed opaque identifiers 同样以规范字符串为权威值。
- `COR-PAGING-001` 的审计发现四条 composite 会从已裁剪数据派生 total、分类或控制流；这些事实污染已先行修复，随后接入统一 public section contract、top+1/cursor proof 和 full-frame fitting。
- `COR-PAGING-001` 的当前 active implementation closure 已完成验证：`diagnose_window` 的 wait 总量使用 scope exact total，security presence 使用分页前 evidence-class exact aggregation，`diagnose_high_wait` 的 scheduler routing 使用未裁剪 wait reasons，slow-startup 的上游输入截断只能返回 `partial`/`lower_bound`/`not_concluded`；histogram exact-requested、CPU batch per-selector boundary、row-local boundary 和 opaque locator schema 均有 focused tests。独立 fail-closed gate 从每个 active output root 反射遍历全部 reachable DTO/collection/tool-path proof，并与 manifest pageable occurrences 双向核对；精确数量由同提交 baseline 生成，不在本文硬编码。该实现闭包不代表第三方客户端、rollout、large-trace、package、审批、tag 或 release asset gate 已完成。
- `COR-TRUNCATION-001` 的当前 active implementation closure 已完成验证：security duration/presence 的有界示例只通过强类型 `WindowEvidenceSample` 返回，每个样本明确 `Representative=false`、`MetricAttributable=false`、`SampleScope=returned_rows_only`；`Details` 只保留穷尽 annotations；`CompositeResultContractValidator` 要求 `/samples` 的 typed `SamplesBoundary`，reachable-collection gate 将其登记为 `typed_nested_boundary`。fresh security、hostile validator 与 closure 回归全绿。该实现事实不代表外部客户端、large-trace、package、审批或发版门禁完成。
- `COR-SCOPE-001/002`、`COR-WAIT-001`：process/thread 解析统一保留半开窗口、PID/TID 复用间隙和 endpoint 语义；MemoryResource、GC/JIT、CLR contention、network、Wait/blocked-time 与 CPU precise 的 unresolved diagnostics 不再把别的生命周期计入 scoped count；high-wait candidate/replay 保留精确 `processStartUs`。
- `COR-VM-001` 与 `COR-FILEMAP-001`：VirtualAlloc 分离 alloc/free/operation traffic/signed observed delta；FileObject/FileKey 使用带 event order 的 temporal binding，冲突返回 typed `ambiguous_temporal_mapping`，并对每个聚合行返回五类 exact mapping-state counts，不以 display sentinel 代替机器状态。
- `COR-STACK-001`、`COR-SYMBOL-001`、`COR-CAUSAL-001`、`COR-HEURISTIC-001` 与 `COR-COUNT-001` 的底层事实/措辞已分离：stack coverage 与目标事件/scope 同域，PDB identity 不再冒充 frame resolution，ReadyThread 只表述 association，security name match 保持 heuristic provenance/confidence，TraceLog parsed count 不再冒充 raw ETL count。
- 最终对抗审查进一步发现并修复 aggregate metric 继承首个 top-N sample 身份的问题：wait/security 的完整范围总量不再绑定首个返回进程、文件或时间，`hard_fault_bytes` 不再用最大单次故障时间锚定 aggregate bytes；FileIO `ReadBytes + WriteBytes` 排序使用 checked `Int64`。186/186 项聚焦回归通过，主项目编译 0 warning/0 error，审查范围内无残留 P0/P1。

交付：

- 先修 analyzer/source facts：实例选择、Wait 分母、VirtualAlloc 方向、temporal file resolver、integer metric、解析计数口径和证据措辞。
- 为 opaque 64 位 ID 生成精确 string 权威值；在最终 wire contract 前，禁止新增任何把 unsafe number 声称为 exact 的投影。
- Top-N/时间线 analyzer 计算确定排序、精确 total 或诚实 lower bound，并暴露内部 completeness model；最终 section envelope 在 Phase 4 接线。
- `diagnose_slow_startup` 等遗漏工具补齐内部 scope/capability/no-data outcome，不先发明临时顶层 wire shape。
- 关闭 §5.3 中所有 analyzer-level Phase 1 Issue ID；每项都有 owner、受影响 tool/DTO/analyzer 清单、兼容处置和专用 golden，不允许只依赖通用 envelope 测试。
- 修正与实现不一致的 description，例如空窗口边界和无堆栈 synthetic frame 语义。

退出条件：底层事实和 golden 已正确，已知错误不再被新的 Contract 2.0 projection 依赖；这一阶段不是最终 MCP contract release gate，截断/unsafe pre-refactor wire 风险要到 Phase 4 关闭后才能宣称完成。

### Phase 2：Active Catalog 与最小 contract/error 骨架

实施状态（2026-08-02）：**生产 Active Catalog、lean `tools/list` 协议分页与完整
contract registry 已完成；当前 active set 为 61 个工具。** 它们由同一 validated model
构造并注册，`Program` 不再使用 `WithToolsFromAssembly`。protocol descriptor 保留完整
input schema 与 `_meta["wpa-mcp/outputContract"]`，但 `outputSchema=null`；production
wrapper 另持有同源完整 schema 做发送前验证。Resource 与 `get_tool_contract` 从同一
canonical bytes 投影，原生工具名、input schema、调用参数与 60 个既有结果形状不变。

cursor 为 `tlc_` 加 128-bit CSPRNG locator；server-side state 绑定 server instance、
catalog version、contract mode、discovery order 和 next index。重试不消费 parent
cursor，错误 binding 不撤销 owner cursor，malformed/tampered/expired/cross-instance
cursor 返回协议级错误而不是 tool result。单个 lean descriptor 不可拆分，startup 使用
最大允许 serialized request-id、实际 JSON-RPC response 与 stdio LF 做 fail-closed
preflight。另一个同源 preflight 遍历固定 8,192-byte contract page registry，测量全部
Resource 与 `get_tool_contract` 镜像 frame；配置必须同时满足两项，不能只用较低的
`tools/list` subsystem minimum 启动。

`eng/contract-baselines/tools-list-pagination.v1.json` 锁定 lean catalog hash、page bytes
与 aggregate SHA-256；`tool-output-contract-registry.v1.json` 独立锁定每个完整 schema
的 canonical bytes/hash。aggregate default `tools/list` hard gate 是 250,000 bytes；
历史约 2.5 MB inline 观测不再是当前 discovery budget。具名第三方 client 的实际注入
token/cache 行为属于兼容性观测，不是全局 release blocker；package stdio harness 则
必须证明全部页面和两条 contract lookup path 闭合。

交付：

- `capabilities.v1.json`、`tool-contracts.vN.json`、Issue/compatibility disposition 和校验器。
- programmatic `McpToolCatalog` 成为生产唯一目录。
- 冻结公共字段位置、nullability、错误 registry、cursor domain 和 envelope actual version 的 ADR。
- `tools/list` 确定性、防篡改分页，以及覆盖完整契约 Resource/Tool lookup 的统一 startup minimum-response preflight。
- lean descriptor/full schema 双投影、content-addressed Resource 与 Tools-only contract fallback。
- typed outcome projection、manifest pageable pointer 和 schema generator 由 pre-refactor snapshot 做非回退核对，不发布第二套 legacy result shape。

Phase 0 snapshot 没有被任何 released version 建立为 public result wire contract，因此本
阶段不实现 legacy adapter。`legacy` 启动值稳定 fail closed，防止把 active Contract 2.0
envelope 错标；这不是 0.4.x release blocker。

退出条件：每个启用工具恰好出现一次、都有 capability/manifest/full contract；所有
catalog 页面合并后完整且不超过 lean aggregate gate；每个 URI/hash 可从 Resource 与
Tools-only path 重组；协议 cursor 与 tool cursor 错误形状分离；服务器不会为适配 cap
隐藏或动态激活工具。

### Phase 3：显式 trace/symbol 生命周期

实施状态（2026-08-01）：**trace lifecycle 与 symbol readiness 核心已接线；
context-bound frame resolution 和 release 物理资源证明仍开放。** `load_trace` 是唯一 raw source 入口：它验证允许的本地路径、把不可变源
快照/ETLX 放入 owned artifact store，并返回 principal-scoped canonical `TraceId`；
ID-only query 只解析已加载 generation，未知 ID 不回退到路径。`unload_trace` retire
handle 并等待/报告 lease drain，但不把 handle retirement 表述为 artifact deletion。
`prepare_symbols` 只接受 TraceId 和启动时批准的 local-only policy，返回 immutable
`SymbolContextId`；普通 query 不读 `_NT_SYMBOL_PATH`、不搜索任意目录，也不访问
symbol server。当前 build 尚无把 pinned artifact 接入 TraceEvent frame lookup 的
context-bound adapter；`resolveSymbols=true` 会稳定 fail closed 为
`symbol_resolution_unavailable` / `context_bound_frame_resolution_unavailable`，不能
回退 legacy ambient resolver，也不能把 readiness/unsymbolized frame 冒充 measured
resolution。`symbols.frame_resolution.measured` 因此仍是 declared gap。
generation-level trace facts/open/conversion 使用 single-flight。

未关闭的 gate 是 opaque converter 的瞬态物理磁盘峰值：retained quota 与单次
materialization checkpoint 不能证明整个转换过程峰值，故 runtime 保留
`release_blocked:retained_quota_only;single_materialization_checkpoint_budget;opaque_converter_transient_peak_unproven`。

交付：

- `load_trace -> traceId`、Registry/lease 和 owned artifact store。
- query 的 compatibility path/ID resolver。
- `prepare_symbols` 与 immutable symbol context。
- secure-default ID-only query 和 annotation 更新。
- 旧 runtime `set_symbol_path`/`add_symbol_server`/`diagnose_symbols` 表面已退出 active
  catalog；迁移目标是 startup-approved local roots + `prepare_symbols`，不是在查询中
  修改全局环境。

退出条件：secure-default analysis 不执行路径 I/O、转换、环境修改或网络访问；未知 trace ID 不回退路径；annotations 与实际行为一致。

### Phase 4：最终版本化 structured contract 与精确预算

实施状态（2026-08-02）：**Contract 2.0 runtime 接线已覆盖全部 61 个 active
tools；最终 release matrix 仍开放。** 统一 `ToolEnvelope<T>`、闭合 output schema、
text/structured 同对象镜像、exact-integer wire projection、reviewed per-section
contracts、manifest-declared fitting 和原子超限失败已接入 production wrapper。
完整 output schema 默认不再内嵌于 `tools/list`，但 server validation、Resource/
`get_tool_contract` 重组结果与 contract snapshot 都使用同一 canonical schema；该发现
投影变化不改变任何工具的实际 Contract 2.0 result shape。
`list_processes`、`thread_lifetime`、`process_create_timing`、`image_load_timing` 已使用
generation/principal/query/scope/privacy 绑定的 `qrc_` inventory/timeline
continuation，不再是未迁移项。

可安全裁剪的 section 会显式报告 `hasMore/moreState/continuationAvailable/
nextCursor/truncationReason=response_budget`。若连最小可信成功响应都装不下，结果
原子转成 terminal `response_too_large` failure：`data=null`、`scope=null`、空
`sections/failedSections`、`hasMore=false`，并保留 unmeasured/not-concluded 的预算
证据；它不是“原 scope 下的空分析”，也没有可继续 cursor。active baseline component
已关闭；全部真实 stdio outcome、hostile/privacy/cancel、完整测试与 package hash gate
仍由各自自动验证报告，本文不提前声明结果。

交付：

- 按 Contract ADR 版本化扩展唯一 `ToolEnvelope<T>`，加入公共 evidence/scope/precision header；failed scope candidates 和 section-scoped NoData 有明确位置。
- 所有工具启用 Schema-valid structured content 和 Contract 2.0 output schema；server/lifecycle/not-applicable nullability 统一。
- authoritative string ID、section paging、completeness、三维 evidence boundary 和旧字段等价/deprecation adapter 接入。
- production 出口执行 §12.4 的 full-frame fitting loop，每次裁剪后重建 text；operation cancellation 与 transport cancellation 分流。
- 全工具真实 stdio success/partial/failed/no-data/truncated/privacy schema/result 验证。

退出条件：不存在不可观察截断或 unsafe authoritative ID；`2^53` 边界经 JavaScript 往返不变；任何预算裁剪仍返回 text/structured 一致、Schema-valid 的完整 frame；Contract 2.0 每个工具都有 output schema。

### Phase 5：能力地图与路由

实施状态（2026-08-02）：**能力地图与同源导航 runtime 已完成，agent/client 质量
门仍开放。** validated model 当前闭合 51 个 declared capabilities、61 个 tools、15 个 goals
和 15 个 workflows。`list_capabilities` 提供 Tools-only cursor 路径；
`inspect_trace.TraceEvidenceMap` 对同一 universe 逐 capability/workflow 公开当前
trace 的 available/partial/unavailable/unknown、capture integrity、evaluator 和
recommendation 边界。trace 不支持的能力仍可发现，不会从目录中消失。

Resources 是同源、byte-budgeted 的低成本读取路径而不是唯一事实入口：
`wpa://capabilities/server`、`wpa://tools/server`、`wpa://workflows/server` 提供索引；
domain/workflow page 提供完整分片；每个 active tool 还暴露
`wpa://tools/{toolName}/sections` 及其 page。section resource 完整公开 JSON pointer、
role、ordering/tie-breakers、proof mode/limit source、evidence IDs、measurement basis、
relationship 与 declared conclusion。支持 Resources 的客户端必须跟完 index 中所有
page；Tools-only 客户端仍可通过标准 tool 路径完成调查。

每个 active tool 还从 lean descriptor 链到
`wpa://contracts/tools/{toolName}/{sha256}`；Tools-only client 通过
`get_tool_contract(toolName,page)` 得到等价 canonical schema fragments。host/client
负责遍历、缓存与渐进式 LLM 注入；server active set 始终静态完整。

`inspect_trace` 使用 generation/principal/filter/privacy 绑定的 `qrc_`，按
capability 后 workflow 的固定顺序完整遍历；首屏/续页证据上下文和各 section
continuation 已分离。未关闭的是具名客户端、goal/applicability 与 LLM overclaim
benchmark 的 release-quality 证据，不是 capability map 数据结构本身。

交付：

- `list_capabilities` Tool 和同源 Resource。
- `inspect_trace` 输出完整 Trace Evidence Map。
- capability requirement evaluator。
- goal/applicability projection 及 provenance。
- capability、tool、workflow、benchmark evidence 的追溯测试。

退出条件：每个能力和工具双向可追溯；trace 不支持的能力仍可发现，并返回准确不可用原因；Tools-only 客户端不依赖 Resource。

### Phase 6：Query Planner 与扫描优化

实施状态（2026-08-01）：**共享 TraceFactsSnapshot 与 typed planner 已投入
`inspect_trace`，composite single-dispatch 和大型 trace gate 尚未完成。** 同一
generation 的 facts 首次构建 single-flight，一次 dispatcher pass 同时产生 metadata、
capability、identity、stack coverage 与 PDB identity；并发 waiter 共享构建，最后
waiter 取消才取消 builder。`PlannerExecution` 区分 ready reuse、join in-flight 和
started build，并把本次参与的 physical pass 与 generation snapshot event count 分开。
尚未被 manifest admission 批准的 composite 明确报告 `not_admitted`，不得宣称
single-pass。实际 2GB ETL 的 wall-time、peak memory、cancel 和 response budget
回归仍是 Phase 6/最终发布 gate。

交付：

- 一次构建的 TraceFactsSnapshot。
- composite single-dispatch/multi-accumulator runner。
- logical/physical pass telemetry。
- 实际大型 trace 的 wall-time、memory、cancel 和 response budget 回归。

退出条件：`inspect_trace` 公共 facts 至多一次物理扫描；已选 composites 满足明确 pass 上限；输出契约与优化前 golden/invariants 等价。

### Phase 7：默认切换与遗留清理

实施状态（2026-08-02）：**启动级 rollout policy 与 release enforcement 已实现，
但 Phase 7 尚未完成，也没有切换版本。** `RuntimeCompatibilityPolicy` 在读取 stdin
前一次性解析 `WPAMCP_CONTRACT_MODE` / `--contract-mode` 与既有
`WPAMCP_TRACE_REFERENCE_MODE` / `--trace-reference-mode`，CLI 覆盖 env；tool call
不能切换。真实选择通过 `wpa://runtime/profile` 暴露，并进入 `tools/list` cursor
binding 与 privacy-safe telemetry。当前 0.3.0 保留已实现的 2.0 + ID-only 开发默认，
但机器状态为 `release_blocked`，因为 ADR 0005 从 0.4.x 才定义可发布 window。0.4.x
默认且唯一 result shape 是 Contract 2.0，并使用 ID-only secure default；raw-path
compatibility 仅是到 1.0 前的显式 startup switch。此前没有 released legacy wire
contract，选择 `legacy` 会 fail closed；没有一个从未发布的 adapter 不阻断 0.4.x。
1.0 因缺少完整 0.5.x raw-path 弃用窗口与 usage telemetry review 而被硬门禁阻断。
release workflow 已将
exact packaged executable 的 runtime profile、version、commit、package stdio、
active snapshots、manifest 和 artifact hashes 绑定到同一证据链。详见 ADR 0005、
`CONTRACT_MIGRATION.zh-CN.md` 与 `CLIENT_COMPATIBILITY.zh-CN.md`。

当前 active snapshots、lean discovery、pagination 与 full-contract registry baseline
已经审查并由自动 gate 绑定，不再是 runtime release blocker。opaque converter
transient-peak artifact 仍缺少通过证据，是当前保留的外部 release blocker。具名 client 的
paging/token/cache 测量只更新兼容指导，不是全局 release blocker；exact package
stdio 必须自行证明 host-side 全页遍历、lean aggregate budget 与两条 full-contract
lookup path。

此外，安装/配置脚本必须通过 package stdio gate 证明只使用当前允许的 secure symbol
options；任何仍传入已拒绝的 `--symbol-path` 的发行路径都必须在发布前修正。故本节
只声明 rollout machinery 已实现，不声明 0.4/0.5/1.0 任一 release window 已满足。

交付：

- 在 0.4.x 发布 secure-default Contract 2.0/ID-only；不引入 legacy response adapter。
- 删除旧 numeric authoritative ID；在公告窗口后删除 raw-path compatibility 与已拒绝的 legacy 配置值。
- 更新 README、Architecture、Capability Gaps、compatibility 和 changelog。
- release workflow 强制 package stdio E2E、schema snapshot、capability evidence 和 immutable version gate。

禁止通过永久并存一套 legacy 工具和一套同名/版本后缀工具完成迁移；原生工具名与
参数保持稳定，也不引入万能 dispatcher。

## 17. 测试与验收矩阵

### 17.1 Catalog 与 capability

- active catalog 中每个 tool 恰好一次。
- 每个 tool 至少一个 CapabilityId，每个非-gap capability 至少一个 tool。
- `capabilities.v1.json`、`tool-contracts.vN.json` 和 benchmark matrix 引用闭合。
- capability map 顶层锁定 `CatalogScope=wpa_mcp_declared_capabilities`、`ExhaustiveForWpa=false` 和 unlisted meaning；domain/goal filter 返回 normalized filter、过滤前/后总数与 paging 状态。
- `tools/list` 所有页面合并后恰好发现全部启用工具。
- `tools/list` cursor 由 registry 或 MAC 保护，并绑定 catalog hash/version、contract mode、server instance 和 next index；无效 base64、篡改内容、错误 mode/hash/server instance、负数/溢出 index 和已失效 cursor 都返回批准的 MCP/JSON-RPC 协议错误，不伪装成 tool result。
- page size 为 1、普通值和边界值时分别覆盖第一页、中间页和末页；有剩余工具时不得返回空页，遍历中不得重复、遗漏或改变排序。
- 每个 lean descriptor 的工具名、description、完整 input schema、annotations 与 contract URI/version/hash/bytes 都不可拆分；默认 descriptor 不内嵌 output schema。
- 所有 `tools/list` 页面合并后的 aggregate lean JSON 不超过 250,000 bytes，且 page frame 各自满足 hard cap；不得通过隐藏工具、弱化 input schema 或丢 contract locator 达标。
- 每个 advertised URI/hash 必须分别通过 Resource index/pages 与 `get_tool_contract(toolName,page)` 重组为同一 canonical schema，byte count 与 SHA-256 精确匹配；缺页、错序、hash mismatch 或 external `$ref` 均 fail closed。
- profile 禁用时 capability map 显示 `disabled_by_policy`。
- 所有可启动 profile 都保留 `list_capabilities` 与 `get_tool_contract`；提供 trace analysis 的 profile 还保留 `inspect_trace`，否则 profile validation 失败。
- 真实 stdio `tools/list` 与 normalized snapshot 一致。
- exact package stdio E2E 以 host 角色遍历全部页面并验证双 lookup path；具名第三方 client 可另记录实际注入 descriptors、token 与 cache 命中，但不是未声明 support guarantee 下的全局 release gate。
- baseline 分开锁定 lean catalog hash/bytes/pages 与 full registry 的逐工具 hash/bytes；任何一侧改变都要求显式 review，不能把历史约 2.5 MB inline measurement 当当前默认成本。

### 17.2 Schema 与结果

- 100% 工具有 active output schema 和 structured content。
- 每个工具至少一个 succeeded 结果通过 advertised schema。
- 代表性工具的 partial、failed、no-data、truncated 和 privacy-redacted 结果通过 schema。
- text 与 structured content 从同一 envelope 产生，contract-critical 字段一致。
- `additionalProperties`、nullability、required fields 和 enum strings 均由 snapshot 锁定。
- failed ambiguity 结果保持 `Data=null`，但公共 scope-resolution header 包含经授权的 candidate process/thread start/generation，可直接重放。
- server/lifecycle 工具的公共 trace/scope 字段使用 required nullable + `not_applicable`，不允许各工具随机省略。
- Contract 2.0 公共 header 与兼容 `Data` 内同义字段从同一内部 outcome 投影，并逐字段等价；任何差异 fail test。
- Phase 0 historical golden 继续逐具体工具执行，作为语义回归输入而不是可执行 legacy wire floor；runtime 只发布 Contract 2.0 result shape。
- 非回退矩阵逐工具验证 process/thread instance、scope、双口径 domain stack coverage、symbol measurement、count/no-data 和三维 evidence/provenance 字段的适用语义；Contract 2.0 envelope 不得把它们降级为自由文本 warning。

### 17.3 精度与完整性

- `2^53-1`、`2^53`、`ulong.MaxValue` 经 Node/JavaScript 往返后 authoritative ID 完全一致。
- schema linter 拒绝未经批准的 public `ulong`/ID-like unsafe integer。
- 构造 `top+1` 聚合项时，`HasMore=true` 且 `TotalState=lower_bound`，不能声称精确总数；完成全量聚合的独立 case 才能返回 `TotalState=exact`。
- 修改 `top` 不改变总量、分母、scope 或 focus-not-found 语义。
- 多字节 UTF-8、超大单行、最低合法 cap 和 cap+1 均返回完整合法 frame。
- timeline cursor 对 principal、trace generation、tool/contract、query/scope、symbol context、privacy profile 任一改变都明确失效，并返回 tool-envelope error；`tools/list` cursor 的协议错误另行验证。
- 每次 response fitting 裁剪后重新生成 text；最终 text 与 structured rows/paging 完全一致，二者共同满足 cap。

### 17.4 证据真实性

- PID/TID 复用返回准确 instance scope，不跨生命周期静默聚合。
- 目标域无事件、scope 内无事件、无 stack、部分 stack、符号未解析分别返回唯一状态；`event_class_not_observed` 同时给 parser/capture-integrity provenance。
- CPU stack 不会使 FileIO/CLR/HardFault capability 可用。
- PDB identity、local readiness 和 frame resolution 不互相替代。
- 每个 stack-dependent capability 同时验证 event-count 与 metric-weighted coverage 的分子/分母、单位、accounting、`[0,100]` 百分比和 synthetic unknown；两种 coverage 不得互换。
- `MeasurementBasis`、`Relationship`、`ConclusionStatus` 独立验证；direct event 不自动升级 association 为 causal，heuristic 不冒充 direct。
- loss、identity-unresolved 或 parser unknown 足以影响域时 capability 为 `unknown/partial`，Planner 不得把它跳过为不可能。
- 空结果始终带具体 NoDataReason；`not_concluded` 还需 boundary code，`focus_not_found` 必须证明已扫描 eligible stacks，composite 可返回 section-scoped NoData。
- 不含 MCP server/worker 实例的 ETL 对 self-performance 返回 `not_concluded`。

### 17.5 副作用与运行时

- secure-default analysis 不创建 ETLX、不修改环境、不下载符号。
- path/UNC/reparse/ADS/device 和 symbol URL 对抗测试允许策略校验所必需的 bounded read-only metadata/handle I/O，但必须在任何写入、网络访问、artifact 创建、trace 解析或转换前失败。
- load、prepare-symbols、query、unload annotations 与真实行为逐项验证。
- 同一主体重复加载同一 source generation 时，公开 trace ID/idempotency 与 ADR 一致，且底层只发生一次 conversion/backend load。
- compatibility raw-path query 先经过 policy/open-handle/snapshot identity，再与 trace-ID query 命中同一 generation/backend；一次 composite 只持有一个共享 generation lease，不为子分析重复转换、open 或注册永久 handle，并返回 canonical 或明确 ephemeral TraceRef。
- 两个主体可以获得不同 trace ID，但只能在 policy 允许时共享同一 immutable backend/artifact；一个主体 unload 不得破坏另一个主体的 lease。
- registry lookup 按 principal/session 隔离；跨主体 token 与随机未知 token 对外不可区分，stdio singleton principal、TTL、rate/handle quota 和 tombstone 上限均有对抗测试。
- 当前没有 active `trace_cache_status`；若未来关闭 `lifecycle.trace.handle` gap 并新增
  inventory/status 工具，它不得泄漏其他主体的 handle、原始路径或全局 cache 细节。
  普通日志、telemetry 和错误不出现 trace ID。
- 最后一个 trace handle unload 后 artifact 是否保留完全服从独立 retention policy；重新 load 不应无条件触发 conversion。
- 不同 `SymbolContextId` 的 readiness/result 不得互相污染，旧 context 的查询语义保持不变。
- symbol-dependent result/readiness/negative-cache key 覆盖 generation/context/resolver、PDB 与 image/architecture、address/RVA、artifact content、privacy/profile、contract/query scope；新 context 会重新评估旧 negative lookup。
- `SymbolContextId` 的 principal binding、TTL/quota、unknown equivalence、日志脱敏和 artifact pin/expired 语义逐项验证；旧 context 不被当前 cache 静默改变。
- operation-local deadline 可按契约返回 partial/failed；negotiated transport cancellation 后没有任何迟到 terminal frame。quota、worker crash 后也没有后台无限工作或未释放 reservation。

### 17.6 路由、性能与 LLM 对抗

- 未知 trace 的 agent 在规定调用次数内使用 `inspect_trace`。
- capability unavailable 时仍能发现工具，但不会把它推荐成有证据的路径。
- capability `unknown/partial` 时 Planner 不把 analyzer 当作不可能；预算跳过有结构化 provenance。
- `inspect_trace` recommendation、manifest requirements 和真实 analyzer capability 一致。
- 记录 wrong-tool rate、overclaim rate、missing-caveat rate 和 mean calls；不能只统计 JSON parse success。
- 固定对抗场景覆盖：静默截断、unsafe ID、无目标 stacks、低符号率、heuristic security scan、PID reuse、budget partial。
- 结论 verifier 独立检查是否错误使用“全部、唯一、确认、根因”等过强措辞。
- benchmark 阈值先基于固定模型/prompt/schema/fixture 建立，再冻结；不能在同一变更中放宽阈值以通过失败结果。

## 18. 风险与固定决策

| 风险 | 固定决策 |
| --- | --- |
| output schema 使 catalog 超限 | 同源双投影：lean descriptor 保留 locator，完整 schema 按需读取；不删 schema、不隐藏工具 |
| 分页客户端只读取第一页 | package host fail test，具名 client 标为不兼容；服务器不伪装成完整目录 |
| capability map 与工具实现漂移 | Active Catalog 启动校验和双向完整性测试 |
| capability map 过大 | Tool/Resource 自身分页，但所有省略都返回明确 `HasMore` |
| “完整能力”被理解成 WPA 全集 | 顶层声明 catalog universe、非 WPA exhaustive 和 unlisted meaning |
| trace assessment 误用全局 stack 状态 | 每 capability 注册目标域 evaluator 和同口径分子/分母 |
| event loss/parser unknown 被误判为无能力 | capture-integrity 参与状态机；受影响时只能 unknown/partial |
| envelope 变成巨大通用 object | 稳定通用头 + 强类型 `TData`，禁止 arbitrary data |
| failed 时丢失可重放 scope candidates | `Data=null`，但公共 scope-resolution header 保留经授权候选 |
| text 与 structured content 冲突 | 从同一已最终化 envelope 生成，两者共同计入预算 |
| 兼容 numeric ID 继续失真 | string 为唯一权威；unsafe 值不再填旧 numeric 字段 |
| trace ID 被当作授权 | 明确不是凭据；未来共享 host 绑定 principal |
| Query Planner 改变语义 | golden/cross-tool invariants 比较优化前后结果 |
| profile 被用于静默瘦身 | 默认 full；禁用必须由管理员配置且能力地图披露 |
| Resource 客户端覆盖不足 | 关键发现同时由 Tool 提供，Resource 只做同源镜像 |
| 旧计划与当前 Wpa 命名/工具数漂移 | 实施前从 active catalog 重新生成 inventory，禁止复制旧常量 |
| 分页被误认为降低总 prompt 成本 | 分开度量 page bytes、aggregate lean bytes、按需 registry bytes 和实际注入 token |
| host 渐进注入被误认为动态 tool activation | server catalog 保持静态完整；host 选择只影响 LLM context，不修改 server state |
| Resource/Tool fallback 漂移 | 两条路径与 server validator 共用 canonical schema，逐 URI/hash/byte closure test |
| 缺 legacy adapter 被误报为 0.4 blocker | Phase 0 snapshot 未形成 released wire contract；0.4.x 只发布 Contract 2.0，不实现 adapter |
| 每次 load 生成新 handle/backend | canonical caller handle + generation-level single-flight，或诚实取消 idempotent hint |
| trace handle、backend、artifact、symbol revision 混为一体 | 使用四种独立身份和独立生命周期；所有结果记录所用 generation/context |
| unload 最后 handle 就删除 ETLX | handle retirement 与 artifact retention 解耦，由独立 quota/LRU 回收 |
| public generation 泄露内容指纹 | 只返回 principal/server-scoped opaque alias，或完全依赖 trace ID |
| symbol artifact eviction 改变旧 context | artifact lease/pin；无法维持时稳定 `symbol_context_expired`，不静默重解析 |
| `top+1` 被当作精确 total | 只返回 lower bound；精确 total 必须完成全量聚合 |

## 19. 需要单独 ADR 锁定的事项

实施状态（2026-08-01）：下列开放选择已由接受状态的后续 ADR 锁定；该状态只解除实现门禁，不代表相应 runtime 已完成。

- `docs/decisions/0003-active-catalog-contract-and-evidence-registry.md`：事项 1–8 的 active catalog、contract、identifier、cursor、capability map 与 evidence registry。
- `docs/decisions/0004-trace-and-symbol-lifecycle.md`：事项 9–12 的 trace/symbol 生命周期、retention 与 annotations。
- `docs/decisions/0005-planner-rollout-and-compatibility-window.md`：事项 13–14 及 compatibility/default/removal release window。

1. `CapabilityId` 的版本和废弃规则。
2. capability/tool manifest 是声明式文件、生成代码还是二者组合；目标必须是一个 validated active model。
3. Contract 2.0 `ToolEnvelope<T>` 的版本号、公共 header、failed scope candidates、section NoData、required-nullable，以及 lean discovery/full contract 同源双投影规则。
4. 64 位 ID 的规范字符串格式及兼容字段删除版本。
5. `ToolSectionPage` 的兼容扩展方式；timeline cursor 的 MAC/registry、principal/symbol/privacy binding 和 tool error。
6. `tools/list` discovery priority、协议 cursor error、单页/aggregate lean gate、full-registry closure 与 host/client 分页责任。
7. `list_capabilities` 与 `inspect_trace` 的 catalog universe、capture-integrity、分页/goal filter 和 symbol-context 契约。
8. `MeasurementBasis/Relationship/ConclusionStatus` registry，以及允许 causal 的证据规则。
9. raw-path compatibility 到完全 ID-only 的版本窗口；Phase 0 result snapshot 不形成 legacy runtime obligation。
10. canonical per-principal trace ID、opaque generation alias、raw-path ephemeral/canonical TraceRef、共享 backend、artifact retention 和 unload 的生命周期关系。
11. `SymbolContextId` 的创建、复用、principal binding、artifact pin/expiry、cache key 和 analysis 参数契约；同步 `symbol_context_expired` error。
12. `load_trace`、`prepare_symbols`、`unload_trace` 的最终 annotations 和 `IdempotentHint`。
13. 2025-11-25 stateful profile 与 2026-07-28 协议/SDK 后续升级边界。
14. 哪些 composite 值得进入 single-pass planner；必须由真实 pass/latency 和 agent benchmark 决定。

ADR 未决不能成为静默猜测实现的理由；不影响 Phase 1 正确性修复的 ADR 可以并行完成。

## 20. 完成定义

只有同时满足以下条件，本文定义的重构才算完成：

- 默认完整能力通过标准 MCP Tool 路径可发现，且无需私有说明或 Resources。
- capability、tool、workflow 和 evidence reference 由一个 validated active model 生成或验证。
- 每个启用工具都有精确 input/output schema 和 Schema-valid structured result。
- 默认 `tools/list` 保留全部静态工具、完整 input schema 与 contract locator，不内嵌深层 output schema；所有页面合并后的 lean JSON 不超过 250,000 bytes。
- 每个完整 Contract 2.0 schema 都可通过 content-addressed Resource 和 `get_tool_contract(toolName,page)` 重组并验证 byte/hash，且与 server 发送前校验所用 schema 完全同源。
- host/client 负责遍历分页、缓存并渐进注入 task-relevant descriptors；server 不做 session-time 动态激活，也不引入万能 dispatcher。
- 每个结果机器可读地披露 scope、capability、completeness、precision 和 evidence boundary。
- 不存在不可观察截断、unsafe opaque 64 位 JSON number 或把 raw event count 当 total groups 的契约。
- 当前 trace 不支持的能力仍可发现，并准确说明 unavailable/partial 原因。
- secure-default query 不含隐式 trace conversion、symbol download 或全局环境修改。
- annotations、description、Schema、runtime behavior 和 snapshot 一致。
- capability detector/metadata 的公共 facts 至多一次物理扫描，目标 composites 满足批准的 pass 上限。
- 真实 stdio、全部目录分页、hostile input、取消、large trace、schema snapshot 和 LLM 对抗 gate 通过。
- breaking changes、compatibility flags、弃用截止版本和迁移示例写入 changelog。
- 0.4.x 只发布 Contract 2.0 result shape 与 ID-only secure default；未发布的 legacy adapter 不属于完成条件或 release blocker。
- release tag、程序集版本、被测试包、能力文档和上传资产来自同一 gated commit。

最终判定标准不是“工具都能返回 JSON”，而是：

> LLM 能完整知道服务器会什么、当前 trace 能证明什么、结果省略了什么，以及哪些结论证据仍然不支持。
