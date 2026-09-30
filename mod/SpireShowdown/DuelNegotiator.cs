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

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(DuelId);
        writer.WriteString(ConnectCode);
    }

    public void Deserialize(PacketReader reader)
    {
        DuelId = reader.ReadString();
        ConnectCode = reader.ReadString();
    }
}

internal static class DuelNegotiator
{
    public static async Task<string> ExchangeConnectCodesAsync(
        string duelId,
        string localConnectCode,
        ulong remotePlayerId,
        CancellationToken cancellationToken)
    {
        var service = RunManager.Instance.NetService;
        if (!service.IsConnected)
            throw new InvalidOperationException("The Slay the Spire multiplayer service is disconnected.");

        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MessageHandlerDelegate<SlippiConnectCodeMessage> handler = (message, senderId) =>
        {
            if (senderId == remotePlayerId
                && message.DuelId == duelId
                && IsConnectCode(message.ConnectCode))
                completion.TrySetResult(message.ConnectCode);
        };

        service.RegisterMessageHandler(handler);
        try
        {
            service.SendMessage(new SlippiConnectCodeMessage
            {
                DuelId = duelId,
                ConnectCode = localConnectCode
            });
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            service.UnregisterMessageHandler(handler);
        }
    }

    private static bool IsConnectCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 9
        && value.Contains('#', StringComparison.Ordinal);
}
