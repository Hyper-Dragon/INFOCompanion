//! Which attempt this is (02_game_boundary.md §6 (`G27`–`G34`)).
//!
//! A **run** is one attempt at a game: the story starting, or restarting, to its next restart or
//! abandonment. It is deliberately not a process lifetime. A redeploy, a crash or a container
//! restart **continues** a run, because the world came back from the checkpoint and the companion
//! is the same person; what ends one is the story starting over, a different game being loaded, or
//! `/reset`.
//!
//! The number is per game and monotonic, and it is a plain integer rather than a timestamp because
//! "third go at this one" is the thing worth saying. It is what the turn record is stamped with and
//! what a belief cites when it needs to say *when* it was formed.
//!
//! Nothing here decides anything about the companion: this module keeps the bookkeeping, and the
//! core hands the number over. What the number means to a player is the companion's business.

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

use crate::seat_protocol::now_rfc3339;

/// One game's attempts, as they stand on disk.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct RunBook {
    /// How many attempts this game has had. Monotonic; `/reset` is the only thing that zeroes it.
    #[serde(default)]
    pub count: u64,
    /// The attempt in progress, or `None` when the last one ended — a switch away, a restart.
    #[serde(default)]
    pub current: Option<Attempt>,
}

/// The attempt in progress.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Attempt {
    /// The number a person would say: the first attempt is run 1.
    pub number: u64,
    /// RFC 3339, at the moment the story started.
    pub started: String,
    /// Forks of the world inside this run, made by a restore (02_game_boundary.md §6 (`G27`–`G34`)). Starts at one.
    #[serde(default = "first_generation")]
    pub generation: u64,
}

fn first_generation() -> u64 {
    1
}

impl Attempt {
    /// A brand new attempt, numbered and stamped.
    pub fn start(number: u64) -> Self {
        Self {
            number,
            started: now_rfc3339(),
            generation: first_generation(),
        }
    }
}

/// `<state>/runs/<stem>.json` — one small file per game, beside the notebook it belongs to.
pub fn path(root: &Path, stem: &str) -> PathBuf {
    root.join("runs").join(format!("{stem}.json"))
}

/// Read a game's run book. A missing file is a game nobody has played, and a file that will not
/// parse is treated the same way: bookkeeping is never worth failing a session over.
pub fn read(root: &Path, stem: &str) -> RunBook {
    let file = path(root, stem);
    match std::fs::read_to_string(&file) {
        Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
        Err(_) => RunBook::default(),
    }
}

/// Write a game's run book back.
pub fn write(root: &Path, stem: &str, book: &RunBook) -> Result<()> {
    let file = path(root, stem);
    if let Some(dir) = file.parent() {
        std::fs::create_dir_all(dir)
            .with_context(|| format!("failed to create {}", dir.display()))?;
    }
    let text = serde_json::to_string_pretty(book).context("failed to encode the run book")?;
    std::fs::write(&file, text).with_context(|| format!("failed to write {}", file.display()))
}

/// The attempt to carry on with, or a new one.
///
/// This is the whole of "a redeploy does not end a run": an attempt left open on disk is the one
/// that is still going, and only an explicitly ended one starts a new number.
pub fn continue_or_start(root: &Path, stem: &str) -> Result<Attempt> {
    let mut book = read(root, stem);
    if let Some(current) = book.current.clone() {
        return Ok(current);
    }

    book.count += 1;
    let attempt = Attempt::start(book.count);
    book.current = Some(attempt.clone());
    write(root, stem, &book)?;
    Ok(attempt)
}

/// The story is starting over: close the attempt and open the next one.
pub fn start_new(root: &Path, stem: &str) -> Result<Attempt> {
    let mut book = read(root, stem);
    book.count += 1;
    let attempt = Attempt::start(book.count);
    book.current = Some(attempt.clone());
    write(root, stem, &book)?;
    Ok(attempt)
}

/// The attempt is over — the player switched to another game. The number stays on the book, so the
/// next attempt is the next one, and the count is what makes "third go at this one" true.
pub fn end(root: &Path, stem: &str) -> Result<()> {
    let mut book = read(root, stem);
    if book.current.take().is_some() {
        write(root, stem, &book)?;
    }
    Ok(())
}

