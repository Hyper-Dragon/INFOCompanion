//! The bridge's configuration file: `webui-config.json` (07_webui.md §7 (`W44`–`W47`)).
//!
//! The daemon's file, at the other end of the same pipe: what it holds is the few numbers this
//! program has of its own — the size a pane starts at, what a page may ask for, and how long a
//! line typed at a pane is given before the channel is closed. Everything else about the browser
//! surface is a *name* (a port, a path, a password), and names belong to the deployment, which
//! sets them in the environment.
//!
//! **The environment beats the file**, the same one rule as the daemon's (10_delivery.md §2 (`D5`)): a
//! container that sets `INFOCOMPANION_PANE_COLS` means it.
//!
//! It lives in the state directory, beside `core-config.json` and `agent-config.json`, for the
//! reason those do — it is the one directory that is writable and worth backing up (10_delivery.md §1 (`D1`–`D4`)).

use serde::Deserialize;
use std::path::{Path, PathBuf};
use std::time::Duration;

/// The file's name inside the state directory.
pub const FILE_NAME: &str = "webui-config.json";

/// The shipped defaults, one copy each: the flag-free equivalent of the daemon's `Args` defaults,
/// and the fallback any pane built without a tuning runs on.
pub const DEFAULT_PANE_COLS: u32 = 80;
pub const DEFAULT_PANE_COLS_MIN: u32 = 20;
pub const DEFAULT_PANE_COLS_MAX: u32 = 500;
pub const DEFAULT_PANE_ROWS: u32 = 24;
pub const DEFAULT_PANE_ROWS_MIN: u32 = 5;
pub const DEFAULT_PANE_ROWS_MAX: u32 = 200;
pub const DEFAULT_TYPE_LINE_SETTLE_MS: u64 = 400;

/// What kind of value a key takes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    /// A whole number.
    Number,
}

/// Every key this schema understands, and the shape of value it takes.
///
/// A mistake is named rather than merely refused: an operator reading `expected u32` with no key
/// in it has to go and find the line themselves.
const KNOWN_KEYS: &[(&str, Kind)] = &[
    ("pane_cols", Kind::Number),
    ("pane_cols_min", Kind::Number),
    ("pane_cols_max", Kind::Number),
    ("pane_rows", Kind::Number),
    ("pane_rows_min", Kind::Number),
    ("pane_rows_max", Kind::Number),
    ("type_line_settle_ms", Kind::Number),
];

/// The file, as written. Every field is optional, and units are in the names.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct ConfigFile {
    /// How many columns a pane is opened with when the page does not say.
    pub pane_cols: Option<u32>,
    /// The narrowest pane a page may ask for.
    pub pane_cols_min: Option<u32>,
    /// The widest pane a page may ask for.
    pub pane_cols_max: Option<u32>,
    /// How many rows a pane is opened with when the page does not say.
    pub pane_rows: Option<u32>,
    /// The shortest pane a page may ask for.
    pub pane_rows_min: Option<u32>,
    /// The tallest pane a page may ask for.
    pub pane_rows_max: Option<u32>,
    /// How long a line typed at a pane is given before the channel is closed.
    pub type_line_settle_ms: Option<u64>,
}

/// One dimension of a pane: what it starts at, and what a page may ask for.
///
/// The bounds are not decoration. `cols` arrives as a query parameter from a browser, and a pane
/// claiming ten thousand columns is a terminal emulator asked to do arithmetic on the way to
/// someone's CPU; the clamp is the difference between a setting and a way to make the daemon
/// suffer.
#[derive(Debug, Clone, Copy)]
pub struct Span {
    pub default: u32,
    pub least: u32,
    pub most: u32,
}

impl Span {
    /// The size to open at, given what the page asked for.
    pub fn resolve(&self, requested: Option<u32>) -> u32 {
        requested
            .unwrap_or(self.default)
            .clamp(self.least, self.most)
    }

