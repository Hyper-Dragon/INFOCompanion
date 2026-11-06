//! INFOCompanion — a co-play companion for interactive fiction.
//!
//! The daemon owns a single frotz process and a bus of **seats**: SSH players, the
//! supervised C# co-player agent, and a stdio transport for dev. Start with the product
//! frame; the session notes describe the wire protocol.
//!
//! Configuration is by environment variable, because this runs in a container
//! reached over ssh and is never launched from a CLI in production. The CLI flags
//! exist for the dev/test harness and mirror the environment (§5).

mod agent_supervisor;
mod config;
mod conversation;
mod core;
mod frotz;
mod game;
mod games;
mod keys;
mod meta_notes;
mod observations;
mod pace;
mod palette;
mod playback;
mod runs;
mod save;
mod screen;
mod seat;
mod seat_protocol;
mod session_log;
mod ssh;
mod state_shape;
mod stdio_transport;
mod stuck;
mod tool;
mod turn_framer;
mod vocabulary;

use anyhow::{Context, Result};
use clap::parser::ValueSource;
use clap::{CommandFactory, FromArgMatches, Parser};
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tracing::{debug, error, info, warn};
use tracing_subscriber::{fmt, prelude::*, EnvFilter};

/// INFOCompanion — a shared interactive-fiction shell with a co-play companion.
#[derive(Parser, Debug)]
#[command(name = "infocompanion", version, about, long_about = None)]
struct Args {
    /// Dev/test only: speak the seat protocol on stdin/stdout instead of serving SSH.
    #[arg(long)]
    stdio: bool,

    /// SSH listen port.
    #[arg(long, env = "INFOCOMPANION_PORT", default_value_t = 2222)]
    port: u16,

    /// SSH bind address.
    #[arg(long, env = "INFOCOMPANION_BIND", default_value = "0.0.0.0")]
    bind: String,

    /// Directory scanned for story files (auto-discovered).
    #[arg(long, env = "INFOCOMPANION_GAMES_DIR", default_value = "/games")]
    games_dir: PathBuf,

    /// Holds saves/, notebook/, logs/ and the current-game pointer.
    #[arg(long, env = "INFOCOMPANION_STATE_DIR", default_value = "/state")]
    state_dir: PathBuf,

    /// Game to boot into; defaults to the last played, else the first discovered.
    #[arg(long, env = "INFOCOMPANION_GAME")]
    game: Option<String>,

