//! Turn framing (issue #4).
//!
//! A **turn** is `input + all frotz output until the game is ready for input
//! again` (§7). The core frames turns because all input flows through the core,
//! and the agent needs to know when a turn has finished before deciding its next
//! move.
//!
//! Two mechanisms, in order of precedence:
//!
//! 1. **Prompt heuristic** — a complete line consisting of `>` alone ends the turn.
//!    Verified against `dfrotz` 2.54: the dumb interface always prints `>` on its
//!    own line when it is waiting for input, and a *sub-prompt* (frotz asking for
//!    a save filename) is printed as `>Please enter a filename […]: ` on one line,
//!    so it does not match and correctly does not end the turn.
//! 2. **Output quiescence** — no frotz bytes for `--turn-timeout-ms` (default
//!    400 ms). The fallback for anything the prompt heuristic misses.
//!
//! The framer is a pure state machine: it takes bytes and timestamps and returns
//! turns. No I/O, no async — which is what makes it unit-testable against the
//! exact byte streams captured from `dfrotz`.

use std::time::{Duration, Instant};

/// How long a bare `>` is given to turn out to be the start of a longer line.
///
/// frotz's prompt is not newline-terminated, so a buffer ending in `>` is usually the
/// prompt — but frotz also prints `>Please enter a filename […]: ` as one line.
/// Waiting this long before calling it a prompt keeps both cases correct, and is
/// still eighty times faster than falling back to quiescence.
///
/// The value in force is `prompt_settle_ms` in `core-config.json`; this is the shipped default
/// and what a framer built by `new` — which is what the tests build — runs on.
pub const PROMPT_SETTLE_MS: u64 = crate::config::DEFAULT_PROMPT_SETTLE_MS;

/// Why a turn was considered finished.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EndReason {
    /// frotz printed its `>` prompt: the reliable signal.
    Prompt,
    /// No output for the quiescence window: the fallback signal.
    Quiescence,
    /// The interpreter said so itself, on the tool channel (`input_wanted`).
    ///
    /// This is the only signal that is not an inference: it is emitted after everything the
    /// story printed has been written and flushed, so the turn is complete by construction
    /// rather than by having gone quiet. It is what replaces both of the heuristics above
    /// when the interpreter is ours (02_game_boundary.md §3 (`G9`–`G15`)).
    Interpreter,
}

/// A turn as produced by the framer (still in bytes; the core decides how to
/// decode it).
#[derive(Debug, Clone)]
pub struct FramedTurn {
    /// Input line(s) that opened the turn; empty for unsolicited output.
    pub input: String,
    /// Did the core write a line for this turn, **even an empty one**?
    ///
    /// `input` alone cannot say, and the two cases are not the same event. An empty `input`
    /// is either "nobody asked for this" — frotz's startup text, or the interpreter
    /// confirming a restore — or "somebody pressed Return", which is how `press_key` answers
    /// a prompt that is not asking for a command. One of those is machinery and the other is
    /// a person playing.
    pub input_written: bool,
    /// Raw output bytes, prompt line removed, outer blank lines trimmed.
    pub output: Vec<u8>,
    /// Wall time from the turn opening to it being framed.
    pub elapsed_ms: u64,
    /// How the turn was recognised as finished.
    pub ended_by: EndReason,
}

impl FramedTurn {
    /// Lossy UTF-8 view of the output, for the protocol and the transcript.
    pub fn output_text(&self) -> String {
        String::from_utf8_lossy(&self.output).into_owned()
    }
}

/// The framing state machine.
#[derive(Debug)]
pub struct TurnFramer {
    timeout: Duration,
    /// How long a bare `>` at the end of the buffer is given to prove it is the prompt.
    settle: Duration,
    /// Output accumulated for the turn in progress.
    buf: Vec<u8>,
    /// Input that opened the turn in progress; `None` until one is written.
    input: Option<String>,
    /// When the turn in progress opened.
    opened_at: Instant,
    /// When the last output byte arrived.
    last_output_at: Instant,
    /// When a bare `>` appeared at the end of the buffer, if it did.
    prompt_candidate: Option<Instant>,
    /// Is a turn in progress?
    open: bool,
}

