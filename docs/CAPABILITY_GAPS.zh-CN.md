# wpa-mcp — 能力缺口对比（vs WPA / PerfView）

> Working notes，不是 RFC。盘点 WPA / PerfView 暴露、但 wpa-mcp 尚未覆盖的分析能力，以及那些专属于 GUI 工具、对 LLM 消费者没必要补的能力。
>
> **文档集合的逻辑分工**——三份现行文档，顺序约束的流水线：
>
> - **`CAPABILITY_GAPS.md`（本文）** ——**补什么**（稳定的清单）
> - **`MCP_SURFACE_DESIGN.md`** ——**怎么补**（Tool / Resource / Prompt、三层架构、annotation 分级）
> - **`MCP_IMPLEMENTATION_TASKS.md`** ——**优先级 + 具体任务**（P0–P3、Scope / Work / Acceptance）
>
> 本文识别的缺口在 `MCP_SURFACE_DESIGN.md` 完成表面分类、`MCP_IMPLEMENTATION_TASKS.md` 完成优先级排序之前**不可执行**。
>
> 早期 brainstorm 文档已归档到 `docs/archive/` 以供溯源。
>
> **修订说明：**
> - **v5 (2026-05-15)**：删除 4 层 punchlist；优先级现在只在 `MCP_IMPLEMENTATION_TASKS.md` 里。本文档只保留稳定的能力清单（A/B/C/D + 不该补 + UI vs data 误判 + 反模式 callout）。文档集合清理同时归档了 `OPTIMIZATION.md`。
> - **v4 (2026-05-15)**：把 punchlist 改写为 4 层优先级结构；明文加入反模式警告；重新定位文档集合为顺序约束；A-4 与 B-5 加风险注脚。
> - **v3 (2026-05-15)**：code review 后收紧多处事实陈述；新增 A 表三个缺口。
> - **v2 (2026-05-15)**：经 `grep` 核实修正 v1 的 time-window 过度描述；"采集侧"拆成"执行（不补）"vs"质量诊断（in-scope）"；新增 D. Trace lifecycle / preprocessing 小节。
> - **v1 (2026-05-15)**：初稿盘点。

## 分类框架

把 WPA / PerfView 能力剥掉 UI 壳子后，剩下大体四类：

- **A. 数据维度**——看的是什么
- **B. 切片 / 聚合**——怎么切
- **C. 元数据**——trace 自身的信息
- **D. Trace lifecycle / 预处理**——分析前后对 trace 文件本身的操作

---

## A. 数据维度缺口

| 缺口 | 价值 | 备注 |
|---|---|---|
| **CPU Usage Precise**（基于 CSwitch 的 on-CPU µs，区别于 Sampled 的统计采样） | 高 | 现 `cpu_*` 是 Sampled 视角；`wait_*` 是反面（off-CPU），都回答不了"thread 实际在 CPU 上跑了多少 µs"。补上 Precise 后凑齐 Sampled CPU / Precise on-CPU / Wait off-CPU 的 thread-time 三角。**注意**：这只是 WPA Precise 视图的一半——另一半见下一行 Scheduler 维度 |
| **Scheduler / core / priority 分析**（per-core 归属、CPU migration、priority inversion、ready latency、quantum end） | 中高 | WPA CPU Usage (Precise) 视图的更深一层。回答"跑在哪个 core？"、"是否在 core 之间反复迁移？"、"是否被 priority inversion 卡住？"、"ready 到 run 的延迟分布？"。与 `ready_thread_*` 不同——后者是唤醒事件本身，不是调度延迟分布 |
| **GC heap dump (`.gcdump`) 加载 + 对象引用图 + retention path** | 高 | 内存泄漏调研必备。现 `clr_gc_*` 只在 ETW 事件层，回答不了"谁还持有这堆 `byte[]`" |
| **内存资源视图**（working set、commit、private bytes、paged / non-paged pool、handle count） | 高 | 系统内存视角，**和分配事件流不重叠**：`clr_gc_*` 是托管分配、`heap_alloc_*` 是 NT heap 事件、`virtual_alloc_*` 是地址空间预留事件。这些都回答不了"现在实际驻留多少？"、"paged pool 是否耗尽？"、"handle 是否在泄漏？"。WPA 有专门视图，wpa-mcp 完全没覆盖。<br/>**风险注脚 (v4)**：需要先验证现有 wpr profile 是否实际采集 working-set / commit / pool / handle 计数器。如果需要新增 wpr keyword，本条降一档优先级——analyzer 无法恢复从未被记录的事件 |
| **Async / Task chain stitching**（CLR Task 跨线程续帧重组） | 高 | `clr_*` 工具按事件分散返回，async 调用链拼不起来；PerfView 有 task tracing 做这件事 |
| **UI 响应性 / 输入延迟 / frame pacing**（input-to-render 延迟、DWM frame pacing、compositor 卡顿） | 中高 | 用户感知性能的维度。下文的 `Window-in-focus` 只回答"哪个窗口在前台"，不回答"从按键到出画面多久"。桌面 / UI 应用调研刚需 |
| **GPU profiling**（Compute Graphics / GPU Work） | 中 | 场景窄但 GPU-bound 场景无替代 |
| **Power profiling**（CPU C-state、frequency、battery transitions） | 中 | 笔记本、移动场景刚需 |
| **Window-in-focus / 前台进程事件** | 中 | 把性能事件挂到"用户当时在看哪个窗口"上 |
| **Boot trace phase 分析**（PreSession / SMSSInit / WinLogonInit 等） | 中 | wpa-mcp 现把 boot trace 当 normal trace 看，丢失分阶段视角 |
| **Audio glitches** | 低 | 媒体场景专用 |

