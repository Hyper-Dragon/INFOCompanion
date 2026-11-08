//! Reading keys from a terminal seat without mistaking a keypress for text.
//!
//! ssh puts the client's terminal into raw mode, so everything except a plain letter
//! arrives as an ANSI escape sequence: `ESC [ A` for Up, `ESC [ 1 ; 3 D` for Alt+Left,
//! `ESC O P` for F1. Only the printable bytes of a line belong in a line — and the *rest
//! of an escape sequence is printable*, so treating `ESC` as "ignore this one byte" files
//! `[1;3D` as if the player had typed it.
//!
//! That is not hypothetical. A player reached for Alt+Left and Alt+Down in the game window
//! and Quill was handed the line `[1;3D[1;3B`, and said, reasonably, "that came through as
//! stray keypresses — nothing readable". This module is the fix, and the reason it is a
//! state machine rather than a byte filter is that a sequence can arrive split across
//! reads: `ESC` in one packet and `[1;3D` in the next.
//!
//! The reader is per-connection state, so a pane that is halfway through a sequence cannot
//! corrupt another pane.

/// What one byte from a terminal means to the session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyEvent {
    /// A printable character: goes into the line, and is echoed.
    Text(u8),
    /// Nothing to do: a control byte, or another byte of an escape sequence.
    Ignored,
    /// Erase the last character of the line.
    Backspace,
    /// Move the caret one character towards the start of the line.
    Left,
    /// Move the caret one character towards the end of the line.
    Right,
    /// Submit the line.
    Enter,
    /// Abandon the line, keeping the session.
    Interrupt,
    /// End of input. On an empty line this is how a terminal says goodbye.
    Eof,
    /// **Tab: the rest of what is being written, now.** Not text and never part of the line — it is
    /// the one key whose whole job is to stop the session metering what it is showing
    /// (03_session.md §5 (`S38.1`)).
    SkipAhead,
}

/// How long an escape sequence may run before we give up on it.
///
/// The longest real sequences are a few bytes; the cap is here so that a truncated or
/// hostile sequence (an unterminated `ESC ]` string, say) cannot swallow the session.
const MAX_SEQUENCE: usize = 32;

/// A byte-at-a-time reader that never mistakes a keypress for text.
#[derive(Debug, Default)]
pub struct KeyReader {
    state: State,
    seen: usize,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
enum State {
    /// Ordinary input.
    #[default]
    Ground,
    /// Just seen `ESC`; the next byte says what kind of sequence this is.
    Escape,
    /// Inside `ESC [ …` (CSI) or `ESC O …` (SS3): parameters until a final byte.
    Control,
    /// Inside `ESC ] …` (OSC) or `ESC P …` (DCS): runs until a terminator.
    Text,
    /// Inside a string and just seen `ESC`; `ESC \` ends it.
    TextEscape,
}

impl KeyReader {
    pub fn new() -> KeyReader {
        KeyReader::default()
    }

