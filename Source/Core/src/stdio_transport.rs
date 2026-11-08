//! `--stdio` dev/test transport (issue #3).
//!
//! No SSH listener, no agent: this process speaks the seat protocol on stdin and
//! stdout, exactly as an agent seat would, so the protocol and the turn framer can
//! be exercised without a network or an LLM.
//!
//!     cargo run -- --stdio --game /games/advent.z5
//!
//! stdout carries protocol bytes only; every log line goes to stderr (§6.1).

use crate::core::{Core, CoreCommand, UtteranceKind};
use crate::game::InputSource;
use crate::seat::{Outbound, SeatKind, SeatReceiver};
use crate::seat_protocol::{
    ChatPayload, Envelope, EnvelopeWriter, GameInputPayload, HelloPayload, PlayerLinePayload,
    ProposalPayload, ToolPayload, MSG_ASK, MSG_CHAT, MSG_GAME_INPUT, MSG_HELLO, MSG_PLAYER_LINE,
    MSG_PROPOSAL, MSG_THOUGHT, MSG_THOUGHT_DELTA, MSG_TOOL, MSG_TURN_FINISHED, MSG_TURN_STARTED,
    MSG_WORKING,
};
use anyhow::{Context, Result};
use serde_json::json;
use std::sync::Arc;
use tokio::io::{AsyncBufReadExt, BufReader, BufWriter};
use tokio::sync::mpsc;
use tracing::{debug, error, info, warn};

