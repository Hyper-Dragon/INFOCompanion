//! The seat protocol (issue #2): JSONL envelopes carried over stdio.
//!
//! This module owns the wire types, the framing helpers and the version check.
//! It deliberately contains no I/O *policy* — nothing here decides who a seat is
//! or what happens when a queue fills up; that lives in `seat.rs` and `core.rs`.
//!
//! The normative description of the protocol lives in `Documents/03_session.md` §3.
//!
//! Framing rules (§6.1):
//!   * one JSON object per line, UTF-8, `\n` terminated, no length prefixes;
//!   * `stdout` carries protocol bytes **only** — all logs go to `stderr`.
//!
//! Envelope (§6.2):
//!   `{ "v": 1, "type": "turn_complete", "seq": 42, "ts": "...", "payload": { } }`

use anyhow::{bail, Context, Result};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::io::{AsyncWrite, AsyncWriteExt};

/// Protocol version spoken by this end. A mismatch is fatal at handshake (§6.4).
pub const PROTOCOL_VERSION: u64 = 1;

/// `hello` — agent → core: opens the handshake.
pub const MSG_HELLO: &str = "hello";
/// `welcome` — core → agent: handshake response carrying a session snapshot.
pub const MSG_WELCOME: &str = "welcome";
/// `game_output` — core → agent: a chunk of raw frotz text.
pub const MSG_GAME_OUTPUT: &str = "game_output";
/// `turn_complete` — core → agent: a framed turn; the agent's sync point.
pub const MSG_TURN_COMPLETE: &str = "turn_complete";
/// `game_input` — agent → core: one line for the game, which is a command unless `key` is set.
pub const MSG_GAME_INPUT: &str = "game_input";
/// `player_line` — agent → core: a line **as if the player had typed it**.
///
/// `/switch`, `/save <name>` and `/restore` are parsed only out of a *player's* line, and `--stdio`
/// opens an agent-style seat whose `game_input` goes straight to the story — so a machine test could
/// not drive them at all, and four items on the session checklist stayed manual, checked by a person
/// at a keyboard or not at all.
///
/// This is the way in, and it is deliberately an *input* message rather than a second platform: the
/// line arrives at [`CoreCommand::PlayerLine`] exactly as an SSH pane's does — the same `/`
/// namespace, the same parsing, the same refusals — so a test that uses it exercises the real path
/// instead of a copy of it. Nothing about a live session changes: the only transport that carries it
/// is the development one, and no shipped seat sends it.
pub const MSG_PLAYER_LINE: &str = "player_line";
/// `chat` — agent → core (agent commentary) and core → agent (player line).
pub const MSG_CHAT: &str = "chat";
/// `ask` — agent → core: a question awaiting the player's answer.
pub const MSG_ASK: &str = "ask";
/// `thought` — agent → core: thinking out loud, shown in the conversation view.
///
/// Additive on top of §6.4: a core that does not know the type ignores it, and an agent
/// that never sends it behaves exactly as before — the reasoning was in its own log file
/// and nowhere else, which is the whole reason this message exists (04_companion.md §4 (`C26`–`C28`)).
pub const MSG_THOUGHT: &str = "thought";
/// `thought_delta` — agent → core: one chunk of that thinking, sent as it arrives.
///
/// Additive again, and nothing for a core that ignores it: the finished [`MSG_THOUGHT`] still
/// arrives and still carries the whole text, which is what the log and `/think` read. Sending
/// the words as they are produced is about the *pane* — measured, 163 of 248 calls in one
/// evening left the screen silent for two seconds or more and the longest were twenty, and a
/// companion that thinks for twenty seconds should look like it is thinking.
pub const MSG_THOUGHT_DELTA: &str = "thought_delta";
/// `proposal` — agent → core: an intended move awaiting grant/veto.
pub const MSG_PROPOSAL: &str = "proposal";
/// `working` — agent → core: a model call has been in flight long enough to be worth a line.
///
/// The payload is empty: the words are the core's, exactly as they are for a proposal. Additive
/// on top of §6.4 — a core that does not know the type ignores it, and nothing about the call
/// changes either way. It exists because a companion thinking hard and a hung terminal are the
/// same screen: measured on 2026-09-16, p95 was 15.9s, one call took 45.7s, and once the player
/// asked "james...you there?" while the companion was mid-call.
pub const MSG_WORKING: &str = "working";
/// `turn_started` — agent → core: the companion has taken a turn and the first model call is away.
///
/// The payload is empty, like [`MSG_WORKING`]'s: the fact is that something is happening, and the
/// core decides what a pane is told. Additive on top of §6.4 — a core that does not know the type
/// ignores it, and an agent that never sends it behaves exactly as before.
///
/// **Per turn, not per call** (03_session.md §7 (`S44`–`S45`)). One reply is several model calls — retrieval rounds,
/// and the repair of a move that missed — so a fact published per call would strobe. This goes out
/// once, when the first call of a turn leaves, and [`MSG_TURN_FINISHED`] goes out once, when the
/// reply has been delivered.
///
/// It exists because the seconds between handing the model a turn and hearing the first word back
/// are exactly the seconds in which a companion working hard and a hung terminal look identical:
/// the thought deltas do not start until the model does, and measured on 2026-09-18 p95 was 15.9s.
pub const MSG_TURN_STARTED: &str = "turn_started";
/// `turn_finished` — agent → core: that turn's reply has been delivered.
///
/// The other half of [`MSG_TURN_STARTED`], and the reason it is not one message with a boolean: the
/// start is sent from the first model call and the end from the end of the pass, which is later —
/// the reply still has to be delivered — and the two are paired by the agent, so a pass that made no
/// call (a tick that decided to say nothing) lights nothing and sends nothing at all. The core still
/// treats an end it never saw a beginning for as a no-op, because a lamp is not worth a failure.
pub const MSG_TURN_FINISHED: &str = "turn_finished";
/// `tool` — agent → core: one tool or extension call, for the panel (03_session.md §4.3 (`S32`–`S34`)).
///
/// Diagnosis, not conversation: `/debugon` puts these lines in the companion's pane so the player
/// can watch it reach — `type(\"north\")`, `recall(\"patrol\")`, `note_route(…)` — and the core
/// decides who sees them. Additive on top of §6.4, and zero-cost when the trace is off, because the
/// agent does not send them at all: the core tells it the state the way it tells it the danger
/// level.
pub const MSG_TOOL: &str = "tool";
/// `system` — core → agent: session events (connect/disconnect/lag/autosave/…).
pub const MSG_SYSTEM: &str = "system";
/// `vocabulary` — core → agent: the words the current game's parser recognises,
/// read from the story file's dictionary. Sent after `welcome` and on every game
/// change; a client that does not know the type ignores it.
pub const MSG_VOCABULARY: &str = "vocabulary";
/// `handover` — core → agent: the player asked the companion to type one line.
///
/// This is `/type` (03_session.md §8.1 (`S53`)), and it is a *request*, not a route: "can you type ……".
/// Additive on top of §6.4 — an agent that does not know the type ignores it, and that is
/// exactly the behaviour before it existed, when the core wrote the line itself.
pub const MSG_HANDOVER: &str = "handover";

