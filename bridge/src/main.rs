mod arena;
mod discovery;
mod iso;
mod process;
mod protocol;
mod server;
mod state;
mod window;

use std::path::PathBuf;

use anyhow::Result;
use clap::{Parser, Subcommand};

#[derive(Debug, Parser)]
#[command(name = "spire-showdown-bridge")]
#[command(about = "Slippi bridge for Spire Showdown", long_about = None)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Find Slippi and Melee and print a machine-readable preflight report.
    Doctor {
        #[arg(long)]
        slippi: Option<PathBuf>,
        #[arg(long)]
        iso: Option<PathBuf>,
        /// Use the patched Melee Unlocked arena, not Dolphin.
        #[arg(long)]
        arena: Option<PathBuf>,
        #[arg(long)]
        proton: Option<PathBuf>,
        #[arg(long)]
        user_dir: Option<PathBuf>,
    },
    /// Start the authenticated loopback bridge server.
    Serve {
        #[arg(long, default_value_t = 0)]
        port: u16,
        #[arg(long)]
        token: String,
    },
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    match cli.command {
        Command::Doctor {
            slippi,
            iso,
            arena,
            proton,
            user_dir,
        } => {
            let mut report = discovery::discover(slippi.as_deref(), iso.as_deref());
            if let Some(executable) = arena {
                if let Err(problem) = (crate::arena::ArenaConfig {
                    executable: Some(executable),
                    proton,
                    user_dir,
                })
                .resolve(&mut report)
                {
                    report.ready = false;
                    report.problems.push(problem);
                }
            }
            println!("{}", serde_json::to_string_pretty(&report)?);
            if report.ready {
                Ok(())
            } else {
                std::process::exit(2)
            }
        }
        Command::Serve { port, token } => server::serve(port, &token),
    }
}
