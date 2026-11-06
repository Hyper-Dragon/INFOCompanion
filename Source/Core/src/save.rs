//! Progress: per-game checkpoints and the agent's notebook (§5.4).
//!
//! The core owns save/restore end to end because it is the only writer to frotz.
//! The model is **one canonical checkpoint per game**, not arbitrary save slots:
//!
//! * `<state>/saves/<stem>/<stem>.sav` is the game's "current position";
//! * auto-save every N turns, on switch, and on shutdown;
//! * `/reset` is a *full* reset — checkpoint and notebook both deleted, as if the
//!   game had never been played.
//!
//! **Saves belong to a game, and their folder says so** (03_session.md §9–§10 (`S58`–`S76`)). They used to sit
//! side by side in one flat directory, where a collision between two stories was somebody's
//! evening. The folder is also what makes a filename the *player* types safe: it is the
//! interpreter's restricted directory, so a name typed at its prompt lands beside this game's
//! checkpoint rather than among every game's.
//!
//! Beside the checkpoint is a small JSON record — which run it came from, which turn, why it
//! was taken. The save file itself is Quetzal, which is the story's format and stays readable
//! by any interpreter; anything the *core* knows belongs in its own file and never inside it.

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use tracing::{debug, info};

/// Marker frotz prints when it wants a filename for `save`.
///
/// Measured against dfrotz 2.54: `>Please enter a filename [advent.qzl]: ` — note
/// the lower-case `enter`, and that the prompt is printed inline after the `>`
/// game prompt with no trailing newline. The match is case-sensitive, so this is
/// kept verbatim; `game::tests` pins it against the measured bytes.
///
/// Only used on an interpreter with no tool channel. With one, the host names the file and
/// no prompt is printed at all (03_session.md §9–§10 (`S58`–`S76`)).
pub const SAVE_PROMPT_MARKER: &str = "enter a filename";
/// frotz's failure line. This one is the *interpreter's*, like the filename prompt — the
/// success line is not, which is why there is no success marker to match here any more.
pub const SAVE_FAIL_MARKER: &str = "Save failed";

/// The state directory layout (§5.2).
#[derive(Debug, Clone)]
pub struct StatePaths {
    root: PathBuf,
}

impl StatePaths {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    /// `<state>/saves`
    pub fn saves_dir(&self) -> PathBuf {
        self.root.join("saves")
    }

    /// `<state>/notebook`
    pub fn notebooks_dir(&self) -> PathBuf {
        self.root.join("notebook")
    }

    /// `<state>/observations`
    pub fn observations_dir(&self) -> PathBuf {
        self.root.join("observations")
    }

    /// `<state>/vocabulary` — what each story's parser refused and accepted, kept between
    /// sessions by the agent (03_session.md §9–§10 (`S58`–`S76`)). Written by the agent, listed here because this is
    /// the one place that knows the shape of the state directory.
    pub fn vocabulary_dir(&self) -> PathBuf {
        self.root.join("vocabulary")
    }

    /// `<state>/meta` — the companion's notes about the harness and about itself (05_memory.md §7 (`M37`–`M39`)).
    /// Written by the agent, listed here for the same reason the vocabulary is: this is the one
    /// place that knows the shape of the state directory. It is not part of the world, it is not
    /// a notebook for a game, and nothing in it is ever read back into a prompt.
    pub fn meta_dir(&self) -> PathBuf {
        self.root.join("meta")
    }

    /// `<state>/meta/<stem>.md` — one file per game, beside the notebook it is deliberately not.
    pub fn meta(&self, stem: &str) -> PathBuf {
        self.meta_dir().join(format!("{stem}.md"))
    }

    /// `<state>/runs/<stem>.json` — which attempt at this game is in progress (02_game_boundary.md §6 (`G27`–`G34`)). One
    /// small file per game, beside the notebook it belongs to; the number in it is what the turn
    /// record is stamped with. The path is `runs::path`, which owns that naming.
    pub fn runs_dir(&self) -> PathBuf {
        self.root.join("runs")
    }

    /// `<state>/observations/<stem>.jsonl` — every turn the core watched, in order
    /// (issue #22). Gameplay data, not the companion's words, so it lives beside the
    /// notebook rather than in it.
    pub fn observations(&self, stem: &str) -> PathBuf {
        self.observations_dir().join(format!("{stem}.jsonl"))
    }

    /// `<state>/playback` — the game's own screen, written down as it happens (03_session.md §4.2 (`S31`)). One
    /// file per game, appended, so an evening can be read back afterwards; `playback::Playback`
    /// owns the writing.
    pub fn playback_dir(&self) -> PathBuf {
        self.root.join("playback")
    }

    /// `<state>/logs`
    pub fn logs_dir(&self) -> PathBuf {
        self.root.join("logs")
    }

    /// `<state>/saves/<stem>` — one folder per game. See the note at the top of this module:
    /// a save belongs to a game, and this is the only place that says which game a file is.
    pub fn game_saves_dir(&self, stem: &str) -> PathBuf {
        self.saves_dir().join(stem)
    }