## B. 切片 / 聚合缺口

| 缺口 | 价值 | 备注 |
|---|---|---|
| **Time-window filtering——统一 + 共享 ROI context + correctness tests** | 中高 | **v1 现状描述纠错。** `startUs` / `endUs` 参数**已经**存在于大多数 stack-shaped 工具上——`grep -l "startUs\|endUs"` 在 `src/` 命中 40+ 文件。**真正缺口**：(a) 覆盖不齐；(b) 调用之间不能共享 ROI context；(c) clip 边界没有系统性 correctness tests。优先级是 **统一 + 共享 context + 加测试**，不是"从零加 startUs/endUs" |
| **跨 trace diff**（baseline vs regression） | 高 | 瓶颈是跨 capture 的 process-identity 匹配 |
| **灵活 group-by / pivot**（按 module / namespace / 线程池 / payload 字段重聚合） | 中高 | WPA 拖列即可，PerfView 有 GroupPats；wpa-mcp 输出 schema 固定 |
| **Aggregation mode 切换**（sum / avg / max / min / count / weighted） | 中 | 多数列锁死 sum 或 count |
| **Generic event 字段 group_by**（受限聚合 DSL，按 task / opcode / event_id / payload 字段切） | 中高 | **v1 现状描述纠错。** `generic_event_top_stacks` **已经**支持按 provider、event-name substring、pid、time window 过滤，还有 `whenBuckets` 时间直方图选项。**真正缺**的是按 event task、opcode、event_id 或 payload 字段做 group / pivot。<br/>**风险-价值注脚 (v4)**：工程风险其实低（在现有工具上扩参数，不是新工具）。不确定的是**价值**——LLM 是否真能驱动受限 pivot DSL。建议先发布最小 2-axis 版本（task + opcode），用 1–2 个真实场景验证后再扩展 |
| **Storage stack 细分**（LayoutComplete / FlushComplete / Volume Flush 分层延迟） | 中 | 现 `disk_io_*` 是合并后的粗粒度 |
| **多 stack-type 混合 CallTree**（CPU + Wait fold 在一棵树里） | 中 | PerfView 强项；wpa-mcp 必须分开查再人脑合并 |

## C. 元数据缺口

