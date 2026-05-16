# MCP SDK Surface Spike

Date: 2026-05-15

Issue: #2, T0.2 in `MCP_IMPLEMENTATION_TASKS.md`

Package inspected: `ModelContextProtocol` / `ModelContextProtocol.Core` 1.2.0.

## Conclusion

No SDK upgrade is required for the P0 navigation work.

The current SDK can express the required MCP surface:

- Tool annotations are available in the protocol model through `ToolAnnotations` and on attributed tools through `[McpServerTool]` properties: `ReadOnly`, `Idempotent`, `OpenWorld`, and `Destructive`.
- The same annotation fields are available for programmatic registration through `McpServerToolCreateOptions`.
- Structured tool output is supported through `UseStructuredContent=true`.
- Attributed tools can advertise output schemas through `OutputSchemaType` when the method returns `CallToolResult`, or can infer the schema from the method return type for normal typed responses.
- Programmatic registration can provide an explicit `OutputSchema`.
- Tool results support both `CallToolResult.StructuredContent` and `CallToolResult.Content`.
- Resource links are represented by `ResourceLinkBlock`, a `ContentBlock` subtype, and can appear in tool result content.

## Implementation Pattern

Keep the existing attributed-tool registration path. `Program.cs` can continue to use `WithToolsFromAssembly()`.

For normal structured tools, prefer returning a strongly typed response record:

```csharp
[McpServerTool(
    ReadOnly = true,
    Idempotent = true,
    OpenWorld = false,
    Destructive = false,
    UseStructuredContent = true)]
public InspectTraceResponse InspectTrace(string path) { ... }
```

Use `CallToolResult` only when the implementation needs to return extra MCP content blocks, such as a `ResourceLinkBlock`. In that case, set:

```csharp
[McpServerTool(
    ReadOnly = true,
    Idempotent = true,
    OpenWorld = false,
    Destructive = false,
    UseStructuredContent = true,
    OutputSchemaType = typeof(InspectTraceResponse))]
```

This keeps `structuredContent` machine-readable while still allowing unstructured text or resource links in `content`.

## Decision for `inspect_trace`

Implement `inspect_trace(path)` as a typed response first. Return stable workflow pointers as structured fields instead of embedding large workflow text. Add `ResourceLinkBlock` content later only if T1.3 Resources need direct links in the tool result.

Do not mass-annotate existing tools in this spike. Tool annotation rollout should be a separate narrow change because each tool's side-effect classification still needs review.

## Verification

`tests/WprMcp.Tests/McpSdkSurfaceTests.cs` locks the SDK surface that T0.3 depends on:

- protocol hint properties
- attribute hint properties
- programmatic registration equivalents
- `outputSchema` / `structuredContent`
- `ResourceLinkBlock` as a tool-result content block
