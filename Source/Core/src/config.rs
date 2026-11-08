//! The core's configuration file: `core-config.json` (10_delivery.md §2 (`D5`, `D9`)).
//!
//! Until this existed every daemon setting was an environment variable, because the daemon is
//! launched by a container runtime that passes no flags. That stays true — the environment is
//! still the way a *deployment* is configured — but the numbers below it, the cadences and queue
//! depths that belong to the code rather than to the deployment, are the sort of thing that wants
//! a comment beside it, a diff, and a place to look when a session behaves oddly. That is what a
//! file is for, and the agent already has one (`agent-config.json`), so this is the same shape at
//! the other end of the pipe.
//!
//! **Precedence is one rule: the command line and the environment beat the file** (10_delivery.md §2 (`D5`)).
//! A setting the file names is used only where nothing else named it, which is why the daemon asks
//! clap where each value came from rather than merely what it is.
//!
//! Where the file lives:
//!
//! * `INFOCOMPANION_CORE_CONFIG`, when it is set — the file a test or an operator points at;
//! * otherwise `<state dir>/core-config.json`, which is the path the shipped container already
//!   bind-mounts (10_delivery.md §1 (`D1`–`D4`)). The state directory is deliberately **not** settable from inside
//!   the file: it is the directory the file is found in.

use serde::Deserialize;
use std::path::{Path, PathBuf};
use std::time::Duration;

/// The file's name inside the state directory.
pub const FILE_NAME: &str = "core-config.json";

/// The shipped defaults, one copy each.
///
/// Three things need every one of these: the flag's own default (`--help` prints it), the value a
/// module uses when it is built without a tuning — which is what the tests do — and the sample
/// file. A number written twice is a number that drifts, so it is written here once.
pub const DEFAULT_SHUTDOWN_GRACE_SECONDS: u64 = 15;
pub const DEFAULT_HELD_LINE_TICK_MS: u64 = 25;
pub const DEFAULT_COMMAND_QUEUE: usize = 256;
pub const DEFAULT_FANOUT_QUEUE: usize = 512;
pub const DEFAULT_REPLAY_LINES: usize = 600;
pub const DEFAULT_TERMINAL_QUEUE: usize = 512;
pub const DEFAULT_PROTOCOL_QUEUE: usize = 512;
pub const DEFAULT_PLAYBACK_QUEUE: usize = 2048;
pub const DEFAULT_LOG_QUEUE: usize = 2048;
pub const DEFAULT_GAME_TICK_MS: u64 = 25;
pub const DEFAULT_TOOL_TURN_DEADLINE_MS: u64 = 250;
pub const DEFAULT_SCRIPT_TIMEOUT_SECONDS: u64 = 5;
pub const DEFAULT_MAX_DEFERRED_CHECKPOINT_TURNS: u32 = 8;
pub const DEFAULT_MAX_QUEUED_INPUTS: usize = 16;
pub const DEFAULT_EXIT_GRACE_MS: u64 = 500;
pub const DEFAULT_PROMPT_SETTLE_MS: u64 = 30;
pub const DEFAULT_SSH_KEEPALIVE_SECONDS: u64 = 30;
pub const DEFAULT_SSH_AUTH_REJECTION_SECONDS: u64 = 1;
pub const DEFAULT_SSH_TYPING_NOTICE_SECONDS: u64 = 1;
/// The backoff ladder: 1s → 5s → 30s, the last one repeating.
pub const DEFAULT_AGENT_BACKOFF_SECONDS: [u64; 3] = [1, 5, 30];
pub const DEFAULT_AGENT_STABLE_UPTIME_SECONDS: u64 = 60;

/// What kind of value a key takes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    /// A whole number.
    Number,
    /// A list of whole numbers.
    List,
}

/// Every key this schema understands, and the shape of value it takes.
///
/// Kept beside the struct so a mistake can be *named* rather than merely refused: a key nobody can
/// see is how a parameter set becomes unidentified (10_delivery.md §2 (`D5`)), and a wrong value that only
/// says "expected usize" leaves an operator reading the file to find out which line.
const KNOWN_KEYS: &[(&str, Kind)] = &[
    ("shutdown_grace_seconds", Kind::Number),
    ("held_line_tick_ms", Kind::Number),
    ("command_queue", Kind::Number),
    ("fanout_queue", Kind::Number),
    ("replay_lines", Kind::Number),
    ("terminal_queue", Kind::Number),
    ("protocol_queue", Kind::Number),
    ("playback_queue", Kind::Number),
    ("log_queue", Kind::Number),
    ("game_tick_ms", Kind::Number),
    ("tool_turn_deadline_ms", Kind::Number),
    ("script_timeout_seconds", Kind::Number),
    ("max_deferred_checkpoint_turns", Kind::Number),
    ("max_queued_inputs", Kind::Number),
    ("exit_grace_ms", Kind::Number),
    ("prompt_settle_ms", Kind::Number),
    ("ssh_keepalive_seconds", Kind::Number),
    ("ssh_auth_rejection_seconds", Kind::Number),
    ("ssh_typing_notice_seconds", Kind::Number),
    ("agent_backoff_seconds", Kind::List),
    ("agent_stable_uptime_seconds", Kind::Number),
];

