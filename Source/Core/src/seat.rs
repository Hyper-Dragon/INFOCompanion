//! Seats (issues #3, #5): the core's "the agent is just another seat" abstraction.
//!
//! A **seat** is any entity that can send input to the core and receive the event
//! stream. SSH players, the `--stdio` dev transport and the C# agent are all seats;
//! the only difference the core cares about is whether a seat talks to a *human
//! terminal* or speaks the *seat protocol*.
//!
//! Outbound traffic is per-seat bounded queues rather than one broadcast channel,
//! because the two seat families need opposite backpressure policies (§6.5):
//!
//! * **terminal seats** must never lose game text, and are cheap to keep up with;
//! * **protocol seats** (the agent) may be slow, and a slow agent must never stall
//!   the game — `game_output` chunks are droppable, `turn_complete` is not.

use crate::conversation::ConversationLog;
use crate::game::InputSource;
use crate::playback::Playback;
use crate::screen::{as_playback_text, ScreenLog};
use crate::seat_protocol::{
    Turn, MSG_CHAT, MSG_GAME_OUTPUT, MSG_HANDOVER, MSG_PLAYER_COMPOSING, MSG_PLAYER_TYPING,
    MSG_SYSTEM, MSG_TURN_COMPLETE, MSG_VOCABULARY, MSG_WELCOME,
};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use tokio::sync::{mpsc, Mutex, MutexGuard, RwLock};
use tracing::{debug, warn};

/// Queue depth for a terminal seat's outbound channel when nothing says otherwise.
///
/// From `core-config.json` (`terminal_queue`); these are the values a registry built without a
/// tuning — which is what the tests build — runs on.
const TERMINAL_QUEUE: usize = crate::config::DEFAULT_TERMINAL_QUEUE;
/// Queue depth for a protocol seat's outbound channel when nothing says otherwise
/// (`protocol_queue`).
const PROTOCOL_QUEUE: usize = crate::config::DEFAULT_PROTOCOL_QUEUE;

/// Identifies a seat for the lifetime of its connection.
pub type SeatId = u64;

/// What kind of seat this is. The core treats every seat identically on the input
/// path; this only selects the outbound rendering and queueing policy.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SeatKind {
    /// An SSH player at a terminal.
    Ssh,
    /// The supervised C# co-player agent (or a dev-mode stand-in).
    Agent,
    /// The `--stdio` dev/test transport.
    Stdio,
}

impl SeatKind {
    /// Protocol seats are spoken to in JSONL envelopes.
    pub fn is_protocol(self) -> bool {
        matches!(self, SeatKind::Agent | SeatKind::Stdio)
    }

    /// A short human label used in logs and system notices.
    pub fn label(self) -> &'static str {
        match self {
            SeatKind::Ssh => "ssh",
            SeatKind::Agent => "agent",
            SeatKind::Stdio => "stdio",
        }
    }
}

/// Messages a *terminal* seat renders for a human (§12).
#[derive(Debug, Clone)]
pub enum ClientMessage {
    /// Raw game bytes, exactly as frotz produced it.
    GameOutput(Vec<u8>),
    /// A companion utterance.
    Companion(String),
    /// A core notice.
    System(String),
    /// A line the core wrote into the game: the companion's command, or a forced one.
    ///
    /// frotz reads from a pipe and therefore echoes nothing, so without this the game pane
    /// shows the game answering questions nobody appears to have asked. Rendering it inline
    /// after the `>` prompt makes the transcript read like an ordinary session.
    Input { line: String, source: InputSource },
    /// The companion's reasoning, revealed by `/think`.
    Thought(String),
    /// A chunk of the companion's reasoning, as it arrives.
    ///
    /// Not a smaller [`ClientMessage::Thought`]: a thought is printed as a finished block, with
    /// its tag on every line, and a pane that re-printed the growing text would reprint it.
    /// This is the stream that the finished thought then closes.
    ThoughtDelta(String),
    /// Something the player said. Quill's pane is a conversation, and one half of a
    /// conversation does not read: without this the pane shows replies to things that were
    /// never said. The window the player typed in is left out — it echoed their keystrokes
    /// as they typed, so a second copy would be a stutter.
    Player(String),
    /// The answer to a `/` command the player typed, sent only to the pane that asked.
    ///
    /// Every other pane reads the same text as a [`ClientMessage::System`] notice. This
    /// exists because the typing pane has to hear back about its own commands: `>> <cmd>`
    /// and then silence is indistinguishable from a hang (issue #25).
    Command(String),
    /// Bytes this pane should write and no other pane ever sees: the keystroke echo, the prompt,
    /// and the one-line hint a read-only pane gives the first time somebody types in it
    /// (03_session.md §5 (`S35`–`S40`)).
    ///
    /// Already rendered, because the pane's own transport is what knows what the player's
    /// keystrokes should look like on the way back. It travels through the seat's stream rather
    /// than being written straight at the socket, and that is the whole point of it existing:
    /// a pane has **one** writer, so a keystroke cannot land in the middle of a sentence the pace
    /// is still delivering. It is in neither record — the echo of a line the player typed is not
    /// history, and a prompt is not a thing that was said.
    Echo(Vec<u8>),
    /// The companion's lamp, lit for the whole of a turn and dark between them (03_session.md §7 (`S44`–`S45`)).
    ///
    /// A fact about the companion, not about the pane it arrives in: it is rendered as a terminal
    /// escape the pane swallows, which is how the page is told something without being shown
    /// anything, and it is the reason the lamps can say "working" during the seconds in which
    /// nothing has arrived yet. The core owns every escape in a seat (02_game_boundary.md), so this is the core
    /// speaking, and it is deliberately not part of either record: a lamp is not history.
    Lamp { on: bool },
    /// The player's own signal that the line in the box begins with a space (03_session.md §6 and §7 (`S41`, `S45`)).
    ///
    /// The lamp's sibling: a state rather than a line, sent to the one pane being typed in, rendered
    /// as a terminal escape that pane swallows. It is not a line for the lamp's reason and one more
    /// — the moment this changes is the moment the player is mid-sentence, so anything occupying a
    /// line would push their half-written words up the screen. In neither record: the page is told a
    /// present state, not shown a line of history.
    ///
    /// A message of its own rather than bytes through [`ClientMessage::Echo`] because the pane's
    /// writer has to be able to tell an escape from ink, and an `Echo` is the player's own
    /// characters coming back — where *those* leave the cursor is a real question, so the pane
    /// counts them. Measured 2026-09-22, while it travelled as an `Echo`: the typing pane lost its
    /// prompt on every line submitted, because the hold is written between the newline and the
    /// prompt.
    Hold { on: bool },
    /// The typing pane's prompt: "type here".
    ///
    /// Sent when the game is ready for input again, when a `/` command has been answered, and
    /// to a pane that logs in mid-session. The pane also lays this down itself on every Return
    /// (issue #37) — see `SshClientHandler::lay_prompt` — so this message is what puts the
    /// prompt back after the things only the core knows about.
    Ready,
}

