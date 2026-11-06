//! The debug bundle: everything needed to work out what happened, in one zip (07_webui.md §6.2 (`W36`–`W43`)).
//!
//! **A reader, like `/stats.json`, `/evening.txt` and the games page.** It opens `<state>`, reads,
//! and writes nothing into it — the zip exists in memory for the length of one request. That is the
//! line this program draws everywhere else, and a debug bundle has no reason to be the exception:
//! the files a debugging session needs are exactly the files the running instance has already
//! written, so there is nothing for this to *make*.
//!
//! The list is the one a person assembles by hand, in the order they regret it: the two log files,
//! the turn record, the screens, the notebooks, the notes about the harness, the saves and their
//! notes, the vocabulary each story's parser taught the companion, the configuration the instance is
//! actually running with, and the prompts that were in force. The story files are **not** here: they
//! are Infocom's, and a list of what was in the library answers the question they would have been
//! read for.
//!
//! Three properties, and each is a decision:
//!
//! * **Secrets are not in it.** The bundle is something a person mails to somebody else, so the
//!   environment is included by *name* and filtered — anything whose name looks like a key, a
//!   token, a secret or a password is dropped entirely, and the manifest says so rather than
//!   leaving the reader to wonder what a missing line would have been.
//! * **Nothing is compressed.** A store-only zip is ninety lines and no dependency, and what is in
//!   here is text: a deflate implementation would be a crate to audit, in a project whose whole
//!   build is three crates deep, to save a few hundred kilobytes on a download somebody makes once.
//! * **It says what it left out.** Every file it took is listed with its size, and every file it
//!   did not is named with the reason — over the cap, a story file, a secret. A bundle that quietly
//!   drops the one file that mattered is worse than no bundle at all.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::identity::Build;
use crate::stats;

/// The most the zip is allowed to hold.
///
/// Generous, because the logs are the point and an evening's worth is a few megabytes; not
/// unlimited, because a state directory left running for a month would otherwise produce a download
/// nobody asked for. What is dropped is named in the manifest.
pub const LIMIT: u64 = 64 * 1024 * 1024;

/// The model's key, and the older name a deployment may still carry it under.
///
/// The second is read by the companion for one release while `.env` files are renamed, so a bundle
/// has to be able to say which of the two carried the key — otherwise the reader is looking at an
/// agent that works and a manifest that says there is no key.
pub const KEY_VARIABLE: &str = "INFOCOMPANION_LLM_API_KEY";
pub const LEGACY_KEY_VARIABLE: &str = "DEEPSEEK_API_KEY";

/// One thing to put in the zip.
struct Entry {
    /// The name inside the zip: relative, forward slashes, no leading slash.
    name: String,
    bytes: Vec<u8>,
}

/// What was left out, and why.
struct Skipped {
    name: String,
    why: String,
}

/// Everything the bundle holds, before it is a zip.
pub struct Bundle {
    entries: Vec<Entry>,
    skipped: Vec<Skipped>,
    manifest: String,
}

impl Bundle {
    /// The zip, as bytes.
    pub fn zip(&self) -> Vec<u8> {
        let mut out = Vec::new();
        let mut central = Vec::new();

        for entry in &self.entries {
            let name = entry.name.as_bytes();
            let crc = crc32(&entry.bytes);
            let size = entry.bytes.len() as u32;
            let offset = out.len() as u32;

            // Local file header: version needed (20 = 2.0), no flags, method 0 (stored), no time.
            //
            // The time is deliberately zero. A zip's timestamp is a DOS one in local time with no
            // zone, so writing it would be a claim this program cannot make honestly; the manifest
            // inside carries the real thing, as ISO 8601 UTC.
            out.extend_from_slice(&0x0403_4b50u32.to_le_bytes());
            out.extend_from_slice(&20u16.to_le_bytes());
            out.extend_from_slice(&0u16.to_le_bytes());
            out.extend_from_slice(&0u16.to_le_bytes());
            out.extend_from_slice(&0u16.to_le_bytes());
            out.extend_from_slice(&0u16.to_le_bytes());
            out.extend_from_slice(&crc.to_le_bytes());
            out.extend_from_slice(&size.to_le_bytes());
            out.extend_from_slice(&size.to_le_bytes());
            out.extend_from_slice(&(name.len() as u16).to_le_bytes());
            out.extend_from_slice(&0u16.to_le_bytes());
            out.extend_from_slice(name);
            out.extend_from_slice(&entry.bytes);

            // Central directory record: the same facts, plus where the local header sits.
            central.extend_from_slice(&0x0201_4b50u32.to_le_bytes());
            central.extend_from_slice(&20u16.to_le_bytes());
            central.extend_from_slice(&20u16.to_le_bytes());
            central.extend_from_slice(&0u16.to_le_bytes());
            central.extend_from_slice(&0u16.to_le_bytes());
            central.extend_from_slice(&0u16.to_le_bytes());
            central.extend_from_slice(&0u16.to_le_bytes());
            central.extend_from_slice(&crc.to_le_bytes());
            central.extend_from_slice(&size.to_le_bytes());
            central.extend_from_slice(&size.to_le_bytes());
            central.extend_from_slice(&(name.len() as u16).to_le_bytes());
            central.extend_from_slice(&0u16.to_le_bytes()); // extra
            central.extend_from_slice(&0u16.to_le_bytes()); // comment
            central.extend_from_slice(&0u16.to_le_bytes()); // disk
            central.extend_from_slice(&0u16.to_le_bytes()); // internal attributes
            central.extend_from_slice(&0u32.to_le_bytes()); // external attributes
            central.extend_from_slice(&offset.to_le_bytes());
            central.extend_from_slice(name);
        }

        let directory_at = out.len() as u32;
        let count = self.entries.len() as u16;
        out.extend_from_slice(&central);
        out.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes()); // this disk
        out.extend_from_slice(&0u16.to_le_bytes()); // the disk the directory starts on
        out.extend_from_slice(&count.to_le_bytes());
        out.extend_from_slice(&count.to_le_bytes());
        out.extend_from_slice(&(central.len() as u32).to_le_bytes());
        out.extend_from_slice(&directory_at.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes()); // no comment
        out
    }
}