/// Run the stdio transport until stdin closes.
pub async fn run(core: Arc<Core>) -> Result<()> {
    let (seat_id, receiver) = core.register_seat("stdio", SeatKind::Stdio, false).await;
    let SeatReceiver::Protocol(rx) = receiver else {
        unreachable!("a stdio seat is a protocol seat");
    };

    let writer = tokio::spawn(write_loop(rx));

    // One reader loop, line by line (§6.1).
    let stdin = BufReader::new(tokio::io::stdin());
    let mut lines = stdin.lines();
    let mut handshaken = false;
    while let Some(line) = lines.next_line().await.context("failed to read stdin")? {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        let envelope = match Envelope::parse(line) {
            Ok(envelope) => envelope,
            Err(err) => {
                warn!(error = %err, "ignoring a malformed protocol line");
                continue;
            }
        };
        if let Err(err) = envelope.check_version() {
            error!("{err}");
            break;
        }
        debug!(kind = %envelope.msg_type, seq = envelope.seq, "stdio <-");
        match envelope.msg_type.as_str() {
            MSG_HELLO => {
                let hello: HelloPayload = match envelope.payload_as() {
                    Ok(hello) => hello,
                    Err(err) => {
                        warn!(error = %err, "bad hello payload");
                        continue;
                    }
                };
                info!(client = %hello.client_name, "stdio seat handshaken");
                let welcome = core.welcome().await;
                core.send_to_seat(seat_id, welcome).await;
                handshaken = true;
            }
            MSG_GAME_INPUT => {
                let payload: GameInputPayload = envelope.payload_as()?;
                core.send(CoreCommand::GameInput {
                    seat_id,
                    line: payload.line,
                    source: InputSource::Forced,
                    key: payload.key,
                })
                .await;
            }
            MSG_PLAYER_LINE => {
                // A line as if the player had typed it. This is the one inbound message that reaches
                // the core's own command namespace — `/switch`, `/save`, `/restore` — and it is what
                // lets a machine test drive the checklist items a player seat alone could reach.
                //
                // It takes the same route an SSH pane's line takes, all the way to the same
                // handler: nothing here re-implements parsing, and there is no way for a test to
                // pass a command the interactive path would refuse.
                let payload: PlayerLinePayload = envelope.payload_as()?;
                core.send(CoreCommand::PlayerLine {
                    seat_id,
                    line: payload.text,
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
                let payload: ChatPayload = envelope.payload_as()?;
                core.send(CoreCommand::AgentUtterance {
                    kind: UtteranceKind::Ask,
                    text: payload.text,
                })
                .await;
            }
            MSG_WORKING => {
                core.send(CoreCommand::AgentUtterance {
                    kind: UtteranceKind::Working,
                    text: String::new(),
                })
                .await;
            }
            MSG_TURN_STARTED | MSG_TURN_FINISHED => {
                core.send(CoreCommand::AgentLamp {
                    on: envelope.msg_type == MSG_TURN_STARTED,
                })
                .await;
            }
            MSG_TOOL => {
                let payload: ToolPayload = envelope.payload_as()?;
                core.send(CoreCommand::AgentUtterance {
                    kind: UtteranceKind::Tool,
                    text: render_tool(&payload),
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
                    text: render_proposal(&payload),
                })
                .await;
            }
            other => {
                warn!(
                    kind = other,
                    "ignoring a core-to-agent message type on the inbound stream"
                );
            }
        }
    }

    info!("stdin closed; stdio transport stopping");
    core.unregister_seat(seat_id).await;
    writer.abort();
    if !handshaken {
        warn!("stdio seat closed without ever sending `hello`");
    }
    Ok(())
}

/// Render a proposal the way a player should read it (§6.4, §12).
///
/// Shared with the agent supervisor so a proposal reads the same whichever transport
/// delivered it.
///
/// **Written as the harness talking, not as the companion.** The companion's own words
/// travel separately, as `chat`, and this is only the fact about the machinery: what a yes
/// and a no will do. A template in a speaking voice — `I want to try: x. Shall I?` — is the
/// single thing iteration 01 §2.3 banned, and a proposal is exactly where it kept happening
/// (issue #29).
///
/// The payload's `reasoning` is deliberately not printed: it is the model's own `[say]` or
/// `[ask]` text, which the player has already read under the companion's name. Rendering it
/// here said everything twice, once of it in a voice that was not the companion's.
pub fn render_proposal(payload: &ProposalPayload) -> String {
    format!(
        "proposal: `{}` (risk {}) — say yes to go ahead, or no to skip it",
        payload.action, payload.risk
    )
}

/// How much of a tool call's argument reaches the pane: one line, and the point is the reaching.
const TOOL_DETAIL_CHARS: usize = 60;

/// One tool or extension call, for the panel while the tool trace is on (03_session.md §4.3 (`S32`–`S34`)).
///
/// Rendered here rather than by the agent for the same reason a proposal is: the words are the
/// harness's, so a reader can tell what the companion *said* from what it *reached for*. The
/// argument is clipped and flattened, because a pane shows one line and a `write_notes` rewrite is
/// thousands of characters.
pub fn render_tool(payload: &ToolPayload) -> String {
    let flat = payload
        .detail
        .as_deref()
        .unwrap_or("")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");

    if flat.is_empty() {
        return format!("· {}()", payload.name);
    }

    let mut clipped: String = flat.chars().take(TOOL_DETAIL_CHARS).collect();
    if flat.chars().count() > TOOL_DETAIL_CHARS {
        clipped.push('…');
    }

    format!("· {}({clipped})", payload.name)
}

/// Serialise outbound protocol messages onto stdout.
async fn write_loop(mut rx: mpsc::Receiver<Outbound>) {
    let stdout = tokio::io::stdout();
    let mut writer = EnvelopeWriter::new(BufWriter::new(stdout));
    while let Some(message) = rx.recv().await {
        let (msg_type, payload) = message.into_wire();
        if let Err(err) = writer.send(msg_type, payload).await {
            error!(error = %err, "failed to write to stdout");
            break;
        }
    }
    debug!("stdio writer finished");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_tool_call_is_rendered_as_the_harness_line_and_its_argument_is_clipped() {
        // 03_session.md §4.3 (`S32`–`S34`): the name and the argument, in the harness's voice, one line.
        let payload = ToolPayload {
            name: "type".into(),
            detail: Some("north".into()),
        };
        assert_eq!(render_tool(&payload), "· type(north)");

        // A call with no argument at all still shows that it happened.
        let payload = ToolPayload {
            name: "press_key".into(),
            detail: None,
        };
        assert_eq!(render_tool(&payload), "· press_key()");

        // A whole-file rewrite is thousands of characters and a pane shows one line, so the
        // argument is clipped — the point is the reaching, not the content.
        let payload = ToolPayload {
            name: "write_notes".into(),
            detail: Some("x".repeat(500)),
        };
        let rendered = render_tool(&payload);
        assert!(rendered.contains('…'), "the clip is shown: {rendered}");
        assert!(rendered.chars().count() < 80, "one line, not a page");
    }

    #[test]
    fn a_proposal_is_the_harness_talking_and_says_how_to_answer() {
        let payload = ProposalPayload {
            id: "p1".into(),
            action: "bribe troll".into(),
            reasoning: "he is still blocking the bridge".into(),
            risk: "medium".into(),
        };
        let text = render_proposal(&payload);
        assert!(text.contains("bribe troll"));
        assert!(text.contains("risk medium"));
        assert!(text.contains("say yes"), "the player is told how to answer");

        // The companion's own words are not repeated here: they went out as chat, under its
        // own name, and a second copy inside a harness line is what made the companion read
        // as a machine (issues #29).
        assert!(!text.contains("blocking the bridge"), "{text}");
        assert_eq!(text.lines().count(), 1, "{text}");
    }
}
