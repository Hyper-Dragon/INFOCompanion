//! The conversation record: what the pane that shows the conversation is given when it connects.
//!
//! The game has a screen (§8), and the screen record exists to answer one question — what is on
//! the screen right now. The companion's pane has no screen; it has a conversation. But for a long
//! time it was handed the screen's record and nothing else, filtered by its own view, and filtering
//! game text out of a record that holds nothing but game text leaves nothing at all. So a `quill`
//! pane that reconnected came back empty: the one pane whose whole purpose is following a
//! conversation already under way was the one pane nobody could tell what had happened
//! (03_session.md §4 (`S25`–`S29`)).
//!
//! There are therefore **two records**, and this is the second one. They are deliberately separate
//! rather than one log with two views of it:
//!
//! * they bound different things. The screen's bound is in lines of monitor text, because a screen
//!   is a fixed number of rows and a chunk of frotz output is not a line; this one bounds the
//!   conversation, which is a few short blocks a minute;
//! * they hold different messages, and the overlap is small enough to be worth the duplication —
//!   a player's own command (`Input { source: Player }`) is the only message that belongs to both,
//!   because on a screen it is an echo and in a conversation it is something the player did.
//!
//! What is in here is the conversation: the companion's speech, the player's lines, the session's
//! notices, and the lines the player typed into the game. What is deliberately not:
//!
//! * **the companion's reasoning.** A working-out is not something said, and it streams as it is
//!   written — replaying the last few minutes of thinking would push the conversation off the top
//!   of the pane. A pane opened mid-thought still shows the thought from that point on, because
//!   the live stream is everything the view shows;
//! * **the machinery.** A checkpoint's `save` and the filename it answers are the core's doing, and
//!   the screen is where they belong (02_game_boundary.md §6 (`G27`–`G34`)) — the same rule the view itself applies;
//! * **the page's own settings.** A page puts the session back where it was by typing the commands
//!   in (07_webui.md §5 (`W18`–`W25`)), which on the wire is indistinguishable from a player's line. Those are the
//!   harness talking to itself: a record that kept them would replay a page's own chatter into the
//!   pane every time it opened (03_session.md §4 (`S25`–`S29`)). The *answers* to them never reach other panes at
//!   all — they go to the pane that asked (`say_to_the_asker`), which is the other half of the same
//!   decision.
//!
//! The record is a record, not a delivery mechanism. Nothing here is paced — a replay is being told
//! what has already been said, not watching words arrive — and nothing here is filtered by a view:
//! the whole record is handed to the pane and the view decides what it shows, which is what keeps a
//! reconnecting pane the same pane as the one that was there before.

use crate::core::Verb;
use crate::game::InputSource;
use crate::seat::ClientMessage;
use std::collections::VecDeque;

/// The bound a conversation is kept to when nothing says otherwise.
///
/// The same setting as the screen's (`replay_lines`), and counted separately, so a long turn in
/// the game cannot push the conversation out of the record and a long conversation cannot do the
/// same to the screen. One knob rather than two because they are two views of one session and an
/// operator who wants more of the evening back wants more of both.
///
/// The companion speaks in short blocks — a sentence or two, sometimes a paragraph — so 600 lines
/// is several hundred of them: more than an evening's worth of exchanges.
pub const CONVERSATION_LINES: usize = crate::config::DEFAULT_REPLAY_LINES;

/// The ordered record of one session's conversation: what was said, by whom, and what the session
/// said about itself.
#[derive(Debug)]
pub struct ConversationLog {
    entries: VecDeque<(ClientMessage, usize)>,
    lines: usize,
    /// The bound this record is kept to, from `core-config.json`.
    limit: usize,
}

impl Default for ConversationLog {
    /// The shipped bound: what a conversation built by `Default` (which is what the tests do) runs
    /// on.
    fn default() -> Self {
        Self::new(CONVERSATION_LINES)
    }
}

