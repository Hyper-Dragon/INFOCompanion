//! Spawning and supervising the C# co-player agent (issue #5).
//!
//! The agent is a **child process speaking the seat protocol on its stdin/stdout**.
//! There is no network port and no auth surface between the core and the agent: the
//! OS pipe *is* the authenticated channel (§2.3).
//!
//! Supervision rules (§8.2):
//!
//! * a dead agent is restarted with backoff **1s → 5s → 30s**, the backoff reset
//!   once a run has been stable;
//! * the game session and any SSH connections never blink, because the agent is
//!   only ever a seat — a gap in it is a gap in commentary, nothing more;
//! * a **fatal** condition (no agent binary, protocol version mismatch) stops
//!   supervision instead of spinning: retrying those would be pure noise;
//! * on daemon shutdown the child's stdin is closed first (a clean EOF), which is
//!   the agent's signal to exit.

use crate::config::AgentTuning;
use crate::core::{Core, CoreCommand, UtteranceKind};
use crate::game::InputSource;
use crate::seat::{Outbound, SeatKind, SeatReceiver};
use crate::seat_protocol::{
    self as proto, AskPayload, ChatPayload, Envelope, EnvelopeWriter, GameInputPayload,
    HelloPayload, ProposalPayload, MSG_ASK, MSG_CHAT, MSG_GAME_INPUT, MSG_HELLO, MSG_PROPOSAL,
    MSG_THOUGHT, MSG_THOUGHT_DELTA, MSG_TOOL, MSG_TURN_FINISHED, MSG_TURN_STARTED, MSG_WORKING,
};
use anyhow::{anyhow, Context, Result};
use serde_json::json;
use std::process::Stdio;
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::io::{AsyncBufReadExt, BufReader, BufWriter};
use tokio::process::{ChildStdin, Command};
use tokio::sync::{mpsc, oneshot};
use tracing::{debug, error, info, warn};

/// The agent's launch command.
#[derive(Debug, Clone)]
pub struct AgentCommand {
    pub program: String,
    pub args: Vec<String>,
}

impl AgentCommand {
    /// Parse `INFOCOMPANION_AGENT_CMD` / `--agent-cmd` (whitespace-separated).
    pub fn parse(spec: &str) -> Result<Self> {
        let mut parts = spec.split_whitespace();
        let program = parts
            .next()
            .ok_or_else(|| anyhow!("INFOCOMPANION_AGENT_CMD is empty"))?
            .to_string();
        Ok(Self {
            program,
            args: parts.map(str::to_string).collect(),
        })
    }

    fn command_line(&self) -> String {
        std::iter::once(self.program.as_str())
            .chain(self.args.iter().map(String::as_str))
            .collect::<Vec<_>>()
            .join(" ")
    }
}

/// Why a run ended.
enum Outcome {
    /// The agent exited or its stream closed: restart it.
    Restart(String),
    /// The daemon is going down: the child was told, and there is nothing to restart.
    Stopping,
    /// Retrying cannot help.
    Fatal(anyhow::Error),
}

/// Which rung of the ladder a restart takes, and which rung is next.
///
/// Split out from the loop so the rule can be read and tested on its own, and so the ladder has
/// exactly one home: a run that lasted long enough to count as stable starts again at the bottom,
/// anything else climbs, and the last rung repeats rather than growing without bound.
fn backoff_step(policy: &AgentTuning, uptime: Duration, step: usize) -> (usize, Duration) {
    if policy.backoff.is_empty() {
        // A ladder with no rungs is not a faster ladder, it is a configuration mistake; the
        // supervisor waits a second rather than spinning on a companion that cannot start.
        return (0, Duration::from_secs(1));
    }
    let stable = uptime >= policy.stable_uptime;
    let step = if stable { 0 } else { step };
    let delay = policy.backoff[step.min(policy.backoff.len() - 1)];
    let next = if stable || step >= policy.backoff.len() - 1 {
        step
    } else {
        step + 1
    };
    (next, delay)
}

