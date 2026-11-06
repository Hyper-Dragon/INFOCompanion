//! What the core watched happen, turn by turn (issue #22).
//!
//! The core is a **witness**, not an interpreter. It sees two things and no more: the line
//! that was typed, and the text the game printed back. Everything written down here comes
//! from one of those two, and nothing is added to it.
//!
//! This replaces a *map*, and how the map failed is the useful part. To build a map the core
//! had to decide which commands were *movements* and which output was a *room* — judgements
//! about text, which is the companion's job and not the core's. So it fingerprinted `north`
//! as a place and filed `take lamp` as nothing at all. The day the companion needed to know
//! whether the lamp had already been taken, the answer was on the screen, in the transcript,
//! and **not in the record**, because the core had thrown it away for not being a place.
//!
//! ```text
//! {"turn":12,"input":"north","output":"Narrow Trail\n...","fingerprint":"Narrow Trail#9f8e7d6c"}
//! ```
//!
//! **Every turn is written down, whatever was typed.** That is the whole of the rule. A
//! refused direction, `take lamp`, a parser complaint, the story speaking by itself with no
//! command at all — all of it is evidence, and none of it is the core's to weigh.
//!
//! A **fingerprint** is offered as an *index*, never an identity: the first line of the
//! turn's output (usually a room's name, as the story printed it) plus a short hash of its
//! first paragraph. It is good for finding the last turn that looked like this one. It is not
//! a claim that matching fingerprints are the same place, nor that differing ones are not —
//! the same room read twice can differ in a single word, and what that means is the
//! companion's judgement, made from the screen rather than from here.
//!
//! Nothing in this module is story-specific. It may rely on the interpreter; it may never
//! rely on the story (02_game_boundary.md §4 (`G16`–`G24c`)), which is why there is no per-game table and no vocabulary
//! of directions.

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::Path;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

/// How much of a turn's opening line to keep in a fingerprint.
///
/// Long enough for the longest room name in the shipped stories, short enough that a
/// fingerprint still reads as a label in a terminal.
const NAME_LIMIT: usize = 48;

/// How many turns `/turns` prints when it is not told a number.
pub const DEFAULT_TURNS: usize = 15;

/// The most `/turns` will print however large a number it is given, so that a mistyped
/// `/turns 900000` cannot fill a pane.
pub const MAX_TURNS: usize = 200;

/// How much of a turn's output goes on the report line.
const PREVIEW: usize = 160;

