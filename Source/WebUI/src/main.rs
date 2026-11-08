//! INFOCompanion's web interface: the panes, in a browser, with nothing in between.
//!
//! **A dumb mux, deliberately.** One SSH shell per pane to the daemon, bytes piped in both
//! directions. Every byte that reaches a screen was rendered by the daemon, which owns colour,
//! layout and what each pane may show; this program knows nothing about turns, games or
//! messages. If it ever needs to know what a byte *means*, it belongs on the other side of the
//! socket instead.
//!
//! It speaks to the daemon with `russh` — the same crate the daemon serves with, used here as
//! a client. One SSH implementation at both ends means there is no third-party client to
//! disagree with our server, and one place to fix it when they do.
//!
//! No authentication, by design: one server per person, and whoever can reach the port is at
//! the keyboard. If you want auth, put this behind a proxy.

mod assets;
mod bundle;
mod config;
mod games;
mod identity;
mod pane;
mod panes;
mod persona;
mod report;
mod stats;
mod synth;
mod voice;

use std::collections::HashMap;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use axum::body::Body;
use axum::extract::{Json, Path as AxumPath, Query, State, WebSocketUpgrade};
use axum::http::{header, HeaderValue, StatusCode, Uri};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use panes::Pane;
use tokio::sync::broadcast;

/// Everything the bridge is told, read once at startup.
struct Settings {
    web_bind: String,
    web_port: u16,
    web_root: PathBuf,
    daemon_host: String,
    daemon_port: u16,
    password: String,
    /// The two names the panes are labelled with and the stats document reports (09_stats.md §3 (`ST8`–`ST9`)):
    /// read once, said verbatim.
    player_name: String,
    persona: String,
    /// The library and the state directory the pointer lives in — read per request by
    /// `/stats.json`, and touched by nothing else.
    games_dir: PathBuf,
    state_dir: PathBuf,
    /// Where the engine and the persona are read from, so a debug bundle can carry the prompts that
    /// were actually in force (07_webui.md §6.2 (`W36`–`W43`)). Read, never written.
    prompts_dir: PathBuf,
    /// The voice, read out of the **persona file** rather than the environment
    /// (`04_companion.md` §11 (`C61`)).
    ///
    /// A voice belongs to a character: the front matter carries it beside the name, so swapping the
    /// persona carries its voice with it. Nothing named there means no voice at all — the page is
    /// not offered a switch it could do nothing with (`V15`) — and where the voice is *spoken* is a
    /// separate, deployment-shaped answer (§7 of `08_webui_tts.md`).
    voice: Option<String>,
    /// Where the voice is spoken, when the deployment pointed at a service
    /// (`08_webui_tts.md` §7 `V27`).
    ///
    /// An **address**, like the daemon's, so it is a deployment's own thing and belongs beside the
    /// other addresses rather than in the file of this program's tunables. Nothing here is not a
    /// broken deployment: it is the browser's own voice speaking instead (`V15`), which is the sink
    /// this project shipped before it had anywhere to point.
    voice_url: Option<String>,
    /// What that service calls its model, and the rate it answers at. Both are passed through
    /// untouched; neither is a fact about INFOCompanion.
    voice_model: String,
    voice_rate: u32,
    /// How fast the voice says it, as a multiplier on whatever the endpoint calls normal.
    ///
    /// A deployment's own number, because the thing it is matched against is a **voice**: the
    /// measured rate differs per voice and per engine (this project's reference pair speaks 195 wpm
    /// at 1.0), and the only reason to set it is to bring the speech close to the pace the text is
    /// written at — at 1.0 a 240 wpm page leaves the voice trailing by about a quarter of the line.
    /// **1.0 is "no opinion" and sends no field at all**, so a deployment that has none speaks to
    /// the endpoint on exactly the shape this project documents (`08_webui_tts.md` §7).
    voice_speed: f64,
    /// The bridge's own numbers: the size a pane starts at, what a page may ask for, and how long
    /// a typed line is given (07_webui.md §7 (`W44`–`W47`)). Resolved once from `webui-config.json` and the
    /// environment, in that order of precedence reversed.
    tuning: config::Tuning,
    single: Vec<Pane>,
    multi: Vec<Pane>,
}