/// The file, as written. Every field is optional: a file that names three settings changes three
/// settings, and a file that names none is the same as no file at all.
///
/// Units are in the names (`_ms`, `_seconds`) rather than implied, for the reason the agent's
/// price table is kept in published units: arithmetic in a config file is how a number ends up
/// quietly wrong.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct ConfigFile {
    /// How long a graceful shutdown is given before the core stops waiting for it.
    pub shutdown_grace_seconds: Option<u64>,
    /// How often a held line looks at the screen it is waiting for.
    pub held_line_tick_ms: Option<u64>,
    /// Depth of the router's command channel.
    pub command_queue: Option<usize>,
    /// Depth of the terminal fan-out channel: game output waiting for the panes.
    pub fanout_queue: Option<usize>,
    /// How many lines of screen and conversation a pane that connects late is given.
    pub replay_lines: Option<usize>,
    /// Outbound queue depth for a terminal seat.
    pub terminal_queue: Option<usize>,
    /// Outbound queue depth for a protocol seat (the agent, the stdio rig).
    pub protocol_queue: Option<usize>,
    /// Entries that may wait for the playback writer before the oldest are dropped.
    pub playback_queue: Option<usize>,
    /// Entries that may wait for the session-log writer before the oldest are dropped.
    pub log_queue: Option<usize>,
    /// How often the game actor checks for quiescence and script deadlines.
    pub game_tick_ms: Option<u64>,
    /// How long the tool channel's byte count is trusted before quiescence wins.
    pub tool_turn_deadline_ms: Option<u64>,
    /// How long one scripted step (a save, a restore) may take.
    pub script_timeout_seconds: Option<u64>,
    /// How many turn completions a checkpoint waits for the game's own prompt.
    pub max_deferred_checkpoint_turns: Option<u32>,
    /// How many queued input lines are tolerated before more are refused.
    pub max_queued_inputs: Option<usize>,
    /// How long frotz's polite exit is given before it is killed.
    pub exit_grace_ms: Option<u64>,
    /// How long a bare `>` is given to turn out to be the start of a longer line.
    pub prompt_settle_ms: Option<u64>,
    /// SSH keepalive interval; three unanswered requests end a dead connection.
    pub ssh_keepalive_seconds: Option<u64>,
    /// How long a rejected SSH authentication is stalled for.
    pub ssh_auth_rejection_seconds: Option<u64>,
    /// How often the core may tell the companion the player is composing a line.
    pub ssh_typing_notice_seconds: Option<u64>,
    /// Agent restart backoff steps, in seconds, in order.
    pub agent_backoff_seconds: Option<Vec<u64>>,
    /// A run at least this long counts as stable and resets the backoff.
    pub agent_stable_uptime_seconds: Option<u64>,
}

/// A configuration file that was found, and anything worth saying about it.
#[derive(Debug, Clone, Default)]
pub struct Loaded {
    /// The settings the file named.
    pub config: ConfigFile,
    /// Where it came from, so the log line can say so.
    pub source: Option<PathBuf>,
    /// Why it could not be used. The daemon runs on the defaults when this is set.
    pub problem: Option<String>,
    /// Keys the schema does not know, and values it could not use.
    pub warnings: Vec<String>,
}

/// Read the configuration file, if there is one.
///
/// A missing file is not a problem — it is a daemon running on its defaults — but a file that
/// will not parse is reported and then stood down from entirely: half a file is worse than none,
/// because it leaves an operator believing settings that are not in force (the same judgement the
/// agent makes about its own config).
pub fn load(state_dir: &Path, explicit: Option<&Path>) -> Loaded {
    for candidate in candidates(state_dir, explicit) {
        if !candidate.is_file() {
            continue;
        }
        let text = match std::fs::read_to_string(&candidate) {
            Ok(text) => text,
            Err(err) => {
                return Loaded {
                    problem: Some(format!("{}: {err}", candidate.display())),
                    ..Loaded::default()
                }
            }
        };
        return match parse(&text) {
            Ok((config, warnings)) => Loaded {
                config,
                source: Some(candidate),
                problem: None,
                warnings,
            },
            Err(problem) => Loaded {
                problem: Some(format!("{}: {problem}", candidate.display())),
                ..Loaded::default()
            },
        };
    }

    Loaded::default()
}

