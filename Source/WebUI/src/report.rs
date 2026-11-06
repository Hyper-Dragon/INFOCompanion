//! The evening report (07_webui.md §6 (`W26`–`W30`)).
//!
//! One page, computed from the two log files an evening leaves behind: what it cost, where the
//! money went, and the handful of numbers that turn the next change into an A/B rather than an
//! argument. Every figure here was extracted by hand after the 2026-09-16 evening, and by hand
//! is how a finding gets lost.
//!
//! It is a *reader*: it opens `<state>/logs`, parses, and prints. Nothing here touches the
//! session, nothing is cached between requests, and a missing or half-written log is a smaller
//! report rather than an error — the tolerance every read-only surface in this program has.
//! Timings are formatted for a person (2.1s, 91m 55s), percentages to one place, money to four.

use std::collections::BTreeMap;
use std::path::Path;

/// One model call, reduced to the numbers the report prints.
struct Call {
    purpose: String,
    cost: f64,
    latency_ms: u64,
    truncated: bool,
}

/// The whole report, as text.
pub fn document(state_dir: &Path) -> String {
    let logs = state_dir.join("logs");
    let agent_log = latest(&logs, "agent-");
    // `core-` since the rename: a *session* in this project is the player's presence, and this file is
    // the core's own record of it, with the agent's log beside it. The name an earlier build
    // wrote is still looked for, so a report over an older evening still finds its two halves.
    let session_log = latest(&logs, "core-").or_else(|| latest(&logs, "session-"));

    let agent = agent_log
        .as_ref()
        .map(|path| std::fs::read_to_string(path).unwrap_or_default())
        .unwrap_or_default();
    let session = session_log
        .as_ref()
        .map(|path| std::fs::read_to_string(path).unwrap_or_default())
        .unwrap_or_default();

    render(
        agent_log
            .as_ref()
            .and_then(|path| path.file_name())
            .and_then(|name| name.to_str())
            .unwrap_or("(none)"),
        session_log
            .as_ref()
            .and_then(|path| path.file_name())
            .and_then(|name| name.to_str())
            .unwrap_or("(none)"),
        &agent,
        &session,
    )
}

/// The newest file in `logs` whose name starts with `prefix` and ends `.jsonl`.
///
/// The names carry a UTC timestamp, so lexical order is chronological order — the same fact the
/// agent itself relies on when it finds its own log to rotate.
fn latest(logs: &Path, prefix: &str) -> Option<std::path::PathBuf> {
    let mut best: Option<std::path::PathBuf> = None;
    for entry in std::fs::read_dir(logs).ok()? {
        let Ok(entry) = entry else { continue };
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if name.starts_with(prefix) && name.ends_with(".jsonl") {
            let path = entry.path();
            if best.as_ref().map(|b| b < &path).unwrap_or(true) {
                best = Some(path);
            }
        }
    }
    best
}

