using WpaMcp.Analyzers;
using WpaMcp.Core;
using System.Text.RegularExpressions;
using Xunit;

namespace WpaMcp.Tests;

public sealed class TraversalCancellationTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void DispatcherCancellationDuringFirstCallback_StopsAndRethrows()
    {
        using var cache = new TraceCache(capacity: 1);
        var trace = cache.Get(FixturePath);
        using var cancellation = new CancellationTokenSource();
        var callbackCount = 0;
        var source = AnalysisEvents.CreateDispatcher(trace, cancellation.Token);
        source.AllEvents += _ =>
        {
            if (Interlocked.Increment(ref callbackCount) == 1)
                cancellation.Cancel();
        };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            AnalysisEvents.Process(source, cancellation.Token));
        Assert.Equal(1, Volatile.Read(ref callbackCount));

        var retryCount = 0;
        var retry = AnalysisEvents.CreateDispatcher(trace);
        retry.AllEvents += _ => retryCount++;
        AnalysisEvents.Process(retry);
        Assert.True(retryCount > 1);
    }

    [Fact]
    public void AmbientTransportToken_ReachesDirectEventEnumeration()
    {
        using var cache = new TraceCache(capacity: 1);
        using var registry = new TraceHandleRegistry(cache);
        var principal = new StdioSessionPrincipal().RegistryKey;
        var loaded = registry.Load(principal, FixturePath);
        var resolver = new TraceReferenceResolver(registry);
        using var resolved = resolver.ResolveQuery(
            principal,
            loaded.TraceId,
            TraceAccessMode.IdOnly);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var execution = TraceQueryExecutionContext.Begin(
            cache,
            loaded.TraceId,
            resolved,
            cancellation.Token);
        using var lease = cache.Acquire(FixturePath);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MarkerSearch.Find(lease.Trace, "Sample", top: 10));
    }

    [Fact]
    public void AmbientTransportToken_ReachesDispatcherAnalyzer()
    {
        const string fixture = "fixtures/small_fileio.etl";
        using var cache = new TraceCache(capacity: 1);
        using var registry = new TraceHandleRegistry(cache);
        var principal = new StdioSessionPrincipal().RegistryKey;
        var loaded = registry.Load(principal, fixture);
        var resolver = new TraceReferenceResolver(registry);
        using var resolved = resolver.ResolveQuery(
            principal,
            loaded.TraceId,
            TraceAccessMode.IdOnly);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var execution = TraceQueryExecutionContext.Begin(
            cache,
            loaded.TraceId,
            resolved,
            cancellation.Token);
        using var lease = cache.Acquire(fixture);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FileIoAnalysis.TopFiles(lease.Trace, top: 10, pid: null));
    }

    [Fact]
    public void AmbientTransportToken_ReachesStackPostProcessing()
    {
        using var cache = new TraceCache(capacity: 1);
        using var registry = new TraceHandleRegistry(cache);
        var principal = new StdioSessionPrincipal().RegistryKey;
        var loaded = registry.Load(principal, FixturePath);
        var resolver = new TraceReferenceResolver(registry);
        using var resolved = resolver.ResolveQuery(
            principal,
            loaded.TraceId,
            TraceAccessMode.IdOnly);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var execution = TraceQueryExecutionContext.Begin(
            cache,
            loaded.TraceId,
            resolved,
            cancellation.Token);
        using var lease = cache.Acquire(FixturePath);
        var raw = StackSourceTopN.CreateRawSource(lease.Trace, "cancellation_test");
        raw.Source.DoneAddingSamples();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            StackSourceTopN.BuildNormalized(
                raw.Source,
                lease.Trace,
                excludeEtwSelfOverhead: false));
    }

    [Fact]
    public void DispatcherStopWithoutCancellation_FailsClosed()
    {
        using var cache = new TraceCache(capacity: 1);
        var trace = cache.Get(FixturePath);
        var source = AnalysisEvents.CreateDispatcher(trace);
        source.AllEvents += _ => source.StopProcessing();

        var failure = Assert.Throws<TraceTraversalException>(() =>
            AnalysisEvents.Process(source));
        Assert.Contains("trace_dispatch_incomplete", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzerSources_CannotBypassTheCancellationBoundary()
    {
        var analyzerRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WpaMcp",
            "Analyzers");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(analyzerRoot, "*.cs"))
        {
            if (string.Equals(
                    Path.GetFileName(path),
                    "AnalysisEvents.cs",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var withoutApprovedProcess = source.Replace(
                "AnalysisEvents.Process(",
                "ApprovedTraversal(",
                StringComparison.Ordinal);
            if (source.Contains("trace.Events", StringComparison.Ordinal) ||
                Regex.IsMatch(
                    source,
                    @"\.Events\s*\.GetSource\s*\(",
                    RegexOptions.CultureInvariant) ||
                Regex.IsMatch(
                    source,
                    @"\bin\s+[A-Za-z_][A-Za-z0-9_]*\.Events\b",
                    RegexOptions.CultureInvariant) ||
                Regex.IsMatch(
                    withoutApprovedProcess,
                    @"\.\s*Process\s*\(",
                    RegexOptions.CultureInvariant) ||
                Regex.IsMatch(
                    source,
                    @"new\s+[A-Za-z0-9_]*TraceEventParser\s*\(\s*trace\b",
                    RegexOptions.CultureInvariant) ||
                (source.Contains("catch (Exception", StringComparison.Ordinal) &&
                 !source.Contains(
                     "catch (OperationCanceledException)",
                     StringComparison.Ordinal)))
            {
                violations.Add(Path.GetFileName(path));
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void StackPostProcessing_CapturesAmbientTokenOncePerLongOperation()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WpaMcp",
            "Analyzers",
            "StackSourceTopN.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("AnalysisEvents.EffectiveCancellationToken()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AnalysisEvents.ThrowIfCancellationRequested()",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WpaMcp.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