/// One turn, exactly as the core saw it.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Observation {
    /// The game's turn number, so a turn can be found in the session log.
    pub turn: u64,
    /// The line that was written into the game. Empty when the game spoke without being
    /// asked, which happens (a prologue, a knock at the door) and is recorded as itself
    /// rather than guessed at.
    pub input: String,
    /// What the game printed, trimmed. Empty when the game said nothing at all, which is a
    /// fact in its own right: a direction that printed nothing went somewhere and did not
    /// say where.
    pub output: String,
    /// An index into the record — see the note at the top of this module.
    pub fingerprint: String,
    /// Which attempt at this game the turn belongs to (02_game_boundary.md §6 (`G27`–`G34`)).
    ///
    /// Zero means the turn predates the field, which is a fact about the record rather than a
    /// claim about a run. It is stamped because a file that mixes attempts without saying so is
    /// exactly how the companion came to cite *"the same look… labelled both turn 1 and turn
    /// 13"* — and because `recall` can only scope by run if the run is written down.
    #[serde(default)]
    pub run: u64,
    /// Which fork of the world inside that attempt the turn belongs to (02_game_boundary.md §6 (`G27`–`G34`)).
    ///
    /// A `restore` does not start a new attempt — the knowledge survives it, which is the whole
    /// reason a playbook works — but it does move the world, so where the companion *stood* before
    /// the fork is not where it stands now. Stamping it is what lets `recall` say so instead of
    /// citing a position out of a branch that was reloaded away (05_memory.md §2 (`M9`–`M11`)). Zero means the
    /// turn predates the field, exactly as for `run`.
    #[serde(default)]
    pub generation: u64,
    /// What the story's own parser made of the line, when the interpreter could tell us
    /// (02_game_boundary.md §3 (`G9`–`G15`)): the words its dictionary accepted and the ones it did not.
    ///
    /// `None` on an interpreter without the tool channel, and on a turn that took no line. It is
    /// here rather than in the transcript because it is *evidence*: "the parser knows `take` and
    /// not `lantern`" is the fact a companion uses to widen its vocabulary, and it is the one
    /// thing about a refusal that no reading of the game's prose can supply.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parser: Option<String>,
    /// The interpreter's own screen, when the story was waiting for a key rather than a command
    /// (issue #19): a form, a menu, a status line — what the class column of the terminal would
    /// show a person and what the byte stream deliberately does not.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub screen: Option<String>,
    /// Where the interpreter said the player was, as its own object number for the room (B4).
    ///
    /// The one place identity this record has. The **fingerprint is an index and never an identity**
    /// — the module note says so, and the first attempt at a map drawn from fingerprints proved it:
    /// twenty-two "places" for a handful of rooms, with `Taken.` and a line of Mr Crisp's dialogue
    /// among them, because a turn that did not move prints something too. This field is the story's
    /// own answer, and it is absent on an interpreter without the tool channel and on a story that
    /// keeps no status line (a Version 5 story draws its own) — absent meaning *cannot say*.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub location: Option<i64>,
}

/// A stable, readable index for the text a turn printed.
///
/// Deliberately built from the *first paragraph* only. A turn's output can carry more than
/// the room — an inventory, a line of dialogue — and hashing all of it would make the same
/// place look like a new one every time somebody spoke. Splitting one place into two is the
/// safe direction to be wrong in: the companion can see that two fingerprints are the same
/// place and say so, but nothing can recover a place that was merged with its neighbour.
pub fn fingerprint(output: &str) -> String {
    let paragraph = first_paragraph(output);
    if paragraph.is_empty() {
        return String::new();
    }

    let name = output
        .lines()
        .map(str::trim)
        .find(|line| !line.is_empty())
        .unwrap_or("?");
    format!("{}#{:08x}", truncate(name, NAME_LIMIT), hash(&paragraph))
}

/// The first paragraph of a turn's output, flattened to one lower-case line.
///
/// Leading blank lines are skipped rather than treated as the end of a paragraph that never
/// started: frotz occasionally emits one before a room description, and reading that as
/// "nothing here" threw away a fingerprint that was there to be had.
fn first_paragraph(output: &str) -> String {
    let mut words: Vec<&str> = Vec::new();
    let mut started = false;
    for line in output.lines() {
        if line.trim().is_empty() {
            if started {
                break;
            }
            continue;
        }
        started = true;
        words.extend(line.split_whitespace());
    }
    let mut joined = words.join(" ").to_lowercase();
    joined.truncate(512);
    joined
}

fn truncate(text: &str, limit: usize) -> String {
    if text.chars().count() <= limit {
        return text.to_string();
    }
    text.chars()
        .take(limit)
        .collect::<String>()
        .trim_end()
        .to_string()
}

/// Everything on one line, at most `limit` characters, for the report.
fn collapse(text: &str, limit: usize) -> String {
    let flat = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if flat.chars().count() <= limit {
        return flat;
    }
    let mut short: String = flat.chars().take(limit.saturating_sub(1)).collect();
    short.push('…');
    short
}

/// FNV-1a. Not a security hash and not trying to be: it only has to tell two paragraphs
/// apart, and eight hex digits makes a fingerprint readable in a note or a log.
fn hash(text: &str) -> u32 {
    let mut value: u32 = 0x811c_9dc5;
    for byte in text.as_bytes() {
        value ^= *byte as u32;
        value = value.wrapping_mul(0x0100_0193);
    }
    value
}