/// The report body, from the two logs' text. Pure, so the tests can drive it with fixtures.
pub fn render(agent_name: &str, session_name: &str, agent: &str, session: &str) -> String {
    let mut calls: Vec<Call> = Vec::new();
    let mut kinds: BTreeMap<String, usize> = BTreeMap::new();
    let mut limits: BTreeMap<String, usize> = BTreeMap::new();
    let mut triggers: BTreeMap<String, usize> = BTreeMap::new();
    let mut mission_ends: BTreeMap<String, usize> = BTreeMap::new();
    let mut warn_kinds: BTreeMap<String, usize> = BTreeMap::new();
    let mut refuses = 0usize;
    let mut misses_repeated = 0usize;
    let mut cost_total = 0.0f64;
    let mut first_ts: Option<String> = None;
    let mut last_ts: Option<String> = None;

    for line in agent.lines() {
        let Ok(value) = serde_json::from_str::<serde_json::Value>(line) else {
            continue;
        };
        let kind = value.get("kind").and_then(|v| v.as_str()).unwrap_or("");
        *kinds.entry(kind.to_string()).or_default() += 1;
        if let Some(ts) = value.get("ts").and_then(|v| v.as_str()) {
            if first_ts.is_none() {
                first_ts = Some(ts.to_string());
            }
            last_ts = Some(ts.to_string());
        }

        match kind {
            "llm_call" => {
                let purpose = value
                    .get("purpose")
                    .and_then(|v| v.as_str())
                    .unwrap_or("?")
                    .to_string();
                let cost = value
                    .get("cost_usd")
                    .and_then(|v| v.as_f64())
                    .unwrap_or(0.0);
                cost_total = value
                    .get("cost_total_usd")
                    .and_then(|v| v.as_f64())
                    .unwrap_or(cost_total + cost);
                calls.push(Call {
                    purpose,
                    cost,
                    latency_ms: value
                        .get("latency_ms")
                        .and_then(|v| v.as_u64())
                        .unwrap_or(0),
                    truncated: value
                        .get("truncated")
                        .and_then(|v| v.as_bool())
                        .unwrap_or(false),
                });
            }
            "unattended_limit" => {
                let purpose = value
                    .get("purpose")
                    .and_then(|v| v.as_str())
                    .unwrap_or("?")
                    .to_string();
                *limits.entry(purpose).or_default() += 1;
            }
            "trigger" => {
                let reason = value
                    .get("reason")
                    .and_then(|v| v.as_str())
                    .unwrap_or("?")
                    .to_string();
                *triggers.entry(reason).or_default() += 1;
            }
            "mission_end" => {
                let reason = value
                    .get("reason")
                    .and_then(|v| v.as_str())
                    .unwrap_or("?")
                    .to_string();
                *mission_ends.entry(reason).or_default() += 1;
            }
            "warn" => {
                let msg = value.get("msg").and_then(|v| v.as_str()).unwrap_or("");
                let name = if msg.contains("max_tokens") {
                    "hit max_tokens"
                } else if msg.contains("parser refused") {
                    "the parser refused a word"
                } else if msg.contains("cut off") {
                    "a write was cut off"
                } else {
                    "other"
                };
                *warn_kinds.entry(name.to_string()).or_default() += 1;
                if name == "the parser refused a word" {
                    refuses += 1;
                }
            }
            "act_missed" => {
                if value
                    .get("after_another_miss")
                    .and_then(|v| v.as_bool())
                    .unwrap_or(false)
                {
                    misses_repeated += 1;
                }
            }
            _ => {}
        }
    }

    let mut turns = 0usize;
    let mut moves = 0usize;
    let mut typed = 0usize;
    let mut session_first: Option<String> = None;
    let mut session_last: Option<String> = None;
    for line in session.lines() {
        let Ok(value) = serde_json::from_str::<serde_json::Value>(line) else {
            continue;
        };
        if let Some(ts) = value.get("ts").and_then(|v| v.as_str()) {
            if session_first.is_none() {
                session_first = Some(ts.to_string());
            }
            session_last = Some(ts.to_string());
        }
        match value.get("kind").and_then(|v| v.as_str()).unwrap_or("") {
            "turn" => turns += 1,
            "agent_game_input" => moves += 1,
            "player_direct" => typed += 1,
            _ => {}
        }
    }

    let count = |kind: &str| kinds.get(kind).copied().unwrap_or(0);
    let (from, to) = (
        first_ts.or(session_first).unwrap_or_default(),
        last_ts.or(session_last).unwrap_or_default(),
    );

    let mut out = String::new();
    out.push_str("INFOCompanion — evening report\n\n");
    out.push_str(&format!("  agent log    {agent_name}\n"));
    out.push_str(&format!("  session log  {session_name}\n\n"));

    match (parse_ts(&from), parse_ts(&to)) {
        (Some(start), Some(end)) if end >= start => {
            out.push_str(&format!(
                "  span         {from} → {to} ({})\n",
                human_seconds(end - start)
            ));
        }
        _ => out.push_str("  span         (no timestamps found)\n"),
    }
    out.push_str(&format!("  turns        {turns}\n"));
    out.push_str(&format!(
        "  moves        {moves} by the companion, {typed} typed by the player\n"
    ));
    out.push_str(&format!("  calls        {}\n", calls.len()));
    if !calls.is_empty() {
        out.push_str(&format!("  spend        ${cost_total:.4}"));
        if let (Some(start), Some(end)) = (parse_ts(&from), parse_ts(&to)) {
            let hours = (end - start) as f64 / 3600.0;
            if hours > 0.01 {
                out.push_str(&format!("  (about ${:.2}/hour)", cost_total / hours));
            }
        }
        out.push('\n');
    }

    let latency = percentiles(&calls);
    if let Some((p50, p90, p95, max)) = latency {
        out.push_str(&format!(
            "  latency      p50 {}  p90 {}  p95 {}  max {}\n",
            human_ms(p50),
            human_ms(p90),
            human_ms(p95),
            human_ms(max)
        ));
    }

    // By purpose, busiest spender first — the one question worth asking of a bill.
    let mut by_purpose: BTreeMap<&str, (usize, f64, u64)> = BTreeMap::new();
    for call in &calls {
        let entry = by_purpose.entry(call.purpose.as_str()).or_default();
        entry.0 += 1;
        entry.1 += call.cost;
        entry.2 += call.latency_ms;
    }
    if !by_purpose.is_empty() {
        out.push_str("\nby purpose              calls    spend   share   avg\n");
        let mut rows: Vec<(&str, (usize, f64, u64))> = by_purpose.into_iter().collect();
        rows.sort_by(|a, b| {
            b.1 .1
                .partial_cmp(&a.1 .1)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        for (purpose, (n, cost, ms)) in rows {
            out.push_str(&format!(
                "  {:20} {:5}  {:>8}  {:>5.1}%  {}\n",
                purpose,
                n,
                format!("${cost:.4}"),
                if cost_total > 0.0 {
                    cost / cost_total * 100.0
                } else {
                    0.0
                },
                human_ms(ms / n.max(1) as u64)
            ));
        }
    }

    // The findings the report exists for.
    let misses = count("act_missed");
    let repairs = calls.iter().filter(|c| c.purpose == "act-missed").count();
    let repair_spend: f64 = calls
        .iter()
        .filter(|c| c.purpose == "act-missed")
        .map(|c| c.cost)
        .sum();
    let truncated: Vec<&Call> = calls.iter().filter(|c| c.truncated).collect();

    out.push_str("\nfindings\n");
    if misses > 0 || repairs > 0 {
        out.push_str(&format!(
            "  act_missed        {misses} events, {repairs} repair calls, ${repair_spend:.4}{}",
            if cost_total > 0.0 {
                format!(" ({:.1}% of the spend)", repair_spend / cost_total * 100.0)
            } else {
                String::new()
            }
        ));
        if misses_repeated > 0 {
            out.push_str(&format!(", {misses_repeated} in a run"));
        }
        out.push('\n');
    }
    if !truncated.is_empty() {
        out.push_str(&format!("  truncated         {} calls (", truncated.len()));
        let mut by: BTreeMap<&str, usize> = BTreeMap::new();
        for call in &truncated {
            *by.entry(call.purpose.as_str()).or_default() += 1;
        }
        let list: Vec<String> = by.iter().map(|(p, n)| format!("{p} {n}")).collect();
        out.push_str(&format!("{})\n", list.join(", ")));
    }
    if !limits.is_empty() {
        let list: Vec<String> = limits.iter().map(|(p, n)| format!("{p} {n}")).collect();
        out.push_str(&format!(
            "  unattended_limit  {} ({})\n",
            limits.values().sum::<usize>(),
            list.join(", ")
        ));
    }
    if count("errand_declared") > 0 || count("mission_start") > 0 {
        let ends: Vec<String> = mission_ends
            .iter()
            .map(|(r, n)| format!("{n} {r}"))
            .collect();
        out.push_str(&format!(
            "  errands           {} declared, {} missions, {} steps\n",
            count("errand_declared"),
            count("mission_start"),
            count("mission_step")
        ));
        if !ends.is_empty() {
            out.push_str(&format!("                    ended: {}\n", ends.join("; ")));
        }
    }
    out.push_str(&format!(
        "  notebook          {} rewrites, {} section updates\n",
        count("notebook_updated"),
        count("notebook_section_updated")
    ));
    if let Some(n) = kinds.get("warn") {
        let list: Vec<String> = warn_kinds.iter().map(|(k, n)| format!("{k} {n}")).collect();
        out.push_str(&format!("  warns             {n} ({})\n", list.join(", ")));
    }
    if refuses > 0 {
        out.push_str(&format!(
            "                    {refuses} parser refusals (remembered per game: state/vocabulary)\n"
        ));
    }
    if !triggers.is_empty() {
        let list: Vec<String> = triggers.iter().map(|(r, n)| format!("{r} {n}")).collect();
        out.push_str(&format!("  triggers          {}\n", list.join(", ")));
    }

    out.push_str(
        "\nEach finding above is a claim about a change: act_missed is the missed-move repair,\n\
         truncated is a reply cut off by the token ceiling, the notebook counts are the memory file,\n\
         unattended_limit is the unattended allowance, and the parser refusals are the words the\n\
         game's own parser would not take. The baselines a change is judged against are in the\n\
         project's operations notes.\n",
    );
    out
}

/// p50, p90, p95 and the maximum, in milliseconds. None when nothing was measured.
fn percentiles(calls: &[Call]) -> Option<(u64, u64, u64, u64)> {
    if calls.is_empty() {
        return None;
    }
    let mut latencies: Vec<u64> = calls.iter().map(|call| call.latency_ms).collect();
    latencies.sort_unstable();
    let at = |fraction: f64| {
        latencies[((latencies.len() as f64 * fraction) as usize).min(latencies.len() - 1)]
    };
    Some((at(0.50), at(0.90), at(0.95), *latencies.last().unwrap()))
}

fn human_ms(ms: u64) -> String {
    if ms >= 1000 {
        format!("{:.1}s", ms as f64 / 1000.0)
    } else {
        format!("{ms}ms")
    }
}

fn human_seconds(seconds: i64) -> String {
    let minutes = seconds / 60;
    if minutes >= 60 {
        format!("{}h {:02}m", minutes / 60, minutes % 60)
    } else if minutes > 0 {
        format!("{}m {:02}s", minutes, seconds % 60)
    } else {
        format!("{seconds}s")
    }
}

/// `2026-09-16T22:26:29Z` (or with fractional seconds) as seconds since the epoch.
fn parse_ts(ts: &str) -> Option<i64> {
    if ts.len() < 19 {
        return None;
    }
    let year: i64 = ts.get(0..4)?.parse().ok()?;
    let month: i64 = ts.get(5..7)?.parse().ok()?;
    let day: i64 = ts.get(8..10)?.parse().ok()?;
    let hour: i64 = ts.get(11..13)?.parse().ok()?;
    let minute: i64 = ts.get(14..16)?.parse().ok()?;
    let second: i64 = ts.get(17..19)?.parse().ok()?;
    Some(days_from_civil(year, month, day) * 86_400 + hour * 3_600 + minute * 60 + second)
}

/// Days since 1970-01-01 for a civil date (Howard Hinnant's algorithm, the short version).
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let year_of_era = year - era * 400;
    let day_of_year = (153 * ((month + 9) % 12) + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

#[cfg(test)]
mod tests {
    use super::*;

    const AGENT: &str = r#"{"kind":"session_start","ts":"2026-09-16T22:24:46Z"}
{"kind":"llm_call","purpose":"mission-step","cost_usd":0.01,"cost_total_usd":0.01,"latency_ms":4000,"truncated":false,"ts":"2026-09-16T22:25:00Z"}
{"kind":"llm_call","purpose":"act-missed","cost_usd":0.03,"cost_total_usd":0.04,"latency_ms":2000,"truncated":true,"ts":"2026-09-16T22:26:00Z"}
{"kind":"act_missed","purpose":"mission-step","after_another_miss":true,"ts":"2026-09-16T22:26:00Z"}
{"kind":"unattended_limit","purpose":"reconcile","ts":"2026-09-16T22:27:00Z"}
{"kind":"trigger","reason":"idlenudge","ts":"2026-09-16T22:28:00Z"}
{"kind":"errand_declared","goal":"g","ts":"2026-09-16T22:29:00Z"}
{"kind":"mission_start","ts":"2026-09-16T22:29:00Z"}
{"kind":"mission_step","ts":"2026-09-16T22:30:00Z"}
{"kind":"warn","msg":"the game's parser refused a command; that verb will not be sent again","ts":"2026-09-16T22:31:00Z"}
{"kind":"warn","msg":"the model's reply hit max_tokens and may be incomplete","ts":"2026-09-16T22:32:00Z"}
{"kind":"notebook_updated","ts":"2026-09-16T22:33:00Z"}
{"kind":"notebook_section_updated","section":"Key facts","ts":"2026-09-16T22:34:00Z"}
"#;

    const SESSION: &str = r#"{"kind":"turn","ts":"2026-09-16T22:25:00Z"}
{"kind":"turn","ts":"2026-09-16T22:26:00Z"}
{"kind":"agent_game_input","ts":"2026-09-16T22:26:30Z"}
{"kind":"player_direct","line":"look","ts":"2026-09-16T22:27:30Z"}
"#;

    #[test]
    fn the_report_prints_what_the_evening_measured() {
        let text = render("agent-x.jsonl", "core-x.jsonl", AGENT, SESSION);
        assert!(text.contains("turns        2"), "{text}");
        assert!(
            text.contains("1 by the companion, 1 typed by the player"),
            "{text}"
        );
        assert!(text.contains("calls        2"), "{text}");
        assert!(text.contains("$0.0400"), "{text}");
        assert!(text.contains("mission-step"), "{text}");
        assert!(
            text.contains("act_missed        1 events, 1 repair calls, $0.0300"),
            "{text}"
        );
        assert!(text.contains("(75.0% of the spend)"), "{text}");
        assert!(text.contains("in a run"), "{text}");
        assert!(
            text.contains("truncated         1 calls (act-missed 1)"),
            "{text}"
        );
        assert!(text.contains("unattended_limit  1 (reconcile 1)"), "{text}");
        assert!(text.contains("1 declared, 1 missions, 1 steps"), "{text}");
        assert!(
            text.contains("notebook          1 rewrites, 1 section updates"),
            "{text}"
        );
        assert!(text.contains("warns             2"), "{text}");
        assert!(text.contains("triggers          idlenudge 1"), "{text}");
    }

    #[test]
    fn an_empty_or_missing_log_is_a_smaller_report_not_a_failure() {
        let text = render("(none)", "(none)", "", "");
        assert!(text.contains("evening report"));
        assert!(text.contains("calls        0"));
        assert!(!text.contains("by purpose"));
        assert!(!text.contains("findings\n  act_missed"));
    }

    #[test]
    fn timestamps_round_trip_and_format_for_a_person() {
        let start = parse_ts("2026-09-16T22:00:00Z").unwrap();
        let end = parse_ts("2026-09-16T23:30:55Z").unwrap();
        assert_eq!(end - start, 5455);
        assert_eq!(human_seconds(5455), "1h 30m");
        assert_eq!(human_seconds(95), "1m 35s");
        assert_eq!(human_seconds(9), "9s");
        assert_eq!(human_ms(45688), "45.7s");
        assert_eq!(human_ms(412), "412ms");
    }
}