/// Everything the daemon and the companion have written, plus what they were told.
///
/// Takes directories rather than a `Settings` so it can be driven by a fixture in a test, and
/// because this is the one place in the program that has to know the shape of the state directory
/// from the outside — the daemon's own `StatePaths` is the authority and this is a reader of it,
/// which is why every path here is optional and a missing one is a line in the manifest.
pub fn build(state: &Path, prompts: &Path, games: &Path, daemon: &str) -> Bundle {
    let mut bundle = Bundle {
        entries: Vec::new(),
        skipped: Vec::new(),
        manifest: String::new(),
    };

    let mut total: u64 = 0;

    // The whole state directory, by prefix: what it is called is the daemon's business, and a
    // directory that appears after this file was written should turn up in the next bundle rather
    // than needing this list edited again.
    let mut roots: Vec<PathBuf> = vec![
        state.join("logs"),
        state.join("observations"),
        state.join("playback"),
        state.join("notebook"),
        state.join("meta"),
        state.join("runs"),
        state.join("saves"),
        state.join("catchup"),
        state.join("vocabulary"),
        state.join("world"),
    ];

    // The configuration the instance is actually running with. These three are the mounted files
    // (10_delivery.md §2 (`D5`)), so they are what the daemon read, not what it shipped with.
    for name in ["core-config.json", "webui-config.json", "agent-config.json"] {
        let path = state.join(name);
        if path.is_file() {
            roots.push(path);
        }
    }

    // The prompts in force: the engine, and whoever the companion was that evening.
    roots.push(prompts.to_path_buf());

    for root in roots {
        collect(&root, state, prompts, &mut bundle, &mut total);
    }

    // The story files are deliberately absent, and the manifest says which ones so the reader can
    // ask for a specific story rather than receive a copyrighted one in a zip.
    let library = library(games);
    bundle
        .entries
        .sort_by(|left, right| left.name.cmp(&right.name));
    bundle
        .skipped
        .sort_by(|left, right| left.name.cmp(&right.name));
    bundle.manifest = manifest(
        state,
        prompts,
        games,
        daemon,
        &bundle.entries,
        &bundle.skipped,
        total,
        &library,
    );
    let manifest_bytes = bundle.manifest.clone().into_bytes();
    bundle.entries.insert(
        0,
        Entry {
            name: "MANIFEST.md".to_string(),
            bytes: manifest_bytes,
        },
    );

    bundle
}

/// Walk one root, adding files and recording what it would not take.
fn collect(root: &Path, state: &Path, prompts: &Path, bundle: &mut Bundle, total: &mut u64) {
    let mut stack = vec![root.to_path_buf()];
    while let Some(path) = stack.pop() {
        let Ok(meta) = fs::metadata(&path) else {
            continue;
        };

        if meta.is_dir() {
            let Ok(entries) = fs::read_dir(&path) else {
                continue;
            };
            for entry in entries.flatten() {
                stack.push(entry.path());
            }
            continue;
        }

        // Symlinks and anything else that is not a plain file are left alone: a bundle that can be
        // talked into walking out of the state directory is a bundle that can be talked into
        // carrying anything on the machine.
        if !meta.is_file() {
            bundle.skipped.push(Skipped {
                name: path.display().to_string(),
                why: "not a plain file".to_string(),
            });
            continue;
        }

        let name = name_in_zip(&path, state, prompts);
        if name.starts_with("debug/") {
            // A previous bundle, were one ever written here. Nothing loops forever.
            continue;
        }

        if *total + meta.len() > LIMIT {
            bundle.skipped.push(Skipped {
                name,
                why: format!("would take the bundle past {} MB", LIMIT / 1024 / 1024),
            });
            continue;
        }

        match fs::read(&path) {
            Ok(bytes) => {
                *total += bytes.len() as u64;
                bundle.entries.push(Entry { name, bytes });
            }
            Err(err) => bundle.skipped.push(Skipped {
                name,
                why: format!("could not be read: {err}"),
            }),
        }
    }
}