    /// `<state>/saves/<stem>/<stem>.sav` — the canonical checkpoint for a game.
    pub fn checkpoint(&self, stem: &str) -> PathBuf {
        self.game_saves_dir(stem).join(format!("{stem}.sav"))
    }

    /// `<state>/saves/<stem>/<stem>.json` — what the core knows about the checkpoint that the
    /// save file itself cannot say. Quetzal is the story's format and stays untouched.
    pub fn save_record(&self, stem: &str) -> PathBuf {
        self.game_saves_dir(stem).join(format!("{stem}.json"))
    }

    /// `<state>/saves/<stem>/<name>.sav` — a save the player named (§5.3).
    ///
    /// The same folder as the game's own checkpoint, and deliberately so: a save belongs to a game,
    /// and this folder is the only thing in the state directory that says which game a file is for.
    /// The name is checked by [`slot_name`] before it reaches here, so it cannot be a path.
    pub fn named_checkpoint(&self, stem: &str, name: &str) -> PathBuf {
        self.game_saves_dir(stem).join(format!("{name}.sav"))
    }

    /// Where a checkpoint is written before being renamed into place: `<saves>/<stem>/stage`.
    ///
    /// Only used on an interpreter with **no** tool channel, where a save still goes through
    /// frotz's filename prompt and that prompt has to be answered. With a channel the
    /// interpreter writes straight to the path the host named, and there is no staging file,
    /// no rename and no `-R` arithmetic to survive (03_session.md §9–§10 (`S58`–`S76`)).
    ///
    /// Two measured facts fix the name:
    ///
    /// * frotz asks `Overwrite existing file?` when the target exists, and an unanswered
    ///   overwrite prompt fails the save — so the save goes to a path the sequence has just
    ///   deleted, and the rename that follows is atomic within the state volume.
    /// * `-R` (restricted read/write) rewrites the directory and **cuts the basename**; the
    ///   name therefore has to be short relative to the restricted directory. This used to be
    ///   arithmetic worth respecting — the rule is `len(dir) ≥ 2 · len(name)` — but the
    ///   truncation was an upstream bug in the dumb interface, fixed in `dFrotzTooled`: the
    ///   third argument of the `strncat` was a difference of lengths rather than the room left
    ///   in the destination. The name stays short because there is no reason to lengthen it,
    ///   not because it has to be.
    pub fn staged_checkpoint(&self, stem: &str) -> PathBuf {
        self.game_saves_dir(stem).join("stage")
    }

    /// `<state>/notebook/<stem>.md` — the agent's memory for this game.
    pub fn notebook(&self, stem: &str) -> PathBuf {
        self.notebooks_dir().join(format!("{stem}.md"))
    }

    /// Create the directory tree the daemon expects to own.
    pub fn ensure(&self) -> Result<()> {
        for dir in [
            self.root.clone(),
            self.saves_dir(),
            self.notebooks_dir(),
            self.observations_dir(),
            self.vocabulary_dir(),
            self.meta_dir(),
            self.runs_dir(),
            self.logs_dir(),
        ] {
            std::fs::create_dir_all(&dir)
                .with_context(|| format!("failed to create state directory {}", dir.display()))?;
        }
        Ok(())
    }

    /// Does a checkpoint exist for this game?
    pub fn has_checkpoint(&self, stem: &str) -> bool {
        self.checkpoint(stem).is_file()
    }
}

/// Create the folder a game's saves live in.
///
/// Made before the game starts, because the interpreter writes the checkpoint itself when it
/// is told where to put it: a missing directory is a failed save, and a failed save is the
/// thing this whole module exists to prevent.
pub fn ensure_game_dir(paths: &StatePaths, stem: &str) -> Result<PathBuf> {
    let dir = paths.game_saves_dir(stem);
    std::fs::create_dir_all(&dir)
        .with_context(|| format!("failed to create the save folder {}", dir.display()))?;
    Ok(dir)
}

/// What the core knows about a checkpoint, beside it.
///
/// The companion is told what a save is *for* by the numbers here rather than by the file: a
/// bare `.sav` says nothing about which attempt at the game it belongs to, and "the second go,
/// about eighty turns in" is what makes a save make sense to somebody reading back over an
/// evening.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SaveRecord {
    pub game: String,
    /// RFC 3339, at the moment the checkpoint committed.
    pub saved_at: String,
    /// `auto`, `manual`, `switch` or `shutdown`.
    pub reason: String,
    /// The core's turn counter when the save was taken.
    pub turn: u64,
    /// Which attempt at this game (`runs.rs`), and which fork of the world within it.
    pub run: u64,
    pub generation: u64,
    /// The size of the file that was written, or -1 when the interpreter did not say.
    pub bytes: i64,
    /// Which interpreter wrote it, so two evenings of the same story can be compared at all.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub interpreter: Option<String>,
    /// Where the companion was standing when this was written, when the core could tell (B7).
    ///
    /// Absent on a record written before this field existed, and absent on a save taken before the
    /// game had printed a turn. The record was usable without it and stays usable without it — the
    /// `.sav` is still the position, and the companion is still told to look.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position: Option<Position>,
}

