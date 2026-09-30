use std::io::{BufRead, BufReader, BufWriter, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};

use anyhow::{Context, Result};

use crate::process::SlippiProcess;
use crate::protocol::{Envelope, Request, Response, ResponseEnvelope, VERSION};
use crate::state::{DuelMachine, DuelState};
use crate::window::{platform_embedder, WindowEmbedder};

struct BridgeRuntime {
    machine: DuelMachine,
    discovery: Option<crate::discovery::DiscoveryReport>,
    slippi: Option<SlippiProcess>,
    embedder: Box<dyn WindowEmbedder + Send>,
    window_attached: bool,
}

impl BridgeRuntime {
    fn new() -> Self {
        Self {
            machine: DuelMachine::default(),
            discovery: None,
            slippi: None,
            embedder: platform_embedder(),
            window_attached: false,
        }
    }

    fn reset_terminal_state(&mut self) {
        if matches!(
            self.machine.state(),
            DuelState::Completed | DuelState::Failed | DuelState::Cancelled
        ) {
            let _ = self.machine.transition(DuelState::Idle);
        }
    }

    fn stop_slippi(&mut self) -> Result<(), String> {
        let detach_result = self.embedder.detach();
        self.window_attached = false;
        let stop_result = self
            .slippi
            .take()
            .map(|mut process| process.stop())
            .unwrap_or(Ok(()));
        match (detach_result, stop_result) {
            (Ok(()), Ok(())) => Ok(()),
            (Err(detach), Ok(())) => Err(detach),
            (Ok(()), Err(stop)) => Err(stop),
            (Err(detach), Err(stop)) => Err(format!(
                "window detach failed: {detach}; Slippi stop failed: {stop}"
            )),
        }
    }
}

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

    let runtime = Arc::new(Mutex::new(BridgeRuntime::new()));
    for connection in listener.incoming() {
        match connection {
            Ok(stream) => {
                if handle_client(stream, expected_token, &runtime)? {
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
    runtime: &Arc<Mutex<BridgeRuntime>>,
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
                let response = dispatch(envelope.message, runtime);
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

fn dispatch(request: Request, runtime: &Arc<Mutex<BridgeRuntime>>) -> Response {
    match request {
        Request::Hello => Response::Hello {
            bridge_version: env!("CARGO_PKG_VERSION").into(),
            platform: std::env::consts::OS.into(),
        },
        Request::Preflight { slippi, iso } => {
            let mut guard = runtime.lock().expect("bridge runtime lock poisoned");
            guard.reset_terminal_state();
            if let Err(message) = guard.machine.transition(DuelState::Preflighting) {
                return error_response("invalid_state", message, true);
            }
            let report = crate::discovery::discover(slippi.as_deref(), iso.as_deref());
            let next = if report.ready {
                DuelState::Ready
            } else {
                DuelState::Failed
            };
            let _ = guard.machine.transition(next);
            guard.discovery = Some(report.clone());
            Response::Preflight { report }
        }
        Request::StartDuel { duel } => {
            let mut guard = runtime.lock().expect("bridge runtime lock poisoned");
            if let Err(message) = guard.machine.transition(DuelState::Launching) {
                return error_response("invalid_state", message, true);
            }
            let Some(report) = guard.discovery.as_ref() else {
                let _ = guard.machine.transition(DuelState::Failed);
                return error_response(
                    "preflight_required",
                    "run preflight before launching",
                    true,
                );
            };
            let (Some(slippi), Some(iso)) = (&report.slippi, &report.melee_iso) else {
                let _ = guard.machine.transition(DuelState::Failed);
                return error_response("preflight_failed", "Slippi or Melee path is missing", true);
            };
            match SlippiProcess::launch(&slippi.path, &iso.path, &duel) {
                Ok(process) => {
                    let pid = process.pid();
                    let duel_id = process.duel_id().to_owned();
                    guard.slippi = Some(process);
                    Response::Started {
                        duel_id,
                        slippi_pid: pid,
                    }
                }
                Err(message) => {
                    let _ = guard.machine.transition(DuelState::Failed);
                    error_response("slippi_launch_failed", message, true)
                }
            }
        }
        Request::AttachWindow {
            parent_handle,
            bounds,
        } => {
            let mut guard = runtime.lock().expect("bridge runtime lock poisoned");
            let Some(pid) = guard.slippi.as_ref().map(SlippiProcess::pid) else {
                return error_response("no_active_duel", "Slippi is not running", true);
            };
            match guard.embedder.attach(parent_handle, pid, bounds) {
                Ok(()) => {
                    guard.window_attached = true;
                    if guard.machine.state() == DuelState::Launching {
                        if let Err(message) = guard.machine.transition(DuelState::Connecting) {
                            return error_response("invalid_state", message, true);
                        }
                    }
                    Response::Accepted
                }
                Err(message) => error_response("window_attach_failed", message, true),
            }
        }
        Request::Status => {
            let mut guard = runtime.lock().expect("bridge runtime lock poisoned");
            let duel_status = match guard.slippi.as_ref().map(SlippiProcess::duel_status) {
                Some(Ok(status)) => status,
                Some(Err(message)) => return error_response("duel_status_failed", message, true),
                None => None,
            };
            if duel_status
                .as_ref()
                .is_some_and(|status| matches!(status.phase.as_str(), "connecting" | "ready"))
                && guard.machine.state() == DuelState::Launching
            {
                let _ = guard.machine.transition(DuelState::Connecting);
            }
            if duel_status
                .as_ref()
                .is_some_and(|status| status.phase == "ready")
                && guard.machine.state() == DuelState::Connecting
                && guard.window_attached
            {
                let _ = guard.machine.transition(DuelState::Playing);
            }
            if duel_status
                .as_ref()
                .is_some_and(|status| status.phase == "completed")
                && guard.machine.state() == DuelState::Playing
            {
                let _ = guard.machine.transition(DuelState::Completed);
            }
            let exited = guard
                .slippi
                .as_mut()
                .and_then(|process| process.try_wait().ok().flatten());
            if exited.is_some()
                && matches!(
                    guard.machine.state(),
                    DuelState::Launching | DuelState::Connecting | DuelState::Playing
                )
            {
                let _ = guard.machine.transition(DuelState::Failed);
            }
            Response::Status {
                state: guard.machine.state(),
                duel_id: guard
                    .slippi
                    .as_ref()
                    .map(|process| process.duel_id().to_owned()),
                slippi_pid: guard.slippi.as_ref().map(SlippiProcess::pid),
                slippi_phase: duel_status.as_ref().map(|status| status.phase.clone()),
                winner_idx: duel_status.and_then(|status| status.winner_idx),
            }
        }
        Request::CancelDuel { duel_id, .. } => {
            let mut guard = runtime.lock().expect("bridge runtime lock poisoned");
            let active_id = guard.slippi.as_ref().map(SlippiProcess::duel_id);
            if active_id != Some(duel_id.as_str()) {
                return error_response("duel_id_mismatch", "active duel ID does not match", true);
            }
            if let Err(message) = guard.stop_slippi() {
                return error_response("slippi_stop_failed", message, true);
            }
            match guard.machine.transition(DuelState::Cancelled) {
                Ok(()) => Response::Accepted,
                Err(message) => error_response("invalid_state", message, true),
            }
        }
        Request::Shutdown => {
            let mut guard = runtime.lock().expect("bridge runtime lock poisoned");
            match guard.stop_slippi() {
                Ok(()) => Response::Accepted,
                Err(message) => error_response("shutdown_failed", message, false),
            }
        }
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
