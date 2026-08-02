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

装好之后（[一行命令在下面](#安装)），直接用自然语言问 agent。server 提供完整、
可分页的能力地图，让模型无需猜测，也不会静默丢掉低频专用工具：

```
> 这个 server 能分析什么？
（list_capabilities——51 个 declared capabilities，其中包含显式 gap；关联
 15 个 goals、15 个 workflows 和可调用工具；必须跟完全部 cursor page）

> 加载这个 trace：C:\path\to\trace.etl
（load_trace——唯一 raw source 入口；把允许的本地 trace 快照到 server-owned
 artifact store，并返回 principal-scoped TraceId。）

> 看一下这份 trace 能回答什么问题。
（inspect_trace——用 TraceId 返回 trace evidence map、同域栈覆盖率、PDB identity
 metadata、quality boundary、workflow 和适用工具；不宣称本地符号 ready 或 frame 已解析。）

> 为这份 trace 准备启动时批准的本地符号。
（prepare_symbols——可选；精确校验 PDB identity 后返回 immutable SymbolContextId，
 但仍不声称 frame 已解析。）

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
# PowerShell——指定 tag、限定客户端、批准一个本地 PDB candidate root
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag v0.2.24 -Client claude-desktop -SymbolLocalRoot 'C:\Symbols' -SymbolStoreRoot '$env:LOCALAPPDATA\WpaMcp\symbol-store'"
```

```bash
# Bash——`bash -s --` 后面的 flag 会传给 install.ps1
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash -s -- -Tag v0.2.24
```

### 卸载（一行命令，对称）

同样支持远程一行调用；反向修改之前注册的客户端配置。不动下载缓存。

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.ps1) }"
```

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
```

会从所有检测到的 MCP 客户端中移除 `wpa-mcp` 条目，并删除 `%USERPROFILE%\.local\bin\wpa-mcp.exe`。approved candidate 目录与 private verified-symbol store 会保留。

### 系统要求

