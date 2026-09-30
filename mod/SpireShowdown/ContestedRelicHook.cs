using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace SpireShowdown;

/// <summary>
/// Intercepts only the asynchronous presentation of a two-player relic fight.
/// StS2's network and main loop continue running while the replacement task is
/// incomplete. Returning true preserves vanilla RPS on every unavailable or
/// unsupported path.
/// </summary>
[HarmonyPatch(typeof(NHandImageCollection), nameof(NHandImageCollection.DoFight))]
internal static class ContestedRelicHook
{
    [HarmonyPrefix]
    private static bool BeforeDoFight(
        RelicPickingResult result,
        NTreasureRoomRelicHolder holder,
        ref Task __result)
    {
        if (result.fight?.playersInvolved.Count != 2 || !DuelRuntime.CanStart)
            return true;

        __result = DuelRuntime.RunAsync(result, holder);
        return false;
    }
}
