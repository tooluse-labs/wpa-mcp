<p align="right">
  <a href="README.md">English</a> | <strong>简体中文</strong>
</p>

<p align="center">
  <img src="assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

# wpa-mcp

一个 C# 实现的 MCP server，把 Windows ETW（`.etl`）trace 分析能力——CPU、wait、image-load、文件 / 磁盘 / mmap I/O——通过任意 MCP-兼容客户端（Claude Code、Claude Desktop、Codex、Cursor）暴露出来。设计上**不绑定特定领域**：任何 Windows trace 都能用，常见用途是排查应用启动慢、子进程 fork 延迟、AV 杀毒拖慢系统、磁盘瓶颈回归等。

> **状态——PoC。** 54 个工具已上线，验证完成前限内部使用。仅限 Windows（TraceEvent 内核 parser 不可移植）。Apache-2.0。

> **看一个真实案例：** [一次完整排查](docs/CASE_STUDIES.md)——进程创建慢到基线的 50 倍，通过 wpa-mcp 工具链根因定位到多套 EDR 在 `PsSetCreateProcessNotifyRoutineEx` 上串行回调。同一份 trace 被两个不同 LLM agent 独立复现得到同样结论。

---

## 快速上手

<!-- 动态演示。把录好的 GIF 放在 assets/quickstart-demo.gif 这里就会渲染出来。
     录制食谱见 assets/quickstart-demo-recording.md（英文）。 -->
<p align="center">
  <img src="assets/quickstart-demo.gif" alt="wpa-mcp 快速上手演示——加载 trace、找出慢进程、钻进 fork burst" width="800">
</p>

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

54 个工具分 15 组。底层全部基于 PerfView 同款的 `Microsoft.Diagnostics.Tracing.TraceEvent` 库——分析能力等同 PerfView，区别在**界面（stdio MCP + JSON 替代 Windows GUI）**和**几个把 PerfView 多步操作打包成一次调用的复合工具**。

### wpa-mcp 相对 PerfView 加了什么

* **Agent 驱动而不是 UI 驱动**：PerfView 是 Windows GUI 一路点过去；wpa-mcp 是 stdio MCP server，自然语言对话即可。同样的数据，无 UI 疲劳，方便编进 CI / 回归脚本。
* **复合工具**：`diagnose_slow_startup`、`process_create_timing`、`image_load_top_gaps` 把 PerfView 多步操作打包成一次调用。
* **Capabilities-aware**：每个工具"返回不出数据"的状态都对应到 `load_trace` 的 `Capabilities` map 里某个 keyword bit——不再有"这个视图为啥是空"的 PerfView 经典侦探。
* **per-trace symbol 推荐**：`load_trace` 扫描 trace 里出现的模块、推荐应加哪些 symbol server。PerfView 里这是用户自己摸索的事。

### 调用 pattern

**永远先调 `load_trace`**：它打开 `.etl`、构建（或复用）`.etlx` 索引，并返回 `Capabilities` map——按 keyword 列出"有没有"的检查（`HasCpuSamples`、`HasCSwitch`、`HasFileIo`、`HasDiskIo`、`HasImageLoad`、`HasHardFaults`、`HasStackWalks`、`HasVirtualAlloc`、`HasNetIo`、`HasRegistry`、`HasReadyThread`、`HasInterrupt`、`HasAlpc`、`HasThreadEvents`、`HasClrGc`、`HasClrJit`、`HasClrAlloc`、`HasClrException`、`HasClrContention`、`HasNtHeap`）。其他每个工具的行为都依赖这些 keyword。

大多数组遵循同样的三件套结构：**summary**（top-N 平铺行）、**stacks**（top-N 调用栈，按 metric 加权）、**caller-callee 钻取**（给一个 focus frame，返回其 caller / callee 邻居，metric 加权）——和 PerfView 的 "Callers" / "Callees" tab 同形态。

