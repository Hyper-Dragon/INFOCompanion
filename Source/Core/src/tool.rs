//! The interpreter's tool channel (02_game_boundary.md §2 (`G5`–`G7`)).
//!
//! dFrotzTooled reports what the Z-machine and the interpreter itself know, as one JSON
//! object per line on **stderr**, and takes commands on **stdin** in the `\`-command
//! namespace the dumb interface already had. Neither stream is new: this module is the
//! core's end of a conversation that costs no extra descriptors, no privilege and nothing
//! platform-specific, which is why a stock `dfrotz` and this one can be run side by side
//! with no special case in the game path (`frotz.rs` asks with `-v` before it commits).
//!
//! Two rules shape everything here:
//!
//! * **Additive.** An event this build does not know is ignored, not an error, so the
//!   interpreter can grow tools without a flag day. Only `hello` is load-bearing: it is
//!   what says the channel is there at all.
//! * **Facts, never text.** Nothing here matches, guesses at or interprets a word of the
//!   story. `parse` is the story's *own* parse buffer read back — the words its dictionary
//!   accepted and the ones it did not — and the status numbers are the global variables
//!   the interpreter itself puts on the status line for Versions 1 to 3.

use serde_json::Value;
use std::path::{Path, PathBuf};

/// Protocol version this build speaks. An interpreter announcing a higher one is still
/// usable: it will only add events, and unknown events are ignored.
pub const PROTOCOL: u64 = 1;

// ---------------------------------------------------------------------------
// What the interpreter says about itself
// ---------------------------------------------------------------------------

/// The handshake: which interpreter this is, and which switches it was given.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Profile {
    pub protocol: u64,
    pub build: String,
    /// Z-machine version of the story.
    pub z_version: u8,
    /// The number the story is told it is running on — the story's own behaviour can
    /// depend on this, so it belongs in the record.
    pub interpreter_number: u8,
    /// The seed given with `-s`, or -1 when the interpreter chose one.
    pub seed: i64,
    pub width: u16,
    pub height: u16,
    pub undo_slots: u32,
    pub tandy: bool,
    pub timed_input: bool,
    /// The one directory a filename prompt may write into, when one was set.
    pub restricted: Option<PathBuf>,
    /// The checkpoint `-L` was given, when the boot was a restore.
    pub restore: Option<PathBuf>,
    /// What this build offers, by name.
    pub tools: Vec<String>,
    pub stem: String,
    pub story: Option<PathBuf>,
}

impl Profile {
    /// Does the interpreter offer this tool?
    pub fn offers(&self, name: &str) -> bool {
        self.tools.iter().any(|tool| tool == name)
    }

    /// A one-line description for the run record.
    pub fn describe(&self) -> String {
        format!(
            "{} v{} (z{}, interpreter {}, seed {}, {}x{}, undo {}, tandy {}, timed input {})",
            self.build,
            self.protocol,
            self.z_version,
            self.interpreter_number,
            self.seed,
            self.width,
            self.height,
            self.undo_slots,
            if self.tandy { "on" } else { "off" },
            if self.timed_input { "on" } else { "off" },
        )
    }
}

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

/// Which shape of input the interpreter is waiting for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputKind {
    Line,
    Char,
}

impl InputKind {
    fn from_str(text: &str) -> Option<Self> {
        match text {
            "line" => Some(InputKind::Line),
            "char" => Some(InputKind::Char),
            _ => None,
        }
    }
}

/// The numbers the interpreter puts on the status line for Versions 1 to 3.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Status {
    pub version: Option<u8>,
    pub location: Option<u32>,
    pub score: Option<i64>,
    pub moves: Option<i64>,
    pub hours: Option<i64>,
    pub minutes: Option<i64>,
}

