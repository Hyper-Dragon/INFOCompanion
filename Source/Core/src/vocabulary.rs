//! The game's own vocabulary, read out of the story file (§10.4).
//!
//! A Z-machine story file points at its **dictionary** from header bytes 0x08-0x09,
//! and that dictionary is the complete list of words the parser will recognise.
//! Reading it is exact, cheap, and per-game — which is precisely what the companion
//! needs, because the parser's vocabulary is not the same as English.
//!
//! Measured, and the reason this module exists: the live run against Adventure sent
//! `follow stream` and `downstream`, and the game answered *"That's not a verb I
//! recognise."* twice, burning two real turns before the companion fell back to a
//! compass direction. Both words are absent from Adventure's dictionary (786
//! entries). `follow` *is* present in The Witness's (711 entries) — so no fixed list
//! can be right, and only the game itself can say.
//!
//! Two details of the format matter for using the result honestly:
//!
//! * **Words are truncated to the dictionary's resolution** — 6 characters for
//!   V1-3, 9 for V4+. The Witness stores `invent` for "inventory" and `examin` for
//!   "examine", and the interpreter truncates typed words the same way, so matching
//!   has to be done on the truncated form or it will refuse words the game accepts.
//! * **Entries can use abbreviations** (5 of the 711 in The Witness), which live in
//!   a separate table and are expanded here rather than being left to rot as junk.
//!
//! Failure is never fatal: a game whose dictionary cannot be read simply has no
//! vocabulary, and the agent falls back to learning from the parser's refusals.

use std::collections::BTreeSet;
use std::path::Path;

use anyhow::{bail, Context, Result};

/// The default A0 alphabet (lowercase), used when a story does not supply one.
const DEFAULT_A0: &str = "abcdefghijklmnopqrstuvwxyz";
/// The default A1 alphabet (uppercase).
const DEFAULT_A1: &str = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
/// The default A2 alphabet (punctuation and digits).
const DEFAULT_A2: &str = " \n0123456789.,!?_#'\"/\\-:()";

/// Z-characters an abbreviation may nest through before we give up. Real stories
/// nest one level at most; the limit is here so a malformed file cannot recurse
/// forever.
const MAX_ABBREVIATION_DEPTH: u8 = 3;

/// Abbreviations are addressed by a 5-bit z-char pair: 3 x 32 entries.
const ABBREVIATION_COUNT: usize = 96;

/// A defensive ceiling on how much vocabulary is worth carrying around. No Infocom
/// story comes close; a corrupted file might claim anything.
const MAX_WORDS: usize = 8192;

/// The words a game's parser will recognise.
#[derive(Debug, Clone, Default)]
pub struct Vocabulary {
    /// Z-machine version the words were read from.
    version: u8,
    /// Z-characters of text in a dictionary entry: what a typed word is cut to.
    resolution: usize,
    /// Words, lower-cased and sorted, exactly as the dictionary stores them
    /// (which means truncated for older games).
    words: BTreeSet<String>,
}

impl Vocabulary {
    /// Read the vocabulary of the game in `path` (a story file or a Blorb).
    pub fn read(path: &Path) -> Result<Vocabulary> {
        let story = crate::games::story_bytes(path)
            .with_context(|| format!("could not read a story file at {}", path.display()))?;
        Vocabulary::from_story(&story)
            .with_context(|| format!("could not read the vocabulary of {}", path.display()))
    }

