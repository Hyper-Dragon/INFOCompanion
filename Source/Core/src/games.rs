//! Game discovery and selection (§5.3).
//!
//! Adding a story file to the mounted games directory makes it available: there is
//! no list flag and no manual registration. Supported formats are every Z-code
//! story file frotz understands (`.z3 .z4 .z5 .z6 .z7 .z8`) plus Blorb-wrapped
//! Z-code (`.zblorb`) and Infocom's own `.dat` releases (Wishbringer, Bureaucracy,
//! Border Zone). Because `.dat` is also a generic suffix for data files, a `.dat`
//! has to prove itself: it is listed only when its header reads as Z-code.
//!
//! The real title of a game lives in the game's own startup text, not in the
//! filename, so this module deliberately reports a *stem* plus the detected Z-code
//! version, and the core learns the title from the intro turn once the game runs.

use anyhow::{bail, Context, Result};
use std::path::{Path, PathBuf};
use tracing::{debug, warn};

/// Story file extensions we auto-discover (lower-case, without the dot).
pub const STORY_EXTENSIONS: [&str; 8] = ["z3", "z4", "z5", "z6", "z7", "z8", "zblorb", "dat"];

/// Extensions that only *might* be a story file, so the header has to agree.
///
/// `.dat` is what Infocom shipped its later MS-DOS releases as, and those are
/// ordinary Z-code stories — but `.dat` is also a generic suffix for a data file,
/// and offering something that is not a story as a game costs a failed boot at
/// the worst possible moment. So it is verified rather than trusted.
const AMBIGUOUS_EXTENSIONS: [&str; 1] = ["dat"];

/// File name of the "last played" pointer kept in the state directory.
pub const CURRENT_GAME_FILE: &str = "current-game";

/// A story file found on disk.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GameInfo {
    /// File name without extension; the game's identity for checkpoints and
    /// notebooks, and the name the player types at `/switch`.
    pub stem: String,
    /// Absolute path to the story file.
    pub path: PathBuf,
    /// Z-machine version, read from the story header (or the Blorb `ZCOD` chunk).
    pub version: Option<u8>,
    /// Size on disk, for the `/games` listing.
    pub size_bytes: u64,
}

impl GameInfo {
    /// `Z-code v5`, or `unknown format` when the header could not be read.
    pub fn version_label(&self) -> String {
        match self.version {
            Some(v) => format!("Z-code v{v}"),
            None => "unknown format".to_string(),
        }
    }
}

/// Is this file name a story file we support?
pub fn is_story_file(path: &Path) -> bool {
    STORY_EXTENSIONS.contains(&file_extension(path).as_str())
}

/// The file's extension, lower-cased and without the dot; empty when there is none.
fn file_extension(path: &Path) -> String {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_ascii_lowercase())
        .unwrap_or_default()
}

/// Does this extension need the header to confirm it really is a story file?
fn needs_proof(path: &Path) -> bool {
    AMBIGUOUS_EXTENSIONS.contains(&file_extension(path).as_str())
}

/// Does this version byte read like a Z-machine story?
fn plausible_version(version: Option<u8>) -> bool {
    matches!(version, Some(1..=8))
}

/// Scan a directory for story files, sorted by stem for a stable listing.
///
/// A missing directory is not an error: the daemon reports an empty game list and
/// keeps running, because a container with no games mounted should still be
/// reachable to explain itself rather than crash-looping.
pub fn discover(dir: &Path) -> Vec<GameInfo> {
    let entries = match std::fs::read_dir(dir) {
        Ok(entries) => entries,
        Err(err) => {
            warn!(dir = %dir.display(), error = %err, "games directory not readable");
            return Vec::new();
        }
    };

    let mut games: Vec<GameInfo> = entries
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.is_file() && is_story_file(path))
        .filter_map(|path| {
            let stem = path.file_stem()?.to_str()?.to_string();
            let version = zcode_version(&path);
            if needs_proof(&path) && !plausible_version(version) {
                warn!(
                    path = %path.display(),
                    version = ?version,
                    "ignoring a file whose extension only suggests a story file"
                );
                return None;
            }
            let size_bytes = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
            Some(GameInfo {
                stem,
                version,
                path,
                size_bytes,
            })
        })
        .collect();

    games.sort_by(|a, b| {
        a.stem
            .to_ascii_lowercase()
            .cmp(&b.stem.to_ascii_lowercase())
    });
    debug!(dir = %dir.display(), count = games.len(), "discovered story files");
    games
}

/// Read the Z-machine version byte from a story file.
///
/// For a bare story file that is byte 0 of the header. For a Blorb container the
/// story lives in the `ZCOD` chunk, whose first data byte is the version — the
/// Blorb format is a simple chunk list, so this stays cheap.
pub fn zcode_version(path: &Path) -> Option<u8> {
    story_bytes(path)?.first().copied()
}

/// The raw Z-code story inside a file: the bytes themselves, or the `ZCOD` chunk
/// when the file is a Blorb container.
///
/// Shared by the version peek and the dictionary reader so that Blorb is handled
/// in exactly one place.
pub fn story_bytes(path: &Path) -> Option<Vec<u8>> {
    let data = std::fs::read(path).ok()?;
    if data.len() >= 12 && &data[0..4] == b"IFRS" {
        return blorb_zcode(&data);
    }
    Some(data)
}

