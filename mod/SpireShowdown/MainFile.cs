using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SpireShowdown;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SpireShowdown";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(
        ModId,
        MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var harmony = new Harmony(ModId);
        harmony.PatchAll(assembly);
        _ = InitializeRuntimeAsync();
        Logger.Info("Spire Showdown initialized; bridge preflight is running.");
    }

    private static async Task InitializeRuntimeAsync()
    {
        try
        {
            await DuelRuntime.InitializeAsync();
        }
        catch (Exception error)
        {
            Logger.Warn($"Spire Showdown is unavailable; vanilla RPS remains active: {error.Message}");
        }
    }
}