    /// The frotz interpreter to run.
    #[arg(
        long,
        env = "INFOCOMPANION_DFROTZ",
        default_value = "/usr/games/dfrotz"
    )]
    dfrotz: PathBuf,

    /// Turn-framing quiescence window in milliseconds (§7).
    ///
    /// Only used on an interpreter with no tool channel. With one, a turn ends when the
    /// interpreter says so and this is not consulted at all (02_game_boundary.md §3 (`G9`–`G15`)).
    #[arg(long, env = "INFOCOMPANION_TURN_TIMEOUT_MS", default_value_t = 400)]
    turn_timeout_ms: u64,

    /// Ask the interpreter for its tool channel (02_game_boundary.md §2 (`G5`–`G7`)).
    ///
    /// `auto` asks the interpreter itself — `dfrotz -T -v`, which a stock build answers by
    /// printing its usage — so the daemon runs beside an unmodified interpreter with no
    /// special case in the game path, and uses the facts on offer when they are there.
    #[arg(
        long,
        env = "INFOCOMPANION_INTERPRETER_TOOLS",
        value_enum,
        default_value_t = ToolsMode::Auto
    )]
    interpreter_tools: ToolsMode,

    /// Auto-save every N turns; 0 disables auto-save.
    #[arg(long, env = "INFOCOMPANION_AUTOSAVE_TURNS", default_value_t = 5)]
    autosave_turns: u64,

    /// The pace words arrive at, in words a minute, for each level (03_session.md §5 (`S35`–`S40`)).
    ///
    /// Medium is where an evening starts, and `veryfast` is what this daemon did before the ladder
    /// existed. A rate of zero means "as fast as it comes".
    #[arg(long, env = "INFOCOMPANION_PACE_VERYSLOW", default_value_t = 60)]
    pace_veryslow: u32,

    #[arg(long, env = "INFOCOMPANION_PACE_SLOW", default_value_t = 120)]
    pace_slow: u32,

    #[arg(long, env = "INFOCOMPANION_PACE_MEDIUM", default_value_t = 240)]
    pace_medium: u32,

    #[arg(long, env = "INFOCOMPANION_PACE_FAST", default_value_t = 480)]
    pace_fast: u32,

    /// The level an instance starts at, for a session with no page to remember one.
    #[arg(long, env = "INFOCOMPANION_PACE", default_value = "medium")]
    pace: String,

    /// A turn is delivered in at most this long, however long it is: the ceiling that keeps a very
    /// long reply from becoming minutes of arrival (03_session.md §5 (`S35`–`S40`)).
    #[arg(long, env = "INFOCOMPANION_PACE_CEILING", default_value_t = 20)]
    pace_ceiling: u64,

    /// How many recent turns to replay to a newly connected agent.
    #[arg(long, env = "INFOCOMPANION_TRANSCRIPT_TAIL", default_value_t = 20)]
    transcript_tail: usize,

    /// How many session log files to keep before rotating.
    #[arg(long, env = "INFOCOMPANION_LOG_KEEP", default_value_t = 20)]
    log_keep: usize,

    /// SSH username.
    #[arg(long, env = "INFOCOMPANION_AUTH_USER", default_value = "player")]
    auth_user: String,
    /// SSH password.
    #[arg(long, env = "INFOCOMPANION_AUTH_PASSWORD", default_value = "letsplay")]
    auth_password: String,

    /// Command used to launch the companion agent.
    #[arg(
        long,
        env = "INFOCOMPANION_AGENT_CMD",
        default_value = "/usr/local/bin/infocompanion-agent"
    )]
    agent_cmd: String,

    /// What the companion is called. Used for the banner, for the prefix on its
    /// speech, and told to the agent so it knows what to answer to.
    #[arg(long, env = "INFOCOMPANION_AGENT_NAME", default_value = "companion")]
    agent_name: String,

    /// Serve SSH with no companion at all (dev: the player drives directly).
    #[arg(long, env = "INFOCOMPANION_NO_AGENT", default_value_t = false)]
    no_agent: bool,

    // ----------------------------------------------------------------
    // The settings below are the daemon's own cadences: how often it looks
    // at things, how long it waits, how deep its queues are. They are
    // documented in `core-config.json` (see `config.rs`), and every one of
    // them can be set from the environment or the command line as well —
    // which is the only way a container can set them, and which wins when
    // both are given (10_delivery.md §2 (`D5`, `D9`)).
    //
    // The defaults below *are* the shipped configuration. They are the
    // values this daemon ran on before the file existed.
    // ----------------------------------------------------------------
    /// Where `core-config.json` lives, when it is not the state directory.
    #[arg(long, env = "INFOCOMPANION_CORE_CONFIG")]
    config: Option<PathBuf>,

    /// How long a graceful shutdown is given before the core stops waiting for it.
    #[arg(
        long,
        env = "INFOCOMPANION_SHUTDOWN_GRACE_SECONDS",
        default_value_t = config::DEFAULT_SHUTDOWN_GRACE_SECONDS
    )]
    shutdown_grace_seconds: u64,

    /// How often a line held behind a moving screen is looked at again.
    #[arg(long, env = "INFOCOMPANION_HELD_LINE_TICK_MS", default_value_t = config::DEFAULT_HELD_LINE_TICK_MS)]
    held_line_tick_ms: u64,

    /// Depth of the router's command channel.
    #[arg(long, env = "INFOCOMPANION_COMMAND_QUEUE", default_value_t = config::DEFAULT_COMMAND_QUEUE)]
    command_queue: usize,

    /// Depth of the terminal fan-out channel: game output waiting for the panes.
    #[arg(long, env = "INFOCOMPANION_FANOUT_QUEUE", default_value_t = config::DEFAULT_FANOUT_QUEUE)]
    fanout_queue: usize,

    /// How many lines of screen and conversation a pane that connects late is given.
    #[arg(long, env = "INFOCOMPANION_REPLAY_LINES", default_value_t = config::DEFAULT_REPLAY_LINES)]
    replay_lines: usize,

    /// Outbound queue depth for a terminal seat.
    #[arg(long, env = "INFOCOMPANION_TERMINAL_QUEUE", default_value_t = config::DEFAULT_TERMINAL_QUEUE)]
    terminal_queue: usize,

    /// Outbound queue depth for a protocol seat (the agent, the stdio rig).
    #[arg(long, env = "INFOCOMPANION_PROTOCOL_QUEUE", default_value_t = config::DEFAULT_PROTOCOL_QUEUE)]
    protocol_queue: usize,

    /// Entries that may wait for the playback writer before the oldest are dropped.
    #[arg(long, env = "INFOCOMPANION_PLAYBACK_QUEUE", default_value_t = config::DEFAULT_PLAYBACK_QUEUE)]
    playback_queue: usize,

    /// Entries that may wait for the session-log writer before the oldest are dropped.
    #[arg(long, env = "INFOCOMPANION_LOG_QUEUE", default_value_t = config::DEFAULT_LOG_QUEUE)]
    log_queue: usize,

    /// How often the game actor checks for quiescence and script deadlines.
    #[arg(long, env = "INFOCOMPANION_GAME_TICK_MS", default_value_t = config::DEFAULT_GAME_TICK_MS)]
    game_tick_ms: u64,

    /// How long the tool channel's byte count is trusted before quiescence wins.
    #[arg(long, env = "INFOCOMPANION_TOOL_TURN_DEADLINE_MS", default_value_t = config::DEFAULT_TOOL_TURN_DEADLINE_MS)]
    tool_turn_deadline_ms: u64,

    /// How long one scripted step (a save, a restore) may take.
    #[arg(long, env = "INFOCOMPANION_SCRIPT_TIMEOUT_SECONDS", default_value_t = config::DEFAULT_SCRIPT_TIMEOUT_SECONDS)]
    script_timeout_seconds: u64,

    /// How many turn completions a checkpoint waits for the game's own prompt.
    #[arg(long, env = "INFOCOMPANION_MAX_DEFERRED_CHECKPOINT_TURNS", default_value_t = config::DEFAULT_MAX_DEFERRED_CHECKPOINT_TURNS)]
    max_deferred_checkpoint_turns: u32,

    /// How many queued input lines are tolerated before more are refused.
    #[arg(long, env = "INFOCOMPANION_MAX_QUEUED_INPUTS", default_value_t = config::DEFAULT_MAX_QUEUED_INPUTS)]
    max_queued_inputs: usize,

    /// How long frotz's polite exit is given before it is killed.
    #[arg(long, env = "INFOCOMPANION_EXIT_GRACE_MS", default_value_t = config::DEFAULT_EXIT_GRACE_MS)]
    exit_grace_ms: u64,

    /// How long a bare `>` is given to turn out to be the start of a longer line.
    #[arg(long, env = "INFOCOMPANION_PROMPT_SETTLE_MS", default_value_t = config::DEFAULT_PROMPT_SETTLE_MS)]
    prompt_settle_ms: u64,

    /// SSH keepalive interval; three unanswered requests end a dead connection.
    #[arg(long, env = "INFOCOMPANION_SSH_KEEPALIVE_SECONDS", default_value_t = config::DEFAULT_SSH_KEEPALIVE_SECONDS)]
    ssh_keepalive_seconds: u64,

    /// How long a rejected SSH authentication is stalled for.
    #[arg(
        long,
        env = "INFOCOMPANION_SSH_AUTH_REJECTION_SECONDS",
        default_value_t = config::DEFAULT_SSH_AUTH_REJECTION_SECONDS
    )]
    ssh_auth_rejection_seconds: u64,

    /// How often the core may tell the companion the player is composing a line.
    #[arg(long, env = "INFOCOMPANION_SSH_TYPING_NOTICE_SECONDS", default_value_t = config::DEFAULT_SSH_TYPING_NOTICE_SECONDS)]
    ssh_typing_notice_seconds: u64,

    /// Agent restart backoff steps, in seconds, in order.
    #[arg(
        long,
        env = "INFOCOMPANION_AGENT_BACKOFF_SECONDS",
        value_delimiter = ',',
        default_value = "1,5,30"
    )]
    agent_backoff_seconds: Vec<u64>,

    /// A run at least this long counts as stable and resets the agent's backoff.
    #[arg(
        long,
        env = "INFOCOMPANION_AGENT_STABLE_UPTIME_SECONDS",
        default_value_t = config::DEFAULT_AGENT_STABLE_UPTIME_SECONDS
    )]
    agent_stable_uptime_seconds: u64,

    /// Verbose logging.
    #[arg(long)]
    debug: bool,
}

