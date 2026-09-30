# Slippi patch

The patches target these inspected upstream revisions:

- `0001-spire-duel-control.patch`: project-slippi/Ishiiruka commit
  `60f7b63496fb6ec7b9180a04f16f3edc0ad89fe2`;
- `0002-spire-duel-autoboot.patch`: project-slippi/slippi-ssbm-asm commit
  `fcf47f10dc244152c2ebaa3a9dec142ea42243b7`.

Together they implement the automatic-duel control path:

- accepts `--spire-duel <duel.json>` without changing normal launches;
- validates the bridge's duel ID, Direct connect code, characters, legal stage,
  and one-stock rule;
- replaces the in-game matchmaking request with the supplied Direct code;
- applies both character selections, the selected stage, and one stock.
- adds a private EXI query that is active only for `--spire-duel` launches;
- jumps directly into Direct mode and starts matchmaking without showing or
  controlling Slippi menus;
- writes `booting`, `connecting`, `ready`, and `completed` status phases next
  to the duel contract, including the winning port index and whether the local
  player won at completion.

The bridge embeds the surface behind the StS2 loading transition, then reports
`ready` when Melee emits its game-info command just before the first match
frames. The overlay removes its cover only at that point.

Apply it from the root of the matching Ishiiruka checkout:

```bash
git apply /path/to/spire-showdown/slippi-patch/0001-spire-duel-control.patch

cd /path/to/slippi-ssbm-asm
git apply /path/to/spire-showdown/slippi-patch/0002-spire-duel-autoboot.patch
gecko build -batched -c netplay.json -defsym "STG_EXIIndex=1"
```

The game-code patch has been assembled locally with all 203 upstream ASM files.
These patches remain experimental until both Bazzite and Windows Slippi builds
pass an actual mixed-platform Direct match.