    /// Read one byte, and say what it means.
    pub fn feed(&mut self, byte: u8) -> KeyEvent {
        self.seen += 1;
        if self.seen > MAX_SEQUENCE {
            // A sequence that never ended. Give up on it and start clean, so a broken
            // stream costs us one sequence rather than the rest of the session.
            self.reset();
        }

        match self.state {
            State::Ground => self.read_plain(byte),

            State::Escape => match byte {
                b'[' | b'O' => {
                    self.state = State::Control;
                    KeyEvent::Ignored
                }
                b']' | b'P' | b'^' | b'_' => {
                    self.state = State::Text;
                    KeyEvent::Ignored
                }
                b'\\' => {
                    // A stray string terminator: nothing to close.
                    self.reset();
                    KeyEvent::Ignored
                }
                // `ESC` followed by anything else is either the Esc key or an Alt
                // combination. We bind no Alt keys, so the byte is read as ordinary input
                // rather than dropped: pressing Esc and then typing must not eat a letter.
                _ => {
                    self.reset();
                    self.read_plain(byte)
                }
            },

            State::Control => {
                // Parameters and intermediates are 0x20–0x3F; the first byte in 0x40–0x7E
                // is the final byte, which ends the sequence.
                if (0x40..=0x7e).contains(&byte) {
                    self.reset();
                    // The two arrows a line has a use for. `ESC [ D` and `ESC O D` are the
                    // same key in two encodings, and a modified arrow (`ESC [ 1 ; 3 D`,
                    // Alt+Left) is still a left arrow — moving the caret is all this pane
                    // could do with it anyway. Everything else in this namespace stays
                    // ignored, Up and Down included: there is no history to walk, and a key
                    // that does nothing must not look like a character.
                    return match byte {
                        b'D' => KeyEvent::Left,
                        b'C' => KeyEvent::Right,
                        _ => KeyEvent::Ignored,
                    };
                }
                KeyEvent::Ignored
            }

            State::Text => {
                match byte {
                    // BEL ends an OSC string, and is also the terminator a terminal uses
                    // for its own title.
                    0x07 => self.reset(),
                    0x1b => self.state = State::TextEscape,
                    _ => {}
                }
                KeyEvent::Ignored
            }

            State::TextEscape => {
                self.state = match byte {
                    b'\\' => State::Ground,
                    _ => State::Text,
                };
                KeyEvent::Ignored
            }
        }
    }

    /// Read one byte as ordinary input.
    fn read_plain(&mut self, byte: u8) -> KeyEvent {
        match byte {
            b'\r' | b'\n' => KeyEvent::Enter,
            0x7f | 0x08 => KeyEvent::Backspace,
            // Ctrl-C: a terminal's "stop what you are doing".
            0x03 => KeyEvent::Interrupt,
            // Ctrl-D: end of input.
            0x04 => KeyEvent::Eof,
            // Tab: "the rest of it, now". Read as a key of its own rather than as a control byte to
            // ignore, because a pane has something to do with it — and it is not text, so it can
            // never become part of a line and the story never sees it.
            0x09 => KeyEvent::SkipAhead,
            0x1b => {
                self.state = State::Escape;
                self.seen = 0;
                KeyEvent::Ignored
            }
            // A printable byte is text. Everything else — other control bytes, and bytes
            // above ASCII, which are only ever the tail of a multi-byte character we have
            // no cursor model for — is ignored rather than echoed into the line.
            b if (0x20..0x7f).contains(&b) => KeyEvent::Text(b),
            _ => KeyEvent::Ignored,
        }
    }