/// Where the personality lives.
///
/// An explicit `INFOCOMPANION_PROMPT_PATH` wins; otherwise it is the persona *slug*
/// (`INFOCOMPANION_PERSONA`, default `james`), so swapping the character is one variable and one
/// file dropped beside the shipped one — and never a knowledge of what the shipped one is called.
///
/// The agent resolves this by the identical rule. If you change one, change the other: the daemon
/// and the agent disagreeing about which file is the personality is invisible to both, and the
/// symptom would be a companion that answers to a name it does not introduce itself by.
fn resolve_persona() -> String {
    if let Ok(path) = std::env::var("INFOCOMPANION_PROMPT_PATH") {
        if !path.trim().is_empty() {
            return path;
        }
    }

    let slug = std::env::var("INFOCOMPANION_PERSONA")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| "james".to_string());

    format!("/prompts/persona/{}.txt", slug.trim())
}

/// The name a personality file declares for itself.
///
/// The personality owns its own name (04_companion.md §11 (`C61`–`C67`)). `INFOCOMPANION_AGENT_NAME` is only what to
/// call the companion *before* one has been read, because a name in a config file and a character
/// in another file are two things that drift apart — and the character is the one anybody editing
/// would think of as the name.
///
/// Front matter, because it is the convention every tool that reads a markdown file with metadata
/// already understands:
///
/// ```text
/// ---
/// name: James
/// ---
/// ```
///
/// A file that will not open, or declares nothing, simply has no opinion — which is not a
/// failure. A missing personality is the agent's problem to report, not the daemon's to guess
/// about.
fn persona_name(path: &str) -> Option<String> {
    let text = std::fs::read_to_string(path).ok()?;
    let mut lines = text.lines();
    if lines.next()?.trim() != "---" {
        return None;
    }

    for line in lines {
        let trimmed = line.trim();
        if trimmed == "---" {
            break;
        }

        if let Some(value) = trimmed.strip_prefix("name:") {
            let name = value.trim().trim_matches(|c| c == '"' || c == '\'').trim();
            if !name.is_empty() {
                return Some(name.to_string());
            }
        }
    }

    None
}

