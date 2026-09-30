using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireShowdown;

internal sealed record SpireShowdownSettings(
    [property: JsonPropertyName("connect_code")] string? ConnectCode,
    [property: JsonPropertyName("slippi_path")] string? SlippiPath,
    [property: JsonPropertyName("melee_iso_path")] string? MeleeIsoPath)
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
                ?? new(null, null, null);
        }
        else
        {
            settings = new(null, null, null);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }

        return (settings with
        {
            ConnectCode = Override("SPIRE_SHOWDOWN_CONNECT_CODE", settings.ConnectCode),
            SlippiPath = Override("SPIRE_SHOWDOWN_SLIPPI", settings.SlippiPath),
            MeleeIsoPath = Override("SPIRE_SHOWDOWN_MELEE_ISO", settings.MeleeIsoPath)
        }, path);
    }

    private static string? Override(string name, string? fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