/// `player_typing` — core → agent: the player is composing a line and has not sent it.
///
/// Additive on top of §6.4: an agent that does not know the type ignores it and behaves exactly
/// as it did before, which is the whole contract for a new message here. It exists for one
/// failure (issue #42) — the companion asks the player a question, the player starts typing the
/// answer, and ninety seconds later the companion decides nobody is coming and carries on without
/// them. A quiet room and a keyboard in use are different things, and this is the only place that
/// difference is known.
pub const MSG_PLAYER_TYPING: &str = "player_typing";

/// `player_composing` — core → agent: what the line being composed in the box looks like, in the
/// one respect the companion is allowed to act on (03_session.md §6 (`S41`–`S43`)).
///
/// A space typed as the *first* character of a line is the player's own signal to hold: the natural
/// way to stop somebody talking over you is to start typing, and the natural thing to type while
/// composing a sentence is nothing at all. The core does not decide that; it reports the fact, and
/// the agent holds.
///
/// The payload is one bit and deliberately no more — the text being typed is never sent anywhere,
/// which is the same reason `player_typing` carries nothing at all. Sent only when the answer
/// changes, so a line costs at most two messages however fast somebody types.
pub const MSG_PLAYER_COMPOSING: &str = "player_composing";

/// One envelope, exactly as it appears on the wire.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Envelope {
    /// Protocol version.
    pub v: u64,
    /// Message type, snake_case.
    #[serde(rename = "type")]
    pub msg_type: String,
    /// Monotonically increasing per-direction sequence number.
    pub seq: u64,
    /// RFC 3339 timestamp.
    pub ts: String,
    /// Type-specific object.
    #[serde(default = "null_payload")]
    pub payload: Value,
}

