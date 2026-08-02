# Contract 与 trace-reference 迁移

本文落实 ADR 0005 已接受的 release window。这里的模式是启动配置，不是
per-call 参数。`WPAMCP_CONTRACT_MODE`、`WPAMCP_TRACE_REFERENCE_MODE`、
`--contract-mode`、`--trace-reference-mode` 都在读取 stdin 前解析；选中的
组合在该 server 进程内不可改变。

## 当前 active surface

2026-08-01 的 validated development catalog 包含 **60 个 active tools、51 个
declared capabilities、15 个 goals、15 个 workflows**。每个 active tool 都广告闭合的
Contract 2.0 output schema，并把同一个 finalized envelope 同步投影到
`structuredContent` 和 text JSON。历史 61-tool/5-structured-tool snapshot 只用于
迁移证据，不是当前 runtime。

`list_capabilities` 与 `inspect_trace.TraceEvidenceMap` 提供能力/证据发现。
同源、byte-budgeted Resources 的索引位于 `wpa://capabilities/server`、
`wpa://tools/server` 和 `wpa://workflows/server`。每个 active tool 都链接到
`wpa://tools/{toolName}/sections`；必须继续读取其 numbered pages，才能获得完整的
per-section ordering、completeness proof、evidence、measurement、relationship 与
conclusion contract。Resources 用来降低重复 discovery 成本，不替代 Tool-only 路径。

## Release 矩阵

| Release line | 必须的默认值 | 允许的显式兼容项 | 删除/门禁 |
|---|---|---|---|
| 0.3.x 开发态 | 当前源码为 Contract `2.0` + `id_only` | raw-path `compatibility`；拒绝 `legacy` | 不属于 ADR 0005 可发布 release line |
| 0.4.x | `legacy` result + raw-path `compatibility` | Contract `2.0`、`id_only`，可分别 opt in | 经审查的 Phase 0 legacy result floor 可运行前不得发布 |
| 0.5.x | Contract `2.0` + `id_only` | 显式 `legacy` result 和/或 raw-path `compatibility` | 需要 supported-client paging/cache 证据与 corrected active baselines |
| 1.0.0+ | 只允许 Contract `2.0` + `id_only` | 无 | 需要完整一个 0.5.x 弃用窗口和 usage telemetry 审查 |

active runtime 目前没有可信的 legacy result adapter。选择 `legacy` 会在启动
时返回
`release_blocked:not_implemented;phase0_legacy_floor_is_not_projected_by_the_active_runtime`。
这样可防止把 Contract 2.0 envelope 冒充 legacy。其直接后果是：即使显式
Contract 2.0 开发 profile 可以运行，当前代码也不能发布符合 ADR 的 0.4.x。

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
2. Contract 2.0 应消费 `structuredContent` 及 declared output schema；text 是同步
   渲染，不是第二份事实来源。
3. 用 `load_trace` + 返回的 opaque TraceId 替代 raw path；不要重建或持久化
   server 内部路径。
4. 遍历全部 `tools/list` cursor page。cursor 绑定 server instance、catalog、
   ordering 和 contract mode；重启或 profile 改变后不得重放。
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

仓库不会永久复制一套 `*_v2` 工具。兼容性是相同 active tool name 外围的启动
adapter，并有明确的 1.0 删除期限。

## 运维检查

`wpa-mcp.exe --runtime-profile` 不启动 MCP，直接输出默认 profile；
`wpa-mcp.exe --validate-release-profile` 在 version line、默认组合、legacy floor
或弃用历史门禁不满足时返回 78。release workflow 会对实际 packaged executable
运行它们，并校验 package stdio evidence、版本、commit、schemas 与 snapshots。
`externalKnownBlockers` 还会持续阻断 eligible：corrected active baselines、0.5+
supported-client matrix、opaque converter 瞬态峰值证据都必须 release-approved。
workflow 会独立要求并验证这些 evidence artifact，因此单独修改 runtime 常量不能
绕过门禁。

即使 corrected active snapshot 文件已经出现在 working tree，blocker 也不会自动
消失。文件存在不等于批准：必须从同一 commit/profile/manifests/packaged executable
重新生成并完成审查。安装/配置也必须通过 package stdio startup；secure-default 会
拒绝旧 `--symbol-path`，应迁移为批准的 local roots/store + `prepare_symbols`。