/// Write one turn to the game's record, creating it if needed.
///
/// A failure here is logged by the caller and never fatal: a record that cannot be written
/// is worth less than a game that keeps playing.
pub fn append(path: &Path, observation: &Observation) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }
    let line = serde_json::to_string(observation).context("failed to serialise a turn")?;
    let mut file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .with_context(|| format!("failed to open {}", path.display()))?;
    writeln!(file, "{line}").with_context(|| format!("failed to append to {}", path.display()))?;
    Ok(())
}

/// Read every turn recorded for a game.
///
/// A line that will not parse is skipped rather than failing the read: half a record that
/// can be read beats a whole one that cannot.
pub fn read(path: &Path) -> Vec<Observation> {
    let Ok(text) = fs::read_to_string(path) else {
        return Vec::new();
    };
    text.lines()
        .filter(|line| !line.trim().is_empty())
        .filter_map(|line| serde_json::from_str::<Observation>(line).ok())
        .collect()
}

/// How much of the record's tail to read when only the newest turn is wanted.
///
/// One turn's line is a few hundred bytes, so this is room for several of them. The read is not
/// required to land on a line break: a line whose beginning was not read is a line that will not
/// parse, and the reader below simply steps back to the one before it.
const TAIL: u64 = 8 * 1024;

/// The newest turn recorded for a game, without reading the whole record (B7).
///
/// Read backwards from the end rather than parsed forwards from the start, because the caller is a
/// checkpoint: a save is written *at* a turn, and by the end of an evening the record is megabytes
/// of turns that a save has no use for. `read` is for the callers that want the evening; this is for
/// the one that wants the turn in front of it.
pub fn newest(path: &Path) -> Option<Observation> {
    let mut file = File::open(path).ok()?;
    let length = file.metadata().ok()?.len();
    let start = length.saturating_sub(TAIL);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes).ok()?;

    // Lossy on purpose: a tail read can begin in the middle of a character, and a mangled first
    // line is a line that will not parse — which is the same treatment every unreadable line gets.
    let text = String::from_utf8_lossy(&bytes);
    text.lines()
        .rev()
        .filter(|line| !line.trim().is_empty())
        .find_map(|line| serde_json::from_str::<Observation>(line).ok())
}

/// The record, as a terminal can read it: the newest turns, with what was typed and what
/// came back.
///
/// This is a *report*, not an interpretation. It merges nothing, it names nothing, and it
/// never says where the player is standing — a refused `north` and a `north` into an
/// undescribed part of the world both look like "a direction that printed something", and
/// the core cannot tell them apart. So it shows what it watched and lets the companion, who
/// has read every turn, say what it adds up to.
pub fn render(observations: &[Observation], wanted: Option<usize>) -> String {
    if observations.is_empty() {
        return "I have not watched a single turn yet. Type something and I will start \
                writing it down."
            .to_string();
    }

    let wanted = wanted.unwrap_or(DEFAULT_TURNS).clamp(1, MAX_TURNS);
    let start = observations.len().saturating_sub(wanted);
    let showing = &observations[start..];

    let mut text = if start == 0 {
        format!("{} turn(s) watched:\n", observations.len())
    } else {
        format!(
            "{} turn(s) watched. The last {}:\n",
            observations.len(),
            showing.len()
        )
    };

    for observation in showing {
        let command = if observation.input.is_empty() {
            "(no command — the game spoke)".to_string()
        } else {
            format!("> {}", observation.input)
        };
        let at = if observation.fingerprint.is_empty() {
            "nothing to index: the game printed nothing".to_string()
        } else {
            observation.fingerprint.clone()
        };
        let said = if observation.output.is_empty() {
            "(nothing)".to_string()
        } else {
            collapse(&observation.output, PREVIEW)
        };
        text.push_str(&format!(
            "\n{:>4}  {command}   [{at}]\n      \"{said}\"\n",
            observation.turn
        ));
    }

    text.push_str(
        "\nEvery turn is in here, whatever was typed. The fingerprint is an index for finding \
         a turn again — not a claim about where anything is.\n",
    );

    text
}