/// What a file is called inside the zip: relative to whichever root it came from.
fn name_in_zip(path: &Path, state: &Path, prompts: &Path) -> String {
    if let Ok(inside) = path.strip_prefix(state) {
        return forward(inside);
    }
    if let Ok(inside) = path.strip_prefix(prompts) {
        return format!("prompts/{}", forward(inside));
    }
    forward(path)
}

fn forward(path: &Path) -> String {
    path.components()
        .map(|part| part.as_os_str().to_string_lossy().to_string())
        .collect::<Vec<String>>()
        .join("/")
}

/// The story files on disk, as names and sizes.
fn library(games: &Path) -> Vec<String> {
    let Ok(entries) = fs::read_dir(games) else {
        return Vec::new();
    };
    let mut found: Vec<String> = Vec::new();
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        let size = entry.metadata().map(|meta| meta.len()).unwrap_or(0);
        found.push(format!("{name} ({size} bytes)"));
    }
    found.sort();
    found
}

/// The manifest: what this is, what is in it, and what is not.
#[allow(clippy::too_many_arguments)]
fn manifest(
    state: &Path,
    prompts: &Path,
    games: &Path,
    daemon: &str,
    entries: &[Entry],
    skipped: &[Skipped],
    total: u64,
    library: &[String],
) -> String {
    let mut text = String::new();
    text.push_str("# INFOCompanion debug bundle\n\n");
    text.push_str(&format!(
        "Built {} UTC by `infocompanion-webui` ({}), on request from the games page.\n\n",
        stamp(SystemTime::now()),
        env!("CARGO_PKG_VERSION")
    ));

    text.push_str("## The instance\n\n");
    // The commit, first and by name (07_webui.md §6.2 (`W36`–`W43`)). Everything else in this file is only worth
    // reading if the reader knows which build wrote it — 0.1.0 is every build this repository has
    // ever made, so a bundle that says nothing else cannot be answered.
    text.push_str(&format!(
        "- build: {}\n",
        Build::of_this_binary().describe()
    ));
    text.push_str(&format!("- state directory: `{}`\n", state.display()));
    text.push_str(&format!("- prompts: `{}`\n", prompts.display()));
    text.push_str(&format!("- story library: `{}`\n", games.display()));
    text.push_str(&format!("- daemon: `{daemon}`\n\n"));

    // What the reader reaches for next, and had to assemble by hand until now (2026-09-21 UAT,
    // A11): which game this evening was, how big it was, and what it cost.
    let evening = evening(state);
    text.push_str("## The evening\n\n");
    match &evening.game {
        Some(game) => text.push_str(&format!(
            "- the game in play: `{game}` — from `current-game`, the pointer the session keeps\n"
        )),
        None => text.push_str(
            "- the game in play: nothing is playing — `current-game` is absent or empty, which is\n  \
             what a session that has never opened a story leaves behind\n",
        ),
    }
    text.push_str(&format!("- the model: {}\n", evening.model_line()));
    if let Some(turns) = evening.turns_line() {
        text.push_str(&format!("- the turns: {turns}\n"));
    }
    text.push('\n');
    text.push_str(
        "**Which game this is, and which games it is not.** `current-game` is the story the session\n\
         was actually playing, and it is **not** `INFOCOMPANION_GAME`: that variable names the story\n\
         to open with, and a session resumed from a save plays the *last played* game instead, so the\n\
         pointer wins (issue #34). `INFOCOMPANION_GAME` = `zork1` beside an evening of Wishbringer is\n\
         therefore the documented design and not a fault — and it is why this bundle carries the\n\
         artifacts of more than one game: whatever the instance opened with, switched to, or left\n\
         behind that evening is in here too.\n\n",
    );
    text.push_str(
        "**How to read the logs.** `logs/core-*.trace` is the session rendered for a person — the\n\
         screen, the companion's thinking, what was typed, every restore and every refusal, in order\n\
         and **oldest event first**, so a window is read forwards. It is the file to open first.\n\
         `logs/*.jsonl` are the same two records as one JSON object per line: the daemon's, and the\n\
         companion's own.\n\n",
    );

    text.push_str("## The environment, by name\n\n");
    text.push_str(
        "Only `INFOCOMPANION_*`, and never anything whose name looks like a credential: a bundle is\n\
         something a person sends to somebody else, so a key must not be able to travel in one by\n\
         accident. A name listed without a value is one that was withheld for that reason.\n\n",
    );
    let mut names: Vec<(String, Option<String>)> = std::env::vars()
        .filter(|(name, _)| name.starts_with("INFOCOMPANION_") && name != KEY_VARIABLE)
        .map(|(name, value)| {
            let held = looks_like_a_secret(&name);
            (name, if held { None } else { Some(value) })
        })
        .collect();
    names.sort();
    if names.is_empty() {
        text.push_str("(none set — this instance is running on the shipped defaults)\n\n");
    } else {
        for (name, value) in names {
            match value {
                Some(value) => text.push_str(&format!("- `{name}` = `{value}`\n")),
                None => text.push_str(&format!(
                    "- `{name}` = **withheld** (its name looks like a credential)\n"
                )),
            }
        }
        text.push('\n');
    }

    // The model's key, which gets a line of its own below rather than a place in the list above.
    // "The companion said nothing" is the commonest thing a person debugs, its usual cause is no key
    // at all, and a bundle that cannot answer "was there one" sends the reader back to a shell to ask
    // `docker compose config`. Whether is the whole fact; the value is never in here. The older name
    // is still read by the agent for one release, so a bundle says which of the two carried it.
    let held = |name: &str| {
        std::env::var(name)
            .ok()
            .filter(|value| !value.trim().is_empty())
            .is_some()
    };
    match (held(KEY_VARIABLE), held(LEGACY_KEY_VARIABLE)) {
        (true, _) => text.push_str(&format!("- `{KEY_VARIABLE}` = set (**withheld**)\n\n")),
        (false, true) => text.push_str(&format!(
            "- `{KEY_VARIABLE}` = not set; the model's key came from the older \
             `{LEGACY_KEY_VARIABLE}` (**withheld**) — set the new name when you next touch \
             `.env`, the old one is to be retired\n\n"
        )),
        (false, false) => text.push_str(&format!(
            "- `{KEY_VARIABLE}` = not set — the companion has no way to reach a model\n\n"
        )),
    }

    text.push_str("## What is in here\n\n");
    text.push_str(&format!(
        "{} file(s), {:.1} MB before zipping (nothing is compressed: all of it is text).\n\n",
        entries.len(),
        total as f64 / 1_048_576.0
    ));
    text.push_str("What most of it is, in four lines:\n\n");
    text.push_str(
        "- `logs/` — the daemon's and the companion's own records, a line of JSON each, in order,\n  \
         and beside them the core's `.trace`: the same session written for a person to read.\n",
    );
    text.push_str(
        "- `observations/` — every turn: what was typed and what the screen said back.\n",
    );
    text.push_str("- `playback/` — the screen itself, as the player saw it.\n");
    text.push_str(
        "- `world/` — the world as the record shows it: the places, the lines that moved between \
         them, and the values the game has printed about the things in them. Nothing in it is not \
         in `observations/`: it is derived, so it can be thrown away and rebuilt.\n\n",
    );

    for entry in entries {
        text.push_str(&format!(
            "- `{}` — {} bytes\n",
            entry.name,
            entry.bytes.len()
        ));
    }
    text.push('\n');

    text.push_str("## What is not in here\n\n");
    text.push_str(
        "- **The story files themselves.** They are Infocom's, and a bundle is not the place to\n  \
         redistribute them. The library on this instance held:\n",
    );
    if library.is_empty() {
        text.push_str("  (nothing)\n");
    } else {
        for file in library {
            text.push_str(&format!("  - {file}\n"));
        }
    }
    text.push_str(
        "\n- **Any credential**, as above. If a debugging session needs to know whether a key was\n  \
         set at all, the list says so without carrying the value.\n",
    );
    if skipped.is_empty() {
        text.push_str(
            "\n- Nothing else was left out: every file the walk found is in this bundle.\n",
        );
    } else {
        text.push_str("\n- Everything else the walk found, and why it was left out:\n\n");
        for entry in skipped {
            text.push_str(&format!("  - `{}` — {}\n", entry.name, entry.why));
        }
    }

    text
}

