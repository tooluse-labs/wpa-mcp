using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WprMcp.Cli;
using WprMcp.Core;

namespace WprMcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";

            Console.WriteLine($"WprMcp {version}");
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

        var telemetry = ToolTelemetry.CreateFromEnvironment();
        var telemetryFilters = new McpTelemetryFilters(telemetry);

        builder.Services.AddSingleton(telemetry);
        builder.Services.AddSingleton(telemetryFilters);
        builder.Services.AddHostedService<ToolListPayloadHostedService>();
        builder.Services.AddSingleton<TraceCache>(_ => new TraceCache());
        builder.Services.AddSingleton<SymbolService>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithMessageFilters(filters =>
            {
                filters.AddIncomingFilter(telemetryFilters.CreateIncomingFilter());
                filters.AddOutgoingFilter(telemetryFilters.CreateOutgoingFilter());
            })
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }
}
