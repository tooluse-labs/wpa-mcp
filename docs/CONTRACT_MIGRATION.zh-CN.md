# Contract 与 trace-reference 迁移

本文落实 ADR 0005 已接受的 release window。这里的模式是启动配置，不是
per-call 参数。`WPAMCP_CONTRACT_MODE`、`WPAMCP_TRACE_REFERENCE_MODE`、
`--contract-mode`、`--trace-reference-mode` 都在读取 stdin 前解析；选中的
组合在该 server 进程内不可改变。

## 当前 active surface

validated development catalog 会在运行时声明当前 tool、capability、goal 和 workflow
总数，客户端不能写死某次 snapshot。每个 active tool 在完整契约 registry 中都有唯一
closed Contract 2.0 output schema，并把同一个 finalized envelope 同步投影到
`structuredContent` 和 text JSON。`tools/list` 只携带 lean discovery descriptor：
完整 input schema 加完整契约 URI/version/hash，不要求内嵌深层 output schema。历史
61-tool/5-structured-tool snapshot 只用于迁移证据，不是当前 runtime。

`list_capabilities` 与 `inspect_trace.TraceEvidenceMap` 提供能力/证据发现。
同源、byte-budgeted Resources 的索引位于 `wpa://capabilities/server`、
`wpa://tools/server` 和 `wpa://workflows/server`。每个 active tool 都链接到
`wpa://contracts/tools/{toolName}/{sha256}` 这一不可变完整输出契约。客户端按其
byte-range page index 拼接 fragment 并校验 size/hash；Tools-only 客户端通过确定性的
`get_tool_contract(toolName, page)` 分页取得同一份 canonical bytes。两条路径共享固定
8,192 UTF-8-byte 页边界与稳定 page identity；启动时按最大合法 request ID 测量实际
Resource/Tool frame，低于 active catalog 下限的配置会在 stdin 前失败（当前审查值为
35,858 bytes）。每个 active tool
还会链接到
`wpa://tools/{toolName}/sections`；必须继续读取其 numbered pages，才能获得完整的
per-section ordering、completeness proof、evidence、measurement、relationship 与
conclusion contract。Resources 用来降低重复 discovery 成本，不替代 Tool-only 路径。

## Release 矩阵

| Release line | 必须的默认值 | 允许的显式兼容项 | 删除/门禁 |
|---|---|---|---|
| 0.3.x 开发态 | 当前源码为 Contract `2.0` + `id_only` | raw-path `compatibility`；拒绝 `legacy` | 不属于 ADR 0005 可发布 release line |
| 0.4.x | Contract `2.0` + `id_only` | 显式 raw-path `compatibility` | 需要 Phase 0–4 correctness、lean-discovery/full-contract closure、stdio 与 lifecycle security 证据 |
| 0.5.x | Contract `2.0` + `id_only` | 显式 raw-path `compatibility` | 需要 capability-map、migration 与 raw-path deprecation telemetry 证据 |
| 1.0.0+ | 只允许 Contract `2.0` + `id_only` | 无 | 需要完整一个 0.5.x 弃用窗口和 usage telemetry 审查 |

没有任何已发布 wpa-mcp 版本把 Phase 0 snapshot 建立为受支持的 public result wire
contract。该 snapshot 只是历史 regression evidence，不是可执行 compatibility floor。
选择 `legacy` 会在启动时返回
`unsupported:no_released_legacy_result_contract_exists;contract_2.0_is_the_only_runtime_shape`。
这样可防止把 Contract 2.0 envelope 冒充 legacy，但缺少 legacy adapter 不会阻断
Contract 2.0-native 的 0.4.x 发布。

## 配置与优先级

环境变量写法：

```json
{
  "env": {
    "WPAMCP_CONTRACT_MODE": "2.0",
    "WPAMCP_TRACE_REFERENCE_MODE": "id_only"
  }
}
```

命令行写法：