/// Whether to ask the interpreter for its tool channel (02_game_boundary.md §2 (`G5`–`G7`)).
#[derive(Debug, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
enum ToolsMode {
    /// Ask the interpreter, and use the channel if it has one.
    Auto,
    /// Assume it has one — for an interpreter whose answer cannot be asked for.
    On,
    /// Never ask. The game boundary behaves exactly as it did before the channel existed.
    Off,
}

/// Take a setting from the file only where neither the command line nor the environment named it.
///
/// This is the whole of the precedence story (10_delivery.md §2 (`D5`)): the deployment wins, the file fills
/// in what the deployment did not say, and the defaults in `Args` catch the rest.
macro_rules! from_file {
    ($matches:expr, $file:expr, $args:expr, $( $arg:literal => $field:ident ),+ $(,)?) => {
        $(
            if !named_by_the_deployment(&$matches, $arg) {
                if let Some(value) = $file.$field {
                    $args.$field = value;
                }
            }
        )+
    };
}

/// Did the command line or the environment set this argument, rather than clap's own default?
///
/// Asking clap where a value came *from* is what makes one rule possible: without it there is no
/// way to tell an operator's `INFOCOMPANION_EXIT_GRACE_MS=200` from the `500` this struct would
/// have supplied anyway, and the file would silently overwrite the deployment.
fn named_by_the_deployment(matches: &clap::ArgMatches, id: &str) -> bool {
    matches!(
        matches.value_source(id),
        Some(ValueSource::CommandLine | ValueSource::EnvVariable)
    )
}

/// Read `core-config.json` and let it fill in whatever nobody else named.
///
/// Called after tracing is up, because what it has to say is worth reading: which file was used,
/// what it names that this build does not understand, and — for the settings that govern a
/// session's feel — what the process ended up running on.
fn apply_config_file(matches: &clap::ArgMatches, args: &mut Args) {
    let loaded = config::load(&args.state_dir, args.config.as_deref());
    match (&loaded.source, &loaded.problem) {
        (Some(path), _) => info!(file = %path.display(), "configuration file read"),
        (None, Some(problem)) => {
            error!(error = %problem, "unusable configuration file; running on the defaults")
        }
        (None, None) => debug!(
            file = config::FILE_NAME,
            "no configuration file; running on the defaults"
        ),
    }
    for warning in &loaded.warnings {
        warn!(target: "config", "{warning}");
    }

    from_file!(
        matches,
        loaded.config,
        args,
        "shutdown_grace_seconds" => shutdown_grace_seconds,
        "held_line_tick_ms" => held_line_tick_ms,
        "command_queue" => command_queue,
        "fanout_queue" => fanout_queue,
        "replay_lines" => replay_lines,
        "terminal_queue" => terminal_queue,
        "protocol_queue" => protocol_queue,
        "playback_queue" => playback_queue,
        "log_queue" => log_queue,
        "game_tick_ms" => game_tick_ms,
        "tool_turn_deadline_ms" => tool_turn_deadline_ms,
        "script_timeout_seconds" => script_timeout_seconds,
        "max_deferred_checkpoint_turns" => max_deferred_checkpoint_turns,
        "max_queued_inputs" => max_queued_inputs,
        "exit_grace_ms" => exit_grace_ms,
        "prompt_settle_ms" => prompt_settle_ms,
        "ssh_keepalive_seconds" => ssh_keepalive_seconds,
        "ssh_auth_rejection_seconds" => ssh_auth_rejection_seconds,
        "ssh_typing_notice_seconds" => ssh_typing_notice_seconds,
        "agent_backoff_seconds" => agent_backoff_seconds,
        "agent_stable_uptime_seconds" => agent_stable_uptime_seconds,
    );
}

