//! The shape of the state directory (02_game_boundary.md §9 (`G51`)).
//!
//! The record, the run books and the notebooks all live under one directory, and the shapes
//! written there change between builds with **no backward compatibility**: a notebook whose
//! sections came to mean something different, or a record row with no place on it, produces a
//! session that starts and is quietly wrong about the game it is playing.
//!
//! So an old directory is **refused**, not read. That is the whole of this module, and it runs
//! before anything else touches the directory, because the point of a refusal is that nothing
//! has been read yet.
//!
//! The rule, in one place:
//!
//! - the marker names the shape this directory is written in;
//! - a marker naming another shape is refused, whichever way round it is;
//! - no marker, on a directory somebody has played in, is refused: it predates the marker;
//! - no marker, on a directory with none of the state's own furniture in it, is **claimed**.
//!
//! Nothing is migrated and nothing is repaired. The message names the fix, which is an empty
//! directory and a new game.

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

use crate::seat_protocol::now_rfc3339;

/// The shape this build writes and reads.
///
/// Raise it whenever a shape this program *writes* changes, and every directory written by an
/// earlier build is refused from then on. A module that adds a file nothing else reads does not
/// need to raise it; one that changes what an existing file means does.
pub const SHAPE: u64 = 1;

/// The marker's own name, inside the state directory.
pub const MARKER: &str = "state-shape.json";

/// The things whose presence means somebody has played here.
///
/// Deliberately not *every* file: the three `*-config.json` are seeded into the directory before
/// the daemon has ever run (`O5`), so a directory holding nothing but those is a directory nobody
/// has played in and is claimed rather than refused. Anything else lying about — a `lost+found`
/// from a bind mount, an editor's droppings — is none of this module's business.
const FURNITURE: &[&str] = &[
    "saves",
    "notebook",
    "observations",
    "runs",
    "logs",
    "playback",
    "meta",
    "catchup",
    "vocabulary",
    "current-game",
];

/// What the marker holds.
#[derive(Debug, Serialize, Deserialize)]
struct Marker {
    /// The shape this directory is written in.
    shape: u64,
    /// RFC 3339, when it was claimed. Diagnostic only, and absent on a hand-written marker.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    written: Option<String>,
}

/// Admit this build to a state directory, or refuse it.
///
/// Called once, at startup, before the directory is read for anything — not the configuration,
/// not the record, not the pointer. A refusal here is the difference between a session that
/// will not start and a session that starts and lies.
pub fn admit(root: &Path) -> Result<()> {
    let file = root.join(MARKER);

    let text = match std::fs::read_to_string(&file) {
        Ok(text) => text,
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => return claim(root, &file),
        Err(err) => return Err(err).with_context(|| format!("failed to read {}", file.display())),
    };

    // A marker that will not parse is a refusal and not a fresh start: somebody wrote something
    // here, and guessing what they meant is how a directory gets half-read.
    let marker: Marker = serde_json::from_str(&text).with_context(|| {
        format!(
            "{} is not a shape marker this build can read",
            file.display()
        )
    })?;

    if marker.shape == SHAPE {
        return Ok(());
    }

    anyhow::bail!(
        "the state directory at {} is written in shape {}, and this build reads shape {}. \
         Nothing has been migrated: start from an empty state directory and a new game.",
        root.display(),
        marker.shape,
        SHAPE
    )
}

/// A directory with no marker: claim it if nobody has played in it, refuse it if somebody has.
fn claim(root: &Path, file: &Path) -> Result<()> {
    if let Some(found) = furniture_in(root) {
        anyhow::bail!(
            "the state directory at {} was written before this build recorded its shape — it \
             already holds {}, and what is in it cannot be read. Nothing has been migrated: \
             start from an empty state directory and a new game.",
            root.display(),
            found
        );
    }

    std::fs::create_dir_all(root)
        .with_context(|| format!("failed to create {}", root.display()))?;

    let marker = Marker {
        shape: SHAPE,
        written: Some(now_rfc3339()),
    };
    let text =
        serde_json::to_string_pretty(&marker).context("failed to encode the shape marker")?;
    std::fs::write(&file, text + "\n")
        .with_context(|| format!("failed to write {}", file.display()))?;
    Ok(())
}