/// An environment variable, if it names one, or `None`.
///
/// A variable that is set but empty is treated as unset: in a compose file a blank value is
/// almost always an unset one with a `:` next to it. This is the form the tunables need, because
/// "the deployment did not speak" and "the deployment spoke with the shipped value" are different
/// answers when a file is waiting to fill in whatever nobody named (10_delivery.md §2 (`D5`)).
fn env_lookup(names: &[&str]) -> Option<String> {
    for name in names {
        if let Ok(value) = std::env::var(name) {
            let value = value.trim();
            if !value.is_empty() {
                return Some(value.to_string());
            }
        }
    }
    None
}

/// An environment variable, or its default.
fn env_or(names: &[&str], default: &str) -> String {
    env_lookup(names).unwrap_or_else(|| default.to_string())
}

/// A numbered environment variable, when the environment names one that parses.
fn env_number<T: std::str::FromStr>(names: &[&str]) -> Option<T> {
    env_lookup(names).and_then(|value| value.parse().ok())
}

/// A numbered environment variable, defaulting when it is missing or unreadable.
fn env_or_number<T: std::str::FromStr>(names: &[&str], default: T) -> T {
    env_number(names).unwrap_or(default)
}

impl Settings {
    fn from_env() -> Self {
        // Read once and kept: the panes are labelled with them, and the stats document reports
        // them verbatim (09_stats.md §3 (`ST8`–`ST9`)) — one setting with two readers, never two settings.
        let persona = env_or(&["INFOCOMPANION_PERSONA"], "companion");
        let player_name = env_or(&["INFOCOMPANION_PLAYER_NAME"], "Peter");
        let names = panes::Names {
            agent: persona.clone(),
            // The same name the daemon puts in the game's own text (`[Peter]>`), so the pane you
            // type in is labelled with the player rather than a category.
            player: player_name.clone(),
        };
        // The voice is **the persona's**, so it is read where the persona is: the front matter of
        // the same file the daemon reads for the name, which is what makes swapping the character
        // one slug and one file (`C61`, `C66`). Where it is spoken is a separate answer, and a
        // deployment may give one without the other (08_webui_tts.md §7).
        let prompts_dir = PathBuf::from(env_or(&["INFOCOMPANION_PROMPTS_DIR"], "/prompts"));
        // The slug as the daemon resolves it — `james` is the daemon's default, where `companion`
        // is only an alias for the pane labels. Both ends have to be reading the same character, or
        // the page publishes the voice of somebody nobody is playing.
        let persona_slug = env_or(&["INFOCOMPANION_PERSONA"], "james");
        let persona_path = persona::path(&prompts_dir, &persona_slug);
        let voice = persona::declared_voice(&persona_path);
        let voice_url = env_lookup(&["INFOCOMPANION_VOICE_URL"]);
        let voice_model = env_or(&["INFOCOMPANION_VOICE_MODEL"], "kokoro");
        let voice_rate = env_or_number(&["INFOCOMPANION_VOICE_RATE"], 24000);
        let voice_speed = env_or_number(&["INFOCOMPANION_VOICE_SPEED"], 1.0);
        let single = env_or(&["INFOCOMPANION_PANE_SINGLE"], "player");
        let multi = env_or(&["INFOCOMPANION_PANE_MULTI"], "game,companion,me");
        let labels = env_or(&["INFOCOMPANION_PANE_LABELS"], "");

        // The tunes come from a file in the state directory, with the environment winning where it
        // speaks (10_delivery.md §2 (`D5`)). Read here rather than in `Settings`'s fields because the state
        // directory has to be known first — it is the directory the file is found in, so it cannot
        // be one of the settings the file sets.
        let state_dir = PathBuf::from(env_or(&["INFOCOMPANION_STATE_DIR"], "/state"));
        let explicit = env_lookup(&["INFOCOMPANION_WEBUI_CONFIG"]);
        let loaded = config::load(&state_dir, explicit.as_deref().map(std::path::Path::new));
        for warning in &loaded.warnings {
            eprintln!("webui: WARNING {warning}");
        }
        match (&loaded.source, &loaded.problem) {
            (Some(path), _) => eprintln!("webui: INFO configuration file {}", path.display()),
            (None, Some(problem)) => eprintln!(
                "webui: ERROR unusable configuration file: {problem}; using the environment and the \
                 defaults"
            ),
            (None, None) => {}
        }

        let overrides = config::Overrides {
            pane_cols: env_number(&["INFOCOMPANION_PANE_COLS"]),
            pane_cols_min: env_number(&["INFOCOMPANION_PANE_COLS_MIN"]),
            pane_cols_max: env_number(&["INFOCOMPANION_PANE_COLS_MAX"]),
            pane_rows: env_number(&["INFOCOMPANION_PANE_ROWS"]),
            pane_rows_min: env_number(&["INFOCOMPANION_PANE_ROWS_MIN"]),
            pane_rows_max: env_number(&["INFOCOMPANION_PANE_ROWS_MAX"]),
            type_line_settle_ms: env_number(&["INFOCOMPANION_TYPE_LINE_SETTLE_MS"]),
        };
        let (tuning, corrections) = config::resolve(&loaded.config, &overrides);
        for correction in &corrections {
            eprintln!("webui: WARNING {correction}");
        }
        eprintln!(
            "webui: INFO panes {}x{} (cols {}..{}, rows {}..{}), typed line settled for {}ms",
            tuning.cols.default,
            tuning.rows.default,
            tuning.cols.least,
            tuning.cols.most,
            tuning.rows.least,
            tuning.rows.most,
            tuning.type_line_settle.as_millis()
        );
        // Said out loud because it is the answer to "why is there no switch", and to "why is the
        // browser talking": a bridge that cannot say which way it went is a bridge somebody debugs
        // by reading its own source.
        match (&voice, &voice_url) {
            (Some(named), Some(at)) => {
                eprintln!("webui: INFO voice {named}, spoken at {at} (speed {voice_speed})")
            }
            (Some(named), None) => eprintln!(
                "webui: INFO voice {named}, spoken by the browser (no INFOCOMPANION_VOICE_URL)"
            ),
            // Said with the path in it, because "the persona names no voice" is only actionable
            // next to the file that was looked at.
            (None, _) => eprintln!(
                "webui: INFO no voice named in {}: the page is offered no switch",
                persona_path.display()
            ),
        }

        // The labels list is positional and shared by both pages: the first pane of each page
        // takes the first label. A page with one pane and a three-label list is not an error,
        // it is a page with one named pane.
        let label_list: Vec<&str> = labels.split(',').map(str::trim).collect();
        let first_label = label_list.first().copied().unwrap_or("");

        Self {
            web_bind: env_or(&["INFOCOMPANION_WEB_BIND"], "0.0.0.0"),
            web_port: env_or_number(&["INFOCOMPANION_WEB_PORT"], 8080),
            web_root: match std::env::var("INFOCOMPANION_WEB_ROOT") {
                Ok(root) if !root.trim().is_empty() => PathBuf::from(root.trim()),
                _ => assets::default_root(),
            },
            daemon_host: env_or(&["INFOCOMPANION_DAEMON_HOST"], "127.0.0.1"),
            // The smoke test sets INFOCOMPANION_DAEMON_PORT to point a pane at nothing; the
            // daemon's own port is INFOCOMPANION_PORT. Both are honoured, so a test that says
            // "nothing is listening here" gets nothing to listen to.
            daemon_port: env_or_number(&["INFOCOMPANION_DAEMON_PORT", "INFOCOMPANION_PORT"], 2222),
            password: env_or(&["INFOCOMPANION_AUTH_PASSWORD"], "letsplay"),
            player_name,
            persona,
            games_dir: PathBuf::from(env_or(&["INFOCOMPANION_GAMES_DIR"], "/games")),
            state_dir,
            prompts_dir,
            voice,
            voice_url,
            voice_model,
            voice_rate,
            voice_speed,
            tuning,
            single: panes::build(&single, first_label, &names),
            multi: panes::build(&multi, &labels, &names),
        }
    }

