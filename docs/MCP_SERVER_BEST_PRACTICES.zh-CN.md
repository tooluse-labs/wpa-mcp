# MCP Server 开发最佳实践

本文整理面向生产级 MCP Server 的开发实践，并针对 `wpa-mcp` 这类分析型 Server 补充正确性契约。目标不仅是让工具“可以调用”，还要确保模型能够正确选择工具、不会误读结果，并能准确判断调用的权限和副作用。

## 1. 把工具描述视为公开契约

每个工具的名称、描述、输入 Schema 和输出 Schema 共同构成面向客户端与 LLM 的公开契约。描述应明确说明：

- 分析对象是整条 trace、进程实例、线程实例，还是时间窗口。
- 时间、长度、字节等参数的单位，以及窗口边界，例如半开区间 `[startUs, endUs)`。
- 字符串参数采用精确匹配、大小写敏感匹配、正则还是模糊匹配。
- 返回内容属于直接事实、统计相关性、启发式判断还是因果证据。
- 调用可能产生的文件写入、网络访问、缓存更新和进程环境修改。
- 缺少必要证据时，工具会以何种状态降级。

工具描述应避免超出证据能力的措辞。例如，与唤醒事件关联的调用栈不能自动证明“谁造成了等待”，启发式识别出的扫描事件也不能直接表述为“确认是杀毒软件”。

## 2. 优先使用结构化输出

工具应声明 `outputSchema`，并保证返回的 `structuredContent` 与 Schema 一致。结构化输出比自由文本更适合模型稳定消费，也便于客户端验证、版本迁移和自动化测试。

分析型工具建议采用统一响应骨架：

```json
{
  "scope": {
    "pid": 4024,
    "processStartUs": 123456,
    "tid": 812,
    "threadStartUs": 124000
  },
  "scopeStatus": "ok",
  "capabilityStatus": "partial_stacks",
  "traceEventCount": 10000,
  "matchedEventCount": 120,
  "stackedEventCount": 80,
  "stackCoveragePct": 66.67,
  "noDataReason": null,
  "rows": [],
  "warnings": []
}
```

字段应遵守以下约定：

- `Trace...` 始终表示整条 trace 的统计。
- `Scoped...` 和 `Matched...` 始终表示已解析的请求范围。
- 分子、分母、计数范围和时间窗口必须一致。
- 空数组不能单独表达能力缺失、范围错误或没有匹配事件。
- 地址、FileKey 和其他可能超过 JavaScript 安全整数范围的标识符，应序列化为十六进制字符串或普通字符串。
- 新增字段时优先保持向后兼容；删除、改名或改变既有字段含义时进行破坏性版本升级。

## 3. 建立统一的空结果语义

工具成功执行但没有数据时，应返回结构化领域状态，而不是只返回 `[]`，也不应统一抛出服务器异常。建议至少区分：

| 状态或原因 | 含义 |
| --- | --- |
| `invalid_scope` | 请求范围或窗口不合法 |
| `pid_not_found` | trace 中没有目标 PID |
| `ambiguous_process_instance` | PID 对应多个生命周期，缺少实例选择器 |
| `event_class_not_observed` | 整条 trace 中未观察到目标事件类 |
| `no_events_in_scope` | 事件类存在，但请求范围内没有匹配事件 |
| `stacks_unavailable` | 范围内有事件，但目标事件没有可用堆栈 |
| `symbols_unresolved` | 有堆栈，但函数名解析质量不足 |
| `focus_not_found` | 已实际扫描可用样本，但没有匹配焦点帧 |
| `not_concluded` | 证据不足，不能形成结论 |

参数验证失败、文件不可读和内部执行失败属于工具或协议错误；“未观察到事件”“范围内没有事件”等属于成功调用后的分析结果。

## 4. 使用进程和线程实例身份

Windows trace 中 PID 和 TID 都可能复用。进程级分析不应只使用 PID，线程级分析也不应只使用 PID 与 TID。

推荐的稳定身份为：

```text
ProcessInstanceKey = (Pid, ProcessStartUs)
ThreadInstanceKey  = (Pid, ProcessStartUs, Tid, ThreadStartUs)
```

当调用者只提供 PID 时：

- 只有一个进程实例时可以自动选择。
- 存在多个实例时默认返回 `ambiguous_process_instance` 和可重放候选项。
- 如果工具支持跨生命周期聚合，应显式返回 `scopeMode=pid_aggregate` 和 PID 复用警告。
- 所有进程级工具应采用相同的实例解析规则，避免不同工具对同一选择器给出不同范围。