/// Where a game was standing when a checkpoint was taken (B7).
///
/// Not a place: the core is a witness and not a mapmaker (`observations`), so what it keeps is the
/// *index* the record already has for the turn — the story's own first line, and a hash of its first
/// paragraph — plus the one number the interpreter keeps about progress. Handed over at a restore,
/// that turns "you do not know where you are, go and look" into a fact, which is the difference
/// between a load that costs a screen-read every time and a load that costs nothing.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct Position {
    /// The first line of that turn's output, as the story printed it: the label a person reads.
    pub room: String,
    /// The index the record knows the turn by (`Narrow Trail#9f8e7d6c`), so a note can be matched
    /// against the record rather than only against a name.
    pub fingerprint: String,
    /// The interpreter's own score, when the story keeps one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub score: Option<i64>,
}

impl Position {
    /// The whole fact in one line, with the save's own name in front of it: what the player is
    /// told at a restore, and what the companion's note is built from.
    ///
    /// One string for both ends on purpose. The companion may only speak about what the player can
    /// see, and two wordings of one fact is how that quietly stops being true.
    ///
    /// The name leads because the save is what the sentence is about and the position is a fact
    /// about it: *which* save came back is what a fork note is anchored on, and a position with no
    /// name in front of it leaves the companion unable to tell one load from the next.
    pub fn said_in_a_line(&self, turn: u64, name: &str) -> String {
        let mut line = format!("the save {name} was taken at turn {turn}, in {}", self.room);
        if let Some(score) = self.score {
            line.push_str(&format!(" (score {score})"));
        }
        line
    }
}

/// Write the record beside the checkpoint. Best effort: a checkpoint that committed is worth
/// keeping even if its note cannot be written.
pub fn write_save_record(paths: &StatePaths, stem: &str, record: &SaveRecord) -> Result<PathBuf> {
    let path = paths.save_record(stem);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }
    let text = serde_json::to_string_pretty(record).context("failed to encode the save record")?;
    std::fs::write(&path, text).with_context(|| format!("failed to write {}", path.display()))?;
    debug!(path = %path.display(), "wrote the save record");
    Ok(path)
}

/// Read back what is known about the checkpoint that sits beside a game.
///
/// `None` when there is no record, which is a real state: the `.sav` file is the position and the
/// `.json` is only what the core happened to know about it. A missing note is never a reason to
/// doubt the checkpoint itself.
pub fn read_save_record(paths: &StatePaths, stem: &str) -> Option<SaveRecord> {
    read_record_beside(&paths.checkpoint(stem))
}

/// The record that belongs to a save file: the same path with `.json` for `.sav`.
///
/// A named save and the game's own checkpoint carry identical records, so the pair is found by
/// arithmetic on the file name rather than by remembering which kind of path a caller happens to
/// be holding. `wishbringer.sav` → `wishbringer.json`, `incell.sav` → `incell.json`.
pub fn record_beside(sav: &Path) -> PathBuf {
    sav.with_extension("json")
}

/// Read the record beside any save file, canonical or named.
pub fn read_record_beside(sav: &Path) -> Option<SaveRecord> {
    let text = std::fs::read_to_string(record_beside(sav)).ok()?;
    serde_json::from_str(&text).ok()
}

/// What a load is announced with, in one line: which save came back and what its note knows
/// (`S70`).
///
/// The name is read from the file the interpreter opened rather than from the word that asked for
/// the load, so one function speaks for every way a save comes back — one the player named, the
/// game's own checkpoint, a `restore` the companion typed, a load a story ran for itself. What it
/// says beyond the name is what the record beside the file holds and no more: a record with a
/// position gives the whole position, a record with only the numbers gives the turn, and no record
/// says so rather than going quiet — *"nothing was written down about where this one stands"* is
/// itself a fact, and the one that sends the companion to look.
///
/// The name is the handle onto everything the line leaves out. The rest of the record — when it was
/// written, why, which attempt and fork it belongs to — is what the companion can read back with
/// the `saves` tool once it knows which save is in hand.
pub fn said_of_the_load(sav: &Path) -> String {
    let name = sav
        .file_stem()
        .map(|stem| stem.to_string_lossy().into_owned())
        .unwrap_or_else(|| sav.display().to_string());
    match read_record_beside(sav) {
        Some(record) => match record.position {
            Some(position) => position.said_in_a_line(record.turn, &name),
            None => format!("the save {name} was taken at turn {}", record.turn),
        },
        None => format!("the save {name} was loaded — where it stands was not written down"),
    }
}

/// Write a record beside any save file, canonical or named.
pub fn write_record_beside(sav: &Path, record: &SaveRecord) -> Result<PathBuf> {
    let path = record_beside(sav);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }
    let text = serde_json::to_string_pretty(record).context("failed to encode the save record")?;
    std::fs::write(&path, text).with_context(|| format!("failed to write {}", path.display()))?;
    Ok(path)
}