#[cfg(test)]
mod tests {
    use super::*;

    fn observation(turn: u64, input: &str, output: &str) -> Observation {
        Observation {
            turn,
            input: input.to_string(),
            output: output.to_string(),
            fingerprint: fingerprint(output),
            run: 1,
            generation: 1,
            parser: None,
            screen: None,
            location: None,
        }
    }

    #[test]
    fn a_row_written_before_runs_were_stamped_reads_as_zero_rather_than_as_the_first_attempt() {
        // The two stamps are what let a citation say where it came from (05_memory.md §2 (`M9`–`M11`)), and the
        // records on disk predate both. Zero is "the record does not know", which is a fact about
        // the record — defaulting to one would claim every old turn came from the attempt in hand.
        let row: Observation = serde_json::from_str(
            r#"{"turn":7,"input":"look","output":"You are in a Forest.","fingerprint":"Forest#aaaa"}"#,
        )
        .expect("a row as it was written before the stamps existed");

        assert_eq!(row.run, 0);
        assert_eq!(row.generation, 0);
        assert_eq!(row.turn, 7);
    }

    #[test]
    fn both_stamps_survive_the_round_trip() {
        let row = serde_json::to_string(&Observation {
            turn: 3,
            input: "north".into(),
            output: "Well house".into(),
            fingerprint: fingerprint("Well house"),
            run: 4,
            generation: 2,
            parser: None,
            screen: None,
            location: Some(180),
        })
        .expect("serialised");

        let back: Observation = serde_json::from_str(&row).expect("parsed");
        assert_eq!((back.run, back.generation), (4, 2));
        assert_eq!(
            back.location,
            Some(180),
            "the interpreter's own place survives"
        );
    }

    // A fingerprint is an index, and these are the properties that makes it worth having.

    #[test]
    fn the_same_room_reads_the_same_way_twice() {
        let first = "Cliff Bottom\nYou're at the bottom of a cliff that rises up from the\nnorth bank.\n\nA gnarled tree trunk stands nearby.";
        let again = "Cliff Bottom\nYou're at the bottom of a cliff that rises up from the\nnorth bank.\n\nSomething else entirely, printed on a later visit.";
        assert_eq!(fingerprint(first), fingerprint(again));
    }

    #[test]
    fn the_same_room_with_somebody_talking_is_still_the_same_room() {
        // Measured: a turn that carries dialogue hashes its first paragraph, which is the
        // room, not the speech. Hashing the whole turn made every line of dialogue look
        // like a new place.
        let quiet = "Cliff Bottom\nA steep trail winds upward.\n\n>";
        let talking =
            "Cliff Bottom\nA steep trail winds upward.\n\n\"There you are, numbskull!\" he said.";
        assert_eq!(fingerprint(quiet), fingerprint(talking));
    }

    #[test]
    fn different_rooms_do_not_collide() {
        let north = "North of Bridge\nThis is the north side of the Festeron Bridge.";
        let bottom =
            "Cliff Bottom\nYou're at the bottom of a cliff that rises up from the north bank.";
        assert_ne!(fingerprint(north), fingerprint(bottom));
    }

    #[test]
    fn a_fingerprint_reads_as_a_label() {
        let room = "North of Bridge\nSheer cliff walls rise abruptly to the north and west.";
        let print = fingerprint(room);
        assert!(print.starts_with("North of Bridge#"), "{print}");
        assert_eq!(print.len(), "North of Bridge#".len() + 8);
    }

    #[test]
    fn empty_output_has_no_fingerprint() {
        assert_eq!(fingerprint(""), "");
        assert_eq!(fingerprint("\n\n   \n"), "");
    }

