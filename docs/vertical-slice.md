# Vertical slice plan

## Player-visible flow

1. Two players contest the same relic.
2. The StS2 host creates a duel ID and seed and pauses only run progression.
3. Both mods ask their local bridge to run preflight.
4. If both clients are ready, the host selects characters and a legal stage
   from the shared seed.
5. Each bridge launches Slippi and embeds the render window in the StS2 duel
   panel.
6. Both players enter Direct mode automatically and play one stock.
7. Both bridges report the same result and replay fingerprint.
8. The host awards the relic and closes the overlays.
9. Any failure before a verified result falls back to normal RPS.

## State machine

```text
idle -> preflighting -> ready -> launching -> connecting -> playing
                                                         -> completed
       any nonterminal state -> failed | cancelled
       failed | cancelled | completed -> idle
```

## Host authority

The StS2 multiplayer host owns the duel seed and final relic award. Client
bridges never mutate run state. A result is accepted only for the active duel
ID and expected players. During the first cooperative release, matching result
reports are sufficient; complete replay exchange can be added for adversarial
validation later.

## Platform behavior

### Bazzite

Launch the temporary Dolphin Qt/render window through XWayland, locate it by
PID and window properties, remove decorations, and re-parent it into an X11
child surface owned by the Godot overlay. If the compositor rejects embedding,
use a precisely aligned borderless overlay and restore the user's previous
display state afterward.

### Windows

Locate Dolphin's top-level render window by PID, remove top-level decorations,
call `SetParent`, size it to the Godot-provided native child handle, and restore
the original window style/parent when the duel ends. Borderless placement is
the fallback.

## Rules

- Stocks: 1
- Items: off
- Characters: independently selected from the full playable roster by the
  host's duel seed
- Stage: selected by the same seed from Battlefield, Final Destination, Dream
  Land, Fountain of Dreams, Pokemon Stadium, and Yoshi's Story
- Slippi mode: Direct

The host sends explicit selections. Clients do not independently press Melee's
Random buttons.

## Milestones

### M1: Local bridge

- Discovery and file picker fallback
- ISO revision/hash validation
- Loopback protocol
- Slippi launch/exit monitoring
- Replay/live event result reader

### M2: Embedding

- Godot overlay returns a native parent handle and bounds
- XWayland backend on Bazzite
- Win32 backend on Windows
- Focus and controller handoff
- Borderless fallback on both platforms

### M3: Automatic Direct mode

- Inspect current Slippi configuration and internal control paths
- Prefer a supported or stable stock-Slippi mechanism
- Otherwise add a minimal command-line/control patch to Slippi Dolphin
- Never depend on timing-based keyboard macros for release builds

### M4: StS2 integration

- Patch the contested-relic RPS entry point
- Add versioned multiplayer messages
- Keep networking and rendering active while progression is locked
- Award the relic only on the host
- Restore normal RPS on every failure path

### M5: Distribution

- Windows x64 bridge installer
- Bazzite x86_64 AppImage
- Steam Workshop mod
- First-run controller and path diagnostics
- Windows/Windows, Bazzite/Bazzite, and mixed-platform test matrix

## First acceptance test

From a debug button in StS2 on both Bazzite and Windows, launch a one-stock
Direct match inside the overlay, finish the stock, detect the winner, close
Dolphin, and return both clients to the same connected StS2 run.

