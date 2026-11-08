//! Session capture (§13).
//!
//! Two logs per session, both under `<state>/logs`:
//!
//! * **this module** writes the *core's* view: game turns, player lines, agent
//!   utterances, arbitration events, `/` commands, system notices — one JSON
//!   object per line, plus a human-readable summary when the session ends;
//! * the **agent** writes its own `agent-*.jsonl` beside it, containing what only
//!   the agent can see: `[think]` lines, classification decisions, token counts
//!   and cost. Keeping them separate means the core never has to guess what the
//!   agent was thinking, and the two files can be joined on timestamp.
//!
//! Logs are bounded: the newest `keep` sessions are retained and older ones are
//! deleted at startup, so host storage stays predictable (§5.4, §13).

use anyhow::{Context, Result};
use serde_json::{Map, Value};
use std::collections::BTreeMap;
use std::fs::File;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex as StdMutex;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::io::AsyncWriteExt;
use tokio::sync::{mpsc, oneshot, Mutex};
use tracing::{debug, info, warn};

/// Event kinds the core records. Free-form strings are allowed; these are the
/// ones the summary knows how to count.
pub const KIND_SESSION_START: &str = "session_start";
pub const KIND_SESSION_END: &str = "session_end";
pub const KIND_TURN: &str = "turn";
pub const KIND_PLAYER_LINE: &str = "player_line";
pub const KIND_PLAYER_DIRECT: &str = "player_direct";
pub const KIND_AGENT_THOUGHT: &str = "agent_thought";
pub const KIND_AGENT_SAY: &str = "agent_say";
pub const KIND_AGENT_ASK: &str = "agent_ask";
pub const KIND_AGENT_PROPOSAL: &str = "agent_proposal";
pub const KIND_AGENT_INPUT: &str = "agent_input";
pub const KIND_AGENT_GAME_INPUT: &str = "agent_game_input";
pub const KIND_SYSTEM: &str = "system";
pub const KIND_CORE_COMMAND: &str = "core_command";
pub const KIND_SEAT: &str = "seat";

/// The prefix on every file this module writes.
///
/// `core-`, not `session-` (03_session.md §9–§10 (`S58`–`S76`)): a **session** in this project is the player's
/// presence (§12), and this file is the *core's* record of it — the agent's own log sits
/// beside it as `agent-*.jsonl`, and the pair is what a session's evidence is made of. Two
/// different things called "session" is how a future reader ends up looking in the wrong
/// file for the wrong half of an evening.
const SESSION_PREFIX: &str = "core-";
/// What the same files were called before the rename. Still rotated away, so that shipping
/// this does not leave the older logs in place for ever.
const LEGACY_SESSION_PREFIX: &str = "session-";

/// Handle used by the core to append events. Cheap to clone-by-reference and
/// never blocks the game: if the writer falls behind, events are dropped and
/// counted rather than stalling play.
pub struct SessionLog {
    tx: mpsc::Sender<Value>,
    shutdown: Mutex<Option<oneshot::Sender<()>>>,
    session_id: String,
    jsonl_path: PathBuf,
    summary_path: PathBuf,
    /// Human-readable trace of the session, when one is being written (§13).
    trace_path: Option<PathBuf>,
    trace: Option<StdMutex<BufWriter<File>>>,
}