### Meta（元信息）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| **`load_trace`** | 加载 / 缓存 `.etl`。返回 trace 元信息、`Capabilities` keyword 出现 map、per-trace symbol-server 推荐。首次 30 秒~3 分钟构建 `.etlx`，后续命中缓存即时返回。 | 打开 trace 文件（无 `Capabilities` 等价物） |
| `list_processes` | 列出进程（可按 `cpu` / `wall` / `wait_ratio` 排序）。`WaitRatio = WallUs / CpuUs` 暴露"高 wall、低 CPU"的进程（卡在 minifilter / IPC 等）。默认隐藏 PID 0（Idle）和 PID 4（System）。 | Processes 视图 |
| `process_create_timing` | 给定父 PID，列出每次 fork。`FirstImageLoadOffsetUs` = `ProcessStart` 到首个 DLL 加载之间的内核窗口——AV / EDR 进程创建回调烧时间的位置。中位数 / p95 / max 一次给全。 | （无对应——复合，见 [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md)（英文）） |
| `thread_lifetime` | 给定 PID 的线程生命周期时序——每次 `ThreadStart` / `ThreadStop`，附 `StartTimeUs` / `EndTimeUs` / `LifetimeUs`，加 `PeakConcurrentThreads`。捕捉线程池抖动 / fork bomb 模式。`TraceResidentStart/End` 标识由 trace capture 边界限定（而非真正 spawn / 退出）的线程。 | （无对应——手动按 `Thread/Start` + `Thread/Stop` 过滤 events 视图） |

### CPU 栈

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `cpu_top_functions` | 给定窗口 / PID，按 exclusive CPU 采样数返回 top-N 热点函数。可选 `excludeEtwSelfOverhead` 把 `EtwpLogKernelEvent` 等折成单个 `[ETW Overhead]` 桶。 | CPU Stacks → ByName |
| `cpu_top_functions_batch` | 同上但一次调用覆盖多个 PID。每个 PID 独立 CallTree（inclusive% 按该 PID 的采样数归一化）。 | （无对应——省 N 次往返） |
| `cpu_caller_callee` | 给定 focus frame，返回其 caller（调进 focus）和 callee（focus 调出去），按 inclusive 采样数排序。Recursion-safe。 | CPU Stacks → Callers / Callees tab |

### Wait / 阻塞时间（CSwitch 衍生）

需要 `CSwitch` 内核 keyword（默认 WPR `CPU` profile 已包含）。

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `wait_analysis` | 每线程阻塞时间 + 主导 wait reason。当 CPU 不忙却 wall 高时回答"为啥这步慢"的标准工具。`WrFilterContext`（卡在 Filter Manager minifilter callback）这类 reason 直接定位到内核状态。 | Thread Time → 每线程阻塞时间 |
| `wait_top_stacks` | 按阻塞 μs 加权的 top-N 调用栈，从每次 `ThreadCSwitch` 的 resume-point stack walk 构建。回答"**代码哪里**在等"（vs `wait_analysis` 回答"哪个线程 / 哪种 reason"）。 | Thread Time / Wait Time → BlockedTime metric（`ThreadTimeStackComputer`） |
| `wait_caller_callee` | 给定 focus frame 的 caller-callee 钻取；metric 是阻塞 μs。 | Thread Time → Callers / Callees tab |

### Image / DLL 加载

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `image_load_timing` | 单进程的 DLL 加载时序（按时间排序），每行带相对 `ProcessStart` 的偏移。用来发现"延迟加载的 DLL"或者"两个 DLL 之间长 gap"——后者常见于 minifilter / sig-scan 串行扫描。 | （无直接对应——手动过滤 events list） |
| `image_load_top_gaps` | 相邻 DLL 加载之间 gap 最大的 top-N 行。和 `image_load_timing` 同源数据，按 gap 排序。响应里也带 `FirstLoadOffsetUs`（首个 DLL 之前的内核 fork 税）。 | （无对应——自定义视图） |
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
| `hard_fault_by_file` | 按**硬页错误（hard page-in）字节**排序的 top-N 文件。多数硬页错误来自首次访问的 mmap'd 文件（DLL、数据文件、网络共享内容），少数来自被换出的 heap/stack 和 page file。回答"哪个文件造成 page-in 加载"。需要 `HardFaults` keyword（**默认 WPR profile 不带**——见 [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)（英文））。 | Memory Hard Fault → ByFile |
| `hard_fault_top_stacks` | 按硬页错误页入字节加权的 top-N 栈。区分 eager loader 引起的 page-in 和 lazy / 扫描器触发的 page-in。 | Memory Hard Fault Stacks |
| `hard_fault_caller_callee` | 给定 focus frame 的钻取；metric 是 page-in 字节。 | Memory Hard Fault Stacks → Callers / Callees tab |

