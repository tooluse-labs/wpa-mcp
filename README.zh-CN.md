<p align="center">
  <img src="https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

<p align="center">
  <a href="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/tooluse-labs/wpa-mcp/releases"><img alt="Release" src="https://img.shields.io/github/v/release/tooluse-labs/wpa-mcp"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue"></a>
</p>

<p align="center">
  <a href="README.md">English</a> | <strong>简体中文</strong> | <a href="CHANGELOG.md">更新日志</a>
</p>

---

一个 C# 实现的 MCP server，把 Windows ETW（`.etl`）trace 分析能力——CPU、scheduler wait、image load、文件 / 磁盘 / mmap / 网络 I/O、注册表、内存资源、CLR runtime 事件——开放给任意 MCP-兼容客户端（Claude Code、Claude Desktop、Codex、Cursor）。设计上**不绑定特定领域**：任何 Windows trace 都能用，常见用途是排查应用启动慢、进程创建慢、AV / EDR 拖慢系统、磁盘瓶颈回归等。

> **状态——PoC。** MCP 工具面已覆盖多个 ETW 分析域。仅限 Windows（TraceEvent 内核 parser 不可移植）。Apache-2.0。

> **看一个真实案例：** [一次完整排查](docs/CASE_STUDIES.md)——进程创建慢到基线的 50 倍，追溯到多套 EDR 在 `PsSetCreateProcessNotifyRoutineEx` 上串行回调。同一份 trace 被两个 LLM agent 独立复现得到同样结论。

---

## 快速上手

<!-- 动态演示。把录好的 GIF 放在 assets/quickstart-demo.gif 这里就会渲染出来。
     录制食谱见 assets/quickstart-demo-recording.md（英文）。 -->
<p align="center">
  <img src="assets/quickstart-demo.gif" alt="wpa-mcp 快速上手演示——加载 trace、找出慢进程、钻进进程创建 burst" width="800">
</p>

装好之后（[一行命令在下面](#安装)），用自然语言问 agent，它会挑对应的工具：

```
> 加载这个 trace：C:\path\to\trace.etl
（load_trace——首次 30 秒~3 分钟构建 .etlx 索引，后续复用缓存。
 返回 trace 元信息和 materialization 后实际观测到的事件类型 map。）

> 看一下这份 trace 能回答什么问题。
（inspect_trace——已观测 capability、每事件域栈覆盖率、PDB identity / readiness、
 quality warnings 和适用 next-tool 提示）

> 诊断 PID <X> 在 <t0> 到 <t1> 的 high wait。
（diagnose_high_wait——同一时间窗的一次调用，返回 candidates、evidence、
 not-concluded reasons、executed-call provenance、next tools）

> 父 PID <X> 下，每个子进程的 kernel-side gap 是多少？
（process_create_timing——一个调用给出该父进程所有子进程的内核窗口分布）

> 钻进 evidence 里的某个 top wait frame：谁调用了它？
（wait_caller_callee——focus frame 的 caller / callee 邻居）
```

同样的 `summary → stacks → caller/callee` 模式适用于 CPU（`cpu_top_functions` → `cpu_caller_callee`）、文件 / 磁盘 / mmap I/O、image load、CLR allocation / exception / contention、网络、注册表这些有 stack view 的域。生命周期和资源类（内存资源快照、thread lifetime、process creation 等）不是栈结构，在下面的工具表里有单独行。

完整端到端走查（症状 → 工具链 → 证据 → 结论或假设 → 改进建议）见 [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md)（英文）。

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

两条路径做的事一样：从 GitHub Releases 下载最新 self-contained `wpa-mcp-win-x64.exe` 到 `%USERPROFILE%\.local\bin\wpa-mcp.exe`，然后直接注册到每个检测到的 MCP 客户端（Claude Code / Codex / Claude Desktop）。不需要本机预装 .NET runtime 或 SDK。

通过一行命令转发额外参数：

```powershell
# PowerShell——指定 tag、限定客户端、自定义 symbol path
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag v0.2.16 -Client claude-desktop -SymbolPath 'SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols'"
```

```bash
# Bash——`bash -s --` 后面的 flag 会传给 install.ps1
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash -s -- -Tag v0.2.16
```

### 卸载（一行命令，对称）

同样支持远程一行调用；反向修改之前注册的客户端配置。不动下载缓存。

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.ps1) }"
```

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
```

会从所有检测到的 MCP 客户端中移除 `wpa-mcp` 条目，并删除 `%USERPROFILE%\.local\bin\wpa-mcp.exe`。符号缓存保留（要清理就删 `%LocalAppData%\WpaMcp\Symbols\`）。

### 系统要求

- Windows 10 / 11（TraceEvent 内核 API 仅 Windows）
- 一行安装路径不需要 .NET runtime；release 已包含 self-contained Windows executable。
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
# DLL 位置: src\WpaMcp\bin\Release\net10.0\WpaMcp.dll
```

