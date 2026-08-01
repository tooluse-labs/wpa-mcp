# 符号解析配方

`_NT_SYMBOL_PATH` 接收用分号分隔的多条 entry。`SRV*<cache>*<url>` 形式是带本地缓存的符号服务器；裸路径指向放 PDB 的本地目录。

## 路径语法

```
[entry];[entry];[entry]…
```

每条 entry 可以是以下任一形式：

| 形式 | 含义 |
|---|---|
| `SRV*<cache-dir>*<server-url>` | 带本地缓存的符号服务器。缓存目录会按需创建。 |
| `SRV*<cache-dir>*\\server\share` | UNC 形式的"服务器"——团队共享的符号 drop 盘。 |
| `<bare-folder>` | 本地目录。`diagnose_symbols` 只探测 `<bare-folder>\<pdbName>`，不会把普通路径当作 symbol-store root。 |
| `cache*<dir>` | 仅缓存条目（不抓取）。少见，通常用 `SRV*`。 |

**顺序有意义**——entry 从左到右尝试，首个签名命中就停。把更快/更优先的源放前面：

- 自己开发构建的本地目录在迭代时应放**最前**（让你刚出炉的 PDB 优先于公开 PDB 命中）。
- `SRV*` 条目首次命中后就走本地缓存，几个 server 之间的相对顺序不如新鲜度重要。

## 常见组合

### 微软系统符号（始终建议加）

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols
```

解析 `ntoskrnl`、`ntdll`、`kernelbase`、`fltmgr`、`wdfilter` 等所有 Windows 公开模块。

### + Chromium 系列浏览器（Chrome、Edge、Brave 等）

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols;SRV*C:\Symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com
```

公开的 Chromium PDB 覆盖任何使用 Chromium 符号服务器的浏览器官方版本。

### + 私有 vendor 符号服务器

```
…上面的内容…;SRV*C:\Symbols*https://your-internal-symsrv.example.com/symbols
```

团队共享盘的 UNC 写法：

```
…上面的内容…;SRV*C:\Symbols*\\fileserver\symbols
```

### + 本地 dev 构建的 PDB

```
C:\src\myapp\out\Default;…上面的内容…
```

在不下载符号的 readiness 检查中，`diagnose_symbols` 对 bare-folder 只探测直接子文件 `<folder>\<pdbName>`；只有 root 来自 `SRV`、`SYMSRV` 或 `CACHE` 时才探测 `<root>\<pdbName>\<GUIDAge>\<pdbName>`。真正执行栈 lookup 时仍应把本地 build 输出目录放在公开服务器之前。

## 自家 DLL 的构建前置条件

把符号服务器的 URL 都配对了，但构建本身没产出可用 PDB——或者 PDB 签名跟最终部署的 DLL 不匹配——也救不了你。

**.NET / C#**

```xml
<PropertyGroup>
  <DebugType>portable</DebugType>
  <DebugSymbols>true</DebugSymbols>
</PropertyGroup>
```

新版 SDK-style 项目默认就是 portable PDB。老的 full framework 项目用 `<DebugType>full</DebugType>` 会出 Windows PDB，TraceEvent 原生读得了。要确认 Release 配置没把 PDB 输出关掉——很多模板 `.csproj` 默认会关。

**C++（MSVC）**

- 编译器：`/Zi`（或 `/Z7`）
- 链接器：`/DEBUG:FULL`
- 把 PDB 跟 DLL 放在同一个 build 输出目录

Release 构建里这两个开关也都要开——很多项目模板默认 Release 不出 PDB。

**签名匹配是硬要求**

PDB 和 DLL 必须共享同一个签名（GUID + age）。同样的源文件再 link 一遍签名就会变；老 PDB 不会解析新 DLL。每次重新部署二进制，对应 PDB 也要一起部署。

## 持久化路径

| 生效范围 | 怎么做 |
|---|---|
| 当前 MCP-server 进程，运行时设置 | `set_symbol_path` / `add_symbol_server`；设置持续到再次修改或 server 退出 |
| 当前 MCP-server 进程，启动时初始化 | 在 MCP 客户端 `args` 中加入 `--symbol-path "..."`（见 README 手动安装） |
| 当前用户、系统级 | `[Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH", "...", "User")` |
| 安装期写入客户端配置 | `install.ps1 -SymbolPath "..."` 把 `--symbol-path` 写进每个被检测到的 MCP 客户端 args |

团队共享场景下 JSON / TOML 的 `args` 条目通常是最优解——跟其它配置一起入库，不依赖单机状态。

## 运行时配置

```
> set_symbol_path SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols false
> add_symbol_server https://chromium-browser-symsrv.commondatastorage.googleapis.com
> diagnose_symbols C:\my\trace.etl
```

`set_symbol_path` 的第二个参数是 `append`（默认 `true`）。传 `false` 会**替换**整条路径——想从干净状态开始时用得上。`add_symbol_server` 始终是追加，且幂等。

`add_symbol_server` 未传 `cacheDir` 时，`DefaultCacheDir` 是它会使用的 fallback。旧字段 `diagnose_symbols.CacheDir` 只是该值的兼容 alias；应查看 `ConfiguredSymbolPath`，不能假定 fallback 就是当前 active cache。

## 验证是否生效

```
> load_trace C:\my\trace.etl
> diagnose_symbols C:\my\trace.etl
> cpu_top_functions C:\my\trace.etl
```

必须分清四层：