impl Status {
    /// A short human form, for the log.
    pub fn describe(&self) -> String {
        if self.location.is_none() {
            return String::new();
        }
        if let Some(hours) = self.hours {
            format!(
                "at object {} (time {:02}:{:02})",
                self.location.unwrap_or(0),
                hours,
                self.minutes.unwrap_or(0)
            )
        } else {
            format!(
                "at object {} (score {}, moves {})",
                self.location.unwrap_or(0),
                self.score.unwrap_or(0),
                self.moves.unwrap_or(0)
            )
        }
    }
}

/// One word the story's parser did or did not accept.
#[derive(Debug, Clone, PartialEq)]
pub struct Token {
    /// The word, as it stood in the story's text buffer.
    pub text: String,
    /// Did the dictionary have it? A dictionary address of zero is the Z-machine's own
    /// way of saying no.
    pub known: bool,
    pub dict: u64,
    pub letters: u32,
    /// Position in the text buffer.
    pub at: u32,
}

/// What the story's own parser made of the line (02_game_boundary.md §3 (`G9`–`G15`)).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct ParseFacts {
    /// The line as the story's text buffer held it — lower-cased, as the Z-machine
    /// requires.
    pub line: String,
    /// How many words the parser found.
    pub words: usize,
    /// The most it was allowed to find.
    pub max: usize,
    pub tokens: Vec<Token>,
}

impl ParseFacts {
    /// The words the story's dictionary does not have. This is the honest answer to "did
    /// it understand me", and it needs no knowledge of the game: the first word of a
    /// command being unknown is a different failure from the verb being accepted and the
    /// noun missing.
    pub fn unknown(&self) -> Vec<&str> {
        self.tokens
            .iter()
            .filter(|token| !token.known)
            .map(|token| token.text.as_str())
            .collect()
    }

    /// A compact form for the turn record and the log.
    pub fn describe(&self) -> String {
        if self.tokens.is_empty() {
            return "the parser found no words".to_string();
        }
        let words: Vec<String> = self
            .tokens
            .iter()
            .map(|token| {
                if token.known {
                    token.text.clone()
                } else {
                    format!("{}(unknown)", token.text)
                }
            })
            .collect();
        words.join(" ")
    }
}

/// The interpreter's own screen (02_game_boundary.md §4.2.1 (`G21`)).
///
/// This is not the same thing as what the game printed, and the difference is the whole reason
/// the dump exists. The dumb interface prints a row only when it holds a cell that has changed
/// *and* is not blank, so a form drawn once and then left alone — a licence application, a menu,
/// a status line — can be on the screen for the whole game and never appear in the output. Issue
/// #19 recorded the symptom (*"the companion has no way to answer a prompt that is not a
/// command"*); this is the answer to it.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct ScreenFacts {
    pub rows: usize,
    pub cols: usize,
    pub cursor_row: usize,
    pub cursor_col: usize,
    /// Lines scrolled off the top, as the interface counts them.
    pub hidden: i64,
    /// 0 = spans, 1 = ansi, 2 = whole screen: how much the interface is willing to print.
    pub compression: i64,
    /// Every row, verbatim, spaces and all. The blanks are what gives a form its shape.
    pub lines: Vec<String>,
}

impl ScreenFacts {
    /// The rows with something on them, trimmed, in order.
    ///
    /// This is the readable screen: enough to see a form's labels and what has been typed into
    /// it, without the padded rows that are only there to pad.
    pub fn text(&self) -> Option<String> {
        let kept: Vec<&str> = self
            .lines
            .iter()
            .map(|line| line.trim_end())
            .filter(|line| !line.trim().is_empty())
            .collect();
        if kept.is_empty() {
            return None;
        }
        Some(kept.join("\n"))
    }

    /// A short form for the log: how big, and how much of it says anything.
    pub fn describe(&self) -> String {
        let written = self
            .lines
            .iter()
            .filter(|line| !line.trim().is_empty())
            .count();
        format!(
            "{}x{}, cursor {},{} — {} of {} rows say something",
            self.cols, self.rows, self.cursor_row, self.cursor_col, written, self.rows
        )
    }
}

