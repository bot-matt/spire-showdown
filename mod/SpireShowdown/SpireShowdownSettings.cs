using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireShowdown;

internal sealed record SpireShowdownSettings(
    [property: JsonPropertyName("connect_code")] string? ConnectCode,
    [property: JsonPropertyName("slippi_path")] string? SlippiPath,
    [property: JsonPropertyName("melee_iso_path")] string? MeleeIsoPath,
    [property: JsonPropertyName("playback_path")] string? PlaybackPath,
    [property: JsonPropertyName("replay_path")] string? ReplayPath,
    [property: JsonPropertyName("controller_mode")] string? ControllerMode)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static (SpireShowdownSettings Settings, string Path) Load()
    {
        var path = System.IO.Path.Combine(Godot.OS.GetUserDataDir(), "spire-showdown.json");
        SpireShowdownSettings settings;
        if (File.Exists(path))
        {
            settings = JsonSerializer.Deserialize<SpireShowdownSettings>(File.ReadAllText(path), Options)
                ?? new(null, null, null, null, null, "auto");
        }
        else
        {
            settings = new(null, null, null, null, null, "auto");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }

        return (settings with
        {
            ConnectCode = Override("SPIRE_SHOWDOWN_CONNECT_CODE", settings.ConnectCode),
            SlippiPath = Override("SPIRE_SHOWDOWN_SLIPPI", settings.SlippiPath),
            MeleeIsoPath = Override("SPIRE_SHOWDOWN_MELEE_ISO", settings.MeleeIsoPath),
            PlaybackPath = Override("SPIRE_SHOWDOWN_PLAYBACK", settings.PlaybackPath),
            ReplayPath = Override("SPIRE_SHOWDOWN_REPLAY", settings.ReplayPath)
        }, path);
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    private static string? Override(string name, string? fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
