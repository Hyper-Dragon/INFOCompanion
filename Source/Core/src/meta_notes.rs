//! The companion's notes about the harness, the session and itself (05_memory.md §7 (`M37`–`M39`)).
//!
//! The **writer** is the agent: a tool call during play, plus lines the harness writes for itself
//! when something fails and the model has no way to know. This module is only the **reader** — the
//! half a person uses at a keyboard, `/meta [n]` — and it is deliberately a reader and nothing
//! else: it parses nothing out of the file, summarises nothing, and never lets any of it near the
//! prompt. A note that could steer a turn would be a second way into the game that no test covers.
//!
//! What the notes are for is the question the other files cannot answer. The notebook holds what
//! is true *in the story*; the memories hold who the *player* is; the report is a document for
//! somebody coming back to the game. None of them is a place for *"the harness lost the world at
//! the cliff, and my note was ahead of a restore rather than wrong"* — which is a workaround for a
//! defect, and was measured sitting in the memories file on 2026-09-20, where the next game's model
//! reads it as a fact about the person it is playing with.
//!
//! So this is the one file in the state directory written *for whoever is debugging*. Two rules
//! come with that, and both are in the renderer:
//!
//! * **Newest last, and only the tail.** A session writes a handful of notes and the interesting
//!   one is almost always the last, so `/meta` prints the end of the file by default.
//! * **It is text, not markup.** The file is markdown the companion writes and a person reads, so
//!   the pane shows it as it is — except for control characters, which are stripped, because a
//!   model's words must never be able to move a terminal's cursor or repaint somebody's screen. The
//!   same rule the palette applies to colour: text arriving from a file gets to say what it means
//!   and not what the terminal does.

use std::path::Path;

/// How many lines `/meta` prints when it is not told a number.
///
/// Larger than `/turns`'s fifteen because a note is prose: it arrives as a heading, a blank line and
/// a paragraph or three, so fifteen lines is one note and half of the next.
pub const DEFAULT_NOTES: usize = 40;

/// The most `/meta` will print however large a number it is given, so that `/meta 900000` cannot
/// fill a pane.
pub const MAX_NOTES: usize = 400;

/// The whole file, as the pane should read it.
///
/// Missing file, empty file and unreadable file are three different sentences: the first is the
/// ordinary state of a game nobody has made a note about, and the last is a fact about the state
/// volume that somebody needs to know.
pub fn render(path: &Path, wanted: Option<usize>) -> String {
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {
            return "I have not written any notes about the harness yet.".to_string();
        }
        Err(err) => return format!("I could not read my notes: {err}"),
    };

    let lines: Vec<String> = visible(&text);
    if lines.is_empty() {
        return "I have not written any notes about the harness yet.".to_string();
    }

    let wanted = wanted.unwrap_or(DEFAULT_NOTES).clamp(1, MAX_NOTES);
    let start = lines.len().saturating_sub(wanted);
    let showing = &lines[start..];

    let mut out = String::new();
    if start > 0 {
        out.push_str(&format!(
            "My notes about the harness — the last {} of {} lines:\n\n",
            showing.len(),
            lines.len()
        ));
    } else {
        out.push_str("My notes about the harness:\n\n");
    }

    for line in showing {
        out.push_str(line);
        out.push('\n');
    }

    if start > 0 {
        out.push_str(&format!(
            "\n(/meta {} for all of them; the file is {})\n",
            lines.len(),
            path.display()
        ));
    }

    out
}