/// One line from the interpreter.
#[derive(Debug, Clone, PartialEq)]
pub enum Event {
    /// The interpreter introduced itself. Nothing works before this.
    Hello(Box<Profile>),
    /// The interpreter is at a keyboard read with everything printed and flushed.
    ///
    /// `out_bytes` is the total it has written to stdout, which lets the core know when it
    /// has received the whole turn instead of waiting for the output to go quiet.
    InputWanted {
        kind: InputKind,
        continued: bool,
        out_bytes: u64,
        after_restore: bool,
        status: Status,
    },
    /// An input line (or key) was handed to the story.
    Input {
        text: String,
        kind: InputKind,
        after_restore: bool,
    },
    /// The story's parser ran.
    Parse(ParseFacts),
    /// The interpreter handed over its own screen.
    Screen(ScreenFacts),
    /// A save finished.
    Saved {
        path: PathBuf,
        ok: bool,
        /// Size of the file, or -1 when there is none.
        bytes: i64,
        /// Did the host name the file, rather than the interpreter asking for one?
        target: bool,
    },
    /// A restore finished.
    Restored {
        path: PathBuf,
        ok: bool,
    },
    /// One of the core's own `\t` commands was accepted.
    Command {
        verb: String,
    },
    Pong,
    /// The channel's own counters.
    Stats {
        frames: u64,
        dropped: u64,
    },
    Quit {
        status: i64,
    },
    /// A one-off note: a fatal error, a command the interpreter did not know.
    Note {
        event: String,
        detail: String,
    },
    /// A line on the channel that was not a frame — an interpreter diagnostic, which is
    /// what stderr carried before there was a channel.
    Diagnostic(String),
    /// An event this build does not know. Ignored, by design.
    Unknown(String),
}

impl Event {
    /// The event's name, for the log.
    pub fn name(&self) -> &str {
        match self {
            Event::Hello(_) => "hello",
            Event::InputWanted { .. } => "input_wanted",
            Event::Input { .. } => "input",
            Event::Parse(_) => "parse",
            Event::Screen(_) => "screen",
            Event::Saved { .. } => "saved",
            Event::Restored { .. } => "restored",
            Event::Command { .. } => "command",
            Event::Pong => "pong",
            Event::Stats { .. } => "stats",
            Event::Quit { .. } => "quit",
            Event::Note { event, .. } => event,
            Event::Diagnostic(_) => "diagnostic",
            Event::Unknown(_) => "unknown",
        }
    }
}

/// Read one line from the channel.
///
/// Anything that is not a JSON object with an `event` is a diagnostic, because that is what
/// the interpreter's stderr used to be and a stray line must never take the daemon down.
pub fn parse_line(line: &str) -> Event {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return Event::Diagnostic(String::new());
    }
    let value: Value = match serde_json::from_str(trimmed) {
        Ok(value) => value,
        Err(_) => return Event::Diagnostic(trimmed.to_string()),
    };
    let name = match value.get("event").and_then(Value::as_str) {
        Some(name) => name.to_string(),
        None => return Event::Diagnostic(trimmed.to_string()),
    };

    match name.as_str() {
        "hello" => Event::Hello(Box::new(profile_from(&value))),
        "input_wanted" => Event::InputWanted {
            kind: value
                .get("kind")
                .and_then(Value::as_str)
                .and_then(InputKind::from_str)
                .unwrap_or(InputKind::Line),
            continued: flag(&value, "continued"),
            out_bytes: number(&value, "out_bytes").unwrap_or(0) as u64,
            after_restore: flag(&value, "after_restore"),
            status: status_from(&value),
        },
        "input" => Event::Input {
            text: text(&value, "text").unwrap_or_default(),
            kind: value
                .get("kind")
                .and_then(Value::as_str)
                .and_then(InputKind::from_str)
                .unwrap_or(InputKind::Line),
            after_restore: flag(&value, "after_restore"),
        },
        "parse" => Event::Parse(ParseFacts {
            line: text(&value, "line").unwrap_or_default(),
            words: number(&value, "words").unwrap_or(0) as usize,
            max: number(&value, "max").unwrap_or(0) as usize,
            tokens: value
                .get("tokens")
                .and_then(Value::as_array)
                .map(|items| items.iter().map(token_from).collect())
                .unwrap_or_default(),
        }),
        "screen" => Event::Screen(screen_from(&value)),
        "saved" => Event::Saved {
            path: text(&value, "path").map(PathBuf::from).unwrap_or_default(),
            ok: flag(&value, "ok"),
            bytes: number(&value, "bytes").unwrap_or(-1),
            target: flag(&value, "target"),
        },
        "restored" => Event::Restored {
            path: text(&value, "path").map(PathBuf::from).unwrap_or_default(),
            ok: flag(&value, "ok"),
        },
        "command" => Event::Command {
            verb: text(&value, "verb").unwrap_or_default(),
        },
        "pong" => Event::Pong,
        "stats" => Event::Stats {
            frames: number(&value, "frames").unwrap_or(0) as u64,
            dropped: number(&value, "dropped").unwrap_or(0) as u64,
        },
        "quit" => Event::Quit {
            status: number(&value, "status").unwrap_or(0),
        },
        "error" | "fatal" => Event::Note {
            event: name,
            detail: text(&value, "detail").unwrap_or_default(),
        },
        other => Event::Unknown(other.to_string()),
    }
}

