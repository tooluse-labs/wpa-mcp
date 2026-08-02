using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace WpaMcp.Core;

internal enum JsonRpcIngressRejection
{
    None,
    FrameLimit,
    RequestIdLimit,
    ByteOrderMark,
    UnterminatedFrame,
}

internal sealed record JsonRpcRequestFrameOptions(int MaxFrameBytes)
{
    internal const int DefaultMaxFrameBytes = 100_000;
    internal const int HardMaxFrameBytes = 100_000;
    internal const int MinimumFrameBytes = 4_096;
    internal const string EnvironmentVariable = "WPAMCP_MAX_JSON_RPC_REQUEST_BYTES";

    internal static JsonRpcRequestFrameOptions FromEnvironment(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var raw = getEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return new(DefaultMaxFrameBytes);
        if (!int.TryParse(raw, out var parsed) ||
            parsed is < MinimumFrameBytes or > HardMaxFrameBytes)
        {
            throw new ToolsListStartupValidationException(
                $"{EnvironmentVariable} must be an integer from {MinimumFrameBytes} through {HardMaxFrameBytes}.");
        }
        return new(parsed);
    }
}

/// <summary>
/// Holds each NDJSON request until its complete raw UTF-8 frame is known to fit.
/// The terminating LF (and any preceding CR) counts toward the complete frame.
/// A rejected frame is never returned to the SDK, so no partial request can be
/// deserialized. EOF with an unterminated frame fails closed.
/// </summary>
internal sealed class JsonRpcFrameLimitingStream : Stream
{
    internal const string RejectionMessage = "wpa-mcp: JSON-RPC request frame limit exceeded";
    internal const string RequestIdRejectionMessage = "wpa-mcp: JSON-RPC request id limit exceeded";

    private readonly Stream _inner;
    private readonly int _payloadLimit;
    private readonly byte[] _inputBuffer = new byte[8192];
    private readonly MemoryStream _pendingFrame = new();
    private byte[]? _approvedFrame;
    private int _approvedOffset;
    private int _inputOffset;
    private int _inputCount;
    private int _payloadBytes;
    private byte _bomFirst;
    private byte _bomSecond;
    private JsonRpcIngressRejection _rejection;

    internal JsonRpcFrameLimitingStream(Stream inner, JsonRpcRequestFrameOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxFrameBytes is < JsonRpcRequestFrameOptions.MinimumFrameBytes or
            > JsonRpcRequestFrameOptions.HardMaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(options));
        _payloadLimit = options.MaxFrameBytes;
    }

    internal bool Rejected => _rejection != JsonRpcIngressRejection.None;
    internal JsonRpcIngressRejection Rejection => _rejection;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The destination range is outside the buffer.");
        if (count == 0)
            return 0;
        Prime();
        return CopyApprovedBytes(buffer.AsSpan(offset, count));
    }

    private void Prime()
    {
        while (!Rejected && !HasApprovedBytes())
        {
            if (_inputOffset == _inputCount)
            {
                _inputCount = _inner.Read(_inputBuffer, 0, _inputBuffer.Length);
                _inputOffset = 0;
                if (_inputCount == 0)
                {
                    ApproveEndOfInput();
                    break;
                }
            }
            ProcessBufferedInput();
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;
        await PrimeAsync(cancellationToken).ConfigureAwait(false);
        return CopyApprovedBytes(buffer.Span);
    }

    internal async ValueTask PrimeAsync(CancellationToken cancellationToken = default)
    {
        while (!Rejected && !HasApprovedBytes())
        {
            if (_inputOffset == _inputCount)
            {
                _inputCount = await _inner.ReadAsync(
                    _inputBuffer,
                    cancellationToken).ConfigureAwait(false);
                _inputOffset = 0;
                if (_inputCount == 0)
                {
                    ApproveEndOfInput();
                    break;
                }
            }
            ProcessBufferedInput();
        }
    }

    private bool HasApprovedBytes() =>
        _approvedFrame is not null && _approvedOffset < _approvedFrame.Length;

    private int CopyApprovedBytes(Span<byte> destination)
    {
        if (Rejected || !HasApprovedBytes() || destination.IsEmpty)
            return 0;
        var count = Math.Min(destination.Length, _approvedFrame!.Length - _approvedOffset);
        _approvedFrame.AsSpan(_approvedOffset, count).CopyTo(destination);
        _approvedOffset += count;
        if (_approvedOffset == _approvedFrame.Length)
        {
            _approvedFrame = null;
            _approvedOffset = 0;
        }
        return count;
    }

    private void ProcessBufferedInput()
    {
        while (_inputOffset < _inputCount && !Rejected && !HasApprovedBytes())
        {
            var value = _inputBuffer[_inputOffset++];
            _pendingFrame.WriteByte(value);
            CountFrameByte();
            if (Rejected)
                return;
            InspectByte(value);
            if (value == (byte)'\n' && !Rejected)
            {
                if (HasOversizedRequestId())
                    Reject(JsonRpcIngressRejection.RequestIdLimit);
                else
                    ApprovePendingFrame();
            }
        }
    }

    private void InspectByte(byte value)
    {
        if (_bomFirst == 0xEF && _bomSecond == 0xBB && value == 0xBF)
        {
            Reject(JsonRpcIngressRejection.ByteOrderMark);
            return;
        }
        _bomFirst = _bomSecond;
        _bomSecond = value;

    }

    private void ApprovePendingFrame()
    {
        _approvedFrame = _pendingFrame.ToArray();
        _approvedOffset = 0;
        _pendingFrame.SetLength(0);
        _payloadBytes = 0;
        _bomFirst = 0;
        _bomSecond = 0;
    }

    private void ApproveEndOfInput()
    {
        if (_pendingFrame.Length > 0)
            Reject(JsonRpcIngressRejection.UnterminatedFrame);
    }

    private void CountFrameByte()
    {
        _payloadBytes++;
        if (_payloadBytes > _payloadLimit)
            Reject(JsonRpcIngressRejection.FrameLimit);
    }

    private bool HasOversizedRequestId()
    {
        var buffer = _pendingFrame.GetBuffer();
        var length = checked((int)_pendingFrame.Length);
        if (length > 0 && buffer[length - 1] == (byte)'\n')
            length--;
        if (length > 0 && buffer[length - 1] == (byte)'\r')
            length--;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("id", out var id) ||
                id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                return false;
            }
            var requestId = id.Deserialize<RequestId>(McpJsonUtilities.DefaultOptions);
            return ToolRequestIdPolicy.SerializedBytes(requestId) > ToolRequestIdPolicy.MaxSerializedBytes;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
        {
            // Malformed JSON and invalid ID kinds remain the SDK's protocol-error
            // responsibility; this guard only owns the decoded size policy.
            return false;
        }
    }

    private void Reject(JsonRpcIngressRejection rejection)
    {
        _rejection = rejection;
        _pendingFrame.SetLength(0);
        _approvedFrame = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pendingFrame.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _pendingFrame.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