/// The cadences, queue depths and timers the daemon runs on, as resolved.
fn tuning_from(args: &Args) -> config::Tuning {
    config::Tuning {
        shutdown_grace: Duration::from_secs(args.shutdown_grace_seconds),
        held_line_tick: Duration::from_millis(args.held_line_tick_ms),
        command_queue: args.command_queue,
        fanout_queue: args.fanout_queue,
        replay_lines: args.replay_lines,
        terminal_queue: args.terminal_queue,
        protocol_queue: args.protocol_queue,
        playback_queue: args.playback_queue,
        log_queue: args.log_queue,
        game: config::GameTuning {
            tick: Duration::from_millis(args.game_tick_ms),
            tool_turn_deadline: Duration::from_millis(args.tool_turn_deadline_ms),
            script_timeout: Duration::from_secs(args.script_timeout_seconds),
            max_deferred_checkpoint_turns: args.max_deferred_checkpoint_turns,
            max_queued_inputs: args.max_queued_inputs,
            exit_grace: Duration::from_millis(args.exit_grace_ms),
            prompt_settle: Duration::from_millis(args.prompt_settle_ms),
        },
        ssh: config::SshTuning {
            keepalive: Duration::from_secs(args.ssh_keepalive_seconds),
            auth_rejection: Duration::from_secs(args.ssh_auth_rejection_seconds),
            typing_notice: Duration::from_secs(args.ssh_typing_notice_seconds),
        },
        agent: config::AgentTuning {
            // An empty ladder cannot be meant: it would either spin or never restart. A blank
            // `INFOCOMPANION_AGENT_BACKOFF_SECONDS=` is a compose file's idea of "unset", and the
            // shipped ladder is what that should get.
            backoff: if args.agent_backoff_seconds.is_empty() {
                warn!("an empty agent backoff ladder is not a ladder; using the default");
                config::DEFAULT_AGENT_BACKOFF_SECONDS
                    .iter()
                    .copied()
                    .map(Duration::from_secs)
                    .collect()
            } else {
                args.agent_backoff_seconds
                    .iter()
                    .copied()
                    .map(Duration::from_secs)
                    .collect()
            },
            stable_uptime: Duration::from_secs(args.agent_stable_uptime_seconds),
        },
    }
}

