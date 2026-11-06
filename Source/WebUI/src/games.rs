//! The games page: what has been played, and what has been written up about it (07_webui.md §6.1 (`W31`–`W35`)).
//!
//! **A reader, like `/stats.json` and `/evening.txt`.** It reads `<state>` and writes nothing into
//! it: the list is what the state directory holds — a game with played data, which is a different
//! question from a game that exists in the library — and the reports are files the companion wrote.
//! Nothing here talks to the session except the one button that has no other way to ask for
//! anything, and that one asks the way a person would: by opening a pane and typing the command.
//!
//! The line the page draws is the same one the rest of this program draws. It knows what a *file* is
//! called and it does not know what a game is, what a turn is, or what a report means: given a
//! directory it lists what is in it, and given a stem it serves the page that is there.

use std::collections::BTreeMap;
use std::fs;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::Value;

/// One game, as the list shows it.
pub struct Row {
    pub stem: String,
    /// When the turn record was last written to, which is as close to "last played" as a
    /// directory of files can honestly say.
    pub played: SystemTime,
    /// When a report was written, if one ever was.
    pub written_up: Option<SystemTime>,
    /// When a write-up was last *asked for* — the moment the pane accepted the line (B9).
    pub requested: Option<SystemTime>,
    /// What the harness itself said when a write-up ended without a report, and when.
    pub note: Option<(SystemTime, String)>,
}

/// How long a write-up may take before the page stops saying it is still coming (B9).
///
/// Generous on purpose: the pass reads everything the harness holds and makes two model calls, so a
/// minute or two is normal. Five is where "still working" stops being a useful thing to tell
/// somebody, and the page switches to saying that it looks like it did not happen.
pub const WRITE_UP_PATIENCE: std::time::Duration = std::time::Duration::from_secs(5 * 60);

/// Every game with played data, newest first.
///
/// Played data is the turn record or a notebook: the two things that only exist because somebody
/// played. A story file with neither is a *library* entry, which is `/games` on the daemon and a
/// different question — the requester's own distinction, and the reason this page exists at all.
pub fn listing(state: &Path) -> Vec<Row> {
    let mut found: BTreeMap<String, Row> = BTreeMap::new();

    for (directory, named) in [("observations", ".jsonl"), ("playback", ".log")] {
        let Ok(entries) = fs::read_dir(state.join(directory)) else {
            continue;
        };

        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            let Some(stem) = name.strip_suffix(named) else {
                continue;
            };
            let written = entry
                .metadata()
                .and_then(|meta| meta.modified())
                .unwrap_or(UNIX_EPOCH);
            found
                .entry(stem.to_string())
                .and_modify(|row| row.played = row.played.max(written))
                .or_insert_with(|| Row {
                    stem: stem.to_string(),
                    played: written,
                    written_up: None,
                    requested: None,
                    note: None,
                });
        }
    }

    for row in found.values_mut() {
        row.written_up = written_up_at(state, &row.stem);
        row.requested = requested_at(state, &row.stem);
        row.note = harness_note(state, &row.stem);
    }

    let mut rows: Vec<Row> = found.into_values().collect();
    rows.sort_by(|left, right| right.played.cmp(&left.played));
    rows
}

/// When a game's report was written, read from the facts file beside it.
///
/// The date is in the file rather than taken from its mtime, because the mtime moves when anything
/// touches the file and the date is the thing the report itself claims.
fn written_up_at(state: &Path, stem: &str) -> Option<SystemTime> {
    let text = fs::read_to_string(state.join("catchup").join(format!("{stem}.json"))).ok()?;
    let facts: Value = serde_json::from_str(&text).ok()?;
    let when = facts.get("generated_at")?.as_str()?;
    humantime_to_system_time(when)
}