    /// The same span with its bounds the right way round.
    ///
    /// `clamp` panics when the bounds are reversed, and a file is exactly where a reversed pair
    /// comes from. Reversing them back is the reading that keeps the intent — a minimum of 500 and
    /// a maximum of 20 says "wide", not "broken" — and the swap is reported either way.
    fn ordered(self, name: &str, warnings: &mut Vec<String>) -> Self {
        if self.least <= self.most {
            return self;
        }
        warnings.push(format!(
            "`{name}_min` ({}) is above `{name}_max` ({}); they have been read the other way round",
            self.least, self.most
        ));
        Self {
            default: self.default,
            least: self.most,
            most: self.least,
        }
    }
}

/// The bridge's own tuning, resolved.
#[derive(Debug, Clone)]
pub struct Tuning {
    pub cols: Span,
    pub rows: Span,
    /// How long a typed line is given for the daemon to take it (see `pane::type_a_line`).
    pub type_line_settle: Duration,
}

impl Tuning {
    /// The shipped tuning, which is what a bridge with no file and no environment runs on.
    pub fn shipped() -> Self {
        Self {
            cols: Span {
                default: DEFAULT_PANE_COLS,
                least: DEFAULT_PANE_COLS_MIN,
                most: DEFAULT_PANE_COLS_MAX,
            },
            rows: Span {
                default: DEFAULT_PANE_ROWS,
                least: DEFAULT_PANE_ROWS_MIN,
                most: DEFAULT_PANE_ROWS_MAX,
            },
            type_line_settle: Duration::from_millis(DEFAULT_TYPE_LINE_SETTLE_MS),
        }
    }
}

/// A configuration file that was found, and anything worth saying about it.
#[derive(Debug, Clone, Default)]
pub struct Loaded {
    /// The settings the file named.
    pub config: ConfigFile,
    /// Where it came from, so the log line can say so.
    pub source: Option<PathBuf>,
    /// Why it could not be used. The bridge runs on the defaults when this is set.
    pub problem: Option<String>,
    /// Keys the schema does not know, and values it could not use.
    pub warnings: Vec<String>,
}

/// Read the configuration file, if there is one.
///
/// A missing file is not a problem; an unreadable one is reported and then stood down from
/// entirely, because half a file leaves an operator believing settings that are not in force.
pub fn load(state_dir: &Path, explicit: Option<&Path>) -> Loaded {
    for candidate in candidates(state_dir, explicit) {
        if !candidate.is_file() {
            continue;
        }
        let text = match std::fs::read_to_string(&candidate) {
            Ok(text) => text,
            Err(err) => {
                return Loaded {
                    problem: Some(format!("{}: {err}", candidate.display())),
                    ..Loaded::default()
                }
            }
        };
        return match parse(&text) {
            Ok((config, warnings)) => Loaded {
                config,
                source: Some(candidate),
                problem: None,
                warnings,
            },
            Err(problem) => Loaded {
                problem: Some(format!("{}: {problem}", candidate.display())),
                ..Loaded::default()
            },
        };
    }

    Loaded::default()
}

/// The paths tried, in order.
fn candidates(state_dir: &Path, explicit: Option<&Path>) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Some(path) = explicit {
        paths.push(path.to_path_buf());
    }
    paths.push(state_dir.join(FILE_NAME));
    paths
}

/// Parse a file, naming anything it cannot use.
fn parse(text: &str) -> Result<(ConfigFile, Vec<String>), String> {
    let value: serde_json::Value = serde_json::from_str(text).map_err(|err| err.to_string())?;
    let object = match &value {
        serde_json::Value::Object(object) => object,
        other => return Err(format!("expected an object, found {}", kind_of(other))),
    };

    let mut warnings = Vec::new();
    for (key, value) in object {
        match KNOWN_KEYS.iter().find(|(known, _)| known == key) {
            None => warnings.push(format!(
                "`{key}` is not a setting this bridge understands; ignored"
            )),
            Some((_, Kind::Number)) if !value.is_number() => {
                return Err(format!("`{key}` takes a number, found {}", kind_of(value)))
            }
            Some(_) => {}
        }
    }
    warnings.sort();

    let config: ConfigFile = serde_json::from_value(value).map_err(|err| err.to_string())?;
    Ok((config, warnings))
}