/// Messages a *protocol* seat receives.
#[derive(Debug, Clone)]
pub enum Outbound {
    /// A chunk of raw frotz text (§6.3 `game_output`).
    GameOutput { data: String },
    /// A framed turn (§6.3 `turn_complete`).
    TurnComplete(Turn),
    /// A player utterance addressed to the agent (§6.3 `chat`).
    Chat { speaker: String, text: String },
    /// The player asking the companion to type one line (§6.4 `handover`).
    ///
    /// `/type` (03_session.md §8.1 (`S53`)). A *request*, not a route — the companion may object once and
    /// then types it — which is why it is neither a `Chat` (the player is not talking to it)
    /// nor a `System` event (the core is not reporting something; it is asking for something).
    Handover { line: String },
    /// The player is typing something and has not sent it yet (§6.4 `player_typing`, issue #42).
    ///
    /// Not a `Chat`: there are no words in it. Not a `System` event either — nothing has
    /// happened; the core is telling the agent about the next few seconds, because a companion
    /// that gives up on an answer while the answer is being typed has misread the room.
    PlayerTyping,
    /// What the line in the box looks like, in the one respect the player can hold the companion
    /// with (03_session.md §6 (`S41`–`S43`)): whether its first character is a space.
    ///
    /// A fact about the composing line, not an instruction: the core reports what is in the box
    /// and the agent decides what it means — which is the same division as `player_typing`, and the
    /// reason a signal like this cannot quietly become the core guessing at a half-typed sentence.
    /// The text itself is never sent.
    PlayerComposing { starts_with_space: bool },
    /// A session event (§6.3 `system`).
    System { event: String, detail: String },
    /// The handshake response (§6.3 `welcome`).
    Welcome {
        game_title: String,
        /// The game's short name, so the agent knows which notebook to load.
        game_stem: String,
        turn_count: u64,
        transcript_tail: Vec<String>,
        /// What the player calls the companion, so the agent knows its own name.
        agent_name: String,
        /// Which attempt at this game this is (02_game_boundary.md §6 (`G27`–`G34`)), 0 when the core has no bookkeeping
        /// for it. A redeploy continues an attempt; only a restart, a switch away or a reset
        /// starts a new one.
        run: u64,
        /// Which fork of the world inside that attempt: a restore moves the companion without
        /// making this a different go (02_game_boundary.md §6 (`G27`–`G34`)).
        generation: u64,
        /// Did the core come back to a position rather than starting the story?
        resumed: bool,
        /// The save it came back with, when it did.
        resumed_from: Option<String>,
        /// The core's turn counter when that checkpoint was taken (05_memory.md §5 (`M23`–`M26`)).
        ///
        /// Load-bearing rather than polite: the autosave is every few turns, so the world that
        /// just came back can be several moves *behind* the conversation. Without this the
        /// companion picks the conversation up where it left it and describes a world the
        /// checkpoint has already undone.
        resumed_at_turn: Option<u64>,
        /// When it was taken, RFC 3339, from the record beside the checkpoint.
        resumed_saved_at: Option<String>,
    },
    /// The words the current game's parser recognises (§10.4 `vocabulary`).
    ///
    /// Sent after `welcome` and again whenever the game changes, because the
    /// vocabulary belongs to the story file and another story has another parser.
    Vocabulary {
        game_stem: String,
        /// Z-machine version the words were read from.
        version: u8,
        /// Z-characters of text a dictionary entry holds: 6 for V1-3, 9 for V4+.
        /// The agent needs this to match words the way the interpreter does.
        resolution: usize,
        words: Vec<String>,
    },
}

impl Outbound {
    /// Convenience constructor for a `system` event.
    pub fn system(event: impl Into<String>, detail: impl Into<String>) -> Self {
        Outbound::System {
            event: event.into(),
            detail: detail.into(),
        }
    }