### 虚拟内存

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `virtual_alloc_top_stacks` | 按 `VirtualMemAlloc` + `VirtualMemFree` 字节加权的 top-N 栈。和物理驻留（`mmap_*`）不同——回答"谁在保留 4 GB 地址空间"/ "谁在泄漏 VirtualAllocs"。每行带 `Bytes` 和 `OpCount`。需要 `VirtualAlloc` 内核 keyword（**默认 WPR `CPU` profile 不带**）。 | VirtualAlloc Stacks |
| `virtual_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是虚拟内存字节。 | VirtualAlloc Stacks → Callers / Callees tab |
| `heap_alloc_top_stacks` | 按 **NT 堆**分配字节（`RtlAllocateHeap` / `HeapAlloc` / `malloc` / `new`——任何走 user-mode heap 的分配）加权的 top-N 栈。Native 内存泄漏的标准工具。和 VirtualAlloc 不同：VirtualAlloc 预留页粒度的地址空间，堆分配器在其上做子分配。响应里拆出 `AllocBytes` / `ReallocBytes`。Free 事件不携带 size，不计入。需要 **per-process** 启用 `Heap` provider（默认 WPR profile 不带；用 PerfView `/HeapTrace` 或自定义 `.wprp` 的 `<Heap>` 元素）。 | HeapAllocStacks |
| `heap_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是 NT 堆字节。 | HeapAllocStacks → Callers / Callees tab |

### 网络 I/O

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `net_top_stacks` | 按网络字节加权的 top-N 栈——TCP + UDP、IPv4 + IPv6 send/recv 合并。响应里拆出 `TcpBytes` / `UdpBytes`。配合 `wait_analysis` 排查"高 wall、低 CPU"且阻塞在网络往返的场景。`Connect` / `Accept` / `Disconnect` 这类无字节 metric 的事件不计入——用 `find_marker`。需要 `NetworkTrace` keyword（**默认 `CPU` profile 不带**）。 | TCP/IP Stacks + UDP/IP Stacks（合并） |
| `net_caller_callee` | 给定 focus frame 的钻取；metric 是网络字节。 | TCP/IP Stacks → Callers / Callees tab |
| `net_connections` | 按 `connid` 配对 Connect/Accept 与 Disconnect/Reconnect，给出每条 TCP 连接"在 T1 打开、T2 关闭，持续 T2−T1"。适合"连接建立到关闭的延迟离群点"/"RPC 慢是因为连接建立慢吗"。IPv4 + IPv6 合并，带 `IsIPv6` 标志。trace 结束时仍开启的连接 `TraceResidentEnd=true`。 | （无直接对应——Events 视图手工配对） |

### 注册表

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `registry_top_stacks` | 按注册表操作计数加权的 top-N 栈（Query / Open / Create / SetValue / EnumerateKey 等）。回答"谁在每条热路径上敲注册表"。Metric 是操作数（注册表没有自然的字节度量）。需要 `Registry` keyword（**默认 `CPU` profile 不带**）。 | Registry Stacks |
| `registry_caller_callee` | 给定 focus frame 的钻取；metric 是注册表操作数。 | Registry Stacks → Callers / Callees tab |

### ReadyThread（因果链）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `ready_thread_top_stacks` | top-N **唤醒方**栈（执行 `SetEvent` / 释放锁 / IOCP 完成等动作把阻塞线程叫醒的代码）。配合 `wait_analysis`：那个工具回答"线程 X 阻塞在 Y 等了 Z μs"——这个工具补上"是谁最终叫醒它的"。`awakenedPid` 过滤"谁在唤醒该 PID 的线程"。需要 `CSwitch` / `ReadyThread` keyword（默认内核 profile 已带）。 | ReadyThread Stacks |
| `ready_thread_caller_callee` | 给定 focus frame 的钻取；metric 是 ready 事件计数。 | ReadyThread Stacks → Callers / Callees tab |