#[tokio::main]
async fn main() -> Result<()> {
    // The file is read after tracing is up, because which file was used and what it said are
    // both worth a line in the log (10_delivery.md §2 (`D5`)).
    let matches = Args::command().get_matches();
    let mut args = Args::from_arg_matches(&matches).unwrap_or_else(|err| err.exit());
    init_tracing(args.debug);

    // The state directory is claimed or refused before it is read for anything at all — not
    // even for the configuration file that lives in it. An older shape cannot be read, so it is
    // not read, and nothing here tries to make one work (02_game_boundary.md §9 (`G51`)).
    if let Err(refusal) = state_shape::admit(&args.state_dir) {
        error!("{refusal:#}");
        return Err(refusal);
    }

    apply_config_file(&matches, &mut args);
    let tuning = tuning_from(&args);

    // Said once, out loud: a parameter set nobody can name is how an afternoon disappears
    // (10_delivery.md §2 (`D5`)). The values that change how a session feels, rather than every queue depth.
    info!(
        shutdown_grace_s = tuning.shutdown_grace.as_secs(),
        held_line_tick_ms = tuning.held_line_tick.as_millis() as u64,
        replay_lines = tuning.replay_lines,
        game_tick_ms = tuning.game.tick.as_millis() as u64,
        tool_turn_deadline_ms = tuning.game.tool_turn_deadline.as_millis() as u64,
        script_timeout_s = tuning.game.script_timeout.as_secs(),
        exit_grace_ms = tuning.game.exit_grace.as_millis() as u64,
        prompt_settle_ms = tuning.game.prompt_settle.as_millis() as u64,
        ssh_keepalive_s = tuning.ssh.keepalive.as_secs(),
        agent_backoff_s = ?tuning.agent.backoff.iter().map(Duration::as_secs).collect::<Vec<_>>(),
        "configuration resolved"
    );

    // The personality owns its own name (04_companion.md §11 (`C61`–`C67`)): a name in a config file and a character in
    // another file are two things that drift apart, and the character is the one that should win.
    // `INFOCOMPANION_AGENT_NAME` is only what to call the companion *before* one has been read.
    let agent_name = persona_name(&resolve_persona()).unwrap_or_else(|| args.agent_name.clone());

    info!(
        version = env!("CARGO_PKG_VERSION"),
        "INFOCompanion starting"
    );

    // Does this interpreter have a tool channel (02_game_boundary.md §2 (`G5`–`G7`))? `auto` asks it rather than
    // assuming: `-T -v` is a one-process question, and a stock `dfrotz` answers an unknown
    // option by printing its usage and exiting non-zero. The answer decides whether turns are
    // framed by the interpreter's own signal or by the heuristics that were there before, and
    // whether a save is written to a path we name or through the interpreter's filename prompt.
    let tools = match args.interpreter_tools {
        ToolsMode::Off => {
            info!("interpreter tools disabled by configuration");
            false
        }
        ToolsMode::On => {
            info!("interpreter tools assumed present by configuration");
            true
        }
        ToolsMode::Auto => {
            let supported = frotz::supports_tools(&args.dfrotz).await;
            if supported {
                info!(
                    interpreter = %args.dfrotz.display(),
                    "the interpreter offers a tool channel; turns are framed by its own signal"
                );
            } else {
                info!(
                    interpreter = %args.dfrotz.display(),
                    "no tool channel on this interpreter; framing and saving the way they always were"
                );
            }
            supported
        }
    };

    // A save belongs to a game, and the saves that predate the per-game folder are moved
    // into one here rather than being found in the wrong place later. Idempotent, so it costs
    // nothing on every start after the first.
    {
        let state = save::StatePaths::new(&args.state_dir);
        if let Err(err) = state.ensure() {
            return Err(err).context("failed to create the state directory");
        }
        match save::migrate_flat_saves(&state) {
            Ok(0) => {}
            Ok(moved) => info!(moved, "moved loose saves into their game's own folder"),
            Err(err) => warn!(error = %err, "could not migrate the saves directory"),
        }
    }

    let settings = core::CoreSettings {
        games_dir: args.games_dir.clone(),
        state: save::StatePaths::new(&args.state_dir),
        // An unset variable and a variable set to the empty string both mean "no
        // preference": compose passes `${INFOCOMPANION_GAME:-}` through verbatim.
        requested_game: args.game.clone().filter(|game| !game.trim().is_empty()),
        dfrotz: args.dfrotz.clone(),
        turn_timeout: Duration::from_millis(args.turn_timeout_ms),
        autosave_turns: args.autosave_turns,
        transcript_tail: args.transcript_tail,
        log_keep: args.log_keep,
        agent_name: agent_name.clone(),
        tools,
        pace: pace::Pace::parse(&args.pace).unwrap_or(pace::Pace::Medium),
        rates: pace::Rates::new(
            args.pace_veryslow,
            args.pace_slow,
            args.pace_medium,
            args.pace_fast,
            Duration::from_secs(args.pace_ceiling),
        ),
        tuning,
    };

    // The core takes the settings; the two things built here that outlive that move — the SSH
    // seat's own timers, and the agent supervisor's backoff — are taken from the same resolution.
    let settings_tuning = settings.tuning.clone();

    // Said once, at the top, because it is the sort of setting somebody changes in a file and then
    // wonders why nothing looks different (03_session.md §5 (`S35`–`S40`)).
    let settings_pace = format!(
        "{} ({})",
        settings.pace.name(),
        match settings.rates.words_per_minute(settings.pace) {
            Some(words) => format!(
                "{words} wpm, ceiling {}s",
                settings.rates.ceiling().as_secs()
            ),
            None => "as fast as it comes".to_string(),
        }
    );

    let core = core::Core::new(settings)
        .await
        .context("failed to start the core")?;

    info!(
        game = %core.game_status().await.stem,
        session = core.session_id(),
        pace = settings_pace,
        "session open"
    );

    if args.stdio {
        info!("stdio mode: speaking the seat protocol on stdin/stdout");
        return stdio_transport::run(Arc::clone(&core)).await;
    }
    // The player's window.
    let ssh_config = ssh::SshConfig {
        bind_address: args.bind.clone(),
        port: args.port,
        auth_user: args.auth_user.clone(),
        auth_password: args.auth_password.clone(),
        agent_name: agent_name.clone(),
        tuning: settings_tuning.ssh.clone(),
    };
    let ssh_server = ssh::SshServer::new(Arc::clone(&core), ssh_config)?;
    let ssh_task = tokio::spawn(async move {
        if let Err(err) = ssh_server.run().await {
            error!(error = %err, "SSH server stopped");
        }
    });

    // The companion, unless we were asked to run without one.
    //
    // The channel is the daemon's shutdown, and it is deliberately *before* the abort that used to
    // end this: the way an agent is told to go is EOF on its stdin (`§8.2`), and aborting the
    // supervisor task does not close that pipe — the writer owns it. Measured 2026-10-01, across a
    // full sitting, a disconnect, a restart and a clean stop: no agent log carried a `session_end`,
    // because the child was killed with the container before it could write one.
    let (stopping_tx, stopping_rx) = tokio::sync::oneshot::channel();
    let agent_task = if args.no_agent {
        info!("running without a companion (INFOCOMPANION_NO_AGENT)");
        None
    } else {
        match agent_supervisor::AgentCommand::parse(&args.agent_cmd) {
            Ok(command) => {
                let core = Arc::clone(&core);
                let policy = settings_tuning.agent.clone();
                Some(tokio::spawn(agent_supervisor::supervise(
                    core,
                    command,
                    policy,
                    settings_tuning.shutdown_grace,
                    stopping_rx,
                )))
            }
            Err(err) => {
                error!(error = %err, "bad agent command; running without a companion");
                None
            }
        }
    };

    info!(
        "ready — connect with: ssh {}@<host> -p {}",
        args.auth_user, args.port
    );

    wait_for_shutdown_signal().await;
    info!("shutting down: checkpointing the game and flushing the session log");

    core.shutdown().await;
    if let Some(task) = agent_task {
        // Say goodbye first, then give it the grace: closing its stdin is the agent's cue to exit,
        // and what it writes on the way out is the session's own summary — the row a bundle is read
        // for. It used to be `task.abort()`, which does not close that pipe: the child was killed
        // when the container went and there was no summary at all.
        let _ = stopping_tx.send(());
        match tokio::time::timeout(settings_tuning.shutdown_grace, task).await {
            Ok(Ok(())) => info!("the companion stopped"),
            Ok(Err(err)) => warn!(error = %err, "the companion's supervisor ended badly"),
            Err(_) => warn!(
                grace_s = settings_tuning.shutdown_grace.as_secs(),
                "the companion did not stop within the grace period; leaving it"
            ),
        }
    }
    ssh_task.abort();

    info!("goodbye");
    Ok(())
}