/// When a write-up was last asked for, from the marker the generate button leaves (B9).
///
/// A plain stamp in a file, written by the route that types the command into a pane — so "asked and
/// nothing came of it yet" is a fact on disk rather than something a page has to remember. Its
/// mtime is what is read: the file's contents are for a person reading the state directory, and a
/// stat is one syscall rather than a parse.
fn requested_at(state: &Path, stem: &str) -> Option<SystemTime> {
    fs::metadata(state.join("catchup").join(format!("{stem}.requested")))
        .and_then(|meta| meta.modified())
        .ok()
}

/// The newest note the harness wrote about itself for this game, with its timestamp (B9).
///
/// The write-up pass already does the right thing when it gives up: it appends a note to
/// `<state>/meta/<stem>.md` under a `## harness — <timestamp>` heading, because *"a generate button
/// that appears to do nothing is the one thing this feature cannot afford"*. Nothing read it — the
/// file is written for a person with a shell — so a failed write-up was invisible from the browser
/// and the row kept yesterday's date for ever. This is that note, found by its heading, so the
/// failure is on the page where the person who pressed the button can see it.
///
/// The block is the *last* one in the file when it is the newest, which is how `MetaNotes` appends;
/// the scan takes the last heading rather than the first for that reason.
fn harness_note(state: &Path, stem: &str) -> Option<(SystemTime, String)> {
    let text = fs::read_to_string(state.join("meta").join(format!("{stem}.md"))).ok()?;
    let lines: Vec<&str> = text.lines().collect();

    // The last one in the file, because `MetaNotes` appends: the newest note is at the bottom.
    let start = lines
        .iter()
        .rposition(|line| line.starts_with("## harness — "))?;
    let stamp = humantime_to_system_time(lines[start]["## harness — ".len()..].trim())?;

    let body = lines[start + 1..]
        .iter()
        .take_while(|line| !line.starts_with("## "))
        .copied()
        .collect::<Vec<&str>>()
        .join("\n");
    let body = body.trim().to_string();

    (!body.is_empty()).then_some((stamp, body))
}

/// `2026-09-19T01:22:31.1234567+00:00` into a `SystemTime`, or nothing when it will not parse.
///
/// Hand-parsed rather than pulled in: one date, one shape, and a page that shows no date at all is
/// a better failure than a page that will not load.
fn humantime_to_system_time(text: &str) -> Option<SystemTime> {
    let (date, rest) = text.split_once('T')?;
    let mut parts = date.split('-');
    let year: i64 = parts.next()?.parse().ok()?;
    let month: i64 = parts.next()?.parse().ok()?;
    let day: i64 = parts.next()?.parse().ok()?;

    let mut clock = rest.split(':');
    let hour: i64 = clock.next()?.parse().ok()?;
    let minute: i64 = clock.next()?.parse().ok()?;
    let seconds: f64 = clock
        .next()?
        .chars()
        .take_while(|c| *c != '.' && *c != '+' && *c != 'Z')
        .collect::<String>()
        .parse()
        .ok()?;

    let days = days_from_civil(year, month, day);
    let seconds = (days * 86_400) + (hour * 3_600) + (minute * 60) + seconds as i64;
    if seconds < 0 {
        return None;
    }

    Some(UNIX_EPOCH + std::time::Duration::from_secs(seconds as u64))
}

/// Days since the epoch, for a civil date — Howard Hinnant's `days_from_civil`.
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let yoe = year - (era * 400);
    let mp = (month + 9) % 12;
    let doy = ((153 * mp) + 2) / 5 + day - 1;
    let doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
    (era * 146_097) + doe - 719_468
}

