//! The voice endpoint: one request, and the sound read back while it is still being made.
//!
//! INFOCompanion does not synthesise speech and owns none of the machinery that could
//! (`08_webui_tts.md` §7): the whole of the voice is a seat, a name, an address and a
//! fallback. The address answers **one shape** — the speech endpoint OpenAI defined and
//! everything else has copied, because a shape other people already implement is a shape a
//! deployment can point at without us writing anything.
//!
//! What this module adds to that shape is one requirement of our own: the answer must be
//! **readable as it is made**. A voice that arrives whole is a voice that arrives late, and the
//! request therefore asks for raw samples rather than a file. Nothing here is an implementation
//! of a voice: it is the client half of an interface, which is the only half the project owns.

use std::sync::OnceLock;
use std::time::Duration;

use anyhow::{anyhow, Context, Result};
use axum::body::Body;
use axum::http::{header, HeaderValue};
use axum::response::{IntoResponse, Response};

/// How long the endpoint has to acknowledge a request.
///
/// Not a budget for synthesising: it is the point at which "no answer" has to become an answer
/// rather than a page waiting for ever. The stream itself is not on a clock — a sentence takes as
/// long as it takes, and a client that has stopped listening closes the connection instead.
const ACKNOWLEDGE: Duration = Duration::from_secs(10);

/// The HTTP client, made once so an evening's lines share one connection pool.
fn client() -> &'static reqwest::Client {
    static CLIENT: OnceLock<reqwest::Client> = OnceLock::new();
    CLIENT.get_or_init(|| {
        reqwest::Client::builder()
            .connect_timeout(ACKNOWLEDGE)
            .build()
            .expect("a client with no options to refuse")
    })
}

/// One utterance to ask for.
pub struct Ask<'a> {
    /// The endpoint's base, without the route: `/v1` for the shape being spoken.
    pub endpoint: &'a str,
    /// What the endpoint calls the model. The shape requires a value; this is the one our own
    /// reference endpoint answers to, and a deployment pointing at something else sets its own.
    pub model: &'a str,
    pub voice: &'a str,
    /// The rate the samples come back at. The stream carries no header of its own, so it is
    /// configured rather than guessed — and it is passed on to the page, which is the only place
    /// that can act on it.
    pub rate: u32,
    /// How fast to say it, as a multiplier on whatever the endpoint calls normal. **1.0 is "no
    /// opinion"**, and the field is left out of the request in that case, so an endpoint sees the
    /// documented shape unless the deployment asked for a speed (`08_webui_tts.md` §7).
    pub speed: f64,
    pub text: &'a str,
}

/// The shortest run worth a request of its own.
///
/// A couple of words is not — a request has a fixed price at the service, and a seam in the prosody
/// where the parts join — but a sentence is, and this is deliberately small: the whole point of the
/// split is that the **first** part is made quickly, so a floor that swallowed the first sentence into
/// the second would give back the wait it exists to remove.
const WORTH_ASKING: usize = 20;

/// The longest a part may be, punctuation or no punctuation.
///
/// A sentence with no full stop in it is not a reason for a page to wait eight seconds for the first
/// sound, so a part that has grown this large is broken at the last space before the ceiling.
const MOST: usize = 400;

/// A line, with what it takes to ask for it.
struct Plan {
    endpoint: String,
    model: String,
    voice: String,
    rate: u32,
    speed: f64,
    parts: Vec<String>,
}

impl Plan {
    fn new(ask: Ask<'_>) -> Self {
        Self {
            endpoint: ask.endpoint.trim_end_matches('/').to_string(),
            model: ask.model.to_string(),
            voice: ask.voice.to_string(),
            rate: ask.rate,
            speed: ask.speed,
            parts: parts_of(ask.text),
        }
    }