    /// The wire message type and payload for this message (§6.3).
    ///
    /// Kept here so every protocol transport (the agent supervisor and the stdio
    /// dev transport) frames messages identically.
    pub fn into_wire(self) -> (&'static str, Value) {
        match self {
            Outbound::GameOutput { data } => (MSG_GAME_OUTPUT, json!({ "data": data })),
            Outbound::TurnComplete(turn) => (
                MSG_TURN_COMPLETE,
                serde_json::to_value(turn).unwrap_or(Value::Null),
            ),
            Outbound::Chat { speaker, text } => {
                (MSG_CHAT, json!({ "speaker": speaker, "text": text }))
            }
            Outbound::Handover { line } => (MSG_HANDOVER, json!({ "line": line })),
            Outbound::PlayerTyping => (MSG_PLAYER_TYPING, json!({})),
            Outbound::PlayerComposing { starts_with_space } => (
                MSG_PLAYER_COMPOSING,
                json!({ "starts_with_space": starts_with_space }),
            ),
            Outbound::System { event, detail } => {
                (MSG_SYSTEM, json!({ "event": event, "detail": detail }))
            }
            Outbound::Welcome {
                game_title,
                game_stem,
                turn_count,
                transcript_tail,
                agent_name,
                run,
                generation,
                resumed,
                resumed_from,
                resumed_at_turn,
                resumed_saved_at,
            } => (
                MSG_WELCOME,
                json!({
                    "game_title": game_title,
                    "game_stem": game_stem,
                    "turn_count": turn_count,
                    "transcript_tail": transcript_tail,
                    "agent_name": agent_name,
                    "run": run,
                    "generation": generation,
                    "resumed": resumed,
                    "resumed_from": resumed_from,
                    "resumed_at_turn": resumed_at_turn,
                    "resumed_saved_at": resumed_saved_at,
                }),
            ),
            Outbound::Vocabulary {
                game_stem,
                version,
                resolution,
                words,
            } => (
                MSG_VOCABULARY,
                json!({
                    "game_stem": game_stem,
                    "version": version,
                    "resolution": resolution,
                    "words": words,
                }),
            ),
        }
    }
}

/// How far behind a pane's own writing is (03_session.md §5 (`S35`–`S40`)).
///
/// The pace makes a promise that only the panes can keep: *a command cannot reach the story before
/// the line announcing it has finished appearing*. The words arrive a word at a time, so "finished
/// appearing" is a fact about a pane's writer, and this is where it is counted — one of these per
/// terminal seat, raised when the pane is handed a message and lowered when it has finished writing
/// it.
///
/// It is deliberately about *writing*, not about reading: the count reaches zero when the bytes
/// have left for the client, and a slow terminal on a slow link is the one thing that could hold
/// it up. The caller bounds its wait for that reason (`Router::release_held_lines`).
#[derive(Debug, Default)]
pub struct PaneProgress {
    /// Is anything actually writing to this pane yet?
    ///
    /// A seat is registered when a session channel opens, and it gets its writer only when the
    /// client asks for a shell or a terminal. A client that opens a channel and asks for neither —
    /// a health check, a port scanner, somebody's `ssh host true` — would otherwise be a pane with a
    /// backlog that nothing will ever write, and every line of the session would wait out the
    /// router's whole patience for it, once per line, for ever.
    forwarding: AtomicBool,
    /// Messages this pane has been given and has not finished writing.
    outstanding: AtomicUsize,
}

impl PaneProgress {
    /// A writer has taken this pane: from here its writing is what the screen is doing.
    pub fn begin_forwarding(&self) {
        self.forwarding.store(true, Ordering::SeqCst);
    }

    /// Nothing is writing to this pane any more. Whatever it was owed, nobody is going to wait for
    /// it — a pane whose writer has gone is not a screen that is still scrolling.
    pub fn abandon(&self) {
        self.forwarding.store(false, Ordering::SeqCst);
    }

    /// One more message is owed to this pane.
    pub fn owed(&self) {
        self.outstanding.fetch_add(1, Ordering::SeqCst);
    }

    /// One is written: the pane is quiet again when nothing is left.
    pub fn paid(&self) {
        self.outstanding.fetch_sub(1, Ordering::SeqCst);
    }

    /// Is this pane showing everything it has been given?
    ///
    /// Asked on a timer rather than awaited: see `HELD_LINE_TICK`, where the router explains why
    /// a line that is waiting for a screen is looked at rather than woken by it.
    pub fn is_quiet(&self) -> bool {
        !self.forwarding.load(Ordering::SeqCst) || self.outstanding.load(Ordering::SeqCst) == 0
    }
}

/// A pane is busy writing, and says so until this is dropped.
///
/// A guard rather than a pair of calls because the writer has early returns — a dead socket, a
/// filtered message, a channel that has closed — and it can panic: either would leave the count
/// standing, and a pane that never goes quiet holds the game back until the caller's patience runs
/// out.
///
/// Two ways to take it, and the difference is **who counted the writing**:
///
/// * [`Writing::begin`] — writing nothing counted, because it is not a message: the banner, the
///   pane's own one-line note, the record it is catching up on. The guard raises the count itself.
/// * [`Writing::settle`] — a message the seat registry has already counted as owed (`offer`). The
///   guard lowers *that* one, so the two halves are one pair and the count cannot drift.
///
/// Both lower the count exactly once when they go. The pane is also marked as having a writer from
/// the first guard it takes, which is what makes its count mean anything at all.
pub struct Writing(Arc<PaneProgress>);

impl Writing {
    /// A pane has a writer, and that writer is busy with something nobody counted.
    pub fn begin(progress: &Arc<PaneProgress>) -> Self {
        progress.begin_forwarding();
        progress.owed();
        Self(Arc::clone(progress))
    }

    /// A pane is writing a message that was counted when it was offered.
    pub fn settle(progress: &Arc<PaneProgress>) -> Self {
        progress.begin_forwarding();
        Self(Arc::clone(progress))
    }
}

impl Drop for Writing {
    fn drop(&mut self) {
        self.0.paid();
    }
}