impl TurnFramer {
    /// A framer with the shipped windows.
    pub fn new(timeout: Duration) -> Self {
        let now = Instant::now();
        Self {
            timeout,
            settle: Duration::from_millis(PROMPT_SETTLE_MS),
            buf: Vec::new(),
            input: None,
            opened_at: now,
            last_output_at: now,
            prompt_candidate: None,
            open: false,
        }
    }

    /// The same framer with the `>` settle taken from configuration instead of the default
    /// (03_session.md §9–§10 (`S58`–`S76`)).
    pub fn settled(mut self, settle: Duration) -> Self {
        self.settle = settle;
        self
    }

    /// Is a turn currently in progress?
    pub fn is_open(&self) -> bool {
        self.open
    }

    /// Bytes accumulated for the turn in progress.
    pub fn buffered_len(&self) -> usize {
        self.buf.len()
    }

    /// Record that the core just wrote `line` into frotz.
    ///
    /// If no turn is open this opens one. The core's line-level gate means a
    /// second input should never arrive mid-turn; if one does (watchdog, recovery)
    /// it is appended to the turn's input rather than silently dropped, so the
    /// transcript stays honest about what frotz actually received.
    pub fn note_input(&mut self, line: &str, now: Instant) {
        if !self.open {
            self.open_turn(now);
        }
        match &mut self.input {
            Some(existing) => {
                existing.push('\n');
                existing.push_str(line);
            }
            None => self.input = Some(line.to_string()),
        }
    }

    /// Feed frotz output. Returns a turn if this chunk finished one.
    pub fn feed(&mut self, bytes: &[u8], now: Instant) -> Option<FramedTurn> {
        if bytes.is_empty() {
            return None;
        }
        if !self.open {
            // frotz spoke without being asked: the startup intro, a death
            // message, or a delayed fragment. §7 edge case.
            self.open_turn(now);
        }
        self.buf.extend_from_slice(bytes);
        self.last_output_at = now;

        // An unambiguous prompt (a complete `>` line) ends the turn at once.
        if completed_prompt_line(&self.buf) {
            return Some(self.finish(now, EndReason::Prompt));
        }

        // A bare `>` at the very end is probably the prompt; give frotz a moment to
        // prove otherwise (see PROMPT_SETTLE_MS).
        self.prompt_candidate = if unterminated_prompt(&self.buf) {
            Some(now)
        } else {
            None
        };
        None
    }

    /// Open a turn that nobody asked for, and that nobody's input opened.
    ///
    /// There is exactly one of these in a session: the story's opening text, which arrives
    /// before any line has been written. On an interpreter with the tool channel the alternative
    /// — opening a turn for *any* unsolicited bytes — is not safe, because a scripted sequence's
    /// leftovers (the prompt reprinted after the core named a save file, the `Ok.` of a save
    /// taken as machinery) would each open one and shift every turn that followed.
    pub fn begin_unsolicited(&mut self, now: Instant) {
        if !self.open {
            self.open_turn(now);
        }
    }

    /// Accumulate bytes without letting the heuristics decide anything.
    ///
    /// Used on an interpreter with the tool channel. The turn's bytes still have to be
    /// collected — and trimmed the same way — but the *decision* that the turn is over comes
    /// from the interpreter (`finish_on_signal`), which is the whole point of having a channel.
    /// Neither heuristic runs here, so a story that prints something shaped like a prompt
    /// cannot end a turn early, and nothing waits for silence to decide anything.
    pub fn accumulate(&mut self, bytes: &[u8], now: Instant) {
        if bytes.is_empty() {
            return;
        }
        if !self.open {
            self.open_turn(now);
        }
        self.buf.extend_from_slice(bytes);
        self.last_output_at = now;
    }