/// What the evening came to, read out of the files rather than from anything running (07_webui.md §6.2 (`W36`–`W43`)).
///
/// Every number here is one a reader could work out for themselves with `wc` and `tail`; the
/// manifest works it out so that the first thing they read is how big the thing in front of them
/// is. Nothing is invented: a session whose agent log holds no call says it made none, and a game
/// with no turn record says nothing about turns at all, because a printed zero reads as a fact.
struct Evening {
    /// The story the session was actually playing, from the pointer it keeps.
    game: Option<String>,
    /// The model named by the last call, and the session's own running total.
    model: Option<String>,
    calls: u64,
    cost_usd: Option<f64>,
    /// Turns recorded for the game in play.
    turns: Option<u64>,
}

impl Evening {
    /// The model line, which is the one a reader looks for first.
    fn model_line(&self) -> String {
        match (&self.model, self.cost_usd) {
            (_, None) if self.model.is_none() => {
                "no model calls in this session — the agent log holds none".to_string()
            }
            (Some(model), Some(cost)) => format!(
                "`{model}`, {} calls, {cost:.4} USD spent in this session so far",
                self.calls
            ),
            (Some(model), None) => format!("`{model}`, {} calls", self.calls),
            (None, _) => "no model calls in this session — the agent log holds none".to_string(),
        }
    }