    /// Ask the service for one part of the line.
    async fn ask(&self, at: usize) -> Result<reqwest::Response> {
        let url = format!("{}/audio/speech", self.endpoint);
        let mut body = serde_json::json!({
            "model": self.model,
            "voice": self.voice,
            "input": self.parts[at],
            // Raw samples rather than a file, and streaming asked for by name rather than left to
            // whatever the shape's default happens to be (`V28`).
            "response_format": "pcm",
            "stream": true,
        });
        // **Only when the deployment has an opinion.** The four fields above are the shape an
        // endpoint has to answer; a rate is a fact about a voice rather than about this project, so
        // a fifth field is something a deployment asks for (`08_webui_tts.md` §7).
        if (self.speed - 1.0).abs() > f64::EPSILON {
            body["speed"] = serde_json::json!(self.speed);
        }
        let answer = tokio::time::timeout(ACKNOWLEDGE, client().post(&url).json(&body).send())
            .await
            .map_err(|_| {
                anyhow!("the voice endpoint at {url} did not answer within {ACKNOWLEDGE:?}")
            })?
            .with_context(|| format!("cannot reach the voice endpoint at {url}"))?;
        let status = answer.status();
        if !status.is_success() {
            // The endpoint's own words rather than a summary of them: it is the only account of what
            // it wanted, and the log line is where somebody reads it.
            let said = answer.text().await.unwrap_or_default();
            return Err(anyhow!(
                "the voice endpoint answered {status}: {}",
                said.trim()
            ));
        }
        Ok(answer)
    }
}

/// Ask for one utterance and hand back the answer as it arrives.
///
/// **A line is asked for in the parts it is said in**, and the answers are handed on in turn. The
/// service this project is written against makes a whole request before it writes any of it —
/// measured here at 4.7 s of silence for a three-sentence line against 2.5 s for its first sentence
/// alone — so the split is ours, and it is the difference between the first sound arriving when the
/// first sentence has been made and arriving when the last one has (`V29`).
///
/// Everything before the first byte is a status the page can act on; everything after it is a stream
/// that can only stop. So the **first** part is asked for before the response exists, and a service
/// that dies part-way through a line leaves a line that stops early rather than one said twice
/// (`V28`, `V31.1`).
pub async fn say(ask: Ask<'_>) -> Result<Response> {
    let plan = Plan::new(ask);
    if plan.parts.is_empty() {
        return Err(anyhow!("nothing to say"));
    }
    let opening = plan.ask(0).await?;
    let rate = plan.rate;

    // The rest are asked for as the earlier ones finish and handed on as they arrive. A channel
    // rather than a stream of streams, because this is a sequence of whole responses: the receiver
    // going away is the page closing the connection, which fails the next send and ends the task —
    // the only cancellation this needs, and the one HTTP already gives us (`V30`).
    let (handing, handed) =
        tokio::sync::mpsc::channel::<Result<axum::body::Bytes, std::io::Error>>(8);
    tokio::spawn(async move {
        let mut body = opening.bytes_stream();
        let mut at = 0usize;
        loop {
            while let Some(chunk) = body.next().await {
                let going = chunk.map_err(|err| std::io::Error::other(err.to_string()));
                let broke = going.is_err();
                // A send that fails is the page gone: nothing here is worth making a sound for.
                if handing.send(going).await.is_err() || broke {
                    return;
                }
            }
            at += 1;
            if at >= plan.parts.len() {
                return;
            }
            match plan.ask(at).await {
                Ok(next) => body = next.bytes_stream(),
                Err(err) => {
                    // Nothing polite is left to say: the page has already heard the start of this
                    // line, and a line that stops early is not a line to say again (`V31.1`). Said
                    // out loud because a service that keeps doing this is a deployment to fix.
                    eprintln!(
                        "webui: WARNING the voice endpoint stopped part-way through a line: {err}"
                    );
                    return;
                }
            }
        }
    });

    use futures_util::StreamExt;
    let samples = futures_util::stream::unfold(handed, |mut handed| async move {
        handed.recv().await.map(|item| (item, handed))
    });

    let mut response = Body::from_stream(samples).into_response();
    let headers = response.headers_mut();
    headers.insert(header::CONTENT_TYPE, HeaderValue::from_static("audio/L16"));
    // Said out loud because raw samples cannot say it themselves.
    headers.insert(
        "x-sample-rate",
        HeaderValue::from_str(&rate.to_string()).expect("a number is a header value"),
    );
    // Nothing here is worth keeping: it is one utterance, made once, for one page.
    headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    Ok(response)
}