    /// Called on a timer by the core: finishes the turn if frotz has gone quiet.
    pub fn poll_quiescence(&mut self, now: Instant) -> Option<FramedTurn> {
        if !self.open || self.buf.is_empty() {
            return None;
        }

        if let Some(seen_at) = self.prompt_candidate {
            if now.saturating_duration_since(seen_at) >= self.settle {
                return Some(self.finish(now, EndReason::Prompt));
            }
        }

        if now.saturating_duration_since(self.last_output_at) >= self.timeout {
            return Some(self.finish(now, EndReason::Quiescence));
        }

        None
    }

    /// Call this when the interpreter itself says it is waiting for input.
    ///
    /// Used instead of `poll_quiescence` on an interpreter with the tool channel: the turn is
    /// over because the interpreter has said so, having flushed everything it printed, and
    /// there is nothing left to infer. Returns `None` when no turn is open, which is what a
    /// re-prompt after one of the core's own commands looks like.
    pub fn finish_on_signal(&mut self, now: Instant) -> Option<FramedTurn> {
        if !self.open {
            return None;
        }
        Some(self.finish(now, EndReason::Interpreter))
    }

    /// Throw away the bytes gathered since `mark` **without ending the turn**.
    ///
    /// For a chunk that turns out to belong to the interpreter rather than to the game: the
    /// prompt it reprints after one of the core's own commands. Clearing the whole framer here
    /// would be a real bug, and was one — a command can be sent while a line is already in
    /// flight, the refreshed prompt arrives first, and the turn that the line opened was thrown
    /// away with it. The turn keeps its input and stays open; only the bytes go.
    ///
    /// *Which* bytes go is `mark`: the buffer length when the story's own prompt arrived, which
    /// is where its text ends. Dropping everything instead — as this did — threw away the turn's
    /// output along with the reprint, and a story whose text all arrives *before* the key prompt
    /// (bureaucracy's licence preamble) came out with an empty turn and nothing but a screen.
    pub fn discard_since(&mut self, mark: usize) {
        if mark < self.buf.len() {
            self.buf.truncate(mark);
        }
        self.last_output_at = Instant::now();
    }

    /// Forget any half-framed turn (used when frotz is restarted).
    pub fn reset(&mut self) {
        let now = Instant::now();
        self.buf.clear();
        self.input = None;
        self.open = false;
        self.opened_at = now;
        self.last_output_at = now;
        self.prompt_candidate = None;
    }

    fn open_turn(&mut self, now: Instant) {
        self.open = true;
        self.buf.clear();
        self.input = None;
        self.opened_at = now;
        self.last_output_at = now;
    }

    fn finish(&mut self, now: Instant, ended_by: EndReason) -> FramedTurn {
        let elapsed_ms = now
            .saturating_duration_since(self.opened_at)
            .as_millis()
            .min(u64::MAX as u128) as u64;
        let mut output = std::mem::take(&mut self.buf);
        // Both of the signals that mean "the interpreter is ready for input again" arrive after
        // it has printed its prompt, so both leave one to strip. Only quiescence does not: there
        // was no prompt to wait for.
        if matches!(ended_by, EndReason::Prompt | EndReason::Interpreter) {
            strip_trailing_prompt(&mut output);
        }
        trim_blank_edges(&mut output);
        let input_written = self.input.is_some();
        let input = self.input.take().unwrap_or_default();
        self.open = false;
        FramedTurn {
            input,
            input_written,
            output,
            elapsed_ms,
            ended_by,
        }
    }
}

// ---------------------------------------------------------------------------
// Byte helpers
// ---------------------------------------------------------------------------

fn is_blank(b: u8) -> bool {
    matches!(b, b'\n' | b'\r' | b' ' | b'\t')
}