impl SessionLog {
    /// Rotate old sessions and open a new one.
    ///
    /// `queue` is how many events may be waiting for the writer before the oldest are dropped
    /// (`log_queue` in `core-config.json`).
    pub fn start(dir: &Path, keep: usize, queue: usize) -> Result<Self> {
        std::fs::create_dir_all(dir)
            .with_context(|| format!("failed to create log directory {}", dir.display()))?;
        rotate(dir, keep);

        let session_id = format!("core-{}", timestamp_slug());
        let jsonl_path = dir.join(format!("{session_id}.jsonl"));
        let summary_path = dir.join(format!("{session_id}.summary.md"));

        let trace_path = trace_enabled().then(|| dir.join(format!("{session_id}.trace")));
        let trace = match &trace_path {
            Some(path) => match File::create(path) {
                Ok(file) => Some(StdMutex::new(BufWriter::new(file))),
                Err(err) => {
                    warn!(path = %path.display(), error = %err, "could not open the session trace");
                    None
                }
            },
            None => None,
        };
        if let Some(path) = &trace_path {
            info!(path = %path.display(), "tracing this session to a readable file");
        }

        let (tx, rx) = mpsc::channel::<Value>(queue);
        let (shutdown_tx, shutdown_rx) = oneshot::channel::<()>();

        let writer_jsonl = jsonl_path.clone();
        let writer_summary = summary_path.clone();
        let writer_id = session_id.clone();
        tokio::spawn(async move {
            if let Err(err) =
                run_writer(rx, shutdown_rx, writer_jsonl, writer_summary, writer_id).await
            {
                warn!(error = %err, "session log writer stopped early");
            }
        });

        let log = Self {
            tx,
            shutdown: Mutex::new(Some(shutdown_tx)),
            session_id,
            jsonl_path,
            summary_path,
            trace_path,
            trace,
        };
        log.emit(
            KIND_SESSION_START,
            serde_json::json!({ "detail": "INFOCompanion core session opened" }),
        );
        Ok(log)
    }

    /// Identifier shared with the file names, handy in player-facing notices.
    pub fn session_id(&self) -> &str {
        &self.session_id
    }

    /// Path of the JSONL file being written.
    pub fn jsonl_path(&self) -> &Path {
        &self.jsonl_path
    }

    /// Path of the summary that will exist after `finish`.
    pub fn summary_path(&self) -> &Path {
        &self.summary_path
    }

    /// Path of the trace file, when one is being written.
    pub fn trace_path(&self) -> Option<&Path> {
        self.trace_path.as_deref()
    }

    /// Append one entry to the session trace — and flush it.
    ///
    /// This is the file to `tail -f` while a session runs. It carries what the two JSONL
    /// logs otherwise have to be joined on `ts` to reconstruct: the screen, the companion's
    /// thinking and speech, the player's lines and the core's notices.
    ///
    /// `actor` is a short tag — `screen`, `think`, `quill`, `player`, `system` — padded to a
    /// fixed width so a transcript lines up, and multi-line text keeps its shape with the
    /// continuation lines indented under the tag.
    ///
    /// Line-flushed on purpose. A trace you cannot read until the process exits is not a
    /// trace you can debug with. The write is a small append to a local file, and a failure
    /// is swallowed: this is a debugging aid and must never take a session down.
    pub fn trace(&self, actor: &str, text: &str) {
        let Some(lock) = &self.trace else {
            return;
        };

        // Trimmed at both ends: frotz's own chunks begin and end with blank lines, and a
        // tag stranded on an empty first line makes the whole trace much harder to scan.
        let text = text.trim();
        if text.is_empty() {
            return;
        }

        let block = render_trace(&crate::seat_protocol::now_rfc3339(), actor, text);
        if let Ok(mut writer) = lock.lock() {
            let _ = writer.write_all(block.as_bytes());
            let _ = writer.flush();
        }
    }

    /// Append one event. Non-blocking by design.
    pub fn emit(&self, kind: &str, fields: Value) {
        let mut object = match fields {
            Value::Object(map) => map,
            other => {
                let mut map = Map::new();
                map.insert("detail".to_string(), other);
                map
            }
        };
        object.insert(
            "ts".to_string(),
            Value::String(crate::seat_protocol::now_rfc3339()),
        );
        object.insert("kind".to_string(), Value::String(kind.to_string()));
        object.insert(
            "session".to_string(),
            Value::String(self.session_id.clone()),
        );
        if self.tx.try_send(Value::Object(object)).is_err() {
            debug!("session log queue full; event dropped");
        }
    }

