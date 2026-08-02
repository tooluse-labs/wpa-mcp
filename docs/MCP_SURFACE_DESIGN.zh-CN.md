# wpa-mcp — MCP 工具表面设计（review draft）

> **历史快照（2026-05；2026-08-01 对齐说明）：** 正文不是当前实现证据。validated development surface 现有 60 个 active tools、51 个 declared capabilities、15 个 goals、15 个 workflows；`list_capabilities` 与可分页 capability/tool/workflow、per-section contract Resources 已实现，所有 active tools 均输出 Contract 2.0 envelope。正文中的旧 `diagnose_symbols`、`set_symbol_path`、`add_symbol_server` 已不在 active surface，由显式本地 `prepare_symbols` lifecycle 取代。当前架构/状态以 [`MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md`](MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md)、`ARCHITECTURE.md` 和 ADR 0002–0005 为准。正文只保留“不删低频工具、不做 mega-tool、不按 trace 动态过滤 `tools/list`”等历史设计动机。

> Working notes，不是 RFC。讨论如何组织当前很宽且仍在增长的工具面，让 LLM 消费者不被 decision fatigue 淹没，同时不丢分析能力。
>
> **2026-05 当时的文档集合分工**——三份历史规划文档，顺序约束的流水线：
>
> - **`CAPABILITY_GAPS.md`** ——**补什么**（清单）
> - **`MCP_SURFACE_DESIGN.md`（本文）** ——**怎么补**（Tool / Resource / Prompt、三层架构、annotation 分级）
> - **`MCP_IMPLEMENTATION_TASKS.md`** ——**优先级 + 具体任务**（P0–P3、Scope / Work / Acceptance）
>
> 早期 brainstorm 文档已归档到 `docs/archive/` 以供溯源。

## 为什么需要这份文档

- wpa-mcp 当前已经有很宽的 MCP 工具面，`CAPABILITY_GAPS.md` 还识别了更多高价值补充——即使优先扩参数而非加新工具，工具面仍会继续增长。
- 当前工具面里大部分是同样的"top-N + stacks + caller-callee"三件套按域复制。
- 这个密度不是致命的——宽工具面的 MCP server 很常见——但对 LLM 开始通过三条通道造成困扰：
  1. **Token 成本**——`tools/list` 在每个 session 前缀里都被加载
  2. **Decision fatigue**——相似工具（`*_top_stacks` 一族）抬高错选率
  3. **Schema 重复**——多数 stack 工具重复 `pid`、`top`、`startUs` / `endUs`，描述几乎一致
- MCP 协议有三个 primitive——**Tools、Resources、Prompts**——但 wpa-mcp 当前只用 Tools。另外两个是显而易见的杠杆，目前完全没用上。

## 不要做的事

| 反模式 | 为什么失败 |
|---|---|
| 把整个工具面合成一个 `analyze_trace(mode=...)` 万能入口 | Decision fatigue 从工具层迁移到参数层 |
| 删除低频底层工具来"简化" | 削弱专家路径。**频率 ≠ 价值** |
| 按已加载 trace 动态过滤 `tools/list` | 破坏 prompt prefix caching；客户端兼容性参差 |
| 为每个新能力加"top + stacks + caller-callee"三件套 | 工具数单调爆炸 |
| 把关键路径能力放进 Resources 或 Prompts | 这两个 primitive 的客户端覆盖弱于 Tools，且不能被模型自主调用 |
| 按 WPA / PerfView 功能清单顺序逐项补能力 | 参照工具按采集 / 域排列功能，不是按 LLM 价值排列。**应按 `MCP_IMPLEMENTATION_TASKS.md` 排序** |

---

## 三层架构

### Layer 1 — 底层工具（当前工具面，基本保留）

直接、细粒度的访问入口，给专家调用者和 Layer 3 composites 当 building block。**保持结构稳定**：

- 客户端配置里 hardcode 的工具名不会断
- 跨 session 的 prompt prefix caching 仍能命中
- Layer 3 composite 能把它们当积木拼装

**演进规则：**
1. 优先扩参数，不优先加工具
2. 新域只在数据形态确实匹配时才上三件套
3. 按使用数据 deprecate，不靠拍脑袋

### Layer 2 — 导航工具（高优先级，尚未实现）

| 工具 | 返回 | 备注 |
|---|---|---|
| `inspect_trace(path)` | capabilities + system metadata + provider counts + stackwalk completeness + symbol quality + 缺 keyword 指导 + 推荐 workflow（原始信号） | 一次性 orientation。由 `MCP_IMPLEMENTATION_TASKS.md` 的 T0.3 建立，并由 T2.1 扩展 |
| `list_applicable_tools(path, goal?)` | 对当前 trace 过滤 + 排序后的工具列表 | 纯路由——**不**改 `tools/list`。依赖 `inspect_trace` 产出。任务 T1.1 |