fn null_payload() -> Value {
    Value::Null
}

impl Envelope {
    /// Build an outbound envelope stamped with the current time.
    pub fn new(msg_type: impl Into<String>, seq: u64, payload: Value) -> Self {
        Self {
            v: PROTOCOL_VERSION,
            msg_type: msg_type.into(),
            seq,
            ts: now_rfc3339(),
            payload,
        }
    }

    /// Parse one JSONL line into an envelope.
    pub fn parse(line: &str) -> Result<Self> {
        let line = line.trim();
        if line.is_empty() {
            bail!("empty protocol line");
        }
        let env: Envelope = serde_json::from_str(line)
            .with_context(|| format!("malformed seat-protocol envelope: {}", excerpt(line)))?;
        Ok(env)
    }

    /// Serialise to a single `\n`-terminated JSONL line.
    pub fn to_line(&self) -> Result<String> {
        let mut s = serde_json::to_string(self).context("failed to serialise envelope")?;
        s.push('\n');
        Ok(s)
    }

    /// Reject a peer that speaks a different protocol version (§6.2).
    pub fn check_version(&self) -> Result<()> {
        if self.v != PROTOCOL_VERSION {
            bail!(
                "seat protocol version mismatch: peer speaks v{}, this end speaks v{}",
                self.v,
                PROTOCOL_VERSION
            );
        }
        Ok(())
    }

    /// Deserialise the payload into a typed structure.
    pub fn payload_as<T: DeserializeOwned>(&self) -> Result<T> {
        serde_json::from_value(self.payload.clone()).with_context(|| {
            format!(
                "payload of `{}` does not match the expected shape: {}",
                self.msg_type,
                excerpt(&self.payload.to_string())
            )
        })
    }
}

// ---------------------------------------------------------------------------
// Inbound payloads (agent → core)
// ---------------------------------------------------------------------------

/// `hello` payload.
#[derive(Debug, Clone, Deserialize)]
pub struct HelloPayload {
    pub protocol_version: u64,
    #[serde(default)]
    pub client_name: String,
}

/// `game_input` payload.
#[derive(Debug, Clone, Deserialize)]
pub struct GameInputPayload {
    pub line: String,

    /// Is this a keystroke rather than a command line?
    ///
    /// The bytes are the same either way — both are a line written to frotz — but the *intent*
    /// is not, and the core needs to know it for two reasons. An empty line is a bare Return,
    /// which is exactly what a `<Pulse una Tecla>` wants and is otherwise a mistake that used to
    /// be dropped silently; and a line of one space is a space *key* rather than a typo, so it
    /// must not be trimmed on the way through. Additive: absent means an ordinary command.
    #[serde(default)]
    pub key: bool,
}