/// The paths tried, in order.
fn candidates(state_dir: &Path, explicit: Option<&Path>) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Some(path) = explicit {
        paths.push(path.to_path_buf());
    }
    paths.push(state_dir.join(FILE_NAME));
    paths
}

/// Parse a file, refusing to guess at anything it cannot read.
fn parse(text: &str) -> Result<(ConfigFile, Vec<String>), String> {
    // Read as a bare document first, so that an unknown key or a value of the wrong shape can be
    // named. serde's own answer to both is a message with no key in it, and the point of a file an
    // operator edits is that the complaint says which line to look at.
    let value: serde_json::Value = serde_json::from_str(text).map_err(|err| err.to_string())?;
    let object = match &value {
        serde_json::Value::Object(object) => object,
        other => return Err(format!("expected an object, found {}", kind_of(other))),
    };

    let mut warnings: Vec<String> = Vec::new();
    for (key, value) in object {
        match KNOWN_KEYS.iter().find(|(known, _)| known == key) {
            None => warnings.push(format!(
                "`{key}` is not a setting this core understands; ignored"
            )),
            Some((_, Kind::Number)) if !value.is_number() => {
                return Err(format!("`{key}` takes a number, found {}", kind_of(value)))
            }
            Some((_, Kind::List)) if !value.is_array() => {
                return Err(format!("`{key}` takes a list, found {}", kind_of(value)))
            }
            Some(_) => {}
        }
    }
    warnings.sort();

    let mut config: ConfigFile = serde_json::from_value(value).map_err(|err| err.to_string())?;
    if config
        .agent_backoff_seconds
        .as_ref()
        .is_some_and(|steps| steps.is_empty())
    {
        warnings.push(
            "`agent_backoff_seconds` is empty; an empty ladder would either spin or never \
             restart, so the default is used"
                .to_string(),
        );
        config.agent_backoff_seconds = None;
    }
    Ok((config, warnings))
}

fn kind_of(value: &serde_json::Value) -> &'static str {
    match value {
        serde_json::Value::Null => "null",
        serde_json::Value::Bool(_) => "a boolean",
        serde_json::Value::Number(_) => "a number",
        serde_json::Value::String(_) => "a string",
        serde_json::Value::Array(_) => "an array",
        serde_json::Value::Object(_) => "an object",
    }
}

/// The tuning the core runs with: which parts of the file matter to which part of the daemon.
///
/// One struct rather than fifteen loose fields on `CoreSettings`, because these travel together —
/// they are read once, in `main`, and then handed to whoever needs them.
#[derive(Debug, Clone)]
pub struct Tuning {
    /// The router: how long a shutdown may take, and how often a held line is re-checked.
    pub shutdown_grace: Duration,
    pub held_line_tick: Duration,
    /// Channel depths and the two records a late pane is replayed from.
    pub command_queue: usize,
    pub fanout_queue: usize,
    pub replay_lines: usize,
    pub terminal_queue: usize,
    pub protocol_queue: usize,
    pub playback_queue: usize,
    pub log_queue: usize,
    /// The game boundary.
    pub game: GameTuning,
    /// The SSH seat.
    pub ssh: SshTuning,
    /// Supervising the companion.
    pub agent: AgentTuning,
}

/// The game actor's own cadences (03_session.md §9 and §10 (`S58`, `S63`)).
#[derive(Debug, Clone)]
pub struct GameTuning {
    pub tick: Duration,
    pub tool_turn_deadline: Duration,
    pub script_timeout: Duration,
    pub max_deferred_checkpoint_turns: u32,
    pub max_queued_inputs: usize,
    pub exit_grace: Duration,
    /// The framer's `>` settle.
    pub prompt_settle: Duration,
}

/// The SSH seat's own timers (03_session.md §5 (`S35`–`S40`), issue #17).
#[derive(Debug, Clone)]
pub struct SshTuning {
    pub keepalive: Duration,
    pub auth_rejection: Duration,
    /// How often the core may say the player is composing a line.
    pub typing_notice: Duration,
}