/// The last complete line, trimmed of surrounding blank bytes.
fn last_line(buf: &[u8]) -> Option<&[u8]> {
    if !buf.ends_with(b"\n") {
        return None; // a partial line is not a line yet
    }
    let without_newline = &buf[..buf.len() - 1];
    let start = without_newline
        .iter()
        .rposition(|&b| b == b'\n')
        .map(|i| i + 1)
        .unwrap_or(0);
    let line = &without_newline[start..];
    let line = trim_ascii(line);
    Some(line)
}

/// Trim trailing spaces, tabs and carriage returns — but not newlines, which are
/// significant when we are asking whether a prompt is the last thing written.
fn trim_trailing_spaces(buf: &[u8]) -> &[u8] {
    let mut end = buf.len();
    while end > 0 && matches!(buf[end - 1], b' ' | b'\t' | b'\r') {
        end -= 1;
    }
    &buf[..end]
}

/// Index just past the end of the buffer's final line (for prompt stripping).
fn last_line_start(buf: &[u8]) -> usize {
    let without_newline = if buf.ends_with(b"\n") {
        &buf[..buf.len() - 1]
    } else {
        buf
    };
    without_newline
        .iter()
        .rposition(|&b| b == b'\n')
        .map(|i| i + 1)
        .unwrap_or(0)
}

/// Does the buffer end with a complete line that is exactly `>`?
fn completed_prompt_line(buf: &[u8]) -> bool {
    last_line(buf) == Some(b">".as_slice())
}

/// Does the buffer end with `>` and no newline — frotz's prompt as it is actually
/// written? It could still turn out to be the start of a longer line, which is why
/// this is a *candidate* rather than a decision.
fn unterminated_prompt(buf: &[u8]) -> bool {
    trim_trailing_spaces(buf).last() == Some(&b'>')
}

/// Does the buffer end with frotz's prompt in either of its shapes?
fn last_line_is_prompt(buf: &[u8]) -> bool {
    completed_prompt_line(buf) || unterminated_prompt(buf)
}

/// Public alias used by the core's scripted save/restore sequences, which wait for
/// the same prompt to know that frotz is ready to be talked to again.
pub fn buffer_ends_with_prompt(buf: &[u8]) -> bool {
    last_line_is_prompt(buf)
}

/// Remove a trailing lone-`>` prompt, in either of its two shapes.
fn strip_trailing_prompt(buf: &mut Vec<u8>) {
    if last_line(buf) == Some(b">".as_slice()) {
        buf.truncate(last_line_start(buf));
        return;
    }

    let end = trim_trailing_spaces(buf).len();
    if buf[..end].last() == Some(&b'>') {
        buf.truncate(end - 1);
    }
}

fn trim_ascii(mut s: &[u8]) -> &[u8] {
    while let [first, rest @ ..] = s {
        if is_blank(*first) {
            s = rest;
        } else {
            break;
        }
    }
    while let [rest @ .., last] = s {
        if is_blank(*last) {
            s = rest;
        } else {
            break;
        }
    }
    s
}