/// `player_line` payload — the words, and nothing else.
///
/// There is no `seat` field and no player identity, because there is nothing on this transport to
/// identify: a `--stdio` process has one seat, and the core knows which one sent the line. Adding
/// one would be inventing an authentication story for a development transport.
#[derive(Debug, Clone, Deserialize)]
pub struct PlayerLinePayload {
    pub text: String,
}

/// `chat` payload from the agent: the core→agent direction adds `speaker`, which
/// is built by `Outbound::into_wire` rather than deserialised here.
#[derive(Debug, Clone, Deserialize)]
pub struct ChatPayload {
    pub text: String,
}

/// `ask` payload.
#[derive(Debug, Clone, Deserialize)]
pub struct AskPayload {
    pub text: String,
    #[serde(default)]
    pub proposal_id: Option<String>,
}

/// `proposal` payload.
#[derive(Debug, Clone, Deserialize)]
pub struct ProposalPayload {
    pub id: String,
    pub action: String,
    #[serde(default)]
    pub reasoning: String,
    #[serde(default = "default_risk")]
    pub risk: String,
}

fn default_risk() -> String {
    "medium".to_string()
}

/// `tool` payload: one call the companion made, as its name and a short argument.
#[derive(Debug, Clone, Deserialize)]
pub struct ToolPayload {
    pub name: String,
    #[serde(default)]
    pub detail: Option<String>,
}

// ---------------------------------------------------------------------------
// Turn framing payload (§7)
// ---------------------------------------------------------------------------

