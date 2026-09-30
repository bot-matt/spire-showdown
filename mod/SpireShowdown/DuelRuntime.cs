using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace SpireShowdown;

internal static class DuelRuntime
{
    /// <summary>
    /// Remains false until the bridge has passed preflight and both contestants
    /// have negotiated a duel. This makes an installed development build fall
    /// back to vanilla RPS instead of blocking a run.
    /// </summary>
    public static bool CanStart => false;

    public static Task RunAsync(
        RelicPickingResult result,
        NTreasureRoomRelicHolder holder)
    {
        // The bridge-backed implementation will:
        // 1. show the Godot duel overlay;
        // 2. await Slippi completion without blocking StS2 networking;
        // 3. map the winning StS2 net ID back to result.fight.playersInvolved;
        // 4. assign result.player and clear the RPS rounds;
        // 5. close the overlay and let AnimateRelicAwards award the relic.
        throw new InvalidOperationException("DuelRuntime cannot run before bridge preflight.");
    }
}