fn profile_from(value: &Value) -> Profile {
    Profile {
        protocol: number(value, "protocol").unwrap_or(0) as u64,
        build: text(value, "build").unwrap_or_default(),
        z_version: number(value, "zversion").unwrap_or(0) as u8,
        interpreter_number: number(value, "interpreter").unwrap_or(0) as u8,
        seed: number(value, "seed").unwrap_or(-1),
        width: number(value, "width").unwrap_or(0) as u16,
        height: number(value, "height").unwrap_or(0) as u16,
        undo_slots: number(value, "undo").unwrap_or(0) as u32,
        tandy: flag(value, "tandy"),
        timed_input: flag(value, "timed_input"),
        restricted: text(value, "restricted").map(PathBuf::from),
        restore: text(value, "restore").map(PathBuf::from),
        tools: text(value, "tools")
            .map(|list| list.split(',').map(|s| s.trim().to_string()).collect())
            .unwrap_or_default(),
        stem: text(value, "stem").unwrap_or_default(),
        story: text(value, "story").map(PathBuf::from),
    }
}

fn status_from(value: &Value) -> Status {
    Status {
        version: number(value, "zversion").map(|n| n as u8),
        location: number(value, "location").map(|n| n as u32),
        score: number(value, "score"),
        moves: number(value, "moves"),
        hours: number(value, "hours"),
        minutes: number(value, "minutes"),
    }
}

fn screen_from(value: &Value) -> ScreenFacts {
    ScreenFacts {
        rows: number(value, "rows").unwrap_or(0) as usize,
        cols: number(value, "cols").unwrap_or(0) as usize,
        cursor_row: number(value, "cursor_row").unwrap_or(0) as usize,
        cursor_col: number(value, "cursor_col").unwrap_or(0) as usize,
        hidden: number(value, "hidden").unwrap_or(0),
        compression: number(value, "compression").unwrap_or(0),
        lines: value
            .get("lines")
            .and_then(Value::as_array)
            .map(|items| {
                items
                    .iter()
                    .map(|item| item.as_str().unwrap_or_default().to_string())
                    .collect()
            })
            .unwrap_or_default(),
    }
}

fn token_from(value: &Value) -> Token {
    Token {
        text: text(value, "text").unwrap_or_default(),
        known: flag(value, "known"),
        dict: number(value, "dict").unwrap_or(0) as u64,
        letters: number(value, "letters").unwrap_or(0) as u32,
        at: number(value, "at").unwrap_or(0) as u32,
    }
}