**Layer 2 故意不包含** `suggest_next_steps(lastResult)`。基于 Layer 2 实际使用数据决策，不要预先做。

### Layer 3 — Composite / workflow 工具

`diagnose_slow_startup` 是已有先例。每个 composite 内部 orchestrate 3–5 个 Layer-1 调用，返回单一响应。**减少决策轮次，不减少工具数**。

| Composite | Layer-1 building blocks |
|---|---|
| `diagnose_high_wait` | `wait_analysis` + `ready_thread_top_stacks` + 在 top wait frame 上跑 caller-callee |
| `diagnose_gc_pressure` | `clr_gc_analysis` + `clr_gc_heap_stats` + `clr_alloc_top_stacks` |
| `diagnose_lock_contention` | `clr_contention_top_stacks` + 在 contended thread 上跑 `wait_analysis` |
| `diagnose_image_load_blocker` | `image_load_timing` + `image_load_top_gaps` + 在最大 gap 上跑 `wait_top_stacks` |
| `diagnose_trace_quality` | 读取 `inspect_trace` 的原始信号，返回带 reasoning 的 yes/no 结论 |

**Composite 是 Tool，不是 Prompt**。Prompt 是 user-invoked；agent-only 部署里 Prompts 永远不触发。

---

## Tools vs Resources vs Prompts——角色分工

| Primitive | 触发模型 | 适合 | wpa-mcp 用途 |
|---|---|---|---|
| **Tools** | Model-controlled，模型自主调用 | 带参数的动作 / 查询；运行时计算 | 当前底层工具 + Layer 2 导航 + Layer 3 composite |
| **Resources** | Client-driven | 稳定 / 半稳定的知识；参考文档；静态目录 | Capability matrix、tool catalog、per-trace 元数据快照 |
| **Prompts** | User-invoked | 可复用的 workflow 模板 | Slow startup、missing symbols、GC pressure、baseline regression |

### 客户端兼容性阶梯

| Primitive | 协议状态 | 客户端覆盖 | 实际意义 |
|---|---|---|---|
| Tools | 标准，必须支持 | ~ 通用 | **关键路径能力可以安全放在 Tools** |
| Resources | 标准 | 不一致——很多 agent runtime **不会**自动注入到上下文 | 适合参考内容；不适合"模型必须自动看到"的内容 |
| Prompts | 标准 | 通常需要显式用户调用；agent-only 一般永远不触发 | 适合 human-in-the-loop；**在纯 agent 部署里实际是 dead code** |
| `tools/list_changed` 通知 | 可选 | 多数第三方 client 忽略或激进缓存 | 不要依赖它做动态工具表面 |
| Tool annotations (`*Hint`) | 标准（2025-03） | 仅用于显示 | Hint，不是安全边界 |

### Critical-path rule

**任何 LLM 必须能自主调用的能力都是 Tool——绝不是 Resource 或 Prompt。**

Resources 和 Prompts 是**附加增强层**，不是 Tools 的替代。**测试**：问"如果某个客户端只支持 Tools，系统还能工作吗？"——对每个关键路径能力，答案必须是"能"。

### 计划中的 Resources

- `resource://wpa-mcp/capability-matrix`
- `resource://wpa-mcp/tool-catalog`
- `resource://wpa-mcp/workflows/{name}`

### 计划中的 Prompts

**Scope**：面向 human-in-the-loop 客户端。agent-only 部署里这些可能永远不被调用——**这是预期**。

- `slow_startup`
- `missing_symbols`
- `high_wait`
- `gc_pressure`
- `baseline_regression`

---

## Tool annotations——副作用 4 档分类

**不是"全部都 readOnly"。** wpa-mcp 工具按实际行为分 4 档：

| 档 | 工具 | `readOnlyHint` | `idempotentHint` | `openWorldHint` | 理由 |
|---|---|---|---|---|---|
| **A. 纯查询（首次访问后）** | `*_top_*` / `*_caller_callee` / `list_processes` / `find_marker` / `thread_lifetime` / `process_create_timing` / `diagnose_symbols`，以及类似查询工具 | `true` | `true` | **`true`** | Drill-down 可能触发 symbol fetch → 网络 + 写 `%LocalAppData%\WpaMcp\Symbols`。**`diagnose_symbols` 属于这一档**（v4 修正，经 `SymbolTools.cs:61-90` 核实）：它**不**修改 `_NT_SYMBOL_PATH`、**不**主动 fetch；只读取已加载 image 事件里的 `module.PdbName` |
| **B. 缓存生成** | `load_trace` | **`false`** | `true` | `false` | `TraceLog.OpenOrConvert` 在输入 `.etl` 旁生成 `.etlx`（TraceCache.cs:54） |
| **C. 环境配置** | `set_symbol_path`、`add_symbol_server` | **`false`** | `false` | **`true`** | 改进程级 `_NT_SYMBOL_PATH`。`openWorldHint=true` 因为它们改变了下游工具在符号解析时会探测的服务器集合 |
| **D. 未来的文件生成工具** | `shrink_trace`、`slice_trace`、`redact_trace`（如果实现） | `false` | `true` | `false` | 写新 `.etl` 文件 |