fn kind_of(value: &serde_json::Value) -> &'static str {
    match value {
        serde_json::Value::Null => "null",
        serde_json::Value::Bool(_) => "a boolean",
        serde_json::Value::Number(_) => "a number",
        serde_json::Value::String(_) => "a string",
        serde_json::Value::Array(_) => "an array",
        serde_json::Value::Object(_) => "an object",
    }
}

/// What the deployment said, as opposed to what the file said.
///
/// Every field is `None` when the environment did not name that setting — which is not the same as
/// naming it with the shipped value, and is the whole reason this is a separate type: the rule is
/// that the environment beats the file, and a rule needs to know which of the two spoke.
#[derive(Debug, Clone, Default)]
pub struct Overrides {
    pub pane_cols: Option<u32>,
    pub pane_cols_min: Option<u32>,
    pub pane_cols_max: Option<u32>,
    pub pane_rows: Option<u32>,
    pub pane_rows_min: Option<u32>,
    pub pane_rows_max: Option<u32>,
    pub type_line_settle_ms: Option<u64>,
}

/// Fold the deployment's values and the file into the shipped defaults, reporting corrections.
///
/// Precedence, in one expression per setting: the environment, then the file, then the default
/// (10_delivery.md §2 (`D5`)).
///
/// Not `Overrides::default()` on a span, then again on the file: `Span` is built once, from both
/// sources, so a pair that is only half overridden by the environment still ends up the right way
/// round.
pub fn resolve(file: &ConfigFile, overrides: &Overrides) -> (Tuning, Vec<String>) {
    let shipped = Tuning::shipped();
    let mut warnings = Vec::new();

    let cols = Span {
        default: overrides
            .pane_cols
            .or(file.pane_cols)
            .unwrap_or(shipped.cols.default),
        least: overrides
            .pane_cols_min
            .or(file.pane_cols_min)
            .unwrap_or(shipped.cols.least),
        most: overrides
            .pane_cols_max
            .or(file.pane_cols_max)
            .unwrap_or(shipped.cols.most),
    }
    .ordered("pane_cols", &mut warnings);

    let rows = Span {
        default: overrides
            .pane_rows
            .or(file.pane_rows)
            .unwrap_or(shipped.rows.default),
        least: overrides
            .pane_rows_min
            .or(file.pane_rows_min)
            .unwrap_or(shipped.rows.least),
        most: overrides
            .pane_rows_max
            .or(file.pane_rows_max)
            .unwrap_or(shipped.rows.most),
    }
    .ordered("pane_rows", &mut warnings);

    let type_line_settle_ms = overrides
        .type_line_settle_ms
        .or(file.type_line_settle_ms)
        .unwrap_or(DEFAULT_TYPE_LINE_SETTLE_MS);
    if type_line_settle_ms == 0 {
        warnings.push(
            "`type_line_settle_ms` is zero; the channel may be closed before the daemon has read \
             the line, and the line will be lost"
                .to_string(),
        );
    }

    let tuning = Tuning {
        cols,
        rows,
        type_line_settle: Duration::from_millis(type_line_settle_ms),
    };
    (tuning, warnings)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_shipped_tuning_is_the_documented_default() {
        let tuning = Tuning::shipped();
        assert_eq!(tuning.cols.resolve(None), 80);
        assert_eq!(tuning.rows.resolve(None), 24);
        assert_eq!(tuning.type_line_settle, Duration::from_millis(400));
    }

    #[test]
    fn a_span_clamps_what_a_page_asks_for() {
        let tuning = Tuning::shipped();
        assert_eq!(tuning.cols.resolve(Some(10)), 20, "narrower than the floor");
        assert_eq!(
            tuning.cols.resolve(Some(9_000)),
            500,
            "wider than the ceiling"
        );
        assert_eq!(
            tuning.cols.resolve(Some(120)),
            120,
            "and otherwise as asked"
        );
    }

    #[test]
    fn a_reversed_pair_is_read_the_other_way_round_rather_than_panicking() {
        let (config, warnings) =
            parse(r#"{"pane_cols_min": 500, "pane_cols_max": 20}"#).expect("a readable file");
        assert!(warnings.is_empty(), "{warnings:?}");
        let (tuning, warnings) = resolve(&config, &Overrides::default());
        assert_eq!(tuning.cols.least, 20);
        assert_eq!(tuning.cols.most, 500);
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("pane_cols"), "{warnings:?}");
    }

    #[test]
    fn a_reversed_pair_is_caught_even_when_the_deployment_supplied_half_of_it() {
        // The file says `max: 120`; the environment says `min=500`. Ordering the halves
        // separately would leave 500 above 120 and panic in `clamp` at the first pane.
        let (config, _) = parse(r#"{"pane_cols_max": 120}"#).expect("a readable file");
        let overrides = Overrides {
            pane_cols_min: Some(500),
            ..Overrides::default()
        };
        let (tuning, warnings) = resolve(&config, &overrides);
        assert_eq!(tuning.cols.least, 120);
        assert_eq!(tuning.cols.most, 500);
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert_eq!(
            tuning.cols.resolve(Some(200)),
            200,
            "and it clamps without panicking"
        );
    }

    #[test]
    fn the_environment_beats_the_file() {
        let (config, _) = parse(r#"{"pane_rows": 40, "pane_cols": 100}"#).expect("a readable file");
        let overrides = Overrides {
            pane_rows: Some(50),
            ..Overrides::default()
        };
        let (tuning, _) = resolve(&config, &overrides);
        assert_eq!(
            tuning.rows.default, 50,
            "the deployment wins (10_delivery.md §2 (`D5`))"
        );
        assert_eq!(
            tuning.cols.default, 100,
            "and the file wins where it did not speak"
        );
    }

    #[test]
    fn a_zero_settle_is_allowed_but_said_out_loud() {
        let (config, _) = parse(r#"{"type_line_settle_ms": 0}"#).expect("a readable file");
        let (tuning, warnings) = resolve(&config, &Overrides::default());
        assert_eq!(tuning.type_line_settle, Duration::ZERO);
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("type_line_settle_ms"), "{warnings:?}");
    }

    #[test]
    fn a_file_names_only_what_it_names() {
        let (config, warnings) = parse(r#"{"pane_rows": 40}"#).expect("a readable file");
        assert!(warnings.is_empty(), "{warnings:?}");
        let (tuning, _) = resolve(&config, &Overrides::default());
        assert_eq!(tuning.rows.default, 40);
        assert_eq!(tuning.rows.least, DEFAULT_PANE_ROWS_MIN);
        assert_eq!(tuning.cols.default, DEFAULT_PANE_COLS, "untouched");
    }

    #[test]
    fn keys_the_schema_does_not_know_are_named_rather_than_ignored() {
        let (_, warnings) = parse(r#"{"pane_width": 80}"#).expect("a readable file");
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("pane_width"), "{warnings:?}");
    }

    #[test]
    fn a_value_of_the_wrong_type_names_the_key() {
        let err = parse(r#"{"pane_cols": "wide"}"#).expect_err("must refuse");
        assert!(err.contains("pane_cols"), "{err}");
        assert!(err.contains("a string"), "{err}");
    }

    #[test]
    fn a_directory_with_no_file_is_not_a_problem() {
        let loaded = load(Path::new("/nonexistent-state-dir-for-tests"), None);
        assert!(loaded.source.is_none());
        assert!(loaded.problem.is_none());
    }

    #[test]
    fn a_broken_file_is_a_problem_and_not_a_partial_read() {
        let loaded = load(Path::new("/nonexistent-state-dir-for-tests"), None);
        assert!(loaded.problem.is_none());
        let err = parse("{ not json").expect_err("must refuse");
        assert!(!err.is_empty());
    }

    #[test]
    fn every_documented_key_is_one_the_schema_has() {
        let documented = r#"{
            "pane_cols": 80,
            "pane_cols_min": 20,
            "pane_cols_max": 500,
            "pane_rows": 24,
            "pane_rows_min": 5,
            "pane_rows_max": 200,
            "type_line_settle_ms": 400
        }"#;
        let (config, warnings) = parse(documented).expect("the documented sample loads");
        assert!(warnings.is_empty(), "{warnings:?}");
        assert_eq!(config.pane_cols, Some(80));
        assert_eq!(config.type_line_settle_ms, Some(400));
        assert_eq!(KNOWN_KEYS.len(), 7);
    }
}