    /// Flush what is buffered and write the summary. Idempotent.
    pub async fn finish(&self) {
        let sender = self.shutdown.lock().await.take();
        if let Some(sender) = sender {
            self.emit(KIND_SESSION_END, serde_json::json!({}));
            if let Some(path) = self.trace_path() {
                info!(path = %path.display(), "session trace closed");
            }
            let _ = sender.send(());
        }
    }
}

async fn run_writer(
    mut rx: mpsc::Receiver<Value>,
    mut shutdown: oneshot::Receiver<()>,
    jsonl_path: PathBuf,
    summary_path: PathBuf,
    session_id: String,
) -> Result<()> {
    let mut file = tokio::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&jsonl_path)
        .await
        .with_context(|| format!("failed to open {}", jsonl_path.display()))?;

    let started = Instant::now();
    let started_wall = SystemTime::now();
    let mut counts: BTreeMap<String, u64> = BTreeMap::new();
    let mut turns: u64 = 0;
    let mut bytes: u64 = 0;

    loop {
        tokio::select! {
            maybe = rx.recv() => match maybe {
                Some(event) => {
                    if let Some(kind) = event.get("kind").and_then(Value::as_str) {
                        if kind == KIND_TURN { turns += 1; }
                    }
                    let line = event.to_string();
                    bytes += line.len() as u64;
                    file.write_all(line.as_bytes()).await?;
                    file.write_all(b"\n").await?;
                    if let Some(kind) = event.get("kind").and_then(Value::as_str) {
                        *counts.entry(kind.to_string()).or_insert(0) += 1;
                    }
                }
                None => break,
            },
            _ = &mut shutdown => {
                // Drain anything already queued so the summary is complete.
                while let Ok(event) = rx.try_recv() {
                    if let Some(kind) = event.get("kind").and_then(Value::as_str) {
                        if kind == KIND_TURN { turns += 1; }
                        *counts.entry(kind.to_string()).or_insert(0) += 1;
                    }
                    let line = event.to_string();
                    bytes += line.len() as u64;
                    file.write_all(line.as_bytes()).await?;
                    file.write_all(b"\n").await?;
                }
                break;
            }
        }
    }
    file.flush().await?;
    drop(file);

    let elapsed = started.elapsed();
    write_summary(
        &summary_path,
        &session_id,
        started_wall,
        elapsed,
        turns,
        bytes,
        &counts,
        &jsonl_path,
    )
    .await?;
    debug!(path = %summary_path.display(), "session summary written");
    Ok(())
}

#[allow(clippy::too_many_arguments)]
async fn write_summary(
    path: &Path,
    session_id: &str,
    started_wall: SystemTime,
    elapsed: Duration,
    turns: u64,
    bytes: u64,
    counts: &BTreeMap<String, u64>,
    jsonl_path: &Path,
) -> Result<()> {
    let started = started_wall
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let mut text = String::new();
    text.push_str(&format!("# Session {session_id}\n\n"));
    text.push_str(&format!(
        "Started: {}\n",
        crate::seat_protocol::rfc3339_from_unix(started)
    ));
    text.push_str(&format!("Duration: {}s\n", elapsed.as_secs()));
    text.push_str(&format!("Turns played: {turns}\n"));
    text.push_str(&format!(
        "Logged bytes: {bytes} ({:.1} KiB)\n\n",
        bytes as f64 / 1024.0
    ));
    text.push_str("## Event counts\n\n");
    if counts.is_empty() {
        text.push_str("_nothing recorded_\n");
    } else {
        text.push_str("| kind | count |\n|---|---|\n");
        for (kind, count) in counts {
            text.push_str(&format!("| `{kind}` | {count} |\n"));
        }
    }
    text.push_str(&format!(
        "\n## Raw log\n\nOne JSON object per line: `{}`\n\n\
         The companion's own log (its `[think]` lines, classification decisions, \
         token counts and cost) sits alongside as `agent-*.jsonl`; join the two on `ts`.\n",
        jsonl_path.display()
    ));
    tokio::fs::write(path, text)
        .await
        .with_context(|| format!("failed to write summary {}", path.display()))?;
    Ok(())
}

