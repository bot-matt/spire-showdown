# Bridge protocol v1

Transport is UTF-8, newline-delimited JSON over a loopback TCP socket. Every
request carries the bridge startup token and a request ID. Every response
echoes the request ID.

Core message types:

- `hello`
- `preflight`
- `start_duel`
- `attach_window`
- `status`
- `duel_result`
- `cancel_duel`
- `shutdown`

The `status` response includes `slippi_phase`. Attach the Slippi surface inside
the duel overlay as soon as it exists, but keep the loading animation above it
through `booting` and `connecting`. Remove that cover only after Slippi reports
`ready`, immediately before the first game frames. A `completed` status also
carries `winner_idx`.

The Rust definitions in `bridge/src/protocol.rs` are canonical until a schema
generator is introduced. The C# records in `mod/SpireShowdown/Protocol.cs`
mirror the wire names.