/// A line, in the parts it is asked for.
///
/// The cuts are the ends of sentences, and every part is a **slice of the original text**: nothing is
/// re-spaced, re-joined at a different place or dropped, so the words the companion said are the
/// words that are spoken. A part too short to be worth its own request is carried into the next one,
/// and a part that has grown past `MOST` is broken at a space rather than left to run.
fn parts_of(text: &str) -> Vec<String> {
    let mut cuts: Vec<usize> = Vec::new();
    for (at, ch) in text.char_indices() {
        if !matches!(ch, '.' | '!' | '?' | '…') {
            continue;
        }
        let after = at + ch.len_utf8();
        // A cut falls after the punctuation and the space that follows it, so the next part starts at
        // a word rather than at a gap.
        if after == text.len() || text[after..].starts_with(char::is_whitespace) {
            cuts.push(after);
        }
    }
    if cuts.last() != Some(&text.len()) {
        cuts.push(text.len());
    }

    let mut parts: Vec<String> = Vec::new();
    let mut start = 0;
    for cut in cuts {
        if text[start..cut].trim().len() < WORTH_ASKING {
            // Carried forward rather than asked for alone: a word or two is not worth a request.
            continue;
        }
        taking(&mut parts, text, start, cut);
        start = cut;
    }
    if start < text.len() {
        taking(&mut parts, text, start, text.len());
    }
    parts
}

/// Push a slice of the line as one or more parts, breaking it at the last space before it grows past
/// `MOST`.
fn taking(parts: &mut Vec<String>, text: &str, from: usize, to: usize) {
    let mut at = from;
    while to - at > MOST {
        let Some(step) = last_space_before(&text[at..to], MOST) else {
            break; // one long word: there is nothing in it to break on
        };
        let piece = text[at..at + step].trim();
        if !piece.is_empty() {
            parts.push(piece.to_string());
        }
        at += step;
    }
    let piece = text[at..to].trim();
    if !piece.is_empty() {
        parts.push(piece.to_string());
    }
}

