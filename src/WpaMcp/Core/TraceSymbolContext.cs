using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace WpaMcp.Core;

/// <summary>
/// Associates a loaded TraceLog with the original ETL path without keeping the trace alive.
/// This lets stack queries include adjacent PDBs in their SymbolReader snapshot without
/// changing the process-wide configured symbol path.
/// </summary>
internal static class TraceSymbolContext
{
    private static readonly ConditionalWeakTable<TraceLog, TracePath> Paths = new();

    public static void Register(TraceLog trace, string canonicalTracePath)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Paths.GetValue(trace, _ => new TracePath(canonicalTracePath));
    }

    public static string? GetEffectivePath(TraceLog trace)
        => Paths.TryGetValue(trace, out var context)
            ? SymbolPathState.GetEffectivePath(context.Path)
            : SymbolPathState.CurrentPath;

    private sealed record TracePath(string Path);
}
