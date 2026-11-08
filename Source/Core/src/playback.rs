//! The evening, written down as it happens (03_session.md §4.2 (`S31`)).
//!
//! Two records already live in memory — the screen and the conversation — and both exist to answer
//! one question at a time: *what should this pane be shown now*. Neither is durable, and neither is
//! shaped like the thing a person would read afterwards. This is: the game's own screen, appended to
//! `<state>/playback/<stem>.log` in the order it was printed, with a banner when an attempt opens.
//!
//! **Why it exists.** An evening has to survive the model being down (§5 W2). When the model is
//! unreachable the game carries on — it always has — and the companion falls silent; the thing that
//! was missing was a *durable* account of what happened while it was away, both for the companion to
//! catch up on and for a person to read afterwards. The turn record holds input and output per turn,
//! and the session log holds every event; neither holds what a pane actually showed, in order, in
//! the game's own words. That is what this is, and it is why it is written *always* rather than only
//! during an outage: the core has no business deciding when an evening became worth keeping, and a
//! file that only exists for the bad evenings is a file nobody has read when the good one goes
//! wrong.
//!
//! **What is in it.** The game's bytes exactly as they arrived, and the lines written into the game
//! — the same two things the screen record keeps, and the same filter (`screen::as_playback_text`),
//! so the file reads like the pane did: the `>` prompt, the story's answer to it, the machinery of a
//! checkpoint, all of it. The companion's words, the notices and the lamp are *not* in it: this is
//! the game's screen, which is the half that cannot be reconstructed from anywhere else.
//!
//! **How it is written.** A channel and a task, like the session log, because the game must never
//! wait on a disk. A message that cannot be queued is dropped and counted rather than blocking the
//! router — a hole in a playback is worth less than a stalled turn — and a file that cannot be
//! opened or written is a warning, never a failure, on the same principle as the turn record: a
//! record that cannot be written is worth less than a game that keeps playing.

use crate::save::StatePaths;
use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use tokio::sync::mpsc;
use tracing::{debug, warn};

/// What the writer task is told to do.
enum Entry {
    /// A new attempt at a game: which file this goes in, and the banner that opens it.
    Attempt {
        stem: String,
        run: u64,
        generation: u64,
        at: String,
    },
    /// Screen text, in the order it was printed.
    Screen(String),
}

/// The handle the core holds. Cheap to clone, and safe to send to from anywhere on the game's path:
/// every method is a non-blocking queue push.
#[derive(Clone)]
pub struct Playback {
    tx: Option<mpsc::Sender<Entry>>,
    dropped: Option<Arc<AtomicU64>>,
}

impl std::fmt::Debug for Playback {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Written out rather than derived so that the registry, which holds one, can keep its own
        // derived Debug: a handle is either writing somewhere or it is not, and the queue's depth
        // is nobody's business.
        match &self.tx {
            Some(_) => f.write_str("Playback(writing)"),
            None => f.write_str("Playback(off)"),
        }
    }
}

impl Playback {
    /// A playback with nowhere to write. Used by tests and by anything built without a state
    /// directory; every method is a no-op.
    pub fn off() -> Self {
        Self {
            tx: None,
            dropped: None,
        }
    }

    /// Open the playback for a state directory: `<state>/playback/<stem>.log`, one file per game.
    ///
    /// The directory is created on the first write rather than here, so a core that never plays
    /// anything never touches the disk.
    pub fn open(state: &StatePaths, queue: usize) -> Self {
        let dir = state.playback_dir();
        let (tx, rx) = mpsc::channel(queue);
        let dropped = Arc::new(AtomicU64::new(0));
        tokio::spawn(write_loop(dir, rx));
        Self {
            tx: Some(tx),
            dropped: Some(dropped),
        }
    }

    /// The banner for an attempt: which game, which attempt, which fork of the world, and when.
    pub fn note_attempt(&self, stem: &str, run: u64, generation: u64, at: String) {
        self.send(Entry::Attempt {
            stem: stem.to_string(),
            run,
            generation,
            at,
        });
    }

    /// Screen text: what the game printed, or a line written into it.
    pub fn screen(&self, text: String) {
        self.send(Entry::Screen(text));
    }

    /// How many entries have been dropped for want of a queue. Used by the tests and by nothing
    /// else, which is the honest state of a number nobody acts on.
    #[cfg(test)]
    pub fn dropped(&self) -> u64 {
        self.dropped
            .as_ref()
            .map(|count| count.load(Ordering::Relaxed))
            .unwrap_or(0)
    }

    fn send(&self, entry: Entry) {
        let Some(tx) = &self.tx else {
            return;
        };
        if tx.try_send(entry).is_err() {
            if let Some(dropped) = &self.dropped {
                let total = dropped.fetch_add(1, Ordering::Relaxed) + 1;
                if total == 1 || total % 1000 == 0 {
                    warn!(dropped = total, "playback queue is full; dropping a line");
                }
            }
        }
    }
}

/// The writer. Owns the open file, and reopens it when the game changes.
async fn write_loop(dir: PathBuf, mut rx: mpsc::Receiver<Entry>) {
    let mut open: Option<(String, BufWriter<File>)> = None;
    while let Some(entry) = rx.recv().await {
        match entry {
            Entry::Attempt {
                stem,
                run,
                generation,
                at,
            } => {
                let banner = format!(
                    "\n=== {stem}, attempt {run}, generation {generation} — opened {at} ===\n"
                );
                match open_file(&dir, &stem) {
                    Ok(file) => {
                        let mut writer = BufWriter::new(file);
                        if writer.write_all(banner.as_bytes()).is_err() {
                            warn!(stem = %stem, "could not write the playback banner");
                            open = None;
                            continue;
                        }
                        debug!(path = %dir.join(format!("{stem}.log")).display(), "playback opened");
                        open = Some((stem, writer));
                    }
                    Err(err) => {
                        warn!(error = %err, stem = %stem, "could not open the playback file");
                        open = None;
                    }
                }
            }
            Entry::Screen(text) => {
                let Some((stem, writer)) = open.as_mut() else {
                    // Nothing has said which game this is, so there is no file to put it in.
                    continue;
                };
                if writer.write_all(text.as_bytes()).is_err() || writer.flush().is_err() {
                    // One warning, and the playback stops rather than repeating it per line.
                    warn!(stem = %stem, "could not write to the playback file; closing it");
                    open = None;
                }
            }
        }
    }
}