### 中断（DPC / ISR）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `interrupt_top_stacks` | 按内核中断时间（DPC + ISR 微秒数）加权的 top-N 栈。暴露在高 IRQL 烧 CPU 的驱动热例程——常见嫌疑是消费级 GPU 驱动、高负载下的网卡驱动、AV minifilter 回调。健康系统该视图应 <5% 的 trace CPU。响应里拆出 `DpcUs` / `IsrUs`。需要 `Interrupt` + `DPC` keyword（默认 `CPU` profile 全带）。 | DPC/ISR Stacks |
| `interrupt_caller_callee` | 给定 focus frame 的钻取；metric 是中断 μs。 | DPC/ISR Stacks → Callers / Callees tab |

### ALPC（跨进程 IPC）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `alpc_top_stacks` | 按 ALPC 消息计数（Send + Receive）加权的 top-N 栈。ALPC 是 Windows 内核 IPC 原语，RPC、COM、AppContainer broker 调用、lsass、SCM 以及绝大多数 Windows 服务表面都走它——用来回答"是不是慢在某次 LPC 往返"/ "哪条调用链做了所有跨进程 IPC"。需要 `ALPC` keyword（**默认 `CPU` profile 不带**）。 | ALPC Stacks |
| `alpc_caller_callee` | 给定 focus frame 的钻取；metric 是 ALPC 消息计数。 | ALPC Stacks → Callers / Callees tab |

### CLR（.NET runtime）

