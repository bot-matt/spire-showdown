using System.Text.Json;
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

public sealed record Bounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] uint Width,
    [property: JsonPropertyName("height")] uint Height);

public static class BridgeResponseExtensions
{
    public static void Require(this BridgeResponse response, string expectedType)
    {
        if (response.Type == "error")
        {
            var message = response.String("message") ?? "Unknown bridge error";
            throw new InvalidOperationException(message);
        }
        if (response.Type != expectedType)
            throw new InvalidDataException($"Expected bridge response '{expectedType}', got '{response.Type}'.");
    }

    public static string? String(this BridgeResponse response, string key) =>
        response.Element(key)?.GetString();

    public static bool? Boolean(this BridgeResponse response, string key) =>
        response.Element(key) is { ValueKind: JsonValueKind.True } ? true
        : response.Element(key) is { ValueKind: JsonValueKind.False } ? false
        : null;

    public static JsonElement? Element(this BridgeResponse response, string key)
    {
        if (response.Data is null || !response.Data.TryGetValue(key, out var raw))
            return null;
        return raw is JsonElement element ? element : null;
    }
}

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