/// Open `<dir>/<stem>.log` for appending, creating the directory if it is not there yet.
fn open_file(dir: &std::path::Path, stem: &str) -> std::io::Result<File> {
    fs::create_dir_all(dir)?;
    OpenOptions::new()
        .create(true)
        .append(true)
        .open(dir.join(format!("{stem}.log")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::game::InputSource;
    use crate::screen::as_playback_text;
    use crate::seat::ClientMessage;

    fn state(root: &std::path::Path) -> StatePaths {
        StatePaths::new(root)
    }

    /// Wait for the writer to have dealt with everything queued, then read the file.
    async fn settled(path: &std::path::Path) -> String {
        for _ in 0..200 {
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            if let Ok(text) = fs::read_to_string(path) {
                return text;
            }
        }
        fs::read_to_string(path).unwrap_or_default()
    }

    #[tokio::test]
    async fn the_screen_is_written_down_in_order_under_a_banner() {
        let root = std::env::temp_dir().join(format!("ic-playback-{}", uuid_like()));
        let playback = Playback::open(&state(&root), crate::config::DEFAULT_PLAYBACK_QUEUE);
        playback.note_attempt("witness", 2, 1, "2026-09-19T00:00:00Z".to_string());
        playback.screen("(garage)\nThe garage, like a car port.\n>".to_string());
        playback.screen("look".to_string());
        playback.screen("\n(garage)\nAgain.\n>".to_string());

        let text = settled(&root.join("playback").join("witness.log")).await;
        assert!(
            text.contains("=== witness, attempt 2, generation 1 — opened 2026-09-19T00:00:00Z ==="),
            "{text}"
        );
        let body = text.split("===\n").nth(1).unwrap_or_default();
        assert_eq!(
            body,
            "(garage)\nThe garage, like a car port.\n>look\n(garage)\nAgain.\n>"
        );

        let _ = fs::remove_dir_all(&root);
    }

    #[tokio::test]
    async fn a_second_attempt_appends_rather_than_starting_again() {
        let root = std::env::temp_dir().join(format!("ic-playback-{}", uuid_like()));
        let playback = Playback::open(&state(&root), crate::config::DEFAULT_PLAYBACK_QUEUE);
        playback.note_attempt("witness", 1, 1, "one".to_string());
        playback.screen("first\n".to_string());
        playback.note_attempt("witness", 2, 1, "two".to_string());
        playback.screen("second\n".to_string());

        let text = settled(&root.join("playback").join("witness.log")).await;
        assert!(
            text.contains("=== witness, attempt 1, generation 1 — opened one ==="),
            "{text}"
        );
        assert!(
            text.contains("=== witness, attempt 2, generation 1 — opened two ==="),
            "{text}"
        );
        assert!(text.contains("first\n"), "the first attempt is still there");
        assert!(text.contains("second\n"));

        let _ = fs::remove_dir_all(&root);
    }

    #[tokio::test]
    async fn screen_text_with_no_game_yet_is_dropped_rather_than_guessed_at() {
        let root = std::env::temp_dir().join(format!("ic-playback-{}", uuid_like()));
        let playback = Playback::open(&state(&root), crate::config::DEFAULT_PLAYBACK_QUEUE);
        playback.screen("nobody has said which game this is\n".to_string());
        playback.note_attempt("witness", 1, 1, "now".to_string());
        playback.screen("here we are\n".to_string());

        let text = settled(&root.join("playback").join("witness.log")).await;
        assert!(!text.contains("nobody has said"), "{text}");
        assert!(text.contains("here we are"));

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn only_the_game_s_screen_is_written_down() {
        // The same filter the screen record uses: the game's bytes and the lines written into the
        // game, and nothing of ours that a pane shows.
        assert_eq!(
            as_playback_text(&ClientMessage::GameOutput(b"West of House\n>".to_vec())),
            Some("West of House\n>".to_string())
        );
        assert_eq!(
            as_playback_text(&ClientMessage::Input {
                line: "north".into(),
                source: InputSource::Player,
            }),
            Some("north".to_string()),
            "the command echoes after the prompt, exactly as the pane shows it"
        );
        for ours in [
            ClientMessage::Companion("hello".into()),
            ClientMessage::System("a notice".into()),
            ClientMessage::Thought("hmm".into()),
            ClientMessage::Player("me".into()),
            ClientMessage::Ready,
            ClientMessage::Lamp { on: true },
        ] {
            assert_eq!(as_playback_text(&ours), None, "{ours:?} is not the screen");
        }
    }

    #[tokio::test]
    async fn a_playback_with_nowhere_to_write_does_nothing() {
        let playback = Playback::off();
        playback.note_attempt("witness", 1, 1, "now".to_string());
        playback.screen("and this goes nowhere, quietly".to_string());
        assert_eq!(playback.dropped(), 0);
    }

    /// A unique-enough name for a temp directory, without pulling in a dependency for it.
    fn uuid_like() -> String {
        use std::time::{SystemTime, UNIX_EPOCH};
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        format!("{nanos}")
    }
}