/// The page: one row per game, with what the state directory knows about it.
pub fn page(rows: &[Row]) -> String {
    let mut body = String::new();

    if rows.is_empty() {
        body.push_str(
            "      <p class=\"empty\">Nothing has been played on this instance yet. Play something \
             and it appears here, with what the companion has written about it.</p>\n",
        );
    }

    for row in rows {
        let stem = escape(&row.stem);
        let (written, state) = standing(row);
        let view = if row.written_up.is_some() {
            format!("<a class=\"button\" href=\"/catchup/{stem}\">view</a>")
        } else {
            "<span class=\"button disabled\">view</span>".to_string()
        };

        body.push_str(&format!(
            "      <tr>\n\
             \x20       <th scope=\"row\">{stem}</th>\n\
             \x20       <td>{played}</td>\n\
             \x20       <td>{written}</td>\n\
             \x20       <td class=\"state\">{state}</td>\n\
             \x20       <td class=\"actions\">{view} \
             <a class=\"button go\" href=\"/catchup/{stem}/generate\">generate</a></td>\n\
             \x20     </tr>\n",
            played = day(row.played),
        ));
    }

    format!(
        "<!doctype html>\n\
         <html lang=\"en\">\n\
         <head>\n\
         <meta charset=\"utf-8\">\n\
         <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n\
         <title>INFOCompanion — the games</title>\n\
         <style>\n{STYLE}\n</style>\n\
         </head>\n\
         <body>\n\
         \x20 <main class=\"terminal\">\n\
         \x20   <header class=\"masthead\">\n\
         \x20     <div class=\"head\">\n\
         \x20       <div>\n\
         \x20         <p class=\"eyebrow\">what has been played</p>\n\
         \x20         <h1>The games</h1>\n\
         \x20       </div>\n\
         \x20       <p class=\"furniture\"><a class=\"button\" href=\"/debug/bundle\">download the \
         debug bundle</a></p>\n\
         \x20     </div>\n\
         \x20     <p class=\"meta\"><a href=\"/\">INFOCompanion</a> · \
         <a href=\"/term\">the terminal</a> · <a href=\"/multiterm\">the panes</a> · \
         <a href=\"/evening.txt\">the evening's numbers</a> · \
         <a href=\"/debug/bundle\">the debug bundle</a></p>\n\
         \x20     <p class=\"meta\">Only what has played data: the games this instance has watched \
         turns of, or has notes on.</p>\n\
         \x20   </header>\n\
         \x20   <table class=\"games\">\n\
         \x20     <thead><tr><th>game</th><th>last played</th><th>written up</th>\
         <th>state</th><th class=\"actions\">&nbsp;</th></tr></thead>\n\
         \x20     <tbody>\n{body}\
         \x20     </tbody>\n\
         \x20   </table>\n\
         \x20   <footer class=\"colophon\"><em>generate</em> asks the companion for a fresh write-up: it \
         reads everything it holds about the game and takes a minute or two, on its own model call. \
         <em>view</em> opens the last one it wrote. <em>the debug bundle</em> downloads one zip \
         with everything needed to work out what went wrong — the logs, the turn record, the \
         screens, the notes, the saves, the configuration and the prompts — with a manifest inside \
         saying what it holds and what it left out. No API key ever travels in one.</footer>\n\
         \x20 </main>\n\
         </body>\n\
         </html>\n"
    )
}

/// The two cells that answer *when, and is it happening?* — the write-up's time, and its state.
///
/// Three states, all read from what is already on disk (B9, decided 2026-09-21). The row used to
/// show a **day and nothing finer**, so regenerating a report twice in one evening left the column
/// reading exactly the same — which is what *"the time never appears to change"* was. And while a
/// pass was running, the old page was served as though it were the new one, with nothing anywhere
/// saying otherwise.
///
/// - **up to date** — nothing has been asked for since the report was written.
/// - **writing up…** — a request is newer than the report and young enough to still be coming. The
///   previous page stays readable, which is the decision: a stale page with a label on it beats a
///   404 where a document used to be.
/// - **did not finish** — a request older than the report by more than the patience, so it either
///   failed or was never picked up. The harness's own note about it is shown when there is one, and
///   said to be missing when there is not: the generate button doing nothing visible was the one
///   thing this feature could not afford.
fn standing(row: &Row) -> (String, String) {
    let written = match row.written_up {
        Some(when) => format!("{} UTC", day(when)),
        None => "never".to_string(),
    };

    let Some(asked) = row.requested else {
        return (written, String::new());
    };

    let already = row.written_up.map(|when| asked <= when).unwrap_or(false);
    if already {
        return (written, String::new());
    }

    let waited = SystemTime::now().duration_since(asked).unwrap_or_default();
    if waited < WRITE_UP_PATIENCE {
        return (
            written,
            "<span class=\"pending\">writing up…</span>".to_string(),
        );
    }

    let note = match &row.note {
        Some((when, text)) if *when >= asked => format!(
            "<span class=\"failed\">did not finish.</span> The harness said, at {} UTC: {}",
            day(*when),
            escape(text).replace('\n', " ")
        ),
        _ => "<span class=\"failed\">did not finish.</span> Nothing was written about why — the \
              companion may have been unreachable. Try <em>generate</em> again."
            .to_string(),
    };

    (written, note)
}

