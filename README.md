# Spire Showdown

Spire Showdown turns a contested relic in Slay the Spire 2 multiplayer into a
one-stock Slippi Melee duel, then returns the winner to the still-running run.

The project is intentionally split into two pieces:

- `mod/` is the C#/Godot Slay the Spire 2 mod.
- `bridge/` is a small native companion that discovers Slippi and the user's
  Melee ISO, launches the duel, embeds its window, and reports the result.

No game image, Nintendo asset, or Slippi binary is included.

## Current status

The current vertical slice includes:

- a versioned, newline-delimited JSON protocol;
- an authenticated local bridge server;
- Linux/Bazzite and Windows Slippi/ISO discovery;
- preflight diagnostics and ISO fingerprinting;
- a tested duel lifecycle state machine;
- a StS2/BaseLib runtime that owns the bridge and a full-screen duel transition;
- a validated Harmony interception point for two-player contested relics;
- managed Slippi launch, status, cancellation, and shutdown;
- native X11/XWayland re-parenting on Bazzite;
- native Win32 child-window embedding with parent/style restoration;
- patched Slippi auto-boot into Direct matchmaking with no visible menus;
- a readiness/result sidecar that keeps the StS2 transition up until Melee is
  entering the match and reports the winning port afterward;
- automatic local Slippi connect-code discovery and reliable code exchange over
  StS2 multiplayer;
- deterministic shared random characters/stage and verified local-winner
  mapping;
- automatic fallback to the original RPS animation on setup or duel failure.

The bridge implementation and tests compile for Linux and Windows. Native
embedding still needs an end-to-end test with the Godot overlay and a patched
Slippi build on both operating systems.

The current StS2 public-beta mod compiles against build 24724944 with no
warnings. Its interception is gated on bridge preflight. Missing Slippi, ISO,
login, peer negotiation, launch, or result data returns to vanilla RPS.

Source inspection confirmed stock Slippi has no external control surface for
automatically entering Direct mode. The narrow `--spire-duel` host and
game-code patches in `slippi-patch/` provide that path without input automation
and leave normal Slippi launches untouched. The game-code patch builds, but an
end-to-end two-machine match with patched builds is still required, so this is
not a playable release yet.

At first startup, the mod creates `spire-showdown.json` in the StS2 Godot user
data directory. Slippi, ISO, and connect code are normally auto-detected. The
file accepts `slippi_path`, `melee_iso_path`, and `connect_code` overrides when
automatic discovery cannot find one of them.

On Bazzite, add `--display-driver x11` to Slay the Spire 2's Steam launch
options. The embedded Slippi window uses X11/XWayland re-parenting, which is not
available when the game itself runs as a native Wayland surface.

## Guided installation

Download the platform mod artifact and matching patched Slippi artifact from
GitHub Actions, then extract the mod artifact. Each platform bundle contains a
guided installer next to its `SpireShowdown` payload:

### Bazzite

```bash
chmod +x install-bazzite.sh
./install-bazzite.sh
```

The installer finds the game, patched Slippi ZIP, Slippi-configured Melee ISO,
and login data; installs the mod; writes `spire-showdown.json`; and runs bridge
preflight. It prints the required `--display-driver x11` Steam option afterward.

### Windows

Right-click `install-windows.ps1` and choose **Run with PowerShell**, or run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
```

The Windows installer finds Steam libraries, expands the portable patched
Slippi build, configures the mod, checks BaseLib, and runs bridge preflight.
Both computers must use the same StS2 branch and enabled gameplay-mod list.

The manual `patched-slippi-build` GitHub workflow builds pinned, reproducible
Bazzite AppImage and Windows portable artifacts with both patches applied. It
is intentionally manual because the upstream emulator builds are large.

## Bridge quick start

```bash
cd bridge
cargo test
cargo run -- doctor
cargo run -- serve --port 0 --token development-only
```

Paths can be supplied explicitly during development:

```bash
cargo run -- doctor --slippi /path/to/Slippi_Dolphin --iso /path/to/melee.iso
```

Or through environment variables:

```bash
export SPIRE_SHOWDOWN_SLIPPI=/path/to/Slippi_Dolphin
export SPIRE_SHOWDOWN_MELEE_ISO=/path/to/melee.iso
```

## Safety behavior

The bridge binds only to loopback and requires a per-session token. A failed
preflight, launch, connection, or result check must return control to StS2's
normal RPS flow; it must never strand a multiplayer run.

See `docs/vertical-slice.md` for the implementation sequence and acceptance
criteria.
