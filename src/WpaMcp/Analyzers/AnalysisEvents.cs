using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;

namespace WpaMcp.Analyzers;

/// <summary>
/// The single cooperative-cancellation boundary for TraceEvent traversal.
/// Transport cancellation stops a dispatcher and is rethrown after Process()
/// returns; collection enumeration checks the same request token before every row.
/// </summary>
internal static class AnalysisEvents
{
    internal static TraceEventDispatcher CreateDispatcher(
        TraceLog trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Resolve(cancellationToken).ThrowIfCancellationRequested();
        return trace.Events.GetSource();
    }

    internal static void Process(
        TraceEventDispatcher source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var effectiveToken = Resolve(cancellationToken);
        effectiveToken.ThrowIfCancellationRequested();

        using var registration = effectiveToken.Register(
            static state => ((TraceEventDispatcher)state!).StopProcessing(),
            source);

        effectiveToken.ThrowIfCancellationRequested();
        var completed = source.Process();
        effectiveToken.ThrowIfCancellationRequested();
        if (!completed)
        {
            throw new TraceTraversalException(
                "trace_dispatch_incomplete: TraceEvent processing stopped without request cancellation.");
        }
    }

    internal static IEnumerable<TraceEvent> Enumerate(
        TraceLog trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var effectiveToken = Resolve(cancellationToken);
        effectiveToken.ThrowIfCancellationRequested();
        using var enumerator = ((IEnumerable<TraceEvent>)trace.Events).GetEnumerator();
        while (true)
        {
            effectiveToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
                break;
            effectiveToken.ThrowIfCancellationRequested();
            yield return enumerator.Current;
        }
        effectiveToken.ThrowIfCancellationRequested();
    }

    internal static IEnumerable<T> Enumerate<T>(
        IEnumerable<T> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var effectiveToken = Resolve(cancellationToken);
        effectiveToken.ThrowIfCancellationRequested();
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            effectiveToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
                break;
            effectiveToken.ThrowIfCancellationRequested();
            yield return enumerator.Current;
        }
        effectiveToken.ThrowIfCancellationRequested();
    }

    internal static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken = default) =>
        Resolve(cancellationToken).ThrowIfCancellationRequested();

    internal static CancellationToken EffectiveCancellationToken(
        CancellationToken cancellationToken = default) =>
        Resolve(cancellationToken);

    private static CancellationToken Resolve(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? cancellationToken
            : TraceQueryExecutionContext.CurrentCancellationToken;
}

internal sealed class TraceTraversalException(string message) : Exception(message);