impl ConversationLog {
    /// A conversation kept to the configured bound.
    pub fn new(limit: usize) -> Self {
        Self {
            entries: VecDeque::new(),
            lines: 0,
            limit,
        }
    }

    /// Record a message if it is part of the conversation. Everything else is ignored, and the
    /// filter belongs here rather than in the forwarder for the same reason the screen's does: it
    /// is a property of the record, and a pane that reconnects should get the same lines as one
    /// that was open all along.
    pub fn push(&mut self, message: &ClientMessage) {
        if !is_the_conversation(message) {
            return;
        }
        let lines = lines_in(message);

        self.entries.push_back((message.clone(), lines));
        self.lines += lines;
        // Oldest first, until it fits — but never the newest entry on its own. One speech block
        // can be long (a companion that answers a session-long question at once), and dropping
        // that entry would leave the pane showing nothing where the most recent thing said should
        // be. The bound is therefore exact for ordinary traffic and approximate for one oversized
        // block, which is the same trade the screen makes.
        while self.lines > self.limit && self.entries.len() > 1 {
            match self.entries.pop_front() {
                Some((_, dropped)) => self.lines = self.lines.saturating_sub(dropped),
                None => break,
            }
        }
    }

    /// The conversation as it stands, oldest first.
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

/// Is this message part of the conversation?
fn is_the_conversation(message: &ClientMessage) -> bool {
    match message {
        // Speech, and what the session says about itself.
        ClientMessage::Companion(_) | ClientMessage::System(_) => true,
        // The player's words. Not the page putting its own settings back.
        ClientMessage::Player(line) => !is_the_page_setting_itself(line),
        // Talking to the game. The machinery is the exception: a checkpoint's `save` is the
        // core's doing, and it belongs on the screen rather than in the conversation.
        ClientMessage::Input { source, .. } => *source != InputSource::Scripted,
        // The reasoning, the screen, the prompts and the answers to `/` commands — which go to
        // the pane that asked and are its own business.
        _ => false,
    }
}

/// How many lines of a pane this message occupies.
///
/// In lines rather than messages, as the screen counts them, because a message is not a line: the
/// companion can answer in a paragraph.
fn lines_in(message: &ClientMessage) -> usize {
    let text = match message {
        ClientMessage::Companion(text)
        | ClientMessage::System(text)
        | ClientMessage::Player(text)
        | ClientMessage::Input { line: text, .. } => text.as_str(),
        _ => return 0,
    };
    text.lines().count()
}

/// Is this one of the commands a page types to put its own settings back?
///
/// The switches and the pace control are ordinary commands (07_webui.md §5 (`W18`–`W25`)): the only route a page has
/// to the session is typing into it, so the page sends the same bytes a person would. That makes
/// them indistinguishable from a player's line on the wire, and this is where they are told apart —
/// by being the verbs that *are* the settings, and nothing else. A player typing `/tagson` is doing
/// the same thing the page does, and neither belongs in the conversation.
fn is_the_page_setting_itself(text: &str) -> bool {
    let Some(rest) = text.trim().strip_prefix('/') else {
        return false;
    };
    let word = rest.split_whitespace().next().unwrap_or_default();
    Verb::parse(word).is_some_and(Verb::is_a_setting)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn said(text: &str) -> ClientMessage {
        ClientMessage::Companion(text.to_string())
    }

    fn notice(text: &str) -> ClientMessage {
        ClientMessage::System(text.to_string())
    }

    fn player(text: &str) -> ClientMessage {
        ClientMessage::Player(text.to_string())
    }

    fn typed(line: &str, source: InputSource) -> ClientMessage {
        ClientMessage::Input {
            line: line.to_string(),
            source,
        }
    }

    fn text_of(message: &ClientMessage) -> String {
        match message {
            ClientMessage::Companion(text)
            | ClientMessage::System(text)
            | ClientMessage::Player(text) => text.clone(),
            ClientMessage::Input { line, .. } => line.clone(),
            other => panic!("expected a line of conversation, got {other:?}"),
        }
    }

    #[test]
    fn holds_the_speech_the_players_words_the_notices_and_the_lines_into_the_game() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        log.push(&notice("player-213 connected."));
        log.push(&player("what about the desk?"));
        log.push(&said("It has a drawer."));
        log.push(&typed("open drawer", InputSource::Player));

        let replayed: Vec<String> = log.replay().iter().map(text_of).collect();
        assert_eq!(
            replayed,
            vec![
                "player-213 connected.",
                "what about the desk?",
                "It has a drawer.",
                "open drawer",
            ]
        );
    }

