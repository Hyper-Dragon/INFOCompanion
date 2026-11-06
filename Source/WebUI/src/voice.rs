//! The voice: one seat, held for the whole bridge and handed to whoever is listening.
//!
//! The daemon says one framed record per utterance on a seat of its own (08_webui_tts.md §2). This
//! holds that seat **once**, for every listener rather than one each: two pages open on the same
//! evening is ordinary, and two of them speaking at once is the failure the design is arranged
//! around (`V13`).
//!
//! It also never stops reading. The voice seat is a counted pane — the session holds its lines for
//! it, the same as any other — so a relay that waited for a speaker, or for a listener, would turn
//! a slow voice into a slow game (`V11`, `V12`). Nothing here blocks on the far end: a listener
//! that has fallen behind loses records, and the records it loses are words that are on the screen
//! in front of it (`V16`).

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock};

use anyhow::{anyhow, Result};
use tokio::sync::broadcast;

use crate::pane::{self, SessionSettings};

/// The wire this relay knows.
///
/// A record that names any other version is reported rather than guessed at: the field exists so
/// that the next shape of the record can be told from this one, which is worth nothing if the
/// reader treats an unknown shape as this one.
const WIRE: u64 = 1;

/// How many records may be waiting for one listener before the oldest are dropped.
const WAITING: usize = 32;

/// The one seat, and the listeners it is handed to.
struct Voice {
    records: broadcast::Sender<String>,
    /// Whether the seat is being held right now. Swapped rather than read and then written, so two
    /// pages opening at the same moment cannot both start a relay.
    holding: AtomicBool,
}

/// This process's single voice, made on first use.
static VOICE: OnceLock<Arc<Voice>> = OnceLock::new();

/// Subscribe to the voice, starting the seat if this is the first listener.
pub fn listen(settings: Arc<SessionSettings>, seat: &str) -> broadcast::Receiver<String> {
    let voice = VOICE.get_or_init(|| {
        // The receiver that comes back from `channel` is dropped on purpose: a sender with no
        // receivers is what tells the relay that the last listener has gone (`V13`).
        let (records, _) = broadcast::channel(WAITING);
        Arc::new(Voice {
            records,
            holding: AtomicBool::new(false),
        })
    });
    let listening = voice.records.subscribe();
    if !voice.holding.swap(true, Ordering::SeqCst) {
        let voice = Arc::clone(voice);
        let seat = seat.to_string();
        tokio::spawn(async move { voice.hold_the_seat(settings, seat).await });
    }
    listening
}

impl Voice {
    /// Hold the daemon's voice seat and hand its records on, until nobody is listening.
    async fn hold_the_seat(&self, settings: Arc<SessionSettings>, seat: String) {
        let mut relayed: u64 = 0;
        match pane::hold(&seat, &settings).await {
            Ok(mut held) => {
                eprintln!("webui: INFO voice seat held as `{seat}`");
                while let Some(line) = held.next_line().await {
                    match record_of(&line) {
                        Ok(Some(text)) => {
                            // The last listener leaving is what ends this: a broadcast with no
                            // receivers is the one honest way to know, and it is also the moment
                            // the session stops waiting on a voice nobody can hear.
                            if self.records.send(text).is_err() {
                                break;
                            }
                            relayed += 1;
                        }
                        Ok(None) => {}
                        Err(why) => eprintln!("webui: WARNING {why}"),
                    }
                }
                held.close().await;
            }
            Err(err) => eprintln!("webui: WARNING cannot hold the voice seat `{seat}`: {err}"),
        }
        self.holding.store(false, Ordering::SeqCst);
        eprintln!("webui: INFO voice seat released after {relayed} record(s)");
    }
}

/// The words in one record, or `None` for a line that carries none.
fn record_of(line: &[u8]) -> Result<Option<String>> {
    let line = std::str::from_utf8(line)
        .map_err(|_| anyhow!("the voice seat wrote bytes that are not utf-8"))?;
    // An empty line is the transport keeping the connection alive, not a record.
    if line.trim().is_empty() {
        return Ok(None);
    }
    let record: serde_json::Value = serde_json::from_str(line)
        .map_err(|err| anyhow!("the voice seat wrote a line that is not a record: {err}"))?;
    if record.get("v").and_then(|v| v.as_u64()) != Some(WIRE) {
        return Err(anyhow!(
            "the voice seat wrote a record this relay does not know (it speaks version {WIRE})"
        ));
    }
    Ok(record
        .get("text")
        .and_then(|text| text.as_str())
        .map(str::to_string))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_record_is_read_for_its_words() {
        let line = br#"{"v":1,"seq":3,"text":"the grate looks loose"}"#;
        assert_eq!(
            record_of(line).expect("a record").as_deref(),
            Some("the grate looks loose")
        );
    }

    /// The prose's own newlines and quotes arrive escaped inside the frame and come back out the
    /// way they went in. This is what makes the frame a boundary rather than a guess.
    #[test]
    fn the_frame_carries_anything_the_companion_says() {
        let said = "the grate looks loose\n\"worth a try\", you think";
        let line = serde_json::json!({ "v": 1, "seq": 1, "text": said }).to_string();
        assert_eq!(
            record_of(line.as_bytes()).expect("a record").as_deref(),
            Some(said)
        );
    }

    #[test]
    fn a_version_this_relay_does_not_know_is_said_rather_than_guessed() {
        let err = record_of(br#"{"v":2,"seq":1,"text":"hello"}"#).expect_err("refused");
        assert!(err.to_string().contains("does not know"), "{err}");
    }

    #[test]
    fn a_line_that_is_not_a_record_is_refused() {
        assert!(record_of(b"the grate looks loose").is_err());
    }

    #[test]
    fn an_empty_line_is_not_a_record() {
        assert!(matches!(record_of(b""), Ok(None)));
        assert!(matches!(record_of(b"\r"), Ok(None)));
    }
}