冒烟测试：

```powershell
dotnet src\WpaMcp\bin\Release\net10.0\WpaMcp.dll --version    # 输出 "WpaMcp 0.2.16"
dotnet test                                                   # 跑 xUnit 套件（需要 fixture，见 CONTRIBUTING.md）
```

然后注册到你的 MCP 客户端。DLL 路径**必须用绝对路径**。

**Claude Code**——按项目（`<project>/.mcp.json`）或全局（`~/.claude.json`）：

```json
{
  "mcpServers": {
    "wpa-mcp": {
      "command": "dotnet",
      "args": ["C:/Users/me/Dev/wpa-mcp/src/WpaMcp/bin/Release/net10.0/WpaMcp.dll"],
      "env": {
        "_NT_SYMBOL_PATH": "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
        "WPAMCP_CACHE_SIZE": "2"
      }
    }
  }
}
```

或者用 CLI helper：

```powershell
claude mcp add wpa-mcp --scope user -- dotnet C:/Users/me/Dev/wpa-mcp/src/WpaMcp/bin/Release/net10.0/WpaMcp.dll
```

（环境变量加 `-e _NT_SYMBOL_PATH=...`。）

**Claude Desktop**——`%APPDATA%\Claude\claude_desktop_config.json`，结构和上面一样。

**Codex / Cursor / 其它 MCP-兼容客户端**——server 走 stdio MCP；任何接受 `command + args` 配置的客户端都行。用上面那段 JSON。

**验证**——重启客户端后，工具会以 `mcp__wpa-mcp__load_trace` 这种命名出现。第一次对一个新 `.etl` 调 `load_trace` 会花 30 秒~3 分钟构建 `.etlx` 索引（写到 stderr）。

</details>

---

## 工具

MCP 工具面覆盖多个 ETW 分析域，底层基于 PerfView 同款的 `Microsoft.Diagnostics.Tracing.TraceEvent` 库。共用 parser 不等于每个视图天然与 PerfView 等价；各分析器会返回实例范围、实际观测到的能力、覆盖率和空结果状态，供调用方判断证据边界。

### wpa-mcp 相对 PerfView 加了什么

* **Agent 驱动而不是 UI 驱动**：PerfView 是 Windows GUI 一路点过去；wpa-mcp 是 stdio MCP server，自然语言对话即可。同样的数据，省去界面操作，方便编进 CI / 回归脚本。
* **复合工具**：`diagnose_high_wait`、`diagnose_slow_startup`、`process_create_timing`、`image_load_top_gaps` 把 PerfView 多步操作打包成一次调用。
* **Capabilities-aware**：`load_trace` 报告 ETLX materialization 后实际观测到的事件类型；单个响应进一步区分 `scope_not_found`、`event_class_not_observed`、`no_events_in_scope` 和 `stacks_unavailable`。没有观测到事件并不能反向证明某个 capture keyword 一定没开。
* **per-trace symbol 推荐**：`load_trace` 扫描 trace 里出现的模块、推荐应加哪些 symbol server。在 PerfView 里这要靠用户自己摸索。

### 设计理念

wpa-mcp 的目标是：**不误导模型，也不限制模型继续推理**。

* **Orientation 工具**（`load_trace`、`inspect_trace`）提前暴露 capability、已启用信号列表、quality gap、推荐诊断路径、symbol 健康度，让模型从真实信号选下一步，而不是从空结果反推。
* **Diagnostic composite**（`diagnose_high_wait`、`diagnose_slow_startup`）压缩调用路径但保留证据链——通过 `Evidence`、`NotConcluded`、`ExecutedToolCalls`、`NextTools` 字段输出，故意不返回综合出的 "root cause" 字段。
* **Per-domain 行 / 栈工具**贴近 PerfView 形态。进程级工具暴露所选 `(Pid, ProcessStartUs)` 生命周期，或明确标注按 PID 聚合；栈工具报告目标事件域自己的覆盖率，不拿其他事件的栈来推断当前工具可用。

### 使用方式

**永远先调 `load_trace`**：它打开 `.etl`、构建（或复用）`.etlx` 索引，并返回 `Capabilities` map，表示 materialized TraceLog 中实际观测到的受支持事件类型。这些字段是已解析事件的证据，不是原始 capture keyword 配置的证明。Map 覆盖：

* **CPU 采样和调度** —— `HasCpuSamples`、`HasCSwitch`、`HasReadyThread`、`HasStackWalks`
* **文件 / 磁盘 / mmap I/O 和 loader** —— `HasFileIo`、`HasDiskIo`、`HasHardFaults`、`HasImageLoad`
* **内存** —— `HasVirtualAlloc`、`HasNtHeap`、`HasMemoryProcessInfo`、`HasHandleEvents`、`HasPoolEvents`
* **网络** —— `HasNetIo`、`HasNetConnections`
* **内核基础设施** —— `HasRegistry`、`HasInterrupt`、`HasAlpc`、`HasThreadEvents`
* **CLR runtime** —— `HasClrGc`、`HasClrJit`、`HasClrAlloc`、`HasClrException`、`HasClrContention`