/// A framed turn: `input` plus everything frotz printed until it was ready again.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Turn {
    /// The single input line that opened this turn.
    ///
    /// Empty for a turn that frotz opened on its own (the startup intro, a death
    /// message) — §7 edge cases.
    pub input: String,
    /// Everything frotz printed for this turn, verbatim, minus the trailing
    /// prompt line.
    pub output: String,
    /// Milliseconds from the input being written to the turn being framed.
    pub elapsed_ms: u64,
    /// What the story's own parser made of this turn's line (02_game_boundary.md §3 (`G9`–`G15`)), when the
    /// interpreter could tell us: the words its dictionary accepted and the ones it did not.
    ///
    /// This is the story's parser answering, not a reading of the game's prose — a word the
    /// dictionary does not have is stored with a zero address by the Z-machine itself — and it
    /// is the difference between "it understood the verb and not the noun" and "it did not
    /// understand any of it", which nothing in the output can say.
    ///
    /// `None` on an interpreter without the tool channel, and on turns that took no line.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parser: Option<String>,
    /// The interpreter's own screen, when the story was waiting for a key (issue #19).
    ///
    /// `awaiting_command: false` says *"the game is waiting for something that is not a command;
    /// the companion is the only thing that can answer"* — and until there was a screen channel,
    /// the companion was asked to answer a prompt whose words it had never been shown. The dumb
    /// interface prints a row only when a cell has changed *and* is not blank, so a licence form
    /// or a menu drawn once and left alone is on the screen for the whole game and in the output
    /// for none of it. This is that screen: the rows that say something, trimmed, in order.
    ///
    /// `None` on an interpreter without the tool channel — where the old behaviour, and the old
    /// limitation, is exactly what it was.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub screen: Option<String>,
    /// Monotonic turn counter, starting at 1.
    pub turn: u64,
    /// Did the turn end at frotz's own `>` prompt, ready for a command?
    ///
    /// `false` means the game is waiting for something that is not a command — a chapter
    /// menu, a licence form, a `(R)estore?` offer, frotz's own `Are you sure?`. The words
    /// are the *story's*, so nothing in the core can read them; the companion is the only
    /// thing in the system that can answer, and this flag is how it is asked to (issue #19).
    ///
    /// Defaults to `true` so a peer that does not send the field behaves as it always did.
    #[serde(default = "default_awaiting_command")]
    pub awaiting_command: bool,
    /// Was this the core's own doing, rather than anybody asking for it?
    ///
    /// Two ways it happens, and **neither needs a word of the game's text to recognise**:
    ///
    /// * the core wrote the line itself (`InputSource::Scripted`) — a checkpoint's `save`
    ///   and filename, or the `look` issued to fill the screen after a restore;
    /// * nothing has been typed yet and the game was *resumed from a checkpoint*, so what
    ///   came back is the interpreter's own restore confirmation.
    ///
    /// This replaces a table of per-game save wordings — `Okay, done.`, `[Okay.]`, `O.K.` —
    /// which was a per-game table by construction: the wording belongs to the interpreter,
    /// and an interpreter is free to say it in any language. It broke the moment that text
    /// stopped being swallowed, and it would have broken again the day a Spanish player ran
    /// this. Nothing in the core matches a word of the game's output, ever.
    ///
    /// Defaults to `false` so a peer that does not send the field behaves as it always did.
    #[serde(default)]
    pub machinery: bool,
    /// Where the interpreter says the player is, as its own object number for the room (B4).
    ///
    /// Filled by the harness out of the tool channel's status, which is the interpreter's own
    /// report of its status line — a *number*, not a reading of the story's prose, which is what
    /// makes it usable as a place identity. It is `None` on an interpreter without the channel and
    /// on a story that keeps no status line (a Version 5 story draws its own), and every reader has
    /// to treat `None` as *cannot say* rather than as a place.
    ///
    /// The turn record's fingerprints are an index for finding similar turns and never an identity
    /// (02_game_boundary.md §6 (`G27`–`G34`)): the same room prints differently between visits, and a turn that did not move
    /// still prints something. This is the field that says where the story put the player.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub location: Option<i64>,
    /// The interpreter's own score, where it keeps the status line (B4).
    ///
    /// The rest of the same report as `location`, and absent for the same reasons: a story that
    /// keeps its own status line has no counters for the interpreter to state
    /// (02_game_boundary.md §7 (`G38`)), and an interpreter with no channel cannot be asked. Every
    /// reader MUST treat `None` as *cannot say* rather than as zero, which is the rule the location
    /// already carries and the one thing a count can be mistaken for.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub score: Option<i64>,
    /// The interpreter's own count of the player's moves, in the same sense as [`Turn::score`].
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub moves: Option<i64>,
    /// The hour on the interpreter's own clock, for a game that keeps one instead of counters.
    ///
    /// A story with a clock has the time of day on its status line where a scored game has the
    /// score and the moves — the interpreter knows which it is (`CONFIG_TIME`), so this side does
    /// not have to guess and MUST NOT: a missing hour is *this game keeps no clock*, never
    /// midnight.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hours: Option<i64>,
    /// The minute on that clock, in the same sense as [`Turn::hours`].
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub minutes: Option<i64>,
}

impl Turn {
    /// The interpreter's own status line, as one line — `None` when it reported none.
    ///
    /// The form is a game that keeps a clock, or a game that keeps counters, because those are the
    /// two things a status line is made of. The room is the interpreter's **object number** and
    /// deliberately not its name: the name is drawn into the top row of the screen by cursor
    /// addressing, so it is in no turn's output and nothing here may read a picture as words
    /// (02_game_boundary.md §4.2.1 (`G22`), §4.3 (`G23`)).
    ///
    /// `None` covers both reasons there may be nothing to say — the story draws its own bar
    /// (`G38`), or there is no channel — and a reader that needs to tell those apart has to ask the
    /// core, which knows the story's version and this one does not.
    pub fn status_line(&self) -> Option<String> {
        let location = self.location?;
        Some(match self.hours {
            Some(hours) => format!(
                "at object {location} — {:02}:{:02}",
                hours,
                self.minutes.unwrap_or(0)
            ),
            None => format!(
                "at object {location} — score {}, moves {}",
                self.score.unwrap_or(0),
                self.moves.unwrap_or(0),
            ),
        })
    }
}

fn default_awaiting_command() -> bool {
    true
}