- Windows 10 / 11（TraceEvent 内核 API 仅 Windows）
- 一行安装路径不需要 .NET runtime；release 已包含 self-contained Windows executable。
- verified symbol readiness 需要：把可信 PDB candidate 放入启动时批准的本地 root，再对已加载的 TraceId 调 `prepare_symbols`（见[Symbol 配置](#symbol-configuration)）。secure profile 不读取 `_NT_SYMBOL_PATH`，也不抓取远端符号；当前 build 尚不能用返回的 context 解析 frame。

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
.\scripts\setup.ps1 -SymbolLocalRoot "C:\Symbols" -SymbolStoreRoot "$env:LOCALAPPDATA\WpaMcp\symbol-store"
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
dotnet src\WpaMcp\bin\Release\net10.0\WpaMcp.dll --version    # 输出 "WpaMcp 0.3.0"
dotnet test                                                   # 跑 xUnit 套件（需要 fixture，见 CONTRIBUTING.md）
```

然后注册到你的 MCP 客户端。DLL 路径**必须用绝对路径**。

**Claude Code**——按项目（`<project>/.mcp.json`）或全局（`~/.claude.json`）：

```json
{
  "mcpServers": {
    "wpa-mcp": {
      "command": "dotnet",
      "args": [
        "C:/Users/me/Dev/wpa-mcp/src/WpaMcp/bin/Release/net10.0/WpaMcp.dll",
        "--symbol-local-root",
        "C:\\Symbols",
        "--symbol-store-root",
        "C:\\Users\\me\\AppData\\Local\\WpaMcp\\symbol-store",
        "--cache-size",
        "2"
      ]
    }
  }
}
```

或者用 CLI helper：

```powershell
claude mcp add wpa-mcp --scope user -- dotnet C:/Users/me/Dev/wpa-mcp/src/WpaMcp/bin/Release/net10.0/WpaMcp.dll
```

**Claude Desktop**——`%APPDATA%\Claude\claude_desktop_config.json`，结构和上面一样。

**Codex / Cursor / 其它 MCP-兼容客户端**——server 走 stdio MCP；任何接受 `command + args` 配置的客户端都行。用上面那段 JSON。

**验证**——重启客户端后，工具会以 `mcp__wpa-mcp__load_trace` 这种命名出现。第一次对一个新 `.etl` 调 `load_trace` 可能花 30 秒~3 分钟在 owned artifact store 中 materialize 索引（日志写到 stderr）。

</details>

---

## 工具

当前 validated development surface 包含 **60 个 active tools、51 个 declared
capabilities、15 个 goals、15 个 workflows**。capability 数量包含显式声明的 gap；
它只对本 server catalog 完整，并不覆盖整个 WPA/ETW universe。客户端必须跟完
`tools/list` 和 `list_capabilities` 的每个 cursor page，不能把第一页或这里的快照数字
硬编码成完整目录。

底层使用 PerfView 同款 `Microsoft.Diagnostics.Tracing.TraceEvent` 库，但共用 parser
不等于视图天然等价。每个 analyzer 都公开 scope、source capability evidence、
completeness、precision 与 conclusion boundary，让调用方判断结果究竟证明了什么。

能力优先的客户端可调用 `list_capabilities`；支持 Resource 的客户端可先读
`wpa://capabilities/server`、`wpa://tools/server`、`wpa://workflows/server`。
选定工具后，应跟完 `wpa://tools/{toolName}/sections` 的全部页，取得每个 section 的
ordering、truncation proof、evidence、measurement、relationship 与 conclusion
contract。Resource 用来降低选择成本，不允许客户端据此隐藏工具或跳过 `tools/list` 页。

### wpa-mcp 相对 PerfView 加了什么

* **Agent 驱动而不是 UI 驱动**：PerfView 是 Windows GUI 一路点过去；wpa-mcp 是 stdio MCP server，自然语言对话即可。同样的数据，省去界面操作，方便编进 CI / 回归脚本。
* **复合工具**：`diagnose_window`、`diagnose_high_wait`、`diagnose_slow_startup`、`process_create_timing`、`image_load_top_gaps` 把 PerfView 多步操作打包成一次调用。
* **两层能力地图**：`list_capabilities` 说明 server 声明什么；`inspect_trace` 评估一份已加载 trace 实际支持什么。缺少 parsed evidence 不会被静默升级为 capture keyword 结论。
* **显式符号证据**：trace PDB identity、verified local artifact readiness 与实际 frame-name resolution 是三种不同状态。`prepare_symbols` 可以建立第二种；当前 build 没有 context-bound TraceEvent frame resolver，因此第三种保持 declared gap，不能从 readiness 推断。

### 设计理念

wpa-mcp 遵循一个总原则：**完整暴露能力，用能力地图降低选择成本；完整暴露证据
边界，用结构化契约阻止 LLM 过度解释。**

* **Orientation 工具**（`list_capabilities`、`load_trace`、`inspect_trace`）先暴露 server 声明、trace evidence map、quality gap 与 workflow，让模型按事实选择，而不是从空结果反推。
* **Diagnostic composite**（`diagnose_window`、`diagnose_high_wait`、`diagnose_slow_startup`）压缩调用路径但保留证据链——通过 `Evidence`、`NotConcluded`、`ExecutedToolCalls`、`NextTools` 字段输出，故意不返回综合出的 "root cause" 字段。
* **Per-domain 行 / 栈工具**贴近 PerfView 形态。进程级工具暴露所选 `(Pid, ProcessStartUs)` 生命周期，或明确标注按 PID 聚合；栈工具报告目标事件域自己的覆盖率，不拿其他事件的栈来推断当前工具可用。

### Contract 2.0 证据 envelope

全部 60 个 active tools 都返回同一 closed structured envelope。解释领域行之前必须
先解释它：

| 字段 | 契约 |
|---|---|
| `status`、`data`、`error`、`noData` | 区分成功有数据、成功无数据、部分结果和执行/投递失败。脱离结构状态的空领域数据没有稳定含义。 |
| `toolRef`、`traceRef`、`scope` | 标识准确工具/契约、immutable trace generation/可选 symbol context，以及解析后的进程/线程/窗口。selector 非 `ok` 不是“成功但为空”。 |
| `capabilityEvidence` | 分离全 trace 与 scoped availability/count、completion 和 capture integrity；两种范围的值不能互作分子分母。 |
| `completeness`、`sections`、`hasMore` | 逐 section 报告 role、returned/total state、精确 sort/tie-breaker、遗漏状态、proof mode；只有确实能续页时才给 cursor。 |
| `evidenceBoundary` | 声明 evidence ID、measurement basis、relationship、conclusion status、provenance 和 `doesNotProve`。association/heuristic evidence 不是 causal attribution。 |
| `precision` | 声明 identifier/metric precision、rounding、accounting 和 denominator。公开 stack metric 使用并行 checked Int64 accumulator，不把 TraceEvent 的 float sample metric 倒灌成“精确整数”。 |

每个工具的完整 section contract 还发布在
`wpa://tools/{toolName}/sections`。异质 composite 的多个 section 不能共用一个
tool-wide 排序或证明声明。

如果最小合法成功 envelope 仍放不进精确 frame budget，server 返回 terminal
`response_too_large` failure：`data=null`、`scope=null`、空
`sections/failedSections`、`hasMore=false`。它表示投递失败，不表示请求 scope 没事件，
也不包含 continuation。

opaque ID 是 JSON string，绝不能经过 JavaScript `number`。重放进程行必须保留
`pid + processStartUs`；线程还要保留 `tid + threadStartUs + threadGeneration`。

secure ID-only profile 下，57 个分析/发现工具声明为 read-only、idempotent、
closed-world、non-destructive。`load_trace` 会写 owned artifact store，
`prepare_symbols` 可能写 private verified-symbol store，`unload_trace` 会 retire handle。
启动时选定 profile 的投影 annotation 才是权威；应读取 `wpa://runtime/profile`，不要
假设所有 profile 的副作用相同。

### 使用方式

不熟悉 server surface 时先调 `list_capabilities`。开始 trace 分析时，**任何 trace
query 之前都必须先调 `load_trace`**，并使用返回的 TraceId。它把允许的本地 source
快照到 server-owned artifact store；返回的是 parsed evidence，不是原始 capture
keyword 配置证明。随后由 `inspect_trace` 投影 trace-specific evidence map。已解析域包括：

* **CPU 采样和调度** —— `HasCpuSamples`、`HasCSwitch`、`HasReadyThread`、`HasStackWalks`
* **文件 / 磁盘 / mmap I/O 和 loader** —— `HasFileIo`、`HasDiskIo`、`HasHardFaults`、`HasImageLoad`
* **内存** —— `HasVirtualAlloc`、`HasNtHeap`、`HasMemoryProcessInfo`、`HasHandleEvents`、`HasPoolEvents`
* **网络** —— `HasNetIo`、`HasNetConnections`
* **内核基础设施** —— `HasRegistry`、`HasInterrupt`、`HasAlpc`、`HasThreadEvents`
* **CLR runtime** —— `HasClrGc`、`HasClrJit`、`HasClrAlloc`、`HasClrException`、`HasClrContention`

`HasStackWalks` 仅是兼容用的全局并集。解释某个栈工具前，应查看该事件域的 `StackCoverage`：事件覆盖（`TotalEventCount`、`StackedEventCount`、`StackCoveragePct`）、metric 加权覆盖（`TotalMetric`、`StackedMetric`、`MetricStackCoveragePct`）和 `CoverageState`（`no_events`、`no_stacks`、`partial` 或 `full`）。`StackSemantics` 标识实际统计的栈来源；尤其 `cswitch` 域使用 switch-out `BlockingStack`，而调试 probe 的普通 CSwitch `CallStackIndex` 是另一类栈，两者覆盖率可能不同。合成的 `?!?` 行只是给无栈事件记账；`ContainsSyntheticUnknown` 表示当前结果是否实际包含它，它绝不是真实调用链。

完整调用流程：

```
list_capabilities ──► declared capabilities + goals + workflows

.etl source ──► load_trace ──► TraceId ──► inspect_trace evidence map
                              │
                              ├────► composite / domain query（未解析符号）
                              │
                              └────► 可选 prepare_symbols
                                          │
                                          ▼
                                  SymbolContextId（仅 readiness）
                                  当前 resolveSymbols=true 会返回
                                  symbol_resolution_unavailable

  Composite  （推荐用于常见 workflow）
  ──────────────────────────────────
  diagnose_window、diagnose_slow_startup、diagnose_high_wait
  返回 Evidence + NotConcluded + ExecutedToolCalls + NextTools
                                                          │
                                                          │  通过 NextTools
                                                          ▼

  Domain drill  （自定义调查，或 composite 之后的钻取）
  ──────────────────────────────────────────────────
  summary  ──►  stacks  ──►  caller_callee
  top-N         top-N         focus-frame
  行            调用栈        钻取

  示例：file_io_top_files  ──►  file_io_top_stacks  ──►  file_io_caller_callee
```

常见 workflow 优先用 `diagnose_window`、`diagnose_high_wait`、
`diagnose_slow_startup` 这类 composite，再按 section/evidence contract 钻取。它们会
说明执行了什么、哪些结论不能下、下一步去哪里。当前 composite 仍是 direct execution，
不能把它当成“单次 shared planner dispatch”的证据。

具备全部三层的 domain 遵循这一结构：**summary**（top-N 平铺行）、**stacks**
（top-N 调用栈，按 metric 加权）、**caller-callee 钻取**（给一个 focus frame，
返回其 caller / callee 邻居，metric 加权）——形式与 PerfView 的 "Callers" /
"Callees" tab 一致。CPU 是例外：从 `cpu_top_functions` 直接钻取到
`cpu_caller_callee`；当前没有 active `cpu_top_stacks` 工具。

下面的表格里 "PerfView 对应" 列指 PerfView GUI 中的对应视图。标 **[复合]** 的把多个 PerfView 视图打包成一次调用，标 **[手动过滤]** 的暴露 PerfView Events 视图能看到但没预聚合的原始事件，标 **[程序化]** 的用结构化 JSON 替代 GUI 对话框。其余多数工具是 PerfView 视图的 1:1 映射。

### 时间窗口语义

接受 `startUs` 和 `endUs` 的工具使用半开区间：事件包含的条件是 `startUs <= timestamp < endUs`。边界为 null 分别表示 trace 开始和 trace 结束。

PID 被复用时，进程级工具应同时传入 `list_processes` 返回的 `processStartUs`。只传 PID 的聚合调用会显式返回 `ScopeMode=pid_aggregate`，并在 `IncludedProcesses` 保留所含生命周期 key；行和总量可能按各工具自己的 accounting 契约跨生命周期合并。必须唯一实例的工具遇到正常复用时，结构化返回 `ScopeStatus/NoDataReason=process_start_required` 和可重放候选；`ambiguous_process_instance` 只用于不安全 / 冲突的生命周期证据。解释空 `Rows` 前先检查 `ScopeStatus`、`CapabilityStatus`、`MatchedEventCount`、`NoDataReason`、`PidReuseObserved` 和 `IncludedProcesses`。`CapabilityStatus=observed` 只表示解析成功的目标范围匹配到源事件；`not_observed` 仅用于已确认的全局/未过滤缺失，其他情况为 `unknown`。

对同时接受 `tid` 的 CPU/Wait 工具，应使用 `threadStartUs` 与可选 `threadGeneration` 区分线程复用。缺失或歧义线程会返回结构化 `scope_not_found` / `ambiguous_thread_instance`，不会退化成 PID-only 数据；`IncludedThreads` 带 `ThreadStartUs` / `ThreadEndUs` / `Thread.Generation`，可用 `pid + processStartUs + tid + threadStartUs + threadGeneration` 精确重放候选。对于捕获边界推断出的相同开始时间，generation 是最终消歧键。

不接受 `startUs` / `endUs` 的工具有意采用不同的作用域；每个工具的 MCP description 会说明是哪一种：

* **Server catalog** —— `list_capabilities` 不属于任何 trace scope。
* **Trace/symbol lifecycle** —— `load_trace` 接收 raw source 并返回 TraceId；`prepare_symbols` 接收 TraceId 并返回 SymbolContextId；`unload_trace` 只 retire public handle。
* **全 trace orientation/query** —— `inspect_trace`、`list_processes`、`find_marker` 查询已加载的 immutable generation。
* **生命周期视图** —— `process_create_timing`、`thread_lifetime`、`image_load_timing`、`image_load_top_gaps`、`diagnose_slow_startup` 用进程启动相对或生命周期相对的窗口，而不是任意 trace 窗口。
* **全 trace 或窗口化 by-file 汇总** —— `file_io_top_files` 和 `hard_fault_by_file` 按文件名汇总，并支持显式 `startUs` / `endUs` 窗口。需要事件关联调用链证据时用对应的 stack 工具。

### Meta（元信息）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| **`list_capabilities`** | 可分页 Server Capability Map：declared capability（含显式 gap）、goal、workflow、可调用工具、cost/scope/symbol requirement 与 evidence boundary。只对本 server catalog 完整，不代表 WPA 全集。 | **[程序化]**——无直接 GUI 等价物 |
| **`load_trace`** | 唯一 raw trace-source 入口。校验允许的本地 `.etl`/`.etlx`，从已打开 handle 快照进 owned immutable artifact store，并返回 canonical principal-scoped TraceId。parsed event count 不是 raw ETW record count。 | 打开 trace 文件（无 TraceId 等价物） |
| **`unload_trace`** | retire TraceId、拒绝新 acquire，并可等待 lease drain。它不删除 immutable artifact，也不宣称物理空间已经释放。 | 关闭 trace handle |
| **`inspect_trace`** | 可分页 Trace Evidence Map：parsed capability assessment、system metadata、provider count、同域 stack coverage、trace PDB identity、quality boundary、self-attribution、适用工具与 workflow。它不探测本地 PDB，也不测 frame resolution；`prepare_symbols` 只建立 verified local readiness，当前 build 的 context-bound frame lookup 仍不可用。 | **[程序化]**——替代手工跨 Events、Modules、capture metadata 做 trace 质量检查 |
| `list_processes` | 通过 cursor 分页列出完整进程生命周期 inventory（可按 `cpu` / `wall` / `wait_ratio` 排序）；必须用相同 query 跟完全部 `nextCursor`。`WaitRatio = WallUs / CpuUs` 只用于排序"高 wall、低 CPU"候选，不能识别具体等待对象。默认隐藏 PID 0（Idle）和 PID 4（System）。 | Processes 视图 |
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
| `hard_fault_by_file` | 按**硬页错误（hard page-in）字节**排序相关 backing file mapping，可用 `startUs` / `endUs` 限定窗口。多数硬页错误来自首次访问的 mmap'd 文件（DLL、数据文件、网络共享内容），少数来自被换出的 heap/stack 和 page file。行内 `MaxLatencyTimeUs` 可用于精确缩放到最慢 page-in；该视图仍不证明上层原因。需要 `HardFaults` keyword（**默认 WPR profile 不带**——见 [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)（英文））。 | Memory Hard Fault → ByFile |
| `hard_fault_top_stacks` | 按硬页错误页入字节加权事件附带栈。它可支持 eager/lazy 访问或并发扫描等假设，但不能单独证明上层原因。 | Memory Hard Fault Stacks |
| `hard_fault_caller_callee` | 给定 focus frame 的钻取；metric 是 page-in 字节。 | Memory Hard Fault Stacks → Callers / Callees tab |

### 虚拟内存

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `memory_resource_analysis` | 基于 `Memory/ProcessMemInfo` 的进程资源快照：working set、commit、推导 private bytes、private working set、virtual size，以及观测到的 handle create/close 和 pool alloc/free delta。需要 `MemoryInfoWS`、`Handle`、`Pool`，可用 `MemoryCapture.wprp`。行按资源大小 / delta 排序，不代表严重性或因果；pool 行是捕获窗口 delta，不是当前绝对计数。 | Memory / Handles 视图 |
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
| `clr_gc_analysis` | 列出每次 GC 的 wall 与 stop-the-world 暂停区间；先在完整 trace 上配对，再投影到请求窗口。`DurationUs` / `PauseUs` 与 `TotalGcUs` / `TotalPauseUs` 是 accounted 裁剪重叠量的兼容别名；`FullDurationUs` / `FullPauseUs` 与 `TotalFullGcUs` / `TotalFullPauseUs` 保留完整配对区间值。`GCStart`→`GCStop` 界定 wall 区间，`GCSuspendEEStart`→`GCRestartEEStop` 界定 mutator 暂停。 | GCStats |
| `clr_jit_analysis` | 按 JIT 编译耗时加权的 top-N 方法。先在完整 trace 上按 `(ProcessInstanceKey, ClrInstanceId, MethodId)` 匹配 `MethodJittingStarted`→`MethodLoadVerbose`，再投影到请求窗口。`JitDurationUs` 是 accounted 裁剪重叠量的兼容别名，`FullDurationUs` 是完整配对时长；`MethodIlSize` 表示 IL 字节数，不是生成的 native code 大小。R2R / NGen / 预编译方法不发 `JittingStarted`，因此不可见。 | JIT Stats |
| `clr_alloc_top_stacks` | 按托管堆分配字节加权的 top-N 栈，由 `GCAllocationTick` 事件驱动（CLR 每分配约 100 KB 触发一次，按 `(堆、代、类型)` 分桶——是采样而非全量，低开销，CLR ≥ 4.0 默认即开）。响应包含 `TopTypes`（按字节排序的 top 类型名）。"谁在请求热路径上分配大量 string"的标准工具。需要 `GC` keyword。 | GC Heap Alloc Stacks |
| `clr_alloc_caller_callee` | 给定 focus frame 的钻取；metric 是分配字节。 | GC Heap Alloc Stacks → Callers / Callees tab |
| `clr_exception_top_stacks` | 按 .NET 异常抛出计数加权的 top-N 栈（`ExceptionStart` 事件）。适合"这条代码路径每秒抛 1000 个异常吗"/"哪里在 retry 循环里吞 `FormatException`"。响应包含 `TopTypes`（top 异常类型名）。需要 `Exception` keyword。 | Exceptions Stacks |
| `clr_exception_caller_callee` | 给定 focus frame 的钻取；metric 是异常计数。 | Exceptions Stacks → Callers / Callees tab |
| `clr_contention_top_stacks` | 按托管 monitor 阻塞 μs 加权的 top-N 栈——即 `lock` / `Monitor.Enter` 的等待。按 `ThreadInstanceKey`（进程生命周期 + TID 代次）匹配 `ContentionStart`→`ContentionStop`，仅计入与请求窗口的重叠量；`TotalFullBlockedUs` 保留完整配对时长。只有完整配对才计入阻塞指标与栈覆盖率。只统计 `ContentionFlags.Managed`（排除 native contention）。需要 `Contention` keyword。 | Monitor Contention Stacks |
| `clr_contention_caller_callee` | 给定 focus frame 的钻取；metric 是阻塞 μs。 | Monitor Contention Stacks → Callers / Callees tab |
| `clr_gc_heap_stats` | 托管堆快照时序，包含各代 heap 大小、pinned-object 与 GC-handle 计数。用于识别趋势；持续上升本身并不能证明 leak 或给出对象 retention path。配合 `clr_gc_analysis` 使用。 | GCStats per-GC snapshot 表 |
| `clr_finalizer_analysis` | top-N 观测到被 finalize 的类型 + finalizer 线程执行批次。`GCFinalizeObject` 按 `TypeName` 聚合，`GCFinalizersStart`→`GCFinalizersStop` 配对；批次时长不自动等于应用暂停。它可辅助判断 finalizer 工作是否与 GC 延迟重叠，但不能单独归因慢 GC，也不能定位分配点。 | **[复合]**——把 GCStats 字段 + Events 视图过滤合并到一次调用 |

### Marker / 通用 ETW 事件

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `find_marker` | 搜索所有 materialized ETW 事件中名字 / task 包含给定 substring 的行。默认 `count_by_event` 返回直方图，也支持 `count_by_process` 和 `rows`。它可发现 Defender / EDR provider 事件（如 `AMFilter_FileScan`），但事件存在不等于耗时或性能因果；空结果返回 `no_name_match`。 | Events 视图 |
| `security_scan_analysis` | 聚合已知 Defender scan schema 与 scan-like vendor/provider 事件。PID 指 payload target PID；缺 target identity 时只使用并明确标注 emitter fallback。`EvidenceKind`、`Provenance`、`Confidence` 区分高置信配对 schema 与低置信名字启发式。事件存在、厂商分类和时间重叠都不能单独证明 AV 扫描或性能根因；`diagnose_window` 会调用它。 | **[复合]**——Defender 配对事件 + Events 启发式证据 |
| `generic_event_top_stacks` | 对**任意** user-mode ETW provider 做 stack-rank 的 top-N 栈：AspNetCore、Kestrel、EFCore、Antimalware-AMFilter、Sense（Defender for Endpoint）、`Microsoft-Windows-DxgKrnl`（GPU）、`Microsoft-Windows-Kernel-Power`（CPU 频率 / C-state），或任何自定义 EventSource。先用 `find_marker` 找出 trace 里有哪些 provider，然后把 `ProviderName` 喂给该工具。可选 `eventNameSubstring` 缩到具体事件类。栈质量取决于 `.wprp` 是否对该 provider 开了 stack-walk。 | Any Stacks（单 provider） |
| `generic_event_caller_callee` | 给定 focus frame 的钻取；metric 是事件计数。 | Any Stacks → Callers / Callees tab |

### 复合诊断

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `diagnose_window` | 针对一个 `startUs` / `endUs` 窗口、可选 PID 的证据 composite：返回按字节 / 最大延迟排序的 hard-fault 文件、File IO、内存压力、security-scan、wait、执行调用 provenance、not-concluded reason 和可选 zoom-in 工具。带 `maxWindowDurationUs` guard，且故意不输出 root-cause verdict。 | **[复合]**——封装 hard fault、File IO、memory、security scan、wait 视图 |
| `diagnose_high_wait` | 高阻塞时间排查的 preview composite。候选按进程生命周期分离，仅在 scoped CSwitch 栈覆盖率支持时补充栈证据，并把 ReadyThread 栈作为关联 wakeup 证据而非 wait 根因证明。它返回明确的 not-concluded reason，不输出 root-cause 字段。 | **[复合]**——把 wait、stack、ReadyThread 视图和证据 provenance 打包到一次调用 |
| `diagnose_slow_startup` | 挑出 wait_ratio 最高的进程（或匹配 `nameSubstring` 的进程），对每个启动窗口运行 wait、image-load、CPU 分析；当 `ProcessStart -> first ImageLoad` gap 达到 `slowFirstImageLoadThresholdUs` 时，还会为该精确 pre-user-mode gap 附加来自 `diagnose_window` 的 `FirstImageLoadGapEvidence`。 | **[复合]**——封装启动 wait、loader、CPU 与 window evidence |

### Symbols（符号）

| 工具 | 功能 | PerfView 对应 |
|---|---|---|
| `prepare_symbols` | 对已加载 TraceId 的完整 trace-native PDB identity 集合检查 startup-approved 本地 candidate root，精确验证 name/GUID/age，把匹配 artifact pin 到 private store，并返回 immutable SymbolContextId。无网络访问，也不宣称 frame resolution；当前 context-bound frame resolver 不可用。 | **[程序化]**——显式 local symbol preparation lifecycle |

---

## 配置

### 结果契约与 trace reference profile

结果契约和 trace-reference 策略都在 stdio transport 读取请求之前一次性选择；
任何 tool call 都不能切换模式。MCP 客户端可从 `wpa://runtime/profile` 读取本次
启动的真实模式、弃用告警和 release blocker。

```json
{
  "env": {
    "WPAMCP_CONTRACT_MODE": "2.0",
    "WPAMCP_TRACE_REFERENCE_MODE": "id_only"
  }
}
```

等价 CLI 参数是 `--contract-mode 2.0` 与
`--trace-reference-mode id_only`；CLI 覆盖环境变量。contract 值严格限定为
`legacy` / `2.0`，trace reference 值为 `compatibility` / `id_only`。

当前源码版本仍是 `0.3.0`：可运行的开发 profile 是 Contract 2.0 + ID-only，
但 ADR 0005 明确把 0.4 之前的 release line 标成 `releaseStatus=blocked`。
`legacy` 值已进入显式矩阵，但仓库尚无经过审查的 legacy result adapter，因此
选择它会在启动时 fail closed，不能把 active Contract 2.0 envelope 冒充 legacy。
raw-path compatibility 只能通过显式启动开关启用，并会给出 1.0.0 删除告警。
固定 profile 前请阅读[契约迁移](docs/CONTRACT_MIGRATION.zh-CN.md)与
[客户端兼容性](docs/CLIENT_COMPATIBILITY.zh-CN.md)。

诊断时可用 `--runtime-profile` 输出默认 profile JSON；当 ADR rollout gate 尚未
满足时，`--validate-release-profile` 返回退出码 78。两者都不会启动 MCP 或读取 stdin。

### Trace 缓存

LRU 默认保留 2 个 materialized trace generation，可用 `WPAMCP_CACHE_SIZE=N` 覆盖。
`load_trace` 从允许的 source handle 取快照，只在 owned artifact store 中 materialize；
secure profile 的 query 只接受 TraceId，不会创建 caller-owned adjacent `.etlx`。query 在
完整使用期间持有 generation lease；eviction、unload、shutdown 只 retire handle，最后
一个 lease drain 后才释放 backend。同一 generation 的构造与 trace-facts extraction
都是 single-flight。

重复加载同一 observed generation 返回同一个 canonical handle。若故意原位改写且保留
了可观察 identity、length、timestamp，可用 `forceRefresh=true`。`unload_trace` 只 retire
public handle，不会立即删除 immutable artifact。未被 pin 的 trace artifact 默认在最后一次
store 访问 7 天后过期；可在启动时用 `WPAMCP_TRACE_ARTIFACT_RETENTION_MINUTES` 或
`--trace-artifact-retention-minutes` 配置为 1 分钟至 365 天。live handle 会 pin 对象，
因此 TTL 不会使 active generation 失效；最后一个 pin drain 后才执行过期清理，generation
cache 也不能静默复活已过期 artifact。retained-store quota 和 materialization checkpoint
已执行，但 opaque converter 的瞬态物理磁盘峰值仍是显式 release blocker，不能冒充
hard bound。

### 自己抓 trace

[`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)（英文）提供了一个推荐 `.wprp`，覆盖 CPU + CSwitch + FileIO + DiskIO + HardFaults + Loader stacks。最常用的抓取流程：

```powershell
wpr.exe -start tests\WpaMcp.Tests\fixtures\MmapCapture.wprp -filemode
# … 复现慢的场景 …
wpr.exe -stop C:\path\to\my_capture.etl
```

### Symbol configuration

> **必须分开 readiness 与 resolution。** 四种状态是 trace PDB identity（name + GUID
> + age）、本地 candidate、verified local readiness 和 observed frame-name
> resolution。`prepare_symbols` 可以建立 readiness，并故意把 resolution 报成
> unmeasured。当前 context-bound TraceEvent frame resolver 不可用，任何 active query
> 都不得把 readiness 升级成 measured resolution；null rate 不是 0%。

#### 启动策略

secure policy 仅允许本地符号。它不读取 `_NT_SYMBOL_PATH`，不检查 trace 目录，不搜索
任意路径，不访问 symbol server，也不允许 tool call 临时传 root。启动前配置一个或多个
absolute local candidate root，以及与它们互不包含的 private verified store：

```powershell
wpa-mcp.exe --symbol-local-root "C:\Symbols" `
  --symbol-store-root "$env:LOCALAPPDATA\WpaMcp\symbol-store"
```

等价环境变量为 `WPAMCP_SYMBOL_LOCAL_ROOTS`（Windows 上用分号分隔）和
`WPAMCP_SYMBOL_STORE_ROOT`。启用 candidate root 时必须配置 store root；UNC、device、
alternate-stream 路径会被拒绝，candidate root 与 store 不能包含彼此。installer 默认
配置 `%LocalAppData%\WpaMcp\symbol-candidates` 与
`%LocalAppData%\WpaMcp\symbol-store`。

PDB 可放在 `<root>\<pdbName>`，或 symbol-store 形状的
`<root>\<pdbName>\<GUIDAge>\<pdbName>`。公开/私有 PDB 需在 wpa-mcp 之外获取，再复制
到 approved root；MCP server 自身不做远端抓取。每个 candidate 都必须被实际打开，
并与 trace 中完整 PDB name/GUID/age 精确匹配，之后才会复制并 pin 到 private verified
store。

#### 自家 DLL 的构建前置条件

approved local root 配得再对，构建本身没产 PDB——或者 PDB 跟最终部署的 DLL 不是同一次构建——也无济于事。

- **.NET / C#**：`<DebugType>portable</DebugType>` + `<DebugSymbols>true</DebugSymbols>`。检查 Release 配置没把 PDB 输出关掉。
- **C++（MSVC）**：`/Zi` + `/DEBUG:FULL`，Release 也要开。PDB 跟 DLL 留同目录。
- PDB 和 DLL 必须共享同一个签名（GUID + age）——重新 link 就生成新签名，老 PDB 不再认新 DLL。

#### 验证是否生效

```
> 加载 C:\my\trace.etl，并保留返回的 TraceId。
> 对该 TraceId 调 prepare_symbols，并保留返回的 SymbolContextId。
> 检查 prepare_symbols 的 readiness，但不要把它解释成 function 已解析。
```

SymbolContextId 绑定 principal、trace generation、policy、resolver、privacy/contract
profile、module identity 与 verified artifact。目标 lookup contract 要求 stack query
同时显式给出它和 `resolveSymbols=true`，不存在 ambient fallback；但当前 build 会用
`symbol_resolution_unavailable` / `context_bound_frame_resolution_unavailable` fail
closed，因为 context-bound TraceEvent adapter 尚未实现。因此
`symbols.frame_resolution.measured` 仍是 declared gap；unsymbolized result 与 preparation
metadata 不能冒充实测 resolution rate。

架构与兼容边界见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)、
[`docs/CONTRACT_MIGRATION.zh-CN.md`](docs/CONTRACT_MIGRATION.zh-CN.md) 和
[`docs/CLIENT_COMPATIBILITY.zh-CN.md`](docs/CLIENT_COMPATIBILITY.zh-CN.md)。
