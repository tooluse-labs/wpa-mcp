<p align="right">
  <a href="README.md">English</a> | <strong>简体中文</strong>
</p>

<p align="center">
  <img src="assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

# wpa-mcp

一个 C# 实现的 MCP server，把 Windows ETW（`.etl`）trace 分析能力——CPU、wait、image-load、文件 / 磁盘 / mmap I/O——通过任意 MCP-兼容客户端（Claude Code、Claude Desktop、Codex、Cursor）暴露出来。设计上**不绑定特定领域**：任何 Windows trace 都能用，常见用途是排查应用启动慢、子进程 fork 延迟、AV 杀毒拖慢系统、磁盘瓶颈回归等。

> **状态——PoC。** 约 17 个工具已上线，验证完成前限内部使用。仅限 Windows（TraceEvent 内核 parser 不可移植）。Apache-2.0。

> **看一个真实案例：** [一次完整排查](docs/CASE_STUDIES.md)——进程创建慢到基线的 50 倍，通过 wpa-mcp 工具链根因定位到多套 EDR 在 `PsSetCreateProcessNotifyRoutineEx` 上串行回调。同一份 trace 被两个不同 LLM agent 独立复现得到同样结论。

---

## 快速上手

装好之后（[一行命令在下面](#安装)），用自然语言问 agent，它会挑对应的工具：

```
> 加载这个 trace：C:\path\to\trace.etl
（load_trace；首次 30 秒~3 分钟构建 .etlx 索引；后续命中缓存。返回值带
 Capabilities map，让你提前知道这份 trace 里有哪些 keyword。）

> 哪些进程的 wait ratio 最高？
（list_processes orderBy=wait_ratio——trace-resident 进程会自动过滤掉）

> 父 PID <X> 下，每次 fork 的 kernel-side gap 是多少？
（process_create_timing——一个调用给出该父进程所有子进程的内核窗口分布）

> PID <X> 在 <t0> 到 <t1> 之间的 top wait 栈，附 20 个桶的直方图
（wait_top_stacks——展示 Filter Manager / driver 链如何阻塞线程）

> 钻进 "<frame!?>"：谁调用了它？
（wait_caller_callee——focus frame 的 caller / callee 邻居）
```

CPU（`cpu_top_functions` → `cpu_caller_callee`）、文件 / 磁盘 / mmap I/O、image load 等都遵循同样模式。每个 "top" 视图都有匹配的 "caller-callee" 钻取。

完整端到端走查（症状 → 工具链 → 证据 → 根因 → 改进建议）见 [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md)（英文）。

---

## 安装

### 一行命令（无需 clone、无需 build）

**PowerShell：**

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) }"
```

**Windows 上的 Git Bash：**

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash
```

两条路径做的事一样：从 GitHub Release 下载最新 zip（内含已构建好的 DLL），缓存到 `%LOCALAPPDATA%\wpa-mcp\releases\<tag>\`，执行包内的 `setup.ps1`。脚本会自动检测机器上所有 MCP 客户端（Claude Code / Codex / Claude Desktop）并把 `wpa-mcp` 注册到每个客户端。.NET 8 runtime 缺失时会 user-scope 自动安装。再次运行立即完成（命中缓存）。

通过一行命令转发额外参数：

```powershell
# PowerShell——指定 tag、限定客户端、自定义 symbol path
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag v0.1.2 -InstallArgs @('-Client','claude-desktop','-SymbolPath','SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols')"
```

```bash
# Bash——`bash -s --` 后面的 flag 会传给 install.ps1
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash -s -- -Tag v0.1.2
```

### 卸载（一行命令，对称）

可 web 调用，反向编辑相同的客户端配置文件。不动下载缓存。

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.ps1) }"
```

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
```

会从所有检测到的 MCP 客户端中移除 `wpa-mcp` 条目。release zip 缓存和符号缓存保留（要清理就删 `%LOCALAPPDATA%\wpa-mcp\` 和 `%LocalAppData%\WprMcp\Symbols\`）。

### 系统要求

- Windows 10 / 11（TraceEvent 内核 API 仅 Windows）
- .NET 8——installer 在缺失时会用 user-scope 自动安装（走 Microsoft 官方的 `dotnet-install.ps1`，不需要管理员权限）。传 `-SkipDotNetInstall` 可禁用此行为。
- 符号解析需要：设置 `_NT_SYMBOL_PATH`，或者在运行时通过 symbol 工具配置（见 [配置 → Symbols](#symbols)）。

<details>
<summary><strong>从 clone 安装（开发者）</strong></summary>

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
.\scripts\setup.ps1
```