`HasStackWalks` 仅是兼容用的全局并集。解释某个栈工具前，应查看该事件域的 `StackCoverage`：`TotalEventCount`、`StackedEventCount`、`StackCoveragePct` 和 `CoverageState`（`no_events`、`no_stacks`、`partial` 或 `full`）。`StackSemantics` 标识实际统计的栈来源；尤其 `cswitch` 域使用 switch-out `BlockingStack`，而调试 probe 的普通 CSwitch `CallStackIndex` 是另一类栈，两者覆盖率可能不同。合成的 `?!?` 行只是给无栈事件记账，不是真实调用链。

完整调用流程：

```
.etl trace
    │
    ▼
load_trace  ──►  返回 Capabilities map
    │
    │  （可选：capture profile 或调查路径不清楚时调 inspect_trace）
    ▼

  Composite  （推荐用于常见 workflow）
  ──────────────────────────────────
  diagnose_slow_startup、diagnose_high_wait
  返回 Evidence + NotConcluded + ExecutedToolCalls + NextTools
                                                          │
                                                          │  通过 NextTools
                                                          ▼

  Domain drill  （自定义调查，或 composite 之后的钻取）
  ──────────────────────────────────────────────────
  summary  ──►  stacks  ──►  caller_callee
  top-N         top-N         focus-frame
  行            调用栈        钻取

  示例：cpu_top_functions  ──►  cpu_top_stacks  ──►  cpu_caller_callee
```

如果不确定 capture profile 覆盖了什么，或者下一步调查路径不清楚，接着调 `inspect_trace`。常见 workflow 优先用 `diagnose_high_wait`、`diagnose_slow_startup` 这类 composite，不要一开始就手工拼单个调用——它们的 `Evidence`、`NotConcluded`、`ExecutedToolCalls`、`NextTools` 会说明跑了什么、哪些结论不能下、下一步该往哪里钻。

多数 stack-oriented 工具组遵循同样的三件套结构：**summary**（top-N 平铺行）、**stacks**（top-N 调用栈，按 metric 加权）、**caller-callee 钻取**（给一个 focus frame，返回其 caller / callee 邻居，metric 加权）——形式与 PerfView 的 "Callers" / "Callees" tab 一致。

下面的表格里 "PerfView 对应" 列指 PerfView GUI 中的对应视图。标 **[复合]** 的把多个 PerfView 视图打包成一次调用，标 **[手动过滤]** 的暴露 PerfView Events 视图能看到但没预聚合的原始事件，标 **[程序化]** 的用结构化 JSON 替代 GUI 对话框。其余多数工具是 PerfView 视图的 1:1 映射。

### 时间窗口语义

接受 `startUs` 和 `endUs` 的工具使用半开区间：事件包含的条件是 `startUs <= timestamp < endUs`。边界为 null 分别表示 trace 开始和 trace 结束。

PID 被复用时，进程级工具应同时传入 `list_processes` 返回的 `processStartUs`。只传 PID 的调用要么返回 `ScopeMode=pid_aggregate` 并保持各生命周期分离，要么在必须唯一实例的工具中拒绝歧义选择。解释空 `Rows` 前先检查 `ScopeStatus`、`CapabilityStatus`、`MatchedEventCount`、`NoDataReason`、`PidReuseObserved` 和 `IncludedProcesses`。`CapabilityStatus=observed` 只表示解析成功的目标范围匹配到源事件；`not_observed` 仅用于已确认的全局/未过滤缺失，其他情况为 `unknown`。

对同时接受 `tid` 的 CPU/Wait 工具，应使用 `threadStartUs` 区分线程复用。缺失或歧义线程会返回结构化 `scope_not_found` / `ambiguous_thread_instance`，不会退化成 PID-only 数据；`IncludedThreads` 带 `ThreadStartUs` / `ThreadEndUs`，可直接用 `pid + processStartUs + tid + threadStartUs` 重放候选。

不接受 `startUs` / `endUs` 的工具有意采用不同的作用域；每个工具的 MCP description 会说明是哪一种：

* **全 trace orientation / 配置** —— `load_trace`、`inspect_trace`、`list_processes`、`find_marker`、`diagnose_symbols`、`set_symbol_path`、`add_symbol_server`。
* **生命周期视图** —— `process_create_timing`、`thread_lifetime`、`image_load_timing`、`image_load_top_gaps`、`diagnose_slow_startup` 用进程启动相对或生命周期相对的窗口，而不是任意 trace 窗口。
* **全 trace 或窗口化 by-file 汇总** —— `file_io_top_files` 和 `hard_fault_by_file` 按文件名汇总，并支持显式 `startUs` / `endUs` 窗口。需要事件关联调用链证据时用对应的 stack 工具。