    fn reset(&mut self) {
        self.state = State::Ground;
        self.seen = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Read a whole chunk, as one `data()` call would.
    fn read(reader: &mut KeyReader, bytes: &[u8]) -> Vec<KeyEvent> {
        bytes.iter().map(|byte| reader.feed(*byte)).collect()
    }

    fn text(s: &str) -> Vec<KeyEvent> {
        s.bytes().map(KeyEvent::Text).collect()
    }

    #[test]
    fn plain_text_is_text() {
        let mut reader = KeyReader::new();
        assert_eq!(read(&mut reader, b"open the grate"), text("open the grate"));
    }

    #[test]
    fn tab_is_the_key_that_asks_for_the_rest() {
        let mut reader = KeyReader::new();
        // In the middle of a line, so both claims are one read: it is a key of its own, and the
        // bytes either side of it are still text.
        let events = read(&mut reader, b"a\tb");
        assert_eq!(
            events,
            vec![
                KeyEvent::Text(b'a'),
                KeyEvent::SkipAhead,
                KeyEvent::Text(b'b')
            ]
        );
        assert!(
            !events
                .iter()
                .any(|event| matches!(event, KeyEvent::Text(b'\t'))),
            "and it can never become part of the line, so the story never sees it"
        );
    }

    #[test]
    fn an_arrow_key_is_not_text() {
        let mut reader = KeyReader::new();
        // Up, then Down.
        assert_eq!(
            read(&mut reader, b"\x1b[A\x1b[B"),
            vec![KeyEvent::Ignored; 6]
        );
    }

    #[test]
    fn the_measured_bug_is_gone() {
        // The exact bytes a player sent by reaching for Alt+Left and Alt+Down, which the
        // session log recorded as the player's line `[1;3D[1;3B`. Every byte of both
        // sequences must be swallowed, leaving nothing to submit.
        let mut reader = KeyReader::new();
        let events = read(&mut reader, b"\x1b[1;3D\x1b[1;3B");

        // Alt+Left is still a left arrow, so the sequence ends in a `Left`; the rest of both
        // sequences is nothing at all. The claim that matters is unchanged.
        assert_eq!(events.iter().filter(|e| **e == KeyEvent::Left).count(), 1);
        assert!(events
            .iter()
            .all(|event| matches!(event, KeyEvent::Left | KeyEvent::Ignored)));
        assert!(
            !events
                .iter()
                .any(|event| matches!(event, KeyEvent::Text(_))),
            "no part of an escape sequence may reach the line buffer"
        );
        // The symptom was a session log with the player's line `[1;3D[1;3B` in it.
    }

    #[test]
    fn left_and_right_move_the_caret_while_up_and_down_do_nothing() {
        // The editing keys, in both encodings, and the keys that would promise a history this
        // pane does not have. The reader is byte-by-byte by design: the introducing bytes are
        // ignored and the final byte is the key.
        let mut reader = KeyReader::new();
        assert_eq!(
            read(&mut reader, b"\x1b[D"),
            vec![KeyEvent::Ignored, KeyEvent::Ignored, KeyEvent::Left]
        );
        assert_eq!(
            read(&mut reader, b"\x1b[C"),
            vec![KeyEvent::Ignored, KeyEvent::Ignored, KeyEvent::Right]
        );
        let mut reader = KeyReader::new();
        assert_eq!(
            read(&mut reader, b"\x1bOD"),
            vec![KeyEvent::Ignored, KeyEvent::Ignored, KeyEvent::Left]
        );
        assert_eq!(
            read(&mut reader, b"\x1bOC"),
            vec![KeyEvent::Ignored, KeyEvent::Ignored, KeyEvent::Right]
        );
        assert_eq!(
            read(&mut reader, b"\x1b[A\x1b[B\x1b[H\x1b[3~"),
            vec![KeyEvent::Ignored; 13]
        );
    }

    #[test]
    fn a_sequence_split_across_reads_is_still_one_keystroke() {
        // ssh hands us whatever arrived: `ESC` can land in one read and `[1;3D` in the
        // next, so the reader has to remember where it was.
        let mut reader = KeyReader::new();
        assert_eq!(read(&mut reader, b"\x1b"), vec![KeyEvent::Ignored]);
        assert_eq!(read(&mut reader, b"["), vec![KeyEvent::Ignored]);
        assert_eq!(
            read(&mut reader, b"1;3D"),
            vec![
                KeyEvent::Ignored,
                KeyEvent::Ignored,
                KeyEvent::Ignored,
                KeyEvent::Left
            ]
        );
        // …and text after it is text again.
        assert_eq!(read(&mut reader, b"x"), text("x"));
    }

    #[test]
    fn function_keys_go_the_same_way() {
        // F1 in two common encodings, and the Home/Delete family.
        for sequence in [
            &b"\x1bOP"[..],
            &b"\x1b[11~"[..],
            &b"\x1b[1~"[..],
            &b"\x1b[3~"[..],
            &b"\x1b[5~"[..],
            &b"\x1b[H"[..],
            &b"\x1b[Z"[..],
        ] {
            let mut reader = KeyReader::new();
            let events = read(&mut reader, sequence);
            assert!(
                !events
                    .iter()
                    .any(|event| matches!(event, KeyEvent::Text(_))),
                "{sequence:?} leaked text"
            );
        }
    }

    #[test]
    fn an_osc_string_is_swallowed_whole() {
        // A terminal answering a title query sends `ESC ] 0 ; … BEL`, or ends with ST.
        let mut reader = KeyReader::new();
        let events = read(&mut reader, b"\x1b]0;some title\x07ok");
        assert_eq!(
            &events[..events.len() - 2],
            vec![KeyEvent::Ignored; events.len() - 2]
        );
        assert_eq!(&events[events.len() - 2..], text("ok"));

        let mut reader = KeyReader::new();
        let events = read(&mut reader, b"\x1b]0;title\x1b\\later");
        assert_eq!(
            &events[..events.len() - 5],
            vec![KeyEvent::Ignored; events.len() - 5]
        );
        assert_eq!(&events[events.len() - 5..], text("later"));
    }

    #[test]
    fn escape_and_then_typing_does_not_eat_the_letter() {
        // The Esc key on its own is ambiguous with Alt: we bind no Alt keys, so a byte that
        // is not the start of a sequence is read as input. Otherwise pressing Esc and then
        // typing would silently lose a character.
        let mut reader = KeyReader::new();
        assert_eq!(
            read(&mut reader, b"\x1ba"),
            vec![KeyEvent::Ignored, KeyEvent::Text(b'a')]
        );
    }

    #[test]
    fn enter_backspace_and_the_ctrl_keys_mean_what_they_say() {
        let mut reader = KeyReader::new();
        assert_eq!(read(&mut reader, b"\r"), vec![KeyEvent::Enter]);
        assert_eq!(read(&mut reader, b"\n"), vec![KeyEvent::Enter]);
        assert_eq!(read(&mut reader, b"\x7f"), vec![KeyEvent::Backspace]);
        assert_eq!(read(&mut reader, b"\x08"), vec![KeyEvent::Backspace]);
        assert_eq!(read(&mut reader, b"\x03"), vec![KeyEvent::Interrupt]);
        assert_eq!(read(&mut reader, b"\x04"), vec![KeyEvent::Eof]);
    }

    #[test]
    fn ctrl_letters_other_than_c_and_d_are_not_text() {
        let mut reader = KeyReader::new();
        for byte in [0x01u8, 0x02, 0x05, 0x06, 0x07, 0x0b, 0x0c, 0x0e, 0x1a, 0x1c] {
            assert_eq!(
                read(&mut reader, &[byte]),
                vec![KeyEvent::Ignored],
                "0x{byte:02x} must not be text"
            );
        }
    }

    #[test]
    fn an_unterminated_sequence_does_not_swallow_the_session() {
        // A truncated OSC would otherwise eat everything after it.
        let mut reader = KeyReader::new();
        let mut junk = vec![0x1b, b']'];
        junk.extend(std::iter::repeat_n(b'x', MAX_SEQUENCE * 2));
        let events = read(&mut reader, &junk);

        let tail = read(&mut reader, b"look");
        assert_eq!(
            tail,
            text("look"),
            "the reader must recover and pass text again"
        );
        assert!(events.len() > MAX_SEQUENCE);
    }

    #[test]
    fn a_sequence_between_words_leaves_the_words_alone() {
        // The realistic case: type, reach for a key, keep typing.
        let mut reader = KeyReader::new();
        let mut events = read(&mut reader, b"take la");
        events.extend(read(&mut reader, b"\x1b[D\x1b[D"));
        events.extend(read(&mut reader, b"mp\r"));

        assert_eq!(
            events,
            [
                text("take la"),
                // Two left-arrows: six bytes, two of which are the key.
                vec![
                    KeyEvent::Ignored,
                    KeyEvent::Ignored,
                    KeyEvent::Left,
                    KeyEvent::Ignored,
                    KeyEvent::Ignored,
                    KeyEvent::Left
                ],
                text("mp"),
                vec![KeyEvent::Enter]
            ]
            .concat()
        );
    }

    #[test]
    fn bytes_above_ascii_are_not_guessed_at() {
        // A multi-byte character arrives as a lead byte plus continuations, and we have no
        // cursor model to place it with; better silent than mangled.
        let mut reader = KeyReader::new();
        assert_eq!(
            read(&mut reader, "é".as_bytes()),
            vec![KeyEvent::Ignored, KeyEvent::Ignored]
        );
    }
}