fn trim_blank_edges(buf: &mut Vec<u8>) {
    let trimmed = trim_ascii(buf);
    let start = trimmed.as_ptr() as usize - buf.as_ptr() as usize;
    let end = start + trimmed.len();
    buf.truncate(end);
    if start > 0 {
        buf.drain(..start);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// frotz's opening turn in `-q` mode: the game's own intro text, then the prompt.
    const INTRO: &[u8] = b"\nYou are standing at the end of a road before a small brick building. \
Around you\nis a forest. A small stream flows out of the building and down a gully.\n\n\n\n>\n";

    /// The save sub-prompt: starts with `>` but is *not* a turn boundary.
    const SAVE_PROMPT: &[u8] = b">Please enter a filename [advent.qzl]: \n";

    fn now() -> Instant {
        Instant::now()
    }

    #[test]
    fn the_interpreters_own_signal_ends_a_turn_with_the_prompt_stripped() {
        // The tool channel's `input_wanted` arrives after the prompt has been written, so the
        // buffer holds that prompt exactly as the `>` heuristic leaves it — and a turn whose
        // output ends in `>` is what the acceptance test calls a leak.
        let mut framer = TurnFramer::new(Duration::from_millis(400));
        let start = Instant::now();
        framer.note_input("look", start);
        framer.accumulate(b"\n\nYou are in a maze.\n\n>", start);
        let turn = framer
            .finish_on_signal(start)
            .expect("the interpreter said the turn was over");
        assert_eq!(turn.ended_by, EndReason::Interpreter);
        assert_eq!(turn.input, "look");
        assert_eq!(turn.output_text(), "You are in a maze.");
        assert!(turn.input_written);
    }

    #[test]
    fn accumulate_without_a_signal_keeps_the_turn_open() {
        // Nothing about the bytes themselves ends a turn on this path: the prompt-shaped line is
        // exactly what the old heuristic would have jumped at.
        let mut framer = TurnFramer::new(Duration::from_millis(400));
        let start = Instant::now();
        framer.note_input("look", start);
        framer.accumulate(b"A room.\n>\n", start);
        assert!(framer.is_open(), "only the interpreter closes it");
        // And a framer that was never opened has nothing to close.
        let mut idle = TurnFramer::new(Duration::from_millis(400));
        assert!(idle.finish_on_signal(start).is_none());
    }

    #[test]
    fn a_prompt_with_no_trailing_newline_frames_turn_after_the_settle_window() {
        // The exact bytes the container produced: frotz prints `>` and waits, so the
        // buffer ends there with no newline. Before the settle window existed, every
        // turn waited the full 400 ms and carried the `>` into its output.
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("look", t0);
        assert!(
            f.feed(
                b"\n\nYou are standing at the end of a road.\n\nand a gully.\n\n>",
                t0 + Duration::from_millis(3),
            )
            .is_none(),
            "a bare `>` is a candidate, not an immediate decision"
        );

        // Not yet: the settle window has not elapsed.
        assert!(f.poll_quiescence(t0 + Duration::from_millis(20)).is_none());

        let turn = f
            .poll_quiescence(t0 + Duration::from_millis(40))
            .expect("the prompt is confirmed");
        assert_eq!(turn.ended_by, EndReason::Prompt);
        assert_eq!(turn.elapsed_ms, 40);
        assert_eq!(
            turn.output_text(),
            "You are standing at the end of a road.\n\nand a gully."
        );
        assert!(
            !turn.output_text().contains('>'),
            "the prompt is not content"
        );
    }

    #[test]
    fn a_sub_prompt_split_across_reads_does_not_end_a_turn() {
        // If a read boundary falls between `>` and frotz's filename request, the
        // candidate is cancelled by the continuation and the turn stays whole — it
        // then frames by quiescence with the text intact rather than lost.
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("save", t0);
        assert!(f.feed(b">", t0).is_none());
        assert!(f
            .feed(b"Please enter a filename [advent.qzl]: ", t0)
            .is_none());

        let turn = f
            .poll_quiescence(t0 + Duration::from_millis(400))
            .expect("quiescence ends the turn");
        assert_eq!(turn.ended_by, EndReason::Quiescence);
        // Trailing whitespace is trimmed from a turn's output, so the space after the
        // colon does not survive; the text itself does.
        assert_eq!(turn.output_text(), ">Please enter a filename [advent.qzl]:");
    }

    #[test]
    fn a_prompt_line_with_a_newline_is_still_stripped() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("look", t0);
        let turn = f.feed(b"You are in a forest.\n\n>\n", t0).unwrap();

        assert_eq!(turn.output_text(), "You are in a forest.");
    }

    #[test]
    fn startup_intro_frames_a_turn_with_empty_input() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        let turn = f.feed(INTRO, t0).expect("prompt ends the intro turn");
        assert_eq!(turn.input, "");
        assert_eq!(turn.ended_by, EndReason::Prompt);
        assert!(turn
            .output_text()
            .starts_with("You are standing at the end of a road"));
        assert!(
            !turn.output_text().contains('>'),
            "the prompt itself is not part of the turn output: {:?}",
            turn.output_text()
        );
        assert!(!f.is_open());
    }

    #[test]
    fn chunks_split_mid_line_do_not_frame_early() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        // Split the prompt across three reads: `>` is not a line until `\n` lands.
        assert!(f.feed(b"gully.\n\n\n\n>", t0).is_none());
        assert!(f.is_open());
        assert!(f.feed(b"", t0).is_none());
        let turn = f
            .feed(b"\n", t0)
            .expect("completes only once the newline arrives");
        assert_eq!(turn.output_text(), "gully.");
    }

    #[test]
    fn save_sub_prompt_does_not_end_a_turn() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        assert!(f.feed(SAVE_PROMPT, t0).is_none());
        assert!(f.is_open());
        // …and the turn only ends when frotz truly asks for input again.
        let turn = f.feed(b"\nOk.\n\n>\n", t0).unwrap();
        assert_eq!(
            turn.output_text(),
            ">Please enter a filename [advent.qzl]: \n\nOk."
        );
    }

    #[test]
    fn a_command_turn_carries_its_input_and_output() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("look", t0);
        let turn = f
            .feed(b"\n\nYou are standing at the end of a road.\n\n>\n", t0)
            .expect("prompt ends the turn");
        assert_eq!(turn.input, "look");
        assert_eq!(turn.output_text(), "You are standing at the end of a road.");
        assert_eq!(turn.ended_by, EndReason::Prompt);
    }

    #[test]
    fn a_confirmation_question_frames_by_quiescence_not_at_a_prompt() {
        // Measured on dfrotz 2.54: `quit` prints this question and waits, with no
        // trailing newline and no `>`. It is therefore *not* a prompt, and the turn
        // can only end by quiescence — which is the signal the core uses to know the
        // next line written would be eaten as the answer, not read as a command.
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("quit", t0);
        assert!(f.feed(b"\n\nAre you sure you want to quit? ", t0).is_none());
        assert!(f.poll_quiescence(t0 + Duration::from_millis(200)).is_none());

        let turn = f
            .poll_quiescence(t0 + Duration::from_millis(400))
            .expect("quiescence ends the turn");
        assert_eq!(turn.ended_by, EndReason::Quiescence);
        assert_eq!(turn.input, "quit");
        assert_eq!(turn.output_text(), "Are you sure you want to quit?");
    }

    #[test]
    fn frotz_re_asking_behind_a_prompt_still_frames_at_that_prompt() {
        // The follow-up to an answer frotz did not understand: it complains and then
        // prints its ordinary `>` prompt on the same line. The turn *does* end at the
        // prompt here — which is why remembering the question matters as well as
        // reading the prompt, or the follow-up would look perfectly safe to write to.
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("save", t0);
        assert!(f.feed(b"Please answer yes or no.>", t0).is_none());

        let turn = f
            .poll_quiescence(t0 + Duration::from_millis(40))
            .expect("the prompt is confirmed after the settle window");
        assert_eq!(turn.ended_by, EndReason::Prompt);
        assert_eq!(turn.output_text(), "Please answer yes or no.");
    }

    #[test]
    fn quiescence_frames_a_turn_when_no_prompt_appears() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("z", t0);
        assert!(f.feed(b"I don't know the word \"z\".\n", t0).is_none());
        // Not yet: the window has not elapsed.
        assert!(f.poll_quiescence(t0 + Duration::from_millis(399)).is_none());
        let turn = f
            .poll_quiescence(t0 + Duration::from_millis(400))
            .expect("quiescence ends the turn");
        assert_eq!(turn.ended_by, EndReason::Quiescence);
        assert_eq!(turn.input, "z");
        assert_eq!(turn.output_text(), "I don't know the word \"z\".");
    }

    #[test]
    fn quiescence_ignores_an_empty_turn() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        assert!(f.poll_quiescence(t0 + Duration::from_secs(10)).is_none());
    }

    #[test]
    fn elapsed_time_is_measured_from_the_input() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("north", t0);
        let turn = f
            .feed(b"It is a forest.\n\n>\n", t0 + Duration::from_millis(210))
            .unwrap();
        assert_eq!(turn.elapsed_ms, 210);
    }

    #[test]
    fn unsolicited_output_after_a_turn_frames_a_new_one() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.feed(b"intro\n\n>\n", t0).unwrap();
        assert!(!f.is_open());
        // A death message arrives out of nowhere: its own turn, empty input.
        let turn = f.feed(b"You have died.\n\n>\n", t0).unwrap();
        assert_eq!(turn.input, "");
        assert_eq!(turn.output_text(), "You have died.");
    }

    #[test]
    fn a_second_input_mid_turn_is_recorded_not_lost() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("look", t0);
        f.note_input("north", t0);
        let turn = f.feed(b"You are in a forest.\n\n>\n", t0).unwrap();
        assert_eq!(turn.input, "look\nnorth");
    }

    #[test]
    fn discarding_bytes_keeps_the_turn_and_its_input() {
        // The bug this exists for: the interpreter reprints its prompt after a command the core
        // sent, and that chunk arrives *after* the next game line has already been written. What
        // has to go is the prompt's bytes, not the turn the line opened.
        let mut framer = TurnFramer::new(Duration::from_millis(400));
        let start = Instant::now();
        framer.note_input("inventory", start);
        framer.accumulate(b">", start);
        framer.discard_since(0);
        assert!(framer.is_open(), "the turn is still open");
        assert_eq!(framer.buffered_len(), 0, "and the prompt is gone from it");

        framer.accumulate(b"\n\nYou are empty-handed.\n\n>", start);
        let turn = framer.finish_on_signal(start).expect("the signal ends it");
        assert_eq!(turn.input, "inventory", "with its input intact");
        assert_eq!(turn.output_text(), "You are empty-handed.");
    }

    #[test]
    fn discarding_since_the_prompt_keeps_the_text_that_came_before_it() {
        // Measured on bureaucracy, whose whole licence preamble arrives before a *key* prompt:
        // the core asks for the screen at that prompt, the interpreter reprints `>`, and clearing
        // the buffer rather than truncating it to the prompt's mark left the turn with no output
        // at all — the preamble was on the screen and nowhere else, which is precisely the bug
        // the screen channel was added to fix.
        let mut framer = TurnFramer::new(Duration::from_millis(400));
        let start = Instant::now();
        framer.note_input("", start);
        framer.accumulate(b"Our records show that you do not have a licence.\n", start);
        let mark = framer.buffered_len();
        // The command's reprint, and then the prompt that the prompt-event is about.
        framer.accumulate(b">", start);
        framer.discard_since(mark);
        assert_eq!(framer.buffered_len(), mark, "only the reprint went");

        let turn = framer.finish_on_signal(start).expect("the signal ends it");
        assert_eq!(
            turn.output_text(),
            "Our records show that you do not have a licence.",
            "the turn keeps the text it had"
        );
    }

    #[test]
    fn reset_discards_a_half_framed_turn() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("look", t0);
        f.feed(b"half a room desc", t0);
        assert!(f.is_open());
        f.reset();
        assert!(!f.is_open());
        assert_eq!(f.buffered_len(), 0);
        assert!(f.poll_quiescence(t0 + Duration::from_secs(5)).is_none());
    }

    #[test]
    fn long_output_is_accumulated_without_loss() {
        let mut f = TurnFramer::new(Duration::from_millis(400));
        let t0 = now();
        f.note_input("verbose", t0);
        let mut expected = String::new();
        for i in 0..2000 {
            let chunk = format!("line {i}\n");
            expected.push_str(&chunk);
            assert!(f.feed(chunk.as_bytes(), t0).is_none());
        }
        let turn = f.feed(b">\n", t0).expect("prompt frames the turn");
        assert_eq!(turn.output_text(), expected.trim_end());
        assert!(turn.output_text().contains("line 1999"));
    }
}
