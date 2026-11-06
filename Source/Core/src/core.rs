//! The core: the seat bus, the router, and the arbitration surface.
//!
//! The core owns no game state of its own — that all lives in the game actor
//! (`game.rs`). What the core owns is **traffic**:
//!
//! * registering seats and fanning the game's output out to them, with a different
//!   backpressure policy per seat family (`seat.rs`);
//! * routing input: a `/command` line is a core command or a forced game input, a
//!   plain line is either conversation for the companion (co-play) or a command
//!   for the game (agent-less);
//! * the transcript tail and session log;
//! * keeping companion chatter out of the middle of a turn (§12);
//! * `/games`, `/switch`, `/reset`, `/save`, `/status`, `/help`, `/bar` (§5.3, §5.4, §8).
//! * `/plan`, `/notes`, `/think`: read-only peepholes into the companion's head, served
//!   straight off the state volume so they cost no model call (§iteration 01 §5).
//!
//! The one rule that matters most here: **the player always wins** (§10.4). The
//! core enforces only line atomicity (in the game actor); everything else is
//! agent-side arbitration, which is why a proposal is rendered to the player and
//! then simply left alone — the answer to it comes back as a player line.

use crate::config::Tuning;
use crate::game::{self, GameConfig, GameEvent, GameRequest, GameStatus, InputSource, SaveReason};
use crate::games::{self, GameInfo};
use crate::meta_notes;
use crate::observations;
use crate::pace::{self, Pace, Rates};
use crate::playback::Playback;
use crate::runs;
use crate::save::{self, StatePaths};
use crate::seat::{ClientMessage, Outbound, SeatId, SeatKind, SeatReceiver, SeatRegistry};
use crate::seat_protocol::Turn;
use crate::session_log::{self as log, SessionLog};
use anyhow::{anyhow, Context, Result};
use serde_json::json;
use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot, RwLock};
use tracing::{debug, error, info, warn};

/// A line the game has not been given yet, because the screen was still moving (§8.2).
struct WaitingLine {
    line: String,
    source: InputSource,
    /// The story it was meant for, so a line cannot be delivered into the world that replaced it.
    stem: String,
    /// When the wait began. Patience is measured from here.
    held: Instant,
}

/// Commands into the core's router. Every transport speaks this.
pub enum CoreCommand {
    /// A seat connected.
    SeatOpened {
        id: SeatId,
        name: String,
        kind: SeatKind,
    },
    /// A seat went away.
    SeatClosed { id: SeatId, kind: SeatKind },
    /// A human at a terminal typed a line.
    PlayerLine { seat_id: SeatId, line: String },
    /// A human at a terminal pressed a key, and nothing has been sent (issue #42).
    ///
    /// Throttled by the transport, not here: this arrives from the keystroke path, and the
    /// router has no business seeing one message per character.
    PlayerTyping { seat_id: SeatId },
    /// The line being composed in a pane's box begins, or no longer begins, with a space (03_session.md §6 (`S41`–`S43`)).
    ///
    /// Sent by the transport when the answer *changes*, which is at most twice per line — nothing
    /// here is proportional to typing speed, and the text of the line is not sent at all.
    PlayerComposing {
        seat_id: SeatId,
        starts_with_space: bool,
    },
    /// A protocol seat asked frotz to run one line.
    GameInput {
        seat_id: SeatId,
        line: String,
        source: InputSource,
        /// A keystroke rather than a command line — see [`GameInputPayload::key`].
        key: bool,
    },
    /// The agent spoke.
    AgentUtterance { kind: UtteranceKind, text: String },
    /// The companion's lamp: a turn began, or its reply has been delivered (03_session.md §7 (`S44`–`S45`)).
    ///
    /// Not an utterance: there are no words in it and it is not addressed to anyone. It is a fact
    /// about the companion that the panes are told as an escape, and it is a separate command from
    /// [`Self::AgentUtterance`] precisely so that it cannot be queued behind a turn, which is the
    /// one moment it is about.
    AgentLamp { on: bool },
    /// The agent's handshake completed.
    AgentOnline { name: String },
    /// The agent went away.
    AgentOffline,
    /// Stop the daemon: checkpoint, close the agent, flush the log.
    Shutdown { reply: oneshot::Sender<()> },
}

/// The ways the companion can address the player (§6.4).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UtteranceKind {
    /// Thinking out loud: its reasoning, in front of the player rather than behind them.
    /// Shown in the conversation view and never on the screen (04_companion.md §4 (`C26`–`C28`)).
    Thought,
    /// A chunk of that reasoning, sent as it arrives rather than at the end.
    ///
    /// A thought is the middle of a sentence for as long as it takes to write it, and measured
    /// tonight one call in six left the screen silent for ten seconds or more — 163 of 248 calls
    /// were quiet for two seconds or more, and the longest were twenty. This is that silence,
    /// made visible: the words appear as they are produced, and the finished [`Self::Thought`]
    /// that follows adds nothing to the pane (it is the log's copy, and it closes the paragraph).
    ThoughtDelta,
    /// Commentary.
    Chat,
    /// A question awaiting an answer.
    Ask,
    /// An intended move awaiting grant or veto.
    Proposal,
    /// A model call has been in flight long enough to be worth one quiet line, and the words
    /// are the harness's rather than the companion's — the same class of line as the proposal
    /// rendering, and the same reason: measured on 2026-09-16, p95 was 15.9s, the longest call
    /// 45.7s, and once the player asked "james...you there?" mid-call. Only sent when somebody
    /// who can type is connected (04_companion.md §5 (`C29`–`C41`)).
    Working,
    /// One tool or extension call, for the panel while the tool trace is on (03_session.md §4.3 (`S32`–`S34`)). The
    /// words are the harness's rendering of what the companion reached for rather than the
    /// companion's own, and whether anybody sees them is the core's decision.
    Tool,
}

/// Configuration for the core.
#[derive(Debug, Clone)]
pub struct CoreSettings {
    /// Directory scanned for story files.
    pub games_dir: PathBuf,
    /// Where saves, notebooks and logs live.
    pub state: StatePaths,
    /// Game requested by config, if any.
    pub requested_game: Option<String>,
    /// The interpreter binary.
    pub dfrotz: PathBuf,
    /// Turn-framing quiescence window.
    pub turn_timeout: Duration,
    /// Auto-save cadence in turns; 0 disables.
    pub autosave_turns: u64,
    /// How many recent turns to replay to a new agent seat.
    pub transcript_tail: usize,
    /// How many session log files to keep.
    pub log_keep: usize,
    /// What the companion is called (§iteration 01, §6).
    pub agent_name: String,
    /// Does the interpreter have a tool channel (02_game_boundary.md §2 (`G5`–`G7`))?
    ///
    /// Decided once, in `main`, by asking the interpreter itself — so every part of the core
    /// below either has facts to work with or is running the path that was there before.
    pub tools: bool,
    /// The level words arrive at to begin with (03_session.md §5 (`S35`–`S40`)).
    ///
    /// This is where an SSH-only evening sets its pace: the browser is a preference store, and a
    /// session with no page has no stored preferences, so its pace is whatever the instance was
    /// started with.
    pub pace: Pace,
    /// The ladder's rates, and the ceiling that bounds it.
    pub rates: Rates,
    /// The cadences, queue depths and timers (10_delivery.md §2 (`D5`, `D9`)).
    ///
    /// Resolved once in `main` from `core-config.json`, the environment and the command line, in
    /// that order of precedence reversed — see `config.rs`.
    pub tuning: Tuning,
}

/// State the core shares with transports (banner, status, `/status`).
pub struct CoreShared {
    game: RwLock<GameStatus>,
    agent_online: AtomicBool,
    agent_name: RwLock<Option<String>>,
    turn_count: AtomicU64,
    started: Instant,
    /// A game turn is in flight: companion chatter waits for it to end (§12).
    turn_open: AtomicBool,
    /// Is the companion's reasoning shown in the panes as it arrives? (issue #35)
    think_visible: AtomicBool,
    /// Are the lines a pane shows marked with their square-bracketed tag — `[system]`, `[me]`,
    /// `[Quill]`, `[think]`, `[pane]`? (03_session.md §2.1 (`S11`)) On until the player says otherwise: with
    /// three panes side by side the tags are what makes a mixed window legible, and reading one
    /// pane on its own they are noise in front of the text.
    tags: AtomicBool,
    /// The pace the words on the screen arrive at (03_session.md §5 (`S35`–`S40`)). Read per message by every pane,
    /// the way the tags are, because `/slow` can be said while three of them are open.
    ///
    /// A number rather than a lock: the reader is a pane's forwarder, which writes to a socket in an
    /// async task and may not wait for anything.
    pace: AtomicU8,
    /// How many times the player has asked for the rest of what is being written, now
    /// (03_session.md §5 (`S38.1`)). Read before and after every word by every pane's forwarder, the
    /// way the pace is — and **session-wide rather than per pane**, because the panes are separate
    /// connections: a pane cannot reach another's socket to stop it metering, and one that kept the
    /// ask to itself would stop only itself. Which is exactly what a first attempt at this did.
    skips: AtomicU64,
    /// The ladder's rates. Fixed for the life of the process: they are configuration, not a
    /// preference.
    rates: Rates,
    /// Are the companion's tool and extension calls shown in its pane? (03_session.md §4.3 (`S32`–`S34`)) Off by
    /// default, and off is free: the switch is what tells the agent to send them at all.
    debug: AtomicBool,
    /// The attempt in progress (02_game_boundary.md §6 (`G27`–`G34`)). Held here because every turn the core records is
    /// stamped with it, and because `welcome` and `/status` both report it.
    run: RwLock<Option<runs::Attempt>>,
    /// The player's standing danger level (§4.7): anything at or under it the companion may
    /// do without asking. Held here because it outlives any one command — the agent is told
    /// on every change, and told again when it rejoins with a fresh config default.
    risk_level: RwLock<String>,
    /// Recent turns, kept so a newly connected agent can be sent the tail (§6.3).
    transcript: RwLock<VecDeque<String>>,
}

/// The core handle. Cheap to clone via `Arc`; every transport holds one.
pub struct Core {
    cmd_tx: mpsc::Sender<CoreCommand>,
    seats: Arc<SeatRegistry>,
    shared: Arc<CoreShared>,
    games: Arc<RwLock<Vec<GameInfo>>>,
    log: Arc<SessionLog>,
    settings: Arc<CoreSettings>,
    /// The game's own screen, written down as it happens (03_session.md §4.2 (`S31`)).
    playback: Playback,
}

impl Core {
    /// Bring the daemon up: state layout, game discovery, frotz, router.
    pub async fn new(settings: CoreSettings) -> Result<Arc<Self>> {
        settings
            .state
            .ensure()
            .context("failed to prepare the state directory")?;

        let games = games::discover(&settings.games_dir);
        let initial = choose_initial_game(
            &games,
            settings.requested_game.as_deref(),
            &settings.state,
            &settings.games_dir,
        )?;
        info!(
            game = %initial.stem,
            version = ?initial.version,
            discovered = games.len(),
            "starting with `{}`",
            initial.stem
        );

        let log = Arc::new(
            SessionLog::start(
                &settings.state.logs_dir(),
                settings.log_keep,
                settings.tuning.log_queue,
            )
            .context("failed to start the session log")?,
        );
        info!(
            events = %log.jsonl_path().display(),
            summary = %log.summary_path().display(),
            "session logging to the state directory"
        );

        let mut game_config = GameConfig::new(
            settings.state.clone(),
            settings.dfrotz.clone(),
            settings.turn_timeout,
            settings.autosave_turns,
            settings.tuning.game.clone(),
        );
        game_config.tools = settings.tools;

        let actor = game::spawn(game_config, initial.clone()).await?;

        let (cmd_tx, cmd_rx) = mpsc::channel(settings.tuning.command_queue);
        let (terminal_tx, mut terminal_rx) = mpsc::channel::<Fanout>(settings.tuning.fanout_queue);

        // Which attempt this is (02_game_boundary.md §6 (`G27`–`G34`)). A boot with an attempt left open on disk continues
        // it, because the world came back from the checkpoint and the companion is the same
        // person; a redeploy is not an event in the story. Only an attempt that was explicitly
        // ended — a restart, a switch away, a reset — starts a new number.
        let attempt = runs::continue_or_start(settings.state.root(), &initial.stem)
            .context("failed to open this game's run book")?;
        info!(
            run = attempt.number,
            generation = attempt.generation,
            stem = %initial.stem,
            "attempt {} of `{}`",
            attempt.number,
            initial.stem
        );

        // The evening, written down as it happens (03_session.md §4.2 (`S31`)): the game's own screen goes to
        // `<state>/playback/<stem>.log`, and a banner opens each attempt so a file that spans a
        // restart is still readable.
        let playback = Playback::open(&settings.state, settings.tuning.playback_queue);
        playback.note_attempt(
            &initial.stem,
            attempt.number,
            attempt.generation,
            attempt.started.clone(),
        );

        let shared = Arc::new(CoreShared {
            game: RwLock::new(actor.status.clone()),
            agent_online: AtomicBool::new(false),
            agent_name: RwLock::new(None),
            turn_count: AtomicU64::new(0),
            started: Instant::now(),
            turn_open: AtomicBool::new(false),
            // Off by default (UAT, 2026-09-18): thinking out loud is worth having and it is
            // also a wall of text beside a game somebody is trying to read, so the player who
            // wants it says so once with `/thinkon`. Nothing is lost by leaving it off — every
            // thought reaches the log either way, and `/think` reads the last one back
            // (issue #35).
            think_visible: AtomicBool::new(false),
            // Tags on until asked otherwise: they are what makes a mixed window legible.
            tags: AtomicBool::new(true),
            pace: AtomicU8::new(settings.pace.index()),
            skips: AtomicU64::new(0),
            rates: settings.rates.clone(),
            // The tool trace is off until asked for: it is diagnosis, not part of playing.
            debug: AtomicBool::new(false),
            // The attempt is worked out just above, once the initial game is known.
            run: RwLock::new(Some(attempt)),
            // Medium is the gate as it always was: the acts nobody can take back stop for
            // the player, and everything else does not (§4.7).
            risk_level: RwLock::new("medium".to_string()),
            transcript: RwLock::new(VecDeque::new()),
        });

        let core = Arc::new(Self {
            cmd_tx,
            seats: Arc::new(SeatRegistry::configured(
                settings.tuning.clone(),
                playback.clone(),
            )),
            shared: shared.clone(),
            games: Arc::new(RwLock::new(games)),
            log: Arc::clone(&log),
            settings: Arc::new(settings),
            playback,
        });

        log.emit(
            log::KIND_SESSION_START,
            json!({
                "game": initial.stem,
                "version": initial.version,
                "restored": actor.status.restored,
            }),
        );

        // One fan-out task owns writes to player terminals: notices keep their
        // order, and a slow SSH connection can never stall the router.
        let on_the_way = Arc::new(AtomicUsize::new(0));
        {
            let seats = Arc::clone(&core.seats);
            let on_the_way = Arc::clone(&on_the_way);
            tokio::spawn(async move {
                while let Some(fanout) = terminal_rx.recv().await {
                    match fanout {
                        Fanout::All(message) => seats.send_terminal(&message).await,
                        Fanout::Except(seat, message) => {
                            seats.send_terminal_except(seat, &message).await
                        }
                        Fanout::To(seat, message) => seats.send_terminal_to(seat, &message).await,
                    }
                    // Counted after the handover, never before: the seat registry raises the
                    // panes' own counts inside those calls, so a message is still "on the way"
                    // for the whole of the window in which it has been sent but not yet owed
                    // (03_session.md §5 (`S35`–`S40`)).
                    on_the_way.fetch_sub(1, Ordering::SeqCst);
                }
            });
        }

        let router = Router {
            core: Arc::clone(&core),
            shared,
            seats: Arc::clone(&core.seats),
            games: Arc::clone(&core.games),
            log: Arc::clone(&core.log),
            settings: Arc::clone(&core.settings),
            playback: core.playback.clone(),
            game_tx: actor.requests,
            terminal_tx,
            pending_companion: VecDeque::new(),
            replying_to: None,
            player_present: None,
            announced_switch: None,
            last_status: None,
            waiting: VecDeque::new(),
            on_the_way,
        };
        tokio::spawn(router.run(cmd_rx, actor.events));

        Ok(core)
    }

    /// Register a seat and hand the transport its receiver.
    ///
    /// `keyboard` is whether this seat can type. It is not the same question as `kind`: an SSH
    /// pane can be the game screen (`game`) or the companion's own window (`quill`), and neither
    /// accepts a line. The core needs it because "somebody is here" and "somebody can answer"
    /// are different facts, and on a server left running it is the difference between a companion
    /// waiting quietly and one that thinks an abandoned screen is an audience.
    pub async fn register_seat(
        &self,
        name: impl Into<String>,
        kind: SeatKind,
        keyboard: bool,
    ) -> (SeatId, SeatReceiver) {
        let name = name.into();
        let (id, rx) = self.seats.register(name.clone(), kind, keyboard).await;
        let _ = self
            .cmd_tx
            .send(CoreCommand::SeatOpened { id, name, kind })
            .await;
        (id, rx)
    }

    /// Remove a seat.
    pub async fn unregister_seat(&self, id: SeatId) {
        if let Some(kind) = self.seats.unregister(id).await {
            let _ = self.cmd_tx.send(CoreCommand::SeatClosed { id, kind }).await;
        }
    }

    /// Show one pane something that is its own and nobody else's (03_session.md §5 (`S35`–`S40`)).
    ///
    /// The keystroke echo, and the prompt that follows a line. No other window shows them and
    /// neither record holds them, but they are still written by the one task that writes to that
    /// pane — so they arrive in the same order as the game's text and the companion's words rather
    /// than through it. Two writers on one screen is a sentence with a command in the middle of it,
    /// which is what the 2026-09-20 UAT saw and read as the companion acting ahead of its speech.
    pub async fn show_to_seat(&self, id: SeatId, message: ClientMessage) {
        self.seats.send_terminal_to(id, &message).await;
    }

    /// Send a command to the router.
    pub async fn send(&self, command: CoreCommand) {
        if self.cmd_tx.send(command).await.is_err() {
            debug!("core router is gone; command dropped");
        }
    }

    /// Send one protocol message to one seat. Used for the handshake reply, which
    /// must reach the specific seat that said `hello`.
    pub async fn send_to_seat(&self, seat_id: SeatId, message: Outbound) {
        self.seats.send_protocol_to(seat_id, &message).await;
    }

    /// Status of the loaded game.
    pub async fn game_status(&self) -> GameStatus {
        self.shared.game.read().await.clone()
    }

    /// Is the companion connected and handshaken?
    pub fn agent_online(&self) -> bool {
        self.shared.agent_online.load(Ordering::Relaxed)
    }

