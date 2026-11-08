//! The game screen (§11, issues #20 and #17).
//!
//! The `game` pane is not a filtered view of the event stream: it is a **screen**, and a
//! screen has one job — show everything that happened on it, in order, exactly once, and
//! nothing else.
//!
//! That is the whole invariant, and it is worth stating as one because the pane had been
//! assembled from two independent producers with a policy applied to each (issue #20):
//!
//! * the commands written into the game, which frotz cannot echo itself because it reads
//!   from a pipe — including the ones the *core* writes, because on a real screen a
//!   `save` is visible whoever typed it, and hiding it left a hole exactly where a
//!   checkpoint had been;
//! * every byte frotz printed, including the prompts and the machinery, because
//!   "we are not trying to hide anything" is the point of the pane;
//!
//! and nothing else: no pane greeting, no `[system]` notices (those belong to the
//! companion's pane, where the conversation is), no annotations we invented.
//!
//! The log is what makes a late-arriving pane work: a screen you sit down in front of
//! shows what is on it, so a `game` pane that connects mid-session — or reconnects after
//! an idle drop (§11.2, issue #17) — is replayed from here.
//!
//! Recording happens under the same lock as seat registration, which is what makes replay
//! exact: a message is either recorded before the seat exists (so the pane gets it in the
//! replay and not live) or after (so it gets it live and not in the replay). No gaps, no
//! duplicates, and no ordering to hope for.

use crate::seat::ClientMessage;
use std::collections::VecDeque;

/// The bound a screen is kept to when nothing says otherwise (03_session.md §4 (`S25`–`S29`)).
///
/// Several pages rather than one. A pane that reconnects — or a browser tab that was reloaded,
/// which is the same thing from here — is exactly the moment somebody wants to look back at what
/// just happened, and a bound of one screenful makes that impossible by construction. 600 lines
/// is about twenty-five screens of a 24-row pane, and a few tens of kilobytes against a session
/// that already holds a transcript, a notebook and a record of every turn.
///
/// The bound is in lines rather than messages because a chunk of game output is not a line.
///
/// It is a default rather than a decision: the value in force is `replay_lines` in
/// `core-config.json`, which is one setting for both records — the screen and the conversation
/// are counted separately but bounded together, because they are two views of one session.
pub const REPLAY_LINES: usize = crate::config::DEFAULT_REPLAY_LINES;

/// Is this message part of the screen?
///
/// One rule, in one place, because three things now ask the same question: the screen record that
/// a pane is replayed from, the playback that is written to disk (03_session.md §4.2 (`S31`)), and the tests
/// that hold both to it.
pub fn is_the_screen(message: &ClientMessage) -> bool {
    matches!(
        message,
        ClientMessage::GameOutput(_) | ClientMessage::Input { .. }
    )
}

/// The same rule, as the text a playback file holds (03_session.md §4.2 (`S31`)).
///
/// The game's bytes exactly as they arrived, and the line written into the game as the pane shows
/// it — a command lands on the `>` prompt with nothing after it, so `north` goes in bare and the
/// file reads the way that terminal read. Everything else is None: this is the game's screen, and
/// the conversation is already written down twice.
pub fn as_playback_text(message: &ClientMessage) -> Option<String> {
    match message {
        ClientMessage::GameOutput(bytes) => Some(String::from_utf8_lossy(bytes).into_owned()),
        ClientMessage::Input { line, .. } => Some(line.clone()),
        _ => None,
    }
}

/// The ordered record of one session's screen: what was written into the game and what
/// frotz wrote back.
#[derive(Debug)]
pub struct ScreenLog {
    entries: VecDeque<(ClientMessage, usize)>,
    lines: usize,
    /// The bound this record is kept to, from `core-config.json`.
    limit: usize,
}

impl Default for ScreenLog {
    /// The shipped bound: what a screen built by `Default` (which is what the tests do) runs on.
    fn default() -> Self {
        Self::new(REPLAY_LINES)
    }
}

impl ScreenLog {
    /// A screen kept to the configured bound.
    pub fn new(limit: usize) -> Self {
        Self {
            entries: VecDeque::new(),
            lines: 0,
            limit,
        }
    }

