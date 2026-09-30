# Spire Showdown

Spire Showdown turns a contested relic in Slay the Spire 2 multiplayer into a
one-stock Slippi Melee duel, then returns the winner to the still-running run.

The project is intentionally split into two pieces:

- `mod/` is the C#/Godot Slay the Spire 2 mod.
- `bridge/` is a small native companion that discovers Slippi and the user's
  Melee ISO, launches the duel, embeds its window, and reports the result.

No game image, Nintendo asset, or Slippi binary is included.

## Current status

This first vertical-slice scaffold includes:

- a versioned, newline-delimited JSON protocol;
- an authenticated local bridge server;
- Linux/Bazzite and Windows Slippi/ISO discovery;
- preflight diagnostics and ISO fingerprinting;
- a tested duel lifecycle state machine;
- a StS2/BaseLib mod shell with an asynchronous bridge client;
- a validated Harmony interception point for two-player contested relics;
- platform seams for XWayland and Win32 window embedding.

The current StS2 public-beta mod compiles against build 24724944 with no
warnings. Its interception is safely gated off until bridge preflight and duel
negotiation exist, so installing this development build preserves vanilla RPS.

The next hardware-backed spike is to determine whether stock Slippi can enter
Direct mode automatically. If stock Slippi exposes no usable control surface,
the bridge will require a small Slippi Dolphin patch rather than brittle input
automation.

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