fn blorb_zcode(data: &[u8]) -> Option<Vec<u8>> {
    let (offset, len) = blorb_find(data, b"ZCOD")?;
    Some(data.get(offset..offset + len)?.to_vec())
}

/// Find a chunk's body offset and length inside a Blorb container.
fn blorb_find(data: &[u8], wanted: &[u8; 4]) -> Option<(usize, usize)> {
    // Header: "IFRS" + u32 total length, so the first chunk starts at offset 8.
    // Chunks are type + u32 length + data, each padded to an even length.
    let mut offset = 8usize;
    while offset + 8 <= data.len() {
        let chunk_type = data.get(offset..offset + 4)?;
        let chunk_len =
            u32::from_be_bytes(data.get(offset + 4..offset + 8)?.try_into().ok()?) as usize;
        if chunk_type == wanted {
            return Some((offset + 8, chunk_len));
        }
        offset = offset
            .checked_add(8)?
            .checked_add(chunk_len)?
            .checked_add(chunk_len & 1)?;
    }
    None
}

/// Resolve a player-supplied game name against the discovered list.
///
/// Accepts, in order of preference: an existing path, an exact stem, a unique
/// stem prefix, a unique stem substring. Anything ambiguous is an error that lists
/// the candidates — better a clear refusal than a coin flip on the player's game.
pub fn resolve(games: &[GameInfo], wanted: &str) -> Result<GameInfo> {
    let wanted = wanted.trim();
    if wanted.is_empty() {
        bail!("no game name given");
    }

    // An explicit path that exists wins outright.
    if (wanted.contains('/') || wanted.contains('\\')) && Path::new(wanted).is_file() {
        let path = Path::new(wanted);
        let stem = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or(wanted)
            .to_string();
        return Ok(GameInfo {
            version: zcode_version(path),
            size_bytes: std::fs::metadata(path).map(|m| m.len()).unwrap_or(0),
            stem,
            path: path.to_path_buf(),
        });
    }

    // Compare without directories or extension, case-insensitively.
    let needle = Path::new(wanted)
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or(wanted)
        .to_ascii_lowercase();

    let matches = |pred: &dyn Fn(&str) -> bool| -> Vec<&GameInfo> {
        games
            .iter()
            .filter(|g| pred(&g.stem.to_ascii_lowercase()))
            .collect()
    };

    let exact = matches(&|stem| stem == needle);
    if let Some(game) = exact.first() {
        return Ok((*game).clone());
    }

    for (label, found) in [
        ("starting with", matches(&|stem| stem.starts_with(&needle))),
        ("containing", matches(&|stem| stem.contains(&needle))),
    ] {
        match found.len() {
            0 => continue,
            1 => return Ok(found[0].clone()),
            _ => bail!(
                "`{}` is ambiguous — {} games {} it: {}",
                wanted,
                found.len(),
                label,
                found
                    .iter()
                    .map(|g| g.stem.as_str())
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
        }
    }

    if games.is_empty() {
        bail!("no story files found — mount some into the games directory and try again");
    }
    bail!(
        "no such game: `{}` (available: {})",
        wanted,
        games
            .iter()
            .map(|g| g.stem.as_str())
            .collect::<Vec<_>>()
            .join(", ")
    )
}

/// Read the "last played" pointer written on every game switch.
pub fn read_current(state_dir: &Path) -> Option<String> {
    let path = state_dir.join(CURRENT_GAME_FILE);
    let text = std::fs::read_to_string(path).ok()?;
    let trimmed = text.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

/// Write the "last played" pointer.
pub fn write_current(state_dir: &Path, stem: &str) -> Result<()> {
    std::fs::create_dir_all(state_dir)
        .with_context(|| format!("failed to create state dir {}", state_dir.display()))?;
    let path = state_dir.join(CURRENT_GAME_FILE);
    std::fs::write(&path, format!("{stem}\n"))
        .with_context(|| format!("failed to write {}", path.display()))?;
    debug!(stem, path = %path.display(), "recorded current game");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn game(stem: &str) -> GameInfo {
        GameInfo {
            stem: stem.to_string(),
            path: PathBuf::from(format!("/games/{stem}.z5")),
            version: Some(5),
            size_bytes: 10,
        }
    }

    #[test]
    fn recognises_supported_extensions_case_insensitively() {
        assert!(is_story_file(Path::new("/games/advent.z5")));
        assert!(is_story_file(Path::new("/games/ADVENT.Z8")));
        assert!(is_story_file(Path::new("/games/jigsaw.zblorb")));
        assert!(!is_story_file(Path::new("/games/notes.txt")));
        assert!(!is_story_file(Path::new("/games/advent")));
    }

    #[test]
    fn reads_the_version_byte_from_a_bare_story_file() {
        let dir = std::env::temp_dir().join("infocompanion-games-test");
        fs::create_dir_all(&dir).unwrap();
        let story = dir.join("v5.z5");
        let mut bytes = vec![0u8; 64];
        bytes[0] = 5;
        fs::write(&story, &bytes).unwrap();
        assert_eq!(zcode_version(&story), Some(5));
        let _ = fs::remove_file(&story);
    }

    #[test]
    fn reads_the_version_from_a_blorb_zcod_chunk() {
        let dir = std::env::temp_dir().join("infocompanion-games-test");
        fs::create_dir_all(&dir).unwrap();
        let blorb = dir.join("packed.zblorb");
        let mut data = Vec::new();
        data.extend_from_slice(b"IFRS");
        data.extend_from_slice(&0u32.to_be_bytes());
        // A PNG-ish chunk first, to prove we walk the list.
        data.extend_from_slice(b"PNG ");
        data.extend_from_slice(&3u32.to_be_bytes());
        data.extend_from_slice(b"abc");
        data.push(0); // pad to even
        data.extend_from_slice(b"ZCOD");
        data.extend_from_slice(&4u32.to_be_bytes());
        data.extend_from_slice(&[8u8, 0, 0, 0]);
        fs::write(&blorb, &data).unwrap();
        assert_eq!(zcode_version(&blorb), Some(8));
        let _ = fs::remove_file(&blorb);
    }

    #[test]
    fn discovers_only_story_files_sorted_by_stem() {
        let dir = std::env::temp_dir().join("infocompanion-discover-test");
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("witness.z5"), [3u8, 0, 0, 0]).unwrap();
        fs::write(dir.join("advent.z5"), [5u8, 0, 0, 0]).unwrap();
        fs::write(dir.join("readme.txt"), b"hello").unwrap();

        let games = discover(&dir);
        let stems: Vec<&str> = games.iter().map(|g| g.stem.as_str()).collect();
        assert_eq!(stems, vec!["advent", "witness"]);
        assert_eq!(games[0].version, Some(5));
        assert_eq!(games[1].version, Some(3));

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn recognises_dat_as_a_story_extension() {
        assert!(is_story_file(Path::new("/games/wishbringer.DAT")));
        assert!(is_story_file(Path::new("/games/moonmist.dat")));
        assert!(!is_story_file(Path::new("/games/state.sav")));
    }

    #[test]
    fn discovers_dat_stories_but_ignores_dat_files_that_are_not_stories() {
        let dir = std::env::temp_dir().join("infocompanion-discover-dat-test");
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        // A real one: the first bytes of wishbringer.DAT (a V3 story).
        fs::write(dir.join("wishbringer.DAT"), [3u8, 2, 0, 0x44]).unwrap();
        // A `.dat` that is a data file: it must not be offered as a game.
        fs::write(dir.join("graphics.dat"), b"not a story at all").unwrap();
        // A story extension is trusted, as before.
        fs::write(dir.join("advent.z5"), [5u8, 0, 0, 0]).unwrap();

        let games = discover(&dir);
        let stems: Vec<&str> = games.iter().map(|g| g.stem.as_str()).collect();
        assert_eq!(stems, vec!["advent", "wishbringer"]);
        assert_eq!(games[1].version, Some(3));
        assert_eq!(games[1].version_label(), "Z-code v3");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn discover_tolerates_a_missing_directory() {
        assert!(discover(Path::new("/definitely/not/here")).is_empty());
    }

    #[test]
    fn resolves_exact_prefix_and_reports_ambiguity() {
        let games = vec![game("advent"), game("adventureland"), game("witness")];

        assert_eq!(resolve(&games, "advent").unwrap().stem, "advent");
        assert_eq!(resolve(&games, "ADVENT").unwrap().stem, "advent");
        assert_eq!(resolve(&games, "  witness ").unwrap().stem, "witness");
        assert_eq!(resolve(&games, "witness.z5").unwrap().stem, "witness");

        // "adventu" is a unique prefix, so it resolves…
        assert_eq!(resolve(&games, "adventu").unwrap().stem, "adventureland");

        // …but "advent" alone is exact, and an ambiguous substring must refuse.
        let ambiguous = resolve(&games, "e").unwrap_err().to_string();
        assert!(ambiguous.contains("ambiguous"), "{ambiguous}");
    }

    #[test]
    fn unknown_game_lists_the_available_ones() {
        let games = vec![game("advent")];
        let err = resolve(&games, "zork").unwrap_err().to_string();
        assert!(err.contains("no such game"), "{err}");
        assert!(err.contains("advent"), "{err}");
    }

    #[test]
    fn resolving_with_no_games_says_so() {
        let err = resolve(&[], "advent").unwrap_err().to_string();
        assert!(err.contains("no story files found"), "{err}");
    }

    #[test]
    fn current_game_pointer_round_trips() {
        let dir = std::env::temp_dir().join("infocompanion-current-game-test");
        let _ = fs::remove_dir_all(&dir);
        assert_eq!(read_current(&dir), None);
        write_current(&dir, "advent").unwrap();
        assert_eq!(read_current(&dir), Some("advent".to_string()));
        let _ = fs::remove_dir_all(&dir);
    }
}
