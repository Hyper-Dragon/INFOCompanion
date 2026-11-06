//! The instance's own numbers (09_stats.md).
//!
//! Configuration and directory listings, and nothing else: the player and the persona are the
//! two variables this process was launched with, the library is a `read_dir`, and the game in
//! play is the one word the session keeps in its state directory. There is no session contact
//! here — no login, no seat, no socket — so nothing in this file can change what the session is
//! doing, and nothing it reports can go stale.
//!
//! One rule is deliberately a second copy: the story-extension list belongs to the session's own
//! `games::discover`, minus the `.dat` header proof — that would mean reading the story files,
//! and this module never opens one. When the session's list changes, this one changes with it,
//! and the instance notes state the price.

use std::path::Path;

/// Story-file extensions the session accepts, lower-case and without the dot (09_stats.md §3 (`ST8`–`ST9`)).
const STORY_EXTENSIONS: [&str; 8] = ["z3", "z4", "z5", "z6", "z7", "z8", "zblorb", "dat"];

/// The pointer the session writes at every start and every switch.
const CURRENT_GAME_FILE: &str = "current-game";

/// The instance's numbers, as one JSON line.
///
/// The two paths and the two names are everything this can be given, and the folders are read
/// afresh on every call. A games directory that cannot be read is no games and a warning in the
/// log — the daemon's own tolerance (`games::discover`), because a missing directory is not an
/// error, it is an empty one (09_stats.md §6 (`ST16`)).
pub fn document(games_dir: &Path, state_dir: &Path, player: &str, persona: &str) -> String {
    let games = match games_in(games_dir) {
        Ok(games) => games,
        Err(err) => {
            eprintln!(
                "webui: WARNING games directory {} not readable — {err}",
                games_dir.display()
            );
            Vec::new()
        }
    };

    let mut text = serde_json::json!({
        "schema": 1,
        "player": player,
        "persona": persona,
        "game": current_game(state_dir),
        "games_available": games.len(),
        "games": games,
    })
    .to_string();
    text.push('\n');
    text
}

/// The story stems in a games directory, sorted the way the session sorts them.
///
/// Case-insensitive by extension, so `wishbringer.DAT` is a game; a `.md` sidecar, a dotfile, a
/// stray note and a subdirectory are not, whatever they are called.
pub fn games_in(dir: &Path) -> std::io::Result<Vec<String>> {
    let mut stems = Vec::new();
    for entry in std::fs::read_dir(dir)? {
        let Ok(entry) = entry else { continue };
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let story = path
            .extension()
            .and_then(|extension| extension.to_str())
            .map(|extension| STORY_EXTENSIONS.contains(&extension.to_ascii_lowercase().as_str()))
            .unwrap_or(false);
        if story {
            if let Some(stem) = path.file_stem().and_then(|stem| stem.to_str()) {
                stems.push(stem.to_string());
            }
        }
    }
    stems.sort_by_key(|stem| stem.to_ascii_lowercase());
    Ok(stems)
}

/// The game in play: the state directory's pointer, as found.
///
/// Missing, unreadable or blank is `None`. Anything found is trimmed the way the session reads
/// it — the file is written with a trailing newline — but it is otherwise reported as it is: if
/// the story has since been deleted from the library, the pointer still says what it says
/// (09_stats.md §4 (`ST10`–`ST12`)).
pub fn current_game(state_dir: &Path) -> Option<String> {
    let text = std::fs::read_to_string(state_dir.join(CURRENT_GAME_FILE)).ok()?;
    let trimmed = text.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    /// A fresh directory under the system temp, unique per test — the tests run in parallel and
    /// the fixtures must not become each other's neighbours.
    fn scratch(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "infocompanion-webui-stats-{name}-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).expect("a scratch directory");
        dir
    }

    #[test]
    fn a_library_is_story_files_by_extension_and_nothing_else() {
        let dir = scratch("library");
        for name in [
            "advent.z5",
            "wishbringer.DAT", // the extension list is case-insensitive
            "moonmist.zblorb",
            "wishbringer.md", // a sidecar
            "notes.txt",
            "README",
            ".hidden",
        ] {
            fs::write(dir.join(name), b"x").expect("a fixture file");
        }
        fs::create_dir(dir.join("folder.z5")).expect("a fixture directory"); // not a game

        assert_eq!(
            games_in(&dir).expect("the library reads"),
            vec!["advent", "moonmist", "wishbringer"]
        );
    }

    #[test]
    fn sorting_is_case_insensitive_like_the_session() {
        let dir = scratch("sorting");
        for name in ["Zork.z5", "advent.z5"] {
            fs::write(dir.join(name), b"x").expect("a fixture file");
        }
        assert_eq!(games_in(&dir).expect("reads"), vec!["advent", "Zork"]);
    }

    #[test]
    fn a_directory_that_is_not_there_is_no_games() {
        let dir = std::env::temp_dir().join("infocompanion-webui-stats-nowhere");
        let _ = fs::remove_dir_all(&dir);
        assert!(games_in(&dir).is_err());
    }

    #[test]
    fn the_pointer_is_read_trimmed_and_may_be_absent() {
        let dir = scratch("pointer");
        assert_eq!(current_game(&dir), None);

        fs::write(dir.join(CURRENT_GAME_FILE), b"  wishbringer \n").expect("a pointer");
        assert_eq!(current_game(&dir).as_deref(), Some("wishbringer"));

        fs::write(dir.join(CURRENT_GAME_FILE), b"\n").expect("a blank pointer");
        assert_eq!(current_game(&dir), None);
    }

    #[test]
    fn the_document_is_the_spec_shape() {
        let games_dir = scratch("document-games");
        let state_dir = scratch("document-state");
        for name in ["advent.z5", "wishbringer.DAT", "wishbringer.md"] {
            fs::write(games_dir.join(name), b"x").expect("a fixture file");
        }
        fs::write(state_dir.join(CURRENT_GAME_FILE), b"advent\n").expect("a pointer");

        // Both names are fixtures, not the shipped defaults: this function is *handed* them, and
        // what is under test is that they reach the JSON unchanged. `Peter` and `james` are the
        // defaults `Settings::from_env` supplies; nothing here would notice if they changed.
        let text = document(&games_dir, &state_dir, "Tester", "james");
        assert!(text.ends_with('\n'), "one trailing newline");
        assert!(!text.trim_end().contains('\n'), "one line");

        let value: serde_json::Value = serde_json::from_str(&text).expect("valid JSON");
        let object = value.as_object().expect("an object");
        assert_eq!(
            object.len(),
            6,
            "the six keys of 09_stats.md §4 (`ST10`–`ST12`)"
        );
        assert_eq!(value["schema"], 1);
        assert_eq!(value["player"], "Tester");
        assert_eq!(value["persona"], "james");
        assert_eq!(value["game"], "advent");
        assert_eq!(value["games_available"], 2);
        assert_eq!(value["games"], serde_json::json!(["advent", "wishbringer"]));
    }

    #[test]
    fn an_empty_instance_is_still_a_document() {
        let games_dir = std::env::temp_dir().join("infocompanion-webui-stats-no-games");
        let state_dir = std::env::temp_dir().join("infocompanion-webui-stats-no-state");
        for dir in [&games_dir, &state_dir] {
            let _ = fs::remove_dir_all(dir);
        }

        let value: serde_json::Value =
            serde_json::from_str(&document(&games_dir, &state_dir, "Tester", "james"))
                .expect("valid JSON");
        assert_eq!(value["games_available"], 0);
        assert_eq!(value["games"], serde_json::json!([]));
        assert!(value["game"].is_null(), "no pointer, no game");
    }
}