/// Width of the `ts  actor  ` prefix a trace entry starts with, so continuation lines can
/// be indented to line up under the text rather than under the timestamp.
const TRACE_PREFIX: usize = 30;

/// Is the session trace wanted?
///
/// On unless it is explicitly turned off, because a trace is only useful if it was already
/// running when something went wrong. `INFOCOMPANION_TRACE=off` disables it.
fn trace_enabled() -> bool {
    !matches!(
        std::env::var("INFOCOMPANION_TRACE")
            .unwrap_or_default()
            .trim()
            .to_ascii_lowercase()
            .as_str(),
        "off" | "0" | "false" | "no"
    )
}

/// Render one trace entry: a timestamp, a fixed-width tag, then the text, with any
/// continuation lines indented so a multi-line thought stays readable.
fn render_trace(ts: &str, actor: &str, text: &str) -> String {
    let mut out = String::with_capacity(text.len() + TRACE_PREFIX + 32);
    let indent = " ".repeat(TRACE_PREFIX);
    for (index, line) in text.lines().enumerate() {
        if index == 0 {
            out.push_str(&format!("{ts}  {actor:<6}  {line}\n"));
        } else {
            out.push_str(&indent);
            out.push_str(line);
            out.push('\n');
        }
    }
    out
}

#[cfg(test)]
mod trace_tests {
    use super::*;

    #[test]
    fn an_entry_carries_a_timestamp_and_a_tag_and_indents_continuations() {
        let rendered = render_trace("2026-09-14T12:06:10Z", "think", "first\nsecond");
        assert!(rendered.starts_with("2026-09-14T12:06:10Z  think   first\n"));
        assert!(rendered.ends_with(&format!("{}second\n", " ".repeat(TRACE_PREFIX))));
    }

    #[test]
    fn a_single_line_entry_has_no_indented_tail() {
        let rendered = render_trace("2026-09-14T12:06:10Z", "screen", "Rotary North");
        assert_eq!(rendered, "2026-09-14T12:06:10Z  screen  Rotary North\n");
    }
}

/// Keep only the newest `keep` sessions (JSONL + summary), oldest first out.
fn rotate(dir: &Path, keep: usize) {
    if keep == 0 {
        return;
    }
    let mut jsonl: Vec<PathBuf> = match std::fs::read_dir(dir) {
        Ok(entries) => entries
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| {
                p.file_name()
                    .and_then(|n| n.to_str())
                    .map(|n| {
                        (n.starts_with(SESSION_PREFIX) || n.starts_with(LEGACY_SESSION_PREFIX))
                            && n.ends_with(".jsonl")
                    })
                    .unwrap_or(false)
            })
            .collect(),
        Err(_) => return,
    };
    // Sorted by the timestamp in the name rather than by the whole name. A name is only
    // time-ordered within one prefix: the moment a directory holds both `core-` and the older
    // `session-` files, sorting whole names puts every `core-` file before every `session-`
    // one whatever their dates, and rotation starts deleting the wrong end of the history.
    jsonl.sort_by_key(|path| {
        path.file_stem()
            .and_then(|stem| stem.to_str())
            .map(|stem| {
                stem.trim_start_matches(SESSION_PREFIX)
                    .trim_start_matches(LEGACY_SESSION_PREFIX)
                    .to_string()
            })
            .unwrap_or_default()
    });
    if jsonl.len() <= keep {
        return;
    }
    for stale in &jsonl[..jsonl.len() - keep] {
        if let Some(stem) = stale.file_stem().and_then(|s| s.to_str()) {
            // The summary and the trace belong to the session, so they rotate with it.
            let _ = std::fs::remove_file(dir.join(format!("{stem}.summary.md")));
            let _ = std::fs::remove_file(dir.join(format!("{stem}.trace")));
        }
        if let Err(err) = std::fs::remove_file(stale) {
            warn!(path = %stale.display(), error = %err, "failed to rotate old session log");
        } else {
            debug!(path = %stale.display(), "rotated out an old session log");
        }
    }
}