    fn session(&self) -> Arc<pane::SessionSettings> {
        Arc::new(pane::SessionSettings {
            host: self.daemon_host.clone(),
            port: self.daemon_port,
            password: self.password.clone(),
        })
    }
}

/// A body with a type, and never cached.
///
/// The pages are small and a stale one is a mystery rather than a slowdown, so nothing is
/// cached: a reload always means the file on disk.
fn served(body: impl Into<Body>, content_type: &str) -> Response {
    let mut response = Response::new(body.into());
    let headers = response.headers_mut();
    headers.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_str(content_type).expect("a header"),
    );
    headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response
}

fn not_found() -> Response {
    (StatusCode::NOT_FOUND, "no such page\n").into_response()
}

/// Read a page or an asset, by whitelist.
async fn file(State(settings): State<Arc<Settings>>, uri: Uri) -> Response {
    let Some(relative) = assets::file_for(uri.path()) else {
        return not_found();
    };
    let Some(content_type) = assets::content_type(&relative) else {
        return not_found();
    };
    match std::fs::read(settings.web_root.join(&relative)) {
        Ok(body) => served(body, content_type),
        Err(_) => not_found(),
    }
}

/// What the panes are, told to the page rather than baked into it.
///
/// Seats are configuration, like everything else: an operator changes `INFOCOMPANION_PANE_MULTI`
/// and the pages follow, with no HTML to edit.
async fn config_js(State(settings): State<Arc<Settings>>) -> Response {
    served(
        panes::config_js(
            &settings.single,
            &settings.multi,
            &settings.daemon_host,
            settings.daemon_port,
            settings.voice.as_deref(),
        ),
        "text/javascript; charset=utf-8",
    )
}