/// The first of the state's own furniture this directory holds, if any.
fn furniture_in(root: &Path) -> Option<&'static str> {
    FURNITURE
        .iter()
        .copied()
        .find(|name| root.join(name).exists())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn temp() -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "ic-shape-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        dir
    }

    fn write_marker(dir: &Path, shape: u64) {
        std::fs::create_dir_all(dir).unwrap();
        std::fs::write(dir.join(MARKER), format!("{{\"shape\": {shape}}}")).unwrap();
    }

    #[test]
    fn a_directory_that_does_not_exist_yet_is_claimed() {
        let dir = temp();
        admit(&dir).unwrap();

        let text = std::fs::read_to_string(dir.join(MARKER)).unwrap();
        assert!(text.contains(&format!("\"shape\": {SHAPE}")), "{text}");
        assert!(text.contains("\"written\""), "{text}");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn an_empty_directory_is_claimed_and_claiming_it_again_is_fine() {
        let dir = temp();
        std::fs::create_dir_all(&dir).unwrap();

        admit(&dir).unwrap();
        admit(&dir).unwrap();

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn the_seeded_configuration_is_not_furniture() {
        // `seed-assets.sh` writes the three config files into a directory nobody has played in
        // (`O5`), so a state directory holding only those must still be claimed.
        let dir = temp();
        std::fs::create_dir_all(&dir).unwrap();
        for name in ["agent-config.json", "core-config.json", "webui-config.json"] {
            std::fs::write(dir.join(name), "{}").unwrap();
        }

        admit(&dir).unwrap();

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn something_else_lying_about_is_not_furniture_either() {
        let dir = temp();
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("lost+found"), "").unwrap();
        std::fs::write(dir.join(".DS_Store"), "").unwrap();

        admit(&dir).unwrap();

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn a_directory_with_no_marker_that_has_been_played_in_is_refused() {
        let dir = temp();
        std::fs::create_dir_all(dir.join("notebook")).unwrap();

        let refusal = admit(&dir).unwrap_err().to_string();

        assert!(refusal.contains("notebook"), "{refusal}");
        assert!(refusal.contains("Nothing has been migrated"), "{refusal}");
        assert!(refusal.contains("empty state directory"), "{refusal}");
        // Refused means untouched: no marker was written over the top of it.
        assert!(!dir.join(MARKER).exists());

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn a_shape_from_another_build_is_refused_whichever_way_round_it_is() {
        let older = temp();
        write_marker(&older, SHAPE - 1);
        let refusal = admit(&older).unwrap_err().to_string();
        assert!(
            refusal.contains(&format!("shape {}", SHAPE - 1)),
            "{refusal}"
        );
        assert!(
            refusal.contains(&format!("reads shape {SHAPE}")),
            "{refusal}"
        );
        assert!(refusal.contains("Nothing has been migrated"), "{refusal}");

        let newer = temp();
        write_marker(&newer, SHAPE + 1);
        assert!(admit(&newer).is_err());

        std::fs::remove_dir_all(&older).ok();
        std::fs::remove_dir_all(&newer).ok();
    }

    #[test]
    fn a_marker_that_will_not_parse_is_refused_rather_than_read_as_fresh() {
        let dir = temp();
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join(MARKER), "not json").unwrap();

        let refusal = admit(&dir).unwrap_err().to_string();
        assert!(refusal.contains("not a shape marker"), "{refusal}");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn the_current_shape_is_admitted() {
        let dir = temp();
        write_marker(&dir, SHAPE);
        admit(&dir).unwrap();
        std::fs::remove_dir_all(&dir).ok();
    }
}
