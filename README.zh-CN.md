<p align="center">
  <img src="https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

<p align="center">
  <a href="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/tooluse-labs/wpa-mcp/releases"><img alt="Release" src="https://img.shields.io/github/v/release/tooluse-labs/wpa-mcp"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue"></a>
</p>

# wpa-mcp

面向 MCP 客户端的本地化、证据驱动 Windows ETL 分析服务。

[English](README.md) | [最新版本](https://github.com/tooluse-labs/wpa-mcp/releases/latest) | [客户端兼容性](docs/CLIENT_COMPATIBILITY.zh-CN.md) | [参与开发](CONTRIBUTING.md)

wpa-mcp 让 AI 客户端分析 ETL trace，而不必把完整 trace 装入模型上下文。服务在本机打开 trace，显式应用进程、线程和时间范围，并以有界分页返回结构化证据。

## 能做什么

- 通过稳定的 trace reference 复用已打开的 ETL，避免每个问题都重新加载。
- 在 trace 包含所需事件时分析 sampled CPU、precise CPU 和调度活动。
- 按进程、PID、线程、TID、模块、堆栈和时间窗口缩小分析范围。
- 使用 `thread_compare_windows` 比较同一精确线程实例的命名快/慢区间，避免混入 PID/TID 复用实例。
- 按需解析符号，并明确报告缺失符号造成的归因限制。
- 返回 capability evidence、warning、局部失败和分页状态，不把缺失数据静默解释成零。
- 压缩高基数堆栈结果，并为批量 CPU 与线程窗口分析提供基于 snapshot 的分页。

## 快速开始

### 1. 一条命令安装并注册（推荐）

PowerShell：

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) }"
```

Windows Git Bash：

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash
```

安装器会把最新的完整 `wpa-mcp-win-x64.zip` 下载到 `%USERPROFILE%\.local`，验证可执行文件可用，并使用绝对路径自动注册所有检测到的 Codex、Claude Code 和 Claude Desktop。发布包是 self-contained，不需要另外安装 .NET runtime 或 SDK。

直接验证已安装的程序：

```powershell
& "$HOME\.local\bin\wpa-mcp.exe" --version
```

执行远程脚本前应先审阅其内容。需要固定版本或明确选择客户端时：

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag '<release-tag>' -Client codex"
```

### 2. 手工安装

Windows x64 用户也可以不执行远程脚本，直接安装完整 ZIP bundle，并始终保持 `bin` 与 `native` 目录在一起：

```powershell
$archive = Join-Path $env:TEMP 'wpa-mcp-win-x64.zip'
$install = Join-Path $HOME '.local\share\wpa-mcp'
$bin = Join-Path $install 'bin'

Invoke-WebRequest 'https://github.com/tooluse-labs/wpa-mcp/releases/latest/download/wpa-mcp-win-x64.zip' -OutFile $archive
Expand-Archive -LiteralPath $archive -DestinationPath $install -Force

# 让当前 PowerShell 会话可以直接运行 wpa-mcp。
$env:Path = "$bin;$env:Path"

# 为新进程和新终端持久保存 bin 目录。
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$userEntries = @($userPath -split ';' | Where-Object { $_ })
if ($userEntries -notcontains $bin) {
    [Environment]::SetEnvironmentVariable(
        'Path', (($userEntries + $bin) -join ';'), 'User')
}

