//! How fast the words arrive (03_session.md §5 (`S35`–`S40`)).
//!
//! The player asked for the words to arrive at a controlled rate: text that appears instantly is
//! difficult to follow, and it makes the pacing of the exchange feel wrong. So there is a ladder of
//! five levels, **medium is the default**, and `veryfast` is exactly what the daemon did before this
//! existed — the words as they come off the wire.
//!
//! Two voices are metered: the game's own text and the companion's words. Everything else — the
//! harness's notices, the answers to `/` commands, the prompt in the typing pane, and the
//! companion's reasoning — arrives whole, because that text is the machine answering you rather than
//! one of the two people in the room. (Reasoning streams as it is written, which is a rate the model
//! sets, not one we can.)
//!
//! What is paced is what a pane is *shown*. The turn, the record and the transcript still take the
//! game's output whole: the point of the setting is that a person can keep up, not that the game
//! waits.
//!
//! The one thing this module cannot decide by itself is *where* the words are — a pane hands it
//! bytes that are already coloured, and they have to stay that way. So pieces are cut at word
//! boundaries and never inside an escape sequence, which is why there is a small amount of scanning
//! below rather than a call to `split_whitespace`.

use std::time::Duration;

/// One of the five levels the player can ask for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Pace {
    VerySlow,
    Slow,
    Medium,
    Fast,
    /// As fast as it comes: no metering at all.
    VeryFast,
}

impl Pace {
    /// Every level, slowest first, which is the order the ladder is described in.
    pub fn all() -> [Pace; 5] {
        [
            Pace::VerySlow,
            Pace::Slow,
            Pace::Medium,
            Pace::Fast,
            Pace::VeryFast,
        ]
    }

    /// The word for this level, which is also the verb that sets it.
    pub fn name(self) -> &'static str {
        match self {
            Pace::VerySlow => "veryslow",
            Pace::Slow => "slow",
            Pace::Medium => "medium",
            Pace::Fast => "fast",
            Pace::VeryFast => "veryfast",
        }
    }

    pub fn parse(word: &str) -> Option<Self> {
        let word = word.trim().to_ascii_lowercase();
        Self::all().into_iter().find(|pace| pace.name() == word)
    }

    /// This level's place on the ladder.
    ///
    /// The pace is held in an atomic rather than behind a lock, because the pane that reads it is
    /// writing to a socket in an async task and must never wait for anything (03_session.md §2.1 (`S11`), where
    /// the tags are held the same way for the same reason).
    pub fn index(self) -> u8 {
        Self::all()
            .iter()
            .position(|level| *level == self)
            .unwrap_or(2) as u8
    }

    /// The level at that place, or `None` for a number no level answers to.
    pub fn from_index(index: u8) -> Option<Self> {
        Self::all().get(index as usize).copied()
    }
}

/// The ladder's rates, and the ceiling that bounds it.
///
/// Rates, not a formula: the numbers came from reading speed rather than from taste (03_session.md §5 (`S35`–`S40`) —
/// 240 wpm for medium, doubling either side of it), and an operator who disagrees with any of them
/// can say so in the environment without a rebuild. That is also where an SSH-only evening sets its
/// pace, because a session with no page has no stored preferences to ask.
#[derive(Debug, Clone)]
pub struct Rates {
    veryslow: u32,
    slow: u32,
    medium: u32,
    fast: u32,
    /// A turn is delivered in at most this long, however many words are in it.
    ceiling: Duration,
}

impl Rates {
    pub fn new(veryslow: u32, slow: u32, medium: u32, fast: u32, ceiling: Duration) -> Self {
        Self {
            veryslow,
            slow,
            medium,
            fast,
            ceiling,
        }
    }

    /// Words a minute for a level, or `None` for the level that is not metered at all.
    pub fn words_per_minute(&self, pace: Pace) -> Option<u32> {
        match pace {
            Pace::VerySlow => Some(self.veryslow),
            Pace::Slow => Some(self.slow),
            Pace::Medium => Some(self.medium),
            Pace::Fast => Some(self.fast),
            Pace::VeryFast => None,
        }
    }

    /// How long one word waits.
    pub fn per_word(&self, pace: Pace) -> Duration {
        match self.words_per_minute(pace) {
            Some(words) if words > 0 => Duration::from_millis(60_000 / u64::from(words)),
            // A level with no rate, or one configured as zero, is not metered: zero is what an
            // operator writes to mean "as fast as it comes".
            _ => Duration::ZERO,
        }
    }

    /// The whole text's delay, before the ceiling is applied. For the readouts and the tests.
    pub fn whole(&self, pace: Pace, words: usize) -> Duration {
        self.per_word(pace) * words as u32
    }

    pub fn ceiling(&self) -> Duration {
        self.ceiling
    }
}