### Meta（元信息）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| **`load_trace`** | 加载 / 缓存 `.etl`，返回 trace 元信息、已观测事件能力和 per-trace symbol-server 推荐。`EventCount` 是 ETLX materialized logical-event count；原始 ETW record count 与 parser coverage ratio 未实测时明确返回 not measured，不做反推。 | 打开 trace 文件（无 `Capabilities` 等价物） |
| **`inspect_trace`** | 一次性 orientation：已观测能力、system metadata、provider counts、每事件域栈覆盖率、PDB identity / local readiness、quality warnings 和 next-tool hints。仅有 PDB identity 不会被标成 frame 已解析；实际解析率只在栈 lookup 真正执行后统计。 | **[程序化]**——替代手工跨 Events、Modules、capture metadata 做 trace 质量检查 |
| `list_processes` | 列出进程生命周期（可按 `cpu` / `wall` / `wait_ratio` 排序）。`WaitRatio = WallUs / CpuUs` 只用于排序"高 wall、低 CPU"候选，不能识别具体等待对象。默认隐藏 PID 0（Idle）和 PID 4（System）。 | Processes 视图 |
| `process_create_timing` | 按父进程生命周期列出子进程创建时序。`FirstImageLoadOffsetUs` 是 `ProcessStart` 到首个 DLL 加载之间的观测区间；其中可能包含回调、扫描、挂起、调度等工作，单凭该区间不能确定机制或根因。 | **[复合]**——Processes + Events + Excel；见 [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md)（英文） |
| `thread_lifetime` | 给定 PID 的线程生命周期时序——每次 `ThreadStart` / `ThreadStop`，附 `StartTimeUs` / `EndTimeUs` / `LifetimeUs`，加 `PeakConcurrentThreads`。捕捉线程池抖动 / fork bomb 模式。`TraceResidentStart/End` 标识由 trace capture 边界限定（而非真正 spawn / 退出）的线程。 | **[手动过滤]**——Events 视图，过滤 `Thread/Start` + `Thread/Stop` 后手动配对 |

### CPU 栈

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `cpu_top_functions` | 给定窗口 / PID，按 exclusive CPU 采样数返回 top-N 热点函数。可选 `excludeEtwSelfOverhead` 把 `EtwpLogKernelEvent` 等折成单个 `[ETW Overhead]` 桶。过滤调用默认省略 `*PctOfTrace`，避免大 ETL 上额外全 trace CPU 采样计数；确实需要整条 trace 百分比时传 `includeTracePct=true`。 | CPU Stacks → ByName |
| `cpu_precise_analysis` | CSwitch + ReadyThread scheduler summary：按线程输出精确 on-CPU 微秒数、ready-to-run latency、per-core runtime attribution，以及 quantum/preemption 计数。用于 sampled CPU 回答不了的"实际跑了多久？"和"ready 后等了多久才被调度？"问题。 | CPU Usage (Precise) |
| `cpu_top_functions_batch` | 同上但一次调用覆盖多个 PID。每个 PID 独立 CallTree（inclusive% 按该 PID 的采样数归一化）。 | **[复合]**——批量变体，省去 N 次 CPU Stacks → ByName 往返 |
| `cpu_caller_callee` | 给定 focus frame，返回其 caller（调进 focus）和 callee（focus 调出去），按 inclusive 采样数排序。Recursion-safe。 | CPU Stacks → Callers / Callees tab |

### Wait / 阻塞时间（CSwitch 衍生）

需要 `CSwitch` 内核 keyword（默认 WPR `CPU` profile 已包含）。

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `wait_analysis` | 每线程阻塞时间 + 观测到的 wait reason。`WrFilterContext` 这类 reason 表示 scheduler wait state，不能单独确定责任组件或根因。响应会区分全 trace 与目标范围的 CSwitch 计数，并报告目标范围栈覆盖率。 | Thread Time → 每线程阻塞时间 |
| `wait_top_stacks` | 按阻塞 μs 加权的 top-N 调用栈，来自目标 switch-out `ThreadCSwitch` 区间附带的 blocking stack。这是与阻塞时间关联的代码路径证据，不能确定外部责任组件或根因。 | Thread Time / Wait Time → BlockedTime metric（`ThreadTimeStackComputer`） |
| `wait_caller_callee` | 给定 focus frame 的 caller-callee 钻取；metric 是阻塞 μs。 | Thread Time → Callers / Callees tab |