## 5. 按目标事件域报告能力

全局 `HasStackWalks=true` 只能说明 trace 中存在某些堆栈，不能推出 FileIO、DiskIO、HardFault、CLR 或其他目标事件拥有堆栈。

每个事件域应独立报告：

- `TraceEventCount`
- `MatchedEventCount`
- `StackedEventCount`
- `StackCoveragePct`
- `StackStatus`
- `StackSemantics`

只有目标事件域确实存在可用堆栈时，工作流推荐器才应建议调用对应的 stack 或 caller/callee 工具。零 metric 事件仍然是已观察事件；事件存在性与其累计 metric 是否为零不能混为一谈。

## 6. 区分符号身份、候选文件和实际解析

符号质量至少应拆分成以下层次：

| 层次 | 含义 |
| --- | --- |
| `PdbIdentityAvailable` | trace 中有完整 PDB 名称、GUID 和 Age |
| `LocalCandidateFound` | 本地路径中存在候选文件 |
| `LocalPdbReady` | 文件可读、格式有效且 identity 匹配 |
| `FrameResolutionRate` | 实际执行 frame lookup 后成功解析的比例 |

不能因为 `PdbName` 非空、文件位于预期目录或带有 PDB 扩展名，就声明符号已经解析。缺少完整 identity 时，应建议重新捕获或合并必要元数据，而不是推荐无法执行 identity 查询的符号服务器。

`candidate_identity_unverified`、`ready` 和 `resolved` 必须保持不同语义；只有真正执行过 stack frame lookup 的工具才能报告实际帧解析率。

## 7. 如实设置 MCP 风险标注

MCP 工具可声明以下行为提示：

- `readOnlyHint`
- `destructiveHint`
- `idempotentHint`
- `openWorldHint`

这些标注是客户端的风险提示，不是安全强制机制。Server 必须按最坏的有效执行路径标注工具：

- 如果首次调用可能运行 `OpenOrConvert` 并创建或刷新 ETLX，不能声明 `readOnlyHint=true`。
- 如果 `resolveSymbols=true` 可能写入符号缓存或下载 PDB，应声明 `readOnlyHint=false` 和 `openWorldHint=true`。
- 如果一个工具有时完全本地、有时访问网络，优先拆成两个边界清晰的工具；无法拆分时采用保守标注。
- 修改环境变量、缓存驱逐和显式 unload 都属于需要披露的状态变化。
- 只有重复调用相同参数不会产生额外语义副作用时，才声明 `idempotentHint=true`。

客户端仍应把不受信任 Server 提供的标注视为不可信信息，并通过授权、沙箱和网络策略实施真正的安全边界。

## 8. 用代码强制安全边界

工具描述和 annotations 不能代替确定性安全控制。Server 应至少落实：

- 对 trace、PDB 和缓存路径进行规范化，并限制在明确允许的根目录内。
- 防止 `..`、NTFS ADS、UNC、设备路径、符号链接和 junction 逃逸。
- 对输入文件大小、执行时间、内存、并发数、事件遍历数、堆栈遍历数和响应大小设置上限。
- 对远程部署使用 OAuth 2.1、HTTPS、最小权限和逐工具或逐能力 scope。
- 不记录 Token、Authorization header、授权码、完整敏感路径或原始 PII。
- 远程符号下载限制允许的 origin、端口、重定向次数、目标网络和单文件大小。
- 防止把客户端 Token 直接转发给下游服务，并防范 SSRF、DNS rebinding 和开放重定向。
- 对长操作实现取消和超时；取消后不得留下半写入的共享产物。

## 9. 把缓存与并发视为正确性问题

缓存不仅影响性能，也会影响分析对象是否正确。推荐实践包括：

- 同一 trace 的并发加载使用 single-flight，避免重复转换和 sidecar 竞争。
- Windows 路径 cache key 使用不区分大小写的比较器。
- 文件身份至少包含规范路径、长度、mtime 和平台可用的稳定文件标识。
- 同路径文件被替换时使旧 cache entry 失效。
- eviction 或 dispose 中一个 callback 抛异常时，继续释放其余条目，最后聚合报告异常。
- active lease 在 eviction 时仍保持有效，最后一个 lease 释放后再销毁底层对象。
- 提供显式 `unload` 或 `invalidate`，供符号状态变化和疑似文件替换时使用。
- 明确残余边界：同一文件被原位修改，同时保持相同长度、mtime 和文件标识时，廉价 stamp 可能无法检测。