// ---------------------------------------------------------------------------
// Outbound writer
// ---------------------------------------------------------------------------

/// Serialises envelopes down a single byte sink, owning the outbound sequence
/// number. One writer per seat direction: this is what guarantees that no two
/// protocol lines are ever interleaved (§6.6).
pub struct EnvelopeWriter<W> {
    inner: W,
    seq: u64,
}

impl<W: AsyncWrite + Unpin> EnvelopeWriter<W> {
    pub fn new(inner: W) -> Self {
        Self { inner, seq: 0 }
    }

    /// Write one envelope. `seq` increments by one for every message.
    pub async fn send(&mut self, msg_type: &str, payload: Value) -> Result<()> {
        self.seq += 1;
        let line = Envelope::new(msg_type, self.seq, payload).to_line()?;
        self.inner
            .write_all(line.as_bytes())
            .await
            .context("failed to write protocol line")?;
        self.inner
            .flush()
            .await
            .context("failed to flush protocol line")?;
        Ok(())
    }
}

// ---------------------------------------------------------------------------
// Timestamps without a date library
// ---------------------------------------------------------------------------

/// Current time as RFC 3339 / ISO 8601 in UTC, e.g. `2026-09-11T00:26:00Z`.
///
/// Hand-rolled rather than pulling in `chrono`: the envelope only needs one
/// format, and the conversion is a well-known civil-calendar algorithm with
/// unit tests below.
pub fn now_rfc3339() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    rfc3339_from_unix(secs)
}

/// Render a Unix timestamp (seconds) as RFC 3339 UTC.
pub fn rfc3339_from_unix(secs: u64) -> String {
    let days = (secs / 86_400) as i64;
    let rem = secs % 86_400;
    let (year, month, day) = civil_from_days(days);
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z",
        year,
        month,
        day,
        rem / 3600,
        (rem % 3600) / 60,
        rem % 60
    )
}

/// Is one of this program's own stamps earlier than another?
///
/// Written for one job — deciding which of two saves is the more recent position (03_session.md §10 (`S67`),
/// "resume where they left off") — and deliberately *not* a date parser. Every stamp compared has
/// to come from [`rfc3339_from_unix`]: same zone (`Z`), same width, zero-padded, whole seconds, so
/// byte order is time order. The shape is checked rather than assumed, because the other source of
/// these strings is a `.json` record on disk that a person can edit, and a hand-written stamp is a
/// save that cannot say when it was taken — which must come out as *unknown* rather than as a wrong
/// answer. `false` means "not provably earlier", so a caller that wants a definite answer has to
/// ask twice.
pub fn an_earlier_stamp(left: &str, right: &str) -> bool {
    is_a_stamp(left) && is_a_stamp(right) && left < right
}

/// Does this text have the exact shape [`rfc3339_from_unix`] produces?
fn is_a_stamp(text: &str) -> bool {
    let bytes = text.as_bytes();
    bytes.len() == 20
        && bytes.iter().enumerate().all(|(i, byte)| match i {
            4 | 7 => *byte == b'-',
            10 => *byte == b'T',
            13 | 16 => *byte == b':',
            19 => *byte == b'Z',
            _ => byte.is_ascii_digit(),
        })
}

