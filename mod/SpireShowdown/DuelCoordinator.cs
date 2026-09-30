namespace SpireShowdown;

public sealed class DuelCoordinator
{
    // Melee external character and stage IDs are deliberately centralized;
    // the values must be verified against the Slippi build used by the bridge.
    private static readonly ushort[] LegalStages = [
        0x02, // Fountain of Dreams
        0x03, // Pokemon Stadium
        0x08, // Yoshi's Story
        0x1C, // Dream Land N64
        0x1F, // Battlefield
        0x20  // Final Destination
    ];

    public DuelLifecycle State { get; private set; } = DuelLifecycle.Idle;

    public static (byte FirstCharacter, byte SecondCharacter, ushort Stage) SelectRules(
        ulong seed,
        byte playableCharacterCount)
    {
        if (playableCharacterCount == 0)
            throw new ArgumentOutOfRangeException(nameof(playableCharacterCount));

        var random = new SplitMix64(seed);
        var first = (byte)(random.Next() % playableCharacterCount);
        var second = (byte)(random.Next() % playableCharacterCount);
        var stage = LegalStages[random.Next() % (ulong)LegalStages.Length];
        return (first, second, stage);
    }

    public void Transition(DuelLifecycle next)
    {
        if (!CanTransition(State, next))
            throw new InvalidOperationException($"Invalid duel transition: {State} -> {next}");
        State = next;
    }

    private static bool CanTransition(DuelLifecycle current, DuelLifecycle next) =>
        (current, next) switch
        {
            (DuelLifecycle.Idle, DuelLifecycle.Preflighting) => true,
            (DuelLifecycle.Preflighting, DuelLifecycle.Ready) => true,
            (DuelLifecycle.Ready, DuelLifecycle.Launching) => true,
            (DuelLifecycle.Launching, DuelLifecycle.Connecting) => true,
            (DuelLifecycle.Connecting, DuelLifecycle.Playing) => true,
            (DuelLifecycle.Playing, DuelLifecycle.Completed) => true,
            (DuelLifecycle.Completed or DuelLifecycle.Failed or DuelLifecycle.Cancelled,
                DuelLifecycle.Idle) => true,
            (_, DuelLifecycle.Failed or DuelLifecycle.Cancelled)
                when current is not DuelLifecycle.Idle
                    and not DuelLifecycle.Completed
                    and not DuelLifecycle.Failed
                    and not DuelLifecycle.Cancelled => true,
            _ => false
        };

    private struct SplitMix64(ulong state)
    {
        private ulong _state = state;

        public ulong Next()
        {
            var z = (_state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

