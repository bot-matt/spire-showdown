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

public sealed record BridgeResponse
{
    [JsonPropertyName("protocol")]
    public ushort Protocol { get; init; }

    [JsonPropertyName("request_id")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    // System.Text.Json does not allow an extension-data property to bind to a
    // positional record constructor parameter. Keep it as a normal property so
    // responses can carry type-specific fields alongside the common envelope.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Data { get; init; }
}

public sealed record DuelSpec(
    [property: JsonPropertyName("duel_id")] string DuelId,
    [property: JsonPropertyName("seed")] ulong Seed,
    [property: JsonPropertyName("opponent_connect_code")] string OpponentConnectCode,
    [property: JsonPropertyName("local_character")] byte LocalCharacter,
    [property: JsonPropertyName("remote_character")] byte RemoteCharacter,
    [property: JsonPropertyName("stage")] ushort Stage,
    [property: JsonPropertyName("stocks")] byte Stocks = 1,
    [property: JsonPropertyName("cpu_test")] bool CpuTest = false,
    [property: JsonPropertyName("cpu_level")] byte CpuLevel = 5,
    [property: JsonPropertyName("lab_view")] bool LabView = true,
    [property: JsonPropertyName("controller_mode")] string ControllerMode = "auto",
    [property: JsonPropertyName("participants")] ArenaParticipant[]? Participants = null,
    [property: JsonPropertyName("local_player_index")] byte LocalPlayerIndex = 0);

public sealed record ArenaParticipant(
    [property: JsonPropertyName("player_id")] ulong PlayerId,
    [property: JsonPropertyName("character")] byte Character,
    [property: JsonPropertyName("connect_code")] string ConnectCode,
    [property: JsonPropertyName("endpoint")] string? Endpoint);

public sealed record ControllerState(bool Connected, ulong Sequence, ushort Buttons,
    sbyte Sx, sbyte Sy, sbyte Cx, sbyte Cy, byte Tl, byte Tr);

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
        return raw;
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