    /// Are the panes marking their lines with a square-bracketed tag? (03_session.md §2.1 (`S11`))
    ///
    /// Read by every pane, and by every message, rather than captured when a pane connects:
    /// `/tagsoff` is a thing the player types while three panes are open, and all three have to
    /// change at the line after it.
    pub fn tags(&self) -> bool {
        self.shared.tags.load(Ordering::Relaxed)
    }

    /// The pace the words arrive at (03_session.md §5 (`S35`–`S40`)).
    pub fn pace(&self) -> Pace {
        Pace::from_index(self.shared.pace.load(Ordering::Relaxed)).unwrap_or(Pace::Medium)
    }

    /// The ladder's rates, and the ceiling.
    pub fn pace_rates(&self) -> &Rates {
        &self.shared.rates
    }

    /// How many times the player has asked for the rest of what is being written, now
    /// (03_session.md §5 (`S38.1`)).
    ///
    /// A **count** rather than a flag, and here rather than in a pane: a flag would be taken by
    /// whichever pane happened to read it first while the others carried on arriving word by word,
    /// and a pane's own state is invisible to every other pane — they are separate connections.
    pub fn skips(&self) -> u64 {
        self.shared.skips.load(Ordering::SeqCst)
    }

    /// The player asked for the rest of it: see [`Core::skips`].
    pub fn skip_ahead(&self) {
        self.shared.skips.fetch_add(1, Ordering::SeqCst);
    }

    /// The session log identifier, for player-facing notices.
    pub fn session_id(&self) -> &str {
        self.log.session_id()
    }

    /// What to call the companion in player-facing text.
    ///
    /// Falls back to the generic noun when it has no name, so a default install still
    /// reads naturally rather than saying "playing with companion".
    pub fn agent_label(&self) -> String {
        let configured = self.settings.agent_name.trim();
        if configured.is_empty() || configured.eq_ignore_ascii_case("companion") {
            "your companion".to_string()
        } else {
            configured.to_string()
        }
    }

    /// The handshake payload for a newly connected agent (§6.3 `welcome`).
    pub async fn welcome(&self) -> Outbound {
        let status = self.game_status().await;
        let title = status.title.clone().unwrap_or_else(|| status.stem.clone());
        let transcript_tail: Vec<String> = self
            .shared
            .transcript
            .read()
            .await
            .iter()
            .cloned()
            .collect();
        // Which attempt, and whether this process came back to a position instead of starting one:
        // the two facts a companion needs before it says anything about where it is (05_memory.md §5 (`M23`–`M26`)).
        // Zero means no attempt was recorded, which is a fact about the bookkeeping rather than a
        // claim about the play.
        let (run, generation) = match self.shared.run.read().await.as_ref() {
            Some(attempt) => (attempt.number, attempt.generation),
            None => (0, 0),
        };
        let resumed = status.restored;
        // What the checkpoint itself says. The turn is the one fact that keeps a restored world
        // honest: the autosave is every few turns, so the position that just came back can be
        // several moves behind the conversation the companion is about to pick up (05_memory.md §5 (`M23`–`M26`)).
        let record = if resumed {
            save::read_save_record(&self.settings.state, &status.stem)
        } else {
            None
        };
        Outbound::Welcome {
            game_title: title,
            game_stem: status.stem.clone(),
            turn_count: self.shared.turn_count.load(Ordering::Relaxed),
            transcript_tail,
            agent_name: self.agent_label(),
            run,
            generation,
            resumed,
            resumed_from: resumed.then(|| format!("{}.sav", status.stem)),
            resumed_at_turn: record.as_ref().map(|record| record.turn),
            resumed_saved_at: record.as_ref().map(|record| record.saved_at.clone()),
        }
    }

    /// The vocabulary of the game in play, read from the story file (§10.4).
    ///
    /// `None` when the words cannot be read — a truncated file, an unusual format —
    /// in which case the agent keeps its seat and falls back to learning from the
    /// parser's own refusals, rather than being given a guess to enforce.
    pub async fn vocabulary(&self) -> Option<Outbound> {
        let status = self.game_status().await;
        let path = {
            let games = self.games.read().await;
            games
                .iter()
                .find(|game| game.stem == status.stem)
                .map(|game| game.path.clone())
        }?;

        match crate::vocabulary::Vocabulary::read(&path) {
            Ok(found) => {
                info!(
                    stem = %status.stem,
                    words = found.len(),
                    resolution = found.resolution(),
                    "read the game's vocabulary from its story file"
                );
                Some(Outbound::Vocabulary {
                    game_stem: status.stem.clone(),
                    version: found.version(),
                    resolution: found.resolution(),
                    words: found.words().map(str::to_string).collect(),
                })
            }
            Err(err) => {
                warn!(
                    stem = %status.stem,
                    error = %err,
                    "could not read the game's vocabulary; the companion will learn from refusals instead"
                );
                None
            }
        }
    }

    /// The banner a player sees when they connect (§12).
    pub async fn banner(&self) -> String {
        let status = self.game_status().await;
        let turns = self.shared.turn_count.load(Ordering::Relaxed);
        let companion = if self.agent_online() {
            // The banner names the companion when it has one: a friend has a name,
            // and "your companion" reads like a feature rather than a person.
            format!("You're playing with {}.", self.agent_label())
        } else {
            "Your companion is offline, so you have the keyboard — play as usual.".to_string()
        };

        let mut text = String::new();
        text.push_str(&format!(
            "INFOCompanion — {} ({})\n",
            status.stem,
            status
                .version
                .map(|v| format!("Z-code v{v}"))
                .unwrap_or_else(|| "unknown format".into())
        ));
        if let Some(title) = &status.title {
            text.push_str(&format!("{title}\n"));
        }
        if status.restored {
            text.push_str("Progress restored from your last checkpoint.\n");
        }
        text.push_str(&format!("{companion}\n"));
        if self.agent_online() {
            // This is the first line a player ever reads, so it has to name a command that
            // exists. It said `/command`, which is not a verb and never has been — `/direct` is
            // the route, and the refusal the player got for following the instructions was a
            // fair introduction to nothing.
            text.push_str(
                "Just talk — or type /direct <command> to take the keyboard yourself for one go.\n",
            );
        } else {
            text.push_str("Type commands directly; /help lists the core commands.\n");
        }
        text.push_str(&format!(
            "Turn {} of {} · {} window(s) · log {}\n",
            turns,
            status.stem,
            self.seats.len().await,
            self.session_id()
        ));
        text
    }

    /// Stop the daemon: checkpoint, close the agent, flush the log.
    pub async fn shutdown(&self) {
        let (tx, rx) = oneshot::channel();
        let _ = self.cmd_tx.send(CoreCommand::Shutdown { reply: tx }).await;
        match tokio::time::timeout(self.settings.tuning.shutdown_grace, rx).await {
            Ok(Ok(())) => info!("core shut down cleanly"),
            Ok(Err(_)) => warn!("core shut down without replying"),
            Err(_) => warn!(
                grace = ?self.settings.tuning.shutdown_grace,
                "core shutdown timed out"
            ),
        }
    }

    /// Record a `[think]`-free core-side event from outside the router.
    pub fn log_event(&self, kind: &str, fields: serde_json::Value) {
        self.log.emit(kind, fields);
    }
}

/// Pick the game to boot into: the last played, else the configured one, else the first
/// discovered (§5.3).
///
/// **The pointer wins** (issue #34). `INFOCOMPANION_GAME` is what to open when there is
/// nothing to resume — a first boot, or a state directory somebody has cleared — and it used
/// to beat the pointer, so a stale line in `.env` quietly discarded the evening's progress on
/// every restart. To force a game there is `/switch` at the keyboard, or deleting
/// `current-game` from the state directory.
fn choose_initial_game(
    games: &[GameInfo],
    requested: Option<&str>,
    state: &StatePaths,
    games_dir: &std::path::Path,
) -> Result<GameInfo> {
    if games.is_empty() {
        return Err(anyhow!(
            "no story files found in `{}` — mount one (.z3/.z4/.z5/.z6/.z7/.z8/.zblorb); there is no session to serve without a game",
            games_dir.display()
        ));
    }

    if let Some(last) = games::read_current(state.root()) {
        match games::resolve(games, &last) {
            Ok(game) => {
                match requested {
                    Some(wanted) if !wanted.trim().eq_ignore_ascii_case(&game.stem) => info!(
                        game = %game.stem,
                        requested = %wanted,
                        "resuming the last played game (INFOCOMPANION_GAME applies only when there is nothing to resume)"
                    ),
                    _ => info!(game = %game.stem, "resuming the last played game"),
                }
                return Ok(game);
            }
            Err(err) => warn!(
                last = %last,
                error = %err,
                "last played game is no longer available; falling back"
            ),
        }
    }

    if let Some(wanted) = requested {
        let game = games::resolve(games, wanted)?;
        info!(game = %game.stem, "opening the configured game");
        return Ok(game);
    }

    info!(game = %games[0].stem, "opening the first game found");
    Ok(games[0].clone())
}

// ---------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------

/// A core-level `/` command.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Verb {
    Help,
    Games,
    Switch,
    /// Start this game over, keeping everything the companion has learned (issue #26).
    Restart,
    /// The nuclear option: forget one game completely (issue #26).
    Reset,
    Save,
    /// Bring the world back from a save (§5.3, §5.4).
    ///
    /// The counterpart §5.3 was missing: a save nobody can return to is a file, not a save. It is
    /// deliberately not a second `/switch` — the story and the attempt stay the same, and what
    /// changes is which branch of the world is standing, which is what a fork records.
    Restore,
    /// Which saves this game has, and what is in them.
    Saves,
    /// The game's own line: the numbers the interpreter keeps on its status line
    /// (02_game_boundary.md §4.3 (`G23`), §7 (`G38`)).
    ///
    /// A verb of its own, deliberately not a paragraph of `/status`. `/status` answers questions
    /// about the harness — what is loaded, who is connected, how long we have been at this — and
    /// this answers a question about the game, which is why a player has two commands rather than
    /// one report. The companion reads the same numbers through its `status` tool
    /// (06_tool_surface.md §2.3 (`T16a`)), which is the Z-machine's own word for the line.
    Bar,
    /// The session report: what is loaded, and how this session is going. No numbers of the game's
    /// own — those are `/bar`.
    Status,
    /// The player's standing danger level (§4.7): anything at or under it the companion may
    /// do without asking, and anything above it it proposes first and waits.
    ///
    /// Four verbs rather than one with an argument, for the same reason `.` exists beside
    /// `/direct`: it is a thing a person says in the middle of playing, and the shortest
    /// spelling of it is the one that gets used. `/risk` alone answers "where is it set?".
    RiskLow,
    RiskMedium,
    RiskHigh,
    Risk,
    /// What the companion is currently trying to do.
    Plan,
    /// The companion's notebook, its own words.
    Notes,
    /// Show the calls the companion makes — tools and extensions — in its own pane (02_game_boundary.md
    /// §8.7). Three verbs rather than one with an argument, for the same reason the danger level
    /// has three: it is a thing someone says mid-play, and the shortest spelling gets used.
    DebugOn,
    DebugOff,
    /// Where the trace is set, and which command moves it.
    Debug,
    /// Its reasoning for the last turn: a deliberate look behind the curtain.
    Think,
    /// Show the companion's reasoning in the panes as it arrives (issue #35).
    ThinkOn,
    /// Hide it again. `/think` still reads the last one out of the log.
    ThinkOff,
    /// Mark every line a pane shows with its square-bracketed tag (03_session.md §2.1 (`S11`)).
    ///
    /// One switch for all of them — `[system]`, `[me]`, the companion's name, `[think]` and
    /// `[pane]` go off together — because the request is "so that a console reads as plain
    /// text", and a per-tag choice would be five commands for one wish.
    TagsOn,
    /// Off: the panes read as plain text, in the same colours and the same layout.
    TagsOff,
    /// One of the five paces the words arrive at (03_session.md §5 (`S35`–`S40`)).
    ///
    /// Five verbs rather than one with an argument, for the reason the danger level has four: it is
    /// a thing a person says in the middle of playing, and the shortest spelling is the one that
    /// gets used.
    Pace(Pace),
    /// Where the pace is set, and what the ladder is.
    Speed,
    /// The turns the core has watched, newest last (issue #22).
    ///
    /// A peephole on the record, like `/think`: it reads a file, so it costs no model call
    /// and still answers when the model is down.
    Turns,
    /// The companion's own notes about the harness and about itself (05_memory.md §7 (`M37`–`M39`)).
    ///
    /// The peephole that is deliberately out of this world: what it writes here is what it could
    /// not put in the notebook (not true of the story) and not in the memories (not true of the
    /// player) — a defect it worked around, a pass that produced nothing, a thing about its own
    /// play worth knowing next time. Nothing here is ever read back into a prompt, which is what
    /// makes it safe to be honest in; the reader is whoever has to work out what went wrong.
    Meta,
    /// Ask the companion to type one command (03_session.md §8.1 (`S53`)).
    ///
    /// `/type <line>` means "Quill, you type this" — the player handing its fingers over,
    /// in the register of *"can you type ……"*. It is a request to the companion, which may
    /// object once and then types it, which makes this the one place an objection can
    /// legitimately arrive *before* the act rather than after it.
    Type,
    /// One command, straight to the game (issues #21 and #24).
    ///
    /// The unconditional route: no model call, no objection, no delay. `.` is the short form.
    Direct,
    /// Press Return, at a prompt that is not asking for a command.
    ///
    /// The player's half of the companion's `press_key`, and it exists because the pane *is*
    /// the keyboard for a story that wants one: a bare Return at a pane is not a line, so
    /// `submit_line` never sees it, and a story that opens with `[Press any key to begin.]`
    /// (Bureaucracy) was answerable by the companion and by nobody sitting at the terminal.
    /// The core already knows the difference — an empty line is a mistake unless it is a
    /// keystroke, which is why this needs a verb rather than an empty line — and the
    /// interpreter reports an empty line to the story as a single carriage return.
    PressReturn,
    /// Press one key — a menu's digit, a `y`, a space — at a prompt that is not asking for a
    /// command.
    ///
    /// Several characters walk a menu by itself: the interpreter hands a line's characters to
    /// the story one `read_char` at a time, which is how `\help`'s own note describes it, so
    /// `/presskey 2` presses `2` and `/presskey ynn` answers three prompts in a row.
    PressKey,
    // There is deliberately no `undo`.
    //
    // There was one, and it wrote the line `/undo` into frotz on the stated grounds that it
    // was the interpreter's command. dfrotz has no `/` command set at all: measured, that line
    // went to the *story*, which answered "That's not a verb I recognise." and left the game
    // exactly where it was. It could have been repaired — the interpreter's undo hotkey `\U`
    // works in every story and every language, and was measured doing it — and it was
    // deliberately not repaired.
    //
    // A person sitting at a 1983 keyboard cannot rewind. The whole frame of this thing is two
    // friends sharing one of those keyboards, and an undo key is the one piece of that fiction
    // which would be a lie. Getting a move wrong costs a turn, and you live with it.
    /// What this session has cost so far, in tokens and in money.
    ///
    /// A peephole like `/think`: read from the agent's own JSONL log, so it costs no model call
    /// and still answers when the model is down — which is exactly when someone might ask.
    Cost,
    /// Have the companion write up a game, for a player coming back to it (03_session.md §8 (`S46`–`S57`)).
    ///
    /// The work is the companion's and happens on its own pass with its own model call: it reads
    /// everything the harness has about the game and writes one page, deliberately *not* in its own
    /// voice, for somebody who has not played for months. The core's whole part in it is telling the
    /// companion to go and saying so to the pane that asked.
    ///
    /// It is a command rather than a page route because typing is the only route anything has into
    /// a session (07_webui.md §5 (`W18`–`W25`)): the browser's *generate* button opens a pane and types this line.
    Catchup,
}

impl Verb {
    pub(crate) fn parse(word: &str) -> Option<Self> {
        match word.to_ascii_lowercase().as_str() {
            "help" | "?" => Some(Verb::Help),
            "games" => Some(Verb::Games),
            "switch" => Some(Verb::Switch),
            "restart" => Some(Verb::Restart),
            "reset" => Some(Verb::Reset),
            "save" => Some(Verb::Save),
            "restore" => Some(Verb::Restore),
            // `/list` is the same verb: it is what somebody asks for when they want to see their
            // saves and cannot remember the word this project chose for them (2026-09-20 UAT).
            "saves" | "list" => Some(Verb::Saves),
            "bar" => Some(Verb::Bar),
            "status" => Some(Verb::Status),
            "risklow" => Some(Verb::RiskLow),
            "riskmedium" => Some(Verb::RiskMedium),
            "riskhigh" => Some(Verb::RiskHigh),
            "risk" => Some(Verb::Risk),
            "plan" => Some(Verb::Plan),
            "notes" | "notebook" => Some(Verb::Notes),
            "debugon" => Some(Verb::DebugOn),
            "debugoff" => Some(Verb::DebugOff),
            "debug" => Some(Verb::Debug),
            "think" => Some(Verb::Think),
            "thinkon" => Some(Verb::ThinkOn),
            "thinkoff" => Some(Verb::ThinkOff),
            "tagson" => Some(Verb::TagsOn),
            "tagsoff" => Some(Verb::TagsOff),
            "speed" => Some(Verb::Speed),
            "veryslow" | "slow" | "medium" | "fast" | "veryfast" => {
                Pace::parse(word).map(Verb::Pace)
            }
            "turns" => Some(Verb::Turns),
            "meta" => Some(Verb::Meta),
            "type" => Some(Verb::Type),
            "direct" => Some(Verb::Direct),
            "pressreturn" => Some(Verb::PressReturn),
            "presskey" => Some(Verb::PressKey),
            "cost" => Some(Verb::Cost),
            "catchup" => Some(Verb::Catchup),
            _ => None,
        }
    }

    /// Is this one of the commands that *are* a setting, rather than something asked or said?
    ///
    /// The page's switches and its pace control have no route to the session but typing (07_webui.md
    /// §5), so they arrive as ordinary player lines and are indistinguishable from a person's
    /// words on the wire. This is the only place that can tell them apart, and the conversation
    /// record is what needs to (03_session.md §4 (`S25`–`S29`)): a page putting its settings back must not write
    /// into the history a pane replays. The set is exactly what `restoreCommands` in the page
    /// sends, and a test holds the two together.
    pub(crate) fn is_a_setting(self) -> bool {
        matches!(
            self,
            Verb::TagsOn
                | Verb::TagsOff
                | Verb::DebugOn
                | Verb::DebugOff
                | Verb::ThinkOn
                | Verb::ThinkOff
                | Verb::Pace(_)
                // The danger level, which the page's segmented control sends (07_webui.md §5.1 (`W22`–`W25`)). A
                // page only sends one once somebody has pressed one, but when it does, it is the
                // same chatter as the rest of the restore and must not enter the conversation.
                | Verb::RiskLow
                | Verb::RiskMedium
                | Verb::RiskHigh
        )
    }
}