async fn healthz() -> Response {
    served("ok\n", "text/plain; charset=utf-8")
}

/// The instance's own numbers, for anything that asks (09_stats.md).
///
/// Configuration and directory listings only: the two names this process was launched with, the
/// library, and the pointer the session keeps in its state directory. Nothing is fetched from
/// the session and nothing is parsed, and a missing folder is an absence rather than an error,
/// so this answers 200 whatever the instance happens to look like.
async fn stats(State(settings): State<Arc<Settings>>) -> Response {
    let body = stats::document(
        &settings.games_dir,
        &settings.state_dir,
        &settings.player_name,
        &settings.persona,
    );
    let mut response = served(body, "application/json");
    // Read-only, no credentials, and a browser on somebody else's page is one of the things
    // "anything that asks" means (09_stats.md §5 (`ST13`)).
    response.headers_mut().insert(
        header::ACCESS_CONTROL_ALLOW_ORIGIN,
        HeaderValue::from_static("*"),
    );
    response
}

/// The evening's own numbers, for anything that asks (07_webui.md §6 (`W26`–`W30`)).
///
/// A reader of `<state>/logs`, like `/stats.json` is a reader of the library: no session
/// contact, nothing cached, 200 whatever the logs happen to look like, and a missing log is a
/// smaller report rather than an error.
async fn evening(State(settings): State<Arc<Settings>>) -> Response {
    let body = report::document(&settings.state_dir);
    let mut response = served(body, "text/plain; charset=utf-8");
    response.headers_mut().insert(
        header::ACCESS_CONTROL_ALLOW_ORIGIN,
        HeaderValue::from_static("*"),
    );
    response
}

/// The games: what has been played here, and what has been written up about it (07_webui.md §6.1 (`W31`–`W35`)).
///
/// A reader of `<state>` like the two above, with one difference: the *generate* link beside each
/// game is a link into the session, and it is served from here rather than from the page because a
/// page has no route into a session except typing — which the bridge can do and the browser cannot.
async fn the_games(State(settings): State<Arc<Settings>>) -> Response {
    let rows = games::listing(&settings.state_dir);
    served(games::page(&rows), "text/html; charset=utf-8")
}