/// Supervise the agent for the lifetime of the daemon.
///
/// `stopping` is the daemon's own shutdown. It is here rather than a task abort because the way an
/// agent is told to go is **EOF on its stdin** (§8.2), and the only thing holding that pipe open is
/// this function's writer: aborting from outside leaves it open and the child is killed with the
/// container, so the agent never runs its exit path and the session's own summary — the numbers a
/// bundle is read for — is never written. Measured 2026-10-01, twice: no log carried a `session_end`,
/// through a full sitting, a disconnect, a restart and a clean stop.
pub async fn supervise(
    core: Arc<Core>,
    command: AgentCommand,
    policy: AgentTuning,
    shutdown_grace: Duration,
    mut stopping: oneshot::Receiver<()>,
) {
    let mut step = 0usize;
    info!(command = %command.command_line(), "supervising the companion");

    loop {
        let started = Instant::now();
        let outcome = run_once(&core, &command, &mut stopping, shutdown_grace).await;

        match outcome {
            Outcome::Stopping => {
                info!(
                    "the daemon is going down; the companion was told and the supervisor is done"
                );
                return;
            }
            Outcome::Fatal(err) => {
                error!(error = %format!("{err:#}"), "not restarting the companion");
                core.send(CoreCommand::AgentOffline).await;
                return;
            }
            Outcome::Restart(reason) => {
                let uptime = started.elapsed();
                let (next, delay) = backoff_step(&policy, uptime, step);
                warn!(
                    reason = %reason,
                    uptime_s = uptime.as_secs(),
                    retry_in_s = delay.as_secs(),
                    "companion stopped; restarting"
                );
                step = next;

                // A shutdown that arrives during a backoff must not wait the rung out, and there is
                // no child to say goodbye to: nothing is holding the pipe, so there is nothing to do
                // but stop supervising.
                tokio::select! {
                    _ = &mut stopping => {
                        info!("the daemon is going down while the companion rests; not restarting it");
                        return;
                    }
                    _ = tokio::time::sleep(delay) => {}
                }
            }
        }
    }
}

