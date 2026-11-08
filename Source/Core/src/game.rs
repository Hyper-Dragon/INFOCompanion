//! The game actor: the single owner of the frotz process.
//!
//! Everything that talks to frotz goes through this task, which is what makes the
//! core's guarantees cheap to hold:
//!
//! * **Line-level atomicity (§2.5, §6.6).** One owner, one writer: a line is
//!   written as a unit and no two seats can interleave partial commands.
//! * **One input per turn.** Inputs are *gated*: a queued line is only written
//!   when frotz is idle at its prompt. So `turn_complete.input` is unambiguous —
//!   it is the line that caused the output that followed, never a guess.
//! * **Scripted control ops.** Auto-save, `/switch` and `/reset` need to drive
//!   frotz through its own interactive prompts. Those sequences run through the
//!   same owner, and their output is suppressed from the player and the agent
//!   (§5.4): machinery is not gameplay.
//!
//! The actor is also where auto-save lives, rather than in the core, because it is
//! the only place that can serialise "save the game" against "the game is mid-turn".

use crate::config::GameTuning;
use crate::frotz::{Frotz, FrotzConfig, FrotzEvent};
use crate::games::{self, GameInfo};
use crate::observations;
use crate::runs;
use crate::save::{self, StatePaths};
use crate::seat_protocol::Turn;
use crate::stuck;
use crate::tool::{self, Event as ToolEvent, InputKind};
use crate::turn_framer::{buffer_ends_with_prompt, EndReason, FramedTurn, TurnFramer};
use anyhow::{anyhow, Context, Result};
use std::collections::VecDeque;
use std::path::PathBuf;
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot};
use tokio::time::{interval, Interval, MissedTickBehavior};
use tracing::{debug, error, info, trace, warn};

/// Where an input line came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputSource {
    /// The co-play agent decided to act.
    Agent,
    /// The player typed a bare command (agent-less or direct mode).
    Player,
    /// The player forced one command with `/command`.
    Forced,
    /// The core itself (restore refresh, scripted sequences).
    Scripted,
}

impl InputSource {
    pub fn label(self) -> &'static str {
        match self {
            InputSource::Agent => "agent",
            InputSource::Player => "player",
            InputSource::Forced => "forced",
            InputSource::Scripted => "scripted",
        }
    }
}

/// The currently loaded game.
#[derive(Debug, Clone, Default)]
pub struct GameStatus {
    /// The game's stem, e.g. `advent`.
    pub stem: String,
    /// Title learned from the game's own startup text, once we have seen it.
    pub title: Option<String>,
    /// Z-machine version.
    pub version: Option<u8>,
    /// Did this boot restore a checkpoint?
    pub restored: bool,
}

/// Why a checkpoint is being taken, for the log and the `system` notice.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SaveReason {
    /// Every N turns (§5.4).
    Auto,
    /// The player asked with `/save`.
    Manual,
    /// The player is switching away (§5.3).
    Switch,
    /// The daemon is shutting down (§5.4).
    Shutdown,
}

impl SaveReason {
    pub fn label(self) -> &'static str {
        match self {
            SaveReason::Auto => "auto",
            SaveReason::Manual => "manual",
            SaveReason::Switch => "switch",
            SaveReason::Shutdown => "shutdown",
        }
    }
}