### Image / DLL 加载

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `image_load_timing` | 单个进程生命周期的 DLL 加载时序，每行带相对 `ProcessStart` 的偏移。它能发现延迟加载与长区间，但不能仅凭区间把延迟归因给 minifilter、签名扫描或其他机制。 | **[手动过滤]**——Events 视图，过滤 `ImageLoad` 后手动算偏移 |
| `image_load_top_gaps` | 相邻 DLL 加载之间 gap 最大的 top-N 行。和 `image_load_timing` 同源数据，按 gap 排序。响应里也带 `FirstLoadOffsetUs`（首个 DLL 之前的内核 fork 税）。 | **[手动过滤]**——同上的 `ImageLoad` 过滤，按相邻事件间隔排序 |
| `image_load_top_stacks` | 按 `ImageLoad` 事件计数加权的 top-N 调用栈。区分 eager 加载（main 初始化里的 `LoadLibraryEx`）和 lazy / 级联加载（`CoCreateInstance`、`AmsiOpenSession`、EDR 注入的 provider）。 | Image Load Stacks |
| `image_load_caller_callee` | 给定 focus frame 的 caller-callee 钻取；metric 是 image-load 计数。 | Image Load Stacks → Callers / Callees tab |

### 文件 / 磁盘 / mmap I/O

三个层次覆盖 I/O 栈不同位置——做差能定位时间到底花在哪一层。

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `file_io_top_files` | 按总 `read + write` 字节排序的 top-N 文件。 | File I/O 视图 → ByFile |
| `file_io_top_stacks` | 按文件 IO 字节加权的 top-N 调用栈。捕获**所有**系统调用包括缓存命中——和 `disk_io_top_stacks` 做差找出缓存读。需要 `FileIO` keyword（默认 `CPU.light` profile 不带）。 | File I/O Stacks |
| `file_io_caller_callee` | 给定 focus frame 的钻取；metric 是文件 IO 字节。 | File I/O Stacks → Callers / Callees tab |
| `disk_io_top_stacks` | 按**物理**磁盘 IO 字节加权的 top-N 栈——只统计真正打到物理介质的事件（无缓存）。需要 `DiskIO` keyword。 | Disk I/O Stacks |
| `disk_io_caller_callee` | 给定 focus frame 的钻取；metric 是物理磁盘字节。 | Disk I/O Stacks → Callers / Callees tab |
| `hard_fault_by_file` | 按**硬页错误（hard page-in）字节**排序相关 backing file mapping。多数硬页错误来自首次访问的 mmap'd 文件（DLL、数据文件、网络共享内容），少数来自被换出的 heap/stack 和 page file。该视图识别与 page-in 关联的映射，不证明上层原因。需要 `HardFaults` keyword（**默认 WPR profile 不带**——见 [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)（英文））。 | Memory Hard Fault → ByFile |
| `hard_fault_top_stacks` | 按硬页错误页入字节加权事件附带栈。它可支持 eager/lazy 访问或并发扫描等假设，但不能单独证明上层原因。 | Memory Hard Fault Stacks |
| `hard_fault_caller_callee` | 给定 focus frame 的钻取；metric 是 page-in 字节。 | Memory Hard Fault Stacks → Callers / Callees tab |

### 虚拟内存

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `virtual_alloc_top_stacks` | 按观测到的 `VirtualMemAlloc` + `VirtualMemFree` 操作字节加权。响应分别报告 allocated / freed 字节与次数、总操作流量和观测净操作字节；它不是 live virtual size、commit、retention 或 leak 统计。需要 `VirtualAlloc` 内核 keyword（**默认 WPR `CPU` profile 不带**）。 | VirtualAlloc Stacks |
| `virtual_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是虚拟内存字节。 | VirtualAlloc Stacks → Callers / Callees tab |
| `heap_alloc_top_stacks` | 按 **NT 堆**分配字节加权的 top-N 栈。这是 allocation flow，不是 retained-memory 或 leak 证明：Free 事件不携带 size，无法计入。响应拆出 `AllocBytes` / `ReallocBytes`；需要 **per-process** 启用 `Heap` provider。 | HeapAllocStacks |
| `heap_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是 NT 堆字节。 | HeapAllocStacks → Callers / Callees tab |

### 网络 I/O

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `net_top_stacks` | 按网络字节加权的 top-N 栈——TCP + UDP、IPv4 + IPv6 send/recv 合并。响应里拆出 `TcpBytes` / `UdpBytes`。配合 `wait_analysis` 排查"高 wall、低 CPU"且阻塞在网络往返的场景。`Connect` / `Accept` / `Disconnect` 这类无字节 metric 的事件不计入——用 `find_marker`。需要 `NetworkTrace` keyword（**默认 `CPU` profile 不带**）。 | TCP/IP Stacks + UDP/IP Stacks（合并） |
| `net_caller_callee` | 给定 focus frame 的钻取；metric 是网络字节。 | TCP/IP Stacks → Callers / Callees tab |
| `net_connections` | 按 `connid` 配对 Connect/Accept 与 Disconnect/Reconnect，给出每条 TCP 连接"在 T1 打开、T2 关闭，持续 T2−T1"，用于寻找观测生命周期异常长的连接。该持续时间不是连接建立延迟、请求/响应延迟或 RTT，不能单独把 RPC 变慢归因于连接建立。IPv4 + IPv6 合并，带 `IsIPv6` 标志；trace 结束时仍开启的连接 `TraceResidentEnd=true`。 | **[手动过滤]**——Events 视图，按 `connid` 手动配对 `TcpIp/Connect` 与 `TcpIp/Disconnect` |