wpa-mcp.exe --version
```

如果系统策略禁止修改用户级环境变量，可省略持久化 PATH 的代码，并使用绝对命令 `& "$install\bin\wpa-mcp.exe"`。修改持久 PATH 后，需要重启已有终端和 MCP 客户端。

独立的 `wpa-mcp-win-x64.exe` 资产适合便携式冒烟检查。常规安装应使用 ZIP 完整包，因为原生依赖和原地升级都要求完整的 `bin` 加 `native` 目录结构。

### 3. 连接 MCP 客户端

把客户端配置为通过 stdio 启动 `bin\wpa-mcp.exe`，并使用绝对路径。采用 JSON 配置的客户端通常使用以下结构：

```json
{
  "mcpServers": {
    "wpa": {
      "command": "C:\\Users\\you\\.local\\share\\wpa-mcp\\bin\\wpa-mcp.exe"
    }
  }
}
```

Codex、Claude Code 和 Claude Desktop 的配置位置不同，请使用[客户端兼容性](docs/CLIENT_COMPATIBILITY.zh-CN.md)中的对应方法。

### 3. 提出第一个问题

```text
打开 C:\traces\startup.etl。先汇总 trace 时长和可用 capability，
然后列出 CPU 占用最高的进程，暂时不要解析符号。
```

先从全局概览开始，选定相关 PID 或 TID 后再请求堆栈和符号。相比一次请求所有堆栈，这种方式证据更清晰、响应也更小。

## Trace 访问

`load_trace` 只能打开位于已配置 trace 根目录内的文件（默认为"文档"文件夹）。需要更多目录时请重复使用 `--trace-root`——每个该选项只接受一个值：

```json
"args": ["--trace-root", "C:\\Traces", "--trace-root", "D:\\Captures"]
```

也可以设置 `WPAMCP_TRACE_ROOTS`（Windows 下用 `;` 分隔）。只要在命令行提供了 `--trace-root`，就会整体替换默认根目录。服务器无法识别的参数会在启动时直接报错，而不再被静默忽略。

`trace_access_denied` 错误会指明拒绝该路径的具体规则并列出已配置的根目录。在完全可信的单人本机环境中，可以用 `--allow-any-trace-path`（或 `WPAMCP_ALLOW_ANY_TRACE_PATH=true`）关闭根目录限制。**仅限本机可信环境使用**：开启后任何工具调用都能读取该账户可访问的所有 `.etl`/`.etlx` 文件。

## 更新

完整 bundle 安装可以原地更新到最新稳定 GitHub Release：

```powershell
wpa-mcp.exe update
```

如果可执行文件不在 `PATH` 中，请使用绝对路径。更新程序只接受已发布且非草稿、非预发布的版本，并在替换安装目录前校验 GitHub asset digest、不可变 release evidence、ZIP SHA-256 和 staging 可执行文件版本。

更新不会改变 MCP 客户端注册。默认情况下，如果其他进程正在运行同一个已安装可执行文件，命令会在下载或修改文件前退出，并列出阻塞 PID；关闭对应 MCP 客户端后即可重试。如需在新 bundle 完成校验后明确终止这些精确路径的服务实例，请使用：

```powershell
wpa-mcp.exe update --stop-running
```

该选项不会终止 MCP 客户端进程，也不会终止其他路径下的同名程序，并且绝不会绕过发布校验。被终止的服务会丢失内存中的 trace 和符号状态。应用阶段由已验证的暂存版 `wpa-mcp.exe` 自身执行，不依赖 PowerShell、ExecutionPolicy 或 shell language mode。异步替换结果会记录在安装根目录的 `.wpa-mcp-update.log`。早于可执行 apply helper 的安装需要先手工安装一次最新 ZIP bundle。

## 分析流程

1. 打开 ETL，检查持续时间、进程和 capability evidence。
2. 选择与问题对应的单个进程、线程或时间区间。
3. 先比较区间，再请求大规模堆栈展开。
4. 只为已选范围解析符号。
5. 根据 `hasMore` 和 continuation metadata 获取后续页面，直到证据完整。

可直接使用的问题：

- `比较 TID 4120 在 3-8 秒和 8-13 秒的表现，报告 sampled CPU、wait duration、热点堆栈，以及 trace 无法提供的证据。`
- `分析 PID 9000 的 CPU 热点，排除 ETW self-overhead，只为最热模块解析符号。`
- `解释该线程为什么处于 runnable 但没有运行的状态，分别报告 CPU execution、ready time 和 blocked time。`
- `按有界页面分析这些 PID，使用返回的 snapshot 继续，不要重新启动整个 batch。`

只有 wait duration 不能确定具体阻塞方法。可靠归因还依赖调度事件、堆栈采集、符号和足够精确的时间范围。
因此 `thread_compare_windows` 会分别报告 sampled count、scheduler running time、ready latency 和 blocked duration；ready latency 与 blocked duration 不可相加。

## 采集有效的 trace

wpa-mcp 只能分析已经记录的事件。Sampled CPU 需要 profile 事件和堆栈；调度延迟分析需要 context-switch 与 ready-thread 事件；方法归因通常需要可解析的符号。

Provider 选择和采集权衡见 [WPR profile 指南](docs/WPR_PROFILE.md)。仓库在 `tests\WpaMcp.Tests\fixtures` 中提供了聚焦 JIT 的 `JitOnlyCapture.wprp` profile 和 `Capture-JitOnly.ps1` 辅助脚本。

不要默认启用所有 provider。应采集能够回答性能问题的最小事件集合，并尽可能在场景前后记录 marker。

## 理解结果

- `traceRef` 标识后续调用复用的已打开 trace。
- `scope` 记录结果实际应用的进程、线程和时间边界。
- `capabilityEvidence` 区分可用、缺失和未测量的 trace 数据。
- `warnings` 与 `failedSections` 显示局部分析失败，而不是隐藏问题。
- `hasMore` 与 continuation metadata 表示仍有后续有界页面。

Capability 不可用应解释为未知，而不是测量值为零。比较结果时应保留 trace reference、scope 和 symbol context。

## 故障排查

| 现象 | 处理方式 |
| --- | --- |
| `response_too_large` | 减少 PID 数量、`top`、堆栈深度或时间范围，并消费 continuation 页面，不要一次请求所有高基数堆栈。单个不可拆分项过大时仍可能超过硬帧限制。 |
| 函数名仍未解析 | 配置符号路径，并只重试已选进程或模块。参见[符号配置方法](docs/SYMBOL_RECIPES.zh-CN.md)。 |
| 缓慢线程的 CPU 很低 | 分别检查 ready time 和 blocked time；仅靠 CPU sample 无法解释调度延迟。 |
| 工具报告数据不可用 | 阅读 `capabilityEvidence`，使用所需 provider 重新采集，不要把缺失解释成零。 |
| `trace_access_denied` | trace 不在已配置的根目录内，错误信息会列出这些根目录。重复使用 `--trace-root` 添加根目录，或在可信本机环境使用 `--allow-any-trace-path`。 |
| 更新无法替换可执行文件 | 关闭 MCP 客户端及正在运行 wpa-mcp 的终端，然后重新执行更新。 |
| 结果噪声过多 | 在解析符号或展开堆栈前缩小进程、线程和时间区间。 |

ETL 文件保留在运行 MCP server 的机器上，但工具结果会返回给已连接客户端。符号解析可能访问该机器配置的 symbol server。

## 文档

- [架构](docs/ARCHITECTURE.md)
- [客户端兼容性](docs/CLIENT_COMPATIBILITY.zh-CN.md)
- [能力缺口](docs/CAPABILITY_GAPS.zh-CN.md)
- [符号配置方法](docs/SYMBOL_RECIPES.zh-CN.md)
- [WPR profile 指南](docs/WPR_PROFILE.md)
- [案例研究](docs/CASE_STUDIES.md)
- [Contract 迁移](docs/CONTRACT_MIGRATION.zh-CN.md)

README 只描述稳定的用户路径。协议设计、rollout 历史、测量基线和实现任务应维护在 `docs/` 中。

## 从源码构建

使用 `global.json` 选择的 SDK：

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp.git
cd wpa-mcp
dotnet restore --locked-mode
dotnet build WpaMcp.sln -c Release --no-restore
dotnet test WpaMcp.sln -c Release --no-build
```

源码构建需要配置的 .NET SDK，正式发布包仍是 self-contained。修改 contract 或 reviewed baseline 前请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。
