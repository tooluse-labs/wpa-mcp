# 符号准备配方

当前 secure profile 使用显式、local-only 的符号生命周期。它不读取
`_NT_SYMBOL_PATH`，不访问 symbol server，不检查 trace 目录，也不搜索任意磁盘路径。

必须分开以下证据状态：

1. **Trace PDB identity**——trace metadata 携带的 PDB name、GUID、age。
2. **Local candidate**——在 approved root 下、允许路径形状中发现的文件。
3. **Verified readiness**——candidate 被实际打开、完整 identity 匹配、复制进 private
   verified store 并 pin。
4. **Observed frame resolution**——stack query 实际尝试并解析了 code-frame name。

`prepare_symbols` 可以建立状态 3；它故意把状态 4 报成 unmeasured。当前 build 没有
用于状态 4 的 context-bound TraceEvent adapter，因此 resolution 保持 declared gap。
null/unmeasured 不是 0%。

## 配置启动策略

配置一个或多个 absolute local candidate root，以及与其互不包含的 private verified
store：

```powershell
wpa-mcp.exe --symbol-local-root "C:\Symbols" `
  --symbol-store-root "$env:LOCALAPPDATA\WpaMcp\symbol-store"
```

重复 `--symbol-local-root` 可批准多个 root。等价环境变量为：

```text
WPAMCP_SYMBOL_LOCAL_ROOTS=C:\Symbols;D:\BuildSymbols
WPAMCP_SYMBOL_STORE_ROOT=C:\Users\me\AppData\Local\WpaMcp\symbol-store
```

只要配置 candidate root，就必须配置 store root。root/store 必须是 absolute local path
且互不包含；UNC、device、alternate-stream 与 reparse traversal 会被拒绝。PowerShell
installer 默认配置：

```text
candidate root: %LocalAppData%\WpaMcp\symbol-candidates
verified store: %LocalAppData%\WpaMcp\symbol-store
```

## 放置 candidate

对每个 trace identity，preparation 只探测以下形状：

```text
<root>\<pdbName>
<root>\<pdbName>\<GUIDAge>\<pdbName>
```

Microsoft、Chromium、vendor 或 private PDB 需要在 wpa-mcp 之外获取，再复制到 approved
root；server 自身不做远端抓取。文件名或 symbol-store 位置看似正确不等于 ready：必须
实际打开 PDB，并让 name/GUID/age 与 trace identity 精确匹配。

对本地构建 binary：

- .NET/C#：启用 portable 或 Windows PDB 输出，并使用与实际部署 binary 同一次构建的 PDB。
- MSVC C++：包括 Release 在内使用 `/Zi` 与 `/DEBUG:FULL`。
- 重新 link 会改变 identity；旧的同名 PDB 不能匹配新 binary。

## 执行生命周期

让 client 按以下顺序执行：

```text
1. load_trace(path) -> TraceId
2. prepare_symbols(TraceId) -> SymbolContextId
3. 检查 verified readiness，但不要称为 frame resolution
```

SymbolContextId 是 immutable 的，并绑定 principal、trace generation、policy、resolver、
privacy/contract profile、module identity 与 verified artifact。query 必须显式提供它；
不存在 process environment、trace directory、任意磁盘或 remote server 的 ambient fallback。
当前 build 对 `resolveSymbols=true` 用 `symbol_resolution_unavailable` 与 detail
`context_bound_frame_resolution_unavailable` fail closed，绝不会调用 legacy ambient
resolver。

## 解释结果

- `ModulesWithPdbIdentity` 是 trace metadata coverage，不是 symbol resolution。
- `ModulesWithVerifiedSymbolArtifact` / readiness 表示 exact local artifact verification，
  不是 function-name lookup。
- preparation 按设计让 frame count/rate 保持 unmeasured。
- `symbols.frame_resolution.measured` 保持 declared gap，直到 context-bound lookup
  implementation 与真实 trace evidence 被正式 admitted。
- 外部/offline frame-resolution 测量不能证明本 MCP runtime 执行了 context-bound lookup。
- candidate 缺失或验证失败必须保持 not-ready/unknown，不能改写成“实测 0% frame
  resolution”。

## 历史接口警告

`set_symbol_path`、`add_symbol_server`、`diagnose_symbols` 属于旧 0.2-era interface，
不在当前 60-tool Active Catalog；secure profile 会拒绝 `--symbol-path`。不要再让当前
client 通过 wpa-mcp 配置 `_NT_SYMBOL_PATH` 或访问远端 symbol server。

生命周期与 evidence contract 详见 [`ARCHITECTURE.md`](ARCHITECTURE.md)、
[`CONTRACT_MIGRATION.zh-CN.md`](CONTRACT_MIGRATION.zh-CN.md)、
[`CLIENT_COMPATIBILITY.zh-CN.md`](CLIENT_COMPATIBILITY.zh-CN.md)。