/// One message on its way to the terminal panes.
///
/// The router used to broadcast everything and let each pane filter it, which cannot
/// express “the pane that asked gets the answer and the others get a notice” — the thing
/// issue #25 needs — because by the time a message is filtered the asking seat is unknown.
#[derive(Debug, Clone)]
enum Fanout {
    /// Every terminal seat.
    All(ClientMessage),
    /// Every terminal seat except one, which already has this text.
    Except(SeatId, ClientMessage),
    /// Exactly one terminal seat: a command's answer, for the pane that asked.
    To(SeatId, ClientMessage),
}

struct Router {
    core: Arc<Core>,
    shared: Arc<CoreShared>,
    seats: Arc<SeatRegistry>,
    games: Arc<RwLock<Vec<GameInfo>>>,
    log: Arc<SessionLog>,
    settings: Arc<CoreSettings>,
    /// The game's own screen, written down as it happens: the router opens each attempt's banner
    /// in it, and the seat registry appends the screen itself (03_session.md §4.2 (`S31`)).
    playback: Playback,
    game_tx: mpsc::Sender<GameRequest>,
    /// Terminal fan-out: non-blocking, ordered, single writer.
    terminal_tx: mpsc::Sender<Fanout>,
    /// Companion lines held back until the turn in flight finishes (§12).
    pending_companion: VecDeque<ClientMessage>,
    /// The pane whose `/` command is being answered, while `run_verb` runs.
    ///
    /// Set for the duration of one command and cleared after it, so `say` can tell the
    /// difference between “the session has something to say” (a broadcast) and “this pane
    /// asked a question” (an answer it would otherwise never see). The router is
    /// single-threaded, so nothing else can run while this is set.
    replying_to: Option<SeatId>,
    /// Is anybody actually here? `None` until it has been worked out once.
    ///
    /// The companion has its own curiosity and will happily work with no audience, and every idle
    /// nudge restarts the idle clock — so a companion left alone plays the whole game through, one
    /// allowed move at a time, on somebody's account. The rule is the 1983 one: with nobody in the
    /// room there is nobody to play *with*, so it waits (04_companion.md §5 (`C29`–`C41`)).
    player_present: Option<bool>,
    /// A game change that `/switch` has already announced to the pane that asked for it.
    ///
    /// The switch is answered synchronously — the command waits for the game it asked for —
    /// but the `game_changed` event that says "now playing …" is delivered afterwards, by
    /// which time the command has been answered and `replying_to` is cleared. Without this
    /// the pane that typed `/switch wishbringer` heard nothing at all about it (issue #25),
    /// while every other pane was told twice.
    announced_switch: Option<String>,
    /// The interpreter's own status line, as the last turn carried it — what `/bar` answers with
    /// (02_game_boundary.md §4.3 (`G23`)).
    ///
    /// Kept rather than asked for: it arrives with every turn and no request can make it fresher,
    /// and it is the only way the numbers ever become readable — the interpreter draws them into
    /// row one by cursor addressing, so they are in no turn's output and in no pane's transcript
    /// (`G21`). It holds the line `Turn::status_line` made of that turn's fields, and it is `None`
    /// both before the first turn and after a turn that reported no numbers — which is why the
    /// version is what tells `the_status_line` those two apart.
    last_status: Option<String>,
    /// Lines the game has not been given yet, because the panes were still writing when they
    /// arrived (03_session.md §5 (`S35`–`S40`)). In order, because input is.
    waiting: VecDeque<WaitingLine>,
    /// Messages on their way from here to the one task that writes to the panes.
    ///
    /// The router does not write to a pane itself, so between "the speech has been handed over"
    /// and "the panes have been counted as owing it" there is a gap — and the screen is asked
    /// whether it has caught up in exactly that gap. Without this counter a command could always
    /// slip through in the instant after the sentence announcing it, which is the failure the
    /// whole arrangement exists to prevent.
    on_the_way: Arc<AtomicUsize>,
}

impl Router {
    async fn run(
        mut self,
        mut cmd_rx: mpsc::Receiver<CoreCommand>,
        mut game_rx: mpsc::Receiver<GameEvent>,
    ) {
        loop {
            // Before anything else: has the screen stopped moving? A held line has no message of
            // its own coming, so this is the only place it can be let go (03_session.md §5 (`S35`–`S40`)).
            self.release_held_lines().await;

            // The borrow of `self` ends with the select, so handlers can use it.
            enum Step {
                Command(CoreCommand),
                Event(GameEvent),
                Tick,
                BothClosed,
            }
            let step = tokio::select! {
                cmd = cmd_rx.recv() => match cmd { Some(c) => Step::Command(c), None => Step::BothClosed },
                evt = game_rx.recv() => match evt { Some(e) => Step::Event(e), None => Step::BothClosed },
                // Only while there is a line to let go of, so an idle session has no timer in
                // this loop at all.
                _ = tokio::time::sleep(self.settings.tuning.held_line_tick), if !self.waiting.is_empty() => Step::Tick,
            };

            match step {
                Step::Command(command) => {
                    if self.handle_command(command).await {
                        break;
                    }
                }
                Step::Event(event) => self.handle_game_event(event).await,
                // Nothing to do: the loop looks at the screen again at the top.
                Step::Tick => {}
                Step::BothClosed => {
                    info!("core router: channels closed; stopping");
                    break;
                }
            }
        }
        self.log.finish().await;
    }

    /// The player is composing a line and has not sent it (issue #42).
    ///
    /// The one failure this is for: the companion asks the player something, the player starts
    /// typing the answer, and the companion's patience runs out while they are mid-sentence — so
    /// it settles its own question and carries on without them. Measured in the UAT session:
    /// four of ten questions were answered by the companion acting first.
    ///
    /// It is deliberately not a reason to act, not an event in the log, and not a reason to
    /// speak: a keystroke is somebody being *there*, and the only thing it changes is how long a
    /// wait may last. The agent decides that; the core only reports the fact, because the
    /// alternative is the core guessing what a half-typed sentence means.
    async fn handle_player_typing(&mut self, seat_id: SeatId) {
        if !self.core.agent_online() {
            return;
        }
        if !self.seats.is_keyboard(seat_id).await {
            return;
        }
        self.to_protocol(Outbound::PlayerTyping).await;
    }

    /// The player's line begins with a space, or stops beginning with one (03_session.md §6 (`S41`–`S43`)).
    ///
    /// Reported rather than acted on: the agent holds the companion while this is true, and the
    /// hold is the agent's business because *"wait while I write this"* is a thing said to a
    /// person, not a property of a terminal. The same two guards as `player_typing`: a companion
    /// that is not there cannot be held, and a pane nobody can type in cannot say anything.
    async fn handle_player_composing(&mut self, seat_id: SeatId, starts_with_space: bool) {
        if !self.core.agent_online() {
            return;
        }
        if !self.seats.is_keyboard(seat_id).await {
            return;
        }
        self.to_protocol(Outbound::PlayerComposing { starts_with_space })
            .await;
    }

    /// Tell the companion whether there is anybody here.
    ///
    /// Sent only when the answer changes, and re-sent when the companion itself connects — so a
    /// companion that starts with an empty room waits, and one that starts mid-evening knows it
    /// has company.
    ///
    /// Why this exists: left alone, the companion plays. Measured on the first live session, it
    /// took eleven turns, unlocked the grate and was working out the lamp with nobody connected,
    /// because each idle nudge grants one more move and restarts the clock that produced it. It
    /// cost real money, and it is the opposite of the frame: two people, one keyboard. With nobody
    /// in the room there is nobody to play with, so the companion waits for them.
    async fn note_player_presence(&mut self) {
        // A seat that cannot type does not count. This used to be every SSH seat, which meant a
        // `game` pane left open on a desk — a screen, with no keyboard at all — told the
        // companion somebody was sitting there. On a server that runs all day, a pane nobody
        // closed kept that lie alive indefinitely.
        let here = self
            .seats
            .list()
            .await
            .iter()
            .any(|(_, _, kind, keyboard)| *kind == SeatKind::Ssh && *keyboard);

        if self.player_present == Some(here) {
            return;
        }

        self.player_present = Some(here);
        let (event, detail) = if here {
            ("player_present", "the player is at the keyboard")
        } else {
            ("player_absent", "the player has gone; wait for them")
        };

        debug!(event, "player presence changed");
        self.log.emit(
            log::KIND_SYSTEM,
            json!({ "event": event, "detail": detail }),
        );
        self.to_protocol(Outbound::system(event, detail)).await;
    }

    /// Returns true when the daemon should stop.
    async fn handle_command(&mut self, command: CoreCommand) -> bool {
        match command {
            CoreCommand::SeatOpened { id, name, kind } => {
                self.log.emit(
                    log::KIND_SEAT,
                    json!({ "event": "opened", "seat": id, "name": name, "kind": kind.label() }),
                );
                debug!(seat = id, name = %name, kind = kind.label(), "seat opened");
                if kind == SeatKind::Ssh {
                    self.say(format!("{name} connected."));
                    self.to_protocol(Outbound::system(
                        "player_connected",
                        format!("{name} is at the keyboard"),
                    ))
                    .await;

                    // A pane that logs in mid-session has missed every `ready` so far, and the
                    // typing pane is a keyboard: it has somewhere to type from the moment it
                    // connects, whatever the game happens to be in the middle of (issue #37).
                    // Nothing else can land on the intro's text either — only the typing pane
                    // renders `Ready`, and every other view filters it out.
                    //
                    // This used to wait for a framed turn *and* for no turn to be open, which
                    // left a pane opened during the startup window showing no prompt at all
                    // until the player happened to press Return.
                    self.seats.send_terminal_to(id, &ClientMessage::Ready).await;

                    self.note_player_presence().await;
                }
            }
            CoreCommand::SeatClosed { id, kind } => {
                self.log.emit(
                    log::KIND_SEAT,
                    json!({ "event": "closed", "seat": id, "kind": kind.label() }),
                );
                if kind == SeatKind::Ssh {
                    self.to_protocol(Outbound::system(
                        "player_disconnected",
                        "the player left their terminal",
                    ))
                    .await;

                    self.note_player_presence().await;
                }
            }
            CoreCommand::PlayerLine { seat_id, line } => {
                self.handle_player_line(seat_id, line).await;
            }
            CoreCommand::PlayerComposing {
                seat_id,
                starts_with_space,
            } => {
                self.handle_player_composing(seat_id, starts_with_space)
                    .await;
            }
            CoreCommand::PlayerTyping { seat_id } => {
                self.handle_player_typing(seat_id).await;
            }
            CoreCommand::GameInput {
                seat_id,
                line,
                source,
                key,
            } => {
                // A keystroke is written exactly as it was given: a line of one space is a
                // space *key*, and trimming it would quietly turn it into a Return, which is a
                // different character to the game. An ordinary command is trimmed, because a
                // trailing space in a typed command is a typo.
                let line = if key { line } else { line.trim().to_string() };

                // An empty line is a mistake unless it is a keystroke: `press_key` with no
                // argument is a bare Return, and frotz reports an empty line to the game as a
                // single carriage return, which is what a "press any key" prompt wants. This
                // guard used to drop it silently — so a companion that worked out exactly what
                // the prompt was asking for got nothing at all for its trouble.
                if line.is_empty() && !key {
                    return false;
                }

                // The interpreter's namespace is not a game command. A line beginning with a
                // backslash never reaches the story: dfrotz eats it and does the interface's
                // bidding — `\N` restarts the story behind the core's checkpoint, `\X` quits
                // the interpreter, `\R` and `\P` record and replay a command file, which is a
                // walkthrough in disguise. Nothing above this line can tell the difference,
                // which is exactly why the refusal is here, at the one place that writes into
                // frotz, and not in the prompt.
                if line.starts_with('\\') {
                    warn!(seat = seat_id, line = %line, "refused: the interpreter's own namespace");
                    self.log.emit(
                        log::KIND_AGENT_GAME_INPUT,
                        json!({ "seat": seat_id, "line": line, "source": source.label(), "key": key, "refused": "interpreter-namespace" }),
                    );
                    // Told, not silently dropped. A companion whose line evaporated would wait
                    // for a turn that is never coming, which is the failure that reads to a
                    // player as the game having stopped.
                    self.to_protocol(Outbound::system(
                        "input_refused",
                        "a line beginning with a backslash is the interpreter's own, not the game's",
                    ))
                    .await;
                    return false;
                }

                self.log.emit(
                    log::KIND_AGENT_GAME_INPUT,
                    json!({ "seat": seat_id, "line": line, "source": source.label(), "key": key }),
                );
                self.give_the_game_a_line(line, source).await;
            }
            CoreCommand::AgentUtterance { kind, text } => {
                self.handle_utterance(kind, text).await;
            }
            CoreCommand::AgentLamp { on } => {
                self.handle_lamp(on).await;
            }
            CoreCommand::AgentOnline { name } => {
                self.shared.agent_online.store(true, Ordering::Relaxed);
                *self.shared.agent_name.write().await = Some(name.clone());
                info!(agent = %name, "companion connected");
                self.log.emit(
                    log::KIND_SYSTEM,
                    json!({ "event": "agent_online", "name": name }),
                );
                // A companion that arrives while nobody is here must not assume an audience, and
                // one that arrives while somebody *is* here needs telling that too — it has no way
                // of knowing either way until the next seat opens or closes.
                self.player_present = None;
                self.note_player_presence().await;

                // The session's danger level goes with the greeting: a companion that has just
                // restarted is holding the configured default, not what the player chose ten
                // minutes ago, and the two must not disagree (§4.7).
                let level = self.shared.risk_level.read().await.clone();
                self.to_protocol(Outbound::system("risk_level", level))
                    .await;

                // The tool trace goes with the greeting for the same reason: a companion that has
                // just restarted is not sending the lines, whatever the player set ten minutes
                // ago, and a switch that only works until the next restart is worse than none.
                let debug = self.shared.debug.load(Ordering::Relaxed);
                self.to_protocol(Outbound::system("debug", if debug { "on" } else { "off" }))
                    .await;

                self.say(format!("{} has joined you.", self.core.agent_label()));
            }
            CoreCommand::AgentOffline => {
                if self.shared.agent_online.swap(false, Ordering::Relaxed) {
                    warn!("companion went offline; the player has the keyboard");
                    self.log
                        .emit(log::KIND_SYSTEM, json!({ "event": "agent_offline" }));
                    self.say(
                        "Your companion stepped away. You have the keyboard — type commands directly. It will rejoin automatically.",
                    );
                }
            }
            CoreCommand::Shutdown { reply } => {
                info!("shutdown requested");
                let (tx, rx) = oneshot::channel();
                self.to_game(GameRequest::Shutdown { reply: tx }).await;
                match tokio::time::timeout(self.settings.tuning.shutdown_grace, rx).await {
                    Ok(_) => debug!("game actor stopped"),
                    Err(_) => warn!("game actor did not stop in time"),
                }
                self.flush_companion(true).await;
                let _ = reply.send(());
                return true;
            }
        }
        false
    }

    // -- player input -------------------------------------------------------

    async fn handle_player_line(&mut self, seat_id: SeatId, line: String) {
        let trimmed = line.trim().to_string();
        if trimmed.is_empty() {
            return;
        }
        self.log.emit(
            log::KIND_PLAYER_LINE,
            json!({ "seat": seat_id, "text": trimmed }),
        );
        self.log.trace("player", &trimmed);

        // Show the player's own words to the other panes. Quill's pane is a conversation,
        // and half a conversation does not read: the companion's replies and its proposals
        // referred to things the pane never showed being said. Their own window is left
        // out, because it echoed their keystrokes as they typed them.
        self.seats
            .send_terminal_except(seat_id, &ClientMessage::Player(trimmed.clone()))
            .await;

        // `.` is `/direct` in short form (issue #24): one command, straight to the game, for
        // when the leading slash is just friction. A single dot, and only when something
        // follows it — a line that merely begins with punctuation is still a line.
        //
        // It is *not* `/type`: that one goes to the companion, which is exactly the
        // difference between typing it yourself and asking somebody to.
        if let Some(rest) = trimmed.strip_prefix('.') {
            if !rest.is_empty() && !rest.starts_with([' ', '.']) {
                self.replying_to = Some(seat_id);
                self.direct(rest).await;
                self.replying_to = None;
                self.prompt_the_pane();
                return;
            }
        }

        let Some(rest) = trimmed.strip_prefix('/') else {
            // A plain line: conversation with the companion when it is here,
            // otherwise a command straight to the game (agent-less mode).
            if self.core.agent_online() {
                self.to_protocol(Outbound::Chat {
                    speaker: "player".to_string(),
                    text: trimmed,
                })
                .await;
            } else {
                self.give_the_game_a_line(trimmed, InputSource::Player)
                    .await;
            }
            return;
        };

        // A command's answer belongs in the pane that typed it, and that pane gets its
        // prompt back afterwards whatever happened (issue #25). Both are the whole of what
        // `replying_to` is for; nothing else runs while it is set.
        self.replying_to = Some(seat_id);
        let (word, args) = split_verb(rest);
        match Verb::parse(word) {
            Some(verb) => {
                self.log.emit(
                    log::KIND_CORE_COMMAND,
                    json!({ "verb": word, "args": args }),
                );
                self.run_verb(verb, args).await;
            }
            None => {
                // An unknown `/` word is a mistake, not a command (issue #21). It used to
                // be forwarded to the game, so `/north` worked — and so did every typo,
                // which is how a mistyped slash word became a wasted turn nobody could
                // explain. Passing a command through is `/type` or `.`.
                self.log.emit(
                    log::KIND_CORE_COMMAND,
                    json!({ "verb": word, "args": args, "unknown": true, "seat": seat_id }),
                );
                self.say(format!(
                    "`/{word}` is not a command — `.` <command> or `/direct <command>` sends one straight to the game, /type <command> asks your companion to, and /help lists the rest."
                ));
            }
        }
        self.replying_to = None;
        self.prompt_the_pane();
    }