/// Everything a debugging session needs, as a zip (07_webui.md §6.2 (`W36`–`W43`)).
///
/// The answer to "can you send me the logs" — which, before this, was a person opening a shell,
/// finding the state directory, deciding which files mattered, and pulling them off by hand at the
/// end of an evening when nobody wants to. The files are the ones the instance has already written;
/// nothing here talks to the session and nothing is written back.
///
/// It is a plain `GET` behind a link on the games page rather than a button with a script, for the
/// same reason the other two are: a download is a download, and the browser already knows how to do
/// it. The filename carries the moment, because the first thing anybody does with two of these is
/// try to tell them apart.
async fn the_debug_bundle(State(settings): State<Arc<Settings>>) -> Response {
    let daemon = format!("{}:{}", settings.daemon_host, settings.daemon_port);
    let bundle = bundle::build(
        &settings.state_dir,
        &settings.prompts_dir,
        &settings.games_dir,
        &daemon,
    );
    let zip = bundle.zip();
    let name = format!("ic-debug-{}.zip", stamp_for_a_filename(SystemTime::now()));

    // The line that makes a bundle findable afterwards: somebody will ask when it was built, and
    // the answer is in the log rather than in the browser's download list.
    eprintln!("webui: INFO {}", bundle::describe(&bundle, &zip));

    let mut response = Response::new(Body::from(zip));
    let headers = response.headers_mut();
    headers.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("application/zip"),
    );
    headers.insert(
        header::CONTENT_DISPOSITION,
        HeaderValue::from_str(&format!("attachment; filename=\"{name}\""))
            .expect("a header value from a name this file just built"),
    );
    headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response
}

/// A stamp safe to put in a filename: no colons, because a browser on Windows will not keep them.
fn stamp_for_a_filename(when: SystemTime) -> String {
    let seconds = when
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_secs() as i64)
        .unwrap_or(0);
    let days = seconds.div_euclid(86_400);
    let clock = seconds.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    format!(
        "{year:04}{month:02}{day:02}-{:02}{:02}{:02}",
        clock / 3_600,
        (clock % 3_600) / 60,
        clock % 60
    )
}

/// The civil date for days since the epoch — the same arithmetic the games page and the bundle do,
/// because a date library for three call sites would be a dependency this program does not have.
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

/// The last write-up for a game, or a page saying there is not one yet.
async fn a_report(
    State(settings): State<Arc<Settings>>,
    AxumPath(stem): AxumPath<String>,
) -> Response {
    let stem = games::safe_stem(&stem);
    match games::read(
        &settings
            .state_dir
            .join("catchup")
            .join(format!("{stem}.html")),
    ) {
        Some(html) => served(html, "text/html; charset=utf-8"),
        None => served(
            games::plain(
                "No write-up yet",
                &format!(
                    "Nothing has been written up for <code>{}</code>. Press <em>generate</em> on \
                     <a href=\"/games\">the games page</a> and the companion will write one.",
                    stem.replace('<', "&lt;").replace('>', "&gt;")
                ),
            ),
            "text/html; charset=utf-8",
        ),
    }
}

