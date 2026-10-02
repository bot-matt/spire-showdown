using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace SpireShowdown;

public struct SlippiConnectCodeMessage : INetMessage
{
    public string DuelId;
    public string ConnectCode;
    public string Kind;

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    // A connect code is valid only for the currently awaited relic duel.
    // Buffering it into later synchronized scenes can poison StS2's
    // multiplayer state/replay stream after the duel has ended.
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(DuelId);
        writer.WriteString(ConnectCode);
        writer.WriteString(Kind);
    }

    public void Deserialize(PacketReader reader)
    {
        DuelId = reader.ReadString();
        ConnectCode = reader.ReadString();
        Kind = reader.ReadString();
    }
}

// All duel messages use one registered handler for the entire session. In
// particular, a faster client must not lose messages before its peer awaits them.
internal sealed class DuelNegotiator : IDisposable
{
    private readonly string _duelId;
    private readonly string _code;
    private readonly Action<SlippiConnectCodeMessage> _send;
    private readonly Action _unregister;
    private readonly CancellationTokenSource _remoteCancelled = new();
    private readonly TaskCompletionSource<string> _remoteCode = new();
    private readonly TaskCompletionSource<string> _remoteOutcome = new();
    private readonly TaskCompletionSource<bool> _codeAck = new();
    private readonly TaskCompletionSource<bool> _outcomeAck = new();
    private string? _localOutcome;

    public CancellationToken RemoteCancelled => _remoteCancelled.Token;

    public DuelNegotiator(string duelId, string localConnectCode, ulong remotePlayerId)
    {
        _duelId = duelId;
        _code = localConnectCode;
        var service = RunManager.Instance.NetService;
        if (!service.IsConnected)
            throw new InvalidOperationException("The Slay the Spire multiplayer service is disconnected.");

        _send = message => service.SendMessage(message);
        MessageHandlerDelegate<SlippiConnectCodeMessage> handler = (message, senderId) =>
        {
            if (senderId != remotePlayerId || message.DuelId != duelId)
                return;
            switch (message.Kind)
            {
                case "code":
                case "code_ack":
                    if (!IsConnectCode(message.ConnectCode)) return;
                    _remoteCode.TrySetResult(message.ConnectCode);
                    if (message.Kind == "code_ack") _codeAck.TrySetResult(true);
                    else Send("code_ack", _code);
                    break;
                case "outcome":
                    _remoteOutcome.TrySetResult(message.ConnectCode);
                    Send("outcome_ack", message.ConnectCode);
                    if (message.ConnectCode == "cancel") _remoteCancelled.Cancel();
                    break;
                case "outcome_ack":
                    if (message.ConnectCode == _localOutcome) _outcomeAck.TrySetResult(true);
                    break;
            }
        };
        service.RegisterMessageHandler(handler);
        _unregister = () => service.UnregisterMessageHandler(handler);
    }

    private void Send(string kind, string value) => _send(new SlippiConnectCodeMessage
    { DuelId = _duelId, ConnectCode = value, Kind = kind });

    public async Task<string> ExchangeConnectCodesAsync(CancellationToken cancellationToken)
    {
        while (!_remoteCode.Task.IsCompleted || !_codeAck.Task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send("code", _code);
            await Task.Delay(250, cancellationToken);
        }
        return await _remoteCode.Task;
    }

    // A winner is committed only after both clients independently report the
    // same player. Cancellation or disagreement makes both use the original RPS.
    public async Task<bool> AgreeOutcomeAsync(ulong? winner, CancellationToken cancellationToken)
    {
        _localOutcome = winner?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "cancel";
        while (!_remoteOutcome.Task.IsCompleted || !_outcomeAck.Task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send("outcome", _localOutcome);
            await Task.Delay(250, cancellationToken);
        }
        return _localOutcome != "cancel" && !_remoteCancelled.IsCancellationRequested
            && await _remoteOutcome.Task == _localOutcome;
    }

    public void Dispose()
    {
        _unregister();
        _remoteCancelled.Dispose();
    }

    private static bool IsConnectCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 9
        && value.Contains('#', StringComparison.Ordinal);
}
