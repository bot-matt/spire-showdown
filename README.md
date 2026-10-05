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

On the main menu, choose **Controller Settings** beneath the version badge.
**Auto-detect** follows the last gamepad used in Spire; **GameCube USB adapter**
keeps Slippi's native adapter mode. The choice is saved and applied before each
duel, so changing it does not require reinstalling the mod.

On Bazzite, add `--display-driver x11` to Slay the Spire 2's Steam launch
options. The embedded Slippi window uses X11/XWayland re-parenting, which is not
available when the game itself runs as a native Wayland surface.

## Guided installation

### Experimental Melee Unlocked arena (alpha.17)

Starting with alpha.14, `install-bazzite.sh` and `install-windows.ps1` install
the embedded Melee Unlocked arena. The explicit `install-arena-*` aliases do
the same thing. Both require the matching arena ZIP and `SHA256SUMS` from
alpha.14 or newer; older releases do not contain that payload.

On another Bazzite machine, the installer discovers Steam libraries (including
external drives), native Spire, and Steam's Proton. Install Proton Experimental
through Steam's Tools library if it is missing. The script does not install
system-wide Wine or compilers. Windows uses the native arena executable and
does not require Proton. Both installers download the matching platform mod
and arena, verify checksums, back up replaced files, and preserve settings.
Each machine needs BaseLib and its own legally dumped Melee NTSC-U 1.02 ISO.
Slippi login is needed for online play, not for the CPU test.

Run the downloaded script with an
explicit release tag to keep both machines on the same version:

```bash
bash ./install-bazzite.sh --release v0.2.0-alpha.17
```

```powershell
powershell -ExecutionPolicy Bypass -File .\install-windows.ps1 -Release v0.2.0-alpha.17
```

The Bazzite script prints the required `--display-driver x11` Steam option.
Alpha.17 uses Melee Unlocked 0.8.75, decouples controller forwarding from UI/status
polling, and follows the monitor refresh rate with predictive presentation.
Windows installer execution and live cross-machine multiplayer remain
unverified. See `melee-unlocked-patch/README.md` for arena verification status.

Alpha.15 fixes neutral Spire samples overriding native input in Auto mode and
accounts for keyboard focus in embedded Windows windows. On Linux, Auto and
GameCube adapter modes read supported USB adapters directly using libusb;
Proton no longer owns adapter USB access. Adapter mode shows detection or USB
permission errors in the arena. Physical adapter operation still needs testing.
Controller setting changes apply to the next arena.
Linux Auto/Spire keyboard input is sampled on the native X11 side: Proton may
report no foreground HWND after reparenting. Keys are forwarded only while
Spire or one of its embedded arena descendants has focus.

Arena fatal errors now return to Spire with a readable diagnostic; missing-asset
panics include the filename. Relative Sys/Lab paths avoid narrow-character
Windows username paths. These are safeguards, not a confirmed diagnosis of the
reported Windows multiplayer crash. Local two-peer engine tests pass; live
Windows/Bazzite cross-machine play remains unverified.

Alpha.16 also samples Spire's actual `controller_*` action states and left-stick
strategy. Steam Input sends these action events rather than raw Godot joypad
buttons; they are now forwarded even with no usable raw joypad device. Auto and
Spire modes combine these with keyboard/physical-pad inputs. The arena footer
shows the forwarded source, buttons and stick for diagnosis without logs.
Controller actions are consumed by the overlay so they do not operate the
underlying Spire menu during a match. Native mode bypasses this forwarding;
choose Auto for Steam Input. Spire's action layout does not expose every raw
Steam-controller axis, so full custom C-stick/analog-trigger support is not
claimed for that path.
On Bazzite, Auto/Spire restores focus to the Spire parent when focus moves into
its Wine arena child, because Spire pauses its controller updater on focus loss.
It does not reclaim focus from other applications; Native mode is unaffected.

CPU tests and multiplayer both force item frequency None and clear the complete
item mask. The real game-start event is checked before READY; a match with items
enabled is rejected instead of silently using different rules.

### Existing Dolphin backend

The previous installers are preserved as `install-slippi-bazzite.sh` and
`install-slippi-windows.ps1`; they are not the alpha.14 default.

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
It maps Player 1 to the connected Steam/evdev gamepad and enables input while
Slippi is embedded.

### Windows

Right-click `install-windows.ps1` and choose **Run with PowerShell**, or run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
```

The Windows installer finds Steam libraries, expands the portable patched
Slippi build, configures the mod, checks BaseLib, and runs bridge preflight.
It maps Player 1 to the first XInput gamepad and enables background input.
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