/// The receiving half handed to a transport when it registers a seat.
pub enum SeatReceiver {
    /// A terminal seat, with the two records it has already missed (§8.4).
    ///
    /// The snapshots are taken while the seat is being registered, so they are exact rather
    /// than approximate: every message is either in here or on its way live, never both
    /// and never neither. The view decides what to do with them — the `game` pane is replayed
    /// the screen, and the pane that shows the conversation is replayed the conversation,
    /// because a record of game text has nothing in it for a pane that was never showing the
    /// game.
    Terminal {
        rx: mpsc::Receiver<ClientMessage>,
        screen: Vec<ClientMessage>,
        conversation: Vec<ClientMessage>,
        /// How far behind this pane's writing is, shared with the registry (§8.2).
        progress: Arc<PaneProgress>,
    },
    Protocol(mpsc::Receiver<Outbound>),
}

impl std::fmt::Debug for SeatReceiver {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SeatReceiver::Terminal {
                screen,
                conversation,
                ..
            } => {
                write!(
                    f,
                    "SeatReceiver::Terminal({} screen, {} conversation)",
                    screen.len(),
                    conversation.len()
                )
            }
            SeatReceiver::Protocol(_) => f.write_str("SeatReceiver::Protocol"),
        }
    }
}

/// A registered seat: an outbound sink plus the facts needed to route to it.
struct Seat {
    id: SeatId,
    name: String,
    kind: SeatKind,
    /// Can this seat type?
    ///
    /// The `game` pane and the companion's pane are read-only by design, and a seat that cannot
    /// type is not a person waiting to be spoken to. Kept here rather than derived from the name
    /// so that the rule has one home — the same one that decides which keys the pane accepts.
    keyboard: bool,
    sink: SeatSink,
    /// How far behind this pane's writing is (§8.2). `None` for a protocol seat: the agent is
    /// metered by nothing and is not a screen.
    progress: Option<Arc<PaneProgress>>,
}

enum SeatSink {
    Terminal(mpsc::Sender<ClientMessage>),
    Protocol(mpsc::Sender<Outbound>),
}

/// Both of the session's records, kept under one lock.
///
/// One lock rather than two, and it is not tidiness: a connecting pane's snapshot and the recording
/// of a live message have to be ordered against each other, and the lock is what orders them. With
/// a lock each, a message could reach the screen's record before the snapshot was taken and the
/// conversation's after it, and the pane would then either lose a line or be given it twice (§11).
#[derive(Debug)]
struct Replay {
    /// The screen: what was written into the game and what frotz wrote back.
    screen: ScreenLog,
    /// The conversation: speech, the player's lines, the notices (03_session.md §4 (`S25`–`S29`)).
    conversation: ConversationLog,
    /// The same screen, written down durably (03_session.md §4.2 (`S31`)).
    playback: Playback,
}

impl Default for Replay {
    fn default() -> Self {
        Self::build(Playback::off(), crate::config::DEFAULT_REPLAY_LINES)
    }
}

impl Replay {
    fn build(playback: Playback, replay_lines: usize) -> Self {
        Self {
            screen: ScreenLog::new(replay_lines),
            conversation: ConversationLog::new(replay_lines),
            playback,
        }
    }

    /// Record a message in whichever of the records it belongs to.
    fn push(&mut self, message: &ClientMessage) {
        self.screen.push(message);
        self.conversation.push(message);
        // And the game's own screen to disk, in the order it was printed, which is the one shape
        // of the evening that exists nowhere else (W2: an evening has to survive the model being
        // down). The recording rule is the screen's own, so nothing of ours leaks into the file.
        if let Some(text) = as_playback_text(message) {
            self.playback.screen(text);
        }
    }

    /// The screen, oldest first.
    fn screen(&self) -> Vec<ClientMessage> {
        self.screen.replay()
    }

    /// The conversation, oldest first.
    fn conversation(&self) -> Vec<ClientMessage> {
        self.conversation.replay()
    }
}

/// The seat registry. Cloning is via `Arc`, so every transport shares one table.
pub struct SeatRegistry {
    next_id: AtomicU64,
    seats: RwLock<HashMap<SeatId, Seat>>,
    /// `game_output` chunks dropped for a slow protocol seat (§6.5).
    dropped_chunks: AtomicU64,
    /// What a pane that connects late is given: the screen, and the conversation.
    replay: Mutex<Replay>,
    /// Outbound queue depths (`core-config.json`): how much may be waiting for a pane before the
    /// oldest is dropped. The terminal's is deeper on purpose — a pane being read is a person
    /// reading — and this is where the difference lives.
    terminal_queue: usize,
    protocol_queue: usize,
}

impl Default for SeatRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl SeatRegistry {
    pub fn new() -> Self {
        Self::with_playback(Playback::off())
    }

    /// The registry the daemon builds: everything `new` does, and the game's screen also written
    /// down durably as it happens (03_session.md §4.2 (`S31`)).
    pub fn with_playback(playback: Playback) -> Self {
        Self::build(
            crate::config::DEFAULT_REPLAY_LINES,
            TERMINAL_QUEUE,
            PROTOCOL_QUEUE,
            playback,
        )
    }

    /// The registry the daemon builds, at the depths and bounds in force (10_delivery.md §2 (`D5`)).
    pub fn configured(tuning: crate::config::Tuning, playback: Playback) -> Self {
        Self::build(
            tuning.replay_lines,
            tuning.terminal_queue,
            tuning.protocol_queue,
            playback,
        )
    }

    fn build(
        replay_lines: usize,
        terminal_queue: usize,
        protocol_queue: usize,
        playback: Playback,
    ) -> Self {
        Self {
            next_id: AtomicU64::new(1),
            seats: RwLock::new(HashMap::new()),
            dropped_chunks: AtomicU64::new(0),
            replay: Mutex::new(Replay::build(playback, replay_lines)),
            terminal_queue,
            protocol_queue,
        }
    }