/// Cut rendered pane bytes into the pieces a pane should be shown, one at a time, with how long to
/// wait after each.///
/// A piece ends after a run of whitespace, so a piece is one word plus what follows it, and the
/// joining of every piece is the input byte for byte — nothing is added, dropped or reordered. An
/// escape sequence is never split: a piece that opens one closes it, which is what keeps a pane from
/// inheriting a colour from the middle of a word.
///
/// The ceiling is applied here rather than by the caller: once the pieces already waiting add up to
/// it, the rest of the message is handed over at once, so no line is ever late by more than it.
pub fn paced<'a>(bytes: &'a [u8], pace: Pace, rates: &Rates) -> Vec<(&'a [u8], Duration)> {
    let per_word = rates.per_word(pace);
    if per_word.is_zero() {
        // `veryfast`, and the state this daemon was in before any of this existed.
        return vec![(bytes, Duration::ZERO)];
    }

    // Which bytes are inside an escape sequence: those are neither words nor boundaries, and a
    // piece may never be cut in the middle of one.
    let mut inside_escape = vec![false; bytes.len()];
    let mut in_escape = false;
    for (index, byte) in bytes.iter().enumerate() {
        if in_escape {
            inside_escape[index] = true;
            if *byte == b'm' {
                in_escape = false;
            }
        } else if *byte == 0x1b {
            inside_escape[index] = true;
            in_escape = true;
        }
    }

    // Where each word begins. A piece runs from one word's start to the next one's, so a word
    // carries the spaces after it, and whatever comes before the first word — the line break a block
    // opens with, the colour its tag is painted in — rides along with that first word rather than
    // arriving on its own.
    let mut starts = Vec::new();
    for index in 0..bytes.len() {
        let byte = bytes[index];
        if inside_escape[index] || byte.is_ascii_whitespace() {
            continue;
        }
        let opening =
            index == 0 || inside_escape[index - 1] || bytes[index - 1].is_ascii_whitespace();
        if opening {
            starts.push(index);
        }
    }

    let mut pieces: Vec<(&[u8], Duration)> = Vec::with_capacity(starts.len().max(1));
    for (position, start) in starts.iter().enumerate() {
        let end = starts.get(position + 1).copied().unwrap_or(bytes.len());
        // The first piece begins at the beginning, so anything in front of the first word is shown
        // with it and nothing is ever left behind.
        let begin = if position == 0 { 0 } else { *start };
        pieces.push((&bytes[begin..end], per_word));
    }

    if pieces.is_empty() {
        // Nothing that reads as a word — a blank line, or an empty message. It still arrives, and it
        // costs one word's wait rather than being held back for a rate that has nothing to meter.
        let wait = if bytes.is_empty() {
            Duration::ZERO
        } else {
            per_word
        };
        pieces.push((bytes, wait));
    }

    // The ceiling: what is left when the budget runs out arrives at once.
    let ceiling = rates.ceiling();
    let mut spent = Duration::ZERO;
    for piece in pieces.iter_mut() {
        if spent >= ceiling {
            piece.1 = Duration::ZERO;
        } else if spent + piece.1 > ceiling {
            piece.1 = ceiling - spent;
        }
        spent += piece.1;
    }

    pieces
}