/// The lines a person should see: the file's own explanation removed, and control characters gone.
///
/// The header is an HTML comment, so it is invisible in anything that renders markdown — which
/// means the reader that shows the file *as text* is the one that has to remove it, or the pane
/// leads with a paragraph addressed to a person who opens the file cold. Comments are removed
/// wherever they appear rather than only at the top, because a model can write one too.
fn visible(text: &str) -> Vec<String> {
    let mut lines: Vec<String> = Vec::new();
    let mut in_comment = false;
    for line in text.lines() {
        let mut kept = String::new();
        let mut rest = line;
        while !rest.is_empty() {
            if in_comment {
                match rest.find("-->") {
                    Some(end) => {
                        in_comment = false;
                        rest = &rest[end + 3..];
                    }
                    None => rest = "",
                }
                continue;
            }

            match rest.find("<!--") {
                Some(start) => {
                    kept.push_str(&rest[..start]);
                    in_comment = true;
                    rest = &rest[start + 4..];
                }
                None => {
                    kept.push_str(rest);
                    rest = "";
                }
            }
        }

        // Text arriving from a file gets to say what it means and not what the terminal does: an
        // escape sequence written into a note must never move a cursor or repaint a pane.
        let safe: String = kept
            .chars()
            .filter(|c| *c == '\t' || !c.is_control())
            .collect();
        if safe.trim().is_empty() {
            // One blank line is kept, because a note's paragraphs are separated by them and the
            // prose is unreadable without it; a run of them is not, because that is where a file's
            // own spacing turns into an empty pane.
            if lines.last().is_none_or(|last| !last.trim().is_empty()) {
                lines.push(String::new());
            }
        } else {
            lines.push(safe);
        }
    }

    while lines.last().is_some_and(|last| last.trim().is_empty()) {
        lines.pop();
    }

    lines
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write(name: &str, text: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("ic-meta-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("temp dir");
        let path = dir.join("wishbringer.md");
        std::fs::write(&path, text).expect("write");
        path
    }

    const NOTES: &str = "<!-- a note to whoever opens this file\n     over two lines -->\n\
                         \n# notes about the harness — wishbringer\n\
                         \n## harness — 2026-09-20T05:28:38Z\n\
                         \nthe write-up pass ended without a report\n";

    #[test]
    fn a_missing_file_is_the_ordinary_state_of_a_game_nobody_wrote_about() {
        let missing = std::env::temp_dir().join("ic-meta-nothing-here.md");
        let _ = std::fs::remove_file(&missing);
        assert_eq!(
            render(&missing, None),
            "I have not written any notes about the harness yet."
        );
    }

    #[test]
    fn the_readers_header_is_removed_and_the_notes_are_shown_newest_last() {
        let path = write("header", NOTES);
        let text = render(&path, None);
        assert!(
            !text.contains("a note to whoever opens this file"),
            "{text}"
        );
        assert!(text.contains("# notes about the harness — wishbringer"));
        assert!(text.contains("## harness — 2026-09-20T05:28:38Z"));
        assert!(text.contains("ended without a report"));
        // The whole file fits, so nothing is claimed to be missing.
        assert!(!text.contains("/meta "), "{text}");
    }

    #[test]
    fn only_the_tail_is_printed_and_the_rest_is_pointed_at() {
        let body: String = (1..=9).map(|n| format!("note {n}\n")).collect();
        let path = write("tail", &body);
        let text = render(&path, Some(3));
        assert!(text.contains("the last 3 of 9 lines"), "{text}");
        assert!(text.contains("note 7") && text.contains("note 9"));
        assert!(!text.contains("note 6"), "{text}");
        assert!(text.contains("/meta 9 for all of them"), "{text}");
        assert!(
            text.contains("wishbringer.md"),
            "the file itself is named: {text}"
        );
    }

    #[test]
    fn a_notes_control_characters_never_reach_the_terminal() {
        // A model's words get to say what they mean and not what the terminal does; the same rule
        // the palette applies to an escaping string from a configuration file.
        let path = write("escapes", "a note with \x1b[2Jclear-screen in it\n");
        let text = render(&path, None);
        assert!(!text.contains('\x1b'), "{text:?}");
        assert!(
            text.contains("a note with [2Jclear-screen in it"),
            "{text:?}"
        );
    }
}