    async fn run_verb(&mut self, verb: Verb, args: &str) {
        match verb {
            Verb::Help => {
                let agent = self.core.agent_online();
                let mut text = String::new();
                text.push_str("INFOCompanion commands\n");
                if agent {
                    text.push_str("  just talk            your companion decides what to do\n");
                } else {
                    text.push_str(
                        "  <command>            plays the game directly (companion offline)\n",
                    );
                }
                // Listed in both modes: they are how a refused `/` word is answered, so a
                // player who typed one has to be able to find them (issue #21). The two
                // routes are deliberately separate — `/type` asks, `/direct` does — and the
                // `.` short form is `/direct` in one keystroke (issue #24).
                text.push_str(
                    "  /type <command>      ask your companion to type it (it may object once)\n",
                );
                text.push_str("  /direct <command>    type it yourself, straight through\n");
                text.push_str("  . <command>          the same as /direct, in one keystroke\n");
                text.push_str(
                    "  /pressreturn         press Return, at a prompt that is not asking for a command\n",
                );
                text.push_str(
                    "  /presskey <keys>     press a key — a menu's digit, a y — and one word walks a menu\n",
                );
                text.push_str("  /cost                what this session has cost so far\n");
                text.push_str("  /games               list the games in the games directory\n");
                text.push_str("  /switch <game>       save here and start another game\n");
                text.push_str(
                    "  /restart             start this game again, keeping what I have learned\n",
                );
                text.push_str(
                    "  /reset <game>        forget a game completely — the nuclear one\n",
                );
                text.push_str("  /save                checkpoint now\n");
                text.push_str(
                    "  /save <name>         checkpoint now, under a name you can come back to\n",
                );
                text.push_str(
                    "  /saves               what saves this game has (/list works too)\n",
                );
                text.push_str("  /restore [name]      go back to a save, forking the world\n");
                text.push_str(
                    "  /status              what is loaded, and how this session is going\n",
                );
                text.push_str(
                    "  /bar                 the game's own numbers — where you are, the score and the moves, or the time of day\n",
                );
                text.push_str("  /risk                the standing danger level\n");
                text.push_str(
                    "  /risklow /riskmedium /riskhigh   set it: ask about everything, ask for what you would regret, or not at all\n",
                );
                if agent {
                    text.push_str("\nwhat your companion is thinking (read-only)\n");
                    text.push_str("  /plan                what it is currently trying to do\n");
                    text.push_str(
                        "  /notes               its notebook: facts, map notes, dead ends\n",
                    );
                    text.push_str(
                        "  /turns [n]           what it has watched, turn by turn (default 15)\n",
                    );
                    text.push_str(
                        "  /meta [n]            its notes about the harness and itself, not the game\n",
                    );
                    text.push_str("  /think               its reasoning for the last turn\n");
                    text.push_str(
                        "  /thinkon /thinkoff   show or hide that reasoning as it arrives\n",
                    );
                    text.push_str(
                        "  /tagson /tagsoff     mark every line with who it is from, or not\n",
                    );
                    text.push_str(
                        "  /speed               how fast the words arrive; /slow, /medium, /fast…\n",
                    );
                    text.push_str(
                        "  /debugon /debugoff   show the calls it makes — tools, searches, notes\n",
                    );
                    text.push_str(
                        "  /catchup [game]      have it write up a game for coming back to it later\n",
                    );
                }
                text.push_str("  /help                this list\n");
                self.say(text);
            }
            Verb::Games => {
                self.say_game_list().await;
            }
            Verb::Switch => {
                let wanted = args.trim();
                if wanted.is_empty() {
                    self.say_game_list().await;
                    return;
                }
                let games = self.games.read().await.clone();
                let game = match games::resolve(&games, wanted) {
                    Ok(game) => game,
                    Err(err) => {
                        self.say(format!("{err}"));
                        return;
                    }
                };
                if game.stem == self.shared.game.read().await.stem {
                    self.say(format!("already playing {}.", game.stem));
                    return;
                }
                let leaving = self.shared.game.read().await.stem.clone();
                // A line held for a screen that had not caught up belongs to the story being
                // left (03_session.md §5 (`S35`–`S40`)): the player has just said the world is not the one they
                // wanted, so the word they typed before that goes with it.
                self.drop_held_lines("the game is being switched");
                let (tx, rx) = oneshot::channel();
                self.to_game(GameRequest::Switch { game, reply: tx }).await;
                match rx.await {
                    Ok(Ok(status)) => {
                        // Answered here, while the pane that typed it is still the one being
                        // replied to, so `/switch` says what it did where it was asked rather
                        // than only in everybody else's pane (issue #25).
                        let title = status.title.clone().unwrap_or_else(|| status.stem.clone());
                        self.announced_switch = Some(status.stem.clone());

                        // The attempt that was in progress is over — a different game has been
                        // loaded — and the new game either carries on its own open attempt or
                        // starts its next number (02_game_boundary.md §6 (`G27`–`G34`)). Whether it is the third go at a
                        // game is the companion's to say; whether it is recorded is ours.
                        if let Err(err) = runs::end(self.settings.state.root(), &leaving) {
                            warn!(error = %err, leaving = %leaving, "could not close the run book");
                        }
                        match runs::continue_or_start(self.settings.state.root(), &status.stem) {
                            Ok(attempt) => {
                                info!(
                                    run = attempt.number,
                                    stem = %status.stem,
                                    "this attempt is now in progress"
                                );
                                // A new game is a new file's worth of screen: the playback says
                                // which game and which attempt the lines that follow belong to.
                                self.playback.note_attempt(
                                    &status.stem,
                                    attempt.number,
                                    attempt.generation,
                                    attempt.started.clone(),
                                );
                                *self.shared.run.write().await = Some(attempt);
                            }
                            Err(err) => warn!(error = %err, "could not open the run book"),
                        }

                        self.say(format!("now playing {title}"));
                        self.log.emit(
                            log::KIND_SYSTEM,
                            json!({ "event": "game_switched", "stem": status.stem }),
                        );
                    }
                    Ok(Err(err)) => {
                        error!(error = %err, "switch failed");
                        self.say(format!("could not switch games: {err}"));
                    }
                    Err(_) => self.say("the game actor did not answer the switch request."),
                }
            }
            Verb::Restart => {
                let stem = self.shared.game.read().await.stem.clone();
                self.drop_held_lines("the game is being restarted");
                let (tx, rx) = oneshot::channel();
                self.to_game(GameRequest::Restart { reply: tx }).await;
                match rx.await {
                    Ok(Ok(_)) => {
                        // A new attempt, numbered (02_game_boundary.md §6 (`G27`–`G34`)). The companion is not new:
                        // nothing has been touched except the checkpoint, so its notebook — and
                        // everything it knows about the player — is still on disk (issue #26).
                        let attempt = match runs::start_new(self.settings.state.root(), &stem) {
                            Ok(attempt) => {
                                // A restart opens a new attempt in the same game, so the playback
                                // says which one the lines after this belong to (§5 W2).
                                self.playback.note_attempt(
                                    &stem,
                                    attempt.number,
                                    attempt.generation,
                                    attempt.started.clone(),
                                );
                                *self.shared.run.write().await = Some(attempt.clone());
                                Some(attempt)
                            }
                            Err(err) => {
                                warn!(error = %err, "could not open the next attempt");
                                None
                            }
                        };
                        let forgotten = {
                            let mut transcript = self.shared.transcript.write().await;
                            let count = transcript.len();
                            transcript.clear();
                            count
                        };
                        info!(turns = forgotten, "restart cleared the transcript tail");
                        let which = match attempt {
                            Some(attempt) => format!("attempt {}", attempt.number),
                            None => "a fresh run".to_string(),
                        };
                        self.say(format!(
                            "{stem} has been restarted — {which}, and I still have my notes."
                        ));
                    }
                    Ok(Err(err)) => self.say(format!("{err}")),
                    Err(_) => self.say("the game actor did not answer the restart request."),
                }
            }
            Verb::Reset => {
                let stem = self.shared.game.read().await.stem.clone();
                let wanted = args.trim();
                if wanted.is_empty() {
                    self.say(format!(
                        "/reset forgets a game completely: checkpoint and notebook both. \
                         {who} will not remember {stem} at all — the memory of *you* is \
                         untouched. Type `/reset {stem}` to confirm, or `/restart` to just \
                         play it again.",
                        who = self.core.agent_label()
                    ));
                    return;
                }
                let (tx, rx) = oneshot::channel();
                self.drop_held_lines("the game is being reset");
                self.to_game(GameRequest::Reset {
                    confirm_stem: wanted.to_string(),
                    reply: tx,
                })
                .await;
                match rx.await {
                    Ok(Ok(_)) => {
                        // "As if the game had never been played" has to include the turn
                        // tail a newly connected agent is sent in `welcome` (§6.3), or the
                        // next companion to connect inherits the run that was just wiped.
                        let forgotten = {
                            let mut transcript = self.shared.transcript.write().await;
                            let count = transcript.len();
                            transcript.clear();
                            count
                        };
                        info!(turns = forgotten, "reset cleared the transcript tail");

                        // As if it had never been played, which has to include the attempts: the
                        // run book is the only thing that zeroes a count, and this is the only
                        // thing that removes it (02_game_boundary.md §6 (`G27`–`G34`)).
                        if let Err(err) = runs::forget(self.settings.state.root(), &stem) {
                            warn!(error = %err, "could not forget the run book");
                        }
                        *self.shared.run.write().await = None;

                        self.say(format!(
                            "{stem} has been reset — as if it had never been played: \
                             no checkpoint, no notebook, and no record of the attempts it took."
                        ));
                    }
                    Ok(Err(err)) => self.say(format!("{err}")),
                    Err(_) => self.say("the game actor did not answer the reset request."),
                }
            }
            Verb::Save => {
                let stem = self.shared.game.read().await.stem.clone();
                // A name is checked before anything is asked of the game, so a name that cannot
                // be a save is refused without spending a turn finding out (§5.3).
                let slot = match args.trim() {
                    "" => None,
                    raw => match save::new_slot_name(&self.settings.state, &stem, raw) {
                        Ok(name) => Some(name),
                        Err(reason) => {
                            self.say(reason);
                            return;
                        }
                    },
                };
                let asked_for = slot.clone();
                let (tx, rx) = oneshot::channel();
                self.to_game(GameRequest::Checkpoint {
                    reason: SaveReason::Manual,
                    reply: tx,
                    slot,
                })
                .await;
                match rx.await {
                    // Answered here so the pane that typed `/save` is told, which the
                    // checkpoint's own notice cannot do: that is delivered after this command
                    // has been answered, so it goes out as a broadcast and the typing pane —
                    // which shows its own commands and nothing else — never sees it.
                    //
                    // The *file* is named (§2.6). "saved wishbringer" was true and useless.
                    Ok(Ok(())) => self.say(match &asked_for {
                        Some(name) => format!("saved {name} — saves/{stem}/{name}.sav"),
                        None => format!("saved {stem} — saves/{stem}/{stem}.sav"),
                    }),
                    Ok(Err(err)) => self.say(format!("{err}")),
                    Err(_) => self.say("the game actor did not answer the save request."),
                }
            }
            Verb::Restore => {
                let stem = self.shared.game.read().await.stem.clone();
                let slot = match args.trim() {
                    "" => None,
                    raw => match save::slot_name(raw) {
                        Ok(name) => Some(name),
                        Err(reason) => {
                            self.say(reason);
                            return;
                        }
                    },
                };
                let label = slot.clone().unwrap_or_else(|| stem.clone());
                let (tx, rx) = oneshot::channel();
                // The world is about to fork: a line held for this screen was aimed at the branch
                // being left behind (03_session.md §5 (`S38`) and §10 (`S67`)).
                self.drop_held_lines("the world is being forked by a restore");
                self.to_game(GameRequest::Restore { slot, reply: tx }).await;
                match rx.await {
                    Ok(Ok(_)) => self.say(format!(
                        "{stem} is back at `{label}`. The world forked: what happened after that \
                         save did not, and what the companion knows about the game still stands."
                    )),
                    Ok(Err(err)) => self.say(format!("{err}")),
                    Err(_) => self.say("the game actor did not answer the restore request."),
                }
            }
            Verb::Saves => {
                let stem = self.shared.game.read().await.stem.clone();
                let found = save::slots(&self.settings.state, &stem);
                if found.is_empty() {
                    self.say(format!(
                        "{stem} has no saves yet. `/save <name>` keeps one you can come back to."
                    ));
                    return;
                }

                let mut text = format!("saves for {stem}\n");
                for slot in &found {
                    let marker = if slot.canonical { "*" } else { " " };
                    let note = if slot.canonical {
                        "  — the game's own checkpoint"
                    } else {
                        ""
                    };
                    text.push_str(&format!(
                        "  {marker} {:<16} {}{note}\n",
                        slot.name,
                        slot.described()
                    ));
                }
                text.push_str(
                    "\n`/restore <name>` goes back to one; `/save <name>` keeps a new one.",
                );
                self.say(text);
            }
            Verb::Plan => self.say_plan().await,
            Verb::Notes => self.say_notes().await,
            Verb::Think => self.say_think().await,
            Verb::ThinkOn => {
                self.shared.think_visible.store(true, Ordering::Relaxed);
                self.say_to_the_asker("Reasoning shows in the panes again as I think.");
            }
            Verb::ThinkOff => {
                self.shared.think_visible.store(false, Ordering::Relaxed);
                self.say_to_the_asker(
                    "Reasoning is hidden while I think — the conversation goes quiet between lines. `/think` still reads the last one.",
                );
            }
            Verb::Turns => self.say_turns(args).await,
            Verb::Meta => self.say_meta(args).await,
            Verb::TagsOn => {
                self.shared.tags.store(true, Ordering::Relaxed);
                self.say_to_the_asker(
                    "Tags on — every line in every pane says who it is from again.",
                );
            }
            Verb::TagsOff => {
                self.shared.tags.store(false, Ordering::Relaxed);
                self.say_to_the_asker(
                    "Tags off — the panes read as plain text, in the same colours. `/tagson` puts them back.",
                );
            }
            Verb::Pace(pace) => {
                self.shared.pace.store(pace.index(), Ordering::Relaxed);
                self.say_to_the_asker(pace::report(pace, &self.shared.rates));
            }
            Verb::Speed => {
                let pace = self.core.pace();
                let mut text = pace::report(pace, &self.shared.rates);
                text.push_str("\nThe ladder: ");
                text.push_str(
                    &Pace::all()
                        .iter()
                        .map(|level| {
                            let rates = &self.shared.rates;
                            match rates.words_per_minute(*level) {
                                Some(words) => format!("`/{}` {} wpm", level.name(), words),
                                None => format!("`/{}` as fast as it comes", level.name()),
                            }
                        })
                        .collect::<Vec<_>>()
                        .join(", "),
                );
                text.push('.');
                self.say_to_the_asker(text);
            }
            Verb::Type => self.ask_type(args).await,
            Verb::Direct => self.direct(args).await,
            Verb::PressReturn => self.press_key("").await,
            Verb::PressKey => self.press_key(args).await,
            Verb::Cost => self.say_cost().await,
            Verb::Catchup => self.ask_for_a_report(args).await,
            Verb::RiskLow => self.set_risk_level("low").await,
            Verb::RiskMedium => self.set_risk_level("medium").await,
            Verb::RiskHigh => self.set_risk_level("high").await,
            Verb::DebugOn => self.set_debug(true).await,
            Verb::DebugOff => self.set_debug(false).await,
            Verb::Debug => {
                let on = self.shared.debug.load(Ordering::Relaxed);
                self.say(format!(
                    "tool trace is {} — `/debugon` shows every call {} makes, `/debugoff` stops it.",
                    if on { "on" } else { "off" },
                    self.core.agent_label()
                ));
            }
            Verb::Risk => {
                let level = self.shared.risk_level.read().await.clone();
                self.say(format!(
                    "danger level: {level} — at or under it, {} acts without asking; above it, you get the question first.\n`/risklow`, `/riskmedium` and `/riskhigh` set it, for this session.",
                    self.core.agent_label()
                ));
            }
            Verb::Bar => {
                // The numbers the interpreter draws into row one, which no pane has ever held
                // (02_game_boundary.md §4.2.1 (`G21`), §4.3 (`G23`), §7 (`G38`)). It is the last
                // turn's line rather than a reading of the screen, and the version is what says
                // which of the two honest silences applies when there is none.
                let version = self.shared.game.read().await.version;
                self.say(the_status_line(self.last_status.as_deref(), version));
            }
            Verb::Status => {
                let status = self.shared.game.read().await;
                let games = self.games.read().await.len();
                let turns = self.shared.turn_count.load(Ordering::Relaxed);
                let uptime = self.shared.started.elapsed().as_secs();
                let checkpoints = self.settings.state.has_checkpoint(&status.stem);
                let agent = self
                    .shared
                    .agent_name
                    .read()
                    .await
                    .clone()
                    .unwrap_or_else(|| "offline".to_string());
                let seats = self.seats.list().await;
                // Panes that can type, rather than panes that exist: `/status` reporting an
                // abandoned game screen as a player would be the same mistake the presence
                // check used to make.
                let players = seats
                    .iter()
                    .filter(|(_, _, kind, keyboard)| *kind == SeatKind::Ssh && *keyboard)
                    .count();
                let mut text = String::new();
                text.push_str(&format!(
                    "game: {} ({})\n",
                    status.stem,
                    status
                        .version
                        .map(|v| format!("Z-code v{v}"))
                        .unwrap_or_else(|| "unknown".into())
                ));
                if let Some(title) = &status.title {
                    text.push_str(&format!("title: {title}\n"));
                }
                text.push_str(&format!(
                    "restored at boot: {}\ncheckpoint on disk: {}\n",
                    status.restored, checkpoints
                ));
                text.push_str(&format!("games discovered: {games}\n"));
                text.push_str(&format!("players connected: {players}\n"));
                text.push_str(&format!("turns of {}: {turns}\n", status.stem));
                text.push_str(&format!("companion: {agent}\n"));
                text.push_str(&format!("uptime: {uptime}s\n"));
                text.push_str(&format!("session log: {}\n", self.core.session_id()));
                text.push_str(&format!(
                    "dropped agent chunks: {}\n",
                    self.seats.dropped_chunks()
                ));
                self.say(text);
            }
        }
    }

    /// `/direct <command>` — the player's own keystroke, for exactly one command (§10.3,
    /// issues #21 and #24). Reached by `/direct <command>` and by the `.` <command> short form.
    ///
    /// Unconditional and immediate: no model call, no objection, no delay. This is the honest
    /// 1983 arrangement — the companion could not have unsent your keystroke either — so
    /// going around it is about *who is at the keyboard*, never about withholding the game
    /// from it. The command and the whole of its output enter the transcript as usual, so the
    /// companion can still read what happened and comment on it afterwards.
    ///
    /// There is deliberately no persistent takeover mode: this is one command, not a mode.
    async fn direct(&mut self, command: &str) {
        let line = command.trim().to_string();
        if line.is_empty() {
            self.say(
                "usage: `/direct <command>`, or `.` <command> — one command, straight to the game, as if you had typed it there yourself. `/type <command>` asks your companion to type it instead.",
            );
            return;
        }
        self.log
            .emit(log::KIND_PLAYER_DIRECT, json!({ "line": line }));
        self.to_protocol(Outbound::system(
            "player_direct",
            format!("the player forced one command: {line}"),
        ))
        .await;
        self.say(format!("Taking the keyboard: {line}"));
        self.give_the_game_a_line(line, InputSource::Forced).await;
    }

