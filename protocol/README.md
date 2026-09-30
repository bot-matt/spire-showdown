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

The Rust definitions in `bridge/src/protocol.rs` are canonical until a schema
generator is introduced. The C# records in `mod/SpireShowdown/Protocol.cs`
mirror the wire names.