/// Log to **stderr** always: stdout is reserved for the seat protocol (§6.1).
fn init_tracing(debug: bool) {
    let filter = if debug {
        EnvFilter::new("debug")
    } else {
        EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info"))
    };
    tracing_subscriber::registry()
        .with(fmt::layer().with_writer(std::io::stderr))
        .with(filter)
        .init();
}

/// Ctrl+C, or SIGTERM when the container runtime asks us to stop.
async fn wait_for_shutdown_signal() {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{signal, SignalKind};
        let mut term = match signal(SignalKind::terminate()) {
            Ok(term) => term,
            Err(err) => {
                error!(error = %err, "could not listen for SIGTERM");
                let _ = tokio::signal::ctrl_c().await;
                return;
            }
        };
        tokio::select! {
            _ = tokio::signal::ctrl_c() => info!("received Ctrl+C"),
            _ = term.recv() => info!("received SIGTERM"),
        }
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
        info!("received Ctrl+C");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cli_defaults_match_the_documented_container_config() {
        // Parsing with no arguments must give the §5.2 defaults, because the
        // container passes nothing but environment variables.
        let args = Args::parse_from(["infocompanion"]);
        assert_eq!(args.port, 2222);
        assert_eq!(args.bind, "0.0.0.0");
        assert_eq!(args.games_dir, PathBuf::from("/games"));
        assert_eq!(args.state_dir, PathBuf::from("/state"));
        assert_eq!(args.dfrotz, PathBuf::from("/usr/games/dfrotz"));
        assert_eq!(args.turn_timeout_ms, 400);
        assert_eq!(args.autosave_turns, 5);
        assert_eq!(args.auth_user, "player");
        assert_eq!(args.auth_password, "letsplay");
        assert_eq!(args.agent_cmd, "/usr/local/bin/infocompanion-agent");
        assert!(!args.stdio);
        assert!(!args.no_agent);
        assert!(args.game.is_none());
    }

    #[test]
    fn stdio_mode_is_reachable_from_the_command_line() {
        let args = Args::parse_from([
            "infocompanion",
            "--stdio",
            "--game",
            "Assets/games/advent.z5",
        ]);
        assert!(args.stdio);
        assert_eq!(args.game.as_deref(), Some("Assets/games/advent.z5"));
    }

    #[test]
    fn no_agent_flag_is_available_for_dev() {
        let args = Args::parse_from(["infocompanion", "--no-agent"]);
        assert!(args.no_agent);
    }

    /// A directory of this test's own, so two runs cannot read each other's file.
    fn scratch(name: &str) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("ic-core-config-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).expect("a scratch directory");
        dir
    }

    /// Parse as the daemon does, with the file at `path` named explicitly.
    ///
    /// The precedence rule needs the same `ArgMatches` the real startup uses, because what makes
    /// the rule true is asking clap *where* each value came from — a test that rebuilt the logic
    /// with a fresh `Args::parse_from` would be testing its own copy of it.
    fn parsed_with_config(path: &std::path::Path, extra: &[&str]) -> (clap::ArgMatches, Args) {
        let mut argv = vec![
            "infocompanion",
            "--config",
            path.to_str().expect("a utf-8 path"),
        ];
        argv.extend_from_slice(extra);
        let matches = Args::command().get_matches_from(argv);
        let args = Args::from_arg_matches(&matches).expect("the same parse the daemon does");
        (matches, args)
    }

    #[test]
    fn the_file_fills_in_what_the_deployment_did_not_name() {
        let dir = scratch("fills-in");
        let path = dir.join(config::FILE_NAME);
        std::fs::write(&path, r#"{"replay_lines": 123, "game_tick_ms": 7}"#).expect("a file");

        let (matches, mut args) = parsed_with_config(&path, &[]);
        apply_config_file(&matches, &mut args);

        assert_eq!(args.replay_lines, 123);
        assert_eq!(args.game_tick_ms, 7);
        assert_eq!(
            args.prompt_settle_ms,
            config::DEFAULT_PROMPT_SETTLE_MS,
            "a key the file does not mention keeps the shipped default"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn the_command_line_beats_the_file() {
        let dir = scratch("precedence");
        let path = dir.join(config::FILE_NAME);
        std::fs::write(&path, r#"{"replay_lines": 123}"#).expect("a file");

        let (matches, mut args) = parsed_with_config(&path, &["--replay-lines", "900"]);
        apply_config_file(&matches, &mut args);

        assert_eq!(
            args.replay_lines, 900,
            "the deployment wins (10_delivery.md §2 (`D5`))"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_file_that_will_not_parse_is_reported_and_the_defaults_stand() {
        let dir = scratch("broken");
        let path = dir.join(config::FILE_NAME);
        std::fs::write(&path, "{ this is not json").expect("a file");

        let loaded = config::load(&dir, None);
        assert!(loaded.source.is_none(), "a broken file is not a source");
        let problem = loaded.problem.expect("the daemon must say what was wrong");
        assert!(problem.contains(config::FILE_NAME), "{problem}");
        assert_eq!(
            loaded.config.replay_lines, None,
            "and it must not be believed in part"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn the_explicit_path_beats_the_state_directory() {
        let dir = scratch("explicit");
        std::fs::write(dir.join(config::FILE_NAME), r#"{"replay_lines": 1}"#).expect("a file");
        let elsewhere = dir.join("elsewhere.json");
        std::fs::write(&elsewhere, r#"{"replay_lines": 2}"#).expect("a file");

        let loaded = config::load(&dir, Some(&elsewhere));
        assert_eq!(loaded.config.replay_lines, Some(2));
        assert_eq!(loaded.source.as_deref(), Some(elsewhere.as_path()));

        let _ = std::fs::remove_dir_all(&dir);
    }
}