/// One agent lifetime: spawn, handshake, pump, reap.
async fn run_once(
    core: &Arc<Core>,
    command: &AgentCommand,
    stopping: &mut oneshot::Receiver<()>,
    shutdown_grace: Duration,
) -> Outcome {
    let mut child = match Command::new(&command.program)
        .args(&command.args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
    {
        Ok(child) => child,
        Err(err) => {
            return Outcome::Fatal(anyhow!(err).context(format!(
                "failed to start the companion: {}",
                command.command_line()
            )))
        }
    };

    let (Some(stdin), Some(stdout), Some(stderr)) =
        (child.stdin.take(), child.stdout.take(), child.stderr.take())
    else {
        return Outcome::Fatal(anyhow!("companion process did not give us its pipes"));
    };

    let (seat_id, receiver) = core
        .register_seat("companion", SeatKind::Agent, false)
        .await;
    let SeatReceiver::Protocol(rx) = receiver else {
        unreachable!("an agent seat is a protocol seat");
    };
    let writer = tokio::spawn(write_loop(stdin, rx));
    let stderr_task = tokio::spawn(pump_stderr(stderr));

    let mut lines = BufReader::new(stdout).lines();
    let mut handshaken = false;
    let mut fatal: Option<anyhow::Error> = None;

    loop {
        // The daemon's shutdown races the agent's own stream, and the two endings are not the same
        // ending: one is a child to say goodbye to, the other is a run to restart.
        let next = tokio::select! {
            _ = &mut *stopping => None,
            line = lines.next_line() => Some(line),
        };

        let Some(next) = next else {
            // Say goodbye the way the protocol defines it: close the child's stdin and let it finish
            // (`§8.2`). This is the only path that ever lets the agent run its own exit, which is
            // where it writes the sitting's and the session's numbers — and it is why the shutdown
            // signal is handled *here*, where the pipe is, rather than by aborting this task from
            // outside, which left the pipe open and the child to be killed with the container.
            writer.abort();
            match tokio::time::timeout(shutdown_grace, child.wait()).await {
                Ok(Ok(status)) => info!(
                    code = ?status.code(),
                    "the companion was told to go and stopped"
                ),
                Ok(Err(err)) => {
                    warn!(error = %err, "could not reap the companion after the goodbye")
                }
                Err(_) => warn!(
                    grace_s = shutdown_grace.as_secs(),
                    "the companion did not stop within the grace period"
                ),
            }
            core.unregister_seat(seat_id).await;
            core.send(CoreCommand::AgentOffline).await;
            stderr_task.abort();
            return Outcome::Stopping;
        };

        match next {
            Ok(Some(line)) => {
                let line = line.trim();
                if line.is_empty() {
                    continue;
                }
                let envelope = match Envelope::parse(line) {
                    Ok(envelope) => envelope,
                    Err(err) => {
                        warn!(error = %err, "ignoring a malformed protocol line from the companion");
                        continue;
                    }
                };
                if let Err(err) = envelope.check_version() {
                    fatal = Some(err.context("the companion speaks a different protocol version"));
                    break;
                }
                debug!(kind = %envelope.msg_type, seq = envelope.seq, "agent <-");
                if envelope.msg_type == MSG_HELLO {
                    match handle_hello(core, seat_id, &envelope).await {
                        Ok(()) => handshaken = true,
                        Err(err) => {
                            fatal = Some(err);
                            break;
                        }
                    }
                    continue;
                }
                if !handshaken {
                    warn!(
                        kind = %envelope.msg_type,
                        "the companion spoke before `hello`; ignoring"
                    );
                    continue;
                }
                if let Err(err) = handle_message(core, seat_id, &envelope).await {
                    warn!(error = %err, "ignoring a bad companion message");
                }
            }
            Ok(None) => break,
            Err(err) => {
                warn!(error = %err, "error reading from the companion");
                break;
            }
        }
    }

    core.unregister_seat(seat_id).await;
    core.send(CoreCommand::AgentOffline).await;
    writer.abort();
    stderr_task.abort();
    let status = child.wait().await;

    if let Some(err) = fatal {
        return Outcome::Fatal(err);
    }
    let reason = match status {
        Ok(status) => format!(
            "companion exited with {}",
            status
                .code()
                .map(|c| c.to_string())
                .unwrap_or_else(|| "no exit code".to_string())
        ),
        Err(err) => format!("could not reap the companion: {err}"),
    };
    if !handshaken {
        return Outcome::Restart(format!("{reason} (it never handshook)"));
    }
    Outcome::Restart(reason)
}

async fn handle_hello(core: &Arc<Core>, seat_id: u64, envelope: &Envelope) -> Result<()> {
    let hello: HelloPayload = envelope
        .payload_as()
        .context("bad `hello` payload from the companion")?;
    if hello.protocol_version != proto::PROTOCOL_VERSION {
        return Err(anyhow!(
            "the companion speaks protocol v{} but this core speaks v{}; rebuild the agent",
            hello.protocol_version,
            proto::PROTOCOL_VERSION
        ));
    }
    let name = if hello.client_name.trim().is_empty() {
        "companion".to_string()
    } else {
        hello.client_name.clone()
    };
    info!(client = %name, "companion handshaken");

    // Snapshot first, then announce: the agent should be able to see the world
    // before the player is told it joined.
    let welcome = core.welcome().await;
    core.send_to_seat(seat_id, welcome).await;

    // Then what this game's parser will actually accept, so the companion can check
    // a command before spending a turn finding out (§10.4).
    if let Some(vocabulary) = core.vocabulary().await {
        core.send_to_seat(seat_id, vocabulary).await;
    }

    core.send(CoreCommand::AgentOnline { name }).await;
    Ok(())
}

async fn handle_message(core: &Arc<Core>, seat_id: u64, envelope: &Envelope) -> Result<()> {
    match envelope.msg_type.as_str() {
        MSG_GAME_INPUT => {
            let payload: GameInputPayload = envelope.payload_as()?;
            core.send(CoreCommand::GameInput {
                seat_id,
                line: payload.line,
                source: InputSource::Agent,
                key: payload.key,
            })
            .await;
        }
        MSG_CHAT => {
            let payload: ChatPayload = envelope.payload_as()?;
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::Chat,
                text: payload.text,
            })
            .await;
        }
        MSG_THOUGHT => {
            let payload: ChatPayload = envelope.payload_as()?;
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::Thought,
                text: payload.text,
            })
            .await;
        }
        MSG_THOUGHT_DELTA => {
            let payload: ChatPayload = envelope.payload_as()?;
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::ThoughtDelta,
                text: payload.text,
            })
            .await;
        }
        MSG_ASK => {
            let payload: AskPayload = envelope.payload_as()?;
            debug!(
                proposal_id = ?payload.proposal_id,
                "companion asked the player a question"
            );
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::Ask,
                text: payload.text,
            })
            .await;
        }
        MSG_PROPOSAL => {
            let payload: ProposalPayload = envelope.payload_as()?;
            core.log_event(
                "agent_proposal",
                json!({
                    "id": payload.id,
                    "action": payload.action,
                    "reasoning": payload.reasoning,
                    "risk": payload.risk,
                }),
            );
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::Proposal,
                text: crate::stdio_transport::render_proposal(&payload),
            })
            .await;
        }
        MSG_WORKING => {
            // Nothing to read: the line is the core's to phrase, and the only fact is that a
            // call is still running (see MSG_WORKING).
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::Working,
                text: String::new(),
            })
            .await;
        }
        MSG_TURN_STARTED | MSG_TURN_FINISHED => {
            // The companion's lamp (03_session.md §7 (`S44`–`S45`)). Nothing to read here either, and it is not an
            // utterance: a light, and it goes out when the reply has been delivered.
            core.send(CoreCommand::AgentLamp {
                on: envelope.msg_type == MSG_TURN_STARTED,
            })
            .await;
        }
        MSG_TOOL => {
            // What it reached for, rendered as the harness's line rather than the companion's
            // (see MSG_TOOL); whether anybody sees it is the core's decision.
            let payload: crate::seat_protocol::ToolPayload = envelope.payload_as()?;
            core.send(CoreCommand::AgentUtterance {
                kind: UtteranceKind::Tool,
                text: crate::stdio_transport::render_tool(&payload),
            })
            .await;
        }
        other => warn!(kind = other, "unknown message type from the companion"),
    }
    Ok(())
}