/// `20260911T002600Z` — filesystem-safe and lexically ordered.
fn timestamp_slug() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    crate::seat_protocol::rfc3339_from_unix(secs)
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    async fn wait_for_file(path: &Path) {
        for _ in 0..100 {
            if path.is_file() {
                return;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        panic!("timed out waiting for {}", path.display());
    }

    #[tokio::test]
    async fn records_events_and_writes_a_summary() {
        let dir = std::env::temp_dir().join("infocompanion-log-test");
        let _ = std::fs::remove_dir_all(&dir);

        let log = SessionLog::start(&dir, 10, crate::config::DEFAULT_LOG_QUEUE).unwrap();
        let jsonl = log.jsonl_path().to_path_buf();
        let summary = log.summary_path().to_path_buf();

        log.emit(KIND_TURN, serde_json::json!({ "input": "look" }));
        log.emit(KIND_PLAYER_LINE, serde_json::json!({ "text": "hello" }));
        log.finish().await;
        wait_for_file(&summary).await;

        let text = std::fs::read_to_string(&jsonl).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 4, "start + 2 events + end");
        for line in &lines {
            let parsed: Value = serde_json::from_str(line).expect("every line is JSON");
            assert!(parsed.get("ts").is_some());
            assert!(parsed.get("kind").is_some());
            assert!(parsed.get("session").is_some());
        }
        assert!(text.contains("\"input\":\"look\""));

        let summary_text = std::fs::read_to_string(&summary).unwrap();
        assert!(summary_text.contains("Turns played: 1"));
        assert!(summary_text.contains("| `turn` | 1 |"));
        assert!(summary_text.contains("| `player_line` | 1 |"));
        assert!(summary_text.contains("agent-*.jsonl"));

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[tokio::test]
    async fn finish_is_idempotent() {
        let dir = std::env::temp_dir().join("infocompanion-log-idempotent");
        let _ = std::fs::remove_dir_all(&dir);
        let log = SessionLog::start(&dir, 10, crate::config::DEFAULT_LOG_QUEUE).unwrap();
        log.finish().await;
        log.finish().await; // must not panic
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[tokio::test]
    async fn rotation_keeps_the_newest_sessions() {
        let dir = std::env::temp_dir().join("infocompanion-log-rotate");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        for stamp in ["20260101T000000Z", "20260102T000000Z", "20260103T000000Z"] {
            std::fs::write(dir.join(format!("core-{stamp}.jsonl")), b"{}\n").unwrap();
            std::fs::write(dir.join(format!("core-{stamp}.summary.md")), b"x").unwrap();
        }
        rotate(&dir, 2);

        assert!(!dir.join("core-20260101T000000Z.jsonl").exists());
        assert!(!dir.join("core-20260101T000000Z.summary.md").exists());
        assert!(dir.join("core-20260102T000000Z.jsonl").exists());
        assert!(dir.join("core-20260103T000000Z.jsonl").exists());

        // keep = 0 disables rotation entirely.
        rotate(&dir, 0);
        assert!(dir.join("core-20260102T000000Z.jsonl").exists());

        // The name these files had before the rename is still swept, oldest first, and the
        // newer files are not: a log that stayed behind would be a log that never ages out,
        // and a rotation that mixed the two prefixes up by name would delete the wrong end.
        std::fs::write(dir.join("session-20250101T000000Z.jsonl"), b"{}\n").unwrap();
        rotate(&dir, 2);
        assert!(!dir.join("session-20250101T000000Z.jsonl").exists());
        assert!(dir.join("core-20260103T000000Z.jsonl").exists());

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn timestamp_slugs_sort_chronologically() {
        let a = "20260911T002600Z";
        let b = "20260911T013000Z";
        assert!(a < b, "lexical order matches time order");
    }
}