    /// The records, taken for the shortest possible moment: they are read on every connect
    /// and written for every line of game output and every line of conversation.
    async fn replay(&self) -> MutexGuard<'_, Replay> {
        self.replay.lock().await
    }

    /// Register a seat and hand its receiver back to the transport.
    ///
    /// The snapshots are taken under the same lock `send_terminal` records with, and the lock
    /// is held until the seat is in the table. That is what makes the replay exact rather than
    /// approximate: every message is either recorded before the seat existed — so these
    /// snapshots have it and the live stream will not — or recorded afterwards, the other way
    /// round. Nothing is missed and nothing arrives twice.
    pub async fn register(
        &self,
        name: impl Into<String>,
        kind: SeatKind,
        keyboard: bool,
    ) -> (SeatId, SeatReceiver) {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let name = name.into();
        let replay = self.replay().await;
        let (seat, rx) = if kind.is_protocol() {
            let (tx, rx) = mpsc::channel(self.protocol_queue);
            (
                Seat {
                    id,
                    name: name.clone(),
                    kind,
                    keyboard,
                    sink: SeatSink::Protocol(tx),
                    progress: None,
                },
                SeatReceiver::Protocol(rx),
            )
        } else {
            let (tx, rx) = mpsc::channel(self.terminal_queue);
            let progress = Arc::new(PaneProgress::default());
            (
                Seat {
                    id,
                    name: name.clone(),
                    kind,
                    keyboard,
                    sink: SeatSink::Terminal(tx),
                    progress: Some(Arc::clone(&progress)),
                },
                SeatReceiver::Terminal {
                    rx,
                    screen: replay.screen(),
                    conversation: replay.conversation(),
                    progress,
                },
            )
        };
        self.seats.write().await.insert(id, seat);
        drop(replay);
        debug!(seat = id, name = %name, kind = kind.label(), "seat registered");
        (id, rx)
    }

    /// Remove a seat's outbound sink. Returns its kind if it existed.
    pub async fn unregister(&self, id: SeatId) -> Option<SeatKind> {
        let removed = self.seats.write().await.remove(&id);
        if let Some(seat) = removed {
            debug!(seat = id, name = %seat.name, "seat unregistered");
            return Some(seat.kind);
        }
        None
    }

    /// Can this seat type? (issue #42)
    ///
    /// The core asks before passing a keystroke on as "the player is composing a line". A key in
    /// a read-only pane is not that — those panes print one hint and drop it — and a companion
    /// held open by somebody leaning on the game pane would wait for ever.
    pub async fn is_keyboard(&self, id: SeatId) -> bool {
        self.seats
            .read()
            .await
            .get(&id)
            .map(|seat| seat.keyboard)
            .unwrap_or(false)
    }

    /// Current seat list, for `/status` and `who`.
    pub async fn list(&self) -> Vec<(SeatId, String, SeatKind, bool)> {
        let mut out: Vec<(SeatId, String, SeatKind, bool)> = self
            .seats
            .read()
            .await
            .values()
            .map(|s| (s.id, s.name.clone(), s.kind, s.keyboard))
            .collect();
        out.sort_by_key(|(id, _, _, _)| *id);
        out
    }

    /// Number of seats currently registered.
    pub async fn len(&self) -> usize {
        self.seats.read().await.len()
    }

    /// `game_output` chunks dropped so far for slow protocol seats.
    pub fn dropped_chunks(&self) -> u64 {
        self.dropped_chunks.load(Ordering::Relaxed)
    }

    /// Push a message to every terminal seat.
    ///
    /// Never blocks the game: a terminal seat that cannot keep up loses the
    /// message for that seat only (logged). Terminal seats are SSH clients, so a
    /// saturated queue means a stalled connection, not a stalled game.
    pub async fn send_terminal(&self, msg: &ClientMessage) {
        // Recorded before it is delivered: the records are what a pane that connects late is
        // given, and a pane registering right now takes its snapshots under this same lock (§11).
        self.replay().await.push(msg);

        let seats = self.seats.read().await;
        for seat in seats.values() {
            offer(seat, msg);
        }
    }

    /// Is every pane showing everything it has been given? (03_session.md §5 (`S35`–`S40`))
    ///
    /// The screen's own question, asked by the router before it lets a line into the story: a pane
    /// that is still writing is a screen that is still scrolling, and a move that lands before its
    /// sentence is a move the player never had the chance to stop.
    pub async fn panes_are_quiet(&self) -> bool {
        self.pane_backlog().await.0 == 0
    }

    /// How many panes are still writing, and how many are watching — `(busy, watching)`.
    ///
    /// For the one log line that explains a wait. A gate that says "the screen has not settled" and
    /// nothing else is a gate nobody can debug: a pane that is writing and a pane that will never
    /// write are the difference between a quiet second and an evening spent on the patience.
    pub async fn pane_backlog(&self) -> (usize, usize) {
        let seats = self.seats.read().await;
        let mut watching = 0;
        let mut busy = 0;
        for seat in seats.values() {
            if let Some(progress) = &seat.progress {
                watching += 1;
                if !progress.is_quiet() {
                    busy += 1;
                }
            }
        }
        (busy, watching)
    }

    /// Push a message to one terminal seat.
    ///
    /// Used to give a pane that has just connected the state it missed: a typing pane needs
    /// to know the game is already waiting for input, and would otherwise sit there looking
    /// dead until the next turn.
    pub async fn send_terminal_to(&self, id: SeatId, msg: &ClientMessage) {
        let seats = self.seats.read().await;
        if let Some(seat) = seats.get(&id) {
            offer(seat, msg);
        }
    }

    /// Push a message to every terminal seat except one.
    ///
    /// Used for the player's own words: the pane they typed in already echoed them.
    ///
    /// Recorded as well as delivered, and this is the one path where that is easy to forget: the
    /// player's words are exactly half of the conversation, and they reach the other panes through
    /// this door rather than through `send_terminal`. A record that missed them would replay every
    /// reply to something the pane could not see was said — the same gap the live stream was given
    /// this message to close (03_session.md §4 (`S25`–`S29`)).
    pub async fn send_terminal_except(&self, except: SeatId, msg: &ClientMessage) {
        self.replay().await.push(msg);

        let seats = self.seats.read().await;
        for seat in seats.values() {
            if seat.id == except {
                continue;
            }
            offer(seat, msg);
        }
    }

    /// Push a message to every protocol seat, applying the lag policy (§6.5).
    pub async fn send_protocol(&self, msg: &Outbound) {
        let seats = self.seats.read().await;
        for seat in seats.values() {
            if let SeatSink::Protocol(tx) = &seat.sink {
                dispatch_protocol(seat.id, &seat.name, tx, msg, &self.dropped_chunks);
            }
        }
    }

    /// Push a message to one protocol seat, applying the lag policy (§6.5).
    pub async fn send_protocol_to(&self, id: SeatId, msg: &Outbound) {
        let seats = self.seats.read().await;
        if let Some(seat) = seats.get(&id) {
            if let SeatSink::Protocol(tx) = &seat.sink {
                dispatch_protocol(seat.id, &seat.name, tx, msg, &self.dropped_chunks);
            }
        }
    }
}