    /// `/pressreturn` and `/presskey <keys>` — the player answering a prompt that is not a
    /// command, from a pane.
    ///
    /// The player's half of `press_key`, and the same unconditional route as `/direct`: no model
    /// call, no objection, no delay. It exists because the pane *is* the keyboard for a story that
    /// wants one, and a story can open that way — Bureaucracy's `[Press any key to begin.]` is the
    /// measured case, and before this the companion could answer it and the person beside it
    /// could not.
    ///
    /// **A keystroke is not a line**, and that difference is why this is a verb rather than
    /// "leave the line empty and press Return". An empty line at a `>` prompt is a wasted turn, so
    /// the panes refuse to submit one and the core drops one that arrives without being told what
    /// it is; both ends of that rule are deliberate. So the *act* gets a name, and the name is
    /// what says the empty line was meant.
    ///
    /// The keys go to the story as the interpreter reports them, one `read_char` at a time, which
    /// makes a word a menu walk rather than a command: `/presskey 2` presses `2`, and `/presskey yn`
    /// answers two questions in a row. A key that *is* a space cannot be asked for from a pane —
    /// the argument is trimmed, so a space key is the companion's business (`press_key`), not a
    /// spelling a person can type here.
    async fn press_key(&mut self, keys: &str) {
        if keys.is_empty() {
            self.log
                .emit(log::KIND_PLAYER_DIRECT, json!({ "key": true }));
            self.to_protocol(Outbound::system(
                "player_key",
                "the player pressed Return at a prompt that is not a command",
            ))
            .await;
            self.say("Pressing Return.");
        } else {
            self.log.emit(
                log::KIND_PLAYER_DIRECT,
                json!({ "key": true, "line": keys }),
            );
            self.to_protocol(Outbound::system(
                "player_key",
                format!("the player pressed `{keys}` at a prompt that is not a command"),
            ))
            .await;
            self.say(format!("Pressing `{keys}`."));
        }

        // An empty line is the Return; frotz reports it to the story as a single carriage return,
        // which is what a "press any key" prompt is waiting for.
        self.give_the_game_a_line(keys.to_string(), InputSource::Forced)
            .await;
    }

    /// `/type <command>` — the player asking the companion to type something (03_session.md §8.1 (`S53`)).
    ///
    /// This is the *social* route, not a technical escape hatch. `/direct` is the unconditional
    /// route and an unknown `/` word is refused, so if this did the same thing as `/direct`
    /// there would be no reason for it to exist — it exists because it is how you ask the other
    /// player to drive for a moment. That makes it the one place an objection can legitimately
    /// arrive *before* the act instead of after it, which is worth a slower road.
    ///
    /// The core does not decide anything here. It hands the line over and says so, because a
    /// player whose companion is not connected is otherwise left waiting on a turn that is
    /// never coming.
    async fn ask_type(&mut self, command: &str) {
        let line = command.trim().to_string();
        if line.is_empty() {
            self.say(
                "usage: /type <command> — ask your companion to type it, in the register of `can you type ……`. `/direct <command>` types it yourself, straight through.",
            );
            return;
        }

        self.log.emit(
            log::KIND_SYSTEM,
            json!({ "event": "handover", "detail": line }),
        );

        if !self.core.agent_online() {
            self.say(format!(
                "I would ask whoever is at the keyboard to type it, but nobody is. \
                 `/direct {line}` types it yourself."
            ));
            return;
        }

        self.say(format!("Asking your companion to type: {line}"));
        self.to_protocol(Outbound::Handover { line }).await;
    }

    /// What this session has cost so far.
    ///
    /// Summed from the agent's own records rather than tracked here: the core never sees a
    /// token count, and keeping its own running total would be a second source of truth for
    /// the same number.
    async fn say_cost(&mut self) {
        match session_cost(self.settings.state.logs_dir().as_path()) {
            Ok(Some(cost)) => self.say(cost.describe()),
            Ok(None) => self.say("No model calls yet, so there is nothing to report.".to_string()),
            Err(message) => self.say(message),
        }
    }

    /// List the discovered games, marking the one being played.
    async fn say_game_list(&mut self) {
        let games = self.games.read().await.clone();
        let current = self.shared.game.read().await.stem.clone();
        self.say(format_game_list(&games, &current));
    }

    // -- peepholes into the companion's head (§iteration 01 §5) --------------
    //
    // Read-only, and served straight off the shared state volume: no model call, no
    // protocol round trip, and they keep working when the model is down. The notebook
    // and the reasoning log are the companion's own words, so `/notes` and `/plan` are
    // in character while `/think` is explicitly not.

    /// Print the whole notebook.
    async fn say_notes(&mut self) {
        let stem = self.shared.game.read().await.stem.clone();
        match self.read_notebook(&stem) {
            Ok(notebook) => self.say(notebook),
            Err(message) => self.say(message),
        }
    }

    /// Print the notebook's goals-and-plan section: what it is trying to do.
    async fn say_plan(&mut self) {
        let stem = self.shared.game.read().await.stem.clone();
        let notebook = match self.read_notebook(&stem) {
            Ok(notebook) => notebook,
            Err(message) => {
                self.say(message);
                return;
            }
        };

        match plan_section(&notebook) {
            Some(plan) => self.say(plan),
            None => self.say(
                "I have not written a plan yet — ask me to do something and I will write one down."
                    .to_string(),
            ),
        }
    }

    /// Print the reasoning behind the companion's last reply.
    async fn say_think(&mut self) {
        match latest_thought(self.settings.state.logs_dir().as_path()) {
            Ok(Some(thought)) => self.think(thought),
            Ok(None) => self.say("I have not thought about anything yet.".to_string()),
            Err(message) => self.say(message),
        }
    }

    /// Print what the core has watched, turn by turn (issue #22).
    ///
    /// A peephole like the others: no model call, served off the shared state volume. It is
    /// the mechanical half of remembering — what was observed — and deliberately not the
    /// companion's reading of it, which is what `/notes` is for.
    async fn say_turns(&mut self, args: &str) {
        let wanted = args.trim().parse::<usize>().ok();
        let stem = self.shared.game.read().await.stem.clone();
        let record = observations::read(&self.settings.state.observations(&stem));
        self.say(observations::render(&record, wanted));
    }

    /// Print the companion's notes about the harness and about itself (05_memory.md §7 (`M37`–`M39`)).
    ///
    /// A peephole like the others, and the only one that is out of the world on purpose. It is
    /// worth having a verb for because the notes are written by two hands: the companion's, when
    /// something was worth remembering that was not about the game, and the harness's own, when a
    /// pass failed and the companion had no way to know. So this is the place to look when a button
    /// appeared to do nothing.
    async fn say_meta(&mut self, args: &str) {
        let wanted = args.trim().parse::<usize>().ok();
        let stem = self.shared.game.read().await.stem.clone();
        let path = self.settings.state.meta(&stem);
        let text = meta_notes::render(&path, wanted);
        self.say(text);
    }

    /// Ask the companion to write a game up, for a player coming back to it (03_session.md §8 (`S46`–`S57`)).
    ///
    /// The core's part in this is one line to the agent and one line back to the pane that asked.
    /// The report itself is the companion's work — its own pass, its own model call, reading
    /// everything the harness has about the game — and it is written to the state directory rather
    /// than sent here, because it is a page for a browser and not something to read at a keyboard.
    ///
    /// The game named has to be one the state directory knows something about. A report on a story
    /// nobody has played would be a page of nothing, and asking the model for one would be paying
    /// for it: this is the game boundary's own rule about who is the authority on what exists
    /// (02_game_boundary.md §2 (`G5`–`G7`)), applied to a file rather than to a prompt.
    async fn ask_for_a_report(&mut self, args: &str) {
        let named = args.trim().to_lowercase();
        let current = self.shared.game.read().await.stem.clone();
        let who = self.core.agent_label();
        let online = self.core.agent_online();

        let stem = match report_request(&named, &current, online, |stem| self.has_played_data(stem))
        {
            Ok(stem) => stem,
            Err(refusal) => {
                self.say(refusal.replace("{who}", &who));
                return;
            }
        };

        self.to_protocol(Outbound::system("catchup", stem.clone()))
            .await;
        self.log.emit(
            log::KIND_SYSTEM,
            json!({ "event": "catchup", "stem": stem }),
        );
        self.say(format!(
            "Asked {who} to write up {stem}. That is its own piece of work — everything it has on the game, one page, in the browser when it is done."
        ));
    }

    /// Does the state directory know anything about this game?
    ///
    /// The same test the browser's games list uses (07_webui.md §6.1 (`W31`)): a game with played data is one
    /// the core has watched turns of, or one the companion has notes on. Deliberately not "a story
    /// file exists for it" — that is the *available* list, which is a different question and a
    /// different page.
    fn has_played_data(&self, stem: &str) -> bool {
        let state = &self.settings.state;
        let observed = std::fs::metadata(state.observations(stem))
            .map(|meta| meta.len() > 0)
            .unwrap_or(false);
        observed || state.notebook(stem).exists()
    }

    /// Reasoning goes to the terminal seats as its own kind of message, so the panes can
    /// colour it differently from speech and from notices.
    fn think(&self, text: impl Into<String>) {
        self.to_terminals(Fanout::All(ClientMessage::Thought(text.into())));
    }

    /// Is the companion's reasoning being shown in the panes as it arrives? (issue #35)
    ///
    /// Off is a display choice and nothing more: the reasoning is still logged and traced,
    /// so `/think` reads the last one out of the log exactly as before.
    fn think_visible(&self) -> bool {
        self.shared.think_visible.load(Ordering::Relaxed)
    }

    fn read_notebook(&self, stem: &str) -> Result<String, String> {
        let path = self.settings.state.notebook(stem);
        match std::fs::read_to_string(&path) {
            Ok(text) if text.trim().is_empty() => Ok("My notebook is empty so far.".to_string()),
            Ok(text) => Ok(text),
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => {
                Ok("I have not started a notebook for this game yet.".to_string())
            }
            Err(err) => Err(format!("I could not read my notebook: {err}")),
        }
    }

    async fn handle_utterance(&mut self, kind: UtteranceKind, text: String) {
        // A chunk of thinking is not an utterance: it is the middle of one, already being shown
        // as it arrives. So it is not trimmed (a chunk can legitimately begin with the space
        // between two words), not logged and not traced — the finished thought does all three a
        // moment later. It does go through the same queue as everything else, which costs it
        // nothing in practice: no turn is open while the companion is thinking, because the
        // thinking is what it does between turns.
        if kind == UtteranceKind::ThoughtDelta {
            if self.think_visible() {
                self.queue_companion(ClientMessage::ThoughtDelta(text))
                    .await;
            }
            return;
        }

        // A working line carries no words at all — it is a fact about a call, not a thing the
        // companion said — so it is handled before the empty-text guard below.
        if kind == UtteranceKind::Working {
            self.handle_working().await;
            return;
        }

        // One tool call, for the panel (03_session.md §4.3 (`S32`–`S34`)). Machinery rather than speech, so it is a
        // notice; and it is handled before the trim below because the renderer already shaped it.
        if kind == UtteranceKind::Tool {
            self.handle_tool(&text).await;
            return;
        }

        let text = text.trim().to_string();
        if text.is_empty() {
            return;
        }
        self.log.emit(
            match kind {
                UtteranceKind::Thought => log::KIND_AGENT_THOUGHT,
                UtteranceKind::ThoughtDelta => unreachable!("handled above"),
                UtteranceKind::Working => unreachable!("handled above"),
                UtteranceKind::Tool => unreachable!("handled above"),
                UtteranceKind::Chat => log::KIND_AGENT_SAY,
                UtteranceKind::Ask => log::KIND_AGENT_ASK,
                UtteranceKind::Proposal => log::KIND_AGENT_PROPOSAL,
            },
            json!({ "text": text }),
        );
        self.log.trace(
            match kind {
                UtteranceKind::Thought => "think",
                UtteranceKind::ThoughtDelta => unreachable!("handled above"),
                UtteranceKind::Working => unreachable!("handled above"),
                UtteranceKind::Proposal => "system",
                _ => "quill",
            },
            &text,
        );

        // A proposal is the harness talking about the machinery, so it goes out as a notice.
        // Speech is the companion's alone: putting a template in its voice is the one thing
        // iteration 01 §2.3 banned, and a proposal was where it survived (issue #29).
        // Thinking out loud is a third kind of line: the view shows it in the conversation
        // and keeps it off the screen, because the screen is the game's.
        let message = match kind {
            UtteranceKind::Proposal => ClientMessage::System(text),
            UtteranceKind::Thought => ClientMessage::Thought(text),
            UtteranceKind::ThoughtDelta => unreachable!("handled above"),
            UtteranceKind::Working => unreachable!("handled above"),
            _ => ClientMessage::Companion(text),
        };
        // Hidden reasoning stops here — after the log and the trace above, so nothing about
        // what the companion thought is lost, it just does not reach a pane (issue #35).
        if matches!(kind, UtteranceKind::Thought) && !self.think_visible() {
            return;
        }
        self.queue_companion(message).await;
    }

    /// The companion's lamp, lit for the whole of a turn (03_session.md §7 (`S44`–`S45`)).
    ///
    /// The fact comes from the agent, which is the only end that knows when a call has left and
    /// when the reply has been delivered; the *pane* is told as an escape, because the page may not
    /// read the session to work this out (07_webui.md §2 (`W3`)) and an escape is how something reaches a pane
    /// without being shown on it. Every pane is told, not only the companion's: the fact is about
    /// the companion rather than about the pane it arrives in, and the page lights the lamp that
    /// belongs to the fact. It goes into neither record — a lamp is not history, and a reconnecting
    /// pane has no business replaying a light that went out ten minutes ago.
    ///
    /// One row in the session log, because a light is invisible to everything outside the page and
    /// the evening's numbers are read afterwards.
    async fn handle_lamp(&mut self, on: bool) {
        self.log.emit(
            log::KIND_SYSTEM,
            json!({ "event": "companion_lamp", "on": on }),
        );
        self.to_terminals(Fanout::All(ClientMessage::Lamp { on }));
    }

    /// One quiet line while a model call outlasts its welcome (04_companion.md §5 (`C29`–`C41`)).    ///
    /// The words are the harness's — a notice, like the proposal rendering — because the
    /// companion has not said anything: it is still mid-call. Shown only when somebody who can
    /// type is connected, and that condition is the core's to make because the core is the end
    /// that knows who is here; a line nobody can read is not worth printing. There is deliberately
    /// no clearing line: the call's own output follows it, and a second notice would be noise.
    async fn handle_working(&mut self) {
        if self.player_present != Some(true) {
            return;
        }

        self.queue_companion(ClientMessage::System("(thinking…)".to_string()))
            .await;
    }

    /// One tool or extension call, in the companion's pane (`/debugon`, 03_session.md §4.3 (`S32`–`S34`)).
    ///
    /// Off is silent, and the state is only ever sent while it is on, so nothing here costs
    /// anything a player did not ask for. It is a notice rather than speech for the same reason a
    /// proposal is: the words are the harness's, and a reader has to be able to tell what the
    /// companion *said* from what it *reached for*. It is also written to the session log, because
    /// a pane scrolls and the time this instrument matters is afterwards.
    async fn handle_tool(&mut self, line: &str) {
        if !self.shared.debug.load(Ordering::Relaxed) {
            return;
        }

        let text = line.trim().to_string();
        if text.is_empty() {
            return;
        }

        self.log
            .emit(log::KIND_SYSTEM, json!({ "event": "tool", "text": text }));
        self.queue_companion(ClientMessage::System(text)).await;
    }

    // -- game events --------------------------------------------------------