/// A slot name a player typed, put through the one rule that decides what a save may be called.
///
/// Lowercased and checked, or the reason it cannot be a name. The check is a **shape, not a
/// sanitiser** — the same judgement `webui`'s route whitelist makes — because a slot name becomes
/// a file name in a directory the interpreter also writes to. Nothing that could be a path (a
/// slash, a dot, a space) is allowed to become one in the first place, so there is no
/// traversal to clean up afterwards.
pub fn slot_name(raw: &str) -> Result<String, String> {
    let name = raw.trim().to_ascii_lowercase();
    if name.is_empty() {
        return Err("a save needs a name: `/save incell`.".to_string());
    }
    if name.len() > 32 {
        return Err(format!(
            "`{name}` is too long for a save name — 32 characters at most."
        ));
    }
    if !name
        .chars()
        .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
    {
        return Err(format!(
            "`{name}` cannot be a save name: letters, digits, `-` and `_` only, because it becomes \
             a file name beside the game's own checkpoint."
        ));
    }
    if name == "stage" {
        return Err(
            "`stage` is the name a checkpoint is written under on the way into place, so it \
             cannot be a save's name."
                .to_string(),
        );
    }
    Ok(name)
}

/// The name to write this game's next named save under, or why it cannot be written.
///
/// The refusals are the whole point (§5.3): a name that is already taken is never merged into,
/// because two different positions under one name is the failure the naming was meant to fix. The
/// existing save is described rather than merely denied, so the player has something to choose
/// between — which turn it was, and when.
pub fn new_slot_name(paths: &StatePaths, stem: &str, raw: &str) -> Result<String, String> {
    let name = slot_name(raw)?;
    if name == stem.to_ascii_lowercase() {
        return Err(format!(
            "`{name}` is this game's own checkpoint — `/save` with no name is what writes that one."
        ));
    }

    let sav = paths.named_checkpoint(stem, &name);
    if sav.is_file() {
        let described = read_record_beside(&sav)
            .map(|record| format!(" (turn {}, saved {})", record.turn, record.saved_at))
            .unwrap_or_default();
        return Err(format!(
            "there is already a save called `{name}`{described} — choose another name, or \
             `/restore {name}` to go back to that one."
        ));
    }

    Ok(name)
}

/// One save a game has: the game's own checkpoint, or one the player named (§5.3).
///
/// The name is the whole of what a caller needs: the file is `saves/<stem>/<name>.sav`, and both
/// halves of that are already known to whoever is holding the game. Carrying a path around as well
/// would be a second way to say the same thing, and two ways to say a path is one way to disagree.
#[derive(Debug, Clone)]
pub struct Slot {
    /// What to type after `/restore`: the game's stem for its own checkpoint, else the save's name.
    pub name: String,
    /// What the record beside it says, when it says anything.
    pub record: Option<SaveRecord>,
    /// The game's own checkpoint rather than a save somebody named.
    pub canonical: bool,
}

impl Slot {
    /// How the save is described in a listing: turn, attempt and when, as far as the record goes.
    pub fn described(&self) -> String {
        match &self.record {
            Some(record) => format!(
                "turn {}, run {} (fork {}), {}",
                record.turn, record.run, record.generation, record.saved_at
            ),
            None => "no record beside it".to_string(),
        }
    }
}

/// Every save this game has: its own checkpoint first, then the named ones by name.
///
/// Read off the directory rather than remembered: a save is a file, so what exists is the answer,
/// and a `.sav` with no record beside it is still a save (the record is the core's note, not the
/// position).
pub fn slots(paths: &StatePaths, stem: &str) -> Vec<Slot> {
    let mut found: Vec<Slot> = Vec::new();
    let canonical = paths.checkpoint(stem);
    if canonical.is_file() {
        found.push(Slot {
            name: stem.to_string(),
            record: read_record_beside(&canonical),
            canonical: true,
        });
    }

    let mut named: Vec<Slot> = Vec::new();
    if let Ok(entries) = std::fs::read_dir(paths.game_saves_dir(stem)) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("sav") {
                continue;
            }
            if path == canonical {
                continue;
            }
            let Some(name) = path.file_stem().and_then(|s| s.to_str()) else {
                continue;
            };
            named.push(Slot {
                name: name.to_string(),
                record: read_record_beside(&path),
                canonical: false,
            });
        }
    }
    named.sort_by(|left, right| left.name.cmp(&right.name));
    found.append(&mut named);
    found
}

