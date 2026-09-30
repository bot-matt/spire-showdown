# Slippi Direct-control spike

Inspected upstream: `project-slippi/Ishiiruka`, `slippi` branch, September 2026.

## Finding

Stock Slippi Dolphin does not expose command-line options for entering Direct
mode, supplying an opponent connect code, or selecting match rules. Its current
CLI can launch an ISO (`--exec`), run in batch mode, choose audio/video options,
select a user directory, and configure the spectator port.

Direct matchmaking begins only when the injected Melee online menu sends a
find-match command to `CEXISlippi::startFindMatch`. The opponent code is carried
in that command's payload. Slippi stores recent Direct codes internally, but
that history is not an external automation API.

## Decision

Release builds should use a small Slippi patch, not focus-sensitive keyboard or
controller macros. The patch should add one option:

```text
--spire-duel=/absolute/path/to/duel.json
```

The file contains the authenticated duel ID, opponent code, character, stage,
stock count, and result endpoint. The patch will preload that data and enter a
dedicated auto-duel path in the injected online code. Normal Slippi behavior is
unchanged when the option is absent.

Maintaining this as a narrow patch also gives us reliable one-stock rules and
deterministic character/stage selection without modifying the user's ordinary
Slippi configuration.

## Implemented proof

The host-side and injected-game patches are stored in `slippi-patch/`. They load
the duel JSON, force the supplied Direct code and deterministic selections, set
one stock, enter Direct mode, and issue the find-opponent request without user
input. Both patches apply cleanly to their pinned upstream revisions.

The modified game code assembled successfully with all 203 source files and
generated both US and Japanese Slippi configurations. The host patch also emits
a sidecar status handshake so StS2 can hide boot and matchmaking, reveal the
embedded surface at game start, and read the winning port.

## Remaining proof

Build the patched Ishiiruka application on Bazzite and Windows, connect two
machines, and verify the reveal lands on the in-game `READY... GO!` sequence in
same-platform and mixed-platform matches. A local full Ishiiruka compile is
still pending because its submodules and CMake build dependencies are not
installed in this workspace.
