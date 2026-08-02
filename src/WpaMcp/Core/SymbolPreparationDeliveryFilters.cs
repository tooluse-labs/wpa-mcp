using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

internal static class SymbolPreparationDeliveryContext
{
    private static readonly AsyncLocal<State?> Current = new();

    internal static IDisposable Begin(out State state)
    {
        var prior = Current.Value;
        state = new State();
        Current.Value = state;
        return new Scope(prior);
    }

    internal static bool TryRegister(SymbolContextDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        var state = Current.Value;
        if (state is null)
            return false;
        state.Register(disclosure);
        return true;
    }

    internal sealed class State
    {
        private SymbolContextDisclosure? _disclosure;

        internal void Register(SymbolContextDisclosure disclosure)
        {
            if (Interlocked.CompareExchange(ref _disclosure, disclosure, null) is not null)
                throw new InvalidOperationException("A prepare_symbols request published more than one disclosure.");
        }

        internal SymbolContextDisclosure? Take() =>
            Interlocked.Exchange(ref _disclosure, null);

        internal async ValueTask RollbackAsync()
        {
            if (Take() is { } disclosure)
                await disclosure.RollbackAsync().ConfigureAwait(false);
        }
    }

    private sealed class Scope(State? prior) : IDisposable
    {
        private State? _prior = prior;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Current.Value = Interlocked.Exchange(ref _prior, null);
        }
    }
}

/// <summary>
/// Keeps newly created symbol contexts provisional until the final successful
/// tools/call response is accepted by every downstream outgoing filter/transport.
/// </summary>
internal sealed class SymbolPreparationDeliveryFilters : IAsyncDisposable
{
    private const string ToolName = "prepare_symbols";
    private readonly ConcurrentDictionary<string, PendingDisclosure> _pending = [];
    private int _disposed;

    internal int PendingCount => _pending.Count;

    public McpMessageFilter CreateIncomingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (!IsPrepareRequest(context.JsonRpcMessage, out var request))
            {
                await next(context, cancellationToken);
                return;
            }

            ThrowIfDisposed();
            var key = MessageIdKey(request);
            SymbolContextDisclosure? disclosure = null;
            using var scope = SymbolPreparationDeliveryContext.Begin(out var state);
            try
            {
                await next(context, cancellationToken);
                disclosure = state.Take();
                if (disclosure is null)
                    return;

                var pending = new PendingDisclosure(
                    disclosure,
                    cancellationToken,
                    () => QueueRollback(key));
                if (!_pending.TryAdd(key, pending))
                {
                    pending.Dispose();
                    await disclosure.RollbackAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "A prepare_symbols request with this id is already pending.");
                }
                disclosure = null;
                if (cancellationToken.IsCancellationRequested)
                {
                    await RollbackAsync(key).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                await state.RollbackAsync().ConfigureAwait(false);
                if (disclosure is not null)
                    await disclosure.RollbackAsync().ConfigureAwait(false);
                await RollbackAsync(key).ConfigureAwait(false);
                throw;
            }
        };

    public McpMessageFilter CreateOutgoingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcMessageWithId message ||
                !_pending.TryRemove(MessageIdKey(message), out var pending))
            {
                await next(context, cancellationToken);
                return;
            }

            var delivered = false;
            try
            {
                if (!IsSuccessfulToolResponse(context.JsonRpcMessage))
                {
                    await next(context, cancellationToken);
                    return;
                }

                await next(context, cancellationToken);
                delivered = IsSuccessfulToolResponse(context.JsonRpcMessage);
                if (delivered)
                    pending.Disclosure.Commit();
            }
            finally
            {
                pending.Dispose();
                if (!delivered)
                    await pending.Disclosure.RollbackAsync().ConfigureAwait(false);
            }
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var key in _pending.Keys)
            await RollbackAsync(key).ConfigureAwait(false);
    }

    private void QueueRollback(string key) => _ = RollbackAsync(key);

    private async ValueTask RollbackAsync(string key)
    {
        if (!_pending.TryRemove(key, out var pending))
            return;
        pending.Dispose();
        await pending.Disclosure.RollbackAsync().ConfigureAwait(false);
    }

    private static bool IsPrepareRequest(
        JsonRpcMessage message,
        out JsonRpcRequest request)
    {
        request = message as JsonRpcRequest ?? null!;
        if (request is null ||
            !string.Equals(request.Method, RequestMethods.ToolsCall, StringComparison.Ordinal))
            return false;
        try
        {
            var call = request.Params?.Deserialize<CallToolRequestParams>(
                McpJsonUtilities.DefaultOptions);
            return string.Equals(call?.Name, ToolName, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSuccessfulToolResponse(JsonRpcMessage message)
    {
        if (message is not JsonRpcResponse response)
            return false;
        var result = JsonSerializer.SerializeToElement(
            response.Result,
            McpJsonUtilities.DefaultOptions);
        return result.ValueKind != JsonValueKind.Object ||
               !result.TryGetProperty("isError", out var isError) ||
               isError.ValueKind != JsonValueKind.True;
    }

    private static string MessageIdKey(JsonRpcMessageWithId message) =>
        JsonSerializer.Serialize(message.Id, McpJsonUtilities.DefaultOptions);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SymbolPreparationDeliveryFilters));
    }

    private sealed class PendingDisclosure : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellation;
        private readonly Timer _expiry;
        private int _disposed;

        internal PendingDisclosure(
            SymbolContextDisclosure disclosure,
            CancellationToken cancellationToken,
            Action rollback)
        {
            Disclosure = disclosure ?? throw new ArgumentNullException(nameof(disclosure));
            _cancellation = cancellationToken.Register(rollback);
            _expiry = new Timer(
                static state => ((Action)state!).Invoke(),
                rollback,
                TimeSpan.FromMinutes(2),
                Timeout.InfiniteTimeSpan);
        }

        internal SymbolContextDisclosure Disclosure { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cancellation.Dispose();
            _expiry.Dispose();
        }
    }
}