```bash
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
./scripts/setup.sh
```

构建（Release）并向所有检测到的 MCP 客户端注册 `wpa-mcp`。幂等——重复运行可用于更新。

常用 flag：

```powershell
.\scripts\setup.ps1 -Client claude-desktop                    # 强制指定客户端
.\scripts\setup.ps1 -SymbolPath "SRV*C:\Symbols*https://..." # 自定义 _NT_SYMBOL_PATH
.\scripts\setup.ps1 -SkipBuild                                # 用现有 DLL，跳过 build
```

从 clone 卸载（`-CleanBuild` 可同时清掉 `bin/` `obj/`）：

```powershell
.\scripts\uninstall.ps1
.\scripts\uninstall.ps1 -CleanBuild
```

```bash
./scripts/uninstall.sh
./scripts/uninstall.sh -CleanBuild
```

</details>

<details>
<summary><strong>手动安装（自定义 JSON / 非标准 MCP 客户端）</strong></summary>

构建：

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
dotnet build -c Release
# DLL 位置: src\WprMcp\bin\Release\net8.0\WprMcp.dll
```

冒烟测试：

```powershell
dotnet src\WprMcp\bin\Release\net8.0\WprMcp.dll --version    # 输出 "WprMcp 0.1.0-poc"
dotnet test                                                   # 跑 xUnit 套件（需要 fixture，见 CONTRIBUTING.md）
```

然后注册到你的 MCP 客户端。DLL 路径**必须用绝对路径**。

**Claude Code**——按项目（`<project>/.mcp.json`）或全局（`~/.claude.json`）：

```json
{
  "mcpServers": {
    "wpa-mcp": {
      "command": "dotnet",
      "args": ["C:/Users/me/Dev/wpa-mcp/src/WprMcp/bin/Release/net8.0/WprMcp.dll"],
      "env": {
        "_NT_SYMBOL_PATH": "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
        "WPRMCP_CACHE_SIZE": "2"
      }
    }
  }
}
```

或者用 CLI helper：

```powershell
claude mcp add wpa-mcp --scope user -- dotnet C:/Users/me/Dev/wpa-mcp/src/WprMcp/bin/Release/net8.0/WprMcp.dll
```

（环境变量加 `-e _NT_SYMBOL_PATH=...`。）

**Claude Desktop**——`%APPDATA%\Claude\claude_desktop_config.json`，结构和上面一样。

**Codex / Cursor / 其它 MCP-兼容客户端**——server 走 stdio MCP；任何接受 `command + args` 配置的客户端都行。用上面那段 JSON。

**验证**——重启客户端后，工具会以 `mcp__wpa-mcp__load_trace` 这种命名出现。第一次对一个新 `.etl` 调 `load_trace` 会花 30 秒~3 分钟构建 `.etlx` 索引（写到 stderr）。

</details>

---

## 工具

**永远先调 `load_trace`。** 它打开 `.etl`、构建（或复用）`.etlx` 索引，并返回一个 `Capabilities` map——按 keyword 列出"有没有"的检查（`HasCpuSamples`、`HasCSwitch`、`HasFileIo`、`HasDiskIo`、`HasImageLoad`、`HasHardFaults`、`HasStackWalks`）。其他每个工具的行为都依赖采集时打开了哪些 keyword，提前读 `Capabilities` 能避免"为啥 `mmap_hot_files` 返回空？"这种意外。

| 分类 | 工具 |
|---|---|
| Meta（元信息） | **`load_trace`**（返回 `Capabilities`）、`list_processes`、`process_create_timing` |
| CPU | `cpu_top_functions`、`cpu_top_functions_batch`、`cpu_caller_callee` |
| Wait（等待） | `wait_analysis`、`wait_top_stacks`、`wait_caller_callee` |
| Image load（模块加载） | `image_load_timing`、`image_load_top_gaps`、`image_load_top_stacks`、`image_load_caller_callee` |
| 文件 / 磁盘 / mmap I/O | `file_io_top_files`、`file_io_top_stacks`、`file_io_caller_callee`、`disk_io_top_stacks`、`disk_io_caller_callee`、`mmap_hot_files`、`mmap_top_stacks`、`mmap_caller_callee` |
| Marker / 符号 | `find_marker`、`set_symbol_path`、`add_symbol_server`、`diagnose_symbols`、`diagnose_slow_startup` |

每个 "top" 视图都有匹配的 "caller-callee" 钻取，传入要聚焦的 frame，返回它的 caller / callee 邻居（按采样数加权）。

---

## 配置

### Trace 缓存

LRU，默认容量 2 条 trace。用 `WPRMCP_CACHE_SIZE=N` 覆盖。首次加载构建 `.etlx`（慢），命中缓存后即时返回。`Capabilities` 和 `TraceLog` 都按 `(path, mtime)` 缓存——重新加载相同 `.etl` 是零成本。

### 自己抓 trace

[`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)（英文）提供了一个推荐 `.wprp`，覆盖 CPU + CSwitch + FileIO + DiskIO + HardFaults + Loader stacks。最常用的抓取流程：