需要 `Microsoft-Windows-DotNETRuntime` ETW provider（WPR `.wprp` 文件需要显式 `<EventCollectorId>`）。

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `clr_gc_analysis` | 列出每次 GC，附 wall 时长**以及** stop-the-world 暂停时长。`GCStart`→`GCStop` 是 wall 区间；`GCSuspendEEStart`→`GCRestartEEStop` 是真正的 mutator 暂停（对 background / concurrent GC 关键——其 wall 远大于真实 pause）。每行带 `Generation` / `Reason` / `PauseUs`，并报 `TotalGcCount` / `Gen0Count` / `Gen1Count` / `Gen2Count` / `TotalPauseUs`。 | GCStats |
| `clr_jit_analysis` | 按 JIT 编译耗时加权的 top-N 方法。按 `(PID, MethodID)` 匹配 `MethodJittingStarted`→`MethodLoadVerbose`。R2R / NGen / 预编译方法不发 `JittingStarted`，因此对该工具不可见——这是"trace 里 JIT 成本"的正确语义。 | JIT Stats |
| `clr_alloc_top_stacks` | 按托管堆分配字节加权的 top-N 栈，由 `GCAllocationTick` 事件驱动（CLR 每分配约 100 KB 触发一次，按 `(堆、代、类型)` 分桶——是采样而非全量，低开销，CLR ≥ 4.0 默认即开）。响应包含 `TopTypes`（按字节排序的 top 类型名）。"谁在请求热路径上分配大量 string"的标准工具。需要 `GC` keyword。 | GC Heap Alloc Stacks |
| `clr_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是分配字节。 | GC Heap Alloc Stacks → Callers / Callees tab |
| `clr_exception_top_stacks` | 按 .NET 异常抛出计数加权的 top-N 栈（`ExceptionStart` 事件）。适合"这条代码路径每秒抛 1000 个异常吗"/"哪里在 retry 循环里吞 `FormatException`"。响应包含 `TopTypes`（top 异常类型名）。需要 `Exception` keyword。 | Exceptions Stacks |
| `clr_exception_caller_callee` | 给定 focus frame 的钻取；metric 是异常计数。 | Exceptions Stacks → Callers / Callees tab |
| `clr_contention_top_stacks` | 按托管 monitor 阻塞 μs 加权的 top-N 栈——即 `lock` / `Monitor.Enter` 的等待。按 `ThreadID` 匹配 `ContentionStart`→`ContentionStop`。只统计 `ContentionFlags.Managed`（同 provider 的 native 锁竞争被排除）。托管代码的锁热点标准工具。需要 `Contention` keyword。 | Monitor Contention Stacks |
| `clr_contention_caller_callee` | 给定 focus frame 的钻取；metric 是阻塞 μs。 | Monitor Contention Stacks → Callers / Callees tab |
| `clr_gc_heap_stats` | 托管堆快照时序——每次 GC 结束时 CLR 触发一次 `GCHeapStats` 事件，每行带 `TotalHeapBytes`、`Gen0/1/2/LOH/POH` 大小、`PinnedObjectCount`、`GcHandleCount`。回答"堆是不是在泄漏"/"pinned 对象在不在涨"，无须多次工具调用。配合 `clr_gc_analysis` 使用。 | GCStats per-GC snapshot 表 |
| `clr_finalizer_analysis` | top-N 被 finalize 的类型 + finalizer 线程的暂停批次。`GCFinalizeObject` 按 `TypeName` 聚合得到 TopTypes 表；`GCFinalizersStart`→`GCFinalizersStop` 配对得到每批次列表（Stop 携带这批次跑了多少个 finalizer）。回答"GC 为啥慢"（finalizer 队列会拖住下一次 GC）和"谁在分配可 finalize 的对象"。 | （无直接对应——GCStats 字段 + Events 视图过滤的复合） |

### Marker / 通用 ETW 事件

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `find_marker` | 搜索所有名字 / task 包含给定 substring 的 ETW 事件。默认模式 `count_by_event` 返回直方图（避免 token 爆炸）；也支持 `count_by_process` 和 `rows`（完整事件细节）。挖一方 Defender / EDR provider 遥测最有效——比如 `Microsoft-Antimalware-AMFilter` provider 的 `AMFilter_FileScan` 行直接告诉你扫描器在干啥。 | Events 视图 |
| `generic_event_top_stacks` | 对**任意** user-mode ETW provider 做 stack-rank 的 top-N 栈：AspNetCore、Kestrel、EFCore、Antimalware-AMFilter、Sense（Defender for Endpoint）、`Microsoft-Windows-DxgKrnl`（GPU）、`Microsoft-Windows-Kernel-Power`（CPU 频率 / C-state），或任何自定义 EventSource。先用 `find_marker` 找出 trace 里有哪些 provider，然后把 `ProviderName` 喂给该工具。可选 `eventNameSubstring` 缩到具体事件类。栈质量取决于 `.wprp` 是否对该 provider 开了 stack-walk。 | Any Stacks（单 provider） |
| `generic_event_caller_callee` | 给定 focus frame 的钻取；metric 是事件计数。 | Any Stacks → Callers / Callees tab |

### 复合诊断

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `diagnose_slow_startup` | 挑出 wait_ratio 最高的进程（或匹配 `nameSubstring` 的进程），对每个跑 `wait_analysis` + `image_load_timing` + `cpu_top_functions`，覆盖启动窗口。一次调用替代手工编排四次。 | （无对应——复合） |

### Symbols（符号）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `set_symbol_path` | 给运行中的 server 设 `_NT_SYMBOL_PATH`（替换或追加）。 | File → Set Symbol Path… |
| `add_symbol_server` | 追加一个符号服务器 URL，可选本地缓存目录（默认 `%LocalAppData%\WprMcp\Symbols`）。 | File → Set Symbol Path…（单条） |
| `diagnose_symbols` | 针对已加载的 trace 报告每个模块的符号状态，给未解析模块的修复建议（应该加哪些服务器）。 | （无对应——程序化）|

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

私有 vendor 符号服务器、Chromium 系列浏览器、本地 build PDB 目录等更多配方见 [`docs/SYMBOL_RECIPES.md`](docs/SYMBOL_RECIPES.md)（英文）。架构总览和贡献时要注意的不变量见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) 和 [`CONTRIBUTING.md`](CONTRIBUTING.md)（均英文）。
