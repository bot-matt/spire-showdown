# StS2 public-beta hook notes

Inspected build: Steam app 2868840, build 24724944, `public-beta`.

The contested relic flow is owned by
`MegaCrit.Sts2.Core.Multiplayer.Game.TreasureRoomRelicSynchronizer`.
After every vote round-trips through the synchronized action queue,
`AwardRelics()` creates a `RelicPickingResult`. For multiple claimants it calls
`RelicPickingResult.GenerateRelicFight`, which resolves RPS synchronously.

The UI subsequently awaits:

```text
NTreasureRoomRelicCollection.AnimateRelicAwards
  -> NHandImageCollection.DoFight(result, holder)
```

`DoFight` returns `Task`, making it the safest vertical-slice interception
point. A Harmony prefix can replace the returned task with an asynchronous
Slippi duel while Godot and StS2 networking keep updating. When the task
finishes, the patch assigns the Slippi winner to `result.player`; vanilla
`AnimateRelicAwards` then performs the actual relic award.

The prefix currently remains gated off until bridge preflight succeeds. All
unsupported cases—including more than two claimants—continue through vanilla
RPS.