| 缺口 | 价值 | 备注 |
|---|---|---|
| **System Configuration**（OS build、CPU model / 拓扑、core count、boot config、driver 列表——SystemConfig 事件承载） | 高 | `load_trace` 只给 `Capabilities`，没给 trace 来源的硬件 / OS 上下文。ETW 通过 SystemConfig 事件携带这些信息，analyzer 取出来即可。AC / battery 状态和 frequency transitions 来自 Kernel-Power provider 的动态事件，归 A 表 "Power profiling"，不在这一行 |
| **Per-provider trace statistics**（per-provider 事件数、buffer 配置、丢事件信号） | 中高 | 基础 `EventsLost` 已经通过 `TraceMeta.EventsLost` 暴露，但 per-provider 拆分还没有。ETW buffer loss 本质上是 session 级，严格的 per-provider 丢事件拆分可能无法重建。能做的是 per-provider 事件数 |
| **采集质量诊断**（profile 推荐、缺 keyword 指导、stackwalk 完整性、符号解析率） | 中高 | "这条 trace 该信几分？"的另一半。基于现有 `Capabilities` + `SymbolStatus` 反推，告诉 LLM 该让用户怎么重采。**纯分析端能力——不涉及启停 ETW session** |
| **Symbol source lookup**（返回 `file:line`） | 中 | LLM 拿到行号可对照源码 |
| **Inline frame expansion** | 中 | inline 函数现被外层吞掉，深度 drilldown 会失真 |

---

## D. Trace lifecycle / 预处理

不是核心分析，但分析端合理地拥有 trace 文件预处理这条线——`tools/etlshrink/` 已经作为独立项目存在，先例已立。

| 缺口 | 价值 | 备注 |
|---|---|---|
| **`shrink_trace(path, pid_list)`** | 中高 | 把 `tools/etlshrink/` wrap 成 MCP 工具 |
| **`slice_trace(path, startUs, endUs)`** | 中 | 按时间窗 subset 导出——和 B-1 的 time-window 统一工作天然成对 |
| **`redact_trace(path, ...)`** | 低中 | 去除特定 process / image / payload 字段 |
| **Folded-stack / call-tree artifact 导出**（`*_top_stacks` 加 `format=folded` 模式） | 低中 | 不是 UI flamegraph；外部工具的导出格式。现有工具的可选模式 |
| **多 trace merge / stitching** | 低 | 偏采集端 |

---

## 明确不该补的能力

### 纯 UI 渲染（对 LLM 零价值）

- Flamegraph / Icicle / Heatmap / 时间轴图 / Bar chart **图像本身**
- 颜色 / 主题 / 字体 / 布局
- 截图、图片导出
- HTML report 生成
- 多视图 tab / 窗口管理
- UI pagination 语义（API 仍然需要 `top` 上限 / cursor / 截断元数据——这是 API 设计问题，不是 UI 移植）

### 配置 / 状态持久化（弱必要）

- `.wpaProfile` / `.perfView` 视图配置文件——tool call 是 request / JSON 导向
- 用户偏好 / 历史会话恢复

### 采集执行（不在 scope）

需要 admin 权限的写接口：

- 启停 ETW session
- 实时 provider enable / disable
- Heap snapshot 触发
- `wpr -start` 等价物

`wpr` / PerfView 拥有这些职责。采集*质量诊断*（C 表）是独立的 in-scope 能力。

---

## 常见误判：UI 形式 vs 数据能力

1. **Time-range selection** 是 **time-window filtering** 的交互形式。已经在大多数工具上以 `startUs` / `endUs` 参数暴露（B-1）
2. **Cross-view linking** 是"多个 analyzer 共享同一时间窗 / 进程过滤上下文"——见 B-1 中"共享 ROI context"那半
3. **Drill-down** 是"上一查询的结果能作为下一查询的输入"。wpa-mcp 现让 LLM 手动重传 `module!func` 字符串，是 drill-down 的弱化版本

---

## 优先级

**⚠️ 反模式，不要做**：按本文档从上到下的顺序补能力。参照工具（WPA / PerfView）按采集 / 域排列功能，不是按 LLM 价值排列。**应按 `MCP_IMPLEMENTATION_TASKS.md` 的优先级结构排序**，而不是顺着本文档读。

本文档描述**相对 WPA / PerfView 缺什么**——这是一个慢变化的清单。**怎么排序工作** 由 `MCP_IMPLEMENTATION_TASKS.md` 承担（P0 导航基础 → P1 路由与 composite → P2 低风险高价值 → P3 高价值高风险），它可以每个 sprint 演进而不动本文档。

---

最后修订：2026-05-15 (v5)。