    async fn handle_game_event(&mut self, event: GameEvent) {
        match event {
            GameEvent::Raw { bytes, machinery } => {
                self.to_terminals(Fanout::All(ClientMessage::GameOutput(bytes.clone())));
                self.log.trace("screen", &String::from_utf8_lossy(&bytes));
                if !machinery {
                    // The screen gets everything; the agent gets gameplay. Machinery is what
                    // the core did to the game, not what the game did (§5.4).
                    self.seats
                        .send_protocol(&Outbound::GameOutput {
                            data: String::from_utf8_lossy(&bytes).into_owned(),
                        })
                        .await;
                }
            }
            GameEvent::Turn(turn) => {
                // The interpreter settling in is not a turn. It still reaches the screen and
                // the transcript — the player has to see where a restore left them, and the
                // companion has to be able to find out — but nothing asked for it, so there
                // is nobody to answer, no turn in the session's count, and nothing in the
                // record of what the game answered.
                if !turn.machinery {
                    // The count the banner, `/status` and the agent's boot all read *is* the game's
                    // own turn number, rather than a second tally beside it (02_game_boundary.md §6 (`G27`–`G34`)). Two
                    // counters that disagree is how the banner came to say "Turn 0" over a story
                    // that had just been restored at turn 208, and how the companion was told the
                    // game was 183 turns old when it had never been started.
                    self.shared.turn_count.store(turn.turn, Ordering::Relaxed);
                }
                self.remember_turn(&turn).await;
                // The interpreter's own line, kept for `/bar`. A machinery turn updates it
                // like any other: it is the newest reading of the numbers, and the checkpoint
                // that produced it moved the world rather than invented a status line.
                self.last_status = turn.status_line();
                if !turn.machinery {
                    self.record_observation(&turn).await;
                }
                self.log.emit(
                    log::KIND_TURN,
                    json!({
                        "turn": turn.turn,
                        "input": turn.input,
                        "elapsed_ms": turn.elapsed_ms,
                        "output_bytes": turn.output.len(),
                        "output": turn.output,
                        "awaiting_command": turn.awaiting_command,
                        "machinery": turn.machinery,
                        // What the story's own parser did with the line, when the interpreter
                        // could tell us: `take lantern(unknown)` is a different failure from a
                        // verb the dictionary does not have, and no reading of the story's
                        // prose can separate them.
                        "parser": turn.parser,
                        // The interpreter's own screen, when the story wanted a key: a form, a
                        // menu, a licence application — what the terminal shows a person and
                        // what the byte stream, by the interface's own rule, does not.
                        "screen": turn.screen,
                    }),
                );
                self.shared.turn_open.store(false, Ordering::Relaxed);
                if !turn.machinery {
                    self.seats
                        .send_protocol(&Outbound::TurnComplete(turn))
                        .await;
                }
                // The game is ready for input again: the typing pane shows its prompt now.
                self.to_terminals(Fanout::All(ClientMessage::Ready));
                self.flush_companion(false).await;
            }
            GameEvent::InputDispatched { line, source } => {
                // The screen shows a command whoever typed it (§11.1), so the trace does too.
                self.log.trace("screen", &format!("> {line}"));
                // A scripted write is not a turn. The framer never sees a scripted
                // sequence's output, so no `Turn` will arrive to close a turn opened here —
                // and with the gate shut, companion chatter would be held back forever.
                if source != InputSource::Scripted {
                    self.shared.turn_open.store(true, Ordering::Relaxed);
                }
                self.log.emit(
                    log::KIND_AGENT_INPUT,
                    json!({ "line": line, "source": source.label() }),
                );
                // Show it in the panes: on a screen a command is visible whoever typed it,
                // and the checkpoint's `save` and filename are the whole of why the game
                // said what it said next (issue #20). The panes that show the conversation
                // filter the machinery back out for themselves (§11.1).
                self.to_terminals(Fanout::All(ClientMessage::Input {
                    line: line.clone(),
                    source,
                }));
            }
            GameEvent::System { event, detail } => {
                self.log.emit(
                    log::KIND_SYSTEM,
                    json!({ "event": event, "detail": detail }),
                );
                // Three of the daemon's own bookkeeping events are recorded and not spoken:
                // auto-save is deliberately silent for the player (§5.4); a *deferred*
                // checkpoint is not yet a problem — the player is told when it is dropped or
                // fails, which is the part that costs them something; and a manual checkpoint
                // has already been answered by `/save` in the pane that asked for it, so
                // saying it again here says it twice in every other window.
                let manual_save = event == "checkpoint" && detail.contains("(manual)");
                if event != "autosave" && event != "checkpoint_deferred" && !manual_save {
                    self.say(detail.clone());
                }
                self.to_protocol(Outbound::System { event, detail }).await;
            }
            GameEvent::GameChanged(status) => {
                let restored = if status.restored {
                    " (resumed from your checkpoint)"
                } else {
                    ""
                };
                let previous_stem = self.shared.game.read().await.stem.clone();
                *self.shared.game.write().await = status.clone();

                if previous_stem == status.stem {
                    // Only the title was learned from the game's own opening text, so
                    // the player does not need telling twice at boot.
                    debug!(stem = %status.stem, title = ?status.title, "game status updated");
                    self.to_protocol(Outbound::system("game_changed", status.stem.clone()))
                        .await;
                    return;
                }

                if self.announced_switch.as_deref() == Some(status.stem.as_str()) {
                    // `/switch` has already announced this, and it announced it to everyone:
                    // the pane that asked read it as its own command's answer and the other
                    // panes read it as a notice. Saying it again here would say it twice in
                    // every window.
                    self.announced_switch = None;
                } else {
                    self.say(format!(
                        "now playing {}{restored}",
                        status.title.clone().unwrap_or_else(|| status.stem.clone())
                    ));
                }
                self.to_protocol(Outbound::system("game_changed", status.stem.clone()))
                    .await;
                // Another story has another parser, so the agent is given the new
                // vocabulary straight away rather than validating against the old one.
                if let Some(vocabulary) = self.core.vocabulary().await {
                    self.to_protocol(vocabulary).await;
                }
            }
            GameEvent::Exited(code) => {
                self.shared.turn_open.store(false, Ordering::Relaxed);
                self.flush_companion(true).await;
                self.log.emit(
                    log::KIND_SYSTEM,
                    json!({ "event": "game_exited", "code": code }),
                );
            }
        }
    }

    /// Write down what just happened: the line that was typed, and the text that came back
    /// (issue #22).
    ///
    /// The core will not say what a turn *means* — deciding that `take lamp` worked, or that
    /// two descriptions are the same place, is a judgement about text, and that is the
    /// companion's job. This records only what was on the screen: the command, and the reply.
    ///
    /// **Every turn is recorded, whatever was typed.** The core used to keep only the turns
    /// it thought were interesting — the commands that moved the player — and the cost of
    /// that was concrete. The day the companion needed to know whether the lamp had already
    /// been taken, the answer had been on the screen, in the transcript, and missing from the
    /// record, because `take lamp` is not a place. A record is evidence, and evidence is not
    /// the core's to filter.
    async fn record_observation(&mut self, turn: &Turn) {
        let stem = self.shared.game.read().await.stem.clone();

        // A restore forks the world inside the attempt (02_game_boundary.md §6 (`G27`–`G34`)): what the companion learned
        // before the fork is still true about the game, and where it stood is not. `restore` is the
        // interpreter's own command — the same class of fact as the filename prompt, and the same
        // word in every story — so recognising it here is structural rather than a reading of the
        // story's prose.
        //
        // Any case, and any argument after it: the word is the interpreter's, and who typed it
        // decides nothing. *Why that is not fussiness:* measured 2026-10-02, the companion typed
        // `RESTORE` — its own capitalisation — at Wishbringer's cliff, the story reloaded and
        // dropped it into the fog, and the exact match below missed it. With no fork stamp on the
        // turn, the fold downstream read the reload as a move from one room to another: a wall
        // drawn into a room the companion had left, and a step in the walk nobody walked
        // (05_memory.md §2 (`M13.5`)).
        if is_a_restore_line(&turn.input) {
            match runs::fork_generation(self.settings.state.root(), &stem) {
                Ok(attempt) => {
                    *self.shared.run.write().await = Some(attempt.clone());
                    info!(
                        generation = attempt.generation,
                        "the world was forked by a restore"
                    );
                    // And the fork is **announced**, not merely written down. Everything derived
                    // from the record is stamped from the run book and is rebuilt when it hears
                    // this (`M13.3`), so a bump nobody is told about leaves the derived copy on the
                    // branch the companion has just left: measured 2026-10-02, the story restored
                    // itself under a companion that typed the word, the run book went to fork 2,
                    // and the world document went on saying fork 1 with `fork_turn: null` for the
                    // rest of the evening — quoting the replaced world as the one in hand, which is
                    // the whole reason the stamps exist (`M13.2`, `M13.5`).
                    let detail = format!(
                        "{stem} was reloaded by a restore — the world forked (fork {})",
                        attempt.generation
                    );
                    self.log.emit(
                        log::KIND_SYSTEM,
                        json!({ "event": "restore", "detail": detail, "fork": attempt.generation }),
                    );
                    self.say(detail.clone());
                    self.to_protocol(Outbound::System {
                        event: String::from("restore"),
                        detail,
                    })
                    .await;
                }
                Err(err) => warn!(error = %err, "could not record the fork"),
            }
        }

        // Both stamps are read from the **run book** on disk, which is where a restore records its
        // fork (`runs::fork_generation`): the actor forks the world where it *issues* a restore
        // (`Op::Restore`), and every turn that restore writes is machinery, which is not recorded
        // at all (`G16`–`G24c`). The copy this used to stamp from was written only when a `restore`
        // *line* reached this function, so it was never written when it mattered — measured
        // 2026-09-25, an evening with two restores in it has every row of every session stamped
        // generation 1, which is 05_memory.md §2 (`M13`) reading as a world in hand on a companion
        // standing in a fork of it.
        let attempt = runs::read(self.settings.state.root(), &stem).current;
        let (run, generation) = match attempt.as_ref() {
            Some(attempt) => (attempt.number, attempt.generation),
            None => (0, 0),
        };
        // Kept in step as well, because the agent's boot reads it, and a companion that reconnects
        // after a restore should be told which world it is standing in.
        *self.shared.run.write().await = attempt;
        let path = self.settings.state.observations(&stem);

        let observation = observations::Observation {
            turn: turn.turn,
            // Empty when the story spoke by itself. That happens — a prologue, a knock at the
            // door — and it is written down as itself rather than guessed at.
            input: turn.input.trim().to_string(),
            output: turn.output.trim().to_string(),
            fingerprint: observations::fingerprint(&turn.output),
            run,
            // The fork the turn belongs to, so a citation out of a reloaded branch can be said to
            // be one (05_memory.md §2 (`M13`)). Read from the run book above rather than kept beside the
            // record, because the restore that forks the world is a machinery turn and machinery is
            // not recorded: nothing here may be the only place a fork is written down.
            generation,
            parser: turn.parser.clone(),
            screen: turn.screen.clone(),
            // The story's own answer to *where is the player* (B4), which the map needs and the
            // fingerprint cannot give: see the note on the field.
            location: turn.location,
        };
        if let Err(err) = observations::append(&path, &observation) {
            // Worth a warning, never worth a failure: a record that cannot be written is
            // worth less than a game that keeps playing.
            warn!(error = %err, path = %path.display(), "could not record a turn");
        }
    }

    async fn remember_turn(&mut self, turn: &Turn) {
        let entry = if turn.input.trim().is_empty() {
            turn.output.clone()
        } else {
            format!("> {}\n{}", turn.input.trim(), turn.output)
        };
        let mut transcript = self.shared.transcript.write().await;
        transcript.push_back(entry);
        while transcript.len() > self.settings.transcript_tail {
            transcript.pop_front();
        }
    }

    // -- output helpers -----------------------------------------------------

    /// Queue or emit companion chatter, never mid-turn (§12).
    async fn queue_companion(&mut self, message: ClientMessage) {
        if self.shared.turn_open.load(Ordering::Relaxed) {
            self.pending_companion.push_back(message);
        } else {
            self.to_terminals(Fanout::All(message));
        }
    }

    async fn flush_companion(&mut self, force: bool) {
        if !force && self.shared.turn_open.load(Ordering::Relaxed) {
            return;
        }
        while let Some(message) = self.pending_companion.pop_front() {
            self.to_terminals(Fanout::All(message));
        }
    }

    /// Hand a message to the terminal fan-out. Never blocks: a saturated queue
    /// costs a notice, not the router's progress.
    fn to_terminals(&self, fanout: Fanout) {
        // Counted before it is offered, and given back if the offer fails: "on the way" has to
        // be true for the whole window in which the message exists in neither place (02_game_boundary.md
        // §8.2, and see `Router::on_the_way`).
        self.on_the_way.fetch_add(1, Ordering::SeqCst);
        if self.terminal_tx.try_send(fanout).is_err() {
            self.on_the_way.fetch_sub(1, Ordering::SeqCst);
            debug!("terminal fan-out queue full; message dropped for players");
        }
    }

    /// Hand the game a line — or hold it until the screen has stopped moving (03_session.md §5 (`S35`–`S40`)).
    ///
    /// **This is the other half of the pace, and the half that matters.** The words on a screen
    /// arrive at a controlled rate, the companion reads the game's output the instant frotz writes
    /// it, and so it used to be able to announce a move and make it in the same breath: the story
    /// received the command while the sentence announcing it was still a third of the way out of
    /// the pane. On a screen that read as the companion acting ahead of its own words — the
    /// 2026-09-20 UAT called it "executing commands on the screen ahead of his speech" — and on a
    /// second pane it read as the game moving on its own.
    ///
    /// So a line waits for the panes to be quiet, which is the only honest definition of "the
    /// sentence has finished appearing": the panes have written everything they were given.
    ///
    /// Both voices come through here, and in practice it is the companion that ever waits: a
    /// *player's* keystrokes stop the metering before their line is even submitted (§8.2), so by the
    /// time their command arrives the screen has settled and there is nothing to hold. The companion
    /// types nothing, which is exactly why it used to get ahead of its own words.
    ///
    /// The core's *own* machinery does not wait: a checkpoint's `save` is not a move, nobody
    /// announced it, and holding one behind a slow screen would only make a save late (§2.6).
    async fn give_the_game_a_line(&mut self, line: String, source: InputSource) {
        if source != InputSource::Scripted && !self.the_screen_has_settled().await {
            let (busy, watching) = self.seats.pane_backlog().await;
            debug!(
                source = source.label(),
                line = %line,
                busy,
                watching,
                on_the_way = self.on_the_way.load(Ordering::SeqCst),
                "holding a line until the screen stops moving"
            );
            self.waiting.push_back(WaitingLine {
                line,
                source,
                stem: self.shared.game.read().await.stem.clone(),
                held: Instant::now(),
            });
            return;
        }
        self.to_game(GameRequest::Input { line, source }).await;
    }

    /// Has everything already said to the panes finished appearing? (03_session.md §5 (`S35`–`S40`))
    ///
    /// Three halves, and each is a different way for a move to get ahead of its sentence:
    ///
    /// * the panes' own counts — a pane that *has* a message is still writing it;
    /// * `on_the_way` — a message this router has sent has not yet reached the task that writes to
    ///   panes, so it is not counted anywhere yet, and the answer would otherwise be yes for the few
    ///   microseconds after the companion's sentence was announced;
    /// * the held chatter — the companion can speak while a turn is in flight, and those lines wait
    ///   in `pending_companion` until the turn ends (§12). A pane that has not been *given* the words
    ///   is a pane that looks quiet, so without this the one case the gate exists for — the companion
    ///   talking over its own move — is the one case it would miss.
    ///
    /// Nothing here can deadlock the wait: a held line is a queue, not an await, so the turn that
    /// ends and flushes the chatter is processed while the line waits.
    async fn the_screen_has_settled(&self) -> bool {
        self.pending_companion.is_empty()
            && self.on_the_way.load(Ordering::SeqCst) == 0
            && self.seats.panes_are_quiet().await
    }

    /// Let the held lines go once the panes have caught up (03_session.md §5 (`S35`–`S40`)).
    ///
    /// Patience rather than an unbounded wait: a terminal on a slow link, or one that has stopped
    /// reading without dying, must not be able to stop the game. When the patience runs out the
    /// line goes anyway and the log says so — the promise is broken in the one case where keeping
    /// it would have hung the session, and the log is where that is visible afterwards.
    async fn release_held_lines(&mut self) {
        let Some(next) = self.waiting.front() else {
            return;
        };
        let impatient = next.held.elapsed() >= self.screen_patience();
        if !impatient && !self.the_screen_has_settled().await {
            return;
        }
        let stem = self.shared.game.read().await.stem.clone();
        while let Some(next) = self.waiting.pop_front() {
            if next.stem != stem {
                // The player changed the world while this line was held. It was aimed at the
                // story that has just been left, and there is nothing sensible to do with it
                // but drop it — `/switch` says as much to the panes.
                debug!(line = %next.line, source = next.source.label(), "dropping a held line: the game changed");
                continue;
            }
            if impatient && self.waiting.is_empty() {
                info!(
                    waited_ms = next.held.elapsed().as_millis() as u64,
                    line = %next.line,
                    "the screen had not caught up in time; the line goes anyway"
                );
            } else {
                debug!(
                    waited_ms = next.held.elapsed().as_millis() as u64,
                    line = %next.line,
                    "the screen has stopped moving; the line goes"
                );
            }
            self.to_game(GameRequest::Input {
                line: next.line,
                source: next.source,
            })
            .await;
        }
    }

    /// Throw away lines that are waiting for a screen that belongs to a world being left.
    ///
    /// Used by the four verbs that change what is loaded. The line was held for the *story that
    /// was standing*: a player who types `north` and then says `/switch zork1` has changed their
    /// mind about the world, not about the word.
    fn drop_held_lines(&mut self, why: &str) {
        if self.waiting.is_empty() {
            return;
        }
        debug!(held = self.waiting.len(), why, "dropping held lines");
        self.waiting.clear();
    }

    /// How long a line may be held behind a screen that is still writing.
    ///
    /// Three times the pace's own ceiling, which bounds how long one message may take to arrive:
    /// a queue of a turn's output, a notice and a speech is three messages, and anything beyond
    /// that is a pane in trouble rather than a pane being read.
    fn screen_patience(&self) -> Duration {
        self.shared.rates.ceiling() * 3
    }

    /// A core notice for the players, or — while a `/` command is being answered — for
    /// the pane that asked.
    ///
    /// The second case is the whole of issue #25. A typing pane shows its own commands
    /// and nothing else, so an answer sent to “everyone” reached every pane except the
    /// one that had asked for it. While `replying_to` is set the pane that typed the
    /// command gets the text as its own command output and the rest of the session reads
    /// it as an ordinary notice, so nothing is said twice.
    fn say(&self, text: impl Into<String>) {
        let text = text.into();
        self.log.trace("system", &text);
        match self.replying_to {
            Some(seat) => {
                self.to_terminals(Fanout::To(seat, ClientMessage::Command(text.clone())));
                self.to_terminals(Fanout::Except(seat, ClientMessage::System(text)));
            }
            None => self.to_terminals(Fanout::All(ClientMessage::System(text))),
        }
    }

    /// A setting, said to the pane that asked and to nobody else (07_webui.md §5.1 (`W22`–`W25`)).
    ///
    /// [`Self::say`] gives every other pane a notice, which is right for something the *session*
    /// has done — a checkpoint, a switch — and wrong for something the player has just chosen. A
    /// page puts its remembered settings back every time it connects, and six notices into the
    /// conversation pane on every reconnect is the noise the settings exist to avoid; with C1 they
    /// would also be in the record that pane replays. The pane that typed the command is answered
    /// exactly as before.
    fn say_to_the_asker(&self, text: impl Into<String>) {
        let text = text.into();
        self.log.trace("system", &text);
        if let Some(seat) = self.replying_to {
            self.to_terminals(Fanout::To(seat, ClientMessage::Command(text)));
        }
    }

    /// Give the typing pane its prompt back after a `/` command.
    ///
    /// `ready` means “the game is waiting for you”, which is true again the moment a
    /// command has been answered. Only the typing pane shows this message, so a broadcast
    /// reaches exactly the panes that want it, and each pane's own line-tracking keeps a
    /// prompt from being printed twice (issue #25).
    fn prompt_the_pane(&self) {
        self.to_terminals(Fanout::All(ClientMessage::Ready));
    }

    /// `/debugon` and `/debugoff` (03_session.md §4.3 (`S32`–`S34`)): show the companion's tool and extension calls
    /// in its own pane.
    ///
    /// Sent to the agent the way the danger level is, and for the same reason: a companion that has
    /// just restarted holds the default, not what the player chose, and the two must not disagree.
    /// Off is also the cheap state, because off is what stops the lines being sent at all.
    async fn set_debug(&mut self, on: bool) {
        if self.shared.debug.load(Ordering::Relaxed) == on {
            self.say_to_the_asker(format!(
                "the tool trace is already {}.",
                if on { "on" } else { "off" }
            ));
            return;
        }

        self.shared.debug.store(on, Ordering::Relaxed);
        self.to_protocol(Outbound::system("debug", if on { "on" } else { "off" }))
            .await;
        self.log
            .emit(log::KIND_SYSTEM, json!({ "event": "debug", "on": on }));

        let who = self.core.agent_label();
        self.say_to_the_asker(if on {
            format!(
                "Tool trace on: every call {who} makes shows in its pane as `name(argument)`. `/debugoff` stops it."
            )
        } else {
            format!("Tool trace off. {who} is still working — you just cannot watch it reach.")
        });
    }

