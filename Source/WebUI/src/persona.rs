//! The persona file, read for the one thing the page needs out of it.
//!
//! **A personality is a file** (`04_companion.md` §11 (`C61`)): the front matter at the top carries
//! the name, and it carries the **voice** as well, because a voice belongs to a character rather
//! than to a deployment. Swap the persona and the voice comes with it — which is the point of it
//! being a file, and the reason a voice named in the environment is wrong: it is a second thing to
//! remember when the character changes, and forgetting it is how a man called James ends up
//! speaking as a woman.
//!
//! The daemon resolves the file and owns the `name` in it. This reads the same file for the key the
//! daemon does not use, so the two cannot disagree about anything. A file that cannot be read, or a
//! front matter with no `voice` in it, is **no voice** rather than an error: a deployment that has
//! not said how the companion sounds gets a silent companion and no switch to press, which is the
//! rule the page already follows (`V15` of `08_webui_tts.md`).

use std::path::{Path, PathBuf};

/// Where the personality is, resolved the way the daemon resolves it: an explicit path first, then
/// the slug beside the prompts directory.
///
/// Deliberately the same order as the daemon's resolver (`Source/Core/src/main.rs`), because both
/// ends reading the same file is the whole design — and a bridge that read a different file would
/// publish the voice of a character nobody is playing.
pub fn path(prompts_dir: &Path, slug: &str) -> PathBuf {
    let explicit = std::env::var("INFOCOMPANION_PROMPT_PATH")
        .ok()
        .map(|path| path.trim().to_string())
        .filter(|path| !path.is_empty());
    match explicit {
        Some(path) => PathBuf::from(path),
        None => prompts_dir
            .join("persona")
            .join(format!("{}.txt", slug.trim())),
    }
}

/// The voice a persona file names, if it names one.
pub fn declared_voice(path: &Path) -> Option<String> {
    let text = std::fs::read_to_string(path).ok()?;
    voice_in(&text)
}

/// The `voice:` key of the front matter, which is the only place a voice is named.
///
/// The block is the lines between the first two fences, and the value is everything after the first
/// colon on its line. It is read as a name rather than parsed as a document: a persona's front
/// matter is two or three lines of names, and the name is the whole of what it says.
fn voice_in(text: &str) -> Option<String> {
    let mut inside = false;
    for line in text.lines() {
        if line.trim() == "---" {
            // The second fence is the end of the block: a `voice:` after it is prose about a voice.
            if inside {
                return None;
            }
            inside = true;
            continue;
        }
        if !inside {
            // The front matter is at the top: the first real line without a fence before it means
            // there is none, and the rest of the file is not a configuration file.
            if !line.trim().is_empty() {
                return None;
            }
            continue;
        }
        let Some((key, value)) = line.split_once(':') else {
            continue;
        };
        if key.trim().eq_ignore_ascii_case("voice") {
            let named = value.trim().trim_matches(['"', '\'']).trim();
            if !named.is_empty() {
                return Some(named.to_string());
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_voice_is_read_out_of_the_front_matter() {
        let file = "---\nname: James\nvoice: bm_fable\n---\n\n# Who you are\n\nYou are James.\n";
        assert_eq!(voice_in(file).as_deref(), Some("bm_fable"));
    }

    /// The shipped persona as it stood before it named a voice: a name and nothing else is a
    /// persona without a voice, not a broken file (`V15`).
    #[test]
    fn a_persona_that_names_no_voice_has_none() {
        assert_eq!(voice_in("---\nname: James\n---\n\nYou are James.\n"), None);
    }

    #[test]
    fn prose_without_front_matter_is_not_configuration() {
        assert_eq!(voice_in("You are James.\nvoice: bm_fable\n"), None);
        assert_eq!(voice_in(""), None);
        assert_eq!(voice_in("\n\n"), None);
    }

    /// Everything after the block is prose, and prose is allowed to mention a voice.
    #[test]
    fn a_voice_named_after_the_block_is_not_the_persona_s_voice() {
        let file = "---\nname: James\n---\n\nSpeak with a voice: bm_fable is one of them.\n";
        assert_eq!(voice_in(file), None);
    }

    #[test]
    fn the_value_is_a_name_and_not_a_quotation() {
        assert_eq!(
            voice_in("---\nvoice: \"bm_fable\"\n---\n").as_deref(),
            Some("bm_fable")
        );
        assert_eq!(
            voice_in("---\nVoice:  bm_fable \n---\n").as_deref(),
            Some("bm_fable")
        );
        // A key nobody named is not a voice, and a colon-less line is not a key.
        assert_eq!(voice_in("---\nname: James\n---\n"), None);
        assert_eq!(voice_in("---\nvoice:\n---\n"), None);
    }

    /// The slug is the persona's file, and the explicit path wins — the daemon's own order, because
    /// both ends have to be looking at the same character.
    #[test]
    fn the_file_asked_for_is_the_one_the_daemon_reads() {
        let dir = Path::new("/prompts");
        assert_eq!(
            path(dir, "james"),
            PathBuf::from("/prompts/persona/james.txt")
        );
        assert_eq!(
            path(dir, " james "),
            PathBuf::from("/prompts/persona/james.txt")
        );
    }
}
