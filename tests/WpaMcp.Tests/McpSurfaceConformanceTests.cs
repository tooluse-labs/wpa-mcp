using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class McpSurfaceConformanceTests
{
    private static readonly string[] ApprovedWindowedMethods =
    [
        "AlpcTools.AlpcCallerCallee",
        "AlpcTools.AlpcTopStacks",
        "ClrTools.ClrAllocCallerCallee",
        "ClrTools.ClrAllocTopStacks",
        "ClrTools.ClrContentionCallerCallee",
        "ClrTools.ClrContentionTopStacks",
        "ClrTools.ClrExceptionCallerCallee",
        "ClrTools.ClrExceptionTopStacks",
        "ClrTools.ClrFinalizerAnalysis",
        "ClrTools.ClrGcAnalysis",
        "ClrTools.ClrGcHeapStats",
        "ClrTools.ClrJitAnalysis",
        "CpuTools.CpuCallerCallee",
        "CpuTools.CpuPreciseAnalysis",
        "CpuTools.CpuTopFunctions",
        "CpuTools.CpuTopFunctionsBatch",
        "DiagnoseTools.DiagnoseHighWait",
        "DiagnoseTools.DiagnoseWindow",
        "GenericProviderTools.GenericEventCallerCallee",
        "GenericProviderTools.GenericEventTopStacks",
        "HardFaultTools.HardFaultByFile",
        "HardFaultTools.HardFaultCallerCallee",
        "HardFaultTools.HardFaultTopStacks",
        "HeapTools.HeapAllocCallerCallee",
        "HeapTools.HeapAllocTopStacks",
        "ImageLoadTools.ImageLoadCallerCallee",
        "ImageLoadTools.ImageLoadTopStacks",
        "InterruptTools.InterruptCallerCallee",
        "InterruptTools.InterruptTopStacks",
        "IoTools.DiskIoCallerCallee",
        "IoTools.DiskIoTopStacks",
        "IoTools.FileIoCallerCallee",
        "IoTools.FileIoTopFiles",
        "IoTools.FileIoTopStacks",
        "NetIoTools.NetCallerCallee",
        "NetIoTools.NetConnections",
        "NetIoTools.NetTopStacks",
        "ReadyThreadTools.ReadyThreadCallerCallee",
        "ReadyThreadTools.ReadyThreadTopStacks",
        "RegistryTools.RegistryCallerCallee",
        "RegistryTools.RegistryTopStacks",
        "SecurityTools.SecurityScanAnalysis",
        "VirtualMemoryTools.MemoryResourceAnalysis",
        "VirtualMemoryTools.VirtualAllocCallerCallee",
        "VirtualMemoryTools.VirtualAllocTopStacks",
        "WaitTools.WaitAnalysis",
        "WaitTools.WaitCallerCallee",
        "WaitTools.WaitTopStacks",
    ];

    private static readonly Regex RawTimeFormula = new(
        @"(?:\(long\)\s*\([^\)\r\n]*(?:TimeStampRelativeMSec|StartTimeRelativeMsec|EndTimeRelativeMsec|ElapsedTimeMSec|CPUMSec|tsRelMs|nowMs|startMs|endMs|timeStampRelativeMSec)[^\)\r\n]*\*\s*1_?000(?:d)?\s*\)|Math\.Floor\s*\(\s*value\s*\*\s*1_?000d?\s*\))",
        RegexOptions.CultureInvariant);

    private static readonly Regex ManualWindowClipFormula = new(
        @"Math\.(?:Min|Max)\s*\([^;\r\n]*(?:startUs|endUs|StartUs|EndUs|WindowStartUs|WindowEndUs)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void McpTools_TidParametersAlwaysHavePid()
    {
        foreach (var method in McpToolMethods())
        {
            var parameterNames = method.GetParameters()
                .Select(parameter => parameter.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (parameterNames.Contains("tid"))
            {
                Assert.Contains("pid", parameterNames);
            }
        }
    }

    [Fact]
    public void CpuAndWaitThreadTools_ExposeTheSameSelectorSuffix()
    {
        var expected = new[]
        {
            (Name: "tid", Type: typeof(int?)),
            (Name: "processStartUs", Type: typeof(long?)),
            (Name: "threadStartUs", Type: typeof(long?)),
            (Name: "threadGeneration", Type: typeof(long?)),
        };
        var methods = new[]
        {
            typeof(WaitTools).GetMethod(nameof(WaitTools.WaitAnalysis))!,
            typeof(WaitTools).GetMethod(nameof(WaitTools.WaitTopStacks))!,
            typeof(WaitTools).GetMethod(nameof(WaitTools.WaitCallerCallee))!,
            typeof(CpuTools).GetMethod(nameof(CpuTools.CpuPreciseAnalysis))!,
            typeof(CpuTools).GetMethod(nameof(CpuTools.CpuTopFunctions))!,
            typeof(CpuTools).GetMethod(nameof(CpuTools.CpuCallerCallee))!,
        };

        Assert.All(methods, method =>
        {
            var suffix = method.GetParameters()[^expected.Length..];
            Assert.Equal(expected.Select(item => item.Name), suffix.Select(parameter => parameter.Name));
            Assert.Equal(expected.Select(item => item.Type), suffix.Select(parameter => parameter.ParameterType));
            Assert.All(suffix, parameter => Assert.True(parameter.HasDefaultValue));
            var generation = suffix[^1];
            var description = Assert.IsType<DescriptionAttribute>(
                Attribute.GetCustomAttribute(generation, typeof(DescriptionAttribute)));
            Assert.Contains("generation", description.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pid and tid", description.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void McpTools_EndUsDescriptionsSayExclusive()
    {
        var methods = McpToolMethods()
            .Where(method => method.GetParameters().Any(parameter => parameter.Name == "endUs"))
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            var parameter = Assert.Single(
                method.GetParameters(), candidate => candidate.Name == "endUs");
            var description = Assert.IsType<DescriptionAttribute>(
                Attribute.GetCustomAttribute(parameter, typeof(DescriptionAttribute)));
            Assert.Contains("exclusive", description.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void WindowedMcpToolInventory_MatchesApprovedSurface()
    {
        var actual = McpToolMethods()
            .Where(method => method.GetParameters().Any(parameter => parameter.Name == "startUs" || parameter.Name == "endUs"))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ApprovedWindowedMethods, actual);
    }

    [Fact]
    public void WindowedMcpTools_RejectInvalidWindowsBeforeTraceAccess()
    {
        foreach (var method in McpToolMethods()
                     .Where(method => method.GetParameters().Any(parameter => parameter.Name == "endUs")))
        {
            AssertWindowFailure(method, startUs: -1, endUs: 1);
            AssertWindowFailure(method, startUs: 2, endUs: 1);
        }
    }

    [Fact]
    public void WindowPrimitiveAllowlist_CoversLegacyFormulas()
    {
        var repoRoot = LocateRepoRoot();
        var allowlistPath = Path.Combine(
            repoRoot, "tests", "WpaMcp.Tests", "Architecture", "window-primitive-allowlist.txt");
        var lines = File.ReadAllLines(allowlistPath, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        var entries = lines.Select(ParseAllowlistEntry).ToArray();

        Assert.Equal(lines.OrderBy(line => line, StringComparer.Ordinal), lines);
        Assert.Equal(entries.Length, entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry => Assert.Contains(
            entry.Owner, new[] { "PERMANENT", "C2", "C3", "C4", "C8" }));
        Assert.Equal(
            new[] { "src/WpaMcp/Core/TimeWindow.cs", "src/WpaMcp/Core/TraceTime.cs" },
            entries.Where(entry => entry.Owner == "PERMANENT")
                .Select(entry => entry.Path)
                .OrderBy(path => path, StringComparer.Ordinal));

        var sourceRoot = Path.Combine(repoRoot, "src", "WpaMcp");
        var findings = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return RawTimeFormula.IsMatch(source) || ManualWindowClipFormula.IsMatch(source);
            })
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var approvedPaths = entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(findings.Where(path => !approvedPaths.Contains(path)));
    }

    private static AllowlistEntry ParseAllowlistEntry(string line)
    {
        var parts = line.Split('|');
        Assert.Equal(3, parts.Length);
        Assert.False(string.IsNullOrWhiteSpace(parts[0]));
        Assert.False(string.IsNullOrWhiteSpace(parts[1]));
        Assert.False(string.IsNullOrWhiteSpace(parts[2]));
        return new AllowlistEntry(parts[0], parts[1], parts[2]);
    }

    private static IReadOnlyList<MethodInfo> McpToolMethods() =>
        typeof(MetaTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

    private static void AssertWindowFailure(MethodInfo method, long startUs, long endUs)
    {
        var constructor = method.DeclaringType!.GetConstructors()
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length > 0 &&
                       parameters[0].ParameterType == typeof(TraceCache) &&
                       parameters.Skip(1).All(parameter => parameter.HasDefaultValue);
            });
        var constructorArguments = constructor.GetParameters()
            .Select((parameter, index) => index == 0
                ? (object)new TraceCache(capacity: 1)
                : parameter.DefaultValue)
            .ToArray();
        var target = constructor.Invoke(constructorArguments);
        var arguments = method.GetParameters()
            .Select(parameter => ArgumentFor(parameter, startUs, endUs))
            .ToArray();

        var exception = Record.Exception(() => method.Invoke(target, arguments));
        var actual = exception is TargetInvocationException invocation
            ? invocation.InnerException
            : exception;

        Assert.True(
            actual is ArgumentOutOfRangeException,
            $"{method.DeclaringType.Name}.{method.Name} touched the trace or returned the wrong " +
            $"error before rejecting [{startUs},{endUs}): {actual?.GetType().Name}: {actual?.Message}");
    }

    private static object? ArgumentFor(ParameterInfo parameter, long startUs, long endUs)
    {
        if (parameter.Name == "traceId")
        {
            return "trc_00000000000000000000000000000000";
        }

        if (parameter.Name == "startUs")
        {
            return startUs;
        }

        if (parameter.Name == "endUs")
        {
            return endUs;
        }

        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        if (parameter.ParameterType == typeof(string))
        {
            return "value";
        }

        if (parameter.ParameterType == typeof(int[]))
        {
            return new[] { 1 };
        }

        if (parameter.ParameterType == typeof(int))
        {
            return 1;
        }

        if (parameter.ParameterType == typeof(long))
        {
            return 1L;
        }

        throw new InvalidOperationException(
            $"No conformance-test value is defined for {parameter.Member.Name}.{parameter.Name} " +
            $"({parameter.ParameterType}).");
    }

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record AllowlistEntry(string Path, string Owner, string Reason);
}