/// Single writer for the agent's stdin: this is what stops two protocol lines from
/// ever being interleaved (§6.6).
async fn write_loop(stdin: ChildStdin, mut rx: mpsc::Receiver<Outbound>) {
    let mut writer = EnvelopeWriter::new(BufWriter::new(stdin));
    while let Some(message) = rx.recv().await {
        let (msg_type, payload) = message.into_wire();
        if let Err(err) = writer.send(msg_type, payload).await {
            debug!(error = %err, "companion stdin closed");
            break;
        }
    }
    debug!("companion writer finished");
}

/// The agent's own stderr is diagnostic, and belongs in our log — not the player's.
async fn pump_stderr(stderr: tokio::process::ChildStderr) {
    let mut lines = BufReader::new(stderr).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        let line = line.trim_end();
        if !line.is_empty() {
            info!(target: "agent", "{line}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_command_with_arguments() {
        let command = AgentCommand::parse("/usr/local/bin/infocompanion-agent --verbose").unwrap();
        assert_eq!(command.program, "/usr/local/bin/infocompanion-agent");
        assert_eq!(command.args, vec!["--verbose".to_string()]);
        assert_eq!(
            command.command_line(),
            "/usr/local/bin/infocompanion-agent --verbose"
        );
    }

    #[test]
    fn parses_a_bare_command() {
        let command = AgentCommand::parse("  infocompanion-agent  ").unwrap();
        assert_eq!(command.program, "infocompanion-agent");
        assert!(command.args.is_empty());
    }

    #[test]
    fn rejects_an_empty_command() {
        assert!(AgentCommand::parse("   ").is_err());
    }

    #[test]
    fn the_ladder_climbs_and_stops_at_its_last_rung() {
        let policy = policy(&[1, 5, 30], 60);
        let mut step = 0;
        let mut waits = Vec::new();
        // Four failures in a row, each short enough not to count as stable: the ladder is walked
        // to its end and then repeats rather than growing.
        for _ in 0..4 {
            let (next, delay) = backoff_step(&policy, Duration::from_secs(1), step);
            waits.push(delay.as_secs());
            step = next;
        }
        assert_eq!(waits, vec![1, 5, 30, 30]);
        assert_eq!(step, 2, "the step stops on the last rung");
    }

    #[test]
    fn a_run_that_lasted_resets_the_ladder() {
        let policy = policy(&[1, 5, 30], 60);
        let (next, delay) = backoff_step(&policy, Duration::from_secs(600), 2);
        assert_eq!(
            delay,
            Duration::from_secs(1),
            "a stable run starts again at the bottom"
        );
        assert_eq!(next, 0);
    }

    #[test]
    fn a_ladder_with_no_rungs_waits_a_second_rather_than_spinning() {
        let policy = policy(&[], 60);
        assert_eq!(
            backoff_step(&policy, Duration::from_secs(1), 0).1,
            Duration::from_secs(1)
        );
    }

    fn policy(seconds: &[u64], stable: u64) -> AgentTuning {
        AgentTuning {
            backoff: seconds.iter().copied().map(Duration::from_secs).collect(),
            stable_uptime: Duration::from_secs(stable),
        }
    }
}