## 10. 提供可观测性但避免泄露

每次调用建议记录：

- request 或 correlation ID
- 工具名及脱敏后的参数摘要
- 总耗时和主要阶段耗时
- cache hit/miss
- 扫描事件数和匹配事件数
- 输出行数与响应大小
- 错误类别和取消状态
- 是否执行符号查询或外部访问

对于 stdio Server，stdout 只能承载 MCP JSON-RPC；日志必须写入 stderr，否则会破坏协议流。远程传输可结合 MCP logging notification 和服务端结构化日志。

## 11. 控制工具面和响应规模

过多且语义重叠的工具会增加模型选择错误。建议：

- 一个工具只承担一个稳定、可描述的职责。
- 使用 `inspect` 或 capability 工具引导后续工具选择。
- 将常用工作流组合成明确的诊断工具，但保留底层证据和调用 provenance。
- 对大结果使用 `topN`、分页或 continuation token。
- 排序规则、截断规则和总匹配数必须随结果返回。
- 不让 `topN` 改变统计分母或是否找到焦点帧等语义。
- 对预计耗时较长的调用报告进度；采用实验性 MCP Tasks 时，要绑定授权上下文并设置不可猜测的 task ID 与合理 TTL。

## 12. 对抗性测试优先于正常路径测试

除了常规功能测试，还应覆盖模型最容易被误导的边界：

- PID/TID 复用。
- Start、Stop、DCStart、DCStop 逆序和同时间戳。
- 事件类只存在于窗口外或其他进程。
- 范围内有事件但没有堆栈。
- 部分堆栈覆盖和低符号解析率。
- metric 为零但事件实际存在。
- 配对事件跨越查询窗口。
- 孤立 Start、Stop、Connect、Disconnect 或 allocation/free endpoint。
- PDB identity 缺失、文件为空、格式损坏和 identity 不匹配。
- trace 文件替换后恢复原 mtime。
- cache eviction、callback 和 dispose 抛异常。
- 64 位整数序列化精度。
- `tools/list` 中的 description、annotations、inputSchema 和 outputSchema 与代码行为一致。
- MCP Inspector、目标客户端和实际大型 trace 的端到端验证。

测试不能仅通过放宽断言或修改 warning 变绿。每个正确性问题应先有可观察的失败反例，再实施最小修复，并验证输出范围、计数分母和状态语义。

## 13. 版本与兼容策略

输出 Schema 是公开 API。版本策略应满足：

- 新增可选字段通常可以作为兼容变更。
- 删除字段、字段改名、改变单位、改变空值含义或改变聚合范围属于破坏性变更。
- 保留旧字段时，将其明确标记为兼容字段，并提供准确描述和迁移字段。
- 发布版本不得复用已经打 tag 或发布的版本号。
- Changelog 应记录 Schema 变化、annotation 变化、副作用变化和迁移方式。
- 使用序列化契约测试或 Schema snapshot 防止意外漂移。

## 14. `wpa-mcp` 的核心真实性原则

对分析型 MCP Server，最重要的原则可以概括为：

> MCP 输出不应只给出分析数值，还必须同时给出该数值的实例范围、能力前提、证据覆盖率，以及当前证据不能支持什么结论。

这要求每个分析结果都能回答：

1. 分析的是哪个进程和线程实例？
2. 使用的是整条 trace 还是选定窗口？
3. 目标事件类是否在 trace 中被观察到？
4. 请求范围内匹配了多少事件？
5. 有多少匹配事件带目标语义的堆栈？
6. 符号状态是 identity、候选文件、ready，还是实际 resolved？
7. 结论属于事实、相关性、启发式还是因果判断？
8. 空结果是能力缺失、范围为空、选择器错误，还是确实没有现象？
9. 调用是否写入文件、修改缓存、访问网络或改变进程状态？

只要这些问题能由结构化结果直接回答，LLM 就不容易被“数值看起来合理、实际范围或证据错误”的结果误导。

## 参考资料

- [MCP Tools 规范](https://modelcontextprotocol.io/specification/draft/server/tools)
- [MCP Tool Annotations 风险说明](https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/)
- [MCP Security Best Practices](https://modelcontextprotocol.io/docs/tutorials/security/security_best_practices)
- [Understanding Authorization in MCP](https://modelcontextprotocol.io/docs/tutorials/security/authorization)
- [MCP Debugging 指南](https://modelcontextprotocol.io/docs/tools/debugging)
- [MCP Tasks 规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/tasks)
