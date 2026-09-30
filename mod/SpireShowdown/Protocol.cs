using System.Text.Json.Serialization;

namespace SpireShowdown;

public static class BridgeProtocol
{
    public const ushort Version = 1;
}

public sealed record BridgeEnvelope<T>(
    [property: JsonPropertyName("protocol")] ushort Protocol,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] T? Payload = default);

public sealed record BridgeResponse(
    [property: JsonPropertyName("protocol")] ushort Protocol,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonExtensionData] Dictionary<string, object>? Data);

public sealed record DuelSpec(
    [property: JsonPropertyName("duel_id")] string DuelId,
    [property: JsonPropertyName("seed")] ulong Seed,
    [property: JsonPropertyName("opponent_connect_code")] string OpponentConnectCode,
    [property: JsonPropertyName("local_character")] byte LocalCharacter,
    [property: JsonPropertyName("remote_character")] byte RemoteCharacter,
    [property: JsonPropertyName("stage")] ushort Stage,
    [property: JsonPropertyName("stocks")] byte Stocks = 1);

public enum DuelLifecycle
{
    Idle,
    Preflighting,
    Ready,
    Launching,
    Connecting,
    Playing,
    Completed,
    Failed,
    Cancelled
}

