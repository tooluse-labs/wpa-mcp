# MCP 客户端兼容性

wpa-mcp 完整暴露能力，也完整暴露证据边界。兼容客户端应降低 discovery 成本，
但不能静默丢弃工具、schema、continuation 或 uncertainty 字段。

当前 validated development surface 是 60 个 active tools，对应 51 个
declared capabilities、15 个 goals、15 个 workflows。这些数量只用于核对完整遍历；客户端
必须读取实际 catalog version 与 totals，不能把数字写死。

## 必需行为

- 在 stateful stdio 上协商仓库锁定的 MCP protocol profile。
- 持续跟随 `tools/list.nextCursor` 直到为空，合并时不得遗漏、重复或重排。只消费
  第一页的客户端不兼容。
- 保留每个工具的完整 input/output schema；tool + schema 是不可拆分分页单元。
- 按 schema 声明的 JSON Schema 2020-12 dialect 解析每个 output schema。server
  只允许 `#/$defs/<safe-id>` 形式的单段同文档引用，并拒绝 dangling、cycle、
  multi-segment、anchor 与 external ref。忽略或不能解析这些 local refs 的客户端
  不兼容，不能把被引用 schema 静默当成 permissive；external ref 绝不能触发网络请求。
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
选择工具后，读取 `wpa://tools/{toolName}/sections` 及其全部页，才能依赖该工具每个
section 的排序与证据语义。这些 Resource 是 Active Catalog 的同源、frame-budgeted
投影；它们不允许客户端跳过 `tools/list` 后续页，也不能对 Tools-only 模型隐藏能力。

## 证据状态

仓库内 package harness 会通过 raw stdio 测试实际发布 executable：使用最大允许
serialized request-ID 初始化、遍历全部 tool/capability page、验证 schema 与同步
structured/text、读取 capability/runtime resource、检查完整 frame budget，并确认
测试前后 executable hash 不变。另一条用例证明恶意首帧会在 telemetry/trace/
symbol 可变副作用之前被拒绝。

这是 protocol/package 证据，不等于每个具名第三方客户端都已验证。各支持客户端/
版本的 prompt-schema token、prefix-cache 行为与完整分页合并证据仍为
`release_blocked:supported_client_matrix_incomplete`。server 不会为迁就忽略 cursor
的客户端而隐藏后续工具。

release workflow 要求通过的
`eng/contract-baselines/supported-client-matrix.v1.json`；其中每个 client row 都
必须记录 full-page consumption、实测 schema token 与实测 prompt-cache 行为。
该文件当前不存在，因此 runtime profile 与 workflow 都保持 blocked，不会把 raw
stdio harness 冒充 client evidence。

corrected active snapshot 文件可以在 release approval 之前存在。runtime 会继续保留
`release_blocked:corrected_active_contract_baselines_not_release_approved`，直到
snapshots、manifests、profile、package executable 与 commit 被作为同一证据集审查。

## Profile 支持

| Runtime profile | 客户端预期 | 当前实现 |
|---|---|---|
| Contract 2.0 + ID-only | closed envelope schema；`load_trace`/TraceId lifecycle | 可运行的开发 profile |
| Contract 2.0 + raw compatibility | 同一 envelope；raw path 已弃用并可能创建 canonical handle | 仅显式启动兼容；1.0 删除 |
| Legacy + 任一 trace mode | Phase 0 legacy golden，而不是重命名后的 Contract 2.0 | 未实现；启动 fail closed |

版本默认值和删除日期见 `CONTRACT_MIGRATION.zh-CN.md` 与 ADR 0005。