/// `/reset`: as if the game had never been played. The attempts go with the checkpoint and the
/// notebook, which is the point of it — and it is the only thing that zeroes a count.
pub fn forget(root: &Path, stem: &str) -> Result<()> {
    let file = path(root, stem);
    match std::fs::remove_file(&file) {
        Ok(()) => Ok(()),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(err) => Err(err).with_context(|| format!("failed to remove {}", file.display())),
    }
}

/// Name a fork of the world inside the attempt in progress (02_game_boundary.md §6 (`G27`–`G34`)): a restore means the
/// world changed under the companion's feet, and what it learned before the fork is still true
/// about the game even though where it stood is not.
pub fn fork_generation(root: &Path, stem: &str) -> Result<Attempt> {
    let mut book = read(root, stem);
    let attempt = match book.current.take() {
        Some(current) => Attempt {
            number: current.number,
            started: current.started,
            generation: current.generation + 1,
        },
        None => {
            book.count += 1;
            Attempt::start(book.count)
        }
    };
    book.current = Some(attempt.clone());
    write(root, stem, &book)?;
    Ok(attempt)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp() -> PathBuf {
        let dir = std::env::temp_dir().join(format!("ic-runs-{}", uuid()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn uuid() -> String {
        format!(
            "{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        )
    }

    #[test]
    fn a_process_restart_continues_the_attempt_instead_of_numbering_a_new_one() {
        // The whole point: a redeploy is not an event in the story. The attempt left open on disk
        // is the one still going.
        let root = temp();
        let first = continue_or_start(&root, "wishbringer").unwrap();
        assert_eq!(first.number, 1);

        let again = continue_or_start(&root, "wishbringer").unwrap();
        assert_eq!(
            again.number, 1,
            "a restart of the process is the same attempt"
        );
        assert_eq!(again.started, first.started);
        assert_eq!(read(&root, "wishbringer").count, 1);
    }

    #[test]
    fn a_story_starting_over_is_the_next_attempt() {
        let root = temp();
        assert_eq!(continue_or_start(&root, "advent").unwrap().number, 1);
        let second = start_new(&root, "advent").unwrap();
        assert_eq!(second.number, 2, "the player is on their second go");
        assert_eq!(continue_or_start(&root, "advent").unwrap().number, 2);
    }

    #[test]
    fn switching_away_ends_the_attempt_and_switching_back_starts_another() {
        let root = temp();
        assert_eq!(continue_or_start(&root, "advent").unwrap().number, 1);
        end(&root, "advent").unwrap();
        assert_eq!(
            continue_or_start(&root, "advent").unwrap().number,
            2,
            "a different game was loaded in between, so that attempt is over"
        );
    }

    #[test]
    fn reset_forgets_the_count_as_well_as_the_world() {
        let root = temp();
        start_new(&root, "advent").unwrap();
        start_new(&root, "advent").unwrap();
        assert_eq!(read(&root, "advent").count, 2);
        forget(&root, "advent").unwrap();
        assert_eq!(read(&root, "advent").count, 0);
        assert_eq!(continue_or_start(&root, "advent").unwrap().number, 1);
    }

    #[test]
    fn a_restore_forks_the_world_without_touching_the_number() {
        // A generation is a fork inside one attempt: the companion is on the same go, standing
        // somewhere else (02_game_boundary.md §6 (`G27`–`G34`)).
        let root = temp();
        let first = continue_or_start(&root, "advent").unwrap();
        assert_eq!(first.generation, 1);
        let forked = fork_generation(&root, "advent").unwrap();
        assert_eq!(forked.generation, 2);
        assert_eq!(forked.number, first.number);
    }

    #[test]
    fn a_corrupt_run_book_is_a_game_nobody_has_played() {
        let root = temp();
        let file = path(&root, "advent");
        std::fs::create_dir_all(file.parent().unwrap()).unwrap();
        std::fs::write(&file, "{ this is not json").unwrap();
        assert_eq!(read(&root, "advent").count, 0);
        assert_eq!(continue_or_start(&root, "advent").unwrap().number, 1);
    }
}