/// The agent supervisor's restart policy (10_delivery.md §2.2 (`D9`)).
#[derive(Debug, Clone)]
pub struct AgentTuning {
    /// Backoff steps, in order, the last one repeating.
    pub backoff: Vec<Duration>,
    pub stable_uptime: Duration,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_file_names_only_what_it_names() {
        let (config, warnings) = parse(r#"{"replay_lines": 120}"#).expect("a readable file");
        assert_eq!(config.replay_lines, Some(120));
        assert_eq!(config.prompt_settle_ms, None);
        assert!(warnings.is_empty(), "{warnings:?}");
    }

    #[test]
    fn an_empty_object_is_a_file_with_no_opinions() {
        let (config, warnings) = parse("{}").expect("a readable file");
        assert_eq!(config.replay_lines, None);
        assert_eq!(config.agent_backoff_seconds, None);
        assert!(warnings.is_empty(), "{warnings:?}");
    }

    #[test]
    fn keys_the_schema_does_not_know_are_named_rather_than_ignored() {
        let (_, warnings) = parse(r#"{"prompt_settle_millis": 30, "nonsense": 1}"#)
            .expect("the file still loads on the settings it does name");
        assert_eq!(warnings.len(), 2, "{warnings:?}");
        assert!(warnings.iter().any(|w| w.contains("prompt_settle_millis")));
        assert!(warnings.iter().any(|w| w.contains("nonsense")));
    }

    #[test]
    fn a_value_of_the_wrong_type_is_a_problem_and_not_a_guess() {
        let err = parse(r#"{"replay_lines": "lots"}"#).expect_err("must refuse");
        assert!(err.contains("replay_lines"), "{err}");
    }

    #[test]
    fn the_backoff_ladder_is_read_as_a_list() {
        let (config, _) = parse(r#"{"agent_backoff_seconds": [2, 10, 60]}"#).expect("readable");
        assert_eq!(config.agent_backoff_seconds, Some(vec![2, 10, 60]));
    }

    #[test]
    fn an_empty_ladder_says_so_and_leaves_the_default_standing() {
        let (config, warnings) = parse(r#"{"agent_backoff_seconds": []}"#).expect("readable");
        assert_eq!(
            config.agent_backoff_seconds, None,
            "the default is what is left"
        );
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("empty"), "{warnings:?}");
    }

    #[test]
    fn the_explicit_path_wins_and_the_state_directory_is_the_fallback() {
        let state = Path::new("/state");
        let explicit = PathBuf::from("/tmp/elsewhere.json");
        assert_eq!(
            candidates(state, Some(&explicit)),
            vec![explicit, PathBuf::from("/state/core-config.json")]
        );
        assert_eq!(
            candidates(state, None),
            vec![PathBuf::from("/state/core-config.json")]
        );
    }

    #[test]
    fn a_directory_with_no_file_is_not_a_problem() {
        let loaded = load(Path::new("/nonexistent-state-dir-for-tests"), None);
        assert!(loaded.source.is_none());
        assert!(loaded.problem.is_none());
    }

    #[test]
    fn every_documented_key_is_one_the_schema_has() {
        // The list is what the unknown-key warning is built from, so a key that is documented
        // and missing here would be warned about while working, which is worse than either.
        let documented = r#"{
            "shutdown_grace_seconds": 1,
            "held_line_tick_ms": 2,
            "command_queue": 3,
            "fanout_queue": 4,
            "replay_lines": 5,
            "terminal_queue": 6,
            "protocol_queue": 7,
            "playback_queue": 8,
            "log_queue": 9,
            "game_tick_ms": 10,
            "tool_turn_deadline_ms": 11,
            "script_timeout_seconds": 12,
            "max_deferred_checkpoint_turns": 13,
            "max_queued_inputs": 14,
            "exit_grace_ms": 15,
            "prompt_settle_ms": 16,
            "ssh_keepalive_seconds": 17,
            "ssh_auth_rejection_seconds": 18,
            "ssh_typing_notice_seconds": 19,
            "agent_backoff_seconds": [20],
            "agent_stable_uptime_seconds": 21
        }"#;
        let (config, warnings) = parse(documented).expect("the documented sample loads");
        assert!(warnings.is_empty(), "{warnings:?}");
        assert_eq!(config.shutdown_grace_seconds, Some(1));
        assert_eq!(config.agent_stable_uptime_seconds, Some(21));
        assert_eq!(KNOWN_KEYS.len(), 21);
    }
}