### 注册表

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `registry_top_stacks` | 按注册表操作计数加权的 top-N 栈（Query / Open / Create / SetValue / EnumerateKey 等）。回答"谁在每条热路径上敲注册表"。Metric 是操作数（注册表没有自然的字节度量）。需要 `Registry` keyword（**默认 `CPU` profile 不带**）。 | Registry Stacks |
| `registry_caller_callee` | 给定 focus frame 的钻取；metric 是注册表操作数。 | Registry Stacks → Callers / Callees tab |

### ReadyThread（关联证据）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `ready_thread_top_stacks` | top-N **readier/wakeup 关联栈证据**，按可选 `awakenedPid` 和请求时间窗聚合。栈属于 readier，而不是被唤醒线程；事件未与某个具体 wait interval 或后续 CSwitch 一一配对，不能单独证明根因。应与 `wait_analysis` 配合作为辅助证据。 | ReadyThread Stacks |
| `ready_thread_caller_callee` | 围绕 focus frame 钻取同一类 readier/wakeup 关联证据；metric 是 ready 事件计数，并具有相同的非因果限制。 | ReadyThread Stacks → Callers / Callees tab |

### 中断（DPC / ISR）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `interrupt_top_stacks` | 按观测到的内核中断时间（DPC + ISR 微秒数）加权的 top-N 栈，响应拆出 `DpcUs` / `IsrUs`。应与可比工作负载和硬件基线比较；这里没有通用的“健康阈值”，热例程本身也不能证明驱动故障。需要 `Interrupt` + `DPC` keyword（默认 `CPU` profile 全带）。 | DPC/ISR Stacks |
| `interrupt_caller_callee` | 给定 focus frame 的钻取；metric 是中断 μs。 | DPC/ISR Stacks → Callers / Callees tab |

### ALPC（跨进程 IPC）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `alpc_top_stacks` | 按 ALPC 消息计数（Send + Receive）加权的 top-N 栈。ALPC 是 RPC、COM、AppContainer broker、lsass、SCM 等组件使用的 Windows 内核 IPC 原语。该工具显示与消息活动关联的调用链；消息计数本身不测量一次 round-trip，也不能解释延迟。需要 `ALPC` keyword（**默认 `CPU` profile 不带**）。 | ALPC Stacks |
| `alpc_caller_callee` | 给定 focus frame 的钻取；metric 是 ALPC 消息计数。 | ALPC Stacks → Callers / Callees tab |

### CLR（.NET runtime）