    /// Read the vocabulary from raw story bytes.
    pub fn from_story(story: &[u8]) -> Result<Vocabulary> {
        if story.len() < 64 {
            bail!("story file is too short to hold a header");
        }
        let version = story[0];
        if !(1..=8).contains(&version) {
            bail!("Z-machine version {version} is not one this reader knows");
        }

        let text_len = entry_text_bytes(version);
        let resolution = resolution(version);
        let dict_addr = be16(story, 0x08).context("no dictionary address in the header")? as usize;

        let reader = Story::new(story, version)?;

        let separator_count = *story.get(dict_addr).context("dictionary is out of range")? as usize;
        let mut at = dict_addr + 1 + separator_count;
        let entry_len = *story
            .get(at)
            .context("dictionary entry length is out of range")? as usize;
        if entry_len <= text_len {
            bail!("dictionary entry length {entry_len} leaves no room for data");
        }
        at += 1;
        let count = be16(story, at).context("dictionary entry count is out of range")? as usize;
        at += 2;

        let entries = at;
        if count > MAX_WORDS {
            bail!("dictionary claims {count} entries, which is not credible");
        }
        if entries + count * entry_len > story.len() {
            bail!("dictionary runs past the end of the story file");
        }

        let mut words = BTreeSet::new();
        for index in 0..count {
            let start = entries + index * entry_len;
            let encoded = &story[start..start + text_len];
            // Only the first word of an entry counts: the rest of the field is
            // padding, and the space that separates it is not part of the word.
            let text = reader.interpret(&reader.zchars(encoded), 0);
            let word = text.split(' ').next().unwrap_or("").trim().to_lowercase();
            if !word.is_empty() {
                words.insert(word);
            }
        }

        if words.is_empty() {
            bail!("the dictionary held no readable words");
        }

        Ok(Vocabulary {
            version,
            resolution,
            words,
        })
    }

    /// How many words the parser knows.
    pub fn len(&self) -> usize {
        self.words.len()
    }

    /// The Z-machine version the words came from.
    pub fn version(&self) -> u8 {
        self.version
    }

    /// Z-characters of text a dictionary entry holds for this game.
    pub fn resolution(&self) -> usize {
        self.resolution
    }

    /// Every word, lower-cased, in sorted order.
    ///
    /// These are the words *as stored*: for a V1-3 game they are truncated to six
    /// characters, so matching a typed word means cutting it the same way first. The
    /// agent does that with `resolution`, which travels with the words.
    pub fn words(&self) -> impl Iterator<Item = &str> {
        self.words.iter().map(String::as_str)
    }
}

/// Bytes of encoded text in a dictionary entry, by version.
fn entry_text_bytes(version: u8) -> usize {
    if version <= 3 {
        4
    } else {
        6
    }
}

/// Z-characters an entry holds: 3 z-chars per 2 bytes.
fn resolution(version: u8) -> usize {
    entry_text_bytes(version) * 3 / 2
}

fn be16(bytes: &[u8], at: usize) -> Option<u16> {
    let pair: [u8; 2] = bytes.get(at..at + 2)?.try_into().ok()?;
    Some(u16::from_be_bytes(pair))
}

/// A reader for the text encoding inside one story file: the alphabets, the
/// abbreviation table, and the z-character stream they explain.
struct Story<'a> {
    bytes: &'a [u8],
    alphabet: [Vec<char>; 3],
    abbreviations: usize,
}

