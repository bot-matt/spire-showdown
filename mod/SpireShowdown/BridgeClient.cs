using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SpireShowdown;

public sealed class BridgeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _token;
    private readonly TcpClient _client = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _requests = new(1,1);

    public BridgeClient(string token)
    {
        _token = token;
    }

    public async Task ConnectAsync(ushort port, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync("127.0.0.1", port, cancellationToken);
        // Tiny input packets must not wait for TCP's delayed-ACK/Nagle timer.
        _client.NoDelay = true;
        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public async Task<BridgeResponse> SendAsync<T>(
        string type,
        T? payload,
        CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
        if (_reader is null || _writer is null)
            throw new InvalidOperationException("Bridge client is not connected.");
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Guid.NewGuid().ToString("N");
        var envelope = new BridgeEnvelope<T>(
            BridgeProtocol.Version,
            requestId,
            _token,
            type,
            payload);
        await _writer.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);

        // Once a command is on the wire, consume its reply even if the duel
        // gets cancelled. Otherwise the next cleanup command reads the old
        // reply, loses framing, and leaves Slippi running. Bridge commands are
        // bounded (window discovery is at most 45 seconds).
        using var responseTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var responseLine = await _reader.ReadLineAsync(responseTimeout.Token).ConfigureAwait(false)
            ?? throw new IOException("Bridge closed the connection.");
        var response = JsonSerializer.Deserialize<BridgeResponse>(responseLine, JsonOptions)
            ?? throw new InvalidDataException("Bridge returned an empty response.");
        if (response.RequestId != requestId)
            throw new InvalidDataException("Bridge response did not match the request.");
        return response;
        } catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) {
            // A timed-out/invalid response leaves stream framing uncertain.
            // Never let a later cleanup request consume a stale reply.
            _client.Dispose(); _reader=null; _writer=null;
            throw;
        } finally { _requests.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        // Close the transport first: even if a reader/writer is currently in
        // flight or throws during disposal, the bridge must observe owner EOF.
        _client.Dispose();
        _reader?.Dispose();
        _writer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