/// What the level in force is, in the words `/speed` and the five levels answer with.
pub fn report(pace: Pace, rates: &Rates) -> String {
    let ceiling = rates.ceiling().as_secs_f32().round() as u64;
    match rates.words_per_minute(pace) {
        Some(words) => format!(
            "pace: {} — about {words} words a minute, so a twenty-word line lands over {:.1}s. Any turn is delivered in at most {ceiling}s whatever its length.",
            pace.name(),
            rates.whole(pace, 20).as_secs_f32()
        ),
        None => format!(
            "pace: {} — the words arrive as they come off the wire, as they always did.",
            pace.name()
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_report_says_where_the_pace_is_and_what_it_costs() {
        let said = report(Pace::Slow, &rates());
        assert!(said.contains("pace: slow"), "{said}");
        assert!(said.contains("120 words a minute"), "{said}");
        assert!(said.contains("10.0s"), "{said}");
        assert!(said.contains("at most 20s"), "{said}");

        // The level that is not metered says so rather than quoting a rate it does not use.
        let fast = report(Pace::VeryFast, &rates());
        assert!(fast.contains("as they come off the wire"), "{fast}");
    }
    fn rates() -> Rates {
        Rates::new(60, 120, 240, 480, Duration::from_secs(20))
    }

    #[test]
    fn a_level_survives_a_turn_as_an_atomic() {
        // The pane reads the pace without a lock, so the level has to be a number it can store.
        for pace in Pace::all() {
            assert_eq!(Pace::from_index(pace.index()), Some(pace));
        }
        assert_eq!(Pace::from_index(9), None);
    }

    #[test]
    fn the_ladder_is_the_one_that_was_asked_for() {
        assert_eq!(Pace::parse("medium"), Some(Pace::Medium));
        assert_eq!(Pace::parse("  VERYFAST "), Some(Pace::VeryFast));
        assert_eq!(Pace::parse("slower"), None);
        assert_eq!(
            Pace::all().map(Pace::name),
            ["veryslow", "slow", "medium", "fast", "veryfast"]
        );

        // Four words a second at medium, which is the 240 wpm the reading-speed figures gave.
        assert_eq!(rates().per_word(Pace::Medium), Duration::from_millis(250));
        assert_eq!(rates().per_word(Pace::Slow), Duration::from_millis(500));
        assert_eq!(
            rates().per_word(Pace::VerySlow),
            Duration::from_millis(1000)
        );
        assert_eq!(rates().per_word(Pace::Fast), Duration::from_millis(125));
        assert_eq!(rates().per_word(Pace::VeryFast), Duration::ZERO);

        // The table's own examples: twenty words at medium is five seconds, sixty is fifteen.
        assert_eq!(rates().whole(Pace::Medium, 20), Duration::from_secs(5));
        assert_eq!(rates().whole(Pace::Medium, 60), Duration::from_secs(15));
        assert_eq!(rates().whole(Pace::Slow, 20), Duration::from_secs(10));
        assert_eq!(rates().whole(Pace::VerySlow, 20), Duration::from_secs(20));
        assert_eq!(rates().whole(Pace::Fast, 20), Duration::from_millis(2500));
    }

    #[test]
    fn veryfast_is_one_piece_and_no_waiting() {
        let text = b"\r\nthe grate looks loose\r\n";
        let pieces = paced(text, Pace::VeryFast, &rates());
        assert_eq!(pieces.len(), 1);
        assert_eq!(pieces[0].0, text.as_slice());
        assert_eq!(pieces[0].1, Duration::ZERO);
    }

    #[test]
    fn every_byte_arrives_and_in_order() {
        let text = b"one two three\r\nfour five\r\n";
        for pace in Pace::all() {
            let pieces = paced(text, pace, &rates());
            let joined: Vec<u8> = pieces
                .iter()
                .flat_map(|(piece, _)| piece.iter().copied())
                .collect();
            assert_eq!(joined, text.to_vec(), "{pace:?} lost or reordered bytes");
        }
    }

    #[test]
    fn the_words_arrive_at_the_rate_that_was_asked_for() {
        // Six words, so six pieces, and the waiting adds up to what the table says. The trailing
        // newline rides along with the last word rather than becoming a piece of its own.
        let pieces = paced(b"a b c d e f\r\n", Pace::Medium, &rates());
        assert_eq!(pieces.len(), 6);
        let total: Duration = pieces.iter().map(|(_, wait)| *wait).sum();
        assert_eq!(total, Duration::from_millis(1500));
        assert_eq!(pieces[0].0, b"a ");
        assert_eq!(pieces[5].0, b"f\r\n");
    }

    #[test]
    fn an_escape_sequence_is_never_cut_in_half() {
        // What a pane is really handed: a coloured block, one line at a time, with the tag painted
        // inside the colour. A piece boundary inside `\x1b[1;37m` would leave the rest of the pane
        // in a colour nobody chose.
        let line = b"\x1b[1;37m[Quill] the grate looks loose\x1b[0m\r\n";
        let pieces = paced(line, Pace::Slow, &rates());

        let escapes = |bytes: &[u8]| bytes.iter().filter(|byte| **byte == 0x1b).count();
        let total: usize = pieces.iter().map(|(piece, _)| escapes(piece)).sum();
        assert_eq!(total, escapes(line), "an escape went missing");

        for (piece, _) in &pieces {
            // Every piece that opens a sequence closes it: the last `\x1b` has an `m` after it.
            if let Some(opened) = piece.iter().rposition(|byte| *byte == 0x1b) {
                assert!(
                    piece[opened..].contains(&b'm'),
                    "a piece ends inside an escape sequence: {:?}",
                    String::from_utf8_lossy(piece)
                );
            }
        }

        // And the whole thing still reads as it did.
        let joined: Vec<u8> = pieces
            .iter()
            .flat_map(|(piece, _)| piece.iter().copied())
            .collect();
        assert_eq!(joined, line.to_vec());
    }

    #[test]
    fn the_ceiling_ends_the_metering_and_drops_nothing() {
        // A long turn at a slow pace: the waiting stops at the ceiling and the rest arrives at once,
        // which is what stops a reply becoming a waiting room.
        let rates = Rates::new(60, 120, 240, 480, Duration::from_secs(2));
        let text = "one two three four five six seven eight nine ten ".repeat(4);
        let pieces = paced(text.as_bytes(), Pace::VerySlow, &rates);

        let total: Duration = pieces.iter().map(|(_, wait)| *wait).sum();
        assert_eq!(
            total,
            Duration::from_secs(2),
            "the ceiling is the whole delay"
        );

        let joined: Vec<u8> = pieces
            .iter()
            .flat_map(|(piece, _)| piece.iter().copied())
            .collect();
        assert_eq!(joined, text.as_bytes().to_vec(), "no word was dropped");

        // And once it is spent, nothing waits again.
        assert!(pieces.last().expect("pieces").1.is_zero());
    }

    #[test]
    fn a_rate_of_zero_is_not_metered() {
        // What an operator writes in the environment to mean "as fast as it comes".
        let rates = Rates::new(0, 0, 0, 0, Duration::from_secs(20));
        let pieces = paced(b"the grate looks loose\r\n", Pace::Medium, &rates);
        assert_eq!(pieces.len(), 1);
        assert_eq!(pieces[0].1, Duration::ZERO);
    }
}