/// The offset of the last space at or before `limit`, without splitting a character in half.
fn last_space_before(text: &str, limit: usize) -> Option<usize> {
    let mut boundary = limit.min(text.len());
    while boundary > 0 && !text.is_char_boundary(boundary) {
        boundary -= 1;
    }
    text[..boundary].rfind(char::is_whitespace)
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::sync::{mpsc, oneshot};

    /// A stand-in endpoint: a socket that answers the shape by hand
    /// (`08_webui_tts.md` §7 `V28`), so what is under test is **our half of the interface** rather
    /// than somebody's model. No implementation is needed to test a contract, which is the point of
    /// there being one.
    ///
    /// The body is written in chunks with a **gate** between the first and the rest, so that a
    /// bridge which waited for the whole thing before handing anything on would deadlock here
    /// rather than pass quietly.
    async fn an_endpoint(
        status: &'static str,
        chunks: Vec<&'static [u8]>,
        gate: Option<oneshot::Receiver<()>>,
    ) -> (String, mpsc::Receiver<String>) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .expect("a port to listen on");
        let base = format!("http://{}/v1", listener.local_addr().expect("an address"));
        let (seen, seen_by_the_test) = mpsc::channel(1);

        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.expect("a connection");
            // The head, then exactly as much body as the head declared.
            let mut head = Vec::new();
            let mut one = [0u8; 1];
            while !head.ends_with(b"\r\n\r\n") {
                socket.read_exact(&mut one).await.expect("a request");
                head.push(one[0]);
            }
            let text = String::from_utf8_lossy(&head).to_string();
            let declared: usize = text
                .lines()
                .find_map(|line| {
                    let (key, value) = line.split_once(':')?;
                    if key.eq_ignore_ascii_case("content-length") {
                        value.trim().parse().ok()
                    } else {
                        None
                    }
                })
                .unwrap_or(0);
            let mut asked = vec![0u8; declared];
            socket
                .read_exact(&mut asked)
                .await
                .expect("the body it was asked with");
            let _ = seen
                .send(format!("{text}{}", String::from_utf8_lossy(&asked)))
                .await;

            let length: usize = chunks.iter().map(|chunk| chunk.len()).sum();
            socket
                .write_all(
                    format!(
                        "HTTP/1.1 {status}\r\ncontent-type: audio/L16\r\ncontent-length: {length}\r\n\r\n"
                    )
                    .as_bytes(),
                )
                .await
                .expect("a response head");
            let mut gate = gate;
            for (at, chunk) in chunks.iter().enumerate() {
                if at == 1 {
                    // Taken rather than matched, because the loop is the second place it is looked
                    // at and a receiver cannot be moved twice.
                    if let Some(gate) = gate.take() {
                        let _ = gate.await;
                    }
                }
                socket.write_all(chunk).await.expect("a chunk of samples");
                socket.flush().await.expect("flushed");
            }
        });

        (base, seen_by_the_test)
    }

    /// The request is the shape a deployment's endpoint already answers, and nothing else: no
    /// session, no identifier, and the format that makes the answer streamable.
    #[tokio::test]
    async fn the_request_is_the_shape_an_endpoint_already_answers() {
        let (base, mut asked) = an_endpoint("200 OK", vec![b"\x01\x02"], None).await;
        let _ = say(Ask {
            endpoint: &base,
            model: "kokoro",
            voice: "bm_fable",
            rate: 24000,
            speed: 1.0,
            text: "The grate looks loose.",
        })
        .await
        .expect("an answer");

        let seen = asked.recv().await.expect("the request");
        assert!(seen.starts_with("POST /v1/audio/speech "), "{seen}");
        assert!(seen.contains("\"response_format\":\"pcm\""), "{seen}");
        assert!(seen.contains("\"voice\":\"bm_fable\""), "{seen}");
        assert!(
            seen.contains("\"input\":\"The grate looks loose.\""),
            "{seen}"
        );
        assert!(seen.contains("\"model\":\"kokoro\""), "{seen}");
        assert!(
            !seen.contains("\"speed\""),
            "and nothing about the rate, because this deployment has no opinion: {seen}"
        );
    }

    /// A deployment that *does* have an opinion sends one more field and nothing else moves: the
    /// rate is a fact about a voice, not about this project (`08_webui_tts.md` §7).
    #[tokio::test]
    async fn a_deployment_with_an_opinion_sends_the_speed_it_chose() {
        let (base, mut asked) = an_endpoint("200 OK", vec![b"\x01\x02"], None).await;
        let _ = say(Ask {
            endpoint: &base,
            model: "kokoro",
            voice: "bm_fable",
            rate: 24000,
            speed: 1.3,
            text: "The grate looks loose.",
        })
        .await
        .expect("an answer");

        let seen = asked.recv().await.expect("the request");
        assert!(seen.contains("\"speed\":1.3"), "{seen}");
    }

    /// The samples come back untouched, the rate is published so the page can play them, and the
    /// first chunk is readable **before the endpoint has finished writing** — which is the whole of
    /// `V7`, and the difference between a voice and a wait.
    #[tokio::test]
    async fn the_samples_are_relayed_while_they_are_still_being_made() {
        use futures_util::StreamExt;
        let (go, gated) = oneshot::channel();
        let (base, _) = an_endpoint(
            "200 OK",
            vec![b"\x01\x02\x03\x04", b"\x05\x06"],
            Some(gated),
        )
        .await;

        let answer = say(Ask {
            endpoint: &base,
            model: "kokoro",
            voice: "bm_fable",
            rate: 22050,
            speed: 1.0,
            text: "worth a try",
        })
        .await
        .expect("an answer");

        assert_eq!(
            answer
                .headers()
                .get("content-type")
                .and_then(|value| value.to_str().ok()),
            Some("audio/L16")
        );
        assert_eq!(
            answer
                .headers()
                .get("x-sample-rate")
                .and_then(|value| value.to_str().ok()),
            Some("22050"),
            "the rate is published, because a stream of samples cannot say it itself"
        );

        let mut samples = answer.into_body().into_data_stream();
        let first = samples.next().await.expect("a first chunk").expect("bytes");
        assert_eq!(first.as_ref(), b"\x01\x02\x03\x04");
        // The endpoint is still holding the rest: if the bridge had waited for the whole body, this
        // would be where the test stopped rather than where it carried on.
        let _ = go.send(());
        let second = samples
            .next()
            .await
            .expect("a second chunk")
            .expect("bytes");
        assert_eq!(second.as_ref(), b"\x05\x06");
    }

    /// A failure is an error a caller can act on — the page falls back to the browser's voice —
    /// and it carries the endpoint's own words, because those are the only account of what it
    /// wanted that anybody is going to get (`V6`, `V12`).
    #[tokio::test]
    async fn an_endpoint_that_will_not_speak_says_so_and_is_not_swallowed() {
        let (base, _) =
            an_endpoint("503 Service Unavailable", vec![b"no voice loaded\n"], None).await;
        let err = say(Ask {
            endpoint: &base,
            model: "kokoro",
            voice: "bm_fable",
            rate: 24000,
            speed: 1.0,
            text: "the grate looks loose",
        })
        .await
        .expect_err("a refusal");

        let said = err.to_string();
        assert!(said.contains("503"), "{said}");
        assert!(said.contains("no voice loaded"), "{said}");
    }

    /// A line is asked for a sentence at a time, because the service makes a whole request before it
    /// writes any of it (`V29`) — this is the difference between the first sound arriving at two and a
    /// half seconds and at four and a half.
    #[test]
    fn a_line_is_asked_for_a_sentence_at_a_time() {
        let line = "The grate looks loose. You could try the handle, though it is painted shut. \
                    Somewhere above you a floorboard shifts.";
        let parts = parts_of(line);
        assert_eq!(parts.len(), 3, "{parts:?}");
    }

    /// Every part is a slice of what the companion said: nothing is dropped, re-spaced or reordered,
    /// which is the one thing a split like this can get wrong in silence.
    #[test]
    fn nothing_the_companion_said_is_lost_or_moved() {
        let line = "One thing, said at length — and then another, said at greater length still, \
                    because a companion does go on. And a third, which is shorter.";
        let parts = parts_of(line);
        let tidy = |text: &str| text.split_whitespace().collect::<Vec<_>>().join(" ");
        assert_eq!(tidy(&parts.join(" ")), tidy(line), "{parts:?}");
    }

    /// A fragment is carried into the next part rather than paying a request of its own.
    #[test]
    fn a_fragment_is_carried_forward() {
        let parts = parts_of(
            "Yes. No. Perhaps, if the weather holds and the door is open, we shall see what the \
             others make of it.",
        );
        assert_eq!(parts.len(), 1, "{parts:?}");
    }

    /// A sentence with no full stop in it must not become one long wait.
    #[test]
    fn a_part_with_no_punctuation_is_broken_at_a_space() {
        let line = "word ".repeat(200);
        let parts = parts_of(line.trim());
        assert!(parts.len() > 1, "a thousand characters is one long wait");
        assert!(
            parts.iter().all(|part| part.len() <= MOST),
            "{:?}",
            parts.iter().map(String::len).collect::<Vec<_>>()
        );
        // And the breaking is at spaces, not in the middle of a word.
        assert!(parts.iter().all(|part| part.ends_with("word")), "{parts:?}");
    }

    #[test]
    fn a_line_with_nothing_in_it_is_no_parts_at_all() {
        assert!(parts_of("   ").is_empty());
        assert!(parts_of("").is_empty());
    }
}