```text
wpa-mcp.exe --contract-mode 2.0 --trace-reference-mode id_only
```

CLI 覆盖环境变量。contract 值是封闭且区分大小写的 `legacy` / `2.0`；trace
reference 接受 `compatibility`、`id_only`（以及等价的 `id-only`）。未知、已删除
或尚未实现的组合会在 MCP transport 开始服务前失败。

## 客户端迁移

1. initialize 后读取 `wpa://runtime/profile`，并把其中的 `contractMode`、
   `traceReferenceMode`、warnings 和 blockers 与诊断证据一起保留。
2. Contract 2.0 应消费 `structuredContent`。只有需要深层客户端校验时，才按 lean
   descriptor 中的 contract URI/hash 获取完整 schema。Tools-only 客户端调用
   `get_tool_contract(toolName, page)`；两条路径必须具有相同固定页边界，按 byte order
   拼接 fragment，并校验声明的 UTF-8 size/hash。text 是同步渲染，不是第二份事实来源。
3. 用 `load_trace` + 返回的 opaque TraceId 替代 raw path；不要重建或持久化
   server 内部路径。
4. 由 MCP client/host 而不是 LLM 遍历全部 `tools/list` cursor page。cursor 绑定
   server instance、catalog、ordering 和 contract mode；重启或 profile 改变后不得
   重放。host 随后可以用 capability map 只向 LLM 注入当前任务相关的 lean
   descriptor，但不能改变 server catalog。
5. 保留 exact string identifier、evidence/completeness state、NoDataReason 与
   process/thread instance selector；不得从空 Rows 单独推导结论。
6. 独立解释每个 `sections[]` 项。Composite 的不同 section 可以有不同 ordering、
   proof mode、measurement basis、relationship 和 conclusion，不能制造一个假的
   tool-wide comparator 覆盖它们。
7. 把 budget fitting 当成契约的一部分。可续页 section 会显式提供 cursor 与
   `truncationReason=response_budget`；terminal `response_too_large` failure 的
   `data=null`、`scope=null`、sections 为空且 `hasMore=false`。它不是请求 scope 下
   的空分析，也不能继续分页。
8. `prepare_symbols` 只表示 verified local readiness。当前 build 因缺少
   context-bound TraceEvent frame adapter，会让 `resolveSymbols=true` 以
   `symbol_resolution_unavailable` fail closed；必须保持 frame resolution unmeasured，
   不能回退到 legacy ambient lookup。

仓库不会永久复制一套 `*_v2` 工具，也不会实现未发布的 legacy result adapter。
Contract 2.0 是唯一结果形状；独立的 raw-path compatibility switch 仍在 1.0 删除。

## 运维检查

`wpa-mcp.exe --runtime-profile` 不启动 MCP，直接输出默认 profile；
`wpa-mcp.exe --validate-release-profile` 在 version line、默认组合、correctness
evidence 或弃用历史门禁不满足时返回 78。release workflow 会对实际 packaged executable
运行它们，并校验 package stdio evidence、版本、commit、schemas 与 snapshots。
corrected active baselines 已在本轮审查并关闭。`externalKnownBlockers` 当前仍会因
opaque converter 瞬态峰值证据未通过而阻断 eligible；workflow 也会独立校验 catalog/
contract baselines 与剩余 evidence artifact，因此单独修改 runtime 常量不能绕过门禁。

具名第三方 client/version 运行可以测量 catalog aggregation、实际注入 descriptor 的
token 与 prompt-cache 行为。除非未来 ADR 显式承诺该具名 client/version，这些数据只
是非阻断兼容性观测。

corrected active snapshots、lean measurements、pagination evidence 与 full-contract
registry 已在本轮一并重建和审查。安装/配置仍必须通过 package stdio startup；
secure-default 会拒绝旧 `--symbol-path`，应迁移为批准的 local roots/store +
`prepare_symbols`。
