use std::io::{BufRead, BufReader, BufWriter, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};

use anyhow::{Context, Result};

use crate::protocol::{Envelope, Request, Response, ResponseEnvelope, VERSION};
use crate::state::{DuelMachine, DuelState};

pub fn serve(port: u16, expected_token: &str) -> Result<()> {
    if expected_token.len() < 12 {
        anyhow::bail!("bridge token must contain at least 12 characters");
    }
    let listener =
        TcpListener::bind(("127.0.0.1", port)).context("binding bridge loopback port")?;
    let address = listener.local_addr()?;
    println!(
        "{}",
        serde_json::json!({
            "type": "bridge_ready",
            "protocol": VERSION,
            "port": address.port(),
            "pid": std::process::id()
        })
    );
    std::io::stdout().flush()?;

    let machine = Arc::new(Mutex::new(DuelMachine::default()));
    for connection in listener.incoming() {
        match connection {
            Ok(stream) => {
                if handle_client(stream, expected_token, &machine)? {
                    return Ok(());
                }
            }
            Err(error) => eprintln!("bridge connection failed: {error}"),
        }
    }
    Ok(())
}

fn handle_client(
    stream: TcpStream,
    expected_token: &str,
    machine: &Arc<Mutex<DuelMachine>>,
) -> Result<bool> {
    let reader = BufReader::new(stream.try_clone()?);
    let mut writer = BufWriter::new(stream);

    for line in reader.lines() {
        let line = line?;
        let decoded: Result<Envelope<Request>, _> = serde_json::from_str(&line);
        let (request_id, response, shutdown) = match decoded {
            Err(error) => (
                "unknown".into(),
                error_response("invalid_json", error.to_string(), true),
                false,
            ),
            Ok(envelope) if envelope.protocol != VERSION => (
                envelope.request_id,
                error_response(
                    "protocol_mismatch",
                    format!("bridge requires protocol {VERSION}"),
                    false,
                ),
                false,
            ),
            Ok(envelope) if envelope.token != expected_token => (
                envelope.request_id,
                error_response("unauthorized", "invalid bridge token", false),
                false,
            ),
            Ok(envelope) => {
                let shutdown = matches!(envelope.message, Request::Shutdown);
                let response = dispatch(envelope.message, machine);
                (envelope.request_id, response, shutdown)
            }
        };

        let response = ResponseEnvelope {
            protocol: VERSION,
            request_id,
            response,
        };
        serde_json::to_writer(&mut writer, &response)?;
        writer.write_all(b"\n")?;
        writer.flush()?;
        if shutdown {
            return Ok(true);
        }
    }
    Ok(false)
}

fn dispatch(request: Request, machine: &Arc<Mutex<DuelMachine>>) -> Response {
    match request {
        Request::Hello => Response::Hello {
            bridge_version: env!("CARGO_PKG_VERSION").into(),
            platform: std::env::consts::OS.into(),
        },
        Request::Preflight { slippi, iso } => {
            let mut guard = machine.lock().expect("duel state lock poisoned");
            if let Err(message) = guard.transition(DuelState::Preflighting) {
                return error_response("invalid_state", message, true);
            }
            let report = crate::discovery::discover(slippi.as_deref(), iso.as_deref());
            let next = if report.ready {
                DuelState::Ready
            } else {
                DuelState::Failed
            };
            let _ = guard.transition(next);
            Response::Preflight { report }
        }
        Request::StartDuel { .. } => error_response(
            "not_implemented",
            "Slippi launch control is the next hardware-backed milestone",
            true,
        ),
        Request::AttachWindow { .. } => error_response(
            "not_implemented",
            "native window attachment requires an active Slippi process",
            true,
        ),
        Request::Status => Response::Status {
            state: machine.lock().expect("duel state lock poisoned").state(),
        },
        Request::CancelDuel { .. } => {
            let mut guard = machine.lock().expect("duel state lock poisoned");
            match guard.transition(DuelState::Cancelled) {
                Ok(()) => Response::Accepted,
                Err(message) => error_response("invalid_state", message, true),
            }
        }
        Request::Shutdown => Response::Accepted,
    }
}

fn error_response(
    code: impl Into<String>,
    message: impl Into<String>,
    recoverable: bool,
) -> Response {
    Response::Error {
        code: code.into(),
        message: message.into(),
        recoverable,
    }
}
