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
| `<bare-folder>` | 本地目录，递归扫描。按签名（GUID + age）匹配 PDB。 |
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

裸路径条目（没有 `SRV*` 前缀）会被递归扫描，按签名匹配 PDB。放在公开服务器之前就能让本地重编译的 PDB 优先命中。

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
| 单次工具调用 | `set_symbol_path` / `add_symbol_server` |
| 单个 MCP-server 进程 | MCP 客户端配置 JSON 里加 `env` 块（见 README 的手动安装） |
| 当前用户、系统级 | `[Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH", "...", "User")` |
| 安装期写入客户端配置 | `setup.ps1 -SymbolPath "..."` 把路径写进每个被检测到的 MCP 客户端的 env 块 |

团队共享场景下 JSON 的 `env` 块通常是最优解——跟其它配置一起入库，不依赖单机状态。

## 运行时配置

```
> set_symbol_path SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols false
> add_symbol_server https://chromium-browser-symsrv.commondatastorage.googleapis.com
> diagnose_symbols C:\my\trace.etl
```

`set_symbol_path` 的第二个参数是 `append`（默认 `true`）。传 `false` 会**替换**整条路径——想从干净状态开始时用得上。`add_symbol_server` 始终是追加，且幂等。

## 验证是否生效

```
> load_trace C:\my\trace.etl
> diagnose_symbols C:\my\trace.etl
> cpu_top_functions C:\my\trace.etl
```

看两个地方：

- `diagnose_symbols` → `Modules` 列表。每个重要模块的 `Resolved` 都应该是 `true`。
- `cpu_top_functions` → `Stats.ResolutionRate`。≥ 0.8 才有意义；< 0.5 说明大部分 top-N 是 `module!?`，结果不能用。

某个模块你以为会解析但没解析，hint 字段会告诉你应该加哪个服务器（私有 DLL 会指向"提供本地 PDB 文件夹"）。

## 中途改路径

`set_symbol_path` / `add_symbol_server` 之后，**已经在缓存里的 trace 不会重新解析符号**——`LookupWarmSymbols` 在每个加载好的 `TraceLog` 上只跑一次。要强制重查：

```
> unload_trace C:\my\trace.etl
> load_trace C:\my\trace.etl
```

正常流程"先配 MS 符号服务器、再 load_trace"不会踩到这个坑（路径在第一次 `load_trace` 之前就配好了）。只有"跑过分析器之后改路径、再跑还是 `module!?`"才会卡这里。

## 缓存管理

- 默认缓存目录：`%LocalAppData%\WprMcp\Symbols`。每个 `SRV*` 条目可以单独覆盖。
- 跟 PerfView 的 `C:\Symbols` 分开，避免两边同时跑时 PDB lock 冲突。
- 缓存只增不减，重度使用后能涨到几个 GB。
- 整个目录可以随时删——下次任意一个走栈解析的工具调用会按需重新拉。

## 常见踩坑

| 现象 | 大概率原因 |
|---|---|
| 所有 DLL 的 `Stats.ResolutionRate` 都接近 0 | 没设 `_NT_SYMBOL_PATH`，或者设了但服务器没你的符号。 |
| 微软模块解析了，你自己的 DLL 没解析 | 构建没出 PDB，或者 PDB 跟最终部署的 DLL 不是同一次构建。 |
| 昨天还能用、今天 `set_symbol_path` 后就废了 | 中途改了路径——见上面的"中途改路径"章节。 |
| 内部 symsrv 超时 | 需要 VPN；要走 HTTP 代理就设 `_NT_SYMBOL_PROXY` 环境变量。 |
| 两个 MCP server 抢 PDB lock | 给每个 server 指向不同的缓存目录。 |
| `diagnose_symbols` 对某个 Windows 系统 DLL（如 `crypt32`/`bcrypt`/`setupapi`）报 "PDB not indexed" | 提示来自一个显式 allowlist（内核 + GDI + COM + .NET runtime + Defender + 图形 + 网络 + DWM），不在表内的模块会落到通用兜底文案。只要 `msdl.microsoft.com` 在 `_NT_SYMBOL_PATH` 里，符号本身照样能解出来——缺的只是每模块那行 hint。 |
