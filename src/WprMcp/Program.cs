using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WprMcp.Cli;

namespace WprMcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("WprMcp 0.1.0-poc");
            return 0;
        }

        // CLI mode: any recognized "--<verb>" first arg routes to CliRunner instead of
        // starting the MCP stdio host. The CLI is a test/debug surface — see Cli/CliRunner.cs.
        if (CliRunner.IsCliInvocation(args))
        {
            return CliRunner.Run(args);
        }

        var serverOptions = McpServerOptions.Parse(args);
        serverOptions.ApplyToEnvironment();

        var builder = Host.CreateApplicationBuilder(serverOptions.HostArgs);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<WprMcp.Core.TraceCache>(_ => new WprMcp.Core.TraceCache());
        builder.Services.AddSingleton<WprMcp.Core.SymbolService>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }
}