### Annotation 是显示 hint，不是安全边界

**server 端的强制执行才是真正的边界**。输入验证、文件路径安全、env 变量 scope、网络出口约束——所有这些都必须由 server 实现，无论 annotation 怎么声明。

### SDK 现状——已验证

`WpaMcp.csproj:13` 固定 `ModelContextProtocol 1.2.0`。T0.2 spike 已确认：P0 导航层工作不需要升级 SDK。

- `[McpServerTool]` 可直接声明 annotation 字段：`ReadOnly`、`Idempotent`、`OpenWorld`、`Destructive`。
- `McpServerToolCreateOptions` 提供同等的 programmatic registration 字段。
- `UseStructuredContent=true` 会启用 `CallToolResult.StructuredContent` 和 `Tool.OutputSchema`。
- 工具返回 `CallToolResult` 时，可用 `OutputSchemaType` 提供 schema。
- `ResourceLinkBlock` 是 `ContentBlock` 子类型，可出现在 tool result content 中。

详见 `MCP_SDK_SURFACE_SPIKE.md` 和 `McpSdkSurfaceTests`。

---

## 排序

详见 **`MCP_IMPLEMENTATION_TASKS.md`** 的优先级任务清单（P0–P3 + Scope / Work / Acceptance）以及推荐顺序。之前的 Week 1 / Week 2 / After-Week-2 日历估算已经被任务文档里基于依赖关系的顺序取代——日历估算容易错，依赖关系不会。

---

## 成功指标——上线前先定义

| 指标 | 目标 | 测量地点 |
|---|---|---|
| 工具错选率 | 可察觉的 ↓ | 跨典型场景的合成 agent benchmark |
| 单次调研的平均 tool 调用数 | 通过 composite ↓ | Session 追踪 |
| `tools/list` payload 大小 | 稳定（增长不超过今天的 2 倍） | Server 启动日志 |
| Prompt 调用率（human-in-the-loop） | >0 | Server 日志过滤到 Claude Desktop / 类似客户端 |
| Prompt 调用率（agent-only） | ≈0 **是预期，不是失败** | Server 日志过滤到 Claude Code / SDK agent |
| `inspect_trace` 采用率 | >50% 的 session 在前 3 次 tool 调用内触发 | Server 日志 |

没有测量，"这样改之后变好了"的每一句话都是 vibes。

---

最后修订：2026-05-15。

修订历史：
- **v8 (2026-05-15)**：T2.1 为 `inspect_trace` 增加 system metadata、provider event counts、driver summary、stackwalk completeness 后，同步更新 Layer-2 返回摘要
- **v7 (2026-05-15)**：用 T0.2 spike 结论替换 SDK readiness unknowns；记录 attributed-tool 对 annotations、structured output、output schema、resource link 的支持方式
- **v6 (2026-05-15)**：`MCP_IMPLEMENTATION_TASKS.md` 将 P0 按依赖顺序重编号后，同步更新任务引用（`T0.2` SDK surface spike，`T0.3` `inspect_trace`）
- **v5 (2026-05-15)**：删除 Implementation path 段（Week 1 / Week 2 / After-Week-2 日历 + 半年后重评估）；排序现在只在 `MCP_IMPLEMENTATION_TASKS.md` 里，以基于依赖关系的优先级形式存在。文档集合清理同时归档了 `OPTIMIZATION.md`，并从文档头去掉对它的引用
- **v4 (2026-05-15)**：把 `diagnose_symbols` 从 Tier C 改正到 Tier A；Tier C 现在只剩两个真正改 env 的工具
- **v3 (2026-05-15)**：把文档集合重新定位为顺序约束流水线；新增"按 WPA 功能清单逐项补"为显式反模式
- **v2 (2026-05-15)**：新增客户端兼容性阶梯；显式 Critical-path rule；澄清 composite 必须是 Tool；把 Prompts scope 限定为 human-in-the-loop；annotation 是显示 hint 不是安全边界
- **v1 (2026-05-15)**：初稿设计文档
