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

## Remaining proof

The first host-side patch is stored in `slippi-patch/`. It loads the duel JSON,
forces the supplied Direct code and deterministic selections, and sets one
stock. The patch applies cleanly to the inspected upstream commit. A local full
compile is still pending because this Bazzite host does not currently have the
Slippi CMake toolchain or its uninitialized submodules.

Next, extend the injected Melee menu/EXI protocol so a `--spire-duel` launch
enters the online scene and issues the first find-opponent request without user
input. Then build on Bazzite and Windows and confirm an unmodified Slippi client
can connect to the patched build in both same-platform and mixed-platform
matches.
