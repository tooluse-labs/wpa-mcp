using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpaMcp.Cli;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;
using WpaMcp.Tools;

namespace WpaMcp;

public static class Program
{
    internal const int StartupConfigurationErrorExitCode = 78;
    internal const int RequestFrameLimitExitCode = 64;
    internal const int RequestIdLimitExitCode = 65;

    public static async Task<int> Main(string[] args)
    {
        if (SelfUpdateApplyCommand.IsInvocation(args))
            return await SelfUpdateApplyCommand.RunAsync(args).ConfigureAwait(false);

        if (SelfUpdateCommand.IsInvocation(args))
            return await SelfUpdateCommand.RunAsync(args).ConfigureAwait(false);

        if (args.Length == 1 && args[0] == "--version")
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";

            Console.WriteLine($"WpaMcp {version}");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] is "--runtime-profile" or "--validate-release-profile")
        {
            var profile = RuntimeCompatibilityPolicy.EvaluateCurrent();
            Console.WriteLine(JsonSerializer.Serialize(
                profile.ToResourceRecord(),
                McpJsonUtilities.DefaultOptions));
            return args[0] == "--validate-release-profile" && !profile.ReleaseEligible
                ? StartupConfigurationErrorExitCode
                : 0;
        }

        // CLI mode: any recognized "--<verb>" first arg routes to CliRunner instead of
        // starting the MCP stdio host. The CLI is a test/debug surface — see Cli/CliRunner.cs.
        if (CliRunner.IsCliInvocation(args))
        {
            return CliRunner.Run(args);
        }

        McpServerOptions serverOptions;
        JsonRpcRequestFrameOptions requestFrameOptions;
        ToolTelemetryOptions telemetryOptions;
        try
        {
            serverOptions = McpServerOptions.Parse(args);
            requestFrameOptions = new JsonRpcRequestFrameOptions(
                serverOptions.ExecutionBudgets.MaxJsonRpcRequestBytes);
            telemetryOptions = ToolTelemetryOptions.FromEnvironment();
        }
        catch (Exception ex) when (
            ex is ArgumentException or PlatformNotSupportedException or
            ToolsListStartupValidationException)
        {
            WritePrePrivacyBoundaryError(
                args,
                $"wpa-mcp: startup configuration failed: {ex.Message}");
            return StartupConfigurationErrorExitCode;
        }
        if (!serverOptions.TraceRuntime.EnforceTraceRoots && !Console.IsErrorRedirected)
        {
            // Only humans on a terminal see this; MCP clients get a clean stderr.
            Console.Error.WriteLine(
                "wpa-mcp: trace root confinement is off; any readable .etl/.etlx path can be loaded. " +
                "Configure --trace-root to restrict trace access.");
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("wpa-mcp: secure trace artifacts require Windows.");
            return StartupConfigurationErrorExitCode;
        }
        // Validate the complete reviewed model and the minimum tools/list response before
        // constructing a transport that can read stdin.
        StartupState startup;
        try
        {
            startup = CreateStartupState(
                telemetry: null,
                serverOptions.TraceRuntime.AccessMode,
                serverOptions.CompatibilityProfile.ContractMode,
                serverOptions.Privacy.Mode,
                serverOptions.ExecutionBudgets,
                serverOptions.CapabilityPolicy);
        }
        catch (Exception ex) when (
            ex is CatalogValidationException or ToolsListStartupValidationException or
                ToolContractDiscoveryStartupValidationException)
        {
            var detail = ex is CatalogValidationException
                ? "active catalog validation failed"
                : ex.Message;
            WritePrePrivacyBoundaryError(
                serverOptions.Privacy.Mode,
                $"wpa-mcp: startup validation failed: {detail}");
            return StartupConfigurationErrorExitCode;
        }
        using var privacyAliases = startup.PrivacyAliases;
        using var privacyLogSink = startup.PrivacyLogSink;

        await using var guardedInput = new JsonRpcFrameLimitingStream(
            Console.OpenStandardInput(),
            requestFrameOptions);
        await guardedInput.PrimeAsync().ConfigureAwait(false);
        if (guardedInput.Rejected)
            return ReportIngressRejection(guardedInput.Rejection, privacyLogSink);

        // All constructors after this point may touch configured telemetry, trace, or
        // symbol storage. A rejected first frame can therefore have no filesystem side
        // effects beyond the catalog/configuration reads required for startup validation.
        var telemetry = ToolTelemetry.Create(telemetryOptions);
        telemetry.RecordRuntimeProfile(serverOptions.CompatibilityProfile);
        startup = startup with
        {
            Pagination = CreatePagination(
                startup.Catalog,
                startup.ServerTools,
                serverOptions.ExecutionBudgets,
                serverOptions.CompatibilityProfile.ContractMode,
                telemetry),
        };

        var activeCatalog = startup.Catalog;
        var catalogServices = startup.Services;
        var activeServerTools = startup.ServerTools;
        var toolsListPagination = startup.Pagination;
        var telemetryFilters = new McpTelemetryFilters(telemetry);
        var contractMessageFilters = new ToolContractMessageFilters(
            activeCatalog.Tools,
            activeServerTools,
            serverOptions.ExecutionBudgets);

        TraceAccessPolicy traceAccessPolicy;
        try
        {
            traceAccessPolicy = new TraceAccessPolicy(serverOptions.TraceRuntime);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            telemetry.Dispose();
            privacyLogSink.Writer.WriteLine(
                $"wpa-mcp: trace policy startup failed: {ex.Message}");
            return StartupConfigurationErrorExitCode;
        }

        var traceCache = new TraceCache(serverOptions.CacheSize ?? 0);
        var artifactStore = new OwnedTraceArtifactStore(
            traceAccessPolicy.ArtifactRoot,
            traceAccessPolicy.MaxInputTraceBytes,
            serverOptions.TraceRuntime.MaxArtifactStoreBytes,
            serverOptions.TraceRuntime.MaxArtifactObjects,
            retentionTtl: serverOptions.TraceRuntime.ArtifactRetentionTtl);
        var artifactLoader = new TraceArtifactLoader(traceAccessPolicy, artifactStore);
        var traceRegistry = new TraceHandleRegistry(traceCache);
        var traceLifecycle = new TraceLifecycleService(artifactLoader, traceRegistry);
        var traceReferences = new TraceReferenceResolver(traceRegistry, traceLifecycle);
        var sessionPrincipal = new StdioSessionPrincipal();
        var capabilityDiscovery = new CapabilityDiscoveryRuntime(
            activeCatalog,
            sessionPrincipal,
            maxResponseFrameBytes: serverOptions.ExecutionBudgets.MaxJsonRpcResponseBytes,
            runtimeProfile: serverOptions.CompatibilityProfile,
            privacyProfile: serverOptions.Privacy.Profile,
            capabilityPolicyIdentity: serverOptions.CapabilityPolicy.ProfileHash);
        var traceToolRuntime = new TraceToolRuntime(
            traceLifecycle,
            traceRegistry,
            sessionPrincipal);
        var traceQueryFilters = new TraceQueryExecutionFilters(
            toolsListPagination.ActiveTools,
            traceReferences,
            traceCache,
            sessionPrincipal,
            serverOptions.TraceRuntime.AccessMode);

        IVerifiedSymbolArtifactStore symbolArtifactStore;
        try
        {
            symbolArtifactStore = serverOptions.SymbolRuntime.ApprovedLocalRoots.Count == 0
                ? new DisabledVerifiedSymbolArtifactStore()
                : new LocalVerifiedSymbolArtifactStore(
                    serverOptions.SymbolRuntime.StoreRoot!,
                    serverOptions.SymbolRuntime.MaxArtifactBytes,
                    new TraceEventLocalPdbIdentityVerifier(),
                    serverOptions.SymbolRuntime.MaxStoreBytes);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or
            PlatformNotSupportedException or TraceAccessException)
        {
            artifactStore.Dispose();
            traceAccessPolicy.Dispose();
            telemetry.Dispose();
            privacyLogSink.Writer.WriteLine(
                $"wpa-mcp: symbol policy startup failed: {ex.Message}");
            return StartupConfigurationErrorExitCode;
        }

        using var symbolArtifactStoreLifetime = symbolArtifactStore as IDisposable;
        await using var symbolContextRegistry = new SymbolContextRegistry(
            SymbolContextRegistryOptions.Default);
        var symbolPolicy = serverOptions.SymbolRuntime.CreatePolicySnapshot();
        var symbolPolicyCatalog = new ApprovedSymbolPolicyCatalog([symbolPolicy]);
        var symbolPreparationResolver = new LocalOnlySymbolPreparationResolver(
            symbolArtifactStore);
        var symbolPreparation = new SymbolPreparationService(
            symbolContextRegistry,
            symbolPolicyCatalog,
            symbolPreparationResolver);
        var symbolToolRuntime = new SymbolToolRuntime(
            traceToolRuntime,
            sessionPrincipal,
            symbolPreparation,
            serverOptions.SymbolRuntime.DefaultPolicyReference,
            serverOptions.Privacy.Profile);
        var symbolQueryFilters = new SymbolQueryExecutionFilters(
            toolsListPagination.ActiveTools,
            symbolContextRegistry,
            sessionPrincipal,
            () => TraceQueryExecutionContext.CurrentCacheGenerationSequence is { } sequence
                ? SymbolTraceGenerationIdentity.FromCacheSequence(sequence)
                : null);
        await using var symbolPreparationDelivery =
            new SymbolPreparationDeliveryFilters();

        foreach (var warning in serverOptions.CompatibilityProfile.Warnings)
        {
            privacyLogSink.Writer.WriteLine(
                "wpa-mcp: compatibility warning: " + warning);
        }

        var builder = Host.CreateApplicationBuilder(serverOptions.HostArgs);

        builder.Logging.ClearProviders();
        if (serverOptions.Privacy.Mode == ToolPrivacyMode.Off)
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        else
            builder.Logging.AddProvider(new PrivacyLoggerProvider(privacyLogSink));

        builder.Services.AddSingleton(telemetry);
        builder.Services.AddSingleton(telemetryFilters);
        builder.Services.AddSingleton(activeCatalog);
        builder.Services.AddSingleton(toolsListPagination);
        builder.Services.AddHostedService<ToolListPayloadHostedService>();
        builder.Services.AddSingleton(serverOptions.TraceRuntime);
        builder.Services.AddSingleton(serverOptions.CompatibilityProfile);
        builder.Services.AddSingleton(serverOptions.SymbolRuntime);
        builder.Services.AddSingleton(serverOptions.ExecutionBudgets);
        builder.Services.AddSingleton(serverOptions.CapabilityPolicy);
        builder.Services.AddSingleton(traceAccessPolicy);
        builder.Services.AddSingleton(traceCache);
        builder.Services.AddSingleton(artifactStore);
        builder.Services.AddSingleton(artifactLoader);
        builder.Services.AddSingleton(traceRegistry);
        builder.Services.AddSingleton(traceLifecycle);
        builder.Services.AddSingleton(traceReferences);
        builder.Services.AddSingleton(sessionPrincipal);
        builder.Services.AddSingleton(capabilityDiscovery);
        builder.Services.AddSingleton(traceToolRuntime);
        builder.Services.AddSingleton(traceQueryFilters);
        builder.Services.AddSingleton(symbolArtifactStore);
        builder.Services.AddSingleton(symbolContextRegistry);
        builder.Services.AddSingleton<IApprovedSymbolPolicyProvider>(symbolPolicyCatalog);
        builder.Services.AddSingleton<ISymbolPreparationResolver>(symbolPreparationResolver);
        builder.Services.AddSingleton(symbolPreparation);
        builder.Services.AddSingleton(symbolToolRuntime);
        builder.Services.AddSingleton(symbolQueryFilters);
        builder.Services.AddSingleton<IPrivacyLogSink>(privacyLogSink);

        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(guardedInput, Console.OpenStandardOutput())
            .WithMessageFilters(filters =>
            {
                filters.AddIncomingFilter(contractMessageFilters.CreateIncomingFilter());
                filters.AddIncomingFilter(toolsListPagination.CreateIncomingFilter());
                filters.AddIncomingFilter(symbolPreparationDelivery.CreateIncomingFilter());
                filters.AddIncomingFilter(traceQueryFilters.CreateIncomingFilter());
                filters.AddIncomingFilter(symbolQueryFilters.CreateIncomingFilter());
                filters.AddIncomingFilter(telemetryFilters.CreateIncomingFilter());
                filters.AddOutgoingFilter(symbolPreparationDelivery.CreateOutgoingFilter());
                filters.AddOutgoingFilter(traceQueryFilters.CreateOutgoingFilter());
                filters.AddOutgoingFilter(toolsListPagination.CreateOutgoingFilter());
                filters.AddOutgoingFilter(telemetryFilters.CreateOutgoingFilter());
            })
            // Force the instance-registration overload. Passing IReadOnlyList<T>
            // directly can bind the generic target-scanning overload, which
            // reconstructs SDK method tools and bypasses ContractMcpServerTool.
            .WithTools((IEnumerable<McpServerTool>)activeServerTools)
            .WithResources<CapabilityDiscoveryResources>();

        using var host = builder.Build();
        catalogServices.Bind(host.Services);
        await host.RunAsync();
        if (guardedInput.Rejected)
            return ReportIngressRejection(guardedInput.Rejection, privacyLogSink);
        return 0;
    }

    private static int ReportIngressRejection(
        JsonRpcIngressRejection rejection,
        IPrivacyLogSink privacyLog)
    {
        if (rejection == JsonRpcIngressRejection.RequestIdLimit)
        {
            privacyLog.Writer.WriteLine(JsonRpcFrameLimitingStream.RequestIdRejectionMessage);
            return RequestIdLimitExitCode;
        }
        privacyLog.Writer.WriteLine(JsonRpcFrameLimitingStream.RejectionMessage);
        return RequestFrameLimitExitCode;
    }

    private static void WritePrePrivacyBoundaryError(string[] args, string message)
    {
        var requested = Environment.GetEnvironmentVariable(ToolPrivacyOptions.EnvironmentVariable);
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], "--privacy-profile", StringComparison.Ordinal))
                requested = args[index + 1];
        }
        var mode = requested?.Trim().ToLowerInvariant() switch
        {
            null or "" or "off" => ToolPrivacyMode.Off,
            "paths" => ToolPrivacyMode.Paths,
            _ => ToolPrivacyMode.Strict,
        };
        WritePrePrivacyBoundaryError(mode, message);
    }

    private static void WritePrePrivacyBoundaryError(ToolPrivacyMode mode, string message) =>
        Console.Error.WriteLine(
            mode == ToolPrivacyMode.Off ? message : "wpa-mcp: [redacted-startup-diagnostic]");

    private static StartupState CreateStartupState(
        ToolTelemetry? telemetry,
        TraceAccessMode traceAccessMode,
        ToolContractMode contractMode,
        ToolPrivacyMode privacyMode,
        ToolExecutionBudgetOptions executionBudgets,
        CapabilityPolicyProfile capabilityPolicy)
    {
        if (contractMode != ToolContractMode.V2)
        {
            throw new CatalogValidationException(
                "CONTRACT-MODE: the active runtime has no reviewed legacy result adapter");
        }
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new DeferredCatalogServiceProvider();
        var paginationOptions = ToolsListPaginationOptions.Default with
        {
            MaxResponseFrameBytes = executionBudgets.MaxJsonRpcResponseBytes,
        };
        var responseBudget = new ToolResponseBudgetOptions(
            paginationOptions.MaxResponseFrameBytes);
        var aliases = new TypedAliasRegistry();
        PrivacyLogSink? privacyLogSink = null;
        try
        {
            var taxonomy = ToolPrivacyTaxonomy.Default;
            var redactor = new ToolPrivacyRedactor(privacyMode, taxonomy, aliases);
            privacyLogSink = new PrivacyLogSink(privacyMode, redactor);
            IReadOnlyList<McpServerTool> serverTools = catalog.CreateServerTools(
                services,
                responseBudget: responseBudget,
                privacy: redactor,
                argumentRewriter: new ToolArgumentRewriter(taxonomy, aliases));
            var policyProjection = catalog.ProjectCapabilityPolicy(
                capabilityPolicy,
                serverTools);
            catalog = policyProjection.Catalog;
            serverTools = policyProjection.ServerTools;
            var pagination = CreatePagination(
                catalog,
                serverTools,
                executionBudgets,
                contractMode,
                telemetry);
            ToolContractDiscoveryPreflight.Measure(catalog, serverTools)
                .Validate(executionBudgets.MaxJsonRpcResponseBytes);
            return new StartupState(
                catalog,
                services,
                serverTools,
                pagination,
                aliases,
                privacyLogSink);
        }
        catch
        {
            privacyLogSink?.Dispose();
            aliases.Dispose();
            throw;
        }
    }

    private static ToolsListPaginationFilters CreatePagination(
        ActiveToolCatalog catalog,
        IReadOnlyList<McpServerTool> serverTools,
        ToolExecutionBudgetOptions executionBudgets,
        ToolContractMode contractMode,
        ToolTelemetry? telemetry)
    {
        var options = ToolsListPaginationOptions.Default with
        {
            MaxResponseFrameBytes = executionBudgets.MaxJsonRpcResponseBytes,
        };
        return new ToolsListPaginationFilters(
            serverTools.Select(tool => tool.ProtocolTool).ToArray(),
            catalog.CatalogVersion,
            options,
            contractMode == ToolContractMode.V2
                ? ToolContractVersions.V2
                : "legacy",
            telemetry,
            capabilityPolicyIdentity: catalog.CapabilityPolicy.ProfileHash);
    }

    private sealed record StartupState(
        ActiveToolCatalog Catalog,
        DeferredCatalogServiceProvider Services,
        IReadOnlyList<McpServerTool> ServerTools,
        ToolsListPaginationFilters Pagination,
        TypedAliasRegistry PrivacyAliases,
        PrivacyLogSink PrivacyLogSink);
}
