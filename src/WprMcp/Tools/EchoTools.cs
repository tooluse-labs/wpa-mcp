using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WprMcp.Tools;

[McpServerToolType]
public static class EchoTools
{
    [McpServerTool, Description("Echo input back. Use to verify MCP transport.")]
    public static string Echo([Description("Text to echo")] string message) => message;
}