    #[test]
    fn leaves_the_reasoning_out() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        log.push(&ClientMessage::Thought("the lamp is brass, so…".into()));
        log.push(&ClientMessage::ThoughtDelta("the lamp".into()));
        log.push(&said("It is brass."));

        let replayed = log.replay();
        assert_eq!(replayed.len(), 1);
        assert_eq!(text_of(&replayed[0]), "It is brass.");
    }

    #[test]
    fn leaves_the_screen_out() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        log.push(&ClientMessage::GameOutput(b"West of House\n>".to_vec()));
        log.push(&typed("save", InputSource::Scripted));
        log.push(&ClientMessage::Ready);
        log.push(&ClientMessage::Command("saved advent".into()));
        log.push(&said("West of House."));

        let replayed = log.replay();
        assert_eq!(replayed.len(), 1, "the screen is not the conversation");
        assert_eq!(text_of(&replayed[0]), "West of House.");
    }

    #[test]
    fn does_not_record_the_commands_a_page_types_to_put_itself_back() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        // Exactly what the page sends when it opens (07_webui.md §5 (`W18`–`W25`), `restoreCommands`), plus the
        // other end of each switch: any of them arriving here would be the page's own chatter in
        // the pane's history the next time the pane connected.
        for line in [
            "/tagson",
            "/tagsoff",
            "/debugon",
            "/debugoff",
            "/thinkon",
            "/thinkoff",
            "/veryslow",
            "/slow",
            "/medium",
            "/fast",
            "/veryfast",
            "  /tagsoff  ",
        ] {
            log.push(&player(line));
        }

        assert!(
            log.is_empty(),
            "the harness's settings are not conversation"
        );
    }

    #[test]
    fn still_records_the_lines_that_only_look_like_settings() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        // A player talking to the game about settings is not the page; the verbs that merely
        // begin like one are ordinary conversation; and a *query* changes nothing, so it is a
        // thing asked rather than a setting put back.
        log.push(&player("/type tagson"));
        log.push(&player("tagson"));
        log.push(&player("/speed"));
        log.push(&player("/SPEED"));
        log.push(&player("/status"));
        log.push(&player("set the speed to medium"));

        assert_eq!(log.len(), 6);
    }

    #[test]
    fn replay_is_bounded_by_lines_not_by_blocks() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        for _ in 0..CONVERSATION_LINES {
            log.push(&said("a line"));
        }
        let before = log.len();

        log.push(&said("another line"));
        assert!(log.len() <= before + 1);
        assert!(log.lines <= CONVERSATION_LINES + 1);
    }

    #[test]
    fn one_long_speech_does_not_empty_the_record() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        log.push(&said("It has a drawer."));
        // A single block far larger than the whole replay budget.
        log.push(&said(
            &"the drawer is empty\n".repeat(CONVERSATION_LINES * 3),
        ));

        let replayed = log.replay();
        assert_eq!(
            replayed.len(),
            1,
            "the long block is kept whole rather than dropped along with the rest"
        );
        assert!(text_of(&replayed[0]).contains("the drawer is empty"));
    }

    #[test]
    fn an_empty_message_does_not_spend_the_budget() {
        let mut log = ConversationLog::new(CONVERSATION_LINES);
        log.push(&notice(""));
        log.push(&said("It has a drawer."));

        assert_eq!(log.len(), 2, "an empty line is still a message in order");
        assert_eq!(log.lines, 1, "but it costs no lines of the bound");
    }
}