/// Move the saves of every game into its own folder, once.
///
/// Idempotent, and safe to run on every start: a file that is already in its folder is left
/// alone, a bare file is moved into one, and a target that already exists is left as it is
/// rather than overwritten — the older checkpoint is not more correct than the newer one, and
/// guessing which is which is not this function's business. Returns how many were moved.
pub fn migrate_flat_saves(paths: &StatePaths) -> Result<usize> {
    let saves = paths.saves_dir();
    let entries = match std::fs::read_dir(&saves) {
        Ok(entries) => entries,
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => return Ok(0),
        Err(err) => return Err(err).with_context(|| format!("failed to read {}", saves.display())),
    };

    let mut moved = 0;
    for entry in entries.flatten() {
        let path = entry.path();
        let name = entry.file_name().to_string_lossy().into_owned();

        if path.is_dir() {
            continue;
        }
        // The old shared staging file, which has no game to belong to and is worthless by
        // design: it was always deleted before a save and renamed away after one.
        if name == "stage" {
            if let Err(err) = std::fs::remove_file(&path) {
                debug!(path = %path.display(), error = %err, "could not remove the old staging file");
            }
            continue;
        }
        if !name.ends_with(".sav") {
            continue;
        }
        let stem = name.trim_end_matches(".sav").to_string();
        if stem.is_empty() {
            continue;
        }

        ensure_game_dir(paths, &stem)?;
        let target = paths.checkpoint(&stem);
        if target.exists() {
            info!(
                path = %path.display(),
                "a checkpoint is already in this game's folder; leaving the loose one alone"
            );
            continue;
        }
        match std::fs::rename(&path, &target) {
            Ok(()) => {
                info!(from = %path.display(), to = %target.display(), "moved a save into its game's folder");
                moved += 1;
            }
            Err(err) => {
                info!(path = %path.display(), error = %err, "could not move a save into its game's folder");
            }
        }
    }
    Ok(moved)
}
///
/// The core seeds the template because it knows the game's identity at boot; from
/// then on the notebook belongs to the agent (and the player, who may correct it).
/// Create the agent's notebook for a game if it does not exist yet.
///
/// The core seeds the template because it knows the game's identity at boot; from
/// then on the notebook belongs to the agent (and the player, who may correct it).
pub fn ensure_notebook(paths: &StatePaths, stem: &str, version: Option<u8>) -> Result<PathBuf> {
    let path = paths.notebook(stem);
    if path.is_file() {
        return Ok(path);
    }
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }
    std::fs::write(&path, notebook_template(stem, version))
        .with_context(|| format!("failed to seed notebook {}", path.display()))?;
    info!(stem, path = %path.display(), "seeded a new notebook");
    Ok(path)
}

/// Delete everything the game has accumulated: checkpoint (and any staged
/// checkpoint), notebook, and the record of what the core watched. Returns what was
/// removed, for an honest report.
pub fn delete_progress(paths: &StatePaths, stem: &str) -> Result<Vec<PathBuf>> {
    let mut removed = Vec::new();
    for candidate in [
        paths.checkpoint(stem),
        paths.staged_checkpoint(stem),
        paths.save_record(stem),
        paths.notebook(stem),
        paths.observations(stem),
    ] {
        match std::fs::remove_file(&candidate) {
            Ok(()) => {
                debug!(path = %candidate.display(), "removed");
                removed.push(candidate);
            }
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
            Err(err) => {
                return Err(err)
                    .with_context(|| format!("failed to remove {}", candidate.display()))
            }
        }
    }
    Ok(removed)
}

/// Delete only the game's checkpoints, leaving the notebook alone.
///
/// This is what a restart does (issue #26): the run starts again at the top, and
/// everything the companion learned about the game — and about the player — stays.
pub fn delete_checkpoint(paths: &StatePaths, stem: &str) -> Result<Vec<PathBuf>> {
    let mut removed = Vec::new();
    for candidate in [
        paths.checkpoint(stem),
        paths.staged_checkpoint(stem),
        paths.save_record(stem),
    ] {
        match std::fs::remove_file(&candidate) {
            Ok(()) => {
                debug!(path = %candidate.display(), "removed");
                removed.push(candidate);
            }
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
            Err(err) => {
                return Err(err)
                    .with_context(|| format!("failed to remove {}", candidate.display()))
            }
        }
    }
    Ok(removed)
}