/// Ask the companion to write a game up, by opening the pane that accepts input and typing it.
///
/// The same route as everything else a browser wants from a session, and deliberately not a control
/// frame: the daemon's commands are how a page talks to a session (07_webui.md §5 (`W18`–`W25`)), so this opens a pane
/// as `me`, writes one line, and closes. The work happens in the daemon and the companion; the
/// browser is told to come back in a minute, because a page that waited would be a page holding a
/// connection open for the length of a model call.
async fn write_up(
    State(settings): State<Arc<Settings>>,
    AxumPath(stem): AxumPath<String>,
) -> Response {
    let stem = games::safe_stem(&stem);
    let asked = pane::type_a_line(
        &settings.session(),
        "me",
        &format!("/catchup {stem}"),
        settings.tuning.type_line_settle,
    )
    .await;
    let message = match asked {
        Ok(()) => {
            // The marker, and the whole of the "writing up…" state (B9): a stamp in a file whose
            // *mtime* is what `/games` reads, so "asked, and nothing came of it yet" is a fact on
            // disk rather than something a page has to remember between two requests. Nothing
            // deletes it — the page compares it with the report's own `generated_at`, so a pass
            // that finishes clears the state by being newer than the thing that asked for it, and
            // one that dies leaves the evidence where a person can see it.
            let asked_for = settings.state_dir.join("catchup").join(format!("{stem}.requested"));
            let _ = std::fs::create_dir_all(settings.state_dir.join("catchup"));
            let _ = std::fs::write(&asked_for, "asked for from the games page\n");

            format!(
                "Asked the companion to write up <code>{stem}</code>. It reads everything it holds about \
                 the game and takes a minute or two, on its own model call — come back to \
                 <a href=\"/games\">the games</a> and press <em>view</em> when it is done."
            )
        }
        Err(err) => format!(
            "The session could not be reached, so nothing was asked for: {}. Nothing was written up.",
            err.to_string().replace('<', "&lt;").replace('>', "&gt;")
        ),
    };

    served(
        games::plain("Write-up asked for", &message),
        "text/html; charset=utf-8",
    )
}

/// One pane, as a browser asks for it.
async fn ws(
    State(settings): State<Arc<Settings>>,
    Query(query): Query<HashMap<String, String>>,
    upgrade: WebSocketUpgrade,
) -> Response {
    let seat = query
        .get("seat")
        .map(|seat| seat.trim().to_string())
        .unwrap_or_default();
    if seat.is_empty() {
        return (StatusCode::BAD_REQUEST, "a pane needs a seat\n").into_response();
    }

    let clamp = |name: &str| {
        query
            .get(name)
            .and_then(|value| value.trim().parse::<u32>().ok())
    };
    let cols = settings.tuning.cols.resolve(clamp("cols"));
    let rows = settings.tuning.rows.resolve(clamp("rows"));

    let session = settings.session();
    upgrade.on_upgrade(move |socket| pane::run(socket, seat, cols, rows, session))
}

/// The records, as a stream a page can read.
///
/// A function of its own rather than a closure inside the handler so that the one bound that
/// matters here is written down where it can be read: a listener is served on another task, so the
/// stream has to be `Send`, and a bound inferred somewhere inside an `async` block is a bound
/// nobody can point at when it fails.
fn the_records(
    listening: broadcast::Receiver<String>,
) -> impl futures_util::Stream<Item = Result<Event, std::convert::Infallible>> + Send + 'static {
    futures_util::stream::unfold(listening, |mut listening| async move {
        loop {
            match listening.recv().await {
                Ok(text) => return Some((Ok(Event::default().data(text)), listening)),
                // A page that has fallen behind loses words rather than holding the seat back: this
                // is a queue in front of a speaker, and the words it loses are on the screen in
                // front of it (`V16`). Said out loud, because a gap in a voice is the kind of thing
                // nobody can account for afterwards.
                Err(broadcast::error::RecvError::Lagged(missed)) => {
                    eprintln!("webui: WARNING a voice listener fell behind by {missed} record(s)");
                }
                Err(broadcast::error::RecvError::Closed) => return None,
            }
        }
    })
}

/// The voice: the companion's speech, as a stream the page reads (08_webui_tts.md).
///
/// A stream rather than a pane because the page is not drawing anything: what arrives here is the
/// words to say, framed one utterance to a record by the daemon. No voice configured is no route —
/// the page is not offered a switch it could do nothing with (`V15`).
///
/// Nobody listening is no seat (`V13`): the relay behind this opens the daemon's voice seat on the
/// first listener and releases it when the last one goes, so an evening nobody is listening to
/// costs the session nothing at all.
async fn the_voice(State(settings): State<Arc<Settings>>) -> Response {
    if settings.voice.is_none() {
        return not_found();
    }
    Sse::new(the_records(voice::listen(settings.session(), "voice")))
        .keep_alive(KeepAlive::default())
        .into_response()
}

/// The words of one utterance, as the page asks for them.
#[derive(serde::Deserialize)]
struct Asked {
    text: String,
}

