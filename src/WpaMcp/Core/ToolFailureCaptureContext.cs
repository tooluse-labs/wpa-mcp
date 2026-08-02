namespace WpaMcp.Core;

/// <summary>
/// Carries a typed, reviewed failure across the SDK's internal exception-to-error
/// conversion without parsing error text. It is active only around one wrapped
/// tool invocation and is ignored when the inner tool succeeds.
/// </summary>
internal static class ToolFailureCaptureContext
{
    private static readonly AsyncLocal<State?> Current = new();

    internal static CaptureScope Begin()
    {
        var state = new State(Current.Value);
        Current.Value = state;
        return new CaptureScope(state);
    }

    internal static void Record(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var state = Current.Value;
        if (state is not null)
            state.Captured ??= exception;
    }

    internal static TException Capture<TException>(TException exception)
        where TException : Exception
    {
        Record(exception);
        return exception;
    }

    internal sealed class State(State? prior)
    {
        internal State? Prior { get; } = prior;
        internal Exception? Captured { get; set; }
    }

    internal sealed class CaptureScope : IDisposable
    {
        private State? _state;

        internal CaptureScope(State state) => _state = state;

        internal Exception? Captured => _state?.Captured;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _state, null);
            if (current is not null)
                Current.Value = current.Prior;
        }
    }
}