    #[test]
    fn a_leading_blank_line_is_not_a_paragraph_break() {
        // frotz does this, and reading it as "the turn printed nothing" threw away a
        // fingerprint that was there to be had.
        let print = fingerprint("\n\nCliff Bottom\nA steep trail winds upward.");
        assert!(print.starts_with("Cliff Bottom#"), "{print}");
    }

    // The report.

    #[test]
    fn with_nothing_recorded_it_says_so() {
        assert!(render(&[], None).contains("not watched a single turn"));
    }

    #[test]
    fn every_kind_of_turn_gets_written_down() {
        // The whole point of the module. The record it replaced kept movements and dropped
        // the rest, so `take lamp` left no trace and the answer to "have I taken the lamp?"
        // was missing from the record.
        let record = vec![
            observation(1, "look", "Cliff Bottom\nA steep trail winds upward."),
            observation(2, "take lamp", "Taken."),
            observation(3, "north", "You can't go that way."),
            observation(4, "", "Time passes..."),
        ];
        let text = render(&record, None);
        for needle in ["look", "take lamp", "north", "Time passes"] {
            assert!(text.contains(needle), "{needle} missing from {text}");
        }
    }

    #[test]
    fn a_turn_with_no_command_says_the_game_spoke() {
        let text = render(&[observation(7, "", "Time passes...")], None);
        assert!(text.contains("the game spoke"), "{text}");
    }

    #[test]
    fn a_turn_the_game_answered_with_nothing_says_so() {
        // Adventure does this constantly: `west` went somewhere and the game did not say
        // where. The record says nothing was printed rather than inventing a destination.
        let text = render(&[observation(7, "west", "")], None);
        assert!(text.contains("(nothing)"), "{text}");
    }

    #[test]
    fn the_report_shows_the_newest_turns_and_says_what_it_left_out() {
        let record: Vec<Observation> = (1..=40)
            .map(|turn| observation(turn, "look", "Cliff Bottom\nSteep."))
            .collect();
        let text = render(&record, Some(5));
        assert!(text.contains("40 turn(s) watched"), "{text}");
        assert!(text.contains("The last 5"), "{text}");
        assert!(!text.contains("  35  "), "{text}");
        assert!(text.contains("  40  "), "{text}");
    }

    #[test]
    fn asking_for_more_than_there_are_shows_them_all() {
        let record = vec![observation(1, "look", "Cliff Bottom\nSteep.")];
        let text = render(&record, Some(MAX_TURNS));
        assert!(text.contains("1 turn(s) watched:"), "{text}");
    }

    #[test]
    fn a_silly_number_does_not_break_it() {
        let record = vec![observation(1, "look", "Cliff Bottom\nSteep.")];
        assert!(render(&record, Some(0)).contains("  1  "));
        assert!(render(&record, Some(usize::MAX)).contains("  1  "));
    }

    #[test]
    fn a_long_output_is_collapsed_onto_one_line() {
        let mut output = String::from("Cliff Bottom\n");
        output.push_str(&"word ".repeat(200));
        let record = vec![observation(1, "look", &output)];
        let text = render(&record, Some(1));
        let said = text
            .lines()
            .find(|line| line.trim_start().starts_with('"'))
            .expect("a quoted line")
            .trim();
        // The quotes are the only thing on the line besides the preview itself.
        assert!(
            said.chars().count() <= PREVIEW + 2,
            "{} chars: {said}",
            said.chars().count()
        );
        assert!(said.ends_with("…\""), "{said}");
    }

    #[test]
    fn the_report_does_not_claim_the_fingerprint_is_an_identity() {
        // The wording is the guarantee: someone reading `/turns` off a phone screen must not
        // come away thinking the core has decided which turns happened in the same place.
        let text = render(&[observation(1, "look", "Cliff Bottom\nSteep.")], None);
        assert!(text.contains("index for finding"), "{text}");
        assert!(
            text.contains("not a claim about where anything is"),
            "{text}"
        );
    }

