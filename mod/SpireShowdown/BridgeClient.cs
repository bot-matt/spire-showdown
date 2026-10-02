using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SpireShowdown;

public sealed class BridgeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _token;
    private readonly TcpClient _client = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public BridgeClient(string token)
    {
        _token = token;
    }

    public async Task ConnectAsync(ushort port, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync("127.0.0.1", port, cancellationToken);
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
        await _writer.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions));

        // Once a command is on the wire, consume its reply even if the duel
        // gets cancelled. Otherwise the next cleanup command reads the old
        // reply, loses framing, and leaves Slippi running. Bridge commands are
        // bounded (window discovery is at most 45 seconds).
        var responseLine = await _reader.ReadLineAsync(CancellationToken.None)
            ?? throw new IOException("Bridge closed the connection.");
        var response = JsonSerializer.Deserialize<BridgeResponse>(responseLine, JsonOptions)
            ?? throw new InvalidDataException("Bridge returned an empty response.");
        if (response.RequestId != requestId)
            throw new InvalidDataException("Bridge response did not match the request.");
        return response;
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