impl<'a> Story<'a> {
    fn new(bytes: &'a [u8], version: u8) -> Result<Story<'a>> {
        let alphabet = read_alphabet(bytes, version);
        // Header 0x18 is the abbreviation table; each entry is a word address that
        // has to be doubled to get a byte offset.
        let abbreviations = be16(bytes, 0x18).unwrap_or(0) as usize;
        Ok(Story {
            bytes,
            alphabet,
            abbreviations,
        })
    }

    /// Unpack the 5-bit z-characters from a run of 2-byte groups, stopping after
    /// the group whose end bit is set (dictionary entries carry one too).
    fn zchars(&self, encoded: &[u8]) -> Vec<u8> {
        let mut out = Vec::with_capacity(encoded.len() / 2 * 3);
        for pair in encoded.chunks(2) {
            let Some(chunk) = pair.get(0..2) else {
                break;
            };
            let word = u16::from_be_bytes([chunk[0], chunk[1]]);
            out.push(((word >> 10) & 0x1f) as u8);
            out.push(((word >> 5) & 0x1f) as u8);
            out.push((word & 0x1f) as u8);
            if word & 0x8000 != 0 {
                break;
            }
        }
        out
    }

    /// Turn a z-character stream into text, expanding abbreviations as it goes.
    fn interpret(&self, zchars: &[u8], depth: u8) -> String {
        let mut out = String::new();
        let mut index = 0usize;
        // 0 = A0, 4 = A1, 5 = A2, 2 = A2 waiting for a 10-bit escape.
        let mut shift = 0u8;

        while index < zchars.len() {
            let z = zchars[index];
            index += 1;

            match z {
                0 => {
                    if shift == 0 {
                        out.push(' ');
                    }
                    shift = 0;
                }
                1..=3 => {
                    // Abbreviation: this z-char and the next form a 10-bit index.
                    let next = zchars.get(index).copied().unwrap_or(0);
                    index += 1;
                    shift = 0;
                    if depth < MAX_ABBREVIATION_DEPTH {
                        let entry = 32 * (z as usize - 1) + next as usize;
                        if let Some(text) = self.abbreviation(entry, depth) {
                            out.push_str(&text);
                        }
                    }
                }
                4 | 5 => shift = z,
                6 if shift == 2 => {
                    let high = zchars.get(index).copied().unwrap_or(0) as u32;
                    let low = zchars.get(index + 1).copied().unwrap_or(0) as u32;
                    index += 2;
                    if let Some(character) = char::from_u32((high << 5) | low) {
                        out.push(character);
                    }
                    shift = 0;
                }
                _ => {
                    let table = match shift {
                        4 => 1,
                        2 | 5 => 2,
                        _ => 0,
                    };
                    shift = 0;
                    if let Some(character) = self.alphabet[table].get(z as usize - 6) {
                        out.push(*character);
                    }
                }
            }
        }

        out
    }

    /// Decode one abbreviation by index.
    fn abbreviation(&self, index: usize, depth: u8) -> Option<String> {
        if index >= ABBREVIATION_COUNT || self.abbreviations == 0 {
            // No table, or a table that is not there: better to drop the fragment
            // than to read the story header as if it were one.
            return None;
        }
        let pointer = self.abbreviations.checked_add(index * 2)?;
        let address = be16(self.bytes, pointer)? as usize * 2;
        if address == 0 || address >= self.bytes.len() {
            return None;
        }

        // Read the abbreviation's own Z-string, one 2-byte group at a time, until
        // the end bit says stop.
        let mut encoded = Vec::new();
        let mut at = address;
        loop {
            let word = be16(self.bytes, at)?;
            encoded.extend_from_slice(&word.to_be_bytes());
            at += 2;
            if word & 0x8000 != 0 {
                break;
            }
            if encoded.len() > 128 {
                break; // malformed: refuse to keep reading
            }
        }

        Some(self.interpret(&self.zchars(&encoded), depth + 1))
    }
}

/// The alphabets a story uses: its own A0/A1 for V5+, the defaults before that.
fn read_alphabet(story: &[u8], version: u8) -> [Vec<char>; 3] {
    let (a0, a1) = if version >= 5 {
        match be16(story, 0x34) {
            Some(address) if address != 0 => match story.get(address as usize) {
                // Bit 7 clear means the default alphabet is in force.
                Some(bits) if bits & 0x80 != 0 => {
                    let raw = story
                        .get(address as usize + 1..address as usize + 27)
                        .unwrap_or_default();
                    let a0 = raw
                        .get(0..26)
                        .map(|slice| String::from_utf8_lossy(slice).chars().collect())
                        .unwrap_or_else(|| DEFAULT_A0.chars().collect());
                    let a1 = raw
                        .get(26..52)
                        .map(|slice| String::from_utf8_lossy(slice).chars().collect())
                        .unwrap_or_else(|| DEFAULT_A1.chars().collect());
                    (a0, a1)
                }
                _ => (DEFAULT_A0.chars().collect(), DEFAULT_A1.chars().collect()),
            },
            _ => (DEFAULT_A0.chars().collect(), DEFAULT_A1.chars().collect()),
        }
    } else {
        (DEFAULT_A0.chars().collect(), DEFAULT_A1.chars().collect())
    };

    [a0, a1, DEFAULT_A2.chars().collect()]
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    /// Encode a lowercase word into dictionary text, setting the end bit, so the
    /// tests can build a story file by hand.
    fn encode_word(word: &str, groups: usize) -> Vec<u8> {
        let mut zchars: Vec<u8> = word
            .bytes()
            .map(|b| b - b'a' + 6) // A0 starts at z-char 6
            .collect();
        zchars.resize(groups * 3, 5); // pad with z-char 5 (the last letter slot)
        let mut out = Vec::new();
        for group in zchars.chunks(3) {
            let word = ((group[0] as u16) << 10) | ((group[1] as u16) << 5) | group[2] as u16;
            out.extend_from_slice(&word.to_be_bytes());
        }
        // Set the end bit on the final group.
        let last = out.len() - 2;
        out[last] |= 0x80;
        out
    }

    /// A minimal V3 story carrying the given words as its dictionary.
    fn story_with(words: &[&str]) -> Vec<u8> {
        let text_len = 4;
        let entry_len = text_len + 3;
        let mut story = vec![0u8; 0x40];
        story[0] = 3; // version
        let dict_addr = 0x40usize;
        story[0x08..0x0a].copy_from_slice(&(dict_addr as u16).to_be_bytes());
        story[0x18..0x1a].copy_from_slice(&0u16.to_be_bytes()); // no abbreviations
        story[0x34..0x36].copy_from_slice(&0u16.to_be_bytes()); // default alphabets

        let mut dict = vec![0u8]; // no separators
        dict.push(entry_len as u8);
        dict.extend_from_slice(&(words.len() as u16).to_be_bytes());
        for word in words {
            dict.extend_from_slice(&encode_word(word, 2));
            dict.extend_from_slice(&[0, 0, 0]);
        }
        story.extend_from_slice(&dict);
        story
    }

    #[test]
    fn reads_the_words_a_hand_built_dictionary_holds() {
        let story = story_with(&["take", "lamp", "east"]);
        let vocabulary = Vocabulary::from_story(&story).expect("the fixture is valid");

        assert_eq!(vocabulary.len(), 3);
        assert_eq!(vocabulary.version(), 3);
        assert_eq!(vocabulary.resolution(), 6);
        assert_eq!(
            vocabulary.words().collect::<Vec<_>>(),
            ["east", "lamp", "take"]
        );
        assert!(!vocabulary.words().any(|word| word == "follow"));
    }

    #[test]
    fn a_v3_dictionary_stores_only_the_words_it_can_hold() {
        // A V3 game stores six characters, so "inventory" is stored as "invent" and
        // the interpreter cuts typed words to match. The resolution travels with the
        // words so the agent can cut them the same way.
        let story = story_with(&["invent", "examin"]);
        let vocabulary = Vocabulary::from_story(&story).unwrap();

        assert_eq!(vocabulary.resolution(), 6);
        assert!(vocabulary.words().any(|word| word == "invent"));
        assert!(vocabulary.words().any(|word| word == "examin"));
        assert!(!vocabulary.words().any(|word| word == "inventory"));
    }

    #[test]
    fn a_v4_story_keeps_more_of_each_word() {
        let mut story = story_with(&["inventory"]);
        story[0] = 5;
        // Longer text: rebuild the dictionary with 6-byte entries.
        let mut rebuilt = vec![0u8; 0x40];
        rebuilt[0] = 5;
        rebuilt[0x08..0x0a].copy_from_slice(&(0x40u16).to_be_bytes());
        let entry_len = 6 + 3;
        let mut dict = vec![0u8, entry_len as u8];
        dict.extend_from_slice(&1u16.to_be_bytes());
        dict.extend_from_slice(&encode_word("inventory", 3));
        dict.extend_from_slice(&[0, 0, 0]);
        rebuilt.extend_from_slice(&dict);

        let vocabulary = Vocabulary::from_story(&rebuilt).unwrap();
        assert_eq!(vocabulary.resolution(), 9);
        assert!(vocabulary.words().any(|word| word == "inventory"));
    }

    #[test]
    fn a_story_with_no_readable_dictionary_is_an_error_not_a_crash() {
        assert!(Vocabulary::from_story(&[0u8; 8]).is_err(), "too short");
        assert!(Vocabulary::from_story(&[9u8; 128]).is_err(), "version 9");
        let mut empty = story_with(&[]);
        // An entry count of zero is not a dictionary.
        let at = 0x40 + 2;
        empty[at..at + 2].copy_from_slice(&0u16.to_be_bytes());
        assert!(Vocabulary::from_story(&empty).is_err());
    }

    #[test]
    fn an_empty_vocabulary_blocks_nothing() {
        // The degraded path: a game whose words cannot be read hands the agent
        // nothing, and the agent falls back to learning from the parser's refusals
        // rather than blocking guesses.
        let vocabulary = Vocabulary::default();
        assert_eq!(vocabulary.len(), 0);
        assert_eq!(vocabulary.resolution(), 0);
    }

    /// The real games in `Assets/games/`, when they are checked out. These are the files
    /// the live run used, so the assertions are the measured facts from that run.
    fn game(name: &str) -> Option<PathBuf> {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("Assets")
            .join("games")
            .join(name);
        path.is_file().then_some(path)
    }

    #[test]
    fn adventure_knows_the_words_it_uses_and_not_the_ones_it_refused() {
        let Some(path) = game("advent.z5") else {
            eprintln!("skipping: Assets/games/advent.z5 is not present");
            return;
        };
        let vocabulary = Vocabulary::read(&path).unwrap();

        assert_eq!(vocabulary.version(), 5);
        assert_eq!(vocabulary.resolution(), 9);
        assert!(vocabulary.len() > 700, "got {} words", vocabulary.len());

        let words: std::collections::HashSet<&str> = vocabulary.words().collect();
        // The words the live session proved the game accepts.
        for word in [
            "look",
            "east",
            "take",
            "lamp",
            "inventory",
            "quit",
            "stream",
        ] {
            assert!(words.contains(word), "advent should know {word}");
        }
        // The two it refused, which cost the session two turns.
        for word in ["follow", "downstream"] {
            assert!(!words.contains(word), "advent should not know {word}");
        }
    }

    #[test]
    fn the_witness_proves_the_vocabulary_is_per_game() {
        let Some(path) = game("witness.z5") else {
            eprintln!("skipping: Assets/games/witness.z5 is not present");
            return;
        };
        let vocabulary = Vocabulary::read(&path).unwrap();

        assert_eq!(vocabulary.version(), 3);
        assert_eq!(vocabulary.resolution(), 6);
        let words: std::collections::HashSet<&str> = vocabulary.words().collect();
        // `follow` is refused by Adventure but known here, which is exactly why a
        // fixed word list cannot be trusted.
        assert!(words.contains("follow"));
        // And a V3 game stores only six characters: the word is "invent", and the
        // agent's resolution-aware match turns "inventory" into it.
        assert!(words.contains("invent"));
        assert!(words.contains("examin"));
    }

    #[test]
    fn the_witness_dictionary_expands_its_abbreviations() {
        let Some(path) = game("witness.z5") else {
            eprintln!("skipping: Assets/games/witness.z5 is not present");
            return;
        };
        let vocabulary = Vocabulary::read(&path).unwrap();

        // Five of this game's entries use abbreviations. Unexpanded they come out as
        // fragments, so no word should be left carrying a placeholder or a newline.
        let debris: Vec<&str> = vocabulary
            .words()
            .filter(|word| word.contains('<') || word.contains('>') || word.contains('\n'))
            .collect();
        assert!(
            debris.is_empty(),
            "abbreviations decoded as debris: {debris:?}"
        );

        // The expansion is only real if it produced words, not if it deleted them.
        assert!(
            vocabulary.len() >= 700,
            "abbreviation entries were dropped rather than expanded: {} words",
            vocabulary.len()
        );
    }
    #[test]
    fn no_word_is_longer_than_the_dictionary_can_hold() {
        // The invariant that proves the decoder is reading the right bytes: a V3
        // dictionary holds six z-characters, so no word in it can be longer than six
        // lowercase letters, and a V5 dictionary holds nine. A longer word would mean
        // the reader had walked off the end of an entry.
        for (name, limit) in [("witness.z5", 6usize), ("advent.z5", 9)] {
            let Some(path) = game(name) else { continue };
            let vocabulary = Vocabulary::read(&path).unwrap();
            let too_long: Vec<&str> = vocabulary
                .words()
                .filter(|word| word.chars().count() > limit)
                .collect();
            assert!(too_long.is_empty(), "{name} produced {too_long:?}");
            assert!(
                vocabulary.len() > 600,
                "{name}: only {} words",
                vocabulary.len()
            );
        }
    }
}