```powershell
wpr.exe -start tests\WprMcp.Tests\fixtures\MmapCapture.wprp -filemode
# … 复现慢的场景 …
wpr.exe -stop C:\path\to\my_capture.etl
```

### Symbols

> **如果 `cpu_top_functions` 满屏 `module!?`、`Stats.ResolutionRate < 0.8`，说明你的符号没工作。** 这是"输出垃圾"的最大单一来源。

三条配置路径（任选一条——最终都设置同一个 `_NT_SYMBOL_PATH`）：

1. **启动前设环境变量**（最干净，重启后仍生效）：
   ```powershell
   [Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH",
       "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols", "User")
   ```
2. **在 MCP 配置 JSON 里加 `env` 块**（见上面的手动安装）。最方便和团队成员共享。
3. **运行时通过工具调用**——直接对 agent 说："*把 symbol path 设成 SRV\*C:\Symbols\*https://msdl.microsoft.com/download/symbols，然后对这个 trace 跑 `diagnose_symbols`*。"

符号缓存默认 `%LocalAppData%\WprMcp\Symbols`（和 PerfView 的 `C:\Symbols` 分开，避免 PDB lock 争用）。每条 trace 的针对性推荐会出现在 `load_trace` 的返回字段 `SymbolStatus.Recommendations` 里，告诉你针对这份 trace 实际出现的模块应该加哪些服务器。

私有 vendor 符号服务器、Chromium 系列浏览器、本地 build PDB 目录等更多配方见 [`docs/SYMBOL_RECIPES.md`](docs/SYMBOL_RECIPES.md)（英文）。

---

## 故障排查

- **`dotnet: command not found`**——装 SDK：`winget install Microsoft.DotNet.SDK.8`，重启 shell / MCP 客户端。
- **MCP server 启动失败**——直接跑 DLL：`dotnet C:\path\to\WprMcp.dll --version`。如果这步就失败，要么 build 坏了要么路径错了。
- **工具列表里没有新工具**——MCP 客户端缓存了旧二进制。完全退出再启动（Claude Desktop）或者跑 `claude mcp restart`（Claude Code）。
- **`Cannot create type. Only core types are supported in this language mode`**——你的 shell 处于 PowerShell Constrained Language Mode（AppLocker / WDAC）。请用 `wpa-mcp ≥ v0.1.2`；早期 release zip 里的 `setup.ps1` 调了 `[StringBuilder]::new(...)`，CLM 拦截。
- **`SymbolStatus.Warning` 说 `_NT_SYMBOL_PATH is not set`**——server 进程没继承到环境变量。改用方案 2（在 MCP 配置 JSON 里加 `env`）或者运行时调 `set_symbol_path`。
- **`ResolutionRate < 0.5`** 但路径都设了——首次下载进行中，或者无法连接到符号服务器。等 1 分钟再试，或者跑 `diagnose_symbols` 看每个模块的解析情况。
- **`mmap_hot_files` 返回空**——trace 缺 `HardFaults` keyword。查 `load_trace` 返回的 `Capabilities.HasHardFaults` 是不是 `false`。用 `MmapCapture.wprp` 重抓。
- **`file_io_top_files` 返回空**——同上，`Capabilities.HasFileIo` 没开。默认 `CPU.light` profile 不带 FileIO。
- **首次 `load_trace` 卡住**——正在构建 `.etlx` 索引。看 stderr；100 MB 的 `.etl` 大约 30 秒，多 GB 要几分钟。同一个文件再加载就是即时的。

---

## 项目信息

**架构：** 整体布局见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)（英文）。要改 analyzer？先读 [`CONTRIBUTING.md`](CONTRIBUTING.md)（英文）——里面记录了几个非显然的不变量（`CpuAnalysis` 的 PerfView 一致性、内核 parser 挂接规则、file vs mmap 的 key 区分），重构时容易踩。

**License：** Apache-2.0（完整文本见 [`LICENSE`](LICENSE)）。贡献按 Apache 2.0 § 5 默认接受同样的 license。