/// The initial notebook: the sections from §10.5, empty for the agent to fill.
pub fn notebook_template(stem: &str, version: Option<u8>) -> String {
    let version = version
        .map(|v| format!("Z-code v{v}"))
        .unwrap_or_else(|| "unknown format".to_string());
    format!(
        r#"# Notebook — {stem}

<!-- The companion's long-term memory for this game. It is a working document,
     not a log: keep it under a few thousand tokens and prune ruthlessly.
     The player is allowed to read this and to correct it — player corrections
     always outrank the companion's own notes. -->

Game: {stem} ({version})
Status: not started

## Current goals and plan

_(what we are trying to do right now, and the next step)_

## Map notes

_(rooms, connections, landmarks, locked doors)_

## Key facts

_(verbatim NPC quotes, the troll's password, item locations, scores)_

## Open questions

_(things we do not know yet and want to find out)_

## Dead ends

_(tried X, failed, because Y — so we do not walk into it twice)_

## Working theories

_(guesses we are acting on, marked as guesses)_

## Player preferences

_(pace, risk tolerance, favourite verbs, pet hates)_

## Player corrections

_(things the player has told us that outrank the companion's own notes)_

## Discarded

_(theories we have abandoned, each with a one-line reason)_
"#
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn layout_is_what_the_spec_promises() {
        let paths = StatePaths::new("/state");
        assert_eq!(paths.saves_dir(), PathBuf::from("/state/saves"));
        assert_eq!(paths.notebooks_dir(), PathBuf::from("/state/notebook"));
        assert_eq!(paths.logs_dir(), PathBuf::from("/state/logs"));
        // One folder per game, because a save belongs to a game.
        assert_eq!(
            paths.game_saves_dir("advent"),
            PathBuf::from("/state/saves/advent")
        );
        assert_eq!(
            paths.checkpoint("advent"),
            PathBuf::from("/state/saves/advent/advent.sav")
        );
        assert_eq!(
            paths.save_record("advent"),
            PathBuf::from("/state/saves/advent/advent.json")
        );
        assert_eq!(
            paths.notebook("advent"),
            PathBuf::from("/state/notebook/advent.md")
        );
        assert_eq!(
            paths.staged_checkpoint("advent"),
            PathBuf::from("/state/saves/advent/stage")
        );
        assert_ne!(
            paths.checkpoint("advent"),
            paths.staged_checkpoint("advent")
        );
        // And two games cannot collide, which is the whole point of the folder.
        assert_ne!(paths.checkpoint("advent"), paths.checkpoint("witness"));
    }

    #[test]
    fn the_staged_name_survives_the_interpreters_rewrite() {
        // `-R` used to cut a basename to `len(restricted) − len(basename)` characters when that
        // number landed inside the name, so the staged name had to stay below half of the
        // restricted path. The truncation was an upstream bug, fixed in `dFrotzTooled` — a
        // `strncat` whose length argument was a difference of lengths instead of the room left.
        // The margin is asserted anyway, because the name is the fallback path's and the rule
        // is cheap to keep: nothing here may depend on the bug being fixed.
        let paths = StatePaths::new("/state");
        let name = paths
            .staged_checkpoint("advent")
            .file_name()
            .unwrap()
            .to_string_lossy()
            .into_owned();
        let dir = paths
            .game_saves_dir("advent")
            .to_string_lossy()
            .into_owned();
        assert!(
            2 * name.len() <= dir.len(),
            "the staged name `{name}` is not below half of `{dir}`"
        );
    }

    #[test]
    fn loose_saves_are_moved_into_their_own_folder_once() {
        let dir = std::env::temp_dir().join("infocompanion-migrate-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();

        // The shape the old layout left behind: bare files, and one shared staging file.
        std::fs::write(paths.saves_dir().join("advent.sav"), b"advent").unwrap();
        std::fs::write(paths.saves_dir().join("wishbringer.sav"), b"wishbringer").unwrap();
        std::fs::write(paths.saves_dir().join("stage"), b"junk").unwrap();

        assert_eq!(migrate_flat_saves(&paths).unwrap(), 2);
        assert!(!paths.saves_dir().join("stage").exists());
        assert_eq!(
            std::fs::read(paths.checkpoint("advent")).unwrap(),
            b"advent"
        );
        assert_eq!(
            std::fs::read(paths.checkpoint("wishbringer")).unwrap(),
            b"wishbringer"
        );
        // Running it again finds nothing to do, and does not disturb what is there.
        assert_eq!(migrate_flat_saves(&paths).unwrap(), 0);
        assert_eq!(
            std::fs::read(paths.checkpoint("advent")).unwrap(),
            b"advent"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_save_record_says_which_attempt_it_belongs_to() {
        let dir = std::env::temp_dir().join("infocompanion-record-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();

        let record = SaveRecord {
            game: "advent".to_string(),
            saved_at: "2026-09-18T18:00:00Z".to_string(),
            reason: "auto".to_string(),
            turn: 42,
            run: 2,
            generation: 1,
            bytes: 852,
            interpreter: Some("dfrotztooled-2.54".to_string()),
            position: None,
        };
        let path = write_save_record(&paths, "advent", &record).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("\"run\": 2"), "{text}");
        assert!(text.contains("\"turn\": 42"), "{text}");
        // And it is beside the checkpoint, in the game's own folder.
        assert_eq!(path.parent(), paths.checkpoint("advent").parent());

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_record_carries_where_the_game_was_standing() {
        // B7. The position travels beside the checkpoint so that a load can be a fact rather than a
        // screen-read: the companion is told where the save was taken instead of being told to go
        // and find out.
        let position = Position {
            room: "North of Bridge".to_string(),
            fingerprint: "North of Bridge#9f8e7d6c".to_string(),
            score: Some(44),
        };
        assert_eq!(
            position.said_in_a_line(210, "00-poodle"),
            "the save 00-poodle was taken at turn 210, in North of Bridge (score 44)"
        );

        // A story that keeps no score is not a story with a score of nothing: the number is left
        // out of the sentence rather than printed as zero.
        let no_score = Position {
            score: None,
            ..position.clone()
        };
        assert_eq!(
            no_score.said_in_a_line(210, "00-poodle"),
            "the save 00-poodle was taken at turn 210, in North of Bridge"
        );

        // And it survives the file.
        let dir = std::env::temp_dir().join("infocompanion-position-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        let record = SaveRecord {
            game: "wishbringer".to_string(),
            saved_at: "2026-09-21T00:01:18Z".to_string(),
            reason: "named".to_string(),
            turn: 210,
            run: 1,
            generation: 4,
            bytes: 1180,
            interpreter: None,
            position: Some(position.clone()),
        };
        write_save_record(&paths, "wishbringer", &record).unwrap();
        assert_eq!(
            read_save_record(&paths, "wishbringer").unwrap().position,
            Some(position)
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_record_written_before_the_position_existed_still_reads() {
        // The field is additive or it is a break: every save on every machine was written without
        // it, and a `.sav` that will not load because its note is old is the worst thing this
        // module could do. Written as the bytes an older core would have left.
        let dir = std::env::temp_dir().join("infocompanion-old-record-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        let path = paths.save_record("wishbringer");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(
            &path,
            r#"{"game":"wishbringer","saved_at":"2026-09-20T21:00:00Z","reason":"auto",
                "turn":195,"run":1,"generation":1,"bytes":1180,
                "interpreter":"dfrotztooled-2.54"}"#,
        )
        .unwrap();

        let record = read_save_record(&paths, "wishbringer").expect("an old record still reads");
        assert_eq!(record.turn, 195);
        assert_eq!(
            record.position, None,
            "and it says nothing it does not know"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_load_is_announced_with_the_save_and_what_its_note_knows() {
        // S70. The name comes from the file the interpreter opened, not from the word that asked
        // for the load, so one rule covers a save the player named, the game's own checkpoint, and
        // a `restore` a story ran for itself. Beyond the name is what the note holds and no more:
        // the whole position when it has one, the turn when that is all there is, and neither
        // invented when there is no note at all.
        let dir = std::env::temp_dir().join("infocompanion-load-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        // The game's own save folder is what `start_game` makes before a save can land in it, and a
        // named save sits in that folder rather than in the state root.
        ensure_game_dir(&paths, "wishbringer").unwrap();

        let named = paths.named_checkpoint("wishbringer", "00-poodle");
        std::fs::write(&named, b"save").unwrap();
        assert_eq!(
            said_of_the_load(&named),
            "the save 00-poodle was loaded — where it stands was not written down",
            "no note is said rather than guessed"
        );

        let record = SaveRecord {
            game: "wishbringer".to_string(),
            saved_at: "2026-10-05T14:00:00Z".to_string(),
            reason: "manual".to_string(),
            turn: 210,
            run: 1,
            generation: 2,
            bytes: 1180,
            interpreter: None,
            position: None,
        };
        write_record_beside(&named, &record).unwrap();
        assert_eq!(
            said_of_the_load(&named),
            "the save 00-poodle was taken at turn 210",
            "a note with only the numbers still says the turn"
        );

        let positioned = SaveRecord {
            position: Some(Position {
                room: "North of Bridge".to_string(),
                fingerprint: "North of Bridge#9f8e7d6c".to_string(),
                score: Some(44),
            }),
            ..record
        };
        write_record_beside(&named, &positioned).unwrap();
        assert_eq!(
            said_of_the_load(&named),
            "the save 00-poodle was taken at turn 210, in North of Bridge (score 44)"
        );

        // The checkpoint is named after the game, and the rule does not change for it.
        let canonical = paths.checkpoint("wishbringer");
        std::fs::write(&canonical, b"save").unwrap();
        write_save_record(&paths, "wishbringer", &positioned).unwrap();
        assert_eq!(
            said_of_the_load(&canonical),
            "the save wishbringer was taken at turn 210, in North of Bridge (score 44)"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    // -- named saves (§5.3) ------------------------------------------------------------------

    #[test]
    fn a_slot_name_is_a_shape_and_not_a_sanitiser() {
        // A name becomes a file name inside a directory the interpreter also writes to, so
        // anything that could be a *path* has to be refused rather than cleaned up: there is
        // nothing to sanitise if it never becomes a name.
        for refused in [
            "",
            "   ",
            "..",
            "a/b",
            "../escape",
            "with space",
            "dot.name",
        ] {
            assert!(
                slot_name(refused).is_err(),
                "`{refused}` should not be usable as a save name"
            );
        }
        // The interpreter writes a checkpoint under this one on the way into place.
        assert!(slot_name("stage").is_err());
        assert!(slot_name(&"x".repeat(33)).is_err());

        // And the ordinary ones are ordinary, case-folded so two spellings are not two saves.
        assert_eq!(slot_name("incell").unwrap(), "incell");
        assert_eq!(slot_name("Incell").unwrap(), "incell");
        assert_eq!(slot_name(" before-troll ").unwrap(), "before-troll");
        assert_eq!(slot_name("wishbringer_2").unwrap(), "wishbringer_2");
    }

    #[test]
    fn a_name_already_taken_is_refused_with_the_one_that_holds_it() {
        let dir = std::env::temp_dir().join("infocompanion-slot-name-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        ensure_game_dir(&paths, "wishbringer").unwrap();

        // Free to start with.
        assert_eq!(
            new_slot_name(&paths, "wishbringer", "incell").unwrap(),
            "incell"
        );

        // The game's own checkpoint is not a name a player can take: `/save` already writes it.
        let err = new_slot_name(&paths, "wishbringer", "wishbringer").unwrap_err();
        assert!(err.contains("own checkpoint"), "{err}");

        // A name that exists is refused, and the refusal says enough to choose again.
        std::fs::write(paths.named_checkpoint("wishbringer", "incell"), b"x").unwrap();
        let record = SaveRecord {
            game: "wishbringer".to_string(),
            saved_at: "2026-09-19T04:29:19Z".to_string(),
            reason: "manual".to_string(),
            turn: 208,
            run: 2,
            generation: 1,
            bytes: 944,
            interpreter: None,
            position: None,
        };
        write_record_beside(&paths.named_checkpoint("wishbringer", "incell"), &record).unwrap();

        let err = new_slot_name(&paths, "wishbringer", "incell").unwrap_err();
        assert!(err.contains("already a save called `incell`"), "{err}");
        assert!(err.contains("turn 208"), "{err}");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn slots_are_the_checkpoint_first_and_then_the_named_ones_in_order() {
        let dir = std::env::temp_dir().join("infocompanion-slots-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        ensure_game_dir(&paths, "wishbringer").unwrap();

        // Nothing played yet is nothing to list, and that is not an error.
        assert!(slots(&paths, "wishbringer").is_empty());

        std::fs::write(paths.checkpoint("wishbringer"), b"c").unwrap();
        std::fs::write(paths.named_checkpoint("wishbringer", "troll"), b"t").unwrap();
        std::fs::write(paths.named_checkpoint("wishbringer", "incell"), b"i").unwrap();
        // A file that is not a save at all is not listed as one.
        std::fs::write(paths.game_saves_dir("wishbringer").join("notes.txt"), b"n").unwrap();

        let found = slots(&paths, "wishbringer");
        let names: Vec<&str> = found.iter().map(|slot| slot.name.as_str()).collect();
        assert_eq!(names, vec!["wishbringer", "incell", "troll"]);
        assert!(found[0].canonical, "the game's own checkpoint comes first");
        assert!(!found[1].canonical);

        // The record is the core's note rather than the position, so a save without one is still a
        // save, and says so instead of pretending.
        assert_eq!(found[1].described(), "no record beside it");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn ensure_creates_the_tree_and_seeds_a_template_once() {
        let dir = std::env::temp_dir().join("infocompanion-state-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        assert!(paths.saves_dir().is_dir());
        assert!(paths.notebooks_dir().is_dir());
        assert!(paths.logs_dir().is_dir());

        let notebook = ensure_notebook(&paths, "advent", Some(5)).unwrap();
        let text = std::fs::read_to_string(&notebook).unwrap();
        assert!(text.contains("Notebook — advent"));
        assert!(text.contains("Z-code v5"));
        for section in [
            "Current goals and plan",
            "Map notes",
            "Key facts",
            "Open questions",
            "Dead ends",
            "Working theories",
            "Player preferences",
            "Player corrections",
            "Discarded",
        ] {
            assert!(text.contains(section), "template is missing `{section}`");
        }

        // A second call must not clobber what the agent has written.
        std::fs::write(&notebook, "the agent was here").unwrap();
        ensure_notebook(&paths, "advent", Some(5)).unwrap();
        assert_eq!(
            std::fs::read_to_string(&notebook).unwrap(),
            "the agent was here"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn delete_progress_removes_checkpoint_and_notebook_but_is_idempotent() {
        let dir = std::env::temp_dir().join("infocompanion-reset-test");
        let _ = std::fs::remove_dir_all(&dir);
        let paths = StatePaths::new(&dir);
        paths.ensure().unwrap();
        ensure_game_dir(&paths, "advent").unwrap();
        ensure_game_dir(&paths, "witness").unwrap();

        std::fs::write(paths.checkpoint("advent"), b"saved").unwrap();
        std::fs::write(paths.staged_checkpoint("advent"), b"staged").unwrap();
        ensure_notebook(&paths, "advent", Some(5)).unwrap();
        // Another game's progress must be untouched.
        std::fs::write(paths.checkpoint("witness"), b"saved").unwrap();

        let removed = delete_progress(&paths, "advent").unwrap();
        assert_eq!(removed.len(), 3);
        assert!(!paths.has_checkpoint("advent"));
        assert!(!paths.notebook("advent").exists());
        assert!(paths.has_checkpoint("witness"), "other games are untouched");

        // Running it again is not an error.
        assert!(delete_progress(&paths, "advent").unwrap().is_empty());

        let _ = std::fs::remove_dir_all(&dir);
    }
}