需要 `Microsoft-Windows-DotNETRuntime` ETW provider（WPR `.wprp` 文件需要显式 `<EventCollectorId>`）。
如果只看 JIT，运行 `tests/WpaMcp.Tests/fixtures/Capture-JitOnly.ps1`，或直接使用 `JitOnlyCapture.wprp!ClrJitOnly`；它只开启 `clr_jit_analysis` 需要的 CLR JIT + Loader bits，不开启 GC / allocation / exception / contention runtime keywords。

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `clr_gc_analysis` | 列出每次 GC，附 wall 时长**以及** stop-the-world 暂停时长。`GCStart`→`GCStop` 是 wall 区间；`GCSuspendEEStart`→`GCRestartEEStop` 是真正的 mutator 暂停（对 background / concurrent GC 关键——其 wall 远大于真实 pause）。每行带 `Generation` / `Reason` / `PauseUs`，并报 `TotalGcCount` / `Gen0Count` / `Gen1Count` / `Gen2Count` / `TotalPauseUs`。 | GCStats |
| `clr_jit_analysis` | 按 JIT 编译耗时加权的 top-N 方法。按 `(PID, MethodID)` 匹配 `MethodJittingStarted`→`MethodLoadVerbose`。R2R / NGen / 预编译方法不发 `JittingStarted`，因此该工具看不到它们——这是"trace 里 JIT 成本"的正确语义。 | JIT Stats |
| `clr_alloc_top_stacks` | 按托管堆分配字节加权的 top-N 栈，由 `GCAllocationTick` 事件驱动（CLR 每分配约 100 KB 触发一次，按 `(堆、代、类型)` 分桶——是采样而非全量，低开销，CLR ≥ 4.0 默认即开）。响应包含 `TopTypes`（按字节排序的 top 类型名）。"谁在请求热路径上分配大量 string"的标准工具。需要 `GC` keyword。 | GC Heap Alloc Stacks |
| `clr_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是分配字节。 | GC Heap Alloc Stacks → Callers / Callees tab |
| `clr_exception_top_stacks` | 按 .NET 异常抛出计数加权的 top-N 栈（`ExceptionStart` 事件）。适合"这条代码路径每秒抛 1000 个异常吗"/"哪里在 retry 循环里吞 `FormatException`"。响应包含 `TopTypes`（top 异常类型名）。需要 `Exception` keyword。 | Exceptions Stacks |
| `clr_exception_caller_callee` | 给定 focus frame 的钻取；metric 是异常计数。 | Exceptions Stacks → Callers / Callees tab |
| `clr_contention_top_stacks` | 按托管 monitor 阻塞 μs 加权的 top-N 栈——即 `lock` / `Monitor.Enter` 的等待。按 `ThreadID` 匹配 `ContentionStart`→`ContentionStop`。只统计 `ContentionFlags.Managed`（同 provider 的 native 锁竞争被排除）。托管代码的锁热点标准工具。需要 `Contention` keyword。 | Monitor Contention Stacks |
| `clr_contention_caller_callee` | 给定 focus frame 的钻取；metric 是阻塞 μs。 | Monitor Contention Stacks → Callers / Callees tab |
| `clr_gc_heap_stats` | 托管堆快照时序，包含各代 heap 大小、pinned-object 与 GC-handle 计数。用于识别趋势；持续上升本身并不能证明 leak 或给出对象 retention path。配合 `clr_gc_analysis` 使用。 | GCStats per-GC snapshot 表 |
| `clr_finalizer_analysis` | top-N 观测到被 finalize 的类型 + finalizer 线程执行批次。`GCFinalizeObject` 按 `TypeName` 聚合，`GCFinalizersStart`→`GCFinalizersStop` 配对；批次时长不自动等于应用暂停。它可辅助判断 finalizer 工作是否与 GC 延迟重叠，但不能单独归因慢 GC，也不能定位分配点。 | **[复合]**——把 GCStats 字段 + Events 视图过滤合并到一次调用 |

### Marker / 通用 ETW 事件

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `find_marker` | 搜索所有 materialized ETW 事件中名字 / task 包含给定 substring 的行。默认 `count_by_event` 返回直方图，也支持 `count_by_process` 和 `rows`。它可发现 Defender / EDR provider 事件（如 `AMFilter_FileScan`），但事件存在不等于耗时或性能因果；空结果返回 `no_name_match`。 | Events 视图 |
| `generic_event_top_stacks` | 对**任意** user-mode ETW provider 做 stack-rank 的 top-N 栈：AspNetCore、Kestrel、EFCore、Antimalware-AMFilter、Sense（Defender for Endpoint）、`Microsoft-Windows-DxgKrnl`（GPU）、`Microsoft-Windows-Kernel-Power`（CPU 频率 / C-state），或任何自定义 EventSource。先用 `find_marker` 找出 trace 里有哪些 provider，然后把 `ProviderName` 喂给该工具。可选 `eventNameSubstring` 缩到具体事件类。栈质量取决于 `.wprp` 是否对该 provider 开了 stack-walk。 | Any Stacks（单 provider） |
| `generic_event_caller_callee` | 给定 focus frame 的钻取；metric 是事件计数。 | Any Stacks → Callers / Callees tab |

### 复合诊断

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `diagnose_high_wait` | 高阻塞时间排查的 preview composite。候选按进程生命周期分离，仅在 scoped CSwitch 栈覆盖率支持时补充栈证据，并把 ReadyThread 栈作为关联 wakeup 证据而非 wait 根因证明。它返回明确的 not-concluded reason，不输出 root-cause 字段。 | **[复合]**——把 wait、stack、ReadyThread 视图和证据 provenance 打包到一次调用 |
| `diagnose_slow_startup` | 挑出 wait_ratio 最高的进程（或匹配 `nameSubstring` 的进程），对每个跑 `wait_analysis` + `image_load_timing` + `cpu_top_functions`，覆盖启动窗口。一次调用替代手工编排四次。 | **[复合]**——把 PerfView 四个视图打包成一次调用 |

### Symbols（符号）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `set_symbol_path` | 给运行中的 server 设 `_NT_SYMBOL_PATH`（替换或追加）。 | File → Set Symbol Path… |
| `add_symbol_server` | 追加一个符号服务器 URL，可选本地缓存目录（默认 `%LocalAppData%\WpaMcp\Symbols`）。 | File → Set Symbol Path…（单条） |
| `diagnose_symbols` | 报告模块的 PDB identity / local readiness 并给出 symbol-path 建议。不会因为 trace 中存在 PDB 名称就标成函数帧已解析；真正执行栈 lookup 前，frame count 与 resolution rate 为 null / not measured。 | **[程序化]**——以结构化 JSON + 自动推荐替代 Modules 标签 + Set Symbol Path 对话框 |

---

## 配置

### Trace 缓存

LRU，默认容量 2 条 trace，可用 `WPAMCP_CACHE_SIZE=N` 覆盖。每个查询在完整使用期间持有 cache lease；eviction、unload 或 shutdown 只会 retire entry，最后一个活动 lease 结束后才释放 TraceLog。并发首次访问只允许胜出的 Lazy 打开 / 转换 ETL，不会重复加载同一份大 trace。

### 自己抓 trace

[`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)（英文）提供了一个推荐 `.wprp`，覆盖 CPU + CSwitch + FileIO + DiskIO + HardFaults + Loader stacks。最常用的抓取流程：