fn text(value: &Value, key: &str) -> Option<String> {
    value.get(key).and_then(Value::as_str).map(str::to_string)
}

fn number(value: &Value, key: &str) -> Option<i64> {
    value.get(key).and_then(Value::as_i64)
}

fn flag(value: &Value, key: &str) -> bool {
    value.get(key).and_then(Value::as_bool).unwrap_or(false)
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

/// Every tool command starts with this, and is written to the story's stdin.
///
/// The dumb interface already intercepts `\`-commands a long way from the story — `\U` to
/// undo, `\s` to dump the screen — so a tool call rides a mechanism that has been there all
/// along, and the same lines work typed by hand at a terminal.
pub const COMMAND_PREFIX: &str = "\\t ";

/// Send the next save to this file, with no question and no restricted-path rewriting.
pub fn save_target(path: &Path) -> String {
    format!("{COMMAND_PREFIX}save {}", path.display())
}

/// Load from this file, with no question.
pub fn restore_target(path: &Path) -> String {
    format!("{COMMAND_PREFIX}restore {}", path.display())
}

/// Ask the interpreter what is on its screen.
pub fn screen() -> String {
    format!("{COMMAND_PREFIX}screen")
}

/// Ask the interpreter what it is carrying, and how many frames it has had to drop.
pub fn stats() -> String {
    format!("{COMMAND_PREFIX}stats")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_handshake_carries_the_profile() {
        let line = r#"{"seq":1,"event":"hello","protocol":1,"build":"dfrotztooled-2.54","pid":9,
            "zversion":3,"story":"/games/zork1.z3","stem":"zork1","interpreter":6,"seed":-1,
            "width":80,"height":24,"undo":500,"tandy":true,"timed_input":false,
            "restricted":"/state/saves","tools":"hello,ping,save,restore"}"#;
        let Event::Hello(profile) = parse_line(line) else {
            panic!("expected a handshake");
        };
        assert_eq!(profile.protocol, 1);
        assert_eq!(profile.z_version, 3);
        assert_eq!(profile.interpreter_number, 6);
        assert_eq!(profile.stem, "zork1");
        assert!(profile.tandy);
        assert!(profile.offers("save"));
        assert!(!profile.offers("screen"));
        assert_eq!(profile.width, 80);
        assert!(profile.describe().contains("dfrotztooled-2.54"));
    }

    #[test]
    fn a_prompt_says_how_much_screen_the_turn_is_owed() {
        let line = r#"{"seq":2,"event":"input_wanted","kind":"line","continued":false,
            "out_bytes":669,"after_restore":false,"counts_trusted":true,
            "location":180,"score":0,"moves":1}"#;
        let Event::InputWanted {
            kind,
            out_bytes,
            status,
            ..
        } = parse_line(line)
        else {
            panic!("expected a prompt");
        };
        assert_eq!(kind, InputKind::Line);
        assert_eq!(out_bytes, 669);
        assert_eq!(status.location, Some(180));
        assert_eq!(status.moves, Some(1));
        assert_eq!(status.describe(), "at object 180 (score 0, moves 1)");
    }

    #[test]
    fn the_parser_facts_name_the_words_the_story_does_not_know() {
        let line = r#"{"seq":6,"event":"parse","line":"take xyzzy","max":15,"words":2,
            "tokens":[{"dict":23530,"known":true,"letters":4,"at":2,"text":"take"},
                      {"dict":0,"known":false,"letters":5,"at":7,"text":"xyzzy"}]}"#;
        let Event::Parse(facts) = parse_line(line) else {
            panic!("expected parse facts");
        };
        assert_eq!(facts.words, 2);
        assert_eq!(facts.unknown(), vec!["xyzzy"]);
        assert_eq!(facts.describe(), "take xyzzy(unknown)");
    }

    #[test]
    fn an_event_this_build_does_not_know_is_ignored_rather_than_fatal() {
        match parse_line(r#"{"seq":40,"event":"teleport","where":"somewhere"}"#) {
            Event::Unknown(name) => assert_eq!(name, "teleport"),
            other => panic!("expected it to be ignored, got {other:?}"),
        }
    }

    #[test]
    fn a_screen_is_the_rows_the_output_never_carried() {
        // Captured from bureaucracy's licence form, which is drawn once and then left alone: the
        // dumb interface prints a row only when it has a changed cell that is not blank, so none
        // of this ever reached stdout.
        let line = r#"{"seq":9,"event":"screen","rows":24,"cols":80,"cursor_row":16,
            "cursor_col":46,"hidden":0,"compression":0,"out_bytes":771,
            "lines":["                    ","   SOFTWARE LICENCE APPLICATION   ","",
                      "   Least favourite colour: EV    ","  "],
            "changed":[0,1,0,1,0]}"#;
        let Event::Screen(facts) = parse_line(line) else {
            panic!("expected the screen");
        };
        assert_eq!(facts.rows, 24);
        assert_eq!(facts.cols, 80);
        assert_eq!(facts.cursor_row, 16);
        assert_eq!(facts.cursor_col, 46);
        assert_eq!(facts.lines.len(), 5);
        // Only the rows with something on them, and without the padding that gives a form its
        // shape: this is what a reader — a person or a model — is given.
        assert_eq!(
            facts.text().unwrap(),
            "   SOFTWARE LICENCE APPLICATION\n   Least favourite colour: EV"
        );
        assert!(facts.describe().contains("2 of 24 rows"));
    }

    #[test]
    fn a_screen_with_nothing_on_it_says_nothing_rather_than_an_empty_string() {
        let facts = ScreenFacts {
            rows: 24,
            cols: 80,
            lines: vec!["   ".to_string(), String::new()],
            ..ScreenFacts::default()
        };
        assert_eq!(facts.text(), None);
    }

    #[test]
    fn a_line_that_is_not_a_frame_is_a_diagnostic() {
        // stderr carried diagnostics long before it carried frames, and it still can:
        // an interpreter that dies on startup says so in plain words.
        match parse_line("Fatal error: Error reading save file") {
            Event::Diagnostic(text) => assert!(text.contains("Fatal error")),
            other => panic!("expected a diagnostic, got {other:?}"),
        }
        match parse_line("") {
            Event::Diagnostic(_) => {}
            other => panic!("expected a diagnostic, got {other:?}"),
        }
    }

    #[test]
    fn a_save_says_which_file_and_whether_the_host_named_it() {
        let line = r#"{"seq":12,"event":"saved","path":"/state/saves/advent/advent.sav",
            "ok":true,"bytes":852,"target":true}"#;
        let Event::Saved {
            path,
            ok,
            bytes,
            target,
        } = parse_line(line)
        else {
            panic!("expected a save");
        };
        assert_eq!(path, PathBuf::from("/state/saves/advent/advent.sav"));
        assert!(ok && target);
        assert_eq!(bytes, 852);
    }

    #[test]
    fn the_counters_say_whether_anything_was_lost() {
        let line = r#"{"seq":14,"event":"stats","frames":14,"dropped":0,"out_bytes":678}"#;
        match parse_line(line) {
            Event::Stats { frames, dropped } => {
                assert_eq!(frames, 14);
                assert_eq!(dropped, 0);
            }
            other => panic!("expected counters, got {other:?}"),
        }
        assert_eq!(stats(), "\\t stats");
    }

    #[test]
    fn commands_are_written_in_the_namespace_that_already_existed() {
        assert_eq!(
            save_target(Path::new("/state/saves/advent/advent.sav")),
            "\\t save /state/saves/advent/advent.sav"
        );
        assert_eq!(
            restore_target(Path::new("/state/saves/advent/advent.sav")),
            "\\t restore /state/saves/advent/advent.sav"
        );
        // The namespace is the interface's, so a person can type this by hand.
        assert!(save_target(Path::new("/tmp/x")).starts_with("\\t"));
    }
}