    // The file.

    #[test]
    fn a_record_survives_a_round_trip_through_the_file() {
        let dir = std::env::temp_dir().join(format!("ic-observations-{}", std::process::id()));
        let path = dir.join("test.jsonl");
        let _ = std::fs::remove_file(&path);

        let record = vec![
            observation(1, "look", "Cliff Bottom\nSteep."),
            observation(2, "", "Time passes..."),
        ];
        for item in &record {
            append(&path, item).expect("append");
        }

        let read_back = read(&path);
        assert_eq!(read_back.len(), 2);
        assert_eq!(read_back[0].input, "look");
        assert_eq!(read_back[1].input, "");
        assert_eq!(read_back[1].output, "Time passes...");
        assert_eq!(read_back[0].fingerprint, record[0].fingerprint);

        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_dir(&dir);
    }

    #[test]
    fn a_file_that_is_not_there_reads_as_nothing() {
        assert!(read(Path::new("/nonexistent/observations.jsonl")).is_empty());
        assert!(newest(Path::new("/nonexistent/observations.jsonl")).is_none());
    }

    #[test]
    fn the_newest_turn_is_the_last_one_written() {
        // B7: a checkpoint asks where the game is standing, and the answer is the last turn the
        // core watched — not the first.
        let dir =
            std::env::temp_dir().join(format!("ic-observations-newest-{}", std::process::id()));
        let path = dir.join("test.jsonl");
        let _ = std::fs::remove_file(&path);

        assert!(newest(&path).is_none(), "nothing written yet");

        append(&path, &observation(1, "look", "Cliff Bottom\nSteep.")).expect("append");
        assert_eq!(newest(&path).expect("a turn").turn, 1);

        append(&path, &observation(2, "north", "Narrow Trail\nSteep.")).expect("append");
        let last = newest(&path).expect("a turn");
        assert_eq!(last.turn, 2);
        assert_eq!(
            last.fingerprint,
            observation(2, "north", "Narrow Trail\nSteep.").fingerprint
        );

        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_dir(&dir);
    }

    #[test]
    fn the_newest_turn_is_read_from_the_tail_of_a_long_record() {
        // The whole point of reading backwards: the turn in front of a checkpoint is at the end of
        // an evening that may be megabytes long. Written past the tail window on purpose, so that a
        // reader that quietly went from the start would still find the answer — the assertion that
        // matters is that the *last* turn is the one that comes back, however it got there.
        let dir = std::env::temp_dir().join(format!("ic-observations-tail-{}", std::process::id()));
        let path = dir.join("test.jsonl");
        let _ = std::fs::remove_file(&path);

        let filler = format!("Cliff Bottom\n{}", "a long room description. ".repeat(400));
        for turn in 1..=40u64 {
            append(&path, &observation(turn, "look", &filler)).expect("append");
        }
        append(
            &path,
            &observation(41, "north", "Narrow Trail\nA steep trail."),
        )
        .expect("append");

        let last = newest(&path).expect("a turn");
        assert_eq!(last.turn, 41);
        assert_eq!(last.input, "north");
        assert!(last.fingerprint.starts_with("Narrow Trail#"), "{last:?}");

        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_dir(&dir);
    }

    #[test]
    fn a_line_that_will_not_parse_costs_only_that_line() {
        let dir = std::env::temp_dir().join(format!("ic-observations-junk-{}", std::process::id()));
        let path = dir.join("test.jsonl");
        let _ = std::fs::remove_file(&path);

        append(&path, &observation(1, "look", "Cliff Bottom\nSteep.")).expect("append");
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .expect("open")
            .write_all(b"{not json at all\n")
            .expect("write junk");
        append(&path, &observation(2, "north", "Narrow Trail\nSteep.")).expect("append");

        assert_eq!(read(&path).len(), 2);

        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_dir(&dir);
    }
}