/// The page shown while a write-up is being asked for, and when there is no report to show.
pub fn plain(title: &str, message: &str) -> String {
    format!(
        "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\
         <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\
         <title>{title}</title><style>\n{STYLE}\n</style></head>\
         <body><main class=\"terminal\"><header class=\"masthead\">\
         <p class=\"eyebrow\">INFOCompanion — two friends, one keyboard, 1983</p>\
         <h1>{title}</h1></header>\
         <div class=\"panels\"><p>{message}</p>\
         <p><a href=\"/\">the index</a> · <a href=\"/games\">the games</a></p>\
         </div></main></body></html>\n",
        title = escape(title),
        message = message,
    )
}

/// A file's contents, if it is there.
pub fn read(path: &Path) -> Option<String> {
    fs::read_to_string(path).ok()
}

/// A stem from a URL, kept to what a file name can be.
///
/// The same rule the daemon applies to a stem it makes from a story file's name, applied again on
/// the way in: a path segment from a browser is a path segment, and `..%2f..%2fetc` must not become
/// a file to read. Anything outside the set is dropped rather than rejected — the caller ends up
/// asking about a game that does not exist, which is the same answer and one less branch.
pub fn safe_stem(stem: &str) -> String {
    let cleaned: String = stem
        .trim()
        .chars()
        .filter(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_'))
        .take(64)
        .collect();

    if cleaned.is_empty() {
        "unknown".to_string()
    } else {
        cleaned.to_lowercase()
    }
}

/// Nothing a game is called ever reaches a page unescaped.
fn escape(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

/// A stamp for the table: date and time, UTC, to the minute.
fn day(when: SystemTime) -> String {
    let seconds = when
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_secs() as i64)
        .unwrap_or(0);
    let days = seconds.div_euclid(86_400);
    let clock = seconds.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    format!(
        "{year:04}-{month:02}-{day:02} {:02}:{:02}",
        clock / 3_600,
        (clock % 3_600) / 60
    )
}

/// The civil date for days since the epoch — `civil_from_days`, the inverse above.
fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let days = days + 719_468;
    let era = if days >= 0 { days } else { days - 146_096 } / 146_097;
    let doe = days - (era * 146_097);
    let yoe = ((doe - (doe / 1_460)) + (doe / 36_524) - (doe / 146_096)) / 365;
    let year = yoe + (era * 400);
    let doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
    let mp = ((5 * doy) + 2) / 153;
    let day = doy - (((153 * mp) + 2) / 5) + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    (if month <= 2 { year + 1 } else { year }, month, day)
}