1. `HasCompletePdbIdentity` 表示 trace 带 PDB name + GUID + age 这组 lookup key；不证明本地或远端一定有该 PDB。
2. `LocalSymbolCandidates` 最多展示 10 个名称符合的文件；`LocalSymbolCandidateCount` / `LocalSymbolCandidatesTruncated` 公开完整发现总数。所有 candidate 都在展示截断之前验证，精确匹配会被移到展示列表首部。
3. `diagnose_symbols` 会直接打开每个已发现的 candidate 路径，并报告 `exact_identity_match`、`identity_mismatch`、`invalid_local_pdb_candidate` 或 `candidate_identity_unverified`。容器探针拒绝或 portable PDB reader 明确的数据错误可以证明 invalid；无法区分 candidate 不兼容与 reader 故障的 Windows DIA 错误保持 `candidate_identity_unverified`，不会伪称 candidate 损坏。只有 PDB 实际 GUID/age 匹配且格式对应的 reader 成功时，`LocalPdbReady` 才为 true。
4. 对应栈工具报告 `SymbolResolutionState`、`Stats.ObservedUniqueCodeFrameNameResolutionRate` 和 `Stats.ObservedMetricWeightedCodeFrameNameResolutionRate`。它们只统计本次查询实际触达的 code frame，排除合成 `?!?`；值为 null 表示没有可测 code frame，不是 0%。

若 identity metadata 不完整，应先在采集机上重新抓取或 merge ETL，再选择 symbol server：缺少 PDB name + GUID + age 时无法组成可执行的服务器查询。否则按 module hint 改善 PDB 可用性，再重跑栈查询。解析率还要结合事件域栈覆盖率与 synthetic-frame 数解释，不能用一个通用阈值决定所有 trace 是否可用。

## 中途改路径

每次栈查询都会快照当前配置，并只在该查询的 `SymbolReader` 中加入 ETL 所在目录，不会把目录写回 `_NT_SYMBOL_PATH`。调用 `set_symbol_path` / `add_symbol_server` 后，重新运行相应栈工具即可使用新快照。已经解析过的 frame name 可能保留在 resident `TraceLog` 缓存中；响应的 lookup state 与观测解析率描述当前查询，不宣称这是全新的 resolver session。

## 文件系统副作用与 MCP metadata

Symbol 配置、逻辑事件分析和文件系统副作用要分开理解：

- 首次查询 raw `.etl` 时可能调用 `TraceLog.OpenOrConvert`，创建或刷新相邻 `.etlx`。
- 栈查询传 `resolveSymbols=true` 时，可能访问已配置服务器，并向其缓存写入 / 下载 PDB。
- 因此 0.3.0 中所有工具的 MCP metadata 都使用 `ReadOnly=false`，因为调用可能改变 server 或文件系统状态，虽然分析器不会改写 ETL 的逻辑事件内容。调用者给出的 trace/cache path 可能是 UNC、mapped drive 或 reparse point，所以 raw-path 工具即使不执行远程 symbol lookup 也保守标成 `OpenWorld=true`；只有 `set_symbol_path` 为 `OpenWorld=false`。
- `Destructive=true` 保守覆盖相邻 ETLX 的替换 / 刷新、cache retirement 和进程级 symbol path 替换；增量追加的 `add_symbol_server` 是唯一 `Destructive=false` 工具。除 `set_symbol_path` 外其余工具均标成幂等。
- `diagnose_symbols` 只打开已发现的精确 candidate 路径，并使用空的 symbol search path。它不主动访问远端 SRV/UNC entry、也不下载 PDB。但配置中看似本地的 filesystem root 仍可能被 OS 通过 mapped drive 或 reparse point 重定向；工具不会执行昂贵的网络拓扑检测。这项 identity / readiness 检查仍不同于真正执行过的栈 lookup，也不测量 frame-name resolution。

## 缓存管理

- 默认缓存目录：`%LocalAppData%\WpaMcp\Symbols`。每个 `SRV*` 条目可以单独覆盖。
- 跟 PerfView 的 `C:\Symbols` 分开，避免两边同时跑时 PDB lock 冲突。
- 缓存只增不减，重度使用后能涨到几个 GB。
- 整个目录可以随时删——下次任意一个走栈解析的工具调用会按需重新拉。

## 常见踩坑

| 现象 | 大概率原因 |
|---|---|
| 已执行 lookup 的栈响应中，自家 DLL 的观测 code-frame 解析率很低 | 配置路径可能缺失、不可达或没有匹配签名的 PDB；先检查 `LookupState`、`LookupFailure` 和 module readiness。 |
| 微软模块解析了，你自己的 DLL 没解析 | 构建没出 PDB，或者 PDB 跟最终部署的 DLL 不是同一次构建。 |
| `set_symbol_path` 后结果变化 | 重跑栈查询并比较 query-local lookup state / rates；已解析 frame name 可能仍缓存于加载中的 trace。 |
| 内部 symsrv 超时 | 需要 VPN；要走 HTTP 代理就设 `_NT_SYMBOL_PROXY` 环境变量。 |
| 两个 MCP server 抢 PDB lock | 给每个 server 指向不同的缓存目录。 |
| `diagnose_symbols` 对某个 Windows 系统 DLL（如 `crypt32`/`bcrypt`/`setupapi`）报 "PDB not indexed" | 只要 `msdl.microsoft.com` 在 `_NT_SYMBOL_PATH` 里，符号本身照样能解出来——缺的只是每模块那行 hint。提示来自一个显式 allowlist（内核 + GDI + COM + .NET runtime + Defender + 图形 + 网络 + DWM），不在表内的模块会落到通用兜底文案。 |