/// One utterance, spoken: the page's request forwarded to the deployment's endpoint and the
/// endpoint's answer handed back as it is made (§7 of `08_webui_tts.md`).
///
/// **No endpoint and no name is no route**, the same rule the seat follows: the page falls back to
/// the browser's own voice rather than being offered a switch that cannot speak (`V15`, `V18`).
/// The status is what the page reads — anything but a `200` means "speak this one yourself" — so a
/// failure here is a fallback rather than a silence, which is the whole point of asking.
///
/// A page that stops wanting an utterance closes the connection, and the stream it was reading is
/// dropped: the endpoint notices, and the work stops. That is the only cancellation there is
/// (`V13`), and it costs nothing to implement because it is what HTTP already does.
async fn the_voice_says(
    State(settings): State<Arc<Settings>>,
    Json(asked): Json<Asked>,
) -> Response {
    let (Some(endpoint), Some(named)) = (settings.voice_url.as_deref(), settings.voice.as_deref())
    else {
        return not_found();
    };
    if asked.text.trim().is_empty() {
        return (StatusCode::BAD_REQUEST, "nothing to say").into_response();
    }
    let ask = synth::Ask {
        endpoint,
        model: &settings.voice_model,
        voice: named,
        rate: settings.voice_rate,
        speed: settings.voice_speed,
        text: &asked.text,
    };
    match synth::say(ask).await {
        Ok(speaking) => speaking,
        Err(err) => {
            // Logged with the endpoint's own words in it, because "the voice went quiet" has one
            // explanation on this side and it is always the same one: it was not asked properly.
            eprintln!("webui: WARNING the voice endpoint did not speak: {err}");
            (StatusCode::BAD_GATEWAY, "the voice endpoint did not speak").into_response()
        }
    }
}

#[tokio::main]
async fn main() -> std::process::ExitCode {
    let settings = Arc::new(Settings::from_env());

    let address = format!("{}:{}", settings.web_bind, settings.web_port);
    let listener = match tokio::net::TcpListener::bind(&address).await {
        Ok(listener) => listener,
        Err(err) => {
            eprintln!("webui: ERROR cannot listen on {address} — {err}");
            return std::process::ExitCode::FAILURE;
        }
    };
    let bound = listener
        .local_addr()
        .unwrap_or_else(|_| SocketAddr::from(([0, 0, 0, 0], 0)));

    let app = Router::new()
        .route("/", get(file))
        .route("/index.html", get(file))
        .route("/term", get(file))
        .route("/multiterm", get(file))
        .route("/games", get(the_games))
        // axum 0.7 spells a parameter `:name`; `{name}` is 0.8's spelling and would be a literal
        // path here, which is the sort of 404 that looks like a missing feature.
        .route("/catchup/:stem", get(a_report))
        .route("/catchup/:stem/generate", get(write_up))
        .route("/config.js", get(config_js))
        .route("/debug/bundle", get(the_debug_bundle))
        .route("/healthz", get(healthz))
        .route("/stats.json", get(stats))
        .route("/evening.txt", get(evening))
        .route("/voice", get(the_voice))
        .route("/voice/say", post(the_voice_says))
        .route("/ws", get(ws))
        .fallback(get(file))
        .with_state(Arc::clone(&settings));

    let seats: Vec<&str> = settings
        .single
        .iter()
        .chain(settings.multi.iter())
        .map(|p| p.seat.as_str())
        .collect();
    eprintln!(
        "webui: INFO web interface on http://{}:{} (panes: {})",
        settings.web_bind,
        bound.port(),
        seats.join(", ")
    );
    eprintln!("webui: INFO no authentication: whoever can reach this port is at the keyboard");
    eprintln!(
        "webui: INFO talking to the session at {}:{}",
        settings.daemon_host, settings.daemon_port
    );
    eprintln!("webui: INFO pages from {}", settings.web_root.display());

    let served = axum::serve(listener, app).with_graceful_shutdown(async {
        let _ = tokio::signal::ctrl_c().await;
    });

    match served.await {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(err) => {
            eprintln!("webui: ERROR {err}");
            std::process::ExitCode::FAILURE
        }
    }
}