    /// Record a message if it is part of the screen. Everything else is ignored, which is
    /// the filter the old pane applied per message — it just belongs here, once, rather
    /// than in the renderer.
    pub fn push(&mut self, message: &ClientMessage) {
        if !is_the_screen(message) {
            return;
        }
        let lines = match message {
            ClientMessage::GameOutput(bytes) => bytes.iter().filter(|byte| **byte == b'\n').count(),
            // A command echo occupies the prompt line it lands on.
            ClientMessage::Input { .. } => 1,
            _ => return,
        };

        self.entries.push_back((message.clone(), lines));
        self.lines += lines;
        // Oldest first, until it fits — but never the newest entry on its own. frotz can
        // print a thousand lines in a single chunk (a full score dump, or a game's opening
        // crawl), and dropping that entry would leave the screen empty and a reconnecting
        // pane with nothing at all. The bound is therefore exact for ordinary traffic and
        // approximate for one oversized chunk, which is the trade that keeps a screen a
        // screen.
        while self.lines > self.limit && self.entries.len() > 1 {
            match self.entries.pop_front() {
                Some((_, dropped)) => self.lines = self.lines.saturating_sub(dropped),
                None => break,
            }
        }
    }

    /// The screen as it stands, oldest first.
    pub fn replay(&self) -> Vec<ClientMessage> {
        self.entries
            .iter()
            .map(|(message, _)| message.clone())
            .collect()
    }

    /// How many messages are held, and whether anything is. Used by the tests.
    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    #[cfg(test)]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::game::InputSource;

    fn output(text: &str) -> ClientMessage {
        ClientMessage::GameOutput(text.as_bytes().to_vec())
    }

    fn input(line: &str, source: InputSource) -> ClientMessage {
        ClientMessage::Input {
            line: line.to_string(),
            source,
        }
    }

    #[test]
    fn holds_game_text_and_the_commands_written_into_it() {
        let mut screen = ScreenLog::new(REPLAY_LINES);
        screen.push(&output("You are in a valley.\n>"));
        screen.push(&input("north", InputSource::Player));
        screen.push(&output("Forest.\n>"));

        let replayed = screen.replay();
        assert_eq!(replayed.len(), 3);
        assert!(matches!(replayed[0], ClientMessage::GameOutput(_)));
        assert!(matches!(replayed[1], ClientMessage::Input { .. }));
        assert!(matches!(replayed[2], ClientMessage::GameOutput(_)));
    }

    #[test]
    fn a_checkpoint_is_on_the_screen_because_frotz_wrote_it() {
        let mut screen = ScreenLog::new(REPLAY_LINES);
        // The core's own `save` and its filename: machinery, and still screen (issue #20).
        screen.push(&input("save", InputSource::Scripted));
        screen.push(&output("Please enter a filename [stage]: "));

        assert_eq!(screen.len(), 2);
    }

    #[test]
    fn keeps_nothing_that_is_not_the_screen() {
        let mut screen = ScreenLog::new(REPLAY_LINES);
        screen.push(&ClientMessage::Companion("watch out".into()));
        screen.push(&ClientMessage::System("saved advent (auto)".into()));
        screen.push(&ClientMessage::Thought("hmm".into()));
        screen.push(&ClientMessage::Player("me".into()));
        screen.push(&ClientMessage::Ready);

        assert!(screen.is_empty(), "the conversation is not the screen");
    }

    #[test]
    fn replay_is_bounded_by_lines_not_by_chunks() {
        let mut screen = ScreenLog::new(REPLAY_LINES);
        for _ in 0..REPLAY_LINES {
            screen.push(&output("line\n"));
        }
        let before = screen.len();

        // One more line of output pushes the oldest chunks off, not the newest.
        screen.push(&output("another line\n"));
        assert!(screen.len() <= before + 1);
        assert!(screen.lines <= REPLAY_LINES + 1);
    }

    #[test]
    fn one_oversized_chunk_does_not_empty_the_screen() {
        let mut screen = ScreenLog::new(REPLAY_LINES);
        screen.push(&output("the intro\n"));
        // A single dump far larger than the whole replay budget.
        screen.push(&output(&"score line\n".repeat(REPLAY_LINES * 3)));

        let replayed = screen.replay();
        assert_eq!(
            replayed.len(),
            1,
            "the oversized chunk is kept whole rather than dropped along with the rest"
        );
        let text = match &replayed[0] {
            ClientMessage::GameOutput(bytes) => String::from_utf8_lossy(bytes).into_owned(),
            other => panic!("expected game output, got {other:?}"),
        };
        assert!(
            text.contains("score line"),
            "the newest chunk is the survivor"
        );
    }
}
