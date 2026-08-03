# MCP 客户端兼容性

wpa-mcp 完整暴露能力，也完整暴露证据边界。兼容客户端应降低 discovery 成本，
但不能静默丢弃 tool descriptor、continuation 或 uncertainty 字段。完整结果契约
按需提供，不再随每个 descriptor 内嵌广播。

validated development surface 会随 catalog 发布当前 tool、capability、goal 和
workflow totals。客户端必须读取实际 catalog version 与 totals，不能把某次 snapshot
数字写死。

## 必需行为

- 在 stateful stdio 上协商仓库锁定的 MCP protocol profile。
- 持续跟随 `tools/list.nextCursor` 直到为空，合并时不得遗漏、重复或重排。这是
  MCP client/host 的职责，不是 LLM 的推理任务。
- 保留每个完整的 lean discovery descriptor：name、description、input schema、
  annotations 以及 Contract 2.0 URI/version/hash。descriptor 是不可拆分页单元；
  完整 output schema 不要求内嵌其中。
- 只有需要深层客户端结果校验时才读取
  `wpa://contracts/tools/{toolName}/{sha256}`。跟随其不可变 byte-range page
  index，按顺序拼接 fragment，再校验声明的 UTF-8 size 与 SHA-256。Tools-only
  客户端通过 `get_tool_contract(toolName, page)` 的确定性分页取得同一份 canonical
  bytes。两条路径共享固定 8,192 UTF-8-byte 页边界；如果配置的 response cap 无法
  投递每个 Resource 页及其 Contract 2.0 镜像 Tool 页，server 会在启动读取 stdin 前
  失败。当前已审查 catalog 的统一最小值是 35,858 bytes，不能用较低的纯
  `tools/list` 分页最小值代替。
- 按声明的 JSON Schema 2020-12 dialect 解析已取得的 output schema。server
  只允许 `#/$defs/<safe-id>` 形式的单段同文档引用，并拒绝 dangling、cycle、
  multi-segment、anchor 与 external ref。validator 不能把被引用 schema 静默当成
  permissive；external ref 绝不能触发网络请求。
- 消费 Contract 2.0 `structuredContent`；`content` text 是其同步渲染。存在结构化
  结果时不得反过来把 text 当权威数据源。
- 原样保留 JSON string identifier。TraceId、SymbolContextId、connection/file/
  handle/address ID 或 64-bit quantity 不得经 JavaScript `number` 转换。
- `prepare_symbols` 只能解释为 verified local-readiness evidence。当前 build 对
  `resolveSymbols=true` 用 `symbol_resolution_unavailable` /
  `context_bound_frame_resolution_unavailable` fail closed；不得把 preparation、
  unsymbolized frame 或外部/offline resolution 统计改写成 MCP 实测 frame resolution。
- tool-level cursor 只能在同一 principal/session、trace generation、contract、
  query、scope、symbol context 与 privacy profile 下继续使用。
- 使用结构化 scope、capability、completeness、precision、NoData、provenance 与
  conclusion boundary。空 Rows、synthetic unknown stack、PDB identity、heuristic
  association 都不是结论。
- 按 section 独立解释 `sections[]`：保留 role、精确 ordering/tie-breakers、
  total/more state、proof mode、continuation、evidence IDs、measurement basis、
  relationship 和 conclusion status。异质 composite 不能套用一个 tool-wide 排序
  或证据声明。
- 把 `response_too_large` 当成 terminal delivery failure。其紧凑可信形状为
  `data=null`、`scope=null`、sections 为空、`hasMore=false`；它既不证明请求 scope
  没数据，也不提供 continuation。只有 section 明确发布 cursor 时才能续页。
- 读取 `wpa://runtime/profile`，确认该 server instance 不可变的 contract/
  trace-reference 模式、output-schema dialect/ref 要求，以及弃用和 release boundary。

## 能力与 section-contract Resources

支持 Resource 的客户端应先读 `wpa://capabilities/server`、
`wpa://tools/server`、`wpa://workflows/server` 的小索引，再跟完其中列出的每一页。
所选工具的 discovery descriptor 会给出不可变完整契约资源
`wpa://contracts/tools/{toolName}/{sha256}`；只有 UI、validator、code generator
或诊断需要深层 schema 时才读取，并按 page index 拼接、校验完整 bytes。还应读取
`wpa://tools/{toolName}/sections` 及其全部页，才能依赖该工具每个 section 的排序与
证据语义。这些 Resource 都是 Active Catalog 的同源、frame-budgeted 投影。

host 应完整合并并缓存 lean discovery catalog，再依据 capability map 与当前任务，
只把相关 descriptor 注入 LLM context。这种 progressive injection 是 host 的责任，
不会改变 server catalog。host 没有注入某 descriptor 时，不能把对应能力报告成
wpa-mcp 不存在。

## 证据状态

release evidence 要求仓库内 package harness 必须通过 raw stdio 测试实际发布
executable：使用最大允许 serialized request-ID 初始化、遍历全部 tool/capability
page、验证 lean descriptor、通过 Resource 和 Tools-only 两条路径解析每个完整契约
URI/hash、校验同步 structured/text、读取 capability/runtime resource、检查完整
frame budget，并确认测试前后 executable hash 不变。另一条用例必须证明恶意首帧
会在 telemetry/trace/symbol 可变副作用之前被拒绝。

这是 protocol/package 证据，不等于每个具名第三方客户端都已验证。具名
client/version 运行可以记录 page aggregation、host 实际注入的 descriptor、
prompt-schema token 与 prefix-cache 行为。这些观测用于更新兼容性表和 host 指南；
除非未来 ADR 显式承诺该具名 client/version 并定义验收条件，否则它们不是全局
release blocker。server 不会为迁就忽略 cursor 的 host 而隐藏后续 descriptor。

corrected active-tool、DTO/stdio、lean-payload、pagination、历史 hash 与完整契约
registry baseline 已在本轮审查，并由自动测试绑定到 active manifests/profile；原
corrected-active-baseline blocker 因此关闭。独立的 opaque converter transient
physical peak 现作为 0.4.x 显式接受的残余风险；runtime warning 和风险接受证据会持续
公开它，catalog gate 通过也不会把它冒充为已经证明的 hard bound。

## Profile 支持

| Runtime profile | 客户端预期 | 当前实现 |
|---|---|---|
| Contract 2.0 + canonical TraceId | lean discovery + 按需 closed envelope contract；`load_trace(tracePath)` 后使用 `traceId` 查询 | 当前 `0.6.x` contract |
| Raw-path 查询兼容 | 不再暴露；path 只能作为 `load_trace.tracePath` | `0.6.0` 删除 |
| Legacy result contract | 没有已发布 compatibility contract；Phase 0 golden 只作 regression evidence | 不支持；启动 fail closed |

破坏性输入迁移见 `CONTRACT_MIGRATION.zh-CN.md` 与 ADR 0006。