/// Howard Hinnant's `civil_from_days`: days since 1970-01-01 → (y, m, d).
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64; // [0, 146096]
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365; // [0, 399]
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100); // [0, 365]
    let mp = (5 * doy + 2) / 153; // [0, 11]
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32; // [1, 31]
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32; // [1, 12]
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// Shorten a string for a log line without splitting UTF-8.
pub fn excerpt(s: &str) -> String {
    const MAX: usize = 160;
    let trimmed = s.trim();
    if trimmed.chars().count() <= MAX {
        return trimmed.to_string();
    }
    let head: String = trimmed.chars().take(MAX).collect();
    format!("{}…", head)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn envelope_round_trips_as_one_jsonl_line() {
        let env = Envelope::new(MSG_TURN_COMPLETE, 7, json!({ "input": "look" }));
        let line = env.to_line().unwrap();
        assert!(line.ends_with('\n'));
        assert_eq!(line.matches('\n').count(), 1, "one object per line");

        let back = Envelope::parse(&line).unwrap();
        assert_eq!(back.msg_type, MSG_TURN_COMPLETE);
        assert_eq!(back.seq, 7);
        assert_eq!(back.v, PROTOCOL_VERSION);
        assert_eq!(
            back.payload.get("input").and_then(Value::as_str),
            Some("look")
        );
    }

    #[test]
    fn version_mismatch_is_fatal() {
        let mut env = Envelope::new(MSG_HELLO, 1, json!({}));
        env.v = 99;
        assert!(env.check_version().is_err());
        env.v = PROTOCOL_VERSION;
        assert!(env.check_version().is_ok());
    }

    #[test]
    fn malformed_lines_are_rejected_with_a_readable_error() {
        assert!(Envelope::parse("not json at all").is_err());
        assert!(Envelope::parse("").is_err());
        // A valid JSON object that is not an envelope must not silently pass.
        assert!(Envelope::parse(r#"{"hello":"world"}"#).is_err());
    }

    #[test]
    fn typed_payloads_are_checked() {
        let env = Envelope::new(MSG_GAME_INPUT, 1, json!({ "line": "north" }));
        let p: GameInputPayload = env.payload_as().unwrap();
        assert_eq!(p.line, "north");

        let bad = Envelope::new(MSG_GAME_INPUT, 1, json!({ "nope": 1 }));
        assert!(bad.payload_as::<GameInputPayload>().is_err());
    }

    #[test]
    fn a_keystroke_says_that_it_is_one() {
        // The bytes are identical to a command line, so this flag is the only thing that says
        // "the empty line is a Return somebody meant, not a mistake to drop" — and the reason the
        // core needs telling is that it used to drop the empty one silently, so a companion that
        // worked out what a `<Pulse una Tecla>` was asking for got nothing for its trouble.
        let command = Envelope::new(MSG_GAME_INPUT, 1, json!({ "line": "north" }));
        assert!(!command.payload_as::<GameInputPayload>().unwrap().key);

        let bare_return = Envelope::new(MSG_GAME_INPUT, 2, json!({ "line": "", "key": true }));
        let payload = bare_return.payload_as::<GameInputPayload>().unwrap();
        assert!(payload.key);
        assert!(payload.line.is_empty());

        // A space is a key as well, and it has to survive parsing intact: frotz hands the game
        // the first character of the line, so `" "` and `""` are genuinely different inputs.
        let space = Envelope::new(MSG_GAME_INPUT, 3, json!({ "line": " ", "key": true }));
        assert_eq!(space.payload_as::<GameInputPayload>().unwrap().line, " ");
    }

    #[test]
    fn optional_proposal_fields_default() {
        let env = Envelope::new(
            MSG_PROPOSAL,
            1,
            json!({ "id": "p1", "action": "bribe troll" }),
        );
        let p: ProposalPayload = env.payload_as().unwrap();
        assert_eq!(p.risk, "medium");
        assert_eq!(p.reasoning, "");
    }

    #[test]
    fn a_turns_status_line_is_the_interpreters_own_numbers() {
        // The two shapes a status line has, and the case that is not a shape at all. Nothing here
        // is a reading of the game's prose: the location is the interpreter's object number for the
        // room, and the numbers are the interpreter's own globals — which is what makes them usable
        // by `/bar` and by the companion's `status` alike.
        let counted = Turn {
            turn: 12,
            location: Some(180),
            score: Some(0),
            moves: Some(19),
            ..Turn::default()
        };
        assert_eq!(
            counted.status_line().as_deref(),
            Some("at object 180 — score 0, moves 19")
        );

        // A game with a clock puts the time of day where a scored game puts the counters.
        let clocked = Turn {
            turn: 12,
            location: Some(94),
            hours: Some(9),
            minutes: Some(5),
            ..Turn::default()
        };
        assert_eq!(
            clocked.status_line().as_deref(),
            Some("at object 94 — 09:05")
        );

        // A story that draws its own bar says nothing at all. A blank or a zero here would be the
        // invention the whole channel exists to avoid (`G38`), so the absence is the answer.
        let none = Turn {
            turn: 12,
            ..Turn::default()
        };
        assert_eq!(none.status_line(), None);
    }

    #[test]
    fn rfc3339_matches_known_instants() {
        assert_eq!(rfc3339_from_unix(0), "1970-01-01T00:00:00Z");
        assert_eq!(rfc3339_from_unix(86_399), "1970-01-01T23:59:59Z");
        assert_eq!(rfc3339_from_unix(86_400), "1970-01-02T00:00:00Z");
        // 2026-01-01T00:00:00Z
        assert_eq!(rfc3339_from_unix(1_767_225_600), "2026-01-01T00:00:00Z");
        // 2026-09-11T00:00:00Z
        assert_eq!(rfc3339_from_unix(1_789_084_800), "2026-09-11T00:00:00Z");
        // Leap day.
        assert_eq!(rfc3339_from_unix(1_709_164_800), "2024-02-29T00:00:00Z");
    }

    #[test]
    fn stamps_written_by_this_program_compare_as_text() {
        // The claim the save choice rests on: same zone, same width, zero-padded, whole seconds —
        // so byte order is time order, and no date library is needed to ask which save is newer.
        assert!(an_earlier_stamp(
            "2026-09-20T21:00:00Z",
            "2026-09-20T21:00:01Z"
        ));
        assert!(an_earlier_stamp(
            "2026-09-20T09:59:59Z",
            "2026-09-20T10:00:00Z"
        ));
        assert!(an_earlier_stamp(
            "2025-12-31T23:59:59Z",
            "2026-01-01T00:00:00Z"
        ));
        assert!(!an_earlier_stamp(
            "2026-09-20T21:00:00Z",
            "2026-09-20T21:00:00Z"
        ));

        // And anything this program did not write is not compared at all. A record with an offset,
        // fractional seconds, a space instead of `T` or a two-digit year is a save that cannot say
        // when it was taken, and `false` is the honest answer to "is it earlier".
        assert!(!an_earlier_stamp(
            "2026-09-20T21:00:00+01:00",
            "2026-09-20T22:00:00Z"
        ));
        assert!(!an_earlier_stamp(
            "2026-09-20T21:00:00.500Z",
            "2026-09-20T22:00:00Z"
        ));
        assert!(!an_earlier_stamp(
            "2026-09-20 21:00:00Z",
            "2026-09-20T22:00:00Z"
        ));
        assert!(!an_earlier_stamp("", "2026-09-20T22:00:00Z"));
        assert!(!an_earlier_stamp("yesterday", "2026-09-20T22:00:00Z"));
    }

    #[tokio::test]
    async fn writer_numbers_sequence_monotonically() {
        let mut buf: Vec<u8> = Vec::new();
        {
            let mut w = EnvelopeWriter::new(&mut buf);
            w.send(MSG_WELCOME, json!({})).await.unwrap();
            w.send(MSG_GAME_OUTPUT, json!({ "data": "hi" }))
                .await
                .unwrap();
        }
        let text = String::from_utf8(buf).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 2);
        assert_eq!(Envelope::parse(lines[0]).unwrap().seq, 1);
        assert_eq!(Envelope::parse(lines[1]).unwrap().seq, 2);
    }

    #[test]
    fn excerpt_never_splits_multibyte_characters() {
        let s = "é".repeat(500);
        let e = excerpt(&s);
        assert!(e.ends_with('…'));
        assert_eq!(e.chars().count(), 161);
    }
}