/// Hand one pane a message, counting it as owed until that pane has written it (§8.2).
///
/// The count goes up **before** the message is offered, and comes back down only if the offer
/// fails. The other order looks safer and is not: the writer can only see the message once
/// `try_send` has accepted it, so a count raised afterwards is a count that can be one message
/// behind — and a pane that is quietly writing a sentence would read as quiet.
fn offer(seat: &Seat, msg: &ClientMessage) {
    let SeatSink::Terminal(tx) = &seat.sink else {
        return;
    };
    let Some(progress) = &seat.progress else {
        let _ = tx.try_send(msg.clone());
        return;
    };

    progress.owed();
    if let Err(err) = tx.try_send(msg.clone()) {
        progress.paid();
        match err {
            mpsc::error::TrySendError::Full(_) => warn!(
                seat = seat.id,
                name = %seat.name,
                "terminal seat queue full; dropping message for that seat"
            ),
            mpsc::error::TrySendError::Closed(_) => {
                debug!(seat = seat.id, "terminal seat closed")
            }
        }
    }
}

/// Apply the per-seat backpressure policy for one protocol message.
///
/// * `game_output` is droppable: the agent can rebuild the picture from the next
///   `turn_complete`, and a `system {event:"lag"}` notice tells it that it happened.
/// * everything else (`turn_complete`, `chat`, `system`, `welcome`) is **never
///   dropped**: if the queue is saturated we hand delivery to a background task
///   rather than blocking the core. The message therefore always arrives; under
///   saturation a late `turn_complete` may land after later `game_output` chunks,
///   which is harmless because a turn carries its own full output.
fn dispatch_protocol(
    id: SeatId,
    name: &str,
    tx: &mpsc::Sender<Outbound>,
    msg: &Outbound,
    dropped: &AtomicU64,
) {
    match tx.try_send(msg.clone()) {
        Ok(()) => {}
        Err(mpsc::error::TrySendError::Closed(_)) => {
            debug!(seat = id, name = %name, "protocol seat closed");
        }
        Err(mpsc::error::TrySendError::Full(pending)) => match pending {
            Outbound::GameOutput { .. } => {
                let total = dropped.fetch_add(1, Ordering::Relaxed) + 1;
                warn!(
                    seat = id,
                    name = %name,
                    dropped_total = total,
                    "slow protocol seat: dropped a game_output chunk"
                );
                // Best effort: tell the seat it fell behind (§6.5).
                let _ = tx.try_send(Outbound::system(
                    "lag",
                    "dropped buffered game_output; resynchronise on the next turn_complete",
                ));
            }
            critical => {
                warn!(
                    seat = id,
                    name = %name,
                    "protocol seat queue saturated; deferring a non-droppable message"
                );
                let tx = tx.clone();
                tokio::spawn(async move {
                    let _ = tx.send(critical).await;
                });
            }
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    fn chunk(n: usize) -> Outbound {
        Outbound::GameOutput {
            data: "x".repeat(n),
        }
    }

    async fn drain(rx: &mut mpsc::Receiver<Outbound>) -> Vec<Outbound> {
        let mut out = Vec::new();
        while let Ok(msg) = rx.try_recv() {
            out.push(msg);
        }
        out
    }

    #[tokio::test]
    async fn registers_both_families_with_the_right_receiver() {
        let reg = SeatRegistry::new();
        let (ssh_id, ssh_rx) = reg.register("ssh-1", SeatKind::Ssh, true).await;
        let (agent_id, agent_rx) = reg.register("agent", SeatKind::Agent, false).await;
        assert_ne!(ssh_id, agent_id);
        assert!(matches!(ssh_rx, SeatReceiver::Terminal { .. }));
        assert!(matches!(agent_rx, SeatReceiver::Protocol(_)));
        assert_eq!(reg.len().await, 2);
        assert_eq!(
            reg.list()
                .await
                .iter()
                .filter(|(_, _, k, _)| *k == SeatKind::Ssh)
                .count(),
            1
        );

        assert_eq!(reg.unregister(agent_id).await, Some(SeatKind::Agent));
        assert_eq!(reg.list().await.len(), 1);
        assert_eq!(reg.unregister(12345).await, None);
    }

    #[tokio::test]
    async fn terminal_and_protocol_traffic_stay_separate() {
        let reg = SeatRegistry::new();
        let (_, ssh_rx) = reg.register("ssh-1", SeatKind::Ssh, true).await;
        let (_, agent_rx) = reg.register("agent", SeatKind::Agent, false).await;
        let SeatReceiver::Terminal { mut rx, .. } = ssh_rx else {
            panic!("expected terminal receiver")
        };
        let SeatReceiver::Protocol(mut agent_rx) = agent_rx else {
            panic!("expected protocol receiver")
        };

        reg.send_terminal(&ClientMessage::Companion("hi".into()))
            .await;
        reg.send_protocol(&chunk(4)).await;

        assert!(matches!(rx.try_recv(), Ok(ClientMessage::Companion(_))));
        assert!(
            rx.try_recv().is_err(),
            "terminal seat sees no protocol traffic"
        );
        assert!(matches!(
            agent_rx.try_recv(),
            Ok(Outbound::GameOutput { .. })
        ));
        assert!(
            agent_rx.try_recv().is_err(),
            "agent seat sees no terminal traffic"
        );
    }

    /// The lines of a conversation snapshot, for comparing them to what was said.
    fn lines(messages: &[ClientMessage]) -> Vec<String> {
        messages
            .iter()
            .map(|message| match message {
                ClientMessage::Companion(text)
                | ClientMessage::System(text)
                | ClientMessage::Player(text) => text.clone(),
                ClientMessage::Input { line, .. } => line.clone(),
                other => panic!("not a line of conversation: {other:?}"),
            })
            .collect()
    }

    #[tokio::test]
    async fn a_pane_that_connects_late_is_given_both_records() {
        let reg = SeatRegistry::new();
        reg.send_terminal(&ClientMessage::GameOutput(b"West of House\n>".to_vec()))
            .await;
        reg.send_terminal(&ClientMessage::Companion("It is a mailbox.".into()))
            .await;
        // The player's own words reach the other panes by the *other* door, and are conversation
        // just as much as the companion's are (03_session.md §4 (`S25`–`S29`)).
        let (me, _) = reg.register("me-1", SeatKind::Ssh, true).await;
        reg.send_terminal_except(me, &ClientMessage::Player("what about the desk?".into()))
            .await;

        let (_, rx) = reg.register("quill-1", SeatKind::Ssh, false).await;
        let SeatReceiver::Terminal {
            screen,
            conversation,
            ..
        } = rx
        else {
            panic!("expected terminal receiver")
        };

        assert_eq!(screen.len(), 1, "the screen is the game's text and no more");
        assert!(matches!(screen[0], ClientMessage::GameOutput(_)));
        assert_eq!(
            lines(&conversation),
            vec!["It is a mailbox.", "what about the desk?"],
            "the conversation is what was said, in order, and none of the screen"
        );
    }

    #[tokio::test]
    async fn the_prompt_and_an_answer_to_the_pane_that_asked_are_not_recorded() {
        let reg = SeatRegistry::new();
        let (me, _) = reg.register("me-1", SeatKind::Ssh, true).await;
        // Both go to one pane only: the prompt is the typing pane's own, and a command's answer
        // is the asking pane's own business (`say_to_the_asker`). Neither is history.
        reg.send_terminal_to(me, &ClientMessage::Ready).await;
        reg.send_terminal_to(me, &ClientMessage::Command("tags on".into()))
            .await;

        let (_, rx) = reg.register("game-1", SeatKind::Ssh, false).await;
        let SeatReceiver::Terminal {
            screen,
            conversation,
            ..
        } = rx
        else {
            panic!("expected terminal receiver")
        };
        assert!(screen.is_empty());
        assert!(conversation.is_empty());
    }

    #[tokio::test]
    async fn the_page_putting_its_settings_back_is_not_conversation() {
        let reg = SeatRegistry::new();
        let (me, _) = reg.register("me-1", SeatKind::Ssh, true).await;
        // Exactly what a page sends when it opens (07_webui.md §5 (`W18`–`W25`)): it has no route into the session
        // but typing, so this is the path its own chatter reaches the other panes by — and the
        // record is where it would accumulate, once per page opened, for ever.
        for line in ["/tagsoff", "/thinkoff", "/medium"] {
            reg.send_terminal_except(me, &ClientMessage::Player(line.into()))
                .await;
        }

        let (_, rx) = reg.register("quill-1", SeatKind::Ssh, false).await;
        let SeatReceiver::Terminal { conversation, .. } = rx else {
            panic!("expected terminal receiver")
        };
        assert!(
            conversation.is_empty(),
            "the harness's own settings are not replayed"
        );
    }

    #[tokio::test]
    async fn the_lamp_is_in_neither_record() {
        // C2. The lamp is a light, not history: a pane that reconnects must not be replayed six
        // hundred lines of conversation and one lamp lit ten minutes ago, and the screen has no
        // business with it either.
        let reg = SeatRegistry::new();
        reg.send_terminal(&ClientMessage::Lamp { on: true }).await;
        reg.send_terminal(&ClientMessage::Companion("hmm".into()))
            .await;
        reg.send_terminal(&ClientMessage::Lamp { on: false }).await;

        let (_, rx) = reg.register("quill-1", SeatKind::Ssh, false).await;
        let SeatReceiver::Terminal {
            screen,
            conversation,
            ..
        } = rx
        else {
            panic!("expected terminal receiver")
        };
        assert!(screen.is_empty());
        assert_eq!(lines(&conversation), vec!["hmm"]);
    }

    #[tokio::test]
    async fn a_pane_that_is_still_writing_is_not_quiet() {
        // 03_session.md §5 (`S38`), and the UAT's second finding. The pace's promise is that a
        // command cannot reach the story before the line announcing it has finished appearing, and
        // "finished appearing" is this: the pane has written everything it was given. It is asked
        // here because nothing asked it before, which is exactly how the daemon came to release a
        // command while the sentence announcing it was still a third of the way out of the pane.
        let reg = SeatRegistry::new();
        let (_, rx) = reg.register("player-1", SeatKind::Ssh, true).await;
        let SeatReceiver::Terminal {
            mut rx, progress, ..
        } = rx
        else {
            panic!("expected a terminal receiver")
        };

        assert!(reg.panes_are_quiet().await, "nothing has been sent yet");

        // The forwarder taking the pane, as it does the moment a pane has a socket: from here its
        // writing is what the screen is doing.
        let opening = Writing::begin(&progress);
        drop(opening);

        reg.send_terminal(&ClientMessage::System("hello".into()))
            .await;
        assert!(
            !reg.panes_are_quiet().await,
            "the pane has been given a message and has not written it"
        );

        // What the forwarder does with every message it takes: hold the guard for the whole of the
        // write, and let it go when the bytes have gone. The registry counted this message when it
        // offered it, so the guard's drop is the other half of that one count.
        let _ = rx
            .recv()
            .await
            .expect("the message was offered to the pane");
        {
            let settling = Writing::settle(&progress);
            assert!(!progress.is_quiet(), "still writing");
            drop(settling);
        }
        assert!(
            reg.panes_are_quiet().await,
            "written: the screen has settled"
        );
    }

    #[tokio::test]
    async fn a_writer_that_gives_up_part_way_still_settles_the_pane() {
        // The writer has early returns — a dead socket, a closed channel — and it can panic. A
        // count kept by a pair of calls would be left standing by either, and a pane that never
        // goes quiet holds every line of the session until the patience runs out.
        let progress = Arc::new(PaneProgress::default());

        fn giving_up(progress: &Arc<PaneProgress>) {
            let _settling = Writing::settle(progress);
            if !progress.is_quiet() {
                // The writer gave up part way out — a dead socket, a closed channel — and the guard
                // went with it.
                return;
            }
            unreachable!("the pane was owing a message, so this is the early exit");
        }
        progress.owed(); // the registry counted the message when it offered it
        giving_up(&progress);

        assert!(
            progress.is_quiet(),
            "the guard was dropped on the way out, so the pane is not still owing"
        );
    }

    #[tokio::test]
    async fn a_seat_with_no_writer_is_not_a_screen_that_is_scrolling() {
        // A session channel opens before anybody asks for a shell or a terminal, and a client that
        // asks for neither — a health check, a scanner, `ssh host true` — must not be a pane with a
        // backlog nothing will ever write, or every line of the session waits out the patience for
        // it.
        let reg = SeatRegistry::new();
        let (_, _rx) = reg.register("scanner", SeatKind::Ssh, false).await;
        reg.send_terminal(&ClientMessage::System("nobody is reading".into()))
            .await;
        assert!(reg.panes_are_quiet().await);
    }

    #[tokio::test]
    async fn the_agent_is_never_a_screen_the_story_waits_for() {
        // Only terminal seats carry the count. The companion reads the game the instant frotz
        // writes it, and that is not a reason for the game to wait for anything.
        let reg = SeatRegistry::new();
        let _ = reg.register("agent", SeatKind::Agent, false).await;
        reg.send_protocol(&chunk(8)).await;
        assert!(reg.panes_are_quiet().await);
    }

    #[tokio::test]
    async fn slow_protocol_seat_drops_game_output_but_keeps_turns() {
        let reg = SeatRegistry::new();
        let (id, rx) = reg.register("slow-agent", SeatKind::Agent, false).await;
        let SeatReceiver::Protocol(mut rx) = rx else {
            panic!("expected protocol receiver")
        };

        // Fill the queue past capacity with droppable chunks.
        for _ in 0..(PROTOCOL_QUEUE + 10) {
            reg.send_protocol(&chunk(8)).await;
        }
        assert!(
            reg.dropped_chunks() > 0,
            "chunks were dropped, not blocked on"
        );

        // A turn must still get through even though the queue is saturated. When
        // the queue is full, delivery of a non-droppable message is handed to a
        // background task, so give that task a moment before draining.
        let turn = Turn {
            input: "look".into(),
            output: "You are…".into(),
            elapsed_ms: 12,
            turn: 1,
            awaiting_command: true,
            machinery: false,
            parser: None,
            screen: None,
            location: None,
            score: None,
            moves: None,
            hours: None,
            minutes: None,
        };
        reg.send_protocol(&Outbound::TurnComplete(turn)).await;

        let mut queued = Vec::new();
        for _ in 0..200 {
            queued.extend(drain(&mut rx).await);
            if queued
                .iter()
                .any(|m| matches!(m, Outbound::TurnComplete(_)))
            {
                break;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let _ = id;
        assert!(
            queued
                .iter()
                .any(|m| matches!(m, Outbound::TurnComplete(t) if t.input == "look")),
            "turn_complete survived saturation"
        );
    }

    #[tokio::test]
    async fn unregistered_seat_receives_nothing() {
        let reg = SeatRegistry::new();
        let (id, rx) = reg.register("ssh-1", SeatKind::Ssh, true).await;
        let SeatReceiver::Terminal { mut rx, .. } = rx else {
            panic!("expected terminal receiver")
        };
        reg.unregister(id).await;
        reg.send_terminal(&ClientMessage::System("bye".into()))
            .await;
        assert!(rx.try_recv().is_err());
    }
}
