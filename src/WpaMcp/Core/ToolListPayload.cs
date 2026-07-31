using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

internal static class ToolListPayload
{
    public const int DefaultMaxPayloadBytes = 200_000;
    public const int BaselineGuardPayloadBytes = 180_000;

    public static ToolListPayloadStats MeasureCurrentAssembly(
        IServiceProvider? services = null,
        Assembly? assembly = null,
        int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        using var toolScope = CreateToolScope(services);
        var tools = CurrentTools(toolScope.Services, assembly).ToList();
        var result = new ListToolsResult { Tools = tools };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length;
        return new ToolListPayloadStats(tools.Count, payloadBytes, maxPayloadBytes);
    }

    internal static IReadOnlyList<string> MeasureCurrentToolNames(
        IServiceProvider? services = null,
        Assembly? assembly = null)
    {
        using var toolScope = CreateToolScope(services);
        return CurrentTools(toolScope.Services, assembly)
            .Select(tool => tool.Name)
            .ToList();
    }

    private static IReadOnlyList<Tool> CurrentTools(IServiceProvider services, Assembly? assembly)
    {
        assembly ??= typeof(Program).Assembly;
        return BuildTools(assembly, services)
            .Select(tool => tool.ProtocolTool)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<McpServerTool> BuildTools(Assembly assembly, IServiceProvider services)
    {
        var options = new McpServerToolCreateOptions { Services = services };
        foreach (var type in assembly.GetTypes()
                     .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            object? target = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                         .OrderBy(method => method.Name, StringComparer.Ordinal))
            {
                if (!method.IsStatic)
                    target ??= ActivatorUtilities.CreateInstance(services, type);

                yield return McpServerTool.Create(method, method.IsStatic ? null : target, options);
            }
        }
    }

    private static ToolScope CreateToolScope(IServiceProvider? services)
    {
        if (services is not null)
            return new ToolScope(services, ownedProvider: null);

        var collection = new ServiceCollection();
        collection.AddSingleton(_ => new TraceCache());
        collection.AddSingleton<SymbolService>();
        var provider = collection.BuildServiceProvider();
        return new ToolScope(provider, provider);
    }

    private sealed class ToolScope(IServiceProvider services, ServiceProvider? ownedProvider) : IDisposable
    {
        public IServiceProvider Services { get; } = services;

        public void Dispose() => ownedProvider?.Dispose();
    }
}

internal sealed record ToolListPayloadStats(int ToolCount, int PayloadBytes, int MaxPayloadBytes)
{
    public bool ExceedsLimit => PayloadBytes > MaxPayloadBytes;
}

internal sealed class ToolListPayloadHostedService(
    ToolTelemetry telemetry,
    ILogger<ToolListPayloadHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var stats = ToolListPayload.MeasureCurrentAssembly();
        telemetry.RecordToolsListPayload(stats);

        if (stats.ExceedsLimit)
        {
            logger.LogWarning(
                "MCP tools/list payload is {PayloadBytes} bytes for {ToolCount} tools; limit is {MaxPayloadBytes} bytes.",
                stats.PayloadBytes,
                stats.ToolCount,
                stats.MaxPayloadBytes);
        }
        else
        {
            logger.LogInformation(
                "MCP tools/list payload is {PayloadBytes} bytes for {ToolCount} tools; limit is {MaxPayloadBytes} bytes.",
                stats.PayloadBytes,
                stats.ToolCount,
                stats.MaxPayloadBytes);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