    /// `/risklow`, `/riskmedium`, `/riskhigh` (§4.7): the player's standing danger level.
    ///
    /// Two dials move together — the gate the companion's code enforces, and the sentence in
    /// the constraint block of every reply after this one — so its judgement and the harness's
    /// question never disagree. Per-session on purpose: it returns to the configured default
    /// when the daemon restarts.
    async fn set_risk_level(&mut self, level: &str) {
        {
            let mut current = self.shared.risk_level.write().await;
            if current.as_str() == level {
                self.say_to_the_asker(format!(
                    "{} is already playing at danger level {level}.",
                    self.core.agent_label()
                ));
                return;
            }

            current.clear();
            current.push_str(level);
        }

        // The agent hears it the way the player's other actions arrive: a system line.
        self.to_protocol(Outbound::system("risk_level", level))
            .await;
        self.log.emit(
            log::KIND_SYSTEM,
            json!({ "event": "risk_level", "level": level }),
        );

        // A settings command is answered to the pane that asked and to nobody else, which is the
        // rule the rest of the panel follows (07_webui.md §5.1 (`W22`–`W25`)): a page gives its remembered level back
        // on every connect, and a notice in every other pane for that would be the noise E6 exists
        // to remove. The companion is told separately, as a `risk_level` system line above, because
        // what changes is what *it* may do — and a person at the keyboard is the asker, so this is
        // the one pane that answers them either way (§4.7).
        {
            let who = self.core.agent_label();
            let text = match level {
                "low" => format!(
                    "Danger level low: {who} asks before anything that changes the world. Looking, reading and walking it just does."
                ),
                "high" => format!(
                    "Danger level high: {who} stops asking. It will still say what it is about to do — but nothing waits for your yes."
                ),
                _ => format!(
                    "Danger level medium: spending, breaking, burning and giving away still stop for your yes. Everything else {who} just does."
                ),
            };
            self.say_to_the_asker(text);
        }
    }

    /// Send an event to the protocol seats (the agent, and stdio in dev).
    async fn to_protocol(&self, message: Outbound) {
        self.seats.send_protocol(&message).await;
    }

    async fn to_game(&self, request: GameRequest) {
        if self.game_tx.send(request).await.is_err() {
            error!("game actor is gone; request dropped");
        }
    }
}

/// Split `/switch advent` into `("switch", "advent")`.
/// The notebook's goals-and-plan section, if it has one.
///
/// The notebook is markdown with fixed headings, so this is a scan rather than a parse:
/// find the heading, and take everything up to the next heading.
///
/// The canonical heading the template writes is looked for first, and by itself: the
/// notebook is the companion's own prose, and a section it wrote for itself earlier
/// ("Cat errand — the goal") must not shadow the plan it keeps under the heading it was
/// asked to keep it under. Only when the canonical heading is absent does the loose
/// match run — any heading containing "goal" or "plan" — because the wording of a
/// heading then belongs to the persona.
fn plan_section(notebook: &str) -> Option<String> {
    let canonical = "current goals and plan";
    let has_canonical = notebook.lines().any(|line| {
        let trimmed = line.trim_start();
        trimmed.starts_with('#')
            && trimmed
                .trim_start_matches('#')
                .trim()
                .eq_ignore_ascii_case(canonical)
    });

    if has_canonical {
        return section_under(notebook, |heading| heading == canonical);
    }

    section_under(notebook, |heading| {
        heading.contains("goal") || heading.contains("plan")
    })
}

/// The body of the first section whose heading satisfies `wanted`, up to the next heading.
fn section_under(notebook: &str, wanted: impl Fn(&str) -> bool) -> Option<String> {
    let mut collecting = false;
    let mut lines: Vec<&str> = Vec::new();

    for line in notebook.lines() {
        if line.trim_start().starts_with('#') {
            if collecting {
                break; // the next heading ends the section
            }
            let heading = line.trim_start_matches('#').trim().to_ascii_lowercase();
            collecting = wanted(&heading);
            continue;
        }

        if collecting {
            lines.push(line);
        }
    }

    let text = lines.join("\n");
    let text = text.trim();
    if text.is_empty() {
        None
    } else {
        Some(text.to_string())
    }
}

/// The reasoning behind the companion's most recent reply.
///
/// `/think` is a peephole, not a window: the last turn only, read from the agent's own
/// JSONL log on the shared state volume, so it costs nothing and needs no protocol.
fn latest_thought(logs_dir: &Path) -> Result<Option<String>, String> {
    let Some(newest) = newest_agent_log(logs_dir) else {
        return Ok(None);
    };

    let text = std::fs::read_to_string(&newest)
        .map_err(|err| format!("I could not read my reasoning log: {err}"))?;

    // Walk backwards: the newest record is the last thing it thought.
    for line in text.lines().rev() {
        let Ok(record) = serde_json::from_str::<serde_json::Value>(line) else {
            continue; // a half-written final line is not worth failing over
        };
        if record.get("kind").and_then(|kind| kind.as_str()) != Some("companion_think") {
            continue;
        }
        if let Some(thought) = record.get("text").and_then(|text| text.as_str()) {
            return Ok(Some(thought.to_string()));
        }
    }

    Ok(None)
}

/// What the newest agent log says this session has cost (§9.3).
///
/// Read rather than tracked. `cost_total_usd` off the last record is the session's own figure,
/// which beats re-adding rounded per-call costs and drifting.
fn session_cost(logs_dir: &Path) -> Result<Option<SessionCost>, String> {
    let Some(newest) = newest_agent_log(logs_dir) else {
        return Ok(None);
    };

    let text = std::fs::read_to_string(&newest)
        .map_err(|err| format!("I could not read my cost log: {err}"))?;

    let mut cost = SessionCost::default();
    for line in text.lines() {
        let Ok(record) = serde_json::from_str::<serde_json::Value>(line) else {
            continue; // a half-written final line is not worth failing over
        };
        if record.get("kind").and_then(|kind| kind.as_str()) != Some("llm_call") {
            continue;
        }

        cost.calls += 1;
        cost.prompt_tokens += count_at(&record, "prompt_tokens");
        cost.cache_hit_tokens += count_at(&record, "cache_hit_tokens");
        cost.completion_tokens += count_at(&record, "completion_tokens");
        if let Some(total) = record
            .get("cost_total_usd")
            .and_then(|value| value.as_f64())
        {
            cost.cost_usd = total;
        }

        // The same figures per purpose. Added because the log of one evening showed that 86% of
        // its calls were the harness talking to itself, and nothing at the keyboard could show
        // it: reading that off the JSONL took a script and an hour. What a session spends is the
        // first thing worth knowing about it (2026-09-15).
        let purpose = record
            .get("purpose")
            .and_then(|value| value.as_str())
            .unwrap_or("unlabelled")
            .to_string();
        let entry = cost.per_purpose.entry(purpose).or_default();
        entry.calls += 1;
        entry.prompt_tokens += count_at(&record, "prompt_tokens");
        entry.completion_tokens += count_at(&record, "completion_tokens");
        let latency = count_at(&record, "latency_ms");
        entry.latency_ms += latency;
        entry.slowest_ms = entry.slowest_ms.max(latency);
    }

    Ok((cost.calls > 0).then_some(cost))
}

/// A token count from an agent record, or zero if it is absent — an older agent log simply has
/// no cache split in it, which is not an error.
fn count_at(record: &serde_json::Value, field: &str) -> u64 {
    record
        .get(field)
        .and_then(|value| value.as_u64())
        .unwrap_or(0)
}

/// One session's tokens and money, as the companion recorded them.
#[derive(Default)]
struct SessionCost {
    calls: u64,
    prompt_tokens: u64,
    cache_hit_tokens: u64,
    completion_tokens: u64,
    cost_usd: f64,
    /// The same figures per purpose, so the shape of a session is visible without mining the log.
    per_purpose: std::collections::BTreeMap<String, PurposeCost>,
}

/// What one purpose cost: the model calls the harness made for a reason of its own.
#[derive(Default)]
struct PurposeCost {
    calls: u64,
    prompt_tokens: u64,
    completion_tokens: u64,
    latency_ms: u64,
    slowest_ms: u64,
}

impl PurposeCost {
    fn mean_seconds(&self) -> f64 {
        if self.calls == 0 {
            0.0
        } else {
            self.latency_ms as f64 / self.calls as f64 / 1000.0
        }
    }
}

impl SessionCost {
    /// How much of the prompt came back from the provider's cache: the figure that explains a
    /// bill, and the one worth watching when the context policy changes.
    fn cached_percent(&self) -> u64 {
        if self.prompt_tokens == 0 {
            return 0;
        }
        (self.cache_hit_tokens as f64 * 100.0 / self.prompt_tokens as f64).round() as u64
    }

    /// The busiest purposes, most calls first.
    fn busiest(&self, take: usize) -> Vec<(&str, &PurposeCost)> {
        let mut rows: Vec<(&str, &PurposeCost)> = self
            .per_purpose
            .iter()
            .map(|(name, cost)| (name.as_str(), cost))
            .collect();
        rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls).then_with(|| a.0.cmp(b.0)));
        rows.truncate(take);
        rows
    }

    /// A line for the player.
    ///
    /// The token figures are the durable half: they mean the same thing on every backend. The
    /// money is orientation only — the shipped rates are one provider's and are indicative — so it
    /// is phrased as an approximation rather than a bill.
    ///
    /// Says so plainly when there is no price, rather than reporting zero: a local model really
    /// does cost nothing per token, and a backend nobody has priced is not a misconfiguration
    /// either. "Free" would be the wrong reading of both.
    fn describe(&self) -> String {
        let calls = if self.calls == 1 { "call" } else { "calls" };
        let mut text = format!(
            "this session: {} {calls}, {} prompt tokens ({}% from the cache), {} in reply",
            self.calls,
            self.prompt_tokens,
            self.cached_percent(),
            self.completion_tokens,
        );

        if self.cost_usd > 0.0 {
            text.push_str(&format!(" — about ${:.4}", self.cost_usd));
        } else {
            text.push_str(" — and no price is set for this model, so there is no money to show");
        }

        // Six, because this is a pane and not a report: enough to see where an evening went, and
        // the tail is a list of one-offs. Tokens rather than money, because a price is one
        // provider's and the tokens mean the same thing everywhere.
        let busiest = self.busiest(6);
        if !busiest.is_empty() {
            text.push_str("\nby purpose (busiest first):");
            for (name, cost) in busiest {
                let calls = if cost.calls == 1 { "call" } else { "calls" };
                text.push_str(&format!(
                    "\n  {name}: {} {calls}, {} prompt, {} in reply",
                    cost.calls, cost.prompt_tokens, cost.completion_tokens,
                ));
                if cost.latency_ms > 0 {
                    text.push_str(&format!(
                        ", mean {:.1}s (slowest {:.1}s)",
                        cost.mean_seconds(),
                        cost.slowest_ms as f64 / 1000.0,
                    ));
                }
            }
        }

        text
    }
}

#[cfg(test)]
mod cost_tests {
    use super::*;

    #[test]
    fn a_summary_reports_the_cache_share_and_the_money() {
        let cost = SessionCost {
            calls: 2,
            prompt_tokens: 6730,
            cache_hit_tokens: 6016,
            completion_tokens: 89,
            cost_usd: 0.0123,
            ..Default::default()
        };

        let text = cost.describe();
        assert!(text.contains("2 calls"), "{text}");
        assert!(text.contains("89% from the cache"), "{text}");
        assert!(text.contains("about $0.0123"), "{text}");
    }

    #[test]
    fn the_summary_says_which_purposes_spent_the_session() {
        // The afternoon this was added, the answer for a whole evening was: 86% of the calls were
        // the harness's own bookkeeping, and no pane said so.
        let mut cost = SessionCost::default();
        cost.per_purpose.insert(
            "mission-step".to_string(),
            PurposeCost {
                calls: 3,
                prompt_tokens: 1800,
                completion_tokens: 240,
                latency_ms: 6000,
                slowest_ms: 4200,
            },
        );
        cost.per_purpose.insert(
            "player-line".to_string(),
            PurposeCost {
                calls: 1,
                prompt_tokens: 900,
                completion_tokens: 80,
                ..Default::default()
            },
        );

        let text = cost.describe();
        assert!(text.contains("by purpose"), "{text}");
        // Busiest first, so the errand work leads and the conversation follows it.
        let missions = text.find("mission-step").expect("the errand is listed");
        let conversation = text
            .find("player-line")
            .expect("the conversation is listed");
        assert!(missions < conversation, "{text}");
        assert!(
            text.contains("3 calls, 1800 prompt, 240 in reply"),
            "{text}"
        );
        assert!(text.contains("mean 2.0s (slowest 4.2s)"), "{text}");
        // A purpose with no recorded latency says nothing about latency rather than claiming zero.
        assert!(
            !text.contains("player-line: 1 call, 900 prompt, 80 in reply, mean"),
            "{text}"
        );
    }

    #[test]
    fn no_price_configured_says_so_rather_than_reporting_free() {
        let cost = SessionCost {
            calls: 1,
            prompt_tokens: 100,
            completion_tokens: 5,
            ..Default::default()
        };

        let text = cost.describe();
        assert!(text.contains("no price is set"), "{text}");
        // The tokens are still reported: they are the half that means the same on every backend.
        assert!(text.contains("100 prompt tokens"), "{text}");
    }

    #[test]
    fn one_call_is_not_pluralised() {
        let cost = SessionCost {
            calls: 1,
            ..Default::default()
        };

        assert!(cost.describe().contains("1 call,"), "{}", cost.describe());
    }
}

/// The newest `agent-*.jsonl` in the log directory, if there is one.
fn newest_agent_log(logs_dir: &Path) -> Option<PathBuf> {
    let mut newest: Option<(std::time::SystemTime, PathBuf)> = None;
    for entry in std::fs::read_dir(logs_dir).ok()?.flatten() {
        let path = entry.path();
        let named_for_the_agent = path
            .file_name()
            .map(|name| name.to_string_lossy().starts_with("agent-"))
            .unwrap_or(false);
        let is_jsonl = path
            .extension()
            .map(|extension| extension == "jsonl")
            .unwrap_or(false);
        if !named_for_the_agent || !is_jsonl {
            continue;
        }

        let modified = entry
            .metadata()
            .and_then(|meta| meta.modified())
            .unwrap_or(std::time::SystemTime::UNIX_EPOCH);
        if newest
            .as_ref()
            .map(|(when, _)| modified > *when)
            .unwrap_or(true)
        {
            newest = Some((modified, path));
        }
    }
    newest.map(|(_, path)| path)
}

fn split_verb(rest: &str) -> (&str, &str) {
    let rest = rest.trim();
    match rest.split_once(char::is_whitespace) {
        Some((word, args)) => (word, args.trim()),
        None => (rest, ""),
    }
}

/// Does this line reload the world?
///
/// The interpreter's own `restore`, which is the same word in every story — in any case, and with
/// the save's name after it — so reading it is structural rather than a reading of the story's
/// prose (`02_game_boundary.md` §6.3 (`G33`)). It is asked of every turn the record is about to
/// write, because a reload is a **fork**: the world that was standing is replaced, the interpreter's
/// turn counter rewinds, and rows either side of it share numbers (`05_memory.md` §2 (`M13`)).
///
/// Who typed it decides nothing. The core's own `/restore` reaches the game this way too, and so
/// does a companion that types the word itself — measured 2026-10-02: `RESTORE`, capitalised, at
/// Wishbringer's cliff, which the story honoured and an exact match on the lowercase word missed.
fn is_a_restore_line(line: &str) -> bool {
    line.split_whitespace()
        .next()
        .is_some_and(|word| word.eq_ignore_ascii_case("restore"))
}

/// The line `/bar` prints for the interpreter's own status line (02_game_boundary.md §4.3 (`G23`), §7 (`G38`)).
///
/// Three answers and no fourth, because the two that carry no numbers are different facts and a
/// blank would be a third that says neither. `numbers` is the line the last turn carried, already
/// rendered from the interpreter's own globals; the middle case is the honest limit of the whole
/// channel — a story that keeps its own bar has no counters for the interpreter to state — and the
/// last is the one a zero would be a lie about: there is nothing to read *yet*.
///
/// The story's Z-machine version decides the middle case, and only this side has it: for Versions 1
/// to 3 the interpreter draws the bar and its globals are the numbers, and from Version 4 the story
/// draws its own. Deciding it here rather than shipping the version is deliberate — a reader given
/// only an absent field cannot tell *the story keeps its own bar* from *the interpreter cannot be
/// asked*, and those two answers are not the same sentence.
fn the_status_line(numbers: Option<&str>, story_version: Option<u8>) -> String {
    match numbers {
        Some(numbers) => format!("status line: {numbers}"),
        None if matches!(story_version, Some(version) if version > 3) => {
            "status line: this story draws its own, so there are no numbers to read".to_string()
        }
        None => "status line: nothing has been read yet".to_string(),
    }
}

/// Which game a write-up was asked for, or the reason there is nothing to write up.
///
/// A rule rather than a few lines inside the router, for the reason `ActPolicy` and `TriggerState`
/// are rules: it can be read and tested without a game, a model or a session. The `{who}` in a
/// refusal is the companion's name, which the router knows and this does not.
fn report_request(
    named: &str,
    current: &str,
    agent_online: bool,
    has_played_data: impl Fn(&str) -> bool,
) -> Result<String, String> {
    if !agent_online {
        return Err(
            "{who} is not here, so nothing can be written up — the write-up is its work, not mine. \
             The panes, the saves and the record are all still there."
                .to_string(),
        );
    }

    let stem = if named.is_empty() { current } else { named };
    if stem.is_empty() {
        return Err(
            "No game is loaded, so there is nothing to write up. `/games` lists them.".to_string(),
        );
    }

    if !has_played_data(stem) {
        return Err(format!(
            "There is nothing written down for `{stem}` — a write-up of a game nobody has played \
             would be a page of nothing. `/games` lists the games, and `/turns` shows what has been \
             watched."
        ));
    }

    Ok(stem.to_string())
}

