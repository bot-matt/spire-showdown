# Melee Unlocked arena integration (development)

This is the experimental default backend starting with **alpha.14**.
A stock Melee Unlocked executable is deliberately rejected:
the bridge needs the boot, readiness, control and result contract below.

Pinned upstream: `Hero88go/melee-unlocked` at
`49db07c4c46644c64afeacb1cf285604e728cfaa` (0.8.75).
Use **Static Recomp**, not Source Port: upstream disables Lab view for Source Port.
No ISO, extracted DOL, or generated retail translation sources belong in this repo.

## Windows build

Run in a Visual Studio x64 developer shell with CMake, Ninja, Python and Git:

```powershell
git clone https://github.com/Hero88go/melee-unlocked.git melee-unlocked
cd melee-unlocked
git checkout 49db07c4c46644c64afeacb1cf285604e728cfaa
git submodule update --init sourceport/extern/melee
git apply --ignore-whitespace ../spire-showdown/melee-unlocked-patch/integration.patch
Copy-Item ../spire-showdown/melee-unlocked-patch/spire_arena.* port/runtime/host/
python tools/extract_dol.py 'PATH-TO-YOUR-NTSC-1.02.iso' build/main.dol
python port/recomp/recomp.py --dol build/main.dol --gct-base 0x8065CC80
cmake -S . -B build-spire -G Ninja -DMELEE_BUILD_EXPERIMENTAL_PORT=ON -DMELEE_CPU_BASELINE=SSE2 -DCMAKE_BUILD_TYPE=Release
cmake --build build-spire --target melee_port
git clone https://github.com/frankborden/slippilab.git ../slippilab
git -C ../slippilab checkout ba4e09172344d1e7957de2d8a6eebab31632d92f
python tools/build_lab_assets.py --slippilab ../slippilab --out build-spire/Lab
```

SSE2 avoids an AVX2-only package excluding older computers. Linux uses this
Windows executable through a private Proton prefix, with the native Linux bridge
performing X11 reparenting. Building on Linux is a developer-only MSVC-Wine setup;
the Bazzite installer must never download a compiler or require Toolbox.

## Package contract

The archive `spire-showdown-arena-Windows-x86_64.zip` must contain:

```text
arena/
  melee_port.exe
  dxr_pathtrace.dxil
  SpireArena.json
  Sys/GameFiles/GALE01/...
  Lab/*.lab
  required redistributable runtime DLLs
  licenses/... (upstream, third-party, Slippi Lab and redistributable notices)
```

`SpireArena.json` requires `protocol: 1` and `engine: "static_recomp"`; include
the pinned upstream revision and integration build version as well. Publish this
asset together with both platform mod ZIPs and a `SHA256SUMS` file listing each
archive. The platform installers pin one release tag for all downloads; they do
not combine a cached engine ZIP with a newer mod DLL.

New installation entry points are `install/install-arena-bazzite.sh` and
`install/install-arena-windows.ps1`. The previous Dolphin installers remain
available during development. The new scripts cannot work against an older
release without the arena asset/checksums, and stop before replacing files.

## Runtime contract

`--spire-duel ABSOLUTE-PATH` reads the bridge's one-stock duel JSON. The engine
atomically writes `PATH.status.json`, including `duel_id`, `phase`, `winner_idx`
and `local_won`. A non-CPU match supplies 2-4 canonical participants.

The engine validates actual game-start players, stage and stocks, then announces
`ready` and holds before play until `PATH.control.json` has matching `duel_id`
and `resume: true`. The bridge writes resume only after successful window reveal.
Control also supports cancellation, live Lab view and a recent controller sample.
`volume_percent` follows Spire's squared master/SFX gain, clamped to 0–100;
the engine starts muted until it receives that gain. Arena launches disable
Melee's jukebox music. GameCube-adapter mode retains native hardware input on
Windows; Linux reads USB in the Spire process and forwards tagged samples.
Auto/Spire modes also forward keyboard controls alongside gamepad input:
arrows/WASD move, Z attacks, X specials, Space jumps, Q shields, E grabs,
and Enter pauses. Native mode focuses the arena for its own bindings.
Samples older than 250 ms fall back to native inputs instead of holding stale
forwarded buttons. Neutral Auto samples also preserve native input; strict
Spire mode accepts neutral samples to release its forwarded buttons.
An absent/null controller sample never blocks resume or cancellation. Viewport
controls synchronize Wine's Win32 swap-chain size with the native embedded
surface; resizing only the X11 child can otherwise clip the game.
Embedded arenas bypass standalone monitor work-area sizing limits. Spire uses
the animated frame's actual UI-to-client transform for placement, including
letterboxing, rather than raw window percentages. While a foreign child is
attached, the mod suspends only Godot's X11 SubstructureNotify subscription,
then restores it after cleanup; foreign resize events must not resize Godot's
cached main viewport.
Stock upstream runtime defaults and memory-card prompts are not a readiness signal.

Alpha.17 moves input RPCs off Godot's UI synchronization context, disables TCP
Nagle buffering on both ends, and samples controllers every active Spire frame.
Status is polled separately at 10 Hz; unchanged viewport/options are not reapplied.
The arena follows the monitor refresh rate with predictive presentation, avoiding
the extra frame of delayed interpolation. Gameplay and netplay remain 60 Hz.
This changes local transport/presentation, not Slippi's negotiated online delay.