/// The look: the same tube the rest of the page is drawn on, showing a list.
///
/// Beige plastic, phosphor amber, a monospace face — and one hairline rule under the masthead, so
/// the list reads as something an institution printed rather than as a web table. The two halves
/// are the two halves of this project: it is a 1983 screen, and it is a document about an evening.
const STYLE: &str = r#"
:root {
  --tube: #0d0f0c;
  --phosphor: #f0b429;
  --phosphor-dim: #9c7a24;
  --case: #cfc4a8;
  --ink: #22312e;
  --memo: #f3efe3;
  --stamp: #b4322a;
}
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; background: var(--tube); }
body {
  font-family: ui-monospace, "SFMono-Regular", "Cascadia Mono", Menlo, monospace;
  color: var(--ink);
  padding: 2.5rem 1rem 4rem;
}
.terminal {
  max-width: 60rem;
  margin: 0 auto;
  background: var(--memo);
  border: 1px solid #d8d2c0;
  box-shadow: 0 0 0 1px #000, 0 18px 60px rgba(0,0,0,.6);
}
.masthead {
  padding: 1.4rem 1.6rem 1rem;
  border-bottom: 2px solid var(--ink);
  background:
    repeating-linear-gradient(to bottom, rgba(18,51,49,.06) 0 1px, transparent 1px 4px),
    var(--memo);
}
.eyebrow {
  margin: 0;
  font-size: .7rem;
  letter-spacing: .3em;
  text-transform: uppercase;
  color: var(--phosphor-dim);
}
.masthead h1 { margin: .3rem 0 .5rem; font-size: 1.9rem; color: var(--ink); }
.meta { margin: 0; font-size: .74rem; line-height: 1.7; color: #5c6b66; }
.state { font-size: .74rem; color: #5c6b66; max-width: 22rem; }
.pending { color: var(--phosphor-dim); text-transform: uppercase; letter-spacing: .12em; }
.failed { color: var(--stamp); font-weight: 700; }
.meta a { color: var(--stamp); }
/* The debug bundle, on the screen when it is wanted without ever sitting on top of the reading: a
   row that wraps, rather than a button placed over the eyebrow, which is what a narrow window
   turned the first version into. */
.head { display: flex; align-items: flex-start; justify-content: space-between; gap: 1rem; flex-wrap: wrap; }
.furniture { margin: 0; }
table.games { width: 100%; border-collapse: collapse; }
table.games th, table.games td {
  text-align: left;
  padding: .65rem 1.6rem;
  border-bottom: 1px solid #e3ddcd;
  font-size: .82rem;
  font-weight: 400;
}
table.games thead th {
  font-size: .68rem;
  letter-spacing: .18em;
  text-transform: uppercase;
  color: #6d6a60;
  border-bottom: 1px solid #c9c2ae;
}
table.games tbody th { font-weight: 600; color: var(--ink); }
table.games .actions { text-align: right; white-space: nowrap; }
.button {
  display: inline-block;
  padding: .28rem .7rem;
  border: 1px solid var(--ink);
  color: var(--ink);
  text-decoration: none;
  font-size: .74rem;
  letter-spacing: .06em;
}
.button.go { border-color: var(--stamp); color: var(--stamp); }
.button.disabled { border-color: #cfc9b8; color: #a9a495; }
.empty { padding: 1.6rem; color: #6d6a60; line-height: 1.7; }
.panels { padding: 1.4rem 1.6rem 1.8rem; line-height: 1.7; }
.panels a { color: var(--stamp); }
.colophon {
  padding: 1rem 1.6rem 1.3rem;
  border-top: 1px solid #e3ddcd;
  font-size: .74rem;
  line-height: 1.7;
  color: #6d6a60;
}
"#;

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn temp(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("infocompanion-games-{name}"));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn a_game_with_played_data_is_listed_and_a_library_entry_is_not() {
        let state = temp("listing");
        fs::create_dir_all(state.join("observations")).unwrap();
        fs::create_dir_all(state.join("playback")).unwrap();
        fs::write(state.join("observations").join("advent.jsonl"), "{}\n").unwrap();
        fs::write(state.join("playback").join("zork1.log"), "screen\n").unwrap();

        let rows = listing(&state);
        let stems: Vec<&str> = rows.iter().map(|row| row.stem.as_str()).collect();
        assert!(stems.contains(&"advent"));
        assert!(stems.contains(&"zork1"));
        // A story file in the games directory with nothing played is not this page's business.
        assert_eq!(stems.len(), 2);
        assert!(rows.iter().all(|row| row.written_up.is_none()));
    }

    #[test]
    fn a_report_is_dated_from_its_own_facts_and_that_is_what_the_page_shows() {
        let state = temp("written-up");
        fs::create_dir_all(state.join("observations")).unwrap();
        fs::create_dir_all(state.join("catchup")).unwrap();
        fs::write(state.join("observations").join("advent.jsonl"), "{}\n").unwrap();
        fs::write(
            state.join("catchup").join("advent.json"),
            r#"{"stem":"advent","generated_at":"2026-09-19T01:22:31.1234567+00:00","model":"deepseek-flash","turns":42,"cost_usd":0.0009}"#,
        )
        .unwrap();

        let rows = listing(&state);
        assert!(rows[0].written_up.is_some());
        assert_eq!(day(rows[0].written_up.unwrap()), "2026-09-19 01:22");

        let html = page(&rows);
        assert!(html.contains("/catchup/advent"));
        assert!(html.contains("2026-09-19 01:22 UTC"));
        assert!(html.contains("generate"));
    }

    #[test]
    fn the_games_page_offers_the_debug_bundle() {
        // The 2026-09-20 request, in the words of the person who made it: *"im fed up of manually
        // pulling files off the server"*. The link has to be on the page a person is looking at when
        // they want it, which is this one.
        let state = temp("debug-link");
        fs::create_dir_all(state.join("observations")).unwrap();
        fs::write(state.join("observations").join("advent.jsonl"), "{}\n").unwrap();

        let html = page(&listing(&state));
        assert!(
            html.contains("href=\"/debug/bundle\""),
            "no debug bundle link"
        );
        // A button rather than a line in the small print: the request was for something reachable
        // when something has gone wrong, which is not a moment for hunting through a masthead.
        assert!(
            html.contains("class=\"button\" href=\"/debug/bundle\""),
            "the debug bundle is not a button"
        );
        assert!(html.contains("debug bundle"));
        // And the one claim worth making on the page itself, because it is what somebody hesitating
        // before mailing a zip wants to know.
        assert!(html.contains("No API key ever travels in one"));
    }

    #[test]
    fn every_page_this_serves_has_a_way_back_to_the_index() {
        // The 2026-09-19 UAT, in the player's words: "no navigation back out of /games/ and the sub
        // screens". A reader who followed *the games -> view* was two pages from the front door with
        // no signpost back, and the pages did not say whose site they were on either.
        let state = temp("way-home");
        fs::create_dir_all(state.join("observations")).unwrap();
        fs::write(state.join("observations").join("advent.jsonl"), "{}\n").unwrap();

        let html = page(&listing(&state));
        assert!(
            html.contains("<a href=\"/\">"),
            "the games page has no way home"
        );
        assert!(html.contains("INFOCompanion"));

        let holding = plain("The games", "Nothing here yet.");
        assert!(
            holding.contains("<a href=\"/\">"),
            "the holding page has no way home"
        );
        assert!(holding.contains("<a href=\"/games\">"));
        assert!(holding.contains("two friends, one keyboard, 1983"));
    }

    #[test]
    fn a_game_with_nothing_written_up_yet_has_no_view_to_click() {
        let state = temp("no-report");
        fs::create_dir_all(state.join("observations")).unwrap();
        fs::write(state.join("observations").join("advent.jsonl"), "{}\n").unwrap();

        let html = page(&listing(&state));
        assert!(html.contains("never"));
        assert!(html.contains("button disabled"));
    }

    #[test]
    fn an_empty_state_directory_is_an_empty_page_and_not_an_error() {
        let state = temp("empty");
        let rows = listing(&state);
        assert!(rows.is_empty());
        assert!(page(&rows).contains("Nothing has been played"));

        // No state directory at all is the same answer: a missing folder is an absence.
        assert!(listing(&state.join("gone")).is_empty());
    }

    #[test]
    fn a_stem_from_a_file_name_cannot_inject_anything_into_the_page() {
        let state = temp("escaping");
        fs::create_dir_all(state.join("observations")).unwrap();
        // A name that needs escaping, and one that can be created on any filesystem this runs on:
        // an ampersand is as much a page's business as an angle bracket, and the rule is the same
        // one for both.
        fs::write(state.join("observations").join("a&b.jsonl"), "{}\n").unwrap();

        let html = page(&listing(&state));
        assert!(!html.contains(">a&b<"));
        assert!(html.contains("a&amp;b"));
    }

    #[test]
    fn a_stem_from_a_url_is_kept_to_what_a_file_name_can_be() {
        // A path segment from a browser is a path segment: `..%2f..%2fetc` must not become a file
        // to read, and the answer for anything outside the set is a game that does not exist.
        assert_eq!(safe_stem("advent"), "advent");
        assert_eq!(safe_stem("Wishbringer"), "wishbringer");
        assert_eq!(safe_stem("advent-2_x"), "advent-2_x");
        assert_eq!(safe_stem("../../etc/passwd"), "etcpasswd");
        assert_eq!(safe_stem("advent%2F..%2Fsecret"), "advent2f2fsecret");
        assert_eq!(safe_stem(""), "unknown");
        assert_eq!(safe_stem("   "), "unknown");
    }

    // -- the three states of a write-up (B9) ------------------------------------------------

    /// A state directory with a game that has been played, and nothing else.
    fn played(name: &str) -> std::path::PathBuf {
        let state = temp(name);
        fs::create_dir_all(state.join("observations")).unwrap();
        fs::write(state.join("observations").join("advent.jsonl"), "{}\n").unwrap();
        state
    }

    /// A report, dated `ago` seconds before now.
    fn report_written(state: &Path, ago: u64) {
        fs::create_dir_all(state.join("catchup")).unwrap();
        let when = SystemTime::now() - std::time::Duration::from_secs(ago);
        fs::write(
            state.join("catchup").join("advent.json"),
            format!(
                "{{\"stem\":\"advent\",\"generated_at\":\"{}\",\"model\":\"m\",\"turns\":1}}",
                rfc3339(when)
            ),
        )
        .unwrap();
    }

    /// A request, made `ago` seconds before now — the marker the generate button leaves.
    fn asked(state: &Path, ago: u64) {
        fs::create_dir_all(state.join("catchup")).unwrap();
        fs::write(state.join("catchup").join("advent.requested"), "asked\n").unwrap();
        set_mtime(
            &state.join("catchup").join("advent.requested"),
            SystemTime::now() - std::time::Duration::from_secs(ago),
        );
    }

    /// `YYYY-MM-DDTHH:MM:SSZ`, for the fixtures — the same shape the agent writes.
    fn rfc3339(when: SystemTime) -> String {
        let text = day(when);
        format!("{}T{}:00Z", &text[..10], &text[11..])
    }

    fn set_mtime(path: &Path, when: SystemTime) {
        // Only backwards, and only by whole seconds, which is what the filetime crate-less way of
        // setting an mtime can do without a dependency: the caller passes a time in the past.
        let when = when
            .duration_since(UNIX_EPOCH)
            .map(|since| since.as_secs())
            .unwrap_or(0);
        let times = std::fs::FileTimes::new()
            .set_modified(UNIX_EPOCH + std::time::Duration::from_secs(when));
        let file = fs::OpenOptions::new().write(true).open(path).unwrap();
        file.set_times(times).unwrap();
    }

    #[test]
    fn a_request_that_is_still_coming_reads_as_writing_up() {
        // The half of B9 that was invisible: press generate, press view, and read yesterday's page
        // with nothing anywhere saying a new one was being written.
        let state = played("writing-up");
        report_written(&state, 3_600);
        asked(&state, 30);

        let rows = listing(&state);
        assert!(rows[0].requested.is_some());

        let html = page(&rows);
        assert!(html.contains("writing up…"), "{html}");
        assert!(!html.contains("did not finish"));
        // And the old page is still there to read, which is the decision: a labelled stale page
        // beats a 404 where a document used to be.
        assert!(html.contains("/catchup/advent"));
    }

    #[test]
    fn a_request_that_produced_nothing_shows_what_the_harness_said() {
        // The words already existed — `WriteUpTheGameAsync` appends a harness note when it gives up
        // — and nothing read them, so the failure was invisible from the browser and the row kept
        // yesterday's date for ever.
        let state = played("failed");
        report_written(&state, 3_600);
        asked(&state, 900);
        fs::create_dir_all(state.join("meta")).unwrap();
        // Dated after the request and before now, which is what a failed pass looks like.
        fs::write(
            state.join("meta").join("advent.md"),
            format!(
                "<!-- the header -->\n\n# notes about the harness — advent\n\n\
                 ## harness — {}\n\n\
                 the write-up pass ended without a report, after three attempts. Nothing was \
                 written up.\n",
                rfc3339(SystemTime::now() - std::time::Duration::from_secs(800))
            ),
        )
        .unwrap();

        let rows = listing(&state);
        assert!(rows[0].note.is_some());
        let html = page(&rows);
        assert!(html.contains("did not finish"), "{html}");
        assert!(
            html.contains("ended without a report, after three attempts"),
            "{html}"
        );
        assert!(!html.contains("Nothing was written about why"), "{html}");
    }

    #[test]
    fn a_request_that_produced_nothing_and_no_note_says_so_instead_of_showing_nothing() {
        let state = played("failed-silent");
        report_written(&state, 3_600);
        asked(&state, 900);

        let html = page(&listing(&state));
        assert!(html.contains("did not finish"), "{html}");
        assert!(html.contains("Nothing was written about why"), "{html}");
    }

    #[test]
    fn a_report_written_after_the_request_clears_the_state_again() {
        // Nothing deletes the marker: a pass that finishes clears the state by being newer than the
        // thing that asked for it, which is why the check is a comparison of two times rather than a
        // flag somebody has to remember to turn off.
        let state = played("settled");
        asked(&state, 900);
        report_written(&state, 60);

        let html = page(&listing(&state));
        assert!(!html.contains("writing up…"), "{html}");
        assert!(!html.contains("did not finish"), "{html}");
        assert!(html.contains("UTC"), "{html}");
    }

    #[test]
    fn the_written_up_column_carries_a_time_and_not_just_a_day() {
        // Two write-ups in one evening used to leave the column reading identically, which is
        // exactly what *"the time never appears to change"* was.
        let state = played("with-time");
        report_written(&state, 120);
        let html = page(&listing(&state));

        assert!(html.contains("UTC"));
        assert!(
            html.contains(&format!(
                "{} UTC",
                day(SystemTime::now() - std::time::Duration::from_secs(120))
            )),
            "{html}"
        );
    }

    #[test]
    fn the_harness_note_is_read_by_its_heading_and_ends_at_the_next_section() {
        let state = played("note-reader");
        fs::create_dir_all(state.join("meta")).unwrap();
        fs::write(
            state.join("meta").join("advent.md"),
            "# notes about the harness — advent\n\n\
             ## harness — 2026-09-20T10:00:00Z\n\n\
             the older failure\n\n\
             ## harness — 2026-09-21T15:20:00Z\n\n\
             the write-up pass ended without a report.\n\
             \n\
             Nothing was written up.\n\n\
             ## myself — 2026-09-21T16:00:00Z\n\n\
             a note of the companion's own, which is not the harness's\n",
        )
        .unwrap();

        let (when, text) = harness_note(&state, "advent").expect("the newest harness note");
        assert_eq!(day(when), "2026-09-21 15:20");
        assert!(text.contains("ended without a report"), "{text}");
        assert!(text.contains("Nothing was written up"), "{text}");
        assert!(!text.contains("a note of the companion's own"), "{text}");
        assert!(!text.contains("the older failure"), "{text}");

        // A file with no harness heading at all, and a game with no file: both are nothing.
        fs::write(
            state.join("meta").join("advent.md"),
            "## myself — 2026-09-21T16:00:00Z\n\nmine\n",
        )
        .unwrap();
        assert!(harness_note(&state, "advent").is_none());
        assert!(harness_note(&state, "a-game-nobody-played").is_none());
    }
}