    /// The turn line, when there is a game to attribute the turns to.
    fn turns_line(&self) -> Option<String> {
        let turns = self.turns?;
        let game = self.game.as_deref()?;
        Some(format!(
            "{turns} recorded for `{game}` in `observations/{game}.jsonl`, one line each"
        ))
    }
}

/// Assemble that, from the three files that know: the pointer, the agent's log and the turn record.
fn evening(state: &Path) -> Evening {
    let game = stats::current_game(state);
    let mut evening = Evening {
        game: game.clone(),
        model: None,
        calls: 0,
        cost_usd: None,
        turns: None,
    };

    if let Some(log) = newest(&state.join("logs"), "agent-", ".jsonl") {
        if let Ok(text) = fs::read_to_string(&log) {
            // Substring rather than a parse per line, and it cannot misfire: a `"kind":"llm_call"`
            // inside somebody's speech is written escaped, as `\"kind\":\"llm_call\"`, so the raw
            // form only ever appears at the head of an event.
            for line in text
                .lines()
                .filter(|line| line.contains("\"kind\":\"llm_call\""))
            {
                evening.calls += 1;
                if let Ok(value) = serde_json::from_str::<serde_json::Value>(line) {
                    if let Some(model) = value.get("model").and_then(|it| it.as_str()) {
                        evening.model = Some(model.to_string());
                    }
                    if let Some(cost) = value.get("cost_total_usd").and_then(|it| it.as_f64()) {
                        evening.cost_usd = Some(cost);
                    }
                }
            }
        }
    }

    if let Some(game) = game {
        let observations = state.join("observations").join(format!("{game}.jsonl"));
        if let Ok(text) = fs::read_to_string(observations) {
            evening.turns =
                Some(text.lines().filter(|line| !line.trim().is_empty()).count() as u64);
        }
    }

    evening
}

/// The newest file in a directory whose name starts and ends the way it is given.
///
/// The names carry their own timestamps — `agent-20260920T212243Z.jsonl` — so the newest is the
/// greatest by byte order: no metadata to stat and no clock to trust.
fn newest(dir: &Path, prefix: &str, suffix: &str) -> Option<PathBuf> {
    let mut found: Option<String> = None;
    for entry in fs::read_dir(dir).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with(prefix) && name.ends_with(suffix) {
            found = match found {
                Some(best) if best >= name => Some(best),
                _ => Some(name),
            };
        }
    }
    Some(dir.join(found?))
}

/// Does this variable's name look like it holds a credential?
///
/// A guess, and deliberately a generous one: the cost of withholding a harmless variable is a line
/// in the manifest saying so, and the cost of the other mistake is a key in somebody's inbox.
fn looks_like_a_secret(name: &str) -> bool {
    let upper = name.to_ascii_uppercase();
    [
        "KEY",
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "PASSWD",
        "CREDENTIAL",
        "AUTH",
    ]
    .iter()
    .any(|needle| upper.contains(needle))
}

/// A UTC stamp in the form the rest of the program uses.
fn stamp(when: SystemTime) -> String {
    let seconds = when
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_secs() as i64)
        .unwrap_or(0);
    let days = seconds.div_euclid(86_400);
    let clock = seconds.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    format!(
        "{year:04}-{month:02}-{day:02} {:02}:{:02}:{:02}",
        clock / 3_600,
        (clock % 3_600) / 60,
        clock % 60
    )
}