Two players use Slippi Direct. Three/four players use upstream experimental
`--local-peer` with explicit reachable IPv4 UDP endpoints; this is not automatic
internet matchmaking and may need forwarding. Spire contenders exchange backend,
version, readiness and winner; spectators wait for committed results.

## Verification so far

- C# mod compiles against the installed game, with no warnings.
- Rust bridge unit tests and simulated 2-4 contender agreement tests pass.
- Bazzite installer offline tests cover update, backup, retained settings,
  checksum rejection and invalid existing configuration.
- The patched executable has built with MSVC through isolated Proton.
- A real one-stock CPU fight reaches ready, renders Lab view inside a disposable
  native X11 parent, reports Fox's win, and exits on cancellation.
- The actual bridge launches the patched CPU engine, reports its real result,
  resets and launches a second fight in the same session, then cancels/reset.
  Lab-view reveal is now visually verified in a mapped native test parent,
  including the controllerless mailbox and synchronized viewport dimensions.
- An isolated offline copy of the actual installed Spire (separate save data,
  `--force-steam=off`) runs F8, attaches the arena as a native child, receives
  the CPU result and restores the menu. Godot framebuffer captures verify the
  summon animation, result text and restored UI. Those framebuffer captures
  exclude foreign native child surfaces; separate X11 captures verify the arena.
- Initialization defers overlay attachment until Godot's root is no longer
  building children, then awaits `_Ready` before declaring preflight success.
  A successful bridge preflight alone did not previously prove F8 was active.
- The same offline Spire instance accepts a fresh F8 through Godot's input
  pipeline, launches a second arena, and restores the menu after the native
  arena child receives `WM_DELETE_WINDOW`. The repeat key is injected by a
  developer-only hook: desktop `XSendEvent` repeat delivery remains unreliable
  here. That hook and framebuffer capture code are not in the shipped assembly.

Spire retains input focus for forwarded-controller/adapter modes; native keyboard
mode retains arena focus. On exit the mod closes the bridge transport first,
allowing private Wine-prefix cleanup before the bounded forced-stop fallback.

Normal Steam-launched presentation, physical controller hardware, actual Windows
installation, and live multiplayer still need integration testing. The offline
desktop probe forces only its own test parent mapped to avoid the compositor
hiding agent-launched windows. Do not label the remaining paths as verified
merely because the unit tests or a disposable parent pass.

Alpha.13 has now been reported working through normal Steam launch on the main
Bazzite machine. Alpha.14's isolated 2560-wide Spire test verifies aligned Lab
rendering and nonzero keyboard input applied by the engine. Rendering remains
capped at 60 FPS; >60 display interpolation is not enabled by this integration.

Alpha.15 adds focus-restricted Linux-native keyboard polling, Linux-native USB adapter discovery/decoding, neutral-input
arbitration, embedded Windows keyboard focus, and persistent asset failure
diagnostics forwarded through the bridge to Spire. Decoder tests cover all four
ports, calibration, disconnect and source selection. A deliberate missing
Battlefield asset reproduces `lbfile.c:238`, exits with a failed status naming
`/GrNBa.dat`, and does not block on a fatal dialog. This does not establish the
cause of a user's Windows crash. Two real local offline-network peers connect
and enter an online match, with nonzero forwarded input recorded by guest
PADRead. Those tests are not live internet matchmaking or actual Windows tests.
The clean alpha.15 production assembly also passes an actual offline Spire
test on a private nested X11 display: F8 launches the embedded child, keyboard
focus is moved into that child, a physical-state XTest press is read by the
production Linux keyboard helper and reaches the engine, and the CPU result
restores Spire. This is not a physical gamepad/adapter test. The normal desktop
XWayland injector does not retain synthetic key state in this environment;
using an isolated display avoids falsely treating that injector failure as
proof that real user keys are not reaching the reader.

Alpha.16 fixes CPU defaults leaving item frequency at Medium and uses the full
`StartMeleeRules.x20` item-mask span (0x20–0x27), not a three-byte-shifted write.
Both CPU and online matches must report `0xFF` (None) at game-start byte 0x10
before the arena becomes ready. Real CPU and both local multiplayer peer
game-start events pass this check.

Spire now forwards its controller action events and the manager's left-stick
vector, not just raw joypad state. This covers the logical action path used by
Steam Input without requiring a raw Godot controller. Pure mapper tests cover
buttons, triggers, stick directions and release; physical controller hardware
still requires a real-device retest. The overlay consumes gameplay key/joypad
and action events to prevent operating Spire's underlying menu while fighting.
An isolated actual-Spire probe injects the same `controller_face_button_south`
action event plus a left-stick action used by Spire's strategy, with no raw
joypad requirement. The engine receives attack (`buttons 256`) and right-stick
movement (`stick 80,0`), completes the CPU fight, and restores Spire. Only the
probe assembly contains this synthetic injection; it is removed before building
the shipped DLL. This does not verify a physical Steam Controller connection.
The clean production DLL also restores actual X11 focus from a clicked arena
child to Spire in forwarding modes, letting Spire's focus-gated controller
updater continue. A real-Spire probe verifies that restoration and separately
verifies that another application keeps focus. The repair checks native X11
focus rather than Godot's cached HasFocus flag, which can be stale for foreign
children. Native binding mode does not apply this focus repair.
