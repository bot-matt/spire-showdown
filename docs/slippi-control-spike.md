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

Before building distribution binaries, prototype the auto-duel command on
Bazzite and confirm that an unmodified Slippi client can still connect to the
patched build. Then build the same patch with the Windows toolchain and repeat
the mixed-platform test.