```powershell
wpr.exe -start tests\WpaMcp.Tests\fixtures\MmapCapture.wprp -filemode
# … 复现慢的场景 …
wpr.exe -stop C:\path\to\my_capture.etl
```

### Symbols（符号）

> **应从真正执行过 lookup 的栈工具判断符号质量。** `inspect_trace` 能报告 PDB identity 与 local readiness，但不能证明函数帧已经解析。若栈响应的 `SymbolResolutionState=executed` 且观测 code-frame 解析率很低，才需要修正 symbol path；解析率为 null 表示没有可测 code frame，不等于 0%。

#### 路径在哪里设

`_NT_SYMBOL_PATH` 接收用分号分隔的多条 entry：`SRV*<cache>*<url>` 是符号服务器，裸路径是本地 PDB 目录，可以混用。三条配置路径（任选一条——最终都设置同一个环境变量）：

1. **启动前设环境变量**（最干净，重启后仍生效）：
   ```powershell
   [Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH",
       "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols", "User")
   ```
2. **在 MCP 配置 JSON 里加 `env` 块**（见上面的手动安装）。最方便和团队成员共享。
3. **运行时通过工具调用**——直接对 agent 说："*把 symbol path 设成 SRV\*C:\Symbols\*https://msdl.microsoft.com/download/symbols，然后对这个 trace 跑 `diagnose_symbols`*。"

符号缓存默认 `%LocalAppData%\WpaMcp\Symbols`（和 PerfView 的 `C:\Symbols` 分开，避免 PDB lock 争用）。每条 trace 的针对性推荐会出现在 `load_trace` 的返回字段 `SymbolStatus.Recommendations` 里，告诉你针对这份 trace 实际出现的模块应该加哪些服务器。

#### 微软模块之外的符号

`load_trace` 的自动推荐只认它内置的 pattern（Microsoft、Chromium）。自家 DLL、第三方 SDK、内部构建的符号需要显式追加，常见写法：

| 你手上有什么 | 应该追加的 entry |
|---|---|
| 内部团队符号服务器 | `SRV*C:\Symbols*https://internal-symsrv.example.com/symbols` |
| 团队共享的 UNC 盘 | `SRV*C:\Symbols*\\fileserver\symbols` |
| 本地 dev 构建产物（自家 PDB） | `C:\src\myapp\out\Default`（裸路径，无 `SRV*`） |

**顺序有意义**——entry 从左到右尝试，首个签名命中就停。迭代构建时把本地 dev 目录放**最前**，让刚出炉的 PDB 优先于公开 PDB 命中。

#### 自家 DLL 的构建前置条件

符号服务器配得再对，构建本身没产 PDB——或者 PDB 跟最终部署的 DLL 不是同一次构建——也无济于事。

- **.NET / C#**：`<DebugType>portable</DebugType>` + `<DebugSymbols>true</DebugSymbols>`。检查 Release 配置没把 PDB 输出关掉。
- **C++（MSVC）**：`/Zi` + `/DEBUG:FULL`，Release 也要开。PDB 跟 DLL 留同目录。
- PDB 和 DLL 必须共享同一个签名（GUID + age）——重新 link 就生成新签名，老 PDB 不再认新 DLL。

#### 验证是否生效

```
> load_trace C:\my\trace.etl
> diagnose_symbols C:\my\trace.etl
> cpu_top_functions C:\my\trace.etl
```

`diagnose_symbols` 给出 PDB identity、local readiness 与配置 hint；实际 frame 是否解析成功，要运行对应栈工具后查看它的 `SymbolResolutionState`、code-frame resolution rate、事件域栈覆盖率和 synthetic-frame 数，不能套一个通用阈值。普通只读查询使用 query-local effective symbol path，不修改 `_NT_SYMBOL_PATH`；只有 `set_symbol_path` 和 `add_symbol_server` 会有意改变进程状态。

完整配方（UNC 路径、私有 vendor、Chromium 浏览器、缓存管理、踩坑排查）见 [`docs/SYMBOL_RECIPES.zh-CN.md`](docs/SYMBOL_RECIPES.zh-CN.md)（中文）/ [`docs/SYMBOL_RECIPES.md`](docs/SYMBOL_RECIPES.md)（英文）。架构总览和贡献时要注意的不变量见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) 和 [`CONTRIBUTING.md`](CONTRIBUTING.md)（均英文）。