/// Requests into the game actor.
pub enum GameRequest {
    /// Queue a line; it is written when frotz is ready.
    Input { line: String, source: InputSource },
    /// Take a checkpoint of the current game.
    ///
    /// `slot` is `None` for the game's own checkpoint, which is what the autosave and `/save`
    /// with no name write. `Some(name)` writes the game's own checkpoint *to that name* and then
    /// refreshes the checkpoint from it (§5.3).
    Checkpoint {
        reason: SaveReason,
        reply: oneshot::Sender<Result<()>>,
        slot: Option<String>,
    },
    /// Bring the world back from a save (§5.3, §5.4).
    ///
    /// `slot` is `None` for the game's own checkpoint, else the name of one the player made.
    /// Deliberately not the same thing as a switch: the story stays the same and the attempt
    /// stays the same, and what changes is which branch of the world is standing.
    Restore {
        slot: Option<String>,
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    /// Checkpoint, then boot a different game and restore its own checkpoint.
    Switch {
        game: GameInfo,
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    /// Full reset: delete this game's progress and boot it from scratch.
    Reset {
        confirm_stem: String,
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    /// Start this game again from the top, keeping everything the player has learned about
    /// it — the ordinary way a story gets replayed (issue #26).
    ///
    /// The difference from `Reset` is one file: the checkpoint goes, the notebook stays.
    Restart {
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    /// Shut the actor down, checkpointing first if there is progress to keep.
    Shutdown { reply: oneshot::Sender<()> },
}

/// Events out of the game actor.
#[derive(Debug)]
pub enum GameEvent {
    /// Raw frotz bytes, for terminal seats: passed through untouched.
    ///
    /// `machinery` marks output frotz produced while the core was driving it through its
    /// own prompts — a checkpoint's `save`, a `/switch`, a `/reset`. That output belongs on
    /// the screen, because it is part of what happened on that terminal (issue #20), but
    /// not in the agent's transcript, which is gameplay (§5.4).
    Raw { bytes: Vec<u8>, machinery: bool },
    /// A framed turn (§7).
    Turn(Turn),
    /// An input line was written to frotz. Lets the core keep companion chatter
    /// out of the middle of a turn (§12) and log the true order of play.
    InputDispatched { line: String, source: InputSource },
    /// A session event worth telling everyone about.
    System { event: String, detail: String },
    /// The loaded game changed (boot, switch, reset).
    GameChanged(GameStatus),
    /// frotz exited.
    Exited(Option<i32>),
}

/// Actor configuration.
#[derive(Debug, Clone)]
pub struct GameConfig {
    /// The interpreter to run.
    pub dfrotz: PathBuf,
    /// Where checkpoints and notebooks live.
    pub state: StatePaths,
    /// Quiescence window for turn framing (§7).
    pub turn_timeout: Duration,
    /// Auto-save cadence in command turns; `0` disables it.
    pub autosave_turns: u64,
    /// How many queued input lines to tolerate before refusing more.
    pub max_queue: usize,
    /// How long one scripted step may take.
    pub script_timeout: Duration,
    /// How often the actor checks for output quiescence and script timeouts.
    ///
    /// Everything below that was a constant in this module is here instead, because it is a
    /// cadence and a cadence is configuration (10_delivery.md §2 (`D5`)).
    pub tick: Duration,
    /// How long the screen bytes a tool-channel prompt promised are waited for.
    ///
    /// The interpreter says how many bytes it has written, so a turn is closed as soon as that
    /// many have arrived rather than when the output goes quiet. This is the safety net for the
    /// case where the two counts disagree — a diagnostic that got mixed into the stream, or an
    /// interpreter whose accounting is not complete — and it is deliberately short: the point of
    /// the channel is to stop waiting for silence, and a fallback that waits half a second would
    /// give that up.
    pub tool_turn_deadline: Duration,
    /// How many turn completions a checkpoint waits for the game to come back to its own `>`
    /// prompt before giving up (§5.4).
    ///
    /// Autosaves are dropped quietly at this point and fire again on the next turn; an explicit
    /// `/save` is told why, because someone is waiting for an answer.
    pub max_deferred_checkpoint_turns: u32,
    /// How long frotz's polite exit (stdin EOF) is given before it is killed.
    pub exit_grace: Duration,
    /// How long a bare `>` is given to turn out to be the start of a longer line.
    pub prompt_settle: Duration,
    /// Ask the interpreter for the tool channel (`-T`), when it has said it understands it.
    ///
    /// Off means the game boundary behaves exactly as it always has: the prompt heuristic, the
    /// quiescence fallback, and a save driven through the interpreter's filename prompt.
    pub tools: bool,
}

impl GameConfig {
    pub fn new(
        state: StatePaths,
        dfrotz: PathBuf,
        turn_timeout: Duration,
        autosave_turns: u64,
        tuning: GameTuning,
    ) -> Self {
        Self {
            dfrotz,
            state,
            turn_timeout,
            autosave_turns,
            max_queue: tuning.max_queued_inputs,
            script_timeout: tuning.script_timeout,
            tick: tuning.tick,
            tool_turn_deadline: tuning.tool_turn_deadline,
            max_deferred_checkpoint_turns: tuning.max_deferred_checkpoint_turns,
            exit_grace: tuning.exit_grace,
            prompt_settle: tuning.prompt_settle,
            tools: false,
        }
    }
}

/// The handles a caller keeps.
pub struct GameHandle {
    /// Requests into the actor.
    pub requests: mpsc::Sender<GameRequest>,
    /// Events out of the actor.
    pub events: mpsc::Receiver<GameEvent>,
    /// The game that came up at boot.
    pub status: GameStatus,
}

/// Boot the actor and the initial game.
pub async fn spawn(cfg: GameConfig, initial: GameInfo) -> Result<GameHandle> {
    let (requests, req_rx) = mpsc::channel(64);
    let (evt_tx, events) = mpsc::channel(256);

    // Built before the config is moved into the actor, and with its two windows from that config:
    // how long silence means the turn is over, and how long a bare `>` is given to prove it is not
    // the start of a longer line. This is the only place a framer is built for the actor, so the
    // `400ms` that used to be written here as well as in `main` is gone: it was overwritten a line
    // later, and a number that looks like a setting but is not is worse than no number at all.
    let framer = TurnFramer::new(cfg.turn_timeout).settled(cfg.prompt_settle);

    let mut actor = Actor {
        cfg,
        req_rx,
        evt_tx,
        frotz: None,
        events_rx: None,
        framer,
        game: initial.clone(),
        status: GameStatus::default(),
        ready: false,
        queue: VecDeque::new(),
        pending: VecDeque::new(),
        script: None,
        since_save: 0,
        auto_checkpoint_due: false,
        turn_counter: 0,
        last_turn_at_prompt: false,
        awaiting_answer: false,
        detector: stuck::Detector::new(),
        deferred_checkpoints: VecDeque::new(),
        current_source: InputSource::Scripted,
        wrote_at: None,
        progress_made: false,
        done: false,
        shutdown_reply: None,
        boot_restore: None,
        tools: Tools::default(),
    };

    let status = actor
        .start_game(&initial, WhereToRestore::Canonical)
        .await
        .with_context(|| format!("failed to start `{}`", initial.stem))?;

    tokio::spawn(actor.run());

    Ok(GameHandle {
        requests,
        events,
        status,
    })
}

// ---------------------------------------------------------------------------
// Scripted control sequences
// ---------------------------------------------------------------------------

/// What a scripted stage waits for before the *next* stage runs.
#[derive(Debug, Clone, Copy)]
enum Expect {
    /// A substring anywhere in the sequence's output so far, in the *interpreter's* words.
    ///
    /// There is deliberately no variant for "any of several wordings": that was used for the
    /// game's own save-success line, and a list of spellings is a per-game table. Success is
    /// the file on disk instead (`complete_script`).
    Contains(&'static str),
    /// frotz's `>` prompt: it is ready for input again.
    Prompt,
    /// The tool channel accepted this command (03_session.md §9–§10 (`S58`–`S76`)).
    ///
    /// The one expectation that is not a reading of the interpreter's output at all: it is the
    /// interpreter saying "done", which is why the tool-driven sequence needs no marker to
    /// recognise and no wording list to maintain.
    Command(&'static str),
}

/// One step of a scripted sequence: `expect` gates `send`.
///
/// The first stage has no expectation, so it runs as soon as the sequence starts.
#[derive(Debug, Clone)]
struct Stage {
    expect: Option<Expect>,
    send: Vec<String>,
}

/// What to do once a checkpoint sequence completes.
enum AfterScript {
    /// Nothing more — the game stays where it is.
    Nothing(SaveReason),
    /// Boot a different game.
    Switch(Box<GameInfo>, oneshot::Sender<Result<GameStatus>>),
    /// Finish shutting down.
    Shutdown,
}

/// A scripted sequence in flight.
struct Script {
    /// Human label for logs and notices.
    label: String,
    stages: Vec<Stage>,
    /// Index of the stage being waited on.
    cursor: usize,
    /// Everything frotz has printed since the sequence began.
    buf: Vec<u8>,
    /// Substrings that mean the sequence has failed.
    fail_markers: Vec<&'static str>,
    /// Per-stage deadline.
    deadline: Instant,
    /// Where the checkpoint file is being staged (for the checkpoint sequence).
    staged_path: Option<PathBuf>,
    /// Where it should end up.
    final_path: Option<PathBuf>,
    /// What happens next.
    after: AfterScript,
    /// Reply slot for the failure path (the success path lives in `after`).
    fail_reply: Option<oneshot::Sender<Result<()>>>,
    /// Every line has been sent and the interpreter has not yet reported the save.
    ///
    /// The tool-driven sequence ends in the interpreter's own `saved` frame rather than in a
    /// prompt: the artifact decides, and it decides in a fact rather than in a spelling.
    awaiting_saved: bool,
    /// The last tool command the interpreter accepted, for `Expect::Command`.
    last_command: Option<String>,
    /// Is this sequence driving a save through the tool channel?
    tool_save: bool,
    /// Why the save is being taken, for the record that goes beside it.
    reason_label: String,
    /// The name the player gave this save, when they gave one (§5.3).
    ///
    /// The interpreter is pointed at `saves/<stem>/<name>.sav` for this sequence, so the file the
    /// player named is the file that is written — and therefore the file the *screen* names, which
    /// is where a save becomes visible at all. The game's own checkpoint is refreshed from it
    /// afterwards, which is the same single write path with the copy in the other direction: there
    /// is one sequence, and the checkpoint is what it produced under the name the core uses.
    slot: Option<String>,
}

/// Where a game's world comes back from, when it starts.
#[derive(Debug, Clone)]
enum WhereToRestore {
    /// Wherever the player actually was: the newest save this game has, checkpoint or named.
    /// What boot and `/switch` do.
    Canonical,
    /// Start at the opening text, whatever is on disk. What `/restart` and `/reset` do.
    Fresh,
    /// This exact file — a save the player named (§5.3).
    Slot(PathBuf),
}

/// Where the player left off: the game's own checkpoint, or a save they named, whichever is newer
/// (03_session.md §10 (`S67`)).
///
/// **Why not just the checkpoint.** The checkpoint is written every `autosave_turns` turns, so
/// booting from it alone lands up to five turns behind — and a player who has just typed
/// `/save 10-conch2` at turn 200 does not want their evening reopened at 195. It is also the save
/// they chose *on purpose*, which is the whole point of naming one.
///
/// **Why the clock and not the turn number.** A turn count is the story's, and it goes *backwards*
/// on purpose — restoring an earlier save is an undo the player meant — so the higher turn is not
/// the later position in the evening. `saved_at` is when the file was written, which is the only
/// question being asked. Both stamps come from `rfc3339_from_unix`, so they compare as text
/// (`an_earlier_stamp`), and a record that has been hand-edited into some other shape counts as
/// *cannot say*, never as a wrong answer.
///
/// Ties, save files with no record beside them, and the case where nothing is provably newer all go
/// to the checkpoint: it is the file this harness maintains, and guessing between two files that
/// cannot say when they were taken is not this function's business.
fn the_newest_save(paths: &save::StatePaths, stem: &str) -> Option<PathBuf> {
    let checkpoint = paths.checkpoint(stem);
    if checkpoint.exists() && save::read_record_beside(&checkpoint).is_none() {
        // A checkpoint with no note is still a position, and no named save can be shown to be newer
        // than an unknown time — so it wins on the same rule as a tie, and it is the file that gets
        // a record written beside it at the next autosave anyway.
        return Some(checkpoint);
    }

    let mut newest: Option<(String, PathBuf)> =
        save::read_record_beside(&checkpoint).map(|record| (record.saved_at, checkpoint.clone()));

    for slot in save::slots(paths, stem)
        .into_iter()
        .filter(|slot| !slot.canonical)
    {
        let Some(record) = slot.record else { continue };
        let candidate = paths.named_checkpoint(stem, &slot.name);
        let better = match &newest {
            Some((when, _)) => crate::seat_protocol::an_earlier_stamp(when, &record.saved_at),
            None => true,
        };
        if better {
            newest = Some((record.saved_at, candidate));
        }
    }

    newest.map(|(_, path)| path)
}

/// A parked request, waiting for the game to be ready.
enum Op {
    Checkpoint {
        reason: SaveReason,
        reply: Option<oneshot::Sender<Result<()>>>,
        slot: Option<String>,
    },
    Restore {
        slot: Option<String>,
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    Switch {
        game: Box<GameInfo>,
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    Reset {
        confirm_stem: String,
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    /// Start this game again from the top, keeping its notebook (issue #26).
    Restart {
        reply: oneshot::Sender<Result<GameStatus>>,
    },
    /// Re-print the scene after a restore: frotz restores silently, so the player
    /// (and the agent) would otherwise see a bare prompt with no room description.
    Refresh,
}

/// A checkpoint that had to wait because frotz was not at its command prompt.
///
/// Only a checkpoint is ever parked here: a `save` is the one scripted sequence
/// whose first line frotz would happily mistake for an answer to a question.
struct DeferredSave {
    op: Op,
    /// Turn completions spent waiting for the prompt to come back.
    turns_waited: u32,
}

/// Phrases frotz uses when the line it wants next is *yes or no*, not a command.
///
/// Measured on dfrotz 2.54: `quit` prints `Are you sure you want to quit? ` and
/// waits for an answer, and any other answer gets `Please answer yes or no.`
/// before the prompt returns. While one of these is outstanding the next line
/// written is eaten as the answer — which is exactly how an auto-checkpoint's
/// `save` was consumed and its sequence timed out.
const CONFIRMATION_MARKERS: [&str; 3] = [
    "are you sure you want to quit",
    "please answer yes or no",
    "do you really want to",
];

/// Does this turn's output look like frotz asking a yes/no question?
fn is_confirmation(output: &str) -> bool {
    let lower = output.to_ascii_lowercase();
    CONFIRMATION_MARKERS
        .iter()
        .any(|marker| lower.contains(marker))
}

/// May a checkpoint start, given how the last turn ended?
///
/// Split out from the actor so the rule can be read and tested on its own: it is
/// the difference between a checkpoint that works and one whose `save` is eaten —
/// by frotz's own confirmation, or by a question the story is asking.
fn prompts_allow_save(last_turn_at_prompt: bool, awaiting_answer: bool) -> bool {
    last_turn_at_prompt && !awaiting_answer
}

/// What the actor should do next, lifted out of the `select!` so that the handler
/// can borrow `self` mutably.
enum Tick {
    Request(GameRequest),
    Event(Option<FrotzEvent>),
    Timer,
    RequestsClosed,
}

struct Actor {
    cfg: GameConfig,
    req_rx: mpsc::Receiver<GameRequest>,
    evt_tx: mpsc::Sender<GameEvent>,
    frotz: Option<Frotz>,
    events_rx: Option<mpsc::Receiver<FrotzEvent>>,
    framer: TurnFramer,
    game: GameInfo,
    status: GameStatus,
    /// Is frotz sitting at its prompt, safe to write to?
    ready: bool,
    /// Is the story going nowhere? (02_game_boundary.md §4.3 (`G24`))
    detector: stuck::Detector,
    /// Inputs waiting for their turn.
    queue: VecDeque<(String, InputSource)>,
    /// Requests waiting for the game to be ready.
    pending: VecDeque<Op>,
    /// Scripted sequence in flight, if any.
    script: Option<Script>,
    /// Command turns since the last checkpoint.
    since_save: u64,
    /// Has an automatic checkpoint come due and not yet been taken?
    ///
    /// It is taken on the **next turn somebody asked for**, never on the turn that made it due, so
    /// the harness is never the first thing written into a screen nobody has answered yet. See
    /// `finish_turn` for what that is worth: measured 2026-10-02, an auto-checkpoint came due on the
    /// turn a companion fell off Wishbringer's cliff, its `save` was typed into the story's death
    /// menu a second later, before the companion had seen the screen, and the game left — an evening
    /// ending with nobody having been asked anything.
    auto_checkpoint_due: bool,
    /// Session-wide turn counter.
    turn_counter: u64,
    /// Did the last framed turn end at frotz's own `>` prompt?
    last_turn_at_prompt: bool,
    /// Is frotz waiting for a yes/no answer rather than a command?
    awaiting_answer: bool,
    /// Checkpoints parked until the game is ready for a command again.
    deferred_checkpoints: VecDeque<DeferredSave>,
    /// Source of the input that opened the turn in progress.
    current_source: InputSource,
    /// When we last wrote an input, for the no-output watchdog.
    wrote_at: Option<Instant>,
    /// Has anything happened worth saving?
    progress_made: bool,
    /// Time to stop.
    done: bool,
    /// Who to tell when a shutdown finishes.
    shutdown_reply: Option<oneshot::Sender<()>>,
    /// The save this interpreter was started on, when it was started on one.
    ///
    /// The interpreter reports every restore it performs over the tool channel, the one at boot
    /// included — and that one is not the frame's to announce (`S70`). A restore the player ordered
    /// gets its line from the code that ordered it, *before* the line that says the world forked
    /// (`S70.2`), because the companion builds its fork note on that ordering; a frame arrives
    /// whenever the interpreter gets there, which is no ordering at all, so the frame stays quiet
    /// for the boot it belongs to. A later frame has nobody else to speak for it: the story's own
    /// `restore`, typed by the player or by the companion, is exactly the load that used to come
    /// back unnamed (B7).
    ///
    /// Taken rather than matched on the path: the game's ordinary `restore` loads the *checkpoint*
    /// (the interpreter is told so at boot), so a restore of the very file a boot came back on is
    /// the common case, and path-matching would swallow it instead of the boot.
    boot_restore: Option<PathBuf>,
    /// The interpreter's tool channel, when it offered one (02_game_boundary.md §2 (`G5`–`G7`)).
    tools: Tools,
}

/// The core's end of the interpreter's tool channel.
///
/// It exists to replace inference with facts, one at a time, and to leave the old way of
/// doing each job in place for an interpreter that has no channel (03_session.md §9–§10 (`S58`–`S76`)). Everything
/// here is empty when there is no channel, and every method is a no-op then, so the fallback
/// path is the code that was always there rather than a second implementation of it.
#[derive(Debug, Default)]
struct Tools {
    /// What the interpreter said it was. `None` means no channel: `active()` is the switch.
    profile: Option<tool::Profile>,
    /// Has *any* frame come off the channel, whatever the handshake says?
    ///
    /// The profile is the interpreter's answer to "what are you?", and the answer can be missing
    /// while the channel itself is plainly working — frames arrive either way, and the handshake is
    /// one frame among them. Measured 2026-10-01, at a stop taken soon after a boot: the interpreter
    /// wrote the checkpoint and said `ok=true bytes=936 target=true` nine milliseconds in, while the
    /// core — believing there was no channel — waited five seconds for a filename prompt a tooled
    /// interpreter never prints. Three attempts at that stop failed because all three asked *the
    /// script*; the question was the predicate. See `has_a_channel`.
    channel_seen: bool,
    /// stdout bytes received, to compare against what the interpreter says it has sent.
    stdout_bytes: u64,
    /// Commands the interpreter has accepted since the last prompt.
    ///
    /// A command makes the interpreter refresh its prompt, and a refreshed prompt is not the
    /// end of a turn: it is the interpreter asking again. Counting them is how the core knows
    /// which prompt is which, without either side having to say so.
    commands_since_prompt: u32,
    /// Have the save targets been named to this interpreter yet?
    ///
    /// Not at handshake time: see `finish_turn`, where the story's opening turn has to be over
    /// first.
    targets_named: bool,
    /// A turn whose screen has not fully arrived: the byte count owed, and when to give up.
    owing: Option<(u64, Instant)>,
    /// Told to us by the interpreter for the turn in progress, for the turn record.
    parse: Option<tool::ParseFacts>,
    /// The status numbers the interpreter keeps for Versions 1 to 3.
    status: tool::Status,
    /// Does the interpreter want a command line, rather than a key? Asked rather than deduced,
    /// and the answer is what `awaiting_command` on the turn records (issue #19).
    awaiting_command: bool,
    /// The last save or restore the interpreter reported.
    saved: Option<tool::Event>,
    /// The screen, as the interpreter last handed it over.
    ///
    /// Asked for rather than assumed, and only ever on the interpreter's own word: a story that
    /// wants a key is drawing something the printed output does not show, and this is what that
    /// looks like. Taken by the turn it belongs to when the turn is framed.
    screen: Option<tool::ScreenFacts>,
    /// Has the screen been asked for at this prompt already?
    ///
    /// A story can read keys one after another — a form, a menu — and asking again after each
    /// one would turn one screen into a conversation. Cleared when the screen arrives.
    screen_wanted: bool,
    /// Where the story's own text ends in the framer's buffer, as of the last prompt.
    ///
    /// The bytes after this point are the prompt the interpreter *reprinted* for a command of
    /// ours, and they belong to no turn: discarded, so they cannot appear in front of the story's
    /// next answer. Everything before it is the turn, and is kept.
    turn_end_mark: usize,
}

impl Tools {
    /// Is there a channel to the interpreter?
    fn active(&self) -> bool {
        self.profile.is_some()
    }

    /// Does the interpreter have a tool channel?
    ///
    /// The question a *checkpoint* needs — "can this interpreter be told the file and write it
    /// itself?" — which is not the question `active()` answers, "did the profile handshake
    /// finish?". The two differ exactly when a frame has arrived and `Hello` has not: the
    /// interpreter is talking, its saves land where the core named them, and the old predicate
    /// could not see any of it.
    ///
    /// Deliberately narrow. Only the checkpoint decisions ask this. Turn framing still asks
    /// `active()`, because a handshake that has not finished is not something to frame a turn's
    /// bytes on, and widening this everywhere would move the framing rules to fix a save.
    fn has_a_channel(&self) -> bool {
        self.profile.is_some() || self.channel_seen
    }

    /// Forget everything per-game. The handshake is re-asked by the next interpreter.
    fn reset(&mut self) {
        *self = Tools::default();
    }
}

impl Actor {
    async fn run(mut self) {
        let mut ticker = interval(self.cfg.tick);
        ticker.set_missed_tick_behavior(MissedTickBehavior::Delay);

        while !self.done {
            let tick = self.next_tick(&mut ticker).await;
            match tick {
                Tick::Request(req) => self.handle_request(req).await,
                Tick::Event(Some(event)) => self.handle_frotz_event(event).await,
                Tick::Event(None) => self.handle_stream_closed().await,
                Tick::Timer => self.on_timer().await,
                Tick::RequestsClosed => {
                    info!("game actor: request channel closed; stopping");
                    break;
                }
            }
        }
        if let Some(reply) = self.shutdown_reply.take() {
            let _ = reply.send(());
        }
        debug!("game actor stopped");
    }

    async fn next_tick(&mut self, ticker: &mut Interval) -> Tick {
        tokio::select! {
            req = self.req_rx.recv() => req.map(Tick::Request).unwrap_or(Tick::RequestsClosed),
            event = recv_optional(&mut self.events_rx) => Tick::Event(event),
            _ = ticker.tick() => Tick::Timer,
        }
    }

    // -- requests -----------------------------------------------------------

    async fn handle_request(&mut self, req: GameRequest) {
        match req {
            GameRequest::Input { line, source } => {
                if self.queue.len() >= self.cfg.max_queue {
                    warn!(
                        line = %line,
                        "input queue full; refusing `{line}` so the game cannot be flooded"
                    );
                    self.notice("input_dropped", "too many commands queued; dropped one")
                        .await;
                    return;
                }
                self.queue.push_back((line, source));
                self.pump().await;
            }
            GameRequest::Checkpoint {
                reason,
                reply,
                slot,
            } => {
                self.pending.push_back(Op::Checkpoint {
                    reason,
                    reply: Some(reply),
                    slot,
                });
                self.pump().await;
            }
            GameRequest::Restore { slot, reply } => {
                self.pending.push_back(Op::Restore { slot, reply });
                self.pump().await;
            }
            GameRequest::Switch { game, reply } => {
                self.pending.push_back(Op::Switch {
                    game: Box::new(game),
                    reply,
                });
                self.pump().await;
            }
            GameRequest::Reset {
                confirm_stem,
                reply,
            } => {
                self.pending.push_back(Op::Reset {
                    confirm_stem,
                    reply,
                });
                self.pump().await;
            }
            GameRequest::Restart { reply } => {
                self.pending.push_back(Op::Restart { reply });
                self.pump().await;
            }
            GameRequest::Shutdown { reply } => {
                self.shutdown_reply = Some(reply);
                self.begin_shutdown().await;
            }
        }
    }

    /// Start the next thing that is allowed to happen.
    ///
    /// Pending requests outrank queued input: a checkpoint that was asked for at
    /// the end of the last turn should happen before the next command.
    async fn pump(&mut self) {
        if self.script.is_some() {
            return;
        }
        if !self.ready {
            // With no frotz running, only ops that do not need the game can
            // proceed; the rest must be answered rather than left hanging.
            if self.frotz.is_none() {
                if let Some(op) = self.pending.pop_front() {
                    match op {
                        Op::Switch { .. }
                        | Op::Reset { .. }
                        | Op::Restart { .. }
                        | Op::Restore { .. } => self.start_op(op).await,
                        Op::Checkpoint { reply, .. } => {
                            if let Some(reply) = reply {
                                let _ = reply.send(Err(anyhow!("no game is running")));
                            }
                        }
                        Op::Refresh => {}
                    }
                }
            }
            return;
        }
        if let Some(op) = self.pending.pop_front() {
            self.start_op(op).await;
            return;
        }
        if let Some((line, source)) = self.queue.pop_front() {
            self.dispatch(line, source).await;
        }
    }

    async fn start_op(&mut self, op: Op) {
        match op {
            Op::Checkpoint {
                reason,
                reply,
                slot,
            } => {
                if !self.at_command_prompt() {
                    debug!(
                        reason = reason.label(),
                        "the game is not at its prompt; deferring the checkpoint"
                    );
                    // In the record, because "the save is pending" is a fact about the
                    // session, and because the only other trace of a deferral is a debug
                    // line in the daemon's own log. Silent to the player (see the core's
                    // handling of this event): nothing has gone wrong yet.
                    self.notice(
                        "checkpoint_deferred",
                        "the game is waiting for an answer, so the save is being held back",
                    )
                    .await;
                    // §2.6: the pane that asked is told *now* that nothing was saved. This used to
                    // be silent — the deferral is kept out of the panes because "nothing has gone
                    // wrong yet" — and silence is how a player comes to believe they have a save
                    // they do not have. The request is still parked and still runs when the game is
                    // ready; what changes is that nobody is left guessing in the meantime.
                    if let Some(reply) = reply {
                        let _ = reply.send(Err(anyhow!(
                            "nothing was saved: the game is waiting for an answer, and a save written \
                             into that prompt would answer it. Answer what it is asking — a line, \
                             `/pressreturn`, or `/presskey` — and the save goes through."
                        )));
                    }
                    self.deferred_checkpoints.push_back(DeferredSave {
                        op: Op::Checkpoint {
                            reason,
                            reply: None,
                            slot,
                        },
                        turns_waited: 0,
                    });
                    return;
                }
                if self.can_checkpoint() {
                    self.begin_checkpoint(reason, reply, None, slot).await;
                } else if self.detector.stuck() {
                    // The other reason a save is refused, and it needs its own words: "nothing has
                    // happened yet" is a lie when the truth is that nothing *is* happening. A
                    // recovery point recorded from a position the story will not move out of is the
                    // position a player comes back to (02_game_boundary.md §4.3 (`G24`)), so this is a refusal with a
                    // reason and a way out, not a failure.
                    debug!(signals = %self.detector.held().describe(), "refusing a checkpoint while the story is stuck");
                    if let Some(reply) = reply {
                        let _ = reply.send(Err(anyhow!(
                            "nothing was saved: the story has stopped moving ({}), and a save written \
                             from here would record a position we cannot get out of. Get the world \
                             moving first — the last checkpoint on disk still stands.",
                            self.detector.held().describe()
                        )));
                    }
                } else {
                    // Nothing has happened yet, so the position the player is asking for is the one
                    // they are already standing in, and no file would be written for it.
                    //
                    // Refused rather than reported as a success, because `/save` answers by *naming
                    // the file* (§2.6): `saved 00-poodle — saves/zork1/00-poodle.sav` over a save
                    // that never happened is the one lie a player cannot check, and asking for a
                    // save on a fresh boot is the first thing anybody does. The 2026-09-20 UAT hit
                    // it in the first minute: the pane said the save existed, `/list` showed
                    // nothing, and the second `/save 00-poodle` was refused as a duplicate *of a
                    // file that was not there*. Only the explicit save has somebody waiting on this
                    // answer; an autosave with nothing to keep stays silent, as it always did.
                    debug!("nothing worth checkpointing yet; refusing rather than pretending");
                    if let Some(reply) = reply {
                        let _ = reply.send(Err(anyhow!(
                            "nothing has happened yet, so there is nothing to save — `/save` writes \
                             the position you are at, and this game has not moved. Play a turn and \
                             ask again."
                        )));
                    }
                }
            }
            Op::Restore { slot, reply } => {
                let stem = self.game.stem.clone();
                let (path, label) = match &slot {
                    Some(name) => (self.cfg.state.named_checkpoint(&stem, name), name.clone()),
                    None => (self.cfg.state.checkpoint(&stem), stem.clone()),
                };
                if !path.is_file() {
                    let _ =
                        reply.send(Err(anyhow!("there is no save called `{label}` for {stem}")));
                    return;
                }

                // The same story and the same attempt, standing somewhere else: a fork of the
                // world rather than a new go at it (02_game_boundary.md §6 (`G27`–`G34`)). The generation is bumped only
                // once the world is actually back, so a restore that fails does not claim one.
                let game = self.game.clone();
                let result = self
                    .start_game(&game, WhereToRestore::Slot(path.clone()))
                    .await;

                if result.is_ok() {
                    // Which save came back, and what its note knows about it, before the line that
                    // says the load happened (B7, S70): the companion builds its note on that line,
                    // and a fact that arrives after the note it belongs in is a fact the note does
                    // not have. The interpreter's own frame for this restore stays quiet for it
                    // (see `boot_restore`) — this is the ordering the frame cannot keep.
                    //
                    // The player is told as well, and on purpose — the same string goes to both
                    // ends (04_companion.md §6 (`C42`–`C47`)), because the companion may only speak about what the
                    // player can see, and two wordings of one fact is how that stops being true.
                    self.notice("save_position", save::said_of_the_load(&path))
                        .await;
                    match runs::fork_generation(self.cfg.state.root(), &stem) {
                        Ok(attempt) => {
                            info!(
                                stem = %stem,
                                slot = %label,
                                generation = attempt.generation,
                                "the world forked"
                            );
                            self.notice(
                                "restore",
                                format!(
                                    "{stem} restored from {label} — the world forked (fork {})",
                                    attempt.generation
                                ),
                            )
                            .await;
                        }
                        Err(err) => {
                            warn!(error = %err, "could not record the fork");
                            self.notice("restore", format!("{stem} restored from {label}"))
                                .await;
                        }
                    }
                }

                let _ = reply.send(result);
            }
            Op::Switch { game, reply } => {
                if !self.at_command_prompt() {
                    // frotz would eat the save's `save` as the answer to whatever it
                    // is asking, so switch without one rather than time out. The
                    // previous checkpoint on disk still stands.
                    warn!(
                        stem = %game.stem,
                        "the game is waiting for an answer; switching without saving"
                    );
                    self.notice(
                        "checkpoint_failed",
                        "the game was waiting for an answer, so nothing was saved before switching",
                    )
                    .await;
                    let result = self.start_game(&game, WhereToRestore::Canonical).await;
                    let _ = reply.send(result);
                    return;
                }
                if self.can_checkpoint() {
                    self.begin_checkpoint(SaveReason::Switch, None, Some((*game, reply)), None)
                        .await;
                } else {
                    let result = self.start_game(&game, WhereToRestore::Canonical).await;
                    let _ = reply.send(result);
                }
            }
            Op::Reset {
                confirm_stem,
                reply,
            } => {
                if confirm_stem != self.game.stem && confirm_stem != self.status.stem {
                    let _ = reply.send(Err(anyhow!(
                        "reset not confirmed: expected the game's name (`{}`)",
                        self.status.stem
                    )));
                    return;
                }
                match save::delete_progress(&self.cfg.state, &self.game.stem) {
                    Ok(removed) => {
                        info!(stem = %self.game.stem, files = removed.len(), "progress deleted");
                        self.notice(
                            "reset",
                            format!("reset {} to a blank slate", self.game.stem),
                        )
                        .await;
                    }
                    Err(err) => {
                        error!(error = %err, "failed to delete progress");
                        self.notice("reset_failed", format!("could not delete progress: {err}"))
                            .await;
                    }
                }
                let game = self.game.clone();
                let result = self.start_game(&game, WhereToRestore::Fresh).await;
                let _ = reply.send(result);
            }
            Op::Restart { reply } => {
                // One file: the checkpoint goes, so the game boots at its opening text.
                // The notebook is untouched, which is the whole difference between a
                // restart and a reset (issue #26).
                match save::delete_checkpoint(&self.cfg.state, &self.game.stem) {
                    Ok(removed) => {
                        info!(stem = %self.game.stem, files = removed.len(), "checkpoint deleted");
                        self.notice(
                            "restart",
                            format!("starting {} again from the top", self.game.stem),
                        )
                        .await;
                    }
                    Err(err) => {
                        error!(error = %err, "failed to delete the checkpoint");
                        self.notice("restart_failed", format!("could not start over: {err}"))
                            .await;
                    }
                }
                let game = self.game.clone();
                let result = self.start_game(&game, WhereToRestore::Fresh).await;
                let _ = reply.send(result);
            }
            Op::Refresh => {
                self.dispatch("look".to_string(), InputSource::Scripted)
                    .await;
            }
        }
    }

    /// Did anything worth saving happen?
    ///
    /// A story that is going nowhere has nothing worth saving, and the save itself is the harm: a
    /// recovery point recorded from a position the story will not move out of is the position a
    /// player comes back to, which is how a fork taken inside a fog-bound maze became the place a
    /// whole session returned to (02_game_boundary.md §4.3 (`G24`)).
    fn can_checkpoint(&self) -> bool {
        !self.detector.stuck()
            && self.frotz.is_some()
            && (self.progress_made || self.cfg.state.has_checkpoint(&self.game.stem))
    }

    /// Is frotz sitting at its own prompt, ready to take a command?
    ///
    /// Both halves matter. A turn that ended by quiescence while frotz's last line
    /// was not `>` means frotz asked something — the measured case is
    /// `Are you sure you want to quit? ` — and the next line written answers it.
    /// `awaiting_answer` then covers the follow-up, where frotz re-asks behind an
    /// ordinary `>` prompt and the prompt alone would look safe.
    /// And neither half is worth anything while the story is going nowhere — but that is not said
    /// here. `at_command_prompt` answers *what shape is the prompt* (02_game_boundary.md §4.2–§4.3 (`G20`–`G24`)), and its answer
    /// is carried into the turn for the companion, which reads "not a command prompt" as *the game is
    /// asking something* and answers it (§10.3, issue #19). Stuck is a different fact with a different
    /// action, so it is checked where a save is decided (`can_checkpoint`), not here.
    fn at_command_prompt(&self) -> bool {
        self.ready && prompts_allow_save(self.last_turn_at_prompt, self.awaiting_answer)
    }

    /// Feed the newest turn to the stuck detector, and say so when the verdict changes.
    ///
    /// The companion hears it as well as the player, and that is the point of the notice rather
    /// than a log line: *"we are not getting anywhere"* is a fact about **how the harness and the
    /// companion are playing**, not about the story, and the only thing that can act on it is the
    /// one part of the system allowed to think above the game. Nothing here can be solved by
    /// repeating the last command, and the notice says so in as many words.
    async fn note_progress(&mut self, turn: &Turn, accepted: Option<bool>) {
        let evidence = stuck::Turn {
            moves: self.tools.status.moves,
            score: self.tools.status.score,
            location: self.tools.status.location,
            fingerprint: observations::fingerprint(&turn.output),
            // Passed in rather than read here: the turn carries the parser's facts onward, so by the
            // time this runs they are gone (see the note at the call site).
            accepted,
            // Whatever the companion could read back, which is the screen as well as the output:
            // a story that draws its room rather than printing it has still shown something.
            produced: !turn.output.trim().is_empty() || turn.screen.is_some(),
            line: turn.input.clone(),
        };

        let was = self.detector.stuck();
        let now = self.detector.observe(evidence);
        if was == now {
            return;
        }

        if now {
            let held = self.detector.held();
            self.notice(
                "stuck",
                format!(
                    "the harness reads this as stuck: {} ({} of 4 signals). No further checkpoints \
                     will be taken while this lasts, because a save written from here is a position \
                     the story will not move out of. Do not send the same command again — go and \
                     reason about how this is being played rather than about the story: try \
                     something else, look at the screen afresh, or ask the player to decide what \
                     happens next.",
                    held.describe(),
                    held.count()
                ),
            )
            .await;
        } else {
            self.notice("unstuck", "the world moved again: checkpoints are back on.")
                .await;
        }
    }

    /// Track whether frotz is waiting for a yes/no answer rather than a command.
    ///
    /// Clearing needs the prompt as well as the absence of a marker: frotz re-asks
    /// behind a normal `>` prompt, so a marker once seen is only behind us once a
    /// turn ends at the prompt without asking again.
    ///
    /// Deliberately a list of measured wordings rather than "ends with a question mark".
    /// That generalisation was tried and measured against the stories we ship: Wishbringer
    /// ends ordinary turns with the narration `What next?`, so every checkpoint deferred
    /// until it gave up and its autosaves were lost — a worse failure than the one the rule
    /// was meant to prevent, which costs one line and is aborted honestly. A story's own
    /// question hiding behind a `>` prompt cannot be told from narration; frotz's own can.
    fn note_confirmation_state(&mut self, output: &str) {
        if is_confirmation(output) {
            self.awaiting_answer = true;
        } else if self.last_turn_at_prompt {
            self.awaiting_answer = false;
        }
    }

    /// Run any checkpoint that was waiting for the game to be ready.
    async fn retry_deferred_checkpoints(&mut self) {
        if self.deferred_checkpoints.is_empty() {
            return;
        }

        let ready = self.at_command_prompt();
        let mut still_waiting = VecDeque::new();
        while let Some(mut deferred) = self.deferred_checkpoints.pop_front() {
            if ready {
                debug!("the game is back at its prompt; running the waiting checkpoint");
                self.pending.push_back(deferred.op);
                continue;
            }

            deferred.turns_waited += 1;
            if deferred.turns_waited >= self.cfg.max_deferred_checkpoint_turns {
                let Op::Checkpoint { reason, reply, .. } = deferred.op else {
                    continue;
                };
                warn!(
                    reason = reason.label(),
                    "gave up waiting for the game's prompt; the checkpoint was dropped"
                );
                // Both paths below report, and neither is optional: an auto-checkpoint that
                // quietly evaporates is data the player would lose without being told, and it
                // is the silent case — not the loud one — that this whole sequence exists to
                // remove.
                self.notice(
                    "checkpoint_dropped",
                    "the game is waiting for an answer, so nothing was saved",
                )
                .await;
                if let Some(reply) = reply {
                    // Only an explicit save has someone waiting on the answer.
                    let _ = reply.send(Err(anyhow!(
                        "the game is waiting for an answer, so it could not be saved"
                    )));
                }
                continue;
            }
            still_waiting.push_back(deferred);
        }
        self.deferred_checkpoints = still_waiting;
    }

    /// Abandon any waiting checkpoint, telling whoever asked for it.
    fn drop_deferred_checkpoints(&mut self, why: &str) {
        for deferred in self.deferred_checkpoints.drain(..) {
            if let Op::Checkpoint {
                reply: Some(reply), ..
            } = deferred.op
            {
                let _ = reply.send(Err(anyhow!(why.to_string())));
            }
        }
    }

    /// Build the `save` → filename → `Ok.` → prompt sequence (§5.4).
    ///
    /// frotz asks `Overwrite existing file?` when the target exists and fails the
    /// save if that goes unanswered (measured), so the checkpoint is written to a
    /// path we have just deleted, then renamed into place.
    async fn begin_checkpoint(
        &mut self,
        reason: SaveReason,
        reply: Option<oneshot::Sender<Result<()>>>,
        switch: Option<(GameInfo, oneshot::Sender<Result<GameStatus>>)>,
        slot: Option<String>,
    ) {
        let stem = self.game.stem.clone();
        // **The file the player named is the file the interpreter is pointed at** (§5.3). The
        // screen is where a save becomes visible, and a screen reading `wishbringer.sav` after
        // `/save 00-poodle` tells the player their name was ignored — the 2026-09-20 UAT found it
        // and could not tell whether the save existed at all.
        //
        // This does not make a name a second write path: one sequence still runs through the
        // interpreter, to one path, and the game's own checkpoint is then refreshed *from* what it
        // wrote — the same copy, in the other direction. Nothing is staged on this path either
        // way, and the fallback interpreter renames its staged file to the same name before the
        // copy, so both interpreters end with the name holding the position and the checkpoint
        // holding the same bytes.
        let final_path = checkpoint_target(&self.cfg.state, &stem, slot.as_deref());

        // **One checkpoint at a time** (`03_session.md` §10 (`S62`)). A second request while a sequence is in
        // flight used to *replace* it — and the replacement arrives with `awaiting_saved: false`, so
        // when the interpreter announced the save it had just been asked for, the script in hand was
        // not the one waiting for that announcement. Nothing advanced it, and it sat until its deadline.
        //
        // Measured 2026-10-01, at every shutdown: the save itself landed **9 ms** in with
        // `target=true`, and five seconds later the core logged `checkpoint shutdown timed out; frotz
        // last said: "Okay, done.\n\n>"` — a buffer ending in a prompt, so the matcher looked at fault
        // and was not (`a_finished_save_ends_with_a_prompt`). The cost was five of the shutdown's
        // seconds and an ERROR line about a save that had worked.
        //
        // Queued rather than dropped, which is what `begin_shutdown` already does with a request that
        // arrives mid-sequence: the work is still wanted, it is only early. A switch is not queued —
        // it carries the game it is changing to, which the queue's shape cannot hold, and superseding
        // is what a switch means.
        if self.script.is_some() && switch.is_none() {
            self.pending.push_back(Op::Checkpoint {
                reason,
                reply,
                slot,
            });
            return;
        }

        // Whatever the interpreter said about the last save belongs to the last save. Cleared here,
        // at the only place a checkpoint begins, so the frame that decides whether *this* one
        // happened cannot be an older one's (03_session.md §10 (`S62`) step 3).
        self.tools.saved = None;
        let (label, after) = match switch {
            Some((game, switch_reply)) => (
                format!("switch {stem} -> {}", game.stem),
                AfterScript::Switch(Box::new(game), switch_reply),
            ),
            None => (
                format!("checkpoint {}", reason.label()),
                AfterScript::Nothing(reason),
            ),
        };
        info!(stem = %stem, reason = reason.label(), "taking a checkpoint");

        if self.tools.has_a_channel() {
            // With a channel the whole sequence is two lines and no guesswork: name the file,
            // then let the story's *own* `save` run, and wait for the interpreter to say it
            // wrote it. Letting the story run the opcode is not decoration — a save taken from
            // outside the story (the interpreter copying its state at a prompt) records a
            // program counter after the read, with the previous command still in the parse
            // buffer, and restoring it would run that command again.
            //
            // So: the semantics are the story's, the *file* is the host's. No staging file, no
            // overwrite question, no restricted-path arithmetic, no success wording, and
            // nothing that changes shape when the state directory gets longer.
            self.script = Some(Script {
                label,
                stages: channel_checkpoint_stages(&final_path),
                cursor: 0,
                buf: Vec::new(),
                fail_markers: vec![save::SAVE_FAIL_MARKER],
                deadline: Instant::now() + self.cfg.script_timeout,
                staged_path: None,
                final_path: Some(final_path),
                after,
                fail_reply: reply,
                awaiting_saved: false,
                last_command: None,
                tool_save: true,
                reason_label: reason.label().to_string(),
                slot,
            });
            // The first stage has no expectation, so this runs it immediately.
            self.advance_script(&[]).await;
            return;
        }

        let staged = self.cfg.state.staged_checkpoint(&stem);
        if let Err(err) = std::fs::remove_file(&staged) {
            if err.kind() != std::io::ErrorKind::NotFound {
                warn!(path = %staged.display(), error = %err, "could not clear the staged checkpoint");
            }
        }

        self.script = Some(Script {
            label,
            stages: checkpoint_stages(&staged),
            cursor: 0,
            buf: Vec::new(),
            fail_markers: vec![save::SAVE_FAIL_MARKER],
            deadline: Instant::now() + self.cfg.script_timeout,
            staged_path: Some(staged),
            final_path: Some(final_path),
            after,
            fail_reply: reply,
            awaiting_saved: false,
            last_command: None,
            tool_save: false,
            reason_label: reason.label().to_string(),
            slot,
        });
        // The first stage has no expectation, so this runs it immediately.
        self.advance_script(&[]).await;
    }

    async fn begin_shutdown(&mut self) {
        if self.script.is_some() {
            // A sequence is in flight; stop once it finishes.
            self.pending.push_back(Op::Checkpoint {
                reason: SaveReason::Shutdown,
                reply: None,
                slot: None,
            });
            return;
        }
        if self.ready && self.can_checkpoint() {
            let stem = self.game.stem.clone();
            let final_path = self.cfg.state.checkpoint(&stem);
            info!(stem = %stem, "checkpointing before shutdown");

            // **A stop is a checkpoint, and it is taken the way this interpreter can take it.**
            //
            // This function built the *fallback* script whatever the interpreter was, and that is
            // the whole of the five seconds every clean stop has been paying: a tooled interpreter
            // typed `save`, ran the story's own opcode, wrote the file itself and said so 9 ms in —
            // and was then waited on for a filename prompt and a prompt it never prints. The save
            // worked; the sequence was one this interpreter could not satisfy.
            //
            // Three attempts were spent on this stop and all three were in `begin_checkpoint`,
            // where a channel path already existed. The path a *stop* takes had no channel branch at
            // all, which is why none of the three changed anything (03_session.md §10 (`S62`)).
            if self.tools.has_a_channel() {
                self.script = Some(Script {
                    label: "checkpoint shutdown".to_string(),
                    stages: channel_checkpoint_stages(&final_path),
                    cursor: 0,
                    buf: Vec::new(),
                    fail_markers: vec![save::SAVE_FAIL_MARKER],
                    deadline: Instant::now() + self.cfg.script_timeout,
                    staged_path: None,
                    final_path: Some(final_path),
                    after: AfterScript::Shutdown,
                    fail_reply: None,
                    awaiting_saved: false,
                    last_command: None,
                    tool_save: true,
                    reason_label: SaveReason::Shutdown.label().to_string(),
                    slot: None,
                });
                self.advance_script(&[]).await;
                return;
            }

            let staged = self.cfg.state.staged_checkpoint(&stem);
            if let Err(err) = std::fs::remove_file(&staged) {
                if err.kind() != std::io::ErrorKind::NotFound {
                    warn!(path = %staged.display(), error = %err, "could not clear the staged checkpoint");
                }
            }
            self.script = Some(Script {
                label: "checkpoint shutdown".to_string(),
                stages: checkpoint_stages(&staged),
                cursor: 0,
                buf: Vec::new(),
                fail_markers: vec![save::SAVE_FAIL_MARKER],
                deadline: Instant::now() + self.cfg.script_timeout,
                staged_path: Some(staged),
                final_path: Some(final_path),
                after: AfterScript::Shutdown,
                fail_reply: None,
                awaiting_saved: false,
                last_command: None,
                tool_save: false,
                reason_label: SaveReason::Shutdown.label().to_string(),
                slot: None,
            });
            self.advance_script(&[]).await;
        } else {
            self.finish_shutdown().await;
        }
    }

    async fn finish_shutdown(&mut self) {
        if let Some(frotz) = self.frotz.take() {
            let code = frotz.shutdown().await;
            debug!(code = ?code, "frotz stopped");
        }
        self.events_rx = None;
        self.done = true;
    }

    // -- frotz events -------------------------------------------------------

    async fn handle_frotz_event(&mut self, event: FrotzEvent) {
        match event {
            FrotzEvent::Data(bytes) => {
                // Counted first: with the tool channel the interpreter says how many bytes it
                // has written, and the two counts are how the core knows a turn's screen has
                // arrived in full.
                self.tools.stdout_bytes += bytes.len() as u64;
                if self.script.is_some() {
                    // A scripted sequence is machinery, and the screen shows machinery
                    // (issue #20): the save prompt and the game's answer are part of what
                    // happened on that terminal, and swallowing them left a hole exactly
                    // where a checkpoint had been. It is still not fed to the framer, so it
                    // never becomes a turn and never reaches the agent's context.
                    self.emit_raw(&bytes, true).await;
                    self.advance_script(&bytes).await;
                    self.close_turn_if_paid_for().await;
                    return;
                }
                self.emit_raw(&bytes, false).await;
                let now = Instant::now();
                if self.tools.active() {
                    // The framer still collects the turn's bytes and trims them exactly as
                    // always; what it does not get to do is decide that the turn is over. That
                    // decision belongs to the interpreter now, and it is the difference
                    // between a frame and an inference (02_game_boundary.md §3 (`G9`–`G15`)).
                    //
                    // Bytes that arrive with no turn open are nobody's output: a scripted
                    // sequence's leftovers, or a prompt reprinted after the core named a save
                    // file. They still reach the screen — `emit_raw` above has already sent them
                    // — but they must not open a turn, because a turn opened by output alone is
                    // a turn with no input, and everything after it would be attributed to the
                    // wrong command.
                    if self.framer.is_open() {
                        self.framer.accumulate(&bytes, now);
                    }
                    self.close_turn_if_paid_for().await;
                    return;
                }
                if let Some(turn) = self.framer.feed(&bytes, now) {
                    self.finish_turn(turn).await;
                }
            }
            FrotzEvent::Tool(event) => self.handle_tool_event(event).await,
            FrotzEvent::Eof => self.handle_stream_closed().await,
        }
    }

    /// One event off the interpreter's tool channel.
    ///
    /// Nothing here is allowed to be an inference. Each arm replaces a guess the core used to
    /// make with something the interpreter knows: when a turn is over, whether the story wants
    /// a command or a key, which words its parser accepted, and which file a save went to.
    async fn handle_tool_event(&mut self, event: ToolEvent) {
        // Every frame, by name, at trace level: when the channel and the core disagree about
        // something, the order of these lines is the whole argument.
        trace!(event = event.name(), "tool event");
        // A frame is a channel: whatever this event turns out to be, the interpreter is talking to
        // us, and that is what a checkpoint has to know. Set before the match so that no arm — least
        // of all one that returns early — can be the thing that decides it.
        self.tools.channel_seen = true;
        match event {
            ToolEvent::Hello(profile) => {
                info!(
                    build = %profile.build,
                    protocol = profile.protocol,
                    tools = %profile.tools.join(","),
                    profile = %profile.describe(),
                    "the interpreter offered a tool channel"
                );
                // Newer is fine and expected: the protocol only ever adds events, and an event
                // this build does not know is ignored rather than fatal. Older is not, because
                // the handshake itself would be a different shape.
                if profile.protocol > tool::PROTOCOL {
                    info!(
                        theirs = profile.protocol,
                        ours = tool::PROTOCOL,
                        "the interpreter speaks a newer tool protocol; unknown events are ignored"
                    );
                } else if profile.protocol < tool::PROTOCOL {
                    warn!(
                        theirs = profile.protocol,
                        ours = tool::PROTOCOL,
                        "the interpreter speaks an older tool protocol than this build"
                    );
                }
                self.tools.profile = Some(*profile);
                // The save file is named, and the channel's health asked after, only once the
                // story's opening turn is over — see `name_the_save_file`.
            }
            ToolEvent::InputWanted {
                kind,
                continued,
                out_bytes,
                after_restore,
                status,
            } => {
                self.tools.status = status;
                if self.tools.commands_since_prompt > 0 {
                    // One of our own commands was accepted and the interpreter is asking
                    // again. This is not a turn boundary: the bytes it just printed are a
                    // refreshed prompt, and the turn in progress has not moved on. Only the
                    // *bytes* go — a command can be written while a line is already in flight
                    // (the save targets are named as soon as the story has opened, and the line
                    // queued behind them is dispatched immediately), so clearing the whole
                    // framer would throw away a turn that has its input and is simply waiting
                    // for its output.
                    self.tools.commands_since_prompt -= 1;
                    self.tools.owing = None;
                    self.framer.discard_since(self.tools.turn_end_mark);
                    // Unless the command was the screen, and it has now arrived. A key prompt's
                    // bytes are paid for before the question is asked, so the screen is the one
                    // thing the turn was still waiting on: with it in hand the turn is complete,
                    // and leaving it open would put the form in the *next* turn's output.
                    if self.tools.screen.is_some() {
                        self.close_turn_now().await;
                    }
                    return;
                }
                // A line prompt means the story is ready for a command. A character prompt
                // means it wants a key, which is the question the prompt heuristic answered by
                // accident and the reason a menu could look like a finished turn (issue #19).
                self.tools.awaiting_command = matches!(kind, InputKind::Line);
                // The story has finished printing and is waiting: this is where its text ends, and
                // anything after it is a prompt the interpreter reprints for a command of ours.
                self.tools.turn_end_mark = self.framer.buffered_len();
                // A key prompt is the one moment the printed output is not the story's state:
                // a form or a menu is drawn in the screen and left there, and the dumb
                // interface only prints rows that have changed and are not blank. So ask for
                // the screen, once per prompt, and the turn carries it.
                if !self.tools.awaiting_command
                    && !self.tools.screen_wanted
                    && self
                        .tools
                        .profile
                        .as_ref()
                        .map(|profile| profile.offers("screen"))
                        .unwrap_or(false)
                {
                    self.tools.screen_wanted = true;
                    if !self.write_tool(&[tool::screen()]).await {
                        warn!("could not ask the interpreter for its screen");
                    }
                }
                debug!(
                    kind = ?kind,
                    continued,
                    after_restore,
                    owed = out_bytes,
                    received = self.tools.stdout_bytes,
                    status = %self.tools.status.describe(),
                    "the interpreter is waiting for input"
                );
                self.tools.owing = Some((out_bytes, Instant::now() + self.cfg.tool_turn_deadline));
                self.close_turn_if_paid_for().await;
            }
            ToolEvent::Input {
                text,
                kind,
                after_restore,
            } => {
                // The framer already has the line the core wrote, and this is the
                // interpreter's own copy of what the story received. Kept in the log because
                // it is ground truth about the one thing the core cannot check any other way.
                debug!(kind = ?kind, after_restore, len = text.len(), "the interpreter took input");
            }
            ToolEvent::Parse(facts) => {
                debug!(
                    line = %facts.line,
                    words = facts.words,
                    unknown = ?facts.unknown(),
                    parser = %facts.describe(),
                    "the story's parser answered"
                );
                self.tools.parse = Some(facts);
            }
            ToolEvent::Saved {
                path,
                ok,
                bytes,
                target,
            } => {
                info!(
                    path = %path.display(),
                    ok,
                    bytes,
                    target,
                    "the interpreter finished a save"
                );
                // Is this the save *this* script is waiting for? The flag alone is not the answer,
                // and that is the bug this replaces. `awaiting_saved` is set when the script has sent
                // its last line (`advance_script`), so a save that arrives *before* that — which is
                // what a channel save does, because the interpreter is told the target first and
                // writes the file straight away — was dropped on the floor: nothing completed the
                // script, and it sat out its deadline.
                //
                // Measured 2026-10-01, at every clean stop: `the interpreter finished a save …
                // ok=true bytes=940 target=true` **16 ms** after `checkpointing before shutdown`, and
                // five seconds later `checkpoint shutdown timed out; frotz last said: "Okay, done.\n\n>"`.
                // The save had worked; what failed was the reading of it. A `tool_save` script *is*
                // the statement "the interpreter's own word decides this" (`S62`), so any save frame
                // that arrives while one is in flight is its — and `self.tools.saved` is cleared at
                // the only place a checkpoint begins, so it can only ever be this one's.
                let waiting = self
                    .script
                    .as_ref()
                    .map(|script| script.tool_save || script.awaiting_saved)
                    .unwrap_or(false);
                self.tools.saved = Some(ToolEvent::Saved {
                    path,
                    ok,
                    bytes,
                    target,
                });
                if waiting {
                    self.complete_script().await;
                }
            }
            ToolEvent::Restored { path, ok } => {
                info!(path = %path.display(), ok, "the interpreter finished a restore");
                // The boot's own restore has been spoken for (see `boot_restore`); a later one is
                // news, and it arrives here — while the turn that ran the restore is still open —
                // so the position is in hand before that turn's fork line is built (`S70.2`).
                let ordered_at_boot = self.boot_restore.take().is_some();
                let sentence = if ok && !ordered_at_boot {
                    Some(save::said_of_the_load(&path))
                } else {
                    None
                };
                self.tools.saved = Some(ToolEvent::Restored { path, ok });
                if let Some(sentence) = sentence {
                    self.notice("save_position", sentence).await;
                }
            }
            ToolEvent::Command { verb } => {
                // Accepted, and — for the interpreter's own bookkeeping — the prompt that
                // follows this is a refresh rather than the end of a turn. Counted here rather
                // than when we write, so that a command typed by hand at a terminal counts too.
                debug!(verb = %verb, "tool command accepted");
                self.tools.commands_since_prompt += 1;
                if let Some(script) = self.script.as_mut() {
                    script.last_command = Some(verb);
                }
                self.advance_script(&[]).await;
            }
            ToolEvent::Screen(facts) => {
                debug!(screen = %facts.describe(), "the interpreter handed over its screen");
                self.tools.screen_wanted = false;
                self.tools.screen = Some(facts);
            }
            ToolEvent::Note { event, detail } => {
                warn!(target: "frotz", event = %event, "{}", detail);
            }
            ToolEvent::Diagnostic(text) => {
                warn!(target: "frotz", "{}", text.trim_end());
            }
            ToolEvent::Quit { status } => {
                info!(status, "the interpreter says it is leaving");
            }
            ToolEvent::Pong => {}
            ToolEvent::Unknown(name) => {
                // Additive by design: an interpreter that grows a tool this build has never
                // heard of is a normal Tuesday, not a fault.
                debug!(event = %name, "ignoring a tool event this build does not know");
            }
            ToolEvent::Stats { frames, dropped } => {
                // The one number that says whether the channel itself is healthy. A frame is
                // only ever dropped when the reader is behind, which is exactly the condition
                // that would quietly cost a turn its boundary — so it is said out loud, in the
                // log, rather than left to be inferred from a session that behaves oddly.
                if dropped > 0 {
                    warn!(
                        frames,
                        dropped, "the interpreter has dropped tool frames; the channel is behind"
                    );
                } else {
                    debug!(frames, "the tool channel is keeping up");
                }
            }
        }
    }

    /// Close the turn once the screen the interpreter promised has arrived.
    ///
    /// The interpreter counts every byte it writes to stdout and says the total with each
    /// prompt, so a turn is complete the moment that many bytes have reached us. That is the
    /// whole point of the channel: no quiescence window, no prompt heuristic, and no guessing
    /// about which bytes belong to which turn. The deadline is the safety net for the case
    /// where the counts disagree, and it is short on purpose.
    ///
    /// A screen the core has *asked for* is part of the same turn: at a key prompt the bytes are
    /// already paid for by the time the question is asked, so closing on the count alone would
    /// frame the turn and leave the screen — the form, the menu, the thing that prompt is about —
    /// arriving just too late to belong to it. Waited for, on the same deadline as the bytes.
    async fn close_turn_if_paid_for(&mut self) {
        let Some((owed, deadline)) = self.tools.owing else {
            return;
        };
        let paid = self.tools.stdout_bytes >= owed;
        if paid && self.tools.screen_wanted && Instant::now() < deadline {
            return;
        }
        if paid {
            self.tools.owing = None;
            self.close_turn_now().await;
        } else if Instant::now() >= deadline {
            warn!(
                owed,
                received = self.tools.stdout_bytes,
                "the interpreter promised more screen than arrived; closing the turn anyway"
            );
            self.tools.owing = None;
            self.close_turn_now().await;
        }
    }

    async fn close_turn_now(&mut self) {
        if let Some(turn) = self.framer.finish_on_signal(Instant::now()) {
            self.finish_turn(turn).await;
        }
    }

    /// Hand frotz's bytes to the screen, always, and say whether they are gameplay.
    ///
    /// The screen is fed every byte; the core decides separately what belongs to the
    /// agent's transcript. `machinery` is what the core drove frotz through — a save
    /// prompt, a filename, a `[Okay.]` — which a real screen shows and a transcript should
    /// not (§5.4, issues #19 and #20).
    async fn emit_raw(&self, bytes: &[u8], machinery: bool) {
        let _ = self
            .evt_tx
            .send(GameEvent::Raw {
                bytes: bytes.to_vec(),
                machinery,
            })
            .await;
    }

    async fn handle_stream_closed(&mut self) {
        if self.frotz.is_none() && self.events_rx.is_none() {
            return; // already handled
        }
        // A story that prints its last words and then leaves — `quit`, a fatal error — never
        // reaches a prompt, so the interpreter's signal never comes. What it printed is still
        // a turn, and the old framer closed it by quiescence; close it here for the same
        // reason, and say so rather than losing the last thing the game said.
        if self.tools.active() && self.script.is_none() && self.framer.is_open() {
            self.tools.owing = None;
            debug!("the interpreter left without asking for input; closing the turn it opened");
            self.close_turn_now().await;
        }
        self.events_rx = None;
        let code = match self.frotz.take() {
            Some(mut frotz) => frotz.kill().await,
            None => None,
        };
        self.ready = false;
        self.script = None;
        warn!(code = ?code, "frotz exited");
        let _ = self
            .evt_tx
            .send(GameEvent::System {
                event: "game_exited".to_string(),
                detail: format!(
                    "the game process stopped (exit {}). Use /switch <game> to load another, or /reset {} to start this one over.",
                    code.map(|c| c.to_string()).unwrap_or_else(|| "unknown".into()),
                    self.status.stem
                ),
            })
            .await;
        let _ = self.evt_tx.send(GameEvent::Exited(code)).await;
    }

    async fn on_timer(&mut self) {
        let now = Instant::now();

        // With the tool channel the turn is closed by the interpreter's signal and this only
        // enforces the deadline behind it. Without one, output quiescence is what completes a
        // turn the prompt heuristic missed.
        if self.script.is_none() {
            if self.tools.active() {
                self.close_turn_if_paid_for().await;
            } else if let Some(turn) = self.framer.poll_quiescence(now) {
                self.finish_turn(turn).await;
            }
        }

        // Watchdog: we wrote a line and heard nothing at all. Without this a frotz
        // that swallows a line would wedge the gate shut forever.
        if let Some(wrote_at) = self.wrote_at {
            if self.script.is_none()
                && self.framer.is_open()
                && self.framer.buffered_len() == 0
                && wrote_at.elapsed() >= self.cfg.script_timeout
            {
                warn!("no output after writing to frotz; reopening the input gate");
                self.framer.reset();
                self.ready = true;
                self.wrote_at = None;
                self.pump().await;
            }
        }

        // Scripted sequences must not hang forever.
        let expired = self
            .script
            .as_ref()
            .map(|s| now >= s.deadline)
            .unwrap_or(false);
        if expired {
            let label = self
                .script
                .as_ref()
                .map(|s| s.label.clone())
                .unwrap_or_default();
            self.abort_script(&format!("{label} timed out")).await;
        }
    }

    /// Feed bytes into the scripted sequence in flight, advancing while the
    /// current stage's expectation is already satisfied — so a single read that
    /// contains several markers is handled in one pass.
    async fn advance_script(&mut self, bytes: &[u8]) {
        let Some(script) = self.script.as_mut() else {
            return;
        };
        script.buf.extend_from_slice(bytes);

        if let Some(marker) = script
            .fail_markers
            .iter()
            .find(|marker| contains(&script.buf, marker.as_bytes()))
            .copied()
        {
            let message = format!("{} failed: frotz said `{marker}`", script.label);
            self.abort_script(&message).await;
            return;
        }

        loop {
            // Decide under a short-lived borrow, then act outside it.
            let next = {
                let Some(script) = self.script.as_mut() else {
                    return;
                };
                let Some(stage) = script.stages.get(script.cursor) else {
                    return;
                };
                let satisfied = match stage.expect {
                    None => true,
                    Some(Expect::Contains(needle)) => contains(&script.buf, needle.as_bytes()),
                    Some(Expect::Prompt) => buffer_ends_with_prompt(&script.buf),
                    Some(Expect::Command(name)) => script.last_command.as_deref() == Some(name),
                };
                if !satisfied {
                    None
                } else {
                    let send = stage.send.clone();
                    script.cursor += 1;
                    script.deadline = Instant::now() + self.cfg.script_timeout;
                    Some((send, script.cursor >= script.stages.len()))
                }
            };

            let Some((send, finished)) = next else {
                return;
            };
            if !send.is_empty() && self.write_lines(&send).await {
                // Announced like any other command, because the screen shows machinery too
                // (issue #20): the `save` and the filename it answers with are what a real
                // screen would have shown, and they are the whole reason the prompt that
                // follows makes sense. `dispatch` is not used here — a scripted stage must
                // not touch the turn gate, since the framer never sees this output and so
                // nothing would ever close the turn it opened.
                for line in &send {
                    let _ = self
                        .evt_tx
                        .send(GameEvent::InputDispatched {
                            line: line.clone(),
                            source: InputSource::Scripted,
                        })
                        .await;
                }
            }
            if finished {
                if self.script.as_ref().map(|s| s.tool_save).unwrap_or(false) {
                    // The last line is sent. What is left is the interpreter saying it wrote
                    // the file, and waiting for that is the point of the channel: the artifact
                    // decides, and it decides in a frame rather than in a spelling. The
                    // deadline above is what stops the wait becoming a hang.
                    if let Some(script) = self.script.as_mut() {
                        script.awaiting_saved = true;
                        script.deadline = Instant::now() + self.cfg.script_timeout;
                    }
                    return;
                }
                self.complete_script().await;
                return;
            }
        }
    }

    async fn complete_script(&mut self) {
        let Some(script) = self.script.take() else {
            return;
        };

        // Success is the artifact, not a line of text. The save file is the one thing that
        // cannot be worded differently by a different story, and matching the game's own
        // success line is what produced a table of per-game spellings that twice reported a
        // checkpoint that had not happened and twice timed out on one that had.
        //
        // Which artifact depends on the path: without a tool channel the save is staged and
        // then renamed into place, so the staged file is what has to exist; with one the
        // interpreter was told the final path and wrote it directly.
        //
        // **With the channel, the interpreter's own word is taken first** (02_game_boundary.md §8 (`G41`–`G44`) rule 2,
        // §10.8 step 3). The file alone cannot tell a save that happened from one that failed on
        // top of a checkpoint taken earlier in the evening: the old bytes are still there, so the
        // check passes and the pane is told a position is safe that is not. The frame that says
        // `ok`, and how many bytes it wrote, is a fact about *this* save — and it is cleared when
        // the sequence starts, so it can only ever be this one's.
        // Asked with the channel question rather than the profile one: a checkpoint that ran through
        // the channel has an interpreter's word to be judged by, and judging it by the file alone is
        // the weaker test this arm exists to replace. The two agree except when a frame has already
        // arrived and `Hello` has not.
        let acked = if self.tools.has_a_channel() {
            match self.tools.saved.as_ref() {
                Some(ToolEvent::Saved { ok: true, bytes, .. }) if *bytes > 0 => None,
                Some(ToolEvent::Saved { ok, bytes, path, .. }) => Some(format!(
                    "the interpreter did not write the checkpoint: ok={ok}, {bytes} bytes, at {} — 
                     whatever is on disk is from an earlier save",
                    path.display()
                )),
                _ => Some(
                    "the interpreter never said it wrote the checkpoint; whatever is on disk is from \
                     an earlier save"
                        .to_string(),
                ),
            }
        } else {
            None
        };
        let artifact = script.staged_path.as_ref().or(script.final_path.as_ref());
        let written = artifact
            .map(|path| {
                std::fs::metadata(path)
                    .map(|meta| meta.len() > 0)
                    .unwrap_or(false)
            })
            .unwrap_or(false);
        if let Some(reason) =
            acked.or_else(|| (!written).then(|| "no save file was written".to_string()))
        {
            let message = format!(
                "{} failed: {reason}; frotz last said: {:?}",
                script.label,
                last_words(&script.buf),
            );
            error!("{message}");
            self.notice("checkpoint_failed", message.clone()).await;
            if let Some(reply) = script.fail_reply {
                let _ = reply.send(Err(anyhow!(message)));
            }
            self.resume_after_failure(false).await;
            return;
        }

        // Promote the staged checkpoint into place.
        if let (Some(staged), Some(final_path)) = (&script.staged_path, &script.final_path) {
            if let Err(err) = std::fs::rename(staged, final_path) {
                let message = format!(
                    "checkpoint was written but could not be committed to {}: {err}",
                    final_path.display()
                );
                error!("{message}");
                self.notice("checkpoint_failed", message.clone()).await;
                if let Some(reply) = script.fail_reply {
                    let _ = reply.send(Err(anyhow!(message)));
                }
                self.resume_after_failure(false).await;
                return;
            }
            debug!(path = %final_path.display(), "checkpoint committed");
        }

        match script.after {
            AfterScript::Nothing(reason) => {
                self.since_save = 0;
                let stem = self.game.stem.clone();
                let meta = self.record_save(&stem, &script.reason_label).await;

                // The save the interpreter wrote is already where the player named it, so all
                // that is left is to refresh the game's own checkpoint from it — one sequence
                // through the interpreter, one file written by it, and the name a snapshot of
                // what it produced rather than a second write path beside it (§5.3).
                let mut kept: Result<Option<String>, String> = Ok(None);
                if let Some(name) = script.slot.clone() {
                    match self.settle_the_checkpoint(&stem, &name) {
                        Ok(path) => {
                            info!(path = %path.display(), name = %name, "the named save is in place");
                            kept = Ok(Some(name));
                        }
                        Err(err) => {
                            // The file the player named is written and correct — what failed is
                            // the checkpoint underneath it, so the position the *core* resumes
                            // from is stale rather than lost. Saying so in the pane that asked is
                            // the whole of §2.6, and this is the one failure that has to be said:
                            // a `/restore` with no name would otherwise go somewhere else.
                            let message = format!(
                                "`{name}` is saved, but the game's own checkpoint could not be \
                                 refreshed from it: {err}"
                            );
                            error!("{message}");
                            self.notice("checkpoint_failed", message.clone()).await;
                            kept = Err(message);
                        }
                    }
                }

                if script.slot.is_some() && kept.is_ok() {
                    // The interpreter's own save target is a *setting* and it persists
                    // (`dtool.c`'s `save_target`), so the name has to be given back: otherwise the
                    // next `save` a story runs — the player or the companion typing it — would
                    // land on the file the player named and merge a later position into it, which
                    // is the one thing naming a save exists to prevent (§5.3).
                    self.point_the_save_file_back().await;
                }

                match &kept {
                    Ok(Some(name)) => {
                        // The reason stays in its own brackets, exactly as it does below, because
                        // that bracket is the core's signal that `/save` has already answered the
                        // pane that asked — and it is the core's reply, not this broadcast, that
                        // names the file (§2.6). Without the bracket the asking pane reads the
                        // same sentence twice.
                        self.notice(
                            "checkpoint",
                            format!("saved {name} ({}){meta}", script.reason_label),
                        )
                        .await;
                    }
                    Ok(None) => {
                        // The reason stays in its own brackets: the core treats "(manual)" as its
                        // signal that `/save` has already answered the pane that asked, and a
                        // longer sentence with the run in it must not break that.
                        self.notice(
                            "checkpoint",
                            format!("saved {stem} ({}){meta}", script.reason_label),
                        )
                        .await;
                    }
                    Err(_) => {}
                }
                if reason == SaveReason::Auto {
                    // §5.4: an auto-save snapshots the game *and* the agent, so
                    // the agent is told to flush its notebook for this moment.
                    //
                    // The turn is in the line because the autosave is every few turns and a
                    // world restored from it can be several moves behind the conversation: the
                    // companion has to be able to say where the checkpoint sits, and it can only
                    // get that from here or from the record beside the file (05_memory.md §5 (`M23`–`M26`)).
                    let turns = self.cfg.autosave_turns;
                    self.notice(
                        "autosave",
                        format!(
                            "{stem} saved after {turns} turns, at turn {}",
                            self.turn_counter
                        ),
                    )
                    .await;
                }
                if let Some(reply) = script.fail_reply {
                    let _ = reply.send(match kept {
                        Ok(_) => Ok(()),
                        Err(message) => Err(anyhow!(message)),
                    });
                }
                self.ready = true;
                // Boxed: `pump` can reach this point again through a scripted
                // sequence, and an unboxed cycle would be infinitely sized.
                Box::pin(self.pump()).await;
            }
            AfterScript::Switch(game, reply) => {
                self.since_save = 0;
                let result = self.start_game(&game, WhereToRestore::Canonical).await;
                let _ = reply.send(result);
            }
            AfterScript::Shutdown => {
                info!("checkpoint complete; shutting down");
                self.finish_shutdown().await;
            }
        }
    }

    async fn abort_script(&mut self, message: &str) {
        let Some(script) = self.script.take() else {
            return;
        };

        // Say what frotz actually printed. Without this a marker mismatch reads as a
        // bare "checkpoint timed out", which is exactly how the `O.K.` bug hid.
        let message = format!("{message}; frotz last said: {:?}", last_words(&script.buf));

        error!("{message}");
        self.notice("checkpoint_failed", message.clone()).await;

        // Only answer frotz if it actually asked something. Measured: when the save had
        // in fact succeeded and we merely failed to recognise the wording, the empty
        // line pushed "to dismiss a filename prompt" became a junk command and the game
        // answered `What?` — which is what the player saw.
        let waiting_for_a_filename = !buffer_ends_with_prompt(&script.buf)
            && contains(&script.buf, save::SAVE_PROMPT_MARKER.as_bytes());

        // A switch does not depend on the checkpoint it tried to take on the way out.
        //
        // Measured, 2026-09-20: `border_zone` was sitting on its **end-of-story menu** — a
        // `read_line` behind a `>`, which no structural signal can tell from a command prompt
        // (§10.4) and which therefore accepted nothing — so the story's own `save` never ran, the
        // sequence timed out, and the switch never happened. `/switch bureaucracy` answered *"the
        // game actor did not answer the switch request"* and the session stayed on a finished game:
        // the player could not leave it. The reason the reply is not sent by the code below is that
        // a switch's reply lives in `after` rather than in `fail_reply`, so on the failure path
        // nobody held it at all.
        //
        // The checkpoint already on disk still stands (which is what the deferral path says too),
        // so the switch goes ahead and the pane is told nothing new was written.
        if let AfterScript::Switch(game, reply) = script.after {
            warn!(
                stem = %game.stem,
                "the checkpoint on the way out failed; switching anyway on the previous one"
            );
            self.framer.reset();
            let _ = reply.send(self.start_game(&game, WhereToRestore::Canonical).await);
            return;
        }

        if let Some(reply) = script.fail_reply {
            let _ = reply.send(Err(anyhow!(message.clone())));
        }

        // And the failure proves something worth keeping: the story did not take a command at this
        // prompt, so it is not a command prompt — whatever it looks like. Left standing, every later
        // checkpoint spends its own five seconds failing in exactly the same way (and, before the
        // switch path above existed, cost the player the switch too). A turn that ends at frotz's own
        // prompt clears it again, so this is a note about where the story is now rather than a
        // verdict about the story.
        self.last_turn_at_prompt = false;

        self.resume_after_failure(waiting_for_a_filename).await;
    }

    /// Recover after a scripted sequence went wrong.
    ///
    /// `waiting_for_a_filename` decides whether a dismissal line is needed: a failed
    /// `save` can leave frotz holding a filename prompt we are never going to answer, and
    /// then the gate would deadlock. When frotz is already sitting at its own prompt there
    /// is nothing to dismiss, so nothing is written.
    async fn resume_after_failure(&mut self, waiting_for_a_filename: bool) {
        self.framer.reset();
        self.ready = false;

        if !waiting_for_a_filename {
            debug!("script failed with frotz at its prompt; reopening the gate, writing nothing");
            self.ready = true;
            self.wrote_at = None;
            // Boxed: `pump` can reach this point through a scripted sequence, and an
            // unboxed cycle would be infinitely sized.
            Box::pin(self.pump()).await;
            return;
        }

        debug!("frotz is waiting for a filename; dismissing the prompt");
        let now = Instant::now();
        self.wrote_at = Some(now);
        self.framer.note_input("", now);
        self.write_lines(&[String::new()]).await;
    }

    // -- turns --------------------------------------------------------------

    /// Was this turn the core's own doing, rather than anybody's move?
    ///
    /// The whole of the rule, in one place, taking no `self` — it is a function of facts the game
    /// already tracks, and it deserves to be readable and testable on its own. See
    /// [`Turn::machinery`] for the two ways it happens.
    ///
    /// The point of both is that **nothing here reads a word of the game's output.** The first
    /// case is known because the core wrote the line; the second because the core knows it
    /// resumed from a checkpoint and nobody has typed since. This replaces a table of save
    /// wordings (`Okay, done.`, `[Okay.]`, `O.K.`) which was a table of one interpreter's
    /// phrasing in one language — and this component has to play any story, in any language,
    /// through any interpreter (02_game_boundary.md §4 (`G16`–`G24c`)).
    fn is_machinery(
        source: InputSource,
        input_written: bool,
        restored: bool,
        progress_made: bool,
    ) -> bool {
        // The core typed the line itself: a checkpoint's `save` and its filename, or the
        // screen-filling `look` it issues after a restore. The `input_written` guard is
        // load-bearing, not decoration: `current_source` is only ever *set* by a real
        // dispatch, so a turn that closed with nothing written is carrying the field's
        // initial value rather than the core's doing — and reading that as machinery filed
        // the opening text of a fresh boot as housekeeping: no turn, no title learned from
        // it, and no companion looking at the game it just watched start. (Measured: the
        // same intro was machinery in a throwaway container and a turn in a live session.
        // That inconsistency was this clause.)
        if input_written && source == InputSource::Scripted {
            return true;
        }

        // Somebody wrote a line, so this output was asked for. The question is *not* whether the
        // line had words in it: `press_key` writes an empty line on purpose, because that is what
        // the interpreter reports to the game as a single carriage return — and a companion
        // answering a "press any key" prompt is not the interpreter volunteering a restore
        // confirmation. Reading this as `was_command` filed every keystroke as machinery, and a
        // machinery turn never reaches the agent: the game would move on and the companion would
        // be looking at a screen nobody had told it about.
        if input_written {
            return false;
        }

        // Nothing was written, nobody has played yet, and the game came back from a checkpoint —
        // so what was just printed is the interpreter confirming the restore, not the game
        // starting. A *fresh* start is deliberately not machinery: that output is the opening
        // text, which is the one place a title is allowed to come from.
        restored && !progress_made
    }

    async fn finish_turn(&mut self, framed: FramedTurn) {
        let was_command = !framed.input.trim().is_empty();
        // Whether anybody asked for this output is a different question from whether the line
        // had words in it. See `is_machinery` for what confusing the two cost.
        let asked_for = framed.input_written;
        let source = self.current_source;
        let output = framed.output_text();

        let machinery =
            Self::is_machinery(source, asked_for, self.status.restored, self.progress_made);
        if !machinery {
            self.turn_counter += 1;
        }

        // Read the title only from a turn nobody asked for that is not machinery. The first
        // thing printed after a restore is the interpreter's confirmation, and reading that
        // as the game's *name* is how this session came to be called "Okay, done.".
        let mut title_changed = false;
        if !machinery && self.status.title.is_none() && !was_command {
            if let Some(title) = learn_title(&output) {
                self.status.title = Some(title);
                title_changed = true;
            }
        }

        // The parser's answer to this turn, read **before** the turn above takes the facts.
        //
        // The first version of this asked afterwards, and `Turn` had already `take`n them — so the
        // detector was told `None` on every turn, read it as "no word was accepted", and counted
        // *barrenness* on a treadmill of perfectly ordinary commands. It was caught by playing: the
        // Wishbringer run reported `place+barren (2)` at a poodle that had answered every command,
        // and barren cannot be true there. A signal that is always on is not a signal, it is a
        // constant, and it makes the detector fire at half the evidence it was designed to need.
        //
        // "Accepted" means the story understood, not that it found words: a line the dictionary does
        // not have is the one case §10.4 actually names, and `florble` counting as accepted would be
        // the opposite of the fact.
        let accepted = self
            .tools
            .parse
            .as_ref()
            .map(|facts| facts.words > 0 && facts.unknown().is_empty());

        let mut turn = Turn {
            input: framed.input,
            output,
            elapsed_ms: framed.elapsed_ms,
            turn: self.turn_counter,
            awaiting_command: true,
            machinery,
            parser: self.tools.parse.take().map(|facts| facts.describe()),
            // The screen belongs to the turn it was asked for in: a turn that drew a form is
            // the turn that says so, and the next turn's output is not a correction of it.
            screen: self.tools.screen.take().and_then(|facts| facts.text()),
            // Filled in below, from the status the interpreter reported for *this* turn: at this
            // point the status of the turn that just ended is the one in hand, and the turn itself
            // is not the place to read it from.
            location: None,
            score: None,
            moves: None,
            hours: None,
            minutes: None,
        };
        debug!(
            turn = turn.turn,
            input = %turn.input,
            bytes = turn.output.len(),
            ended_by = ?framed.ended_by,
            elapsed_ms = turn.elapsed_ms,
            "turn framed"
        );

        self.note_progress(&turn, accepted).await;

        self.ready = true;
        self.wrote_at = None;
        // The first turn is behind us, which is the moment to name the save file: doing it at
        // startup would put the commands' reprinted prompts inside the story's opening turn,
        // where they would arrive after the turn's own frame and be attributed to it.
        if self.tools.active() && !self.tools.targets_named {
            self.name_the_save_file().await;
            if !self.write_tool(&[tool::stats()]).await {
                warn!("could not ask the interpreter about its channel");
            }
            self.tools.targets_named = true;
        }
        self.last_turn_at_prompt = if self.tools.active() {
            // Asked, not deduced: the interpreter says whether it wants a line or a key.
            self.tools.awaiting_command
        } else {
            framed.ended_by == EndReason::Prompt
        };
        self.note_confirmation_state(&turn.output);

        // Issue #19. A turn that ended without frotz's `>` prompt means the game is waiting
        // for something that is not a command, and the words are the *story's* — a chapter
        // menu, a licence form, frotz's own `Are you sure?`. Nothing here can read them and
        // no wording table could cover them, which is why this is reported rather than
        // interpreted: the companion is the only thing in the system that can answer, and
        // the checkpoint stands down until it has.
        turn.awaiting_command = self.at_command_prompt();

        // A keystroke counts as progress even though it is not a command. Somebody pressed
        // something and the game moved, and a story that opens by asking questions (issue #19)
        // is played *entirely* this way. Counting only commands meant such a session could
        // never reach its first checkpoint, because `can_checkpoint` needs progress.
        if asked_for && source != InputSource::Scripted {
            self.progress_made = true;
            // A checkpoint that came due on the last turn is taken here, at the top of the next
            // turn somebody asked for — which is the turn *after* the one whose output the story
            // and the companion have now both seen. The order matters more than the timing rule
            // reads: the checkpoint's first act is to write a line of the harness's own into the
            // game, and the case that punishes writing it early is the one screen the harness
            // cannot tell from a command prompt (`G35`) — a story asking whether to restart,
            // restore or quit, behind an ordinary `>` banner. Measured 2026-10-02: a companion fell
            // off Wishbringer's cliff, the auto-checkpoint came due on that very turn, its `save`
            // went into the death menu one second later and the story left — the evening ended
            // before the companion had been shown the screen, and the friend it was playing with
            // was never asked. Waiting a turn costs nothing and gives the conversation the first
            // word, which is the whole shape of this thing: the machinery does not answer the game,
            // the people at the keyboard do.
            if std::mem::take(&mut self.auto_checkpoint_due) {
                self.pending.push_back(Op::Checkpoint {
                    reason: SaveReason::Auto,
                    reply: None,
                    slot: None,
                });
            }
            // An autosave from a position the story will not move out of is worse than none, and
            // the notice already told the companion that checkpoints are off while this lasts, so
            // the cadence is skipped rather than queued and deferred.
            if self.cfg.autosave_turns > 0 && !self.detector.stuck() {
                self.since_save += 1;
                if self.since_save >= self.cfg.autosave_turns {
                    // Counted as soon as it is owed, not when it commits: a checkpoint can wait
                    // several turns for the game's prompt, and counting past the threshold would
                    // owe a second one.
                    self.since_save = 0;
                    self.auto_checkpoint_due = true;
                }
            }
        }

        if title_changed {
            let status = self.status.clone();
            let _ = self.evt_tx.send(GameEvent::GameChanged(status)).await;
        }
        // Where the interpreter says the player now is (B4). Its own object number for the room,
        // out of the status it just reported, which is the one place identity in this program that
        // is not a reading of the story's prose: the turn record's fingerprint is an index for
        // finding similar turns, and a turn that did not move prints something too. `None` on a
        // story that keeps no status line, which is *cannot say* and not a place.
        //
        // The rest of the same report travels with it, so that the numbers a player can see on row
        // one of the screen are readable from the turn itself: they are drawn by cursor addressing
        // and appear in no line of the game's output, which is why nothing could show them until
        // the interpreter was asked for them (`G21`, `G23`).
        turn.location = self.tools.status.location.map(i64::from);
        turn.score = self.tools.status.score;
        turn.moves = self.tools.status.moves;
        turn.hours = self.tools.status.hours;
        turn.minutes = self.tools.status.minutes;
        let _ = self.evt_tx.send(GameEvent::Turn(turn)).await;
        // A checkpoint that was waiting for the game to be ready gets its chance
        // before anything new is pumped.
        self.retry_deferred_checkpoints().await;
        self.pump().await;
    }

    async fn dispatch(&mut self, line: String, source: InputSource) {
        // A story the harness has counted as going nowhere does not get the same command again
        // **from the same place**.
        //
        // This is the only *stop* in the stuck rule (02_game_boundary.md §7 (`G39.3`)) and it is deliberately the
        // narrowest one available: the harness refuses a line the companion has already tried **from
        // where it is standing** — the same string, from the same place, in a world that has not
        // moved — and lets everything else through. A different idea is the entire way out, so this
        // cannot be a lock; it is a refusal to help somebody walk into the same wall again.
        //
        // **The place is the fingerprint, and it has to be.** A story may keep one location object for
        // a whole maze and vary only its prose — Wishbringer's Steep Trail is six rooms whose status
        // line says `245` — so "from here" is the detector's own idea of a place, which is the one the
        // other signals count. Measured 2026-10-01: with the rule asking the whole window instead, a
        // companion that had just worked the spiral out could not type the first move of it.
        //
        // **Reported as `input_refused` and not as `stuck`, and that is the whole of what makes it
        // safe to do.** A line written into the game is a turn somebody is waiting for, and the
        // daemon waits for it: it holds every reply until the turn it believes is in flight lands.
        // `stuck` is a verdict about the story, and the daemon's answer to one is to offer the
        // companion a turn to think — a reply, held on exactly that wait. A refusal is the opposite
        // fact: no turn is coming at all, and the daemon has a handler for it, which stops waiting.
        // Measured 2026-09-24: a refused `down` was reported as `stuck`, the daemon went on waiting
        // for a turn that could not arrive, no model call was made again, and the session was silent
        // with nothing on any screen to say why.
        // **Never the player** (`G39.3`), and that clause was written before this code was: the player
        // is the one person entitled to decide that this wall is worth walking into again. Measured
        // 2026-10-01, the code refused them as well, and on Wishbringer's Steep Trail that was the whole
        // of why a wedged sitting could only be recovered with a restore.
        if self.detector.stuck()
            && self.detector.already_tried(&line)
            && matches!(source, InputSource::Agent)
        {
            warn!(line = %line, source = source.label(), "refusing a repeat from the same place while the story is stuck");
            self.notice(
                "input_refused",
                format!(
                    "`{line}` was not sent again: you have already tried that from where you are \
                     standing, and the harness reads the play as repeating. Sending it once more from \
                     here cannot help. Something different is needed — from the companion, a fresh \
                     reading of the screen and another approach; from the player, whatever they think \
                     of, or a decision about what to do instead."
                ),
            )
            .await;
            return;
        }

        // At a character prompt, a *short* input is the key the story is waiting for, and a long one
        // is refused (02_game_boundary.md §4.2–§4.3 (`G20`–`G24`)).
        //
        // Measured, 2026-09-20, on Bureaucracy's licence form: the story is inside `read_char`, so
        // the first character of a line answers the prompt and the rest are typed into whatever the
        // story counts next. `Fillmore`, sent as a line, put `ILLMORE` in the field — a confident,
        // wrong answer, and the worst kind of failure, because nothing says so and the companion
        // goes on believing its command is in flight.
        //
        // So the line is not refused for being a line: an **empty** line is Return — the universal
        // answer, and exactly what a `[Press any key to begin.]` page wants — and a single character
        // is that key. Both are written as they are, and the interpreter hands them to the story as
        // keystrokes. Refusing them would refuse the answer, which is what a first attempt at this
        // guard did: it turned the companion's empty `type` into a loop of notices while the page sat
        // there waiting.
        //
        // A longer line is refused rather than converted, because converting would be the harness
        // guessing how many characters a prompt wants — and on a form the first character is eaten
        // by the prompt itself, which is the difference between `Fillmore` and `ILLMORE`. Deciding
        // that is the companion's, one prompt at a time. `Forced` and `Scripted` are exempt: those
        // are the player's own `/pressreturn` and `/presskey`, and the core's own scripts.
        if self.tools.active()
            && !self.tools.awaiting_command
            && matches!(source, InputSource::Agent | InputSource::Player)
            && line.chars().count() > 1
        {
            warn!(
                line = %line,
                source = source.label(),
                "the game wants a key; refusing a line so it cannot be typed into the prompt"
            );
            self.notice(
                "key_wanted",
                format!(
                    "nothing was sent: the game is waiting for a key rather than a command, and \
                     `{line}` would have been typed into the prompt one character at a time. Answer \
                     what it is asking with a key — `press_key` — or, from a pane, `/pressreturn` \
                     or `/presskey {line}`."
                ),
            )
            .await;
            return;
        }

        if self.write_lines(std::slice::from_ref(&line)).await {
            self.framer.note_input(&line, Instant::now());
            self.current_source = source;
            self.ready = false;
            self.wrote_at = Some(Instant::now());
            let _ = self
                .evt_tx
                .send(GameEvent::InputDispatched { line, source })
                .await;
        }
    }

    /// Write lines to frotz as whole units. Returns whether anything was written.
    async fn write_lines(&mut self, lines: &[String]) -> bool {
        let Some(frotz) = self.frotz.as_mut() else {
            return false;
        };
        for line in lines {
            if let Err(err) = frotz.write_line(line).await {
                error!(error = %err, line = %line, "failed to write to frotz");
                return false;
            }
            debug!(line = %line, "wrote to frotz");
        }
        true
    }

    /// Write tool commands (03_session.md §9–§10 (`S58`–`S76`)).
    ///
    /// Deliberately not `write_lines`: a command is not a turn, and going through the same
    /// path would open one in the framer that nothing would ever close. It *is* announced the
    /// way a scripted line is, because a checkpoint is machinery the screen shows (issue #20) —
    /// the pane should see the core name the file, exactly as it used to see the staged
    /// filename answered — and because the session log is where "the core drove a save" has to
    /// be visible to anything checking that a checkpoint was honestly taken.
    async fn write_tool(&mut self, commands: &[String]) -> bool {
        let Some(frotz) = self.frotz.as_mut() else {
            return false;
        };
        for command in commands {
            if let Err(err) = frotz.write_tool_command(command).await {
                error!(error = %err, command = %command, "failed to write a tool command");
                return false;
            }
            debug!(command = %command, "wrote a tool command");
        }
        for command in commands {
            let _ = self
                .evt_tx
                .send(GameEvent::InputDispatched {
                    line: command.clone(),
                    source: InputSource::Scripted,
                })
                .await;
        }
        true
    }

    /// Tell the interpreter where this game's saves and restores go.
    ///
    /// This is the per-game save folder (03_session.md §9–§10 (`S58`–`S76`)) done through the channel instead of through the
    /// interpreter's filename prompt (03_session.md §9–§10 (`S58`–`S76`)): the host names the file, so there is no
    /// arithmetic about how a restricted directory rewrites a basename, no overwrite question
    /// to answer, and no success wording to recognise — and a story's own `save`, typed by the
    /// player, lands in the same place as the core's checkpoint.
    async fn name_the_save_file(&mut self) {
        let Some(profile) = self.tools.profile.as_ref() else {
            return;
        };
        // Asked rather than assumed: an interpreter that does not offer `save` gets the old
        // path — the filename prompt — rather than a command it would have to refuse.
        if !profile.offers("save") {
            info!("this interpreter has no `save` tool; saves go through its filename prompt");
            return;
        }
        let checkpoint = self.cfg.state.checkpoint(&self.game.stem);
        let commands = vec![
            tool::save_target(&checkpoint),
            tool::restore_target(&checkpoint),
        ];
        if !self.write_tool(&commands).await {
            warn!(stem = %self.game.stem, "could not name the save file to the interpreter");
        }
    }

    /// Put the interpreter's own save target back on the game's checkpoint (§5.3).
    ///
    /// The counterpart of a named save, which points the interpreter at the file the player named
    /// so that the screen shows that name. The setting outlives the save — `dtool.c`'s
    /// `save_target` is sticky until it is set again — so without this the next `save` a *story*
    /// runs would overwrite the position the player put aside.
    ///
    /// Deliberately silent, unlike [`Self::name_the_save_file`], which announces its two commands
    /// because they are what makes the boot screen's `save` make sense. This one is the same
    /// setting being put back, nobody typed it, and announcing it would print
    /// `wishbringer.sav` seconds after the player asked for `00-poodle` — which is precisely the
    /// confusion this pair of changes exists to remove.
    async fn point_the_save_file_back(&mut self) {
        let checkpoint = self.cfg.state.checkpoint(&self.game.stem);
        let Some(frotz) = self.frotz.as_mut() else {
            return;
        };
        if let Err(err) = frotz
            .write_tool_command(&tool::save_target(&checkpoint))
            .await
        {
            warn!(error = %err, "could not point the save file back at the checkpoint");
        }
    }

    // -- lifecycle ----------------------------------------------------------

    async fn start_game(&mut self, game: &GameInfo, restore: WhereToRestore) -> Result<GameStatus> {
        // Another story, another attempt, or the same game standing somewhere else: none of the
        // evidence the detector is holding is true of the world that is about to be loaded, and the
        // verdict would otherwise follow a session across a switch or a restore (02_game_boundary.md §4.3 (`G24`)).
        self.detector.forget();
        if let Some(frotz) = self.frotz.take() {
            let _ = frotz.shutdown().await;
        }
        self.events_rx = None;
        self.script = None;
        self.queue.clear();
        self.ready = false;
        self.wrote_at = None;
        self.since_save = 0;
        self.auto_checkpoint_due = false;
        self.progress_made = false;
        self.last_turn_at_prompt = false;
        self.awaiting_answer = false;
        self.drop_deferred_checkpoints("the game changed before it could be saved");
        self.framer.reset();
        // The story's opening text arrives before anybody has written a line, so the turn it
        // makes is opened here rather than by its own output (see `begin_unsolicited`).
        self.framer.begin_unsolicited(Instant::now());
        // A different game is a different channel: the next interpreter introduces itself
        // again, and nothing carried over from the last one may be believed.
        self.tools.reset();

        save::ensure_game_dir(&self.cfg.state, &game.stem)?;
        let restore_path = match restore {
            WhereToRestore::Fresh => None,
            WhereToRestore::Canonical => the_newest_save(&self.cfg.state, &game.stem),
            WhereToRestore::Slot(path) => Some(path),
        };
        // The interpreter reports this restore over the tool channel like any other, and it is the
        // one frame the core speaks for in its own words instead (see `boot_restore`).
        self.boot_restore = restore_path.clone();
        if let Some(path) = &restore_path {
            info!(
                stem = %game.stem,
                save = %path.display(),
                "coming back to the newest position this game has"
            );
        }

        save::ensure_notebook(&self.cfg.state, &game.stem, game.version)?;
        games::write_current(self.cfg.state.root(), &game.stem)?;

        let cfg = FrotzConfig {
            program: self.cfg.dfrotz.clone(),
            game: game.path.clone(),
            exit_grace: self.cfg.exit_grace,
            restore: restore_path.clone(),
            // The one directory frotz may write through a filename prompt. It is the game's
            // own save folder now, so a filename the player types at a prompt lands beside
            // this game's checkpoint rather than in a heap of every game's saves.
            restricted: Some(self.cfg.state.game_saves_dir(&game.stem)),
            tools: self.cfg.tools,
        };
        let (frotz, events_rx) = Frotz::spawn(&cfg).await?;
        self.frotz = Some(frotz);
        self.events_rx = Some(events_rx);
        self.game = game.clone();
        self.status = GameStatus {
            stem: game.stem.clone(),
            title: None,
            version: game.version,
            restored: restore_path.is_some(),
        };

        // The turn number belongs to the game, not to the session (02_game_boundary.md §6 (`G27`–`G34`)).
        //
        // It used to be one counter for the whole process, so `/switch` carried it across: the
        // 2026-09-19 UAT ran wishbringer 1–183, border_zone 183–197, bureaucracy 198–203 and then
        // wishbringer again from 203 — and the companion, handed "turn 185" at a story it had never
        // started, reasoned that it must have "been playing a while". Worse, the number is the
        // citation on every observation and the turn a checkpoint records, so a belief stamped 185
        // could not say which story it was about.
        //
        // A restored game resumes at the number its checkpoint recorded, so a redeploy or a
        // switch back does not renumber the attempt; a game starting fresh starts at nothing.
        //
        // Read from the record beside the file the world actually came from, so restoring a save
        // the player named puts the number where *that* save was, rather than where the game's own
        // checkpoint had got to.
        self.turn_counter = match &restore_path {
            Some(path) => save::read_record_beside(path)
                .map(|record| record.turn)
                .unwrap_or(0),
            None => 0,
        };

        if self.status.restored {
            // frotz restores silently: without this the player would be dropped
            // into a blank prompt with no idea where they are.
            self.pending.push_back(Op::Refresh);
        }

        let _ = self
            .evt_tx
            .send(GameEvent::GameChanged(self.status.clone()))
            .await;
        info!(
            stem = %game.stem,
            restored = self.status.restored,
            "game is up"
        );
        Ok(self.status.clone())
    }

    async fn notice(&self, event: &str, detail: impl Into<String>) {
        let _ = self
            .evt_tx
            .send(GameEvent::System {
                event: event.to_string(),
                detail: detail.into(),
            })
            .await;
    }

    /// Write the note that belongs beside a checkpoint, and describe it in one line.
    ///
    /// The save file is Quetzal: the story's format, readable by any interpreter, and it says
    /// nothing at all about which attempt at the game it belongs to. What makes a checkpoint
    /// mean something to somebody reading back over an evening — to the companion especially,
    /// which has to decide whether a save is worth returning to — is "the second go, about
    /// eighty turns in". So the numbers are written *beside* the file rather than into it, and
    /// the same line is what the checkpoint notice carries, which is how the companion hears
    /// about it at all.
    ///
    /// Returns the suffix for that notice: `, run 2, fork 3, turn 41`.
    async fn record_save(&self, stem: &str, reason: &str) -> String {
        let record = self.save_record_for(stem, reason);
        if let Err(err) = save::write_save_record(&self.cfg.state, stem, &record) {
            warn!(error = %err, "could not write the record beside the checkpoint");
        }
        if record.generation > 1 {
            format!(
                ", run {}, fork {}, turn {}",
                record.run, record.generation, record.turn
            )
        } else {
            format!(", run {}, turn {}", record.run, record.turn)
        }
    }

    /// The record for the checkpoint that has just committed: what a save is *for*, in numbers.
    fn save_record_for(&self, stem: &str, reason: &str) -> save::SaveRecord {
        let bytes = match &self.tools.saved {
            Some(ToolEvent::Saved { bytes, .. }) => *bytes,
            _ => std::fs::metadata(self.cfg.state.checkpoint(stem))
                .map(|meta| meta.len() as i64)
                .unwrap_or(-1),
        };
        let attempt = runs::read(self.cfg.state.root(), stem);
        let run = attempt
            .current
            .as_ref()
            .map(|attempt| attempt.number)
            .unwrap_or(attempt.count);
        let generation = attempt
            .current
            .as_ref()
            .map(|attempt| attempt.generation)
            .unwrap_or(1);
        save::SaveRecord {
            game: stem.to_string(),
            saved_at: crate::seat_protocol::now_rfc3339(),
            reason: reason.to_string(),
            turn: self.turn_counter,
            run,
            generation,
            bytes,
            interpreter: self
                .tools
                .profile
                .as_ref()
                .map(|profile| profile.build.clone()),
            position: self.the_position_of_this_turn(stem),
        }
    }

    /// Where the game is standing, as far as the core can witness it (B7).
    ///
    /// Two facts, and both are already written down by something else. The **room** comes from the
    /// newest turn in the observation record — its fingerprint's own label, which is the first line
    /// the story printed, so the name and the index cannot disagree. The **score** comes from the
    /// interpreter's status line, which is the same number `/bar` shows.
    ///
    /// `None` when the game has not printed a turn yet, or when the turn printed nothing: a save
    /// taken at a blank screen has no place to name, and "in the room called (nothing)" is worse to
    /// hand the companion than not knowing. A missing position is a state the record has always had.
    fn the_position_of_this_turn(&self, stem: &str) -> Option<save::Position> {
        let observation = observations::newest(&self.cfg.state.observations(stem))?;
        // The label in front of the hash *is* the first line of that turn's output, trimmed to a
        // label by `observations::fingerprint` — see the note there. Read off the fingerprint
        // rather than recomputed from the output, so the two cannot say different things.
        let room = observation
            .fingerprint
            .split('#')
            .next()
            .unwrap_or_default()
            .to_string();
        if room.is_empty() {
            return None;
        }
        Some(save::Position {
            room,
            fingerprint: observation.fingerprint.clone(),
            score: self.tools.status.score,
        })
    }

    /// Put the game's own checkpoint where the save the player named left it.
    ///
    /// A copy rather than a second save: the interpreter is driven exactly once, to exactly one
    /// path — the name the player chose, which is why the screen shows that name — and the
    /// checkpoint the rest of the core reads (`/restore` with no argument, the autosave, the resume
    /// at boot) is the same position under the name the core uses.
    ///
    /// The guard is not decoration: a checkpoint and a name that were the same path would make this
    /// `fs::copy` a file onto itself, which truncates it — the one way a save could be destroyed by
    /// a copy that was supposed to preserve it. [`save::new_slot_name`] refuses the stem as a name,
    /// and this is the belt to that pair of braces.
    fn settle_the_checkpoint(&self, stem: &str, name: &str) -> Result<PathBuf> {
        let named = self.cfg.state.named_checkpoint(stem, name);
        let canonical = self.cfg.state.checkpoint(stem);
        if named != canonical {
            std::fs::copy(&named, &canonical).with_context(|| {
                format!("could not refresh the checkpoint from {}", named.display())
            })?;
        }
        save::write_record_beside(&named, &self.save_record_for(stem, "named"))?;
        Ok(named)
    }
}

/// Where a checkpoint's file goes: the name the player gave it, or the game's own checkpoint.
///
/// A function rather than two lines inside `begin_checkpoint`, because it is the whole of the
/// 2026-09-20 UAT's first finding and it has to be pinned by something that runs in a test rather
/// than by a session somebody watched: pointed at the checkpoint, the *screen* said
/// `wishbringer.sav` after `/save 00-poodle`, and a player who cannot see their own name on their
/// own save cannot tell whether it exists at all.
fn checkpoint_target(paths: &StatePaths, stem: &str, slot: Option<&str>) -> PathBuf {
    match slot {
        Some(name) => paths.named_checkpoint(stem, name),
        None => paths.checkpoint(stem),
    }
}

/// The `save` sequence: send `save`, answer the filename prompt, then wait for the prompt
/// that means frotz is ready again.
///
/// Both markers here are the *interpreter's* own words — the filename prompt and, if
/// anything goes wrong, its failure line — and both are pinned against measured bytes in
/// the tests below. There is deliberately no marker for success: that line belongs to the
/// game (`Ok.`, `O.K.`, `[Okay.]`, `Okay, done.` were all measured, and there is no end to
/// the list), so success is decided by the artifact instead — the file on disk — which no
/// story can word differently. See `complete_script`.
fn checkpoint_stages(staged_path: &std::path::Path) -> Vec<Stage> {
    // The **basename**, not the path, and `-R` is the reason: frotz's restricted mode does not
    // check the filename, it replaces everything before the last separator with the restricted
    // directory. Typing the full path would work by accident today and stop working the moment
    // the restricted directory is not the parent — and the save would land somewhere the core
    // then reports as "no save file was written", which is the worst way to find out.
    let name = staged_path
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| staged_path.display().to_string());

    vec![
        Stage {
            expect: None,
            send: vec!["save".to_string()],
        },
        Stage {
            expect: Some(Expect::Contains(save::SAVE_PROMPT_MARKER)),
            send: vec![name],
        },
        Stage {
            expect: Some(Expect::Prompt),
            send: Vec::new(),
        },
    ]
}

/// The stages of a checkpoint taken through the interpreter's own tool channel.
///
/// Two, against the fallback's three — and the count is the whole of the 2026-10-01 shutdown
/// timeout. With a channel the interpreter is *told* the file, writes it, and says so in its own
/// frame (`ToolEvent::Saved`), so there is no filename to type, no prompt to wait for, and nothing
/// to infer. The three-stage script waits for two things a tooled interpreter never prints, which
/// is what the five seconds at every clean stop were.
///
/// Kept beside `checkpoint_stages` and shared by both callers — the mid-evening checkpoint and the
/// one a stop takes — because those two drifting apart is exactly how a stop came to build the
/// fallback while the channel was live and working.
fn channel_checkpoint_stages(final_path: &std::path::Path) -> Vec<Stage> {
    vec![
        Stage {
            expect: None,
            send: vec![tool::save_target(final_path)],
        },
        Stage {
            expect: Some(Expect::Command("save")),
            send: vec!["save".to_string()],
        },
    ]
}

/// Receive from an optional stream: `pending` forever when there is no frotz, so
/// the `select!` branch simply never fires instead of spinning.
async fn recv_optional(rx: &mut Option<mpsc::Receiver<FrotzEvent>>) -> Option<FrotzEvent> {
    match rx {
        Some(rx) => rx.recv().await,
        None => std::future::pending().await,
    }
}

/// The tail of what frotz said, for a failure message.
///
/// Without it a marker mismatch reads as a bare "timed out", which is how the `O.K.` bug
/// hid for so long: the interpreter had saved the game and we were waiting for a word it
/// never writes.
fn last_words(buf: &[u8]) -> String {
    String::from_utf8_lossy(buf)
        .chars()
        .rev()
        .take(200)
        .collect::<String>()
        .chars()
        .rev()
        .collect::<String>()
}

fn contains(haystack: &[u8], needle: &[u8]) -> bool {
    if needle.is_empty() || haystack.len() < needle.len() {
        return false;
    }
    haystack.windows(needle.len()).any(|w| w == needle)
}

/// Learn a display title from the game's own startup text (§5.3).
///
/// A light touch on purpose: the first plausible line, or nothing at all. A wrong
/// guess here would be worse than showing the filename.
fn learn_title(output: &str) -> Option<String> {
    output
        .lines()
        .map(|line| line.trim())
        .find(|line| {
            !line.is_empty()
                && line.len() >= 4
                && line.len() <= 60
                && !line.starts_with('>')
                && !line.chars().all(|c| c == '*' || c == '-' || c == '=')
        })
        .map(|line| line.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A record beside a save file, with only the two fields this question is about.
    fn record(when: &str, turn: u64) -> save::SaveRecord {
        save::SaveRecord {
            game: "wishbringer".to_string(),
            saved_at: when.to_string(),
            reason: "manual".to_string(),
            turn,
            run: 1,
            generation: 1,
            bytes: 852,
            interpreter: None,
            position: None,
        }
    }

    fn scratch(name: &str) -> save::StatePaths {
        let dir = std::env::temp_dir().join(format!("infocompanion-newest-save-{name}"));
        let _ = std::fs::remove_dir_all(&dir);
        let paths = save::StatePaths::new(&dir);
        paths.ensure().unwrap();
        // The game's own folder, which `ensure` does not make: a save is written into it.
        save::ensure_game_dir(&paths, "wishbringer").unwrap();
        paths
    }

    #[test]
    fn booting_comes_back_to_the_newest_save_the_player_has() {
        // B8, and the complaint behind it: with `autosave_turns: 5`, booting from the checkpoint
        // alone lands up to five turns behind the save the player named and walked away from. They
        // named `10-conch2` at turn 200 and the checkpoint still held 195; reopening at 195 reads
        // as the harness having lost the evening.
        let paths = scratch("newest");
        let checkpoint = paths.checkpoint("wishbringer");
        let named = paths.named_checkpoint("wishbringer", "10-conch2");
        std::fs::write(&checkpoint, b"checkpoint").unwrap();
        std::fs::write(&named, b"named").unwrap();
        save::write_record_beside(&checkpoint, &record("2026-09-21T14:00:00Z", 195)).unwrap();
        save::write_record_beside(&named, &record("2026-09-21T15:00:00Z", 200)).unwrap();

        assert_eq!(the_newest_save(&paths, "wishbringer"), Some(named.clone()));

        // And the other way round: an undo the player made on purpose is a position, not an
        // accident — the checkpoint is newer, so the checkpoint is where they come back to.
        save::write_record_beside(&named, &record("2026-09-21T13:00:00Z", 200)).unwrap();
        save::write_record_beside(&checkpoint, &record("2026-09-21T14:00:00Z", 60)).unwrap();
        assert_eq!(
            the_newest_save(&paths, "wishbringer"),
            Some(paths.checkpoint("wishbringer"))
        );

        // A hand-edited stamp is not a time, so it cannot win: it is a file that cannot say when
        // it was taken, not a file that was taken later.
        save::write_record_beside(&named, &record("yesterday afternoon", 200)).unwrap();
        assert_eq!(
            the_newest_save(&paths, "wishbringer"),
            Some(paths.checkpoint("wishbringer"))
        );

        let _ = std::fs::remove_dir_all(paths.root());
    }

    #[test]
    fn a_game_with_no_checkpoint_still_comes_back_to_a_save_it_has() {
        let paths = scratch("named-only");
        let named = paths.named_checkpoint("wishbringer", "incell");
        std::fs::write(&named, b"named").unwrap();
        save::write_record_beside(&named, &record("2026-09-21T15:00:00Z", 40)).unwrap();

        assert_eq!(the_newest_save(&paths, "wishbringer"), Some(named));
        let _ = std::fs::remove_dir_all(paths.root());
    }

    #[test]
    fn a_game_that_has_never_been_saved_starts_at_the_opening_text() {
        // Every game's first evening: no files at all, so there is nothing to come back to and the
        // story starts where a story starts.
        let paths = scratch("nothing");
        assert_eq!(the_newest_save(&paths, "wishbringer"), None);
        let _ = std::fs::remove_dir_all(paths.root());
    }

    #[test]
    fn a_checkpoint_with_no_note_beside_it_is_still_where_they_were() {
        // The record is the harness's note, not the position: a `.sav` committed by a version that
        // wrote no record, or one whose note was lost, is still the last place the game stood — and
        // nothing can be shown to be newer than a time nobody wrote down.
        let paths = scratch("no-note");
        let checkpoint = paths.checkpoint("wishbringer");
        std::fs::write(&checkpoint, b"checkpoint").unwrap();
        let named = paths.named_checkpoint("wishbringer", "incell");
        std::fs::write(&named, b"named").unwrap();
        save::write_record_beside(&named, &record("2026-09-21T23:00:00Z", 40)).unwrap();

        assert_eq!(
            the_newest_save(&paths, "wishbringer"),
            Some(paths.checkpoint("wishbringer"))
        );
        let _ = std::fs::remove_dir_all(paths.root());
    }

    #[test]
    fn save_markers_match_what_dfrotz_actually_prints() {
        // Byte-for-byte output from dfrotz 2.54, captured while probing the
        // interpreter (see `G33`). These markers gate the scripted
        // save sequence, so a mismatch silently breaks every checkpoint.
        let filename_prompt = b">Please enter a filename [advent.qzl]: ";
        assert!(
            contains(filename_prompt, save::SAVE_PROMPT_MARKER.as_bytes()),
            "SAVE_PROMPT_MARKER ({:?}) must appear in {:?}",
            save::SAVE_PROMPT_MARKER,
            String::from_utf8_lossy(filename_prompt)
        );
        assert!(contains(
            b"\nSave failed.\n",
            save::SAVE_FAIL_MARKER.as_bytes()
        ));
    }

    #[test]
    fn a_missing_save_file_is_a_failure_whatever_the_game_said() {
        // The artifact is the only proof, so this is what `complete_script` checks. Measured
        // wordings (Adventure `Ok.`, The Witness `O.K.`, Moonmist `[Okay.]`, Wishbringer
        // `Okay, done.`) are why: a table of spellings is a per-game table, and it twice
        // reported a checkpoint that had not happened.
        let dir = std::env::temp_dir().join("infocompanion-artifact-test");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let staged = dir.join("stage");
        let written =
            |path: &std::path::Path| std::fs::metadata(path).is_ok_and(|meta| meta.len() > 0);

        assert!(!written(&staged), "nothing written == no save");
        std::fs::write(&staged, b"IFZS").unwrap();
        assert!(written(&staged), "a written file is the success");
        // An empty file is not one: a truncated write must not be committed.
        std::fs::write(&staged, b"").unwrap();
        assert!(!written(&staged), "an empty file is a failure");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_finished_save_ends_with_a_prompt_in_the_game_s_own_words() {
        // Measured 2026-10-01, on a real shutdown: the checkpoint script timed out and the core
        // logged `checkpoint shutdown timed out; frotz last said: "Okay, done.\n\n>"` — a buffer
        // that *ends with a prompt*, which is exactly what the third stage waits for. So either the
        // matcher does not recognise this shape or the timeout had another cause, and the difference
        // is the whole of the fix: `"Okay, done."` is one of the answers the comment above
        // `checkpoint_stages` lists as measured with no end to the list, so this shape is the normal
        // one rather than an oddity.
        let finished = b"Okay, done.\n\n>";
        assert!(
            buffer_ends_with_prompt(finished),
            "a save whose answer ends in frotz's prompt satisfies the stage that waits for one"
        );

        // And the same words with the newline frotz sometimes leaves on the prompt line.
        assert!(buffer_ends_with_prompt(b"Okay, done.\n\n>\n"));
        assert!(buffer_ends_with_prompt(b"What next?\n>"));
    }

    #[test]
    fn a_failure_dismisses_the_filename_prompt_only_when_one_is_pending() {
        // The blind empty line was the `What?` the player saw: after a save that had in
        // fact succeeded, the dismissal line arrived as a junk command.
        let waiting = b"What next?\n>Please enter a filename [witness.qzl]: ";
        assert!(
            !buffer_ends_with_prompt(waiting)
                && contains(waiting, save::SAVE_PROMPT_MARKER.as_bytes()),
            "a pending filename prompt must be dismissed"
        );

        let finished = b"What next?\n>Please enter a filename [witness.qzl]: O.K.\n\nWhat next?\n>";
        assert!(
            buffer_ends_with_prompt(finished)
                || !contains(finished, save::SAVE_PROMPT_MARKER.as_bytes()),
            "a finished save must not be answered with a junk line"
        );
    }

    #[test]
    fn contains_finds_substrings_and_survives_edges() {
        assert!(contains(b"abc", b"abc"));
        assert!(!contains(b"abc", b"abcd"));
        assert!(!contains(b"", b"a"));
        assert!(!contains(b"abc", b""));
        // Case matters: frotz's prompt says "enter", not "Enter".
        assert!(!contains(b">Please enter a filename", b"Enter a filename"));
    }

    #[test]
    fn the_confirmation_phrases_frotz_uses_are_recognised() {
        // Measured output from dfrotz 2.54 after `quit`, and after an answer it did
        // not understand. Case is not guaranteed by the interpreter, so it is ignored.
        assert!(is_confirmation("Are you sure you want to quit?"));
        assert!(is_confirmation("Please answer yes or no."));
        assert!(is_confirmation("Do you really want to RESTART?"));
        assert!(!is_confirmation("You are in a valley beside a stream."));
        assert!(!is_confirmation(""));
    }

    #[test]
    fn a_checkpoint_waits_for_a_command_prompt_and_no_pending_question() {
        // The prompt alone is enough when nothing is being asked.
        assert!(prompts_allow_save(true, false));
        // The quit question frames by quiescence, never at a prompt.
        assert!(!prompts_allow_save(false, true));
        assert!(
            !prompts_allow_save(false, false),
            "no turn has completed yet"
        );
        // frotz re-asks behind an ordinary prompt, so a live question outranks it.
        assert!(!prompts_allow_save(true, true));
    }

    #[test]
    fn machinery_is_recognised_without_reading_a_word_of_the_game() {
        // The restore confirmation — the case a wording table used to swallow. `Okay, done.`
        // is one interpreter's phrasing, in one language, and matching it is the bug this
        // rule exists to not have.
        assert!(Actor::is_machinery(InputSource::Player, false, true, false));

        // The core's own line, whoever is nominally credited with it. A checkpoint's `save`
        // looks like a command and is not one.
        assert!(Actor::is_machinery(
            InputSource::Scripted,
            true,
            false,
            true
        ));
        assert!(Actor::is_machinery(InputSource::Scripted, true, true, true));

        // The opening text of a *fresh* boot, where `current_source` has never been set by a
        // dispatch and is still its initial `Scripted` — the clause that filed the intro as
        // housekeeping. Nothing was written, so this is not the core's own line.
        assert!(!Actor::is_machinery(
            InputSource::Scripted,
            false,
            false,
            false
        ));

        // The restore confirmation with that same un-set source: nothing written and the game
        // resumed from a checkpoint, so it is machinery — by the third clause, where it
        // belongs.
        assert!(Actor::is_machinery(
            InputSource::Scripted,
            false,
            true,
            false
        ));

        // A fresh start: the opening text is not machinery. It is where a title comes from.
        assert!(!Actor::is_machinery(
            InputSource::Player,
            false,
            false,
            false
        ));

        // Somebody has played, so a game that speaks on its own later is the game speaking —
        // a knock at the door, a prologue's second act — and not the interpreter settling in.
        assert!(!Actor::is_machinery(InputSource::Player, false, true, true));

        // An ordinary move.
        assert!(!Actor::is_machinery(InputSource::Forced, true, true, true));

        // A keystroke: an empty line the *companion* wrote, as the first thing after a
        // restore. The fourth argument is `input_written`, not "the line had words in it" —
        // and that distinction is the whole point. Read the old way this was machinery, so a
        // companion answering a "press any key" prompt got no turn back and the game moved on
        // without ever telling it.
        assert!(!Actor::is_machinery(InputSource::Agent, true, true, false));
    }

    #[test]
    fn titles_are_learned_but_junk_is_not() {
        assert_eq!(
            learn_title("\n\nWelcome to Adventure!\n(Please type HELP)"),
            Some("Welcome to Adventure!".to_string())
        );
        assert_eq!(learn_title("*****\n======\n"), None);
        assert_eq!(learn_title("\n\n\n"), None);
        // Very long lines are descriptions, not titles.
        assert_eq!(learn_title(&"x".repeat(200)), None);
    }

    #[test]
    fn checkpoint_stages_start_by_sending_save() {
        let staged = StatePaths::new("/state").staged_checkpoint("advent");
        let stages = checkpoint_stages(&staged);
        assert_eq!(stages.len(), 3, "save, the filename it asks for, then done");
        assert!(
            stages[0].expect.is_none(),
            "the first stage fires immediately"
        );
        assert_eq!(stages[0].send, vec!["save".to_string()]);
        assert!(
            matches!(stages[1].expect, Some(Expect::Contains(needle)) if needle == save::SAVE_PROMPT_MARKER),
            "the path is only sent once the interpreter asks for it"
        );
        // The basename, not the path. frotz runs `-R` (restricted read/write), which does not
        // check the filename it is handed — it throws away everything up to the last separator
        // and puts the restricted directory in its place, **truncating the basename** when the
        // restricted path's length minus the basename's lands between zero and the basename's
        // own (measured 2026-09-16; see `StatePaths::staged_checkpoint`). The pinned names are
        // short enough to survive that, and a full path typed here would land somewhere else —
        // the core would then report "no save file was written", the worst possible way to
        // find out.
        assert_eq!(stages[1].send, vec!["stage".to_string()]);
        // And nothing waits on the game's success wording: the file on disk decides
        // (see `complete_script`), because the wording belongs to the story.
        assert!(matches!(stages[2].expect, Some(Expect::Prompt)));
    }

    #[test]
    fn a_channel_checkpoint_is_two_stages_where_the_fallback_is_three() {
        // The count is the diagnosis, so it is pinned. The 2026-10-01 shutdown timeout read
        // `cursor=1 of 3` for a stop taken against a working tool channel: three stages is the
        // fallback — `save`, the filename prompt, the prompt — and a tooled interpreter prints
        // neither of the last two. It is asked of both builders here, because the bug was that the
        // stop used one of them while the interpreter needed the other.
        let final_path = StatePaths::new("/state").checkpoint("wishbringer");
        let channel = channel_checkpoint_stages(&final_path);
        let fallback =
            checkpoint_stages(&StatePaths::new("/state").staged_checkpoint("wishbringer"));

        assert_eq!(channel.len(), 2, "name the file, then the story's own save");
        assert_eq!(
            fallback.len(),
            3,
            "save, the filename it asks for, then done"
        );

        // The interpreter is *told* the file, over the channel, rather than asked for a filename.
        assert_eq!(
            channel[0].send,
            vec![tool::save_target(&final_path)],
            "the target is what the channel carries; the path is never typed at a prompt"
        );
        assert!(channel[0].expect.is_none(), "and it is sent immediately");
        // The story still runs the save itself — a save taken from outside the story records a
        // program counter after the read, with the previous command still in the parse buffer.
        assert!(matches!(channel[1].expect, Some(Expect::Command("save"))));
        assert_eq!(channel[1].send, vec!["save".to_string()]);
    }

    #[test]
    fn a_frame_is_a_channel_even_before_the_handshake_answers() {
        // `active()` asks whether the profile handshake finished; a checkpoint needs to know
        // whether the interpreter can be told a file and write it. On 2026-10-01 a stop was taken
        // in the gap between the two: the interpreter saved and said so, and the core waited five
        // seconds for prompts that interpreter does not print.
        let mut tools = Tools::default();
        assert!(!tools.active() && !tools.has_a_channel());

        tools.channel_seen = true;
        assert!(!tools.active(), "the profile is still unknown");
        assert!(
            tools.has_a_channel(),
            "and the checkpoint can still be taken the channel's way"
        );

        // A game change forgets both: the next interpreter has to say so again.
        tools.reset();
        assert!(!tools.has_a_channel());
    }

    #[test]
    fn a_named_save_is_written_where_the_player_named_it() {
        // The 2026-09-20 UAT, and the reason this is a function with a test rather than two lines
        // inside `begin_checkpoint`: `/save 00-poodle` used to point the interpreter at the game's
        // own checkpoint, so the *screen* — which is where a save becomes visible — said
        // `wishbringer.sav`, and the name the player typed appeared nowhere they could see.
        let paths = StatePaths::new("/state");
        assert_eq!(
            checkpoint_target(&paths, "wishbringer", None),
            PathBuf::from("/state/saves/wishbringer/wishbringer.sav")
        );
        assert_eq!(
            checkpoint_target(&paths, "wishbringer", Some("00-poodle")),
            PathBuf::from("/state/saves/wishbringer/00-poodle.sav")
        );
    }

    #[test]
    fn save_reason_and_source_labels_are_stable() {
        assert_eq!(InputSource::Agent.label(), "agent");
        assert_eq!(InputSource::Forced.label(), "forced");
        assert_eq!(SaveReason::Auto.label(), "auto");
        assert_eq!(SaveReason::Shutdown.label(), "shutdown");
    }
}