/// The civil date for days since the epoch. The same arithmetic the games page does for its table,
/// which is two small copies of an algorithm rather than a date library in a project with three
/// dependencies — deliberately, and both are tested.
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let days = days + 719_468;
    let era = if days >= 0 { days } else { days - 146_096 } / 146_097;
    let doe = days - (era * 146_097);
    let yoe = ((doe - (doe / 1_460)) + (doe / 36_524) - (doe / 146_096)) / 365;
    let year = yoe + (era * 400);
    let doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
    let mp = ((5 * doy) + 2) / 153;
    let day = doy - (((153 * mp) + 2) / 5) + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    (if month <= 2 { year + 1 } else { year }, month, day)
}

/// CRC-32 (IEEE 802.3), which a zip's every entry has to carry.
///
/// Bitwise rather than a 256-entry table: this runs once per file on a request somebody makes by
/// hand, and a table would be 1 KB of numbers for a bundle nobody builds twice.
fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = 0xffff_ffffu32;
    for byte in bytes {
        crc ^= *byte as u32;
        for _ in 0..8 {
            let carry = crc & 1;
            crc >>= 1;
            if carry != 0 {
                crc ^= 0xedb8_8320;
            }
        }
    }
    !crc
}

/// Write the zip somewhere a person can look at it, for the log line that says it was built.
pub fn describe(bundle: &Bundle, zip: &[u8]) -> String {
    format!(
        "debug bundle: {} file(s), {} bytes zipped",
        bundle.entries.len(),
        zip.len()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(name: &str) -> (PathBuf, PathBuf, PathBuf) {
        let root = std::env::temp_dir().join(format!("infocompanion-bundle-{name}"));
        let _ = fs::remove_dir_all(&root);
        let state = root.join("state");
        let prompts = root.join("prompts");
        let games = root.join("games");
        for dir in [
            state.join("logs"),
            state.join("observations"),
            state.join("playback"),
            state.join("notebook"),
            state.join("meta"),
            state.join("runs"),
            state.join("saves"),
            state.join("catchup"),
            state.join("vocabulary"),
            state.join("world"),
            prompts.join("persona"),
            games.clone(),
        ] {
            fs::create_dir_all(dir).unwrap();
        }
        (state, prompts, games)
    }

    #[test]
    fn the_crc_is_the_standard_one() {
        // The check vector from the CRC-32 specification: if this is wrong, every unzip tool on
        // earth says the file is corrupt, so it is worth the one assertion.
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
        assert_eq!(crc32(b""), 0);
    }

    #[test]
    fn the_bundle_carries_the_logs_the_record_and_the_screen() {
        let (state, prompts, games) = fixture("contents");
        fs::write(
            state.join("logs").join("agent-20260920T000000Z.jsonl"),
            "{}\n",
        )
        .unwrap();
        fs::write(state.join("observations").join("wishbringer.jsonl"), "{}\n").unwrap();
        fs::write(state.join("playback").join("wishbringer.log"), "screen\n").unwrap();
        fs::write(state.join("core-config.json"), "{}").unwrap();
        fs::write(prompts.join("engine.txt"), "the engine\n").unwrap();
        fs::write(prompts.join("persona").join("james.txt"), "James\n").unwrap();
        fs::write(games.join("wishbringer.z3"), "not really a story").unwrap();

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");
        let names: Vec<&str> = bundle
            .entries
            .iter()
            .map(|entry| entry.name.as_str())
            .collect();

        assert!(names.contains(&"logs/agent-20260920T000000Z.jsonl"));
        assert!(names.contains(&"observations/wishbringer.jsonl"));
        assert!(names.contains(&"playback/wishbringer.log"));
        assert!(names.contains(&"core-config.json"));
        assert!(names.contains(&"prompts/engine.txt"));
        assert!(names.contains(&"prompts/persona/james.txt"));
        assert!(names.contains(&"MANIFEST.md"));
        assert_eq!(
            names[0], "MANIFEST.md",
            "the manifest goes first: it is the way in"
        );

        // The story file is named and not carried.
        assert!(!names.iter().any(|name| name.ends_with(".z3")));
        assert!(bundle.manifest.contains("wishbringer.z3"));
    }

    #[test]
    fn the_bundle_carries_the_world_and_what_the_companion_believes_about_it() {
        // `W38`, and the reason it matters: a session's whole argument about where the companion
        // thought it was, and which world it was in, is one short file — and the hypotheses beside it
        // are the only thing in the state directory that a rebuild cannot produce (`M40`, `M43`).
        let (state, prompts, games) = fixture("world");
        fs::write(state.join("world").join("moonmist.json"), "{}\n").unwrap();
        fs::write(
            state.join("world").join("moonmist.annotations.jsonl"),
            "{}\n",
        )
        .unwrap();

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");
        let names: Vec<&str> = bundle
            .entries
            .iter()
            .map(|entry| entry.name.as_str())
            .collect();

        assert!(names.contains(&"world/moonmist.json"), "{names:?}");
        assert!(
            names.contains(&"world/moonmist.annotations.jsonl"),
            "{names:?}"
        );
    }

    #[test]
    fn a_credential_is_never_carried_and_the_manifest_says_so() {
        // The one mistake in this file that would really matter: a bundle is sent to somebody else.
        let (state, prompts, games) = fixture("secrets");
        fs::write(state.join("logs").join("agent-x.jsonl"), "{}\n").unwrap();
        std::env::set_var("INFOCOMPANION_TEST_API_KEY", "sk-do-not-ship-me");
        std::env::set_var("INFOCOMPANION_TEST_STATE", "/state");

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");

        assert!(
            !bundle.manifest.contains("sk-do-not-ship-me"),
            "a key reached the manifest"
        );
        assert!(bundle.manifest.contains("INFOCOMPANION_TEST_API_KEY"));
        assert!(bundle.manifest.contains("withheld"));
        assert!(bundle.manifest.contains("INFOCOMPANION_TEST_STATE"));
    }

    #[test]
    fn the_manifest_says_whether_a_model_key_was_set_without_carrying_it() {
        // "The companion said nothing" is the commonest thing a person debugs and its usual cause
        // is no key at all, so the bundle has to answer the question — and only the question.
        let (state, prompts, games) = fixture("model-key");
        fs::write(state.join("logs").join("agent-x.jsonl"), "{}\n").unwrap();

        std::env::remove_var(LEGACY_KEY_VARIABLE);
        std::env::set_var(KEY_VARIABLE, "sk-never-in-a-bundle");
        let set = build(&state, &prompts, &games, "127.0.0.1:2222");
        std::env::remove_var(KEY_VARIABLE);

        // The older name still carries a key for one release: a bundle says so rather than leaving
        // the reader to work out why the key they set is not the one being read.
        std::env::set_var(LEGACY_KEY_VARIABLE, "sk-old-name-never-in-a-bundle");
        let legacy = build(&state, &prompts, &games, "127.0.0.1:2222");
        std::env::remove_var(LEGACY_KEY_VARIABLE);

        let absent = build(&state, &prompts, &games, "127.0.0.1:2222");

        assert!(
            !set.manifest.contains("sk-never-in-a-bundle"),
            "the model key itself reached the manifest"
        );
        assert!(set.manifest.contains(&format!("`{KEY_VARIABLE}` = set")));
        assert!(set.manifest.contains("withheld"));
        assert!(!legacy.manifest.contains("sk-old-name-never-in-a-bundle"));
        assert!(legacy.manifest.contains(&format!(
            "the model's key came from the older `{LEGACY_KEY_VARIABLE}`"
        )));
        assert!(absent
            .manifest
            .contains(&format!("`{KEY_VARIABLE}` = not set")));
    }

    #[test]
    fn the_zip_is_one_an_unzip_tool_will_open() {
        let (state, prompts, games) = fixture("zip");
        fs::write(
            state.join("logs").join("agent-x.jsonl"),
            "{\"kind\":\"info\"}\n",
        )
        .unwrap();

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");
        let zip = bundle.zip();

        // The signatures, and the end-of-directory marker, which is what a bad writer gets wrong.
        assert_eq!(&zip[0..4], b"PK\x03\x04");
        assert!(zip.len() > 100);
        assert!(
            zip.windows(4).any(|window| window == b"PK\x01\x02"),
            "no central directory"
        );
        assert!(
            zip.windows(4).any(|window| window == b"PK\x05\x06"),
            "no end of central directory"
        );

        // The stored name and the stored bytes are both findable, which is the whole point of a
        // store-only zip: nothing here has to be interpreted to check it. The window is the
        // needle's own length — the first version of this compared a fifteen-byte window against an
        // eleven-byte name, which can never be equal.
        let name = b"MANIFEST.md";
        let body = b"{\"kind\":\"info\"}\n";
        assert!(zip.windows(name.len()).any(|window| window == name));
        assert!(zip.windows(body.len()).any(|window| window == body));
    }

    #[test]
    fn a_state_directory_that_is_not_there_is_an_empty_bundle_rather_than_an_error() {
        let root = std::env::temp_dir().join("infocompanion-bundle-missing");
        let _ = fs::remove_dir_all(&root);
        let bundle = build(
            &root.join("state"),
            &root.join("prompts"),
            &root.join("games"),
            "nowhere",
        );
        assert_eq!(bundle.entries.len(), 1, "the manifest alone");
        assert!(bundle.manifest.contains("nothing"));
    }

    #[test]
    fn a_file_over_the_cap_is_named_in_the_manifest_instead_of_silently_dropped() {
        let (state, prompts, games) = fixture("cap");
        fs::write(
            state.join("logs").join("agent-big.jsonl"),
            vec![b'x'; (LIMIT + 1) as usize],
        )
        .unwrap();

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");
        assert!(bundle
            .skipped
            .iter()
            .any(|entry| entry.name == "logs/agent-big.jsonl"));
        assert!(bundle.manifest.contains("would take the bundle past"));
    }

    #[test]
    fn the_manifest_dates_itself_in_the_form_the_rest_of_the_program_uses() {
        assert_eq!(stamp(UNIX_EPOCH), "1970-01-01 00:00:00");
        // 2025-10-20T22:40:00Z, worked out rather than guessed: the first version of this asserted
        // a time off by thirteen hours, which is the mistake the arithmetic exists to prevent.
        assert_eq!(
            stamp(UNIX_EPOCH + std::time::Duration::from_secs(1_761_000_000)),
            "2025-10-20 22:40:00"
        );
    }

    #[test]
    fn the_manifest_names_the_build_it_came_from() {
        // The highest-leverage line in the file (2026-09-21 UAT, A5): without it every other
        // observation in a bundle is unanswerable, because `0.1.0` is every build this repository
        // has ever made. It is asserted against the value this binary actually compiled in, so the
        // whole path is covered — `build.rs`, the environment hand-off, and the compile-time read —
        // and it is true of a build from a tarball as well, where the value is the honest refusal.
        let (state, prompts, games) = fixture("build-identity");
        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");

        let described = Build::of_this_binary().describe();
        assert!(
            bundle.manifest.contains(&format!("- build: {described}\n")),
            "the manifest does not name the build:\n{}",
            bundle.manifest
        );
        // And it is in the instance block, not buried: the reader finds it before the file list.
        let instance = bundle.manifest.find("## The instance").unwrap();
        let listed = bundle.manifest.find("- build: ").unwrap();
        assert!(listed > instance);
    }

    #[test]
    fn the_manifest_says_which_game_this_evening_was_and_what_it_cost() {
        // A11, from the 2026-09-21 bundle: the four things a reader reached for first were all
        // absent — which game the evening actually was (as against the story the instance is
        // configured to open with), how many calls it took, what it came to, and the fact that
        // `logs/core-*.trace` is the whole session rendered for a person to read.
        let (state, prompts, games) = fixture("evening");
        fs::write(state.join("current-game"), "wishbringer\n").unwrap();
        fs::write(
            state.join("logs").join("agent-20260920T212243Z.jsonl"),
            concat!(
                "{\"kind\":\"session_start\"}\n",
                "{\"kind\":\"llm_call\",\"model\":\"deepseek-flash\",\"cost_usd\":0.01,\"cost_total_usd\":0.01}\n",
                "{\"cost_total_usd\":0.4908,\"kind\":\"llm_call\",\"model\":\"deepseek-flash\"}\n",
            ),
        )
        .unwrap();
        fs::write(
            state.join("observations").join("wishbringer.jsonl"),
            "{\"turn\":1}\n{\"turn\":2}\n{\"turn\":3}\n",
        )
        .unwrap();

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");
        let manifest = &bundle.manifest;

        assert!(
            manifest.contains("## The evening"),
            "no evening section:\n{manifest}"
        );
        assert!(
            manifest.contains("- the game in play: `wishbringer`"),
            "{manifest}"
        );
        assert!(
            manifest.contains("`deepseek-flash`, 2 calls, 0.4908 USD"),
            "{manifest}"
        );
        assert!(
            manifest.contains("3 recorded for `wishbringer` in `observations/wishbringer.jsonl`"),
            "{manifest}"
        );
        assert!(manifest.contains("core-*.trace"), "{manifest}");
        assert!(manifest.contains("oldest event first"), "{manifest}");
        // And the reading of the environment that looks like a bug and is not (issue #34). The
        // phrase is wrapped across a line in the prose, so it is checked in its own words.
        assert!(manifest.contains("`INFOCOMPANION_GAME`"), "{manifest}");
        assert!(manifest.contains("pointer wins"), "{manifest}");
    }

    #[test]
    fn a_session_with_no_model_calls_says_so_rather_than_printing_a_zero() {
        // A zero reads as a fact about the evening. The absence of a log is not evidence that
        // nothing was spent, so the manifest says what it actually knows.
        let (state, prompts, games) = fixture("no-calls");
        fs::write(state.join("current-game"), "wishbringer\n").unwrap();

        let bundle = build(&state, &prompts, &games, "127.0.0.1:2222");

        assert!(
            bundle
                .manifest
                .contains("no model calls in this session — the agent log holds none"),
            "{}",
            bundle.manifest
        );
        // No turn record either, and nothing is claimed about turns.
        assert!(
            !bundle.manifest.contains("the turns:"),
            "{}",
            bundle.manifest
        );
    }
}