/// The `/games` listing: stem, detected version and size, with the game being
/// played marked.
fn format_game_list(games: &[GameInfo], current: &str) -> String {
    if games.is_empty() {
        return "no games are mounted.".to_string();
    }
    let mut text = String::from("games available\n");
    for game in games {
        let marker = if game.stem == current { "*" } else { " " };
        text.push_str(&format!(
            "  {marker} {}  ({}, {:.0} KiB)\n",
            game.stem,
            game.version_label(),
            game.size_bytes as f64 / 1024.0
        ));
    }
    text.push_str("  /switch <name> to play one\n");
    text
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn a_restore_line_is_recognised_in_any_case_and_with_any_argument() {
        // 05_memory.md §2 (`M13.5`). Measured 2026-10-02: the companion typed `RESTORE` at
        // Wishbringer's cliff, the story reloaded the world and left it in the fog, and the exact
        // lowercase match missed it — so the turn carried no fork stamp, and the fold downstream
        // read a reload as a move between two rooms.
        assert!(is_a_restore_line("restore"));
        assert!(is_a_restore_line("RESTORE"));
        assert!(is_a_restore_line("Restore"));
        assert!(is_a_restore_line("  restore  "));
        assert!(is_a_restore_line("restore 12-lost"));
        assert!(is_a_restore_line("RESTORE WISHBRINGER"));

        // And nothing else is: the word has to be the line's own first word, because a game that
        // has a verb of its own by that name must be able to see it.
        assert!(!is_a_restore_line(""));
        assert!(!is_a_restore_line("   "));
        assert!(!is_a_restore_line("north"));
        assert!(!is_a_restore_line("restor"));
        assert!(!is_a_restore_line("restored"));
        assert!(!is_a_restore_line("say restore"));
    }

    #[test]
    fn list_is_another_way_to_say_saves() {
        // The 2026-09-20 UAT asked for a `/list` and the session already had `/saves`: two names for
        // one answer, because the word a player reaches for is the word they already know
        // (`/saves` is this project's invention, and it was not the one anybody typed under
        // pressure). Pinned so the spelling cannot quietly go away again.
        assert_eq!(Verb::parse("list"), Some(Verb::Saves));
        assert_eq!(Verb::parse("LIST"), Some(Verb::Saves));
        assert_eq!(Verb::parse("saves"), Some(Verb::Saves));
    }

    fn temp_state(name: &str) -> StatePaths {
        let dir = std::env::temp_dir().join(format!("infocompanion-core-{name}"));
        let _ = fs::remove_dir_all(&dir);
        StatePaths::new(dir)
    }

    fn game(stem: &str) -> GameInfo {
        GameInfo {
            stem: stem.to_string(),
            path: PathBuf::from(format!("/games/{stem}.z5")),
            version: Some(5),
            size_bytes: 100,
        }
    }

    /// The tests all boot from the same directory.
    fn choose(games: &[GameInfo], requested: Option<&str>, state: &StatePaths) -> Result<GameInfo> {
        choose_initial_game(games, requested, state, std::path::Path::new("/games"))
    }

    #[test]
    fn verbs_are_recognised_and_typos_are_not() {
        assert_eq!(Verb::parse("HELP"), Some(Verb::Help));
        assert_eq!(Verb::parse("?"), Some(Verb::Help));
        assert_eq!(Verb::parse("switch"), Some(Verb::Switch));
        // A bare direction is not a core verb: it needs `/type north` (issue #21),
        // because an unrecognised slash word is refused rather than forwarded now.
        assert_eq!(Verb::parse("north"), None);
        assert_eq!(Verb::parse("type"), Some(Verb::Type));
        // There is no `undo`, on purpose — see the comment on the verb list. This assertion is
        // here so that re-adding one has to be a deliberate act rather than a quiet merge.
        assert_eq!(Verb::parse("undo"), None);
        assert_eq!(Verb::parse("cost"), Some(Verb::Cost));
        // A game verb that looks like a command must not be swallowed.
        assert_eq!(Verb::parse("reset"), Some(Verb::Reset));
    }

    #[test]
    fn split_verb_handles_whitespace_and_empty_args() {
        assert_eq!(split_verb("switch advent"), ("switch", "advent"));
        assert_eq!(split_verb("switch   advent  "), ("switch", "advent"));
        assert_eq!(split_verb("save"), ("save", ""));
        assert_eq!(split_verb(" switch  "), ("switch", ""));
    }

    #[test]
    fn the_status_line_says_the_numbers_or_why_there_are_none() {
        // The three answers, and the reason this is a function rather than a `format!` at the call
        // site: the middle case cannot be reached from the numbers, because a missing number is the
        // same field whether the story draws its own bar or the interpreter cannot be asked at all
        // (02_game_boundary.md §4.3 (`G23`), §7 (`G38`)). The numbers arrive already rendered by the turn; the version is
        // what this side knows and a reader of the turn does not.
        assert_eq!(
            the_status_line(Some("at object 12 — score 0, moves 1"), Some(3)),
            "status line: at object 12 — score 0, moves 1"
        );
        assert_eq!(
            the_status_line(None, Some(5)),
            "status line: this story draws its own, so there are no numbers to read"
        );
        // Nothing read yet, whatever the version: a Version 3 story whose interpreter has no
        // channel reports nothing either, and neither case is a zero.
        assert_eq!(
            the_status_line(None, Some(3)),
            "status line: nothing has been read yet"
        );
        assert_eq!(
            the_status_line(None, None),
            "status line: nothing has been read yet"
        );
    }

    #[test]
    fn the_peephole_verbs_are_recognised() {
        assert_eq!(Verb::parse("plan"), Some(Verb::Plan));
        assert_eq!(Verb::parse("notes"), Some(Verb::Notes));
        assert_eq!(Verb::parse("notebook"), Some(Verb::Notes));
        assert_eq!(Verb::parse("think"), Some(Verb::Think));
        assert_eq!(Verb::parse("PLAN"), Some(Verb::Plan));
        assert_eq!(Verb::parse("TYPE"), Some(Verb::Type));
        // `/type` and `/direct` are different things and must not collapse into one: the
        // first is a request to the companion, the second is the player's own keystroke.
        assert_eq!(Verb::parse("direct"), Some(Verb::Direct));
        assert_eq!(Verb::parse("DIRECT"), Some(Verb::Direct));
        assert_ne!(Verb::parse("type"), Verb::parse("direct"));
    }

    #[test]
    fn the_reasoning_can_be_switched_off_in_the_panes() {
        assert_eq!(Verb::parse("thinkon"), Some(Verb::ThinkOn));
        assert_eq!(Verb::parse("tagsoff"), Some(Verb::TagsOff));
        assert_eq!(Verb::parse("TAGSON"), Some(Verb::TagsOn));
        assert_eq!(Verb::parse("thinkoff"), Some(Verb::ThinkOff));
        assert_eq!(Verb::parse("THINKOFF"), Some(Verb::ThinkOff));
        // Reading the last one out of the log is a different verb, and stays.
        assert_eq!(Verb::parse("think"), Some(Verb::Think));
        assert_ne!(Verb::parse("think"), Verb::parse("thinkoff"));
        assert_eq!(Verb::parse("debugon"), Some(Verb::DebugOn));
        assert_eq!(Verb::parse("DEBUGOFF"), Some(Verb::DebugOff));
        assert_eq!(Verb::parse("debug"), Some(Verb::Debug));
        assert_ne!(Verb::parse("debug"), Verb::parse("debugoff"));
        // The neighbours in the verb list are easy to lose while editing it, so `/turns` —
        // which the toggle was added next to — is pinned here as well.
        assert_eq!(Verb::parse("turns"), Some(Verb::Turns));
        assert_eq!(Verb::parse("meta"), Some(Verb::Meta));
    }

    #[test]
    fn the_verbs_a_page_restores_its_settings_with_are_settings() {
        // Exactly `restoreCommands` in `Source/WebUI/public/app.js` (07_webui.md §5 (`W18`–`W25`)): both ends of each
        // switch, and the pace. This is what `is_a_setting` is for — the page's commands arrive
        // as ordinary player lines, and the conversation record has to tell them from the
        // player's words or it replays the harness's own chatter (03_session.md §4 (`S25`–`S29`)). A switch
        // added to the page and not here would quietly start writing into that record.
        for word in [
            "tagson",
            "tagsoff",
            "debugon",
            "debugoff",
            "thinkon",
            "thinkoff",
            "veryslow",
            "slow",
            "medium",
            "fast",
            "veryfast",
            "risklow",
            "riskmedium",
            "riskhigh",
        ] {
            let verb = Verb::parse(word).unwrap_or_else(|| panic!("`{word}` is not a verb at all"));
            assert!(verb.is_a_setting(), "`{word}` is a setting");
        }

        // And the ones that only look like them. A bare report changes nothing, so it is
        // something asked rather than a setting put back, and it stays in the conversation.
        for word in [
            "speed", "debug", "think", "turns", "status", "bar", "cost", "risk",
        ] {
            let verb = Verb::parse(word).unwrap_or_else(|| panic!("`{word}` is not a verb at all"));
            assert!(!verb.is_a_setting(), "`{word}` changes nothing");
        }
    }

    #[test]
    fn the_standing_danger_level_has_its_own_verbs() {
        // §4.7: four verbs, the shortest spelling a person would actually type, and `/risk`
        // alone is the question rather than a setter.
        assert_eq!(Verb::parse("risklow"), Some(Verb::RiskLow));
        assert_eq!(Verb::parse("riskmedium"), Some(Verb::RiskMedium));
        assert_eq!(Verb::parse("riskhigh"), Some(Verb::RiskHigh));
        assert_eq!(Verb::parse("risk"), Some(Verb::Risk));
        assert_eq!(Verb::parse("RISKHIGH"), Some(Verb::RiskHigh));
        assert_ne!(Verb::parse("risk"), Verb::parse("riskhigh"));
    }

    #[test]
    fn the_player_can_answer_a_prompt_that_is_not_a_command() {
        // The player's half of `press_key`. A story can open by asking for a keystroke
        // (Bureaucracy's `[Press any key to begin.]`), and the pane is the keyboard for it.
        assert_eq!(Verb::parse("pressreturn"), Some(Verb::PressReturn));
        assert_eq!(Verb::parse("presskey"), Some(Verb::PressKey));
        assert_eq!(Verb::parse("PRESSRETURN"), Some(Verb::PressReturn));
        assert_ne!(Verb::parse("pressreturn"), Verb::parse("presskey"));
        // A game verb that begins the same way must not be swallowed.
        assert_eq!(Verb::parse("press"), None);
        // The neighbours in the verb list, pinned: the whole reason this test exists is that
        // adding a verb beside these is how `/turns` was once lost.
        assert_eq!(Verb::parse("type"), Some(Verb::Type));
        assert_eq!(Verb::parse("direct"), Some(Verb::Direct));
        assert_eq!(Verb::parse("cost"), Some(Verb::Cost));
        // The write-up (03_session.md §8 (`S46`–`S57`)), with and without a game named after it.
        assert_eq!(Verb::parse("catchup"), Some(Verb::Catchup));
        assert_eq!(Verb::parse("CATCHUP"), Some(Verb::Catchup));
        assert_eq!(split_verb("catchup"), ("catchup", ""));
        assert_eq!(split_verb("catchup advent"), ("catchup", "advent"));
        // And `/pressreturn` with nothing after it is a bare Return, which `split_verb` hands
        // over as an empty argument rather than as a space.
        assert_eq!(split_verb("pressreturn"), ("pressreturn", ""));
        assert_eq!(split_verb("presskey 2"), ("presskey", "2"));
        assert_eq!(split_verb("presskey ynn"), ("presskey", "ynn"));
    }

    #[test]
    fn a_write_up_is_asked_for_only_for_a_game_with_something_written_down() {
        // With a game named, that is the game — whatever is loaded.
        assert_eq!(
            report_request("witness", "advent", true, |_| true).unwrap(),
            "witness"
        );
        // With nothing named, whatever is loaded: a pane's `/catchup` is about the game in front
        // of the player.
        assert_eq!(
            report_request("", "advent", true, |_| true).unwrap(),
            "advent"
        );

        // Nothing written down: refused, and told where the list is.
        let refusal = report_request("grue", "advent", true, |_| false).unwrap_err();
        assert!(refusal.contains("nothing written down"), "{refusal}");
        assert!(refusal.contains("/games"), "{refusal}");

        // Nobody to do the work: refused for that reason instead, because a report is the
        // companion's and there is no fallback the core could write itself. `{who}` is the name the
        // router substitutes.
        let refusal = report_request("advent", "advent", false, |_| true).unwrap_err();
        assert!(refusal.contains("{who}"), "{refusal}");
        assert!(!refusal.contains("nothing written down"), "{refusal}");

        // No game loaded and none named: nothing to write up, and no model call spent finding out.
        let refusal = report_request("", "", true, |_| true).unwrap_err();
        assert!(refusal.contains("No game is loaded"), "{refusal}");
    }

    #[test]
    fn the_last_played_game_beats_the_configured_one() {
        let state = temp_state("last-played-wins");
        games::write_current(state.root(), "wishbringer").unwrap();
        let games = vec![game("advent"), game("wishbringer"), game("witness")];

        // Issue #34: the configured game used to win, so a stale `INFOCOMPANION_GAME` in
        // `.env` sent every restart to that game and last night's progress was dropped.
        let chosen = choose(&games, Some("witness"), &state).unwrap();
        assert_eq!(chosen.stem, "wishbringer");
    }

    #[test]
    fn the_configured_game_is_used_when_there_is_nothing_to_resume() {
        let state = temp_state("configured-when-nothing-to-resume");
        let games = vec![game("advent"), game("witness")];

        // A fresh state directory: the configuration is what boots.
        assert_eq!(
            choose(&games, Some("witness"), &state).unwrap().stem,
            "witness"
        );
        // Nothing configured either: the first discovered game.
        assert_eq!(choose(&games, None, &state).unwrap().stem, "advent");
    }

    #[test]
    fn a_pointer_to_a_game_that_is_gone_falls_back_rather_than_failing() {
        let state = temp_state("stale-pointer");
        games::write_current(state.root(), "deleted-last-week").unwrap();
        let games = vec![game("advent")];
        assert_eq!(
            choose(&games, Some("advent"), &state).unwrap().stem,
            "advent"
        );
    }

    #[test]
    fn no_games_is_an_error_that_says_what_to_do() {
        let state = temp_state("no-games");
        let err = choose(&[], Some("advent"), &state).unwrap_err().to_string();
        assert!(err.contains("no story files found"), "{err}");
        assert!(err.contains("/games"), "{err}");
        // It names a story file by its extension, because "mount one" is useless advice to
        // somebody who does not know what to mount. It deliberately says nothing about
        // restarting: under compose the container is retried on its own (the loop is the
        // symptom the instructions list), and under `docker run` the operator starts it.
        assert!(
            err.contains(".z5"),
            "the message should name what a story file looks like: {err}"
        );
        assert!(
            !err.contains("restart"),
            "the daemon does not know how it was started, so it cannot tell anyone to restart it: {err}"
        );
    }

    #[test]
    fn the_plan_is_read_out_of_the_notebook() {
        let notebook = "# advent\n\n## Current goals and plan\n\n- sweep the open exits\n- then decide\n\n## Map notes\n\nvalley -> forest (east)\n";

        let plan = plan_section(notebook).expect("the section is there");

        assert_eq!(plan, "- sweep the open exits\n- then decide");
        // The next section is not part of the plan.
        assert!(!plan.contains("valley"));
    }

    #[test]
    fn the_canonical_plan_heading_beats_an_earlier_goal_section() {
        // Measured live on 2026-09-16: the notebook's own "## Cat errand — the goal"
        // shadowed "## Current goals and plan", so `/plan` showed a frozen errand while
        // the arcade cabinet was the live work.
        let notebook = "# wishbringer\n\n## Cat errand — the goal\n\n- find Chaos\n\n## Current goals and plan\n\n- probe the arcade cabinet\n";

        assert_eq!(
            plan_section(notebook),
            Some("- probe the arcade cabinet".to_string())
        );
    }

    #[test]
    fn a_notebook_with_an_empty_plan_says_so_rather_than_printing_nothing() {
        let notebook = "# advent\n\n## Current goals and plan\n\n## Map notes\n\nvalley\n";
        assert_eq!(plan_section(notebook), None);

        // And a notebook with no such section at all is equally empty.
        assert_eq!(
            plan_section("# advent\n\n## Key facts\n- the troll wants treasure\n"),
            None
        );
    }

    #[test]
    fn the_last_thought_is_the_newest_one_logged() {
        let dir = std::env::temp_dir().join(format!("ic-think-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let log = dir.join("agent-20260911T000000Z.jsonl");
        std::fs::write(
            &log,
            concat!(
                "{\"kind\":\"turn\",\"input\":\"look\"}\n",
                "{\"kind\":\"companion_think\",\"text\":\"the stream is the only thing with a direction\"}\n",
                "{\"kind\":\"companion_say\",\"text\":\"going east\"}\n",
                "{\"kind\":\"companion_think\",\"text\":\"east was a wall, so try north\"}\n"
            ),
        )
        .unwrap();

        let thought = latest_thought(&dir).unwrap().expect("a thought is logged");
        assert_eq!(thought, "east was a wall, so try north");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn no_thought_logged_is_not_an_error() {
        let dir = std::env::temp_dir().join(format!("ic-nothink-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();

        // Nothing at all, and then a log with no reasoning in it.
        assert_eq!(latest_thought(&dir).unwrap(), None);
        std::fs::write(dir.join("agent-x.jsonl"), "{\"kind\":\"turn\"}\n").unwrap();
        assert_eq!(latest_thought(&dir).unwrap(), None);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn boots_the_configured_game_when_asked() {
        let state = temp_state("configured");
        let games = vec![game("advent"), game("witness")];
        let chosen = choose(&games, Some("witness"), &state).unwrap();
        assert_eq!(chosen.stem, "witness");
    }

    #[test]
    fn prefers_the_last_played_game_over_the_first_discovered() {
        let state = temp_state("last-played");
        state.ensure().unwrap();
        games::write_current(state.root(), "witness").unwrap();
        let games = vec![game("advent"), game("witness")];
        let chosen = choose(&games, None, &state).unwrap();
        assert_eq!(chosen.stem, "witness");
    }

    #[test]
    fn falls_back_when_the_last_played_game_is_gone() {
        let state = temp_state("last-missing");
        state.ensure().unwrap();
        games::write_current(state.root(), "deleted-game").unwrap();
        let games = vec![game("advent")];
        let chosen = choose(&games, None, &state).unwrap();
        assert_eq!(chosen.stem, "advent");
    }

    #[test]
    fn boots_the_first_discovered_game_with_no_hints() {
        let state = temp_state("first");
        let games = vec![game("advent"), game("witness")];
        let chosen = choose(&games, None, &state).unwrap();
        assert_eq!(chosen.stem, "advent");
    }

    #[test]
    fn game_listing_marks_the_game_being_played() {
        let games = vec![game("advent"), game("witness")];
        let text = format_game_list(&games, "witness");
        assert!(text.contains("  * witness"), "{text}");
        assert!(text.contains("    advent"), "{text}");
        assert!(text.contains("Z-code v5"), "{text}");
        assert_eq!(format_game_list(&[], "advent"), "no games are mounted.");
    }

    #[test]
    fn refuses_to_start_with_no_games_with_a_useful_message() {
        let state = temp_state("no-games");
        let err = choose(&[], None, &state).unwrap_err().to_string();
        assert!(err.contains("no story files found"), "{err}");
        assert!(
            err.contains("/games"),
            "the message names the directory: {err}"
        );
    }
}
