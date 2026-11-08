//! The SSH player seat (§12, issue #14).
//!
//! A player's SSH connection is *a seat and nothing more*: it renders the event
//! stream for a human and turns typed lines into input. It owns no game state, so
//! detaching and reconnecting shows the world exactly as it was left (§12) — the
//! daemon holds the session, ssh is just a window onto it.
//!
//! Rendering rules:
//!
//! * game bytes go out untouched apart from newline translation (`\n` → `\r\n`),
//!   because the daemon deliberately runs frotz over pipes with no terminal
//!   post-processing (`frotz.rs`);
//! * companion lines are marked `[companion]` and are only ever emitted between
//!   turns, so they can never be confused with game text;
//! * core notices are marked `[system]`.

use crate::core::{Core, CoreCommand};
use crate::seat::{ClientMessage, SeatId, SeatKind, SeatReceiver, Writing};
use anyhow::{Context, Result};
use async_trait::async_trait;
use russh::server::{self, Auth, Msg, Server as _, Session};
use russh::{Channel, ChannelId, CryptoVec};
use russh_keys::key::KeyPair;
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tracing::{debug, info, warn};

use crate::config::SshTuning;
use crate::game::InputSource;
use crate::keys::{KeyEvent, KeyReader};
use crate::pace;
use crate::palette::{Palette, Role};

/// SSH listener and credentials.
#[derive(Debug, Clone)]
pub struct SshConfig {
    pub bind_address: String,
    pub port: u16,
    pub auth_user: String,
    pub auth_password: String,
    /// What the companion is called, used as the tag on its speech (`[Quill]`).
    pub agent_name: String,
    /// The seat's own timers (`core-config.json`): the keepalive that decides when a connection is
    /// dead, how long a rejected authentication is stalled for, and how often the core may be told
    /// the player is composing a line.
    pub tuning: SshTuning,
}

/// The SSH server.
pub struct SshServer {
    core: Arc<Core>,
    config: SshConfig,
    server_key: KeyPair,
    /// Sessions are numbered so a seat has a readable name in logs and notices
    /// rather than a debug-formatted channel id.
    players: u64,
}

impl SshServer {
    /// Build the server, generating an ephemeral host key.
    ///
    /// A generated key is deliberate for now: the daemon is a single-player
    /// container reached over a private network, and this keeps key management out
    /// of the story. Reconnecting clients may see a changed host key after a
    /// restart, which is what `ssh -o StrictHostKeyChecking=no` is for.
    pub fn new(core: Arc<Core>, config: SshConfig) -> Result<Self> {
        let server_key =
            KeyPair::generate_ed25519().context("failed to generate the SSH host key")?;
        Ok(Self {
            core,
            config,
            server_key,
            players: 0,
        })
    }

    /// Serve until the process ends.
    pub async fn run(mut self) -> Result<()> {
        let russh_config = russh::server::Config {
            auth_rejection_time: self.config.tuning.auth_rejection,
            auth_rejection_time_initial: Some(std::time::Duration::from_secs(0)),
            // Issue #17: a read-only pane sends nothing while it is being read, and
            // russh resets its inactivity timer only on traffic *received* from the
            // client — the game text we push the other way does not count at all. At
            // the 600 s default that killed a pane mid-session, mid-scroll. So the
            // timer is off, and liveness is established from our side instead: an
            // OpenSSH keepalive every 30 s, which a live client answers (russh asks
            // for the reply on purpose) and a dead one does not, so three unanswered
            // requests — the default `keepalive_max` — still end a dead connection.
            inactivity_timeout: None,
            keepalive_interval: Some(self.config.tuning.keepalive),
            keys: vec![self.server_key.clone()],
            ..Default::default()
        };
        let russh_config = Arc::new(russh_config);
        let bind_addr = format!("{}:{}", self.config.bind_address, self.config.port);
        info!(
            address = %bind_addr,
            user = %self.config.auth_user,
            "SSH listening"
        );
        self.run_on_address(russh_config, &bind_addr)
            .await
            .map_err(|err| anyhow::anyhow!("SSH server error: {err}"))
    }
}

/// What a terminal seat shows: which parts of the stream it is for.
///
/// The game and the companion share one screen today, and mixing them makes both harder
/// to follow — the game stops being a clean scroll, and the collaboration stops feeling
/// like collaboration. A view is the smallest thing that fixes it: one connection per
/// pane, each told at login what it is for.
///
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SeatView {
    /// Everything: game text, the companion's speech, session notices. The default, and
    /// what a single window gets.
    All,
    /// Game text only, pristine and uninterrupted — the pane you read the game in.
    Game,
    /// Everything that is *not* the game: the companion's words, its proposals, and
    /// session notices — the pane you talk and listen in.
    Companion,
    /// The typing pane's own prompt, plus the answers to the commands typed here.
    ///
    /// A pane to type in: your keystrokes echo where you type them, and the prompt is the
    /// pane's own — laid down on every Return (issue #37), so there is always somewhere to
    /// type. The game's output belongs in the game pane, and the conversation in the
    /// companion's.
    Input,
    /// The voice's own connection: the companion's speech, and nothing else (08_webui_tts.md).
    ///
    /// Not a pane, though it arrives on a terminal seat: it is owed each utterance and **never
    /// metered** (`08_webui_tts.md` §2 (`V4`, `V9`)). Its wire is one framed record per utterance
    /// rather than a rendering, it is **never** caught up on the record, and there is nothing to read
    /// on it or type at it. The bridge holds it and hands the records on; the page is what speaks.
    Voice,
}

impl SeatView {
    /// Does this view show this message?
    pub fn shows(self, message: &ClientMessage) -> bool {
        // The pane's own bytes go to the pane they are for and to nowhere else, so every view
        // shows them: a read-only pane is exactly the pane whose hint has to arrive (02_game_boundary.md
        // §8.2). Nobody else is ever sent one. The hold travels the same way — to the one pane
        // being typed in and no other — so it is shown on the same terms.
        if matches!(message, ClientMessage::Echo(_) | ClientMessage::Hold { .. }) {
            return true;
        }
        match self {
            SeatView::All => !matches!(message, ClientMessage::Ready),
            // The screen is the screen and nothing of ours goes on it — including the lamp, which
            // is not even a line (03_session.md §7 (`S44`–`S45`)). It is a monitor's light, and the light is on the
            // companion's business, so the pane that carries it is the pane that shows it.
            SeatView::Game => matches!(
                message,
                ClientMessage::GameOutput(_) | ClientMessage::Input { .. }
            ),
            // Everything that is not the game. Its own commands belong here too: "talking to
            // the game" is one of the things the companion does, and it should be visible as
            // such rather than only in passing in the game pane. So does what the player
            // said: this pane is where the conversation is.
            //
            // The machinery is the exception: a checkpoint's `save` and its filename are
            // the core's doing, not the conversation, and the screen is where they belong
            // (§11.1) — a conversation pane that narrates our own housekeeping is noise.
            SeatView::Companion => match message {
                ClientMessage::Companion(_)
                | ClientMessage::System(_)
                | ClientMessage::Thought(_)
                | ClientMessage::ThoughtDelta(_)
                | ClientMessage::Player(_) => true,
                // The companion's lamp, which is a fact about this pane and not a line on it.
                ClientMessage::Lamp { .. } => true,
                ClientMessage::Input { source, .. } => *source != InputSource::Scripted,
                _ => false,
            },
            // The typing pane: a prompt when the game is ready, and the answers to the
            // commands typed here. Without the second half a `/` command was answered into
            // a void — the reply went to every pane *except* the one that asked, because
            // this view shows nothing but `Ready` (issue #25).
            SeatView::Input => matches!(message, ClientMessage::Ready | ClientMessage::Command(_)),
            // The voice: speech and nothing else. The reasoning is the line this filter exists
            // for — it is streamed, it is the longest thing the companion produces, and read
            // aloud it is the difference between a voice the player keeps and one they mute for
            // ever. Notices, the player's own words and the game's prose are not the companion
            // speaking (08_webui_tts.md §2 (`V5`)).
            SeatView::Voice => matches!(message, ClientMessage::Companion(_)),
        }
    }

    /// Which view a login name asks for.
    ///
    /// The player's own name keeps the everything view, so one window still works exactly
    /// as before and the panes are opt-in.
    pub fn for_user(user: &str, agent_name: &str) -> SeatView {
        match user.trim().to_ascii_lowercase().as_str() {
            "game" => SeatView::Game,
            "me" | "input" => SeatView::Input,
            // `quill` is the companion's pane whatever the personality is called. The pane login
            // is a backend name — 04_companion.md §2 (`C6`–`C13`)'s proxy for "whoever the companion is" — and it
            // should not move every time somebody renames a character.
            "companion" | "quill" => SeatView::Companion,
            // The voice's login, held by the bridge and never by a person. Its own word rather
            // than the persona's name, for the same reason `quill` is: it is a backend name and
            // it must not move when somebody renames a character (08_webui_tts.md §2 (`V6`)). A
            // name that is *not* here falls through to the everything view, which is why this
            // one is written out rather than left to a default.
            "voice" => SeatView::Voice,
            other => {
                // The name the personality declares also works, so a companion called James can
                // be reached as `james` by anybody who has read the file.
                let named = agent_name.trim().to_ascii_lowercase();
                if !named.is_empty() && other == named {
                    SeatView::Companion
                } else {
                    SeatView::All
                }
            }
        }
    }

    /// Is this login name one of the pane views, rather than the player's own?
    pub fn is_pane_name(user: &str, agent_name: &str) -> bool {
        SeatView::for_user(user, agent_name) != SeatView::All
    }

    /// Is this pane a prompt to type at?
    ///
    /// The game pane and the companion's pane are for reading: they are the two places the
    /// player is most likely to press a key while following the game, and a stray keypress
    /// used to become a line the companion had to interpret. In those panes the only keys
    /// that do anything are the ones that end the session.
    pub fn accepts_input(self) -> bool {
        matches!(self, SeatView::All | SeatView::Input)
    }

    /// The one-line note a pane starts with, or `None` for a pane that is self-explanatory.
    ///
    /// The game pane gets nothing at all: it is a screen, and the first thing on a screen
    /// is what the game said (issue #20). The other panes are ones you have to know how to
    /// use, so they say so once.
    pub fn greeting(self, agent_name: &str) -> Option<String> {
        match self {
            SeatView::Game => None,
            SeatView::Companion => {
                // The companion's own name, so the one pane that has to explain itself explains
                // the right name. This used to be the word "Quill" typed into a string literal,
                // which meant a renamed companion was greeted by the old name in the window
                // devoted to reading it.
                let who = if agent_name.trim().is_empty() {
                    "the companion"
                } else {
                    agent_name.trim()
                };
                Some(format!(
                    "{who} — read only (ctrl-c or ctrl-d disconnects); type in the input pane"
                ))
            }
            SeatView::Input => {
                Some("type here — ctrl-c clears the line, ctrl-d disconnects".to_string())
            }
            // Nothing to read on a subscription and nothing to type at it, so nothing to explain.
            // The one thing that can be wrong on this connection is a record the relay cannot
            // frame, and that is the relay's to report rather than the pane's (08_webui_tts.md §3).
            SeatView::Voice => None,
            SeatView::All => None,
        }
    }
}

impl server::Server for SshServer {
    type Handler = SshClientHandler;

    fn new_client(&mut self, peer_addr: Option<std::net::SocketAddr>) -> Self::Handler {
        self.players += 1;
        let player = self.players;
        info!(peer = ?peer_addr, player, "SSH connection");
        SshClientHandler {
            core: Arc::clone(&self.core),
            config: self.config.clone(),
            player,
            seat_id: None,
            channels: HashMap::new(),
            input_buffer: String::new(),
            caret: 0,
            last_typing_notice: None,
            composing_holds: None,
            view: SeatView::All,
            palette: Palette::plain(),
        }
    }
}

/// One SSH connection.
pub struct SshClientHandler {
    core: Arc<Core>,
    config: SshConfig,
    /// This session's number, used to name the seat.
    player: u64,
    /// The seat this connection holds, handed back when the connection ends however it ends.
    ///
    /// `Option` for the same reason `channel_close` takes it: a connection that said goodbye has no
    /// seat left to give, and the one thing that must not happen is the core being told twice.
    seat_id: Option<SeatId>,
    channels: HashMap<ChannelId, ChannelState>,
    input_buffer: String,
    /// Where the caret is in `input_buffer`, as a byte index.
    ///
    /// The buffer is only ever the player's own characters, and `KeyReader` offers a byte in
    /// `0x20..0x7f` as text and nothing else, so it is ASCII by construction: a byte index and a
    /// character index are the same number, and every split below lands on a boundary.
    ///
    /// Left and Right arrows move this; typing and Backspace happen here rather than at the end
    /// of the line. Up and Down are still ignored: there is no history, and a key that does
    /// nothing must not look like a character.
    caret: usize,
    /// When this connection last told the core the player was typing (issue #42).
    ///
    /// Throttled here rather than in the router because this is where the keystrokes are: one
    /// message a second is enough to say "somebody is mid-sentence", and one per character would
    /// be a protocol whose traffic is proportional to the player's typing speed.
    last_typing_notice: Option<std::time::Instant>,
    /// Whether the core has been told that the line in the box begins with a space (03_session.md §6 (`S41`–`S43`)), so the
    /// fact is sent when it changes and not on every keystroke. `None` means "never told", which is
    /// why a line that starts with a space from its first character still reaches the agent.
    composing_holds: Option<bool>,
    /// Which pane this connection is, decided by the login name.
    view: SeatView,
    /// Colours for this connection: decided at login, when we know whether it asked for a
    /// terminal and what `TERM` it claims.
    palette: Palette,
}

struct ChannelState {
    /// Taken by the forwarder task once the session handle is available.
    receiver: Option<SeatReceiver>,
    /// Is this pane's cursor at the start of a fresh line?
    ///
    /// A typing pane is a prompt, so it must show exactly one. A turn end says "ready", and turns
    /// end while nobody is typing, so printing a prompt per turn would leave `>> >> >> >>` on the
    /// line. **The forwarder owns this**, and only it: every byte this pane shows — the game's
    /// output, the companion's words, the player's own keystrokes — is written by that one task
    /// (03_session.md §5 (`S35`–`S40`)), so where the cursor is is a property of what that task has written.
    at_line_start: Arc<AtomicBool>,
    /// The player has touched the keyboard, so the pane stops metering what it is in the middle
    /// of: the rest of the message arrives at once and the keystroke is echoed straight after it
    /// (03_session.md §5 (`S35`–`S40`)).
    ///
    /// Setting a flag is all the keystroke path may do here. Writing the echo itself is what made
    /// a pane have two writers, and two writers on one screen is a sentence with somebody else's
    /// command in the middle of it — which is what the 2026-09-20 UAT read as the companion
    /// "executing commands on the screen ahead of his speech".
    flush: Arc<AtomicBool>,
    /// This pane's key reader: it is what keeps an arrow key from becoming text.
    keys: KeyReader,
    /// Has this pane already been told that it is read-only? Once is a courtesy; every
    /// keypress would be a nag, and the player leaning on the keyboard is the very case
    /// this pane is protecting against.
    hint_shown: bool,
    /// How big this pane said it was, in the pty request that opened it.
    ///
    /// The line editor needs the width and nothing else does: a pane wraps on its own, silently,
    /// and an echo that steps back over a wrap does not land where it was told to (B2). `None` is
    /// a client that never asked for a pty — a script, or a pipe — and on that pane the editor
    /// keeps the arithmetic that assumes one row, which is all such a client can be given.
    pane: Option<Pane>,
}

/// The tag the player's own words are marked with: the name of the pane they type in, so
/// the companion's pane reads as a conversation between two people.
const PLAYER_TAG: &str = "me";

impl Drop for SshClientHandler {
    /// Hand the seat back when the connection ends, however it ends.
    ///
    /// `channel_close` is the hook for a client that *says* it is going, and `russh` calls it only
    /// when the peer sends that message — `ctrl-d`, or a window closed properly. A client that dies
    /// sends nothing at all: a closed laptop lid, a dropped link, a terminal killed from another
    /// window. The seat then stayed registered for the life of the process, so the core went on
    /// believing somebody was at the keyboard, `player_absent` was never emitted, and the companion
    /// went on playing an errand with an empty room. Measured 2026-10-01: two SSH seats opened and
    /// never closed, the agent log's last presence event 25 minutes old, and the core's own comment
    /// on this exactly describes what followed — *"left alone, the companion plays … it cost real
    /// money, and it is the opposite of the frame."*
    ///
    /// The drop is where this belongs because it is the one point **every** ending passes through:
    /// the handler is owned by the connection's task, so it is dropped when the client says goodbye,
    /// when the socket dies under it, and when the connection is torn down for any other reason.
    /// The seat is *taken*, so the polite path (`channel_close`) and this one cannot both report the
    /// same seat — and the unregister is spawned rather than awaited because a `Drop` cannot wait.
    fn drop(&mut self) {
        let Some(seat_id) = the_seat_the_connection_owes(&mut self.seat_id) else {
            return;
        };
        // No runtime means the process is going down, and a seat in a core that is ending is not
        // worth a panic in a destructor.
        let Ok(runtime) = tokio::runtime::Handle::try_current() else {
            return;
        };
        let core = Arc::clone(&self.core);
        runtime.spawn(async move {
            core.unregister_seat(seat_id).await;
        });
    }
}

/// The seat a connection still owes the core, taken so it can only ever be owed once.
///
/// Two hooks hand seats back and they must not both hand back the same one: `channel_close` is the
/// polite path and the destructor is the one that catches every other ending, and a core told twice
/// about one seat would count two departures where there was one. Taking it here is what makes the
/// pair safe rather than the order in which they happen to run.
fn the_seat_the_connection_owes(held: &mut Option<SeatId>) -> Option<SeatId> {
    held.take()
}

impl SshClientHandler {
    /// The tag the companion's speech is marked with.
    ///
    /// Its configured name when it has one, so the player reads `[Quill]` rather than
    /// a category label; `[companion]` otherwise, which still reads naturally.
    fn companion_tag(&self) -> String {
        let configured = self.config.agent_name.trim();
        if configured.is_empty() || configured.eq_ignore_ascii_case("companion") {
            "companion".to_string()
        } else {
            configured.to_string()
        }
    }

    /// Show this pane something that is its own and nobody else's (03_session.md §5 (`S35`–`S40`)).
    ///
    /// The keystroke echo and the pane's prompt: no other window shows them, neither record holds
    /// them, and they are written by the same task that writes everything else this pane shows. That
    /// is the whole of it — a second writer is a second order, and the two orders meet in the middle
    /// of a word.
    async fn show_here(&self, bytes: Vec<u8>) {
        let Some(seat_id) = self.seat_id else {
            return;
        };
        self.core
            .show_to_seat(seat_id, ClientMessage::Echo(bytes))
            .await;
    }

    /// Ask this pane for a prompt, the way a turn end does.
    ///
    /// Sent as the ordinary `Ready` rather than written here, so that the one rule about prompts —
    /// one per line, and only on an empty one — lives where the cursor is known, in the forwarder.
    /// A pane whose view does not show prompts is not given one, exactly as before.
    async fn ask_for_a_prompt(&self) {
        let Some(seat_id) = self.seat_id else {
            return;
        };
        self.core.show_to_seat(seat_id, ClientMessage::Ready).await;
    }

    /// The player has touched the keyboard: stop metering the message being written.
    ///
    /// **This pane only.** The ask that reaches every pane is [`Core::skip_ahead`] (`S38.1`), because
    /// the panes are separate connections and this handler can see no channel but its own.
    fn let_the_screen_stop_scrolling(&self, channel: ChannelId) {
        if let Some(state) = self.channels.get(&channel) {
            state.flush.store(true, Ordering::SeqCst);
        }
    }

    /// Hand the seat's stream to a task that writes it to the SSH channel.
    fn start_forwarder(&mut self, channel_id: ChannelId, session: &mut Session) {
        let Some(state) = self.channels.get_mut(&channel_id) else {
            return;
        };
        let Some(SeatReceiver::Terminal {
            mut rx,
            screen,
            conversation,
            progress,
        }) = state.receiver.take()
        else {
            return;
        };
        let at_line_start = Arc::clone(&state.at_line_start);
        let flush = Arc::clone(&state.flush);
        // `state` is done with: the receiver is owned now, and the flags are shared.
        let tag = self.companion_tag();
        // Owned before the spawn: the task outlives this call, so it cannot borrow the config.
        let agent_name = self.config.agent_name.clone();
        let view = self.view;
        let palette = self.palette.clone();
        let handle = session.handle();
        // The session itself, for the one setting a pane has to re-read per message rather than
        // once at connect: `/tagsoff`, typed in any window, changes what every window shows from
        // the next line on (03_session.md §2.1 (`S11`)).
        let core = Arc::clone(&self.core);
        tokio::spawn(async move {
            // Everything this pane will ever show is written by this one task, and this is what
            // says so to the rest of the daemon: while this guard is alive the pane is writing, and
            // the story holds its lines for it (03_session.md §5 (`S35`–`S40`)). Opening the pane counts — a pane
            // being handed six hundred lines of history has not caught up either — and the guard is
            // dropped the moment the record has been written, so the wait is never longer than the
            // writing actually is.
            let opening = Writing::begin(&progress);

            // The session banner, for the window that shows everything.
            //
            // A pane does not get it: the game pane is meant to be game text and the typing pane
            // is not for reading, so each pane gets a one-line `[pane]` note below instead. The
            // banner is still there for anyone who logs in as the player — and it is written here,
            // by the one writer, because a banner racing the record it is meant to sit on top of is
            // how the two orders got mixed in the first place.
            if view == SeatView::All {
                let banner = format!("\r\n{}\r\n", to_crlf_str(&core.banner().await));
                if handle
                    .data(channel_id, CryptoVec::from_slice(banner.as_bytes()))
                    .await
                    .is_err()
                {
                    return;
                }
            }

            // The pane's own one-liner, so an otherwise-empty pane is not a mystery. The
            // game pane has none: a screen starts with what the game said (issue #20).
            if let Some(greeting) = view.greeting(&agent_name) {
                let bytes = block("pane", &greeting, Role::Notice, &palette, core.tags());
                if handle
                    .data(channel_id, CryptoVec::from_slice(&bytes))
                    .await
                    .is_err()
                {
                    return;
                }
            }

            // A screen you sit down in front of shows what is on it, so every pane is given
            // the part of the record it arrived too late for — the same bytes the live stream
            // carries, rendered the same way, so a reconnecting pane is not a different pane
            // (issues #20 and #17).
            //
            // Which record, though, is the view's own business, and there are two of them: the
            // screen, and the conversation (03_session.md §4 (`S25`–`S29`)). The companion's pane was handed the
            // screen's record and filtered by its own view, and filtering game text out of a
            // record that holds nothing but game text leaves nothing — so that pane reconnected
            // to an empty screen, which is the one thing a pane that exists to follow a
            // conversation must not do. Everything else keeps the screen, including the player's
            // own window: the two records cannot be interleaved honestly (neither knows where it
            // sat relative to the other), and a window that shows both would rather have the
            // game's text in order than both in the wrong order.
            //
            // Every other pane, not only the game's, which is what it used to be. Reported from UAT
            // as "the game is not updating": the player was in the everything pane, which gets
            // no replay, so a pane opened while the game was sitting at a prompt showed an
            // empty terminal and stayed that way until the next byte of game output. Nothing
            // was wrong with the game and nothing was wrong with the pane — the pane simply
            // had not been told what was already on the screen. Each view still sees only its
            // own half, because `shows` filters the replay exactly as it filters the stream.
            //
            // One renderer for the whole pane, replay and live stream alike: it is the thing
            // that carries a half-written thought across whichever messages arrive next.
            let missed = caught_up_on(view, conversation, screen);
            let mut renderer = PaneRenderer::default();
            for message in missed {
                if !view.shows(&message) {
                    continue;
                }
                renderer.tags = core.tags();
                // The replay is deliberately *not* paced (03_session.md §5 (`S35`–`S40`)): a pane opening is being
                // told what is already on the screen, not watching words arrive. Pacing six hundred
                // lines would take minutes and tell the player nothing new.
                let bytes = renderer.render(message, &tag, &palette);
                if bytes.is_empty() {
                    continue;
                }
                // The replay leaves the cursor somewhere, and the live loop below decides
                // whether to print a prompt by asking exactly that. Game output often ends
                // mid-line — frotz's `>` prompt has nothing after it — so a pane that got the
                // replay and then a `Ready` would otherwise put `>> ` in the middle of the
                // game's own prompt line. (A lamp needs no exemption here, the way it does in the
                // live loop: neither record holds one — a lamp is not history — so the replay
                // cannot be carrying one.)
                at_line_start.store(bytes.ends_with(b"\n"), Ordering::Relaxed);
                if handle
                    .data(channel_id, CryptoVec::from_slice(&bytes))
                    .await
                    .is_err()
                {
                    return;
                }
            }
            // The record is written: this pane is up to date, and the story may move again.
            drop(opening);

            // The voice's record numbers, counted for this connection: the first utterance it
            // hears is record 1, whatever the session was doing before it arrived.
            let mut seq: u64 = 0;

            while let Some(message) = rx.recv().await {
                // Whatever becomes of this message, this pane stops owing it the moment the
                // iteration ends — including the cases that write nothing at all, because a view
                // filters it out or renders it to an empty byte string. A pane that kept owing a
                // message it had chosen not to show would never be quiet, and the game would wait
                // out its whole patience on every line, for ever. The registry counted this message
                // when it was offered; the guard is the other half of that pair.
                let _settling = Writing::settle(&progress);
                if !view.shows(&message) {
                    continue;
                }
                // One prompt per waiting turn, not one per turn end.
                if matches!(message, ClientMessage::Ready)
                    && !at_line_start.swap(false, Ordering::Relaxed)
                {
                    continue;
                }
                renderer.tags = core.tags();
                let bytes = match view {
                    // The voice is not rendered as a pane: its wire is one framed record per
                    // utterance (08_webui_tts.md §2 (`V6`–`V7`)). Nothing else about this loop
                    // changes — it is a terminal seat, so it is owed what is said, and the relay
                    // holding it must keep reading (`V11`). What it is *not* owed is the pace
                    // (`V9`): a record is for a machine that will speak it rather than a showing to
                    // read, and nothing downstream can act on half of one.
                    SeatView::Voice => {
                        seq += 1;
                        voice_record(&message, seq)
                    }
                    _ => renderer.render(message.clone(), &tag, &palette),
                };
                if bytes.is_empty() {
                    continue;
                }
                // Where the cursor ends up is a property of the bytes, not of the message:
                // `>> ` leaves it mid-line (so the next `ready` is the one that prints a
                // prompt), while every block we build ends on a fresh line. Tracking it
                // here is what lets a command's answer be followed by a prompt without the
                // two colliding or the prompt being swallowed.
                //
                // The escapes are the exception, because they are not lines: the cursor does not
                // move, and one arriving between `ready` and the prompt must not swallow it
                // (03_session.md §7 (`S44`–`S45`)). Which ones those are is `it_moves_the_cursor`'s business.
                if it_moves_the_cursor(&message) {
                    at_line_start.store(bytes.ends_with(b"\n"), Ordering::Relaxed);
                }
                if write_paced(
                    &handle,
                    channel_id,
                    &bytes,
                    is_paced(view, &message),
                    &flush,
                    &core,
                )
                .await
                .is_err()
                {
                    break;
                }
            }
            // This pane's writer has stopped — the socket died, or the client went away. Whatever
            // it was still owing nobody is going to read, and a pane that stayed "not quiet" would
            // hold every line of the session behind it (see `PaneProgress::abandon`).
            progress.abandon();
            debug!(?channel_id, "player forwarder finished");
        });
    }

    /// Remember how big a pane said it was (B2).
    fn remember_the_pane(&mut self, channel: ChannelId, cols: u32, rows: u32) {
        let Some(state) = self.channels.get_mut(&channel) else {
            return;
        };
        state.pane = Some(Pane {
            cols: cols as usize,
            rows: rows as usize,
        });
    }

    /// Put a mid-line edit on the pane, and move the caret if the way it had to be written moved
    /// it (B2).
    ///
    /// One place decides, for both the byte typed and the character erased, because the decision is
    /// about the *row* rather than about the key: three ways to write an edit, chosen by how much
    /// of the row is left and whether the caret can be put back where it was.
    async fn echo_the_edit(&mut self, channel: ChannelId, edit: TheEdit) {
        let row = Row {
            line: &self.input_buffer,
            caret: self.caret,
            prompt: PROMPT_COLS,
            pane: self.channels.get(&channel).and_then(|state| state.pane),
            ours: self.view == SeatView::Input,
        };
        let echo = the_way_to_write_it(&row, edit);
        if echo.the_caret_goes_to_the_end() {
            // The line editor must agree with the pane about where the caret is. It is the whole
            // reason this is one decision and not two: a caret that the screen does not have is
            // the next keystroke echoed in the wrong place.
            self.caret = self.input_buffer.len();
        }
        self.show_here(echo.into_bytes()).await;
    }

    /// Tell the core the player is composing a line, at most once a second.    ///
    /// The companion needs the distinction between a quiet room and a keyboard in use (issue
    /// #42): it asked a question, and somebody is answering it. Sent from the keystroke path and
    /// not from the line path, because a submitted line is already an answer — this is about the
    /// seconds before one exists.
    async fn note_typing(&mut self) {
        let now = std::time::Instant::now();
        if let Some(last) = self.last_typing_notice {
            if now.duration_since(last) < self.config.tuning.typing_notice {
                return;
            }
        }

        self.last_typing_notice = Some(now);
        let Some(seat_id) = self.seat_id else {
            return;
        };

        self.core.send(CoreCommand::PlayerTyping { seat_id }).await;
    }

    /// Tell the core whether the line in the box begins with a space (03_session.md §6 (`S41`–`S43`)).
    ///
    /// Sent when the answer *changes* and never otherwise, so a line costs at most two messages
    /// however fast it is typed — which is the property `note_typing` needs a throttle for, because
    /// that one is about seconds passing. Both are called from the keystroke path, which is the
    /// only place that knows what is in the box before it is submitted.
    async fn note_composing(&mut self) {
        let holds = the_line_holds_the_companion(&self.input_buffer);

        // The player is told, in the one way that cannot disturb what they are writing: an escape
        // on the pane they are typing in, which this program's own page draws as a state and which
        // every other terminal ignores (B3). A line would be the obvious thing and the wrong one —
        // it lands in the middle of the sentence the hold exists to protect.
        //
        // Said after *every* keystroke rather than on the change, because it is a state and not an
        // event: a pane that missed the transitions (the companion connecting halfway through a
        // sentence) is put right by the next character typed, and the page sets the same class
        // again harmlessly.
        //
        // Only while a companion is connected: with nobody to wait, "actions are locked" is a
        // sentence about nothing.
        //
        // Sent as `ClientMessage::Hold` rather than as bytes through `show_here`, so the pane's
        // writer can tell it apart from the player's own characters. An `Echo` is ink coming back
        // where the player put it, and the writer counts those to know whether the cursor is at the
        // start of an empty line — which is what decides whether a prompt is due. Measured live on
        // 2026-09-22: sent as an `Echo`, this cleared that flag on its way past and the typing pane
        // lost its `>> ` on every line submitted, because the hold is written between the newline
        // and the prompt.
        if self.core.agent_online() {
            if let Some(seat_id) = self.seat_id {
                self.core
                    .show_to_seat(seat_id, ClientMessage::Hold { on: holds })
                    .await;
            }
        }

        if self.composing_holds == Some(holds) {
            return;
        }

        self.composing_holds = Some(holds);
        let Some(seat_id) = self.seat_id else {
            return;
        };

        self.core
            .send(CoreCommand::PlayerComposing {
                seat_id,
                starts_with_space: holds,
            })
            .await;
    }

    /// Send one line of input to the core.
    async fn submit_line(&mut self, line: String) {
        let Some(seat_id) = self.seat_id else {
            return;
        };
        self.core
            .send(CoreCommand::PlayerLine { seat_id, line })
            .await;
    }

    /// Ask for the typing pane's prompt on a fresh line (issue #37).
    ///
    /// The prompt means "type here", and this pane owns the line it sits on. It is *asked for* from
    /// the keystroke path — the moment the player's line has been consumed — and it is written by
    /// the pane's one writer, with the ordinary `Ready` rule (a prompt only on an empty line), which
    /// is the only place that knows where the cursor actually is.
    ///
    /// The alternative, which is what was there before, is to let the core send it: the prompt
    /// arrived when the *game* was ready (a framed turn end) or when a `/` command had been
    /// answered, so a second line typed while the companion was still thinking landed with
    /// nothing in front of it, and the prompt that did arrive came after the words. Measured in
    /// the UAT session: two lines out of five.
    ///
    /// Written straight at the socket, this was also the second writer on the pane's screen — the
    /// other half of the interleaving the pace's promise depends on there being none of (§8.2).
    async fn prompt_the_pane(&self) {
        if !self.view.shows(&ClientMessage::Ready) {
            return;
        }
        self.ask_for_a_prompt().await;
    }
}

#[async_trait]
impl server::Handler for SshClientHandler {
    type Error = anyhow::Error;

    async fn auth_password(&mut self, user: &str, password: &str) -> Result<Auth, Self::Error> {
        // The player's own name, or one of the pane names — same password for all of them,
        // because they are the same person looking at one session.
        let known =
            user == self.config.auth_user || SeatView::is_pane_name(user, &self.config.agent_name);
        if known && password == self.config.auth_password {
            self.view = SeatView::for_user(user, &self.config.agent_name);
            info!(user, view = ?self.view, "player authenticated");
            Ok(Auth::Accept)
        } else {
            warn!(user, "rejected a bad password");
            Ok(Auth::Reject {
                proceed_with_methods: None,
            })
        }
    }

    async fn auth_none(&mut self, _user: &str) -> Result<Auth, Self::Error> {
        Ok(Auth::Reject {
            proceed_with_methods: Some(russh::MethodSet::PASSWORD),
        })
    }

    async fn channel_open_session(
        &mut self,
        channel: Channel<Msg>,
        _session: &mut Session,
    ) -> Result<bool, Self::Error> {
        let channel_id = channel.id();
        let (seat_id, receiver) = self
            .core
            .register_seat(
                format!("player-{}", self.player),
                SeatKind::Ssh,
                self.view.accepts_input(),
            )
            .await;
        debug!(seat = seat_id, "player seat registered");
        self.seat_id = Some(seat_id);
        self.channels.insert(
            channel_id,
            ChannelState {
                receiver: Some(receiver),
                at_line_start: Arc::new(AtomicBool::new(true)),
                flush: Arc::new(AtomicBool::new(false)),
                keys: KeyReader::new(),
                hint_shown: false,
                pane: None,
            },
        );
        Ok(true)
    }

    async fn pty_request(
        &mut self,
        channel: ChannelId,
        term: &str,
        col_width: u32,
        row_height: u32,
        _pix_width: u32,
        _pix_height: u32,
        _modes: &[(russh::Pty, u32)],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        // A PTY request is also the moment we learn whether this client is a terminal and
        // what it calls itself, which is what decides colour — and how wide it is, which is what
        // the line editor counts columns with (B2).
        self.palette = Palette::from_env(true, Some(term));
        self.remember_the_pane(channel, col_width, row_height);
        debug!(
            term,
            colours = self.palette.is_enabled(),
            cols = col_width,
            rows = row_height,
            "terminal attached"
        );
        self.start_forwarder(channel, session);
        Ok(())
    }

    async fn window_change_request(
        &mut self,
        channel: ChannelId,
        col_width: u32,
        row_height: u32,
        _pix_width: u32,
        _pix_height: u32,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        // A pane that is resized is a pane whose wraps move. Nothing is redrawn on the strength of
        // it — the arithmetic is only ever used for the edit in front of it — but the next
        // keystroke must count columns in the pane the player now has, not the one they opened.
        self.remember_the_pane(channel, col_width, row_height);
        Ok(())
    }

    async fn shell_request(
        &mut self,
        channel: ChannelId,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        // Some clients request a shell without a pty; make sure they still get the
        // forwarder — and with it the banner — rather than a silent void.
        if self
            .channels
            .get(&channel)
            .map(|state| state.receiver.is_some())
            .unwrap_or(false)
        {
            // No PTY: a pipe or a script, so no colour unless it was asked for.
            self.palette = Palette::from_env(false, None);
            self.start_forwarder(channel, session);
        }
        Ok(())
    }

    async fn data(
        &mut self,
        channel: ChannelId,
        data: &[u8],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        // Read the keys first, in their own borrow, then act on them.
        let events: Vec<KeyEvent> = match self.channels.get_mut(&channel) {
            Some(state) => data.iter().map(|byte| state.keys.feed(*byte)).collect(),
            None => return Ok(()),
        };

        // A pane you read in honours no keys but the ones that end it. Nothing is echoed
        // and nothing is buffered, so leaning on the keyboard while reading the game
        // cannot become something the companion has to make sense of.
        if !self.view.accepts_input() {
            if events
                .iter()
                .any(|event| matches!(event, KeyEvent::Interrupt | KeyEvent::Eof))
            {
                info!(?channel, view = ?self.view, "read-only pane asked to disconnect");
                session.close(channel);
                return Ok(());
            }

            // Trying to *type* here is the moment to say why nothing is happening — a
            // player who presses a letter and sees no echo would reasonably think the
            // pane had frozen. Arrow keys are silent: they mean "scroll", not "broken".
            let typing = events
                .iter()
                .any(|event| matches!(event, KeyEvent::Text(_) | KeyEvent::Enter));
            let first_time = match self.channels.get_mut(&channel) {
                Some(state) => !std::mem::replace(&mut state.hint_shown, true),
                None => false,
            };
            if typing && first_time {
                let hint = block(
                    "pane",
                    "this pane is read only — type in the input pane; ctrl-d disconnects",
                    Role::Notice,
                    &self.palette,
                    self.core.tags(),
                );
                self.show_here(hint).await;
            }
            return Ok(());
        }

        if !events.is_empty() {
            // Somebody is at the keyboard. From here the pane stops metering the message it is in
            // the middle of, so the echo below is written within a word rather than behind a
            // sentence (03_session.md §5 (`S35`–`S40`)).
            self.let_the_screen_stop_scrolling(channel);
        }

        for event in events {
            match event {
                KeyEvent::Enter => {
                    self.show_here(b"\r\n".to_vec()).await;
                    let line = std::mem::take(&mut self.input_buffer);
                    self.caret = 0;
                    // A fresh line with nothing on it: whatever the last one was holding, it is not
                    // holding anything now (03_session.md §6 (`S41`–`S43`)). Submitted with the space still on it, as agreed —
                    // the pause is the line's, and the line has gone.
                    self.note_composing().await;
                    // Asked before the line is handed over, because the answer decides whether
                    // this pane is owed a prompt of its own (issue #37).
                    let answered = the_core_answers_the_line(&line);
                    if !line.trim().is_empty() {
                        self.submit_line(line).await;
                    }
                    if !answered {
                        self.prompt_the_pane().await;
                    }
                }
                KeyEvent::Backspace => {
                    if self.caret > 0 {
                        self.caret -= 1;
                        self.input_buffer.remove(self.caret);
                        if self.caret == self.input_buffer.len() {
                            // At the end of the line, where erasing is the classic one.
                            self.show_here(b"\x08 \x08".to_vec()).await;
                        } else {
                            // With text after the caret the hole has to be closed: the tail comes
                            // back over it, one way or another (B2).
                            self.echo_the_edit(channel, TheEdit::Erased).await;
                        }
                        // Taking the space off the front is how the player lets go (03_session.md §6 (`S41`–`S43`)).
                        self.note_composing().await;
                        self.note_typing().await;
                    }
                }
                KeyEvent::Left => {
                    // A step back is one backspace of the cursor and nothing else: the buffer
                    // holds the player's own characters, so the pane's prompt is never reached
                    // and never needs redrawing.
                    if self.caret > 0 {
                        self.caret -= 1;
                        self.show_here(b"\x08".to_vec()).await;
                    }
                }
                KeyEvent::Right => {
                    // The character the caret is sitting on, echoed because the cursor moves
                    // over it. At the end of the line there is nothing to step onto.
                    let byte = self.input_buffer.as_bytes().get(self.caret).copied();
                    if let Some(byte) = byte {
                        self.caret += 1;
                        self.show_here(vec![byte]).await;
                    }
                }
                KeyEvent::Interrupt => {
                    self.input_buffer.clear();
                    self.caret = 0;
                    self.show_here(b"^C\r\n".to_vec()).await;
                    // A cleared line is a free line: nothing is holding anything (03_session.md §6 (`S41`–`S43`)).
                    self.note_composing().await;
                    // A cleared line is a free line, and the pane says so (issue #37).
                    self.prompt_the_pane().await;
                }
                KeyEvent::Eof => {
                    if self.input_buffer.is_empty() {
                        session.close(channel);
                    }
                }
                // **Tab: the rest of what is being written, now.** Nothing is echoed and nothing is
                // buffered — the key is a request about the screen, not a character — so its only
                // trace is every pane arriving at once (03_session.md §5 (`S38.1`)). The count lives
                // in the core rather than here because the panes are separate connections: this
                // handler can see only its own channel, and the pane that is still arriving is
                // usually somebody else's.
                KeyEvent::SkipAhead => self.core.skip_ahead(),
                KeyEvent::Text(byte) => {
                    if self.caret == self.input_buffer.len() {
                        // The common case, and one byte in for one byte out.
                        self.input_buffer.push(byte as char);
                        self.caret += 1;
                        self.show_here(vec![byte]).await;
                    } else {
                        // In the middle of the line: insert it, then put the tail back over it.
                        // The tail is written from the caret rather than from the start of the
                        // line because the pane's prompt is in front of the player's characters,
                        // and a repaint from column zero would eat the prompt rather than the
                        // line — but a line that has wrapped cannot be written back over itself
                        // a byte at a time, which is why this goes through one decision (B2).
                        self.input_buffer.insert(self.caret, byte as char);
                        self.caret += 1;
                        self.echo_the_edit(channel, TheEdit::Inserted).await;
                    }
                    self.note_composing().await;
                    self.note_typing().await;
                }
                // A control byte, or another byte of an escape sequence: an arrow key we
                // have no use for, a function key, a mouse report. There is nothing to do
                // with it.
                KeyEvent::Ignored => {}
            }
        }
        Ok(())
    }

    async fn channel_close(
        &mut self,
        channel: ChannelId,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.channels.remove(&channel);
        // The same rule the destructor uses, so the two can never report one departure twice.
        if let Some(seat_id) = the_seat_the_connection_owes(&mut self.seat_id) {
            self.core.unregister_seat(seat_id).await;
        }
        Ok(())
    }

    async fn channel_eof(
        &mut self,
        channel: ChannelId,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        debug!(?channel, "player sent EOF");
        Ok(())
    }
}

/// Turn a seat message into the bytes a terminal should show.
///
/// Only lines we build ourselves are coloured. Game bytes are passed straight through, so
/// no escape sequence can ever land inside a game description, and a terminal without
/// colour still reads the game perfectly.
///
/// `tags` is the session's answer to `/tagsoff` (03_session.md §2.1 (`S11`)): off, every line reads as plain
/// text — the same words, the same colours, the same layout, without the `[system]` in front of
/// them. It is an argument here rather than a property of the message because it can change
/// between one line and the next, and the pane is the only place that knows how a line looks.
fn render(message: ClientMessage, companion_tag: &str, palette: &Palette, tags: bool) -> Vec<u8> {
    match message {
        // Game bytes: newline translation only, never colour.
        ClientMessage::GameOutput(bytes) => to_crlf(&bytes),
        ClientMessage::Companion(text) => block(companion_tag, &text, Role::Speech, palette, tags),
        ClientMessage::System(text) => block("system", &text, Role::Notice, palette, tags),
        ClientMessage::Thought(text) => block("think", &text, Role::Thought, palette, tags),
        // A streamed chunk, on its own: the raw words in the thinking colour. The tag and the
        // line break belong to the *first* chunk of a stream, and remembering which that was is
        // the one thing a pane cannot work out from this message alone (see `PaneRenderer`).
        ClientMessage::ThoughtDelta(text) => {
            to_crlf(palette.paint(Role::Thought, &text).as_bytes())
        }
        // The player's own words, under the name of the pane they type in.
        ClientMessage::Player(text) => block(PLAYER_TAG, &text, Role::Player, palette, tags),
        // A command's answer, in the pane that asked. Rendered as a notice rather than as
        // speech: it is the harness answering about the harness, and it must not read as
        // something the companion said (issue #25, iteration 01 §2.3). The tag matches the
        // one the other panes show for the same text, so a command reads the same wherever
        // you happen to be looking.
        ClientMessage::Command(text) => block("system", &text, Role::Notice, palette, tags),
        // Written *without* a leading newline: frotz's `>` prompt is sitting at the end of
        // the last line with no newline after it, so this lands as `>north` — what the
        // player would have seen had frotz echoed its own input. Nothing is appended to it:
        // the screen shows the command, and only the command (issue #20).
        ClientMessage::Input { line, .. } => {
            format!("{}\r\n", palette.paint(Role::Command, &line)).into_bytes()
        }
        // The typing pane's own prompt, which is the only thing it ever shows.
        ClientMessage::Ready => palette.paint(Role::Prompt, PROMPT).into_bytes(),
        // The pane's own bytes, written exactly as they were handed over: a keystroke's echo, the
        // prompt, the hint to a read-only pane. There is nothing to render — the transport that
        // made them knows what the player's own keystrokes look like coming back — and nothing to
        // translate, because a Return's echo is already a carriage return and a newline.
        ClientMessage::Echo(bytes) => bytes,
        // The companion's lamp, as an escape the pane swallows (03_session.md §7 (`S44`–`S45`)).
        //
        // It is an OSC and not a line because there is nothing to read: the page watches it and
        // lights a lamp, and a terminal — which is what most panes are — has no idea it was ever
        // there. The id is a private one and the payload is namespaced, so a terminal that does
        // understand the number (iTerm2 defines this one) still ignores a key it does not know.
        // No newline, deliberately: this must not occupy a line of anyone's scrollback.
        ClientMessage::Lamp { on } => format!(
            "\x1b]{};ic-turn={}\x07",
            LAMP_OSC,
            if on { "start" } else { "end" }
        )
        .into_bytes(),
        // The hold, on the same terms and on a number of its own (03_session.md §7 (`S44`–`S45`)). A second escape
        // rather than a second payload on the lamp's: the two are live at once — a lamp bright
        // while the strip says the companion is held — and one number carrying two keys would
        // invite a reader to think they are the same fact.
        ClientMessage::Hold { on } => hold_escape(on),
    }
}

/// The OSC number the companion's lamp travels on (03_session.md §7 (`S44`–`S45`)).
///
/// Private-use, and the same one the page watches for. Nothing else in this program emits an OSC:
/// the daemon's only other escapes are the SGR colours in `palette`.
pub const LAMP_OSC: u32 = 1337;

/// The OSC number the *hold* travels on (B3).
///
/// A second escape rather than a line, and for the same reason the lamp is one: the player is
/// mid-sentence when this changes, so anything that occupied a line would push their half-written
/// words up the screen — which is the bug, not the fix. The page watches it and shows a state; a
/// plain terminal swallows it and notices nothing, which is the right outcome for a terminal that
/// has no indicator to draw.
pub const HOLD_OSC: u32 = 1338;

/// `ic-hold=on` / `ic-hold=off`, as the page's handler reads it.
fn hold_escape(on: bool) -> Vec<u8> {
    format!(
        "\x1b]{};ic-hold={}\x07",
        HOLD_OSC,
        if on { "on" } else { "off" }
    )
    .into_bytes()
}

/// The record a view is caught up on when it connects, if any.
///
/// Every pane is handed the part of the record it arrived too late for, which is what makes a
/// reconnecting pane not a different pane. The voice is not a pane, and it is the **one view that
/// must never be caught up**: the record holds the speech itself, and the replay renders the live
/// stream's own bytes — so a voice that was caught up would read the whole evening aloud on every
/// reload, and because the two are byte for byte alike, no rule on the client could tell them
/// apart (08_webui_tts.md §2 (`V8`)).
fn caught_up_on(
    view: SeatView,
    conversation: Vec<ClientMessage>,
    screen: Vec<ClientMessage>,
) -> Vec<ClientMessage> {
    match view {
        SeatView::Voice => Vec::new(),
        SeatView::Companion => conversation,
        _ => screen,
    }
}

/// One utterance, as the voice seat's wire carries it: a framed record, then a newline.
///
/// A terminal stream has no record boundary — `Companion` may be several lines, and a trailing
/// newline cannot say whether the prose had one of its own — so the frame is explicit and the text
/// travels whole (08_webui_tts.md §2 (`V7`)). `seq` numbers the records: a relay can tell a repeat
/// from a new one with it, and a gap is a record that was dropped rather than a sentence that was
/// never said.
///
/// `v` is the *wire's* version, and it exists so that the next shape of this record can be told
/// from this one: to a relay that does not know a version it is handed, guessing is the one thing
/// it must not do.
fn voice_record(message: &ClientMessage, seq: u64) -> Vec<u8> {
    let ClientMessage::Companion(text) = message else {
        // Nothing else is spoken. A view that admitted more would have to be given a shape for it
        // here, and admitting nothing else is the point (`V5`).
        return Vec::new();
    };
    let mut bytes = serde_json::json!({
        "v": 1,
        "seq": seq,
        "text": text,
    })
    .to_string()
    .into_bytes();
    bytes.push(b'\n');
    bytes
}

/// A pane's memory of the one thing a single message cannot say about itself: whether a streamed
/// thought is still being written.
///
/// Everything else renders message by message, which is what keeps `render` a pure function the
/// tests can call directly. A stream is the exception — its first chunk carries the block's tag
/// and line break, the chunks after it are just words, and the finished thought that follows is
/// the log's copy of what the player has already watched arrive, so the pane prints the end of
/// the paragraph and nothing else.
struct PaneRenderer {
    thought_open: bool,
    thought_ended_cleanly: bool,
    /// Is this session tagging its lines? (03_session.md §2.1 (`S11`)) On by default, and set from the
    /// session before every message rather than captured at connect: `/tagsoff` is typed while
    /// the pane is open, and it takes effect on the line after it.
    tags: bool,
}

impl Default for PaneRenderer {
    fn default() -> Self {
        Self {
            thought_open: false,
            thought_ended_cleanly: false,
            tags: true,
        }
    }
}

impl PaneRenderer {
    fn render(
        &mut self,
        message: ClientMessage,
        companion_tag: &str,
        palette: &Palette,
    ) -> Vec<u8> {
        match message {
            ClientMessage::ThoughtDelta(text) => {
                let first = !self.thought_open;

                // A first chunk that carries no words does not open a block, and the line break a
                // provider likes to start its reasoning with is dropped rather than shown (B6).
                //
                // Both halves are the same defect seen twice: a tag printed alone on its line reads
                // as a thought that *came back* empty, which is a different thing from a call that
                // has not spoken yet. Nothing is written until there is something to write, and a
                // block opens with a tag rather than with a blank line.
                let text = if first {
                    text.trim_start_matches(['\r', '\n']).to_string()
                } else {
                    text
                };
                if first && text.trim().is_empty() {
                    return Vec::new();
                }

                self.thought_open = true;
                self.thought_ended_cleanly = text.ends_with('\n');

                let body = if first && self.tags {
                    format!("[think] {text}")
                } else {
                    text
                };
                let mut bytes = Vec::new();
                if first {
                    bytes.extend_from_slice(b"\r\n");
                }
                bytes.extend_from_slice(&to_crlf(palette.paint(Role::Thought, &body).as_bytes()));
                bytes
            }

            // The finished thought, at the end of a stream the player has already read.
            ClientMessage::Thought(_) if self.thought_open => {
                self.thought_open = false;
                if self.thought_ended_cleanly {
                    Vec::new()
                } else {
                    b"\r\n".to_vec()
                }
            }

            // A thought that came back with nothing in it: there is nothing to show, so nothing is
            // shown. Not a blank block, and emphatically not a `[think]` with no thought after it —
            // by the time this message exists the call is over, and what it says is that this one
            // had nothing to say (B6).
            ClientMessage::Thought(text) if text.trim().is_empty() => Vec::new(),

            // The turn's work is over, so a block of reasoning still open ends here (B6).
            //
            // A pass called off mid-flight never sends its finished thought, and neither does a call
            // that simply never spoke again. A block left open is worse than a missing one: the next
            // thing written to the pane lands on the same line as a half-finished thought. The lamp
            // going out is the one signal that covers every way a pass can end — it goes out in a
            // `finally`, including when everything in flight was cancelled.
            ClientMessage::Lamp { on: false } if self.thought_open => {
                self.thought_open = false;
                let mut bytes = Vec::new();
                if !self.thought_ended_cleanly {
                    bytes.extend_from_slice(b"\r\n");
                }
                bytes.extend_from_slice(&render(
                    ClientMessage::Lamp { on: false },
                    companion_tag,
                    palette,
                    self.tags,
                ));
                bytes
            }

            other => render(other, companion_tag, palette, self.tags),
        }
    }
}

/// Will the core answer this line with words of its own? (issue #37)
///
/// This is the one thing this layer knows about a line's *content*, and it exists for one
/// reason: a `/` command, and the `.` short form of `/direct`, are answered by the core, and
/// the prompt comes back **after** the answer (`Router::prompt_the_pane`, which is the whole of
/// issue #25). Every other line — a remark to the companion, or a command with nobody to take
/// it — is answered by nobody, so the pane has to lay its own prompt down.
///
/// The conditions mirror `Router::handle_player_line`, including the awkward ones: `.` counts
/// only when something follows it, and not a space or a second dot, so `. . .` is a remark and
/// belongs to the companion.
fn the_core_answers_the_line(line: &str) -> bool {
    let trimmed = line.trim();
    if let Some(rest) = trimmed.strip_prefix('.') {
        if !rest.is_empty() && !rest.starts_with([' ', '.']) {
            return true;
        }
    }
    trimmed.starts_with('/')
}

/// Does the line in the box begin with a space? (03_session.md §6 (`S41`–`S43`))
///
/// A free function so the rule is testable without a session, a terminal or a core: the signal the
/// player holds the companion with is one character of their own line, and it is the *line* that
/// matters rather than the caret — a space typed anywhere but the front is an ordinary space, and a
/// space at the front is somebody saying "wait while I write this".
fn the_line_holds_the_companion(line: &str) -> bool {
    line.starts_with(' ')
}

/// Mark a multi-line core/companion message so it can never be mistaken for game
/// text (§12), keep it on its own lines, and colour the whole block.
///
/// With the tags off (03_session.md §2.1 (`S11`)) the line keeps everything else: it still starts on a fresh
/// line, still has one line of the message per line of the pane, and still carries its role's
/// colour. Only the `[system]` at the front goes.
fn block(tag: &str, text: &str, role: Role, palette: &Palette, tags: bool) -> Vec<u8> {
    let mut out = String::from("\r\n");
    for line in text.lines() {
        let line = if tags {
            format!("[{tag}] {line}")
        } else {
            line.to_string()
        };
        out.push_str(&palette.paint(role, &line));
        out.push_str("\r\n");
    }
    out.into_bytes()
}

/// What to write to the pane to put a byte into the middle of a line: the byte, the text that
/// follows it, and a step back over that text.
///
/// The editing happens in the pane the player is looking at rather than in a line editor of our
/// own, so the arithmetic is explicit: the cursor moves by exactly the bytes written here, and
/// nothing is ever redrawn from column zero, which is what keeps the pane's prompt intact.
fn echo_insert(byte: u8, tail: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(tail.len() * 2 + 1);
    bytes.push(byte);
    bytes.extend_from_slice(tail);
    bytes.extend(std::iter::repeat_n(b'\x08', tail.len()));
    bytes
}

/// What to write to the pane to erase the character before the caret when there is text after
/// it: step back onto it, pull the tail over the hole, blank whatever is left at the end, and
/// step back to where the caret now belongs.
fn echo_erase(tail: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(tail.len() * 2 + 2);
    bytes.push(b'\x08');
    bytes.extend_from_slice(tail);
    bytes.push(b' ');
    bytes.extend(std::iter::repeat_n(b'\x08', tail.len() + 1));
    bytes
}

/// What to write to the pane to put a byte into the middle of a line and leave the caret at the
/// *end* of it: the byte and the text that follows, followed by nothing.
///
/// This is [`echo_insert`] without the steps back, and it is what a pane gets when the caret
/// cannot honestly be put back where it was — a line longer than the pane, or a row that is the
/// story's rather than the player's (B2, [`the_way_to_write_it`]).
fn echo_insert_onward(byte: u8, tail: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(tail.len() + 1);
    bytes.push(byte);
    bytes.extend_from_slice(tail);
    bytes
}

/// The same, for an erase: step back onto the character, pull the tail over the hole, blank what
/// is left at the end, and step back once — to the new end of the line, which is where the caret
/// is left.
fn echo_erase_onward(tail: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(tail.len() + 3);
    bytes.push(b'\x08');
    bytes.extend_from_slice(tail);
    bytes.push(b' ');
    bytes.push(b'\x08');
    bytes
}

/// The prompt a typing pane's line begins after: `>> ` (§4.2).
///
/// Its width is load-bearing for the echo, so the columns the line editor counts and the prompt
/// the pane is shown are the same string: one is `PROMPT.len()` of the other, and neither can drift
/// from the other quietly.
pub const PROMPT: &str = ">> ";

/// The prompt's width in columns, which is what the line editor counts with.
pub const PROMPT_COLS: usize = PROMPT.len();

/// How big a pane said it was (`pty_request`, `window_change_request`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Pane {
    pub cols: usize,
    pub rows: usize,
}

/// An edit to the middle of a line, as the echo has to make sense of it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum TheEdit {
    /// A byte was typed at the caret.
    Inserted,
    /// The character before the caret was erased, and there was text after it.
    Erased,
}

/// What the pane is given to put a mid-line edit right, and where the caret ends up.
#[derive(Debug, PartialEq, Eq)]
enum Echo {
    /// The tail, written and stepped back over: the caret stays where the player put it.
    Stepped(Vec<u8>),
    /// The whole line, written again on the row the caret is on, with the caret put back.
    Again(Vec<u8>),
    /// The tail, written and followed: the caret ends at the end of the line.
    Followed(Vec<u8>),
}

impl Echo {
    /// Does this way of writing it move the caret to the end of the line?
    ///
    /// The pane cannot be asked to put the caret back, so the *line editor* has to agree with the
    /// pane about where the caret now is — otherwise the next keystroke echoes from the wrong
    /// place, which is the bug this whole decision exists to avoid.
    fn the_caret_goes_to_the_end(&self) -> bool {
        matches!(self, Echo::Followed(_))
    }

    fn into_bytes(self) -> Vec<u8> {
        match self {
            Echo::Stepped(bytes) | Echo::Again(bytes) | Echo::Followed(bytes) => bytes,
        }
    }
}

/// The line the player is writing, as the echo needs to think about it: where the caret is on the
/// row it is writing on, and what is left of the row.
struct Row<'a> {
    /// The line as it now stands, after the edit, and where the caret sits in it.
    line: &'a str,
    caret: usize,
    /// The columns in front of the line's own characters — the prompt, on a row that is ours.
    prompt: usize,
    /// What the terminal said about itself, or `None` for a pane that never told us.
    pane: Option<Pane>,
    /// Is this row the player's own?
    ///
    /// The typing pane's is, and only that one: it shows its own prompt and nothing else (§8.2).
    /// A screen's row belongs to the story, and rewriting a row of the story to say the player's
    /// own line is not ours to do.
    ours: bool,
}

impl Row<'_> {
    /// Columns of ink the line has taken, prompt and all.
    fn printed(&self) -> usize {
        self.prompt + self.line.chars().count()
    }

    /// The column the caret sits in, counted from the start of the line.
    fn caret_at(&self) -> usize {
        self.prompt + self.line[..self.caret].chars().count()
    }

    /// How much of the row is left, from the caret's column to the pane's edge.
    fn left_on_the_row(&self, cols: usize) -> usize {
        cols - self.caret_at() % cols
    }

    /// Would the whole line fit on the pane's screen? Only then can it be written again with
    /// anywhere to put the caret back to.
    fn fits_the_screen(&self, pane: Pane) -> bool {
        self.printed() <= pane.cols * pane.rows
    }
}

/// Which way to put a mid-line edit on the pane (B2).
///
/// Three ways, in the order they are preferred:
///
/// 1. **Step back over the tail** — the arithmetic this has always had: write the tail from the
///    caret and step back over it. Correct as long as the whole of it stays on the row the caret is
///    on, which is why it wants to know how much of that row is left. On a pane that never said how
///    wide it was, this is what it gets: it is all that pane can be given, and it is what was
///    always done.
/// 2. **Write the line again** — when the tail would spill onto the row below, the steps back
///    cannot return across the wrap, and the pane is given the whole line from the row it is on
///    with the caret put back by row and column. Only for a line that fits on the screen, because
///    the caret has to end up somewhere the pane can actually show.
/// 3. **Follow the tail** — when it cannot: a line longer than the pane, or a row that is the
///    story's rather than the player's. The tail is written and the caret goes to the end of the
///    line, which is where the pane can honestly put it. The player keeps their line and every
///    character of it stays readable; what is given up is the caret's place in the middle of it.
///
/// The wrong answer to this question was a screen full of duplicated fragments: the tail redrawn
/// onto the row below the one it belonged to, over and over, with the caret's place in the line
/// and the caret's place on the screen drifting apart.
fn the_way_to_write_it(row: &Row, edit: TheEdit) -> Echo {
    let tail = row.line.chars().count() - row.line[..row.caret].chars().count();
    let Some(pane) = row.pane.filter(|pane| pane.cols > 0) else {
        return step_back_over_the_tail(row, edit);
    };

    // The columns the edit will write, and the room the caret has before the pane's edge.
    let (wanted, room) = match edit {
        TheEdit::Inserted => (tail + 1, row.left_on_the_row(pane.cols)),
        TheEdit::Erased => (tail, row.left_on_the_row(pane.cols)),
    };
    if wanted <= room {
        return step_back_over_the_tail(row, edit);
    }
    if row.ours && row.fits_the_screen(pane) {
        return Echo::Again(echo_the_line_again(row, pane));
    }
    Echo::Followed(match edit {
        TheEdit::Inserted => echo_insert_onward(row.line.as_bytes()[row.caret - 1], &the_tail(row)),
        TheEdit::Erased => echo_erase_onward(&the_tail(row)),
    })
}

/// The text after the caret, as bytes.
fn the_tail(row: &Row) -> Vec<u8> {
    row.line.as_bytes()[row.caret.min(row.line.len())..].to_vec()
}

/// Write the tail from the caret and step back over it, which is the arithmetic that needs one row.
fn step_back_over_the_tail(row: &Row, edit: TheEdit) -> Echo {
    Echo::Stepped(match edit {
        TheEdit::Inserted => echo_insert(row.line.as_bytes()[row.caret - 1], &the_tail(row)),
        TheEdit::Erased => echo_erase(&the_tail(row)),
    })
}

/// Write the whole line again on the row it started on, and put the caret back where the player
/// left it (B2).
///
/// The line is written again *where it is*, which is why this needs to know the row it begins on:
/// the cursor is standing in the line, `actual_row` rows below the beginning, so stepping up that
/// far puts it at the line's own first row and the line ends up where it started — prompt still in
/// front of it, and nothing left above to be read as a second copy of itself.
///
/// Every column here is arithmetic rather than a guess about where the line began, because the
/// caller only takes this way when the whole line fits on the pane's screen (see
/// [`the_way_to_write_it`]): the steps up cannot go past the top row, and the caret ends up
/// somewhere the pane can actually show.
fn echo_the_line_again(row: &Row, pane: Pane) -> Vec<u8> {
    let cols = pane.cols;
    let printed = row.printed();
    let caret_at = row.caret_at();

    // Where the cursor is standing: `caret_at` columns into the line — and one row up from that
    // when the caret sits on the row below a full one, which is a wrap the pane has not taken yet.
    // `actual_row` rows above that is the line's own beginning.
    let actual_row = if caret_at == 0 {
        0
    } else {
        (caret_at - 1) / cols
    };
    let mut out = String::new();
    if actual_row > 0 {
        out.push_str(&format!("\x1b[{actual_row}A"));
    }
    out.push_str("\r\x1b[J");
    out.push_str(PROMPT);
    out.push_str(row.line);

    // Where the cursor is now: at the end of the line, on the last row it reached — and *one past*
    // that row when the line ended exactly at the pane's edge, which is where the next character
    // would be printed and therefore where the caret belongs.
    let end_row = if printed == 0 {
        0
    } else {
        (printed - 1) / cols
    };
    let caret_row = caret_at / cols;
    // Steps back in rows, then a column, because a `\b` cannot cross a wrap.
    out.push_str(&match end_row.cmp(&caret_row) {
        std::cmp::Ordering::Greater => format!("\x1b[{}A", end_row - caret_row),
        std::cmp::Ordering::Less => format!("\x1b[{}B", caret_row - end_row),
        std::cmp::Ordering::Equal => String::new(),
    });
    out.push('\r');
    let column = caret_at % cols;
    if column > 0 {
        out.push_str(&format!("\x1b[{}G", column + 1));
    }
    out.into_bytes()
}

/// Write a pane's message, at the pace the session is set to (03_session.md §5 (`S35`–`S40`)).
///
/// Two voices are metered — the game's own text and the companion's words — and everything else
/// arrives whole: a notice, the answer to a `/` command and the prompt in the typing pane are the
/// machine answering you, and the companion's reasoning streams as the model writes it. Pacing the
/// machine would make the harness feel slow rather than the room feel unhurried.
///
/// The ceiling inside `pace::paced` is what stops this from becoming a queue: a turn is delivered in
/// at most the configured number of seconds whatever is in it, the rest arriving at once.
///
/// **A keystroke stops the metering** (`flush`), which is the other half of the pacing being
/// bearable at all: from the moment somebody types, the rest of what is on its way arrives at once
/// and their own echo — which is queued behind this message, because a pane has one writer — is
/// written immediately after it. Without that, the echo would have to wait behind the whole of a
/// slow sentence, and the alternative to waiting is writing it straight at the socket, which is how
/// a command ends up in the middle of a word.
async fn write_paced(
    handle: &russh::server::Handle,
    channel: ChannelId,
    bytes: &[u8],
    paced_message: bool,
    flush: &AtomicBool,
    core: &Core,
) -> Result<(), ()> {
    // The pace is the **session's**, read here rather than handed in: it is read once per message
    // either way, and the core is the one place every pane can see it from.
    let pace = core.pace();
    let honest = if paced_message {
        pace::paced(bytes, pace, core.pace_rates())
    } else {
        vec![(bytes, std::time::Duration::ZERO)]
    };

    if paced_message && honest.len() > 1 {
        // Worth a line: a setting that does nothing is indistinguishable from one that was never
        // read, and the pace is set in a file (or by a slider) rather than in the code.
        debug!(
            pieces = honest.len(),
            pace = pace.name(),
            "showing this at the session's pace"
        );
    }

    // A keystroke that arrived before this message started is still a keystroke: it is answered by
    // delivering this whole message rather than by holding the echo behind it.
    let mut unmetered = flush.swap(false, Ordering::SeqCst);

    // **Where the count stood when this message started.** An ask that happened before it is not a
    // request about *this* message, so only a change underneath it stops the metering — which is the
    // difference between "give me the rest of this" and "everything from here on arrives at once".
    let skips_when_it_started = core.skips();

    for (piece, wait) in honest {
        if handle
            .data(channel, CryptoVec::from_slice(piece))
            .await
            .is_err()
        {
            return Err(());
        }
        if wait.is_zero() || unmetered {
            continue;
        }
        tokio::time::sleep(wait).await;
        if flush.swap(false, Ordering::SeqCst) {
            unmetered = true;
        }
        // Somebody asked for the rest of it while this was being written (03_session.md §5 `S38.1`):
        // every pane stops metering together, which is the whole point of counting the ask where all
        // of them can see it.
        if core.skips() != skips_when_it_started {
            unmetered = true;
        }
    }
    Ok(())
}

/// Is this message metered **for this view**?
///
/// The two voices are the two a person *reads*, which is the whole of what the pace is for
/// (03_session.md §5 (`S36`)). The voice seat is not one of those readers: what it is owed is a framed
/// record on its way to a speaker (08_webui_tts.md §2 (`V4`, `V9`)), and a bridge can act on that only
/// once every byte of it has arrived — so metering it holds the words back from the voice until the
/// reading is over. Measured live on 2026-09-24: the voice began only as the text block finished
/// appearing, because the record the bridge was waiting for was being written a word at a time.
fn is_paced(view: SeatView, message: &ClientMessage) -> bool {
    view != SeatView::Voice
        && matches!(
            message,
            ClientMessage::GameOutput(_) | ClientMessage::Companion(_)
        )
}

/// Does writing this message move the pane's cursor? (03_session.md §7 (`S44`–`S45`))
///
/// An **escape is not a line**: the pane swallows it, the cursor does not move, and one arriving
/// between `ready` and the prompt must not swallow the prompt. Both of them, named one by one
/// rather than inferred from the bytes, because it is the *message* that knows it is not a line and
/// this is a question about the cursor.
///
/// The rule used to be the lamp alone. The hold is the same kind of thing and was added later, and
/// nothing told this function about it — so it cleared the "is the cursor at the start of an empty
/// line" flag on its way past. Measured live on 2026-09-22: with a companion connected, the typing
/// pane lost its `>> ` on every line submitted, because the hold is written between the newline and
/// the prompt.
fn it_moves_the_cursor(message: &ClientMessage) -> bool {
    !matches!(
        message,
        ClientMessage::Lamp { .. } | ClientMessage::Hold { .. }
    )
}

/// `\n` → `\r\n`, without doubling up on `\r` that is already there.
fn to_crlf(bytes: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(bytes.len() + 16);
    let mut previous = 0u8;
    for &byte in bytes {
        if byte == b'\n' && previous != b'\r' {
            out.push(b'\r');
        }
        out.push(byte);
        previous = byte;
    }
    out
}

fn to_crlf_str(text: &str) -> String {
    String::from_utf8_lossy(&to_crlf(text.as_bytes())).into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::palette::Policy;

    /// `render` with the tags on, which is where a session starts. The switch has its own test;
    /// the tests around it are about what a line looks like.
    fn render(message: ClientMessage, companion_tag: &str, palette: &Palette) -> Vec<u8> {
        super::render(message, companion_tag, palette, true)
    }

    #[test]
    fn the_voice_seat_is_told_only_what_is_said() {
        let speech = ClientMessage::Companion("the grate looks loose".into());
        assert!(SeatView::Voice.shows(&speech));

        let not_speech = [
            ClientMessage::Thought("hm".into()),
            ClientMessage::ThoughtDelta("hm".into()),
            ClientMessage::System("a notice".into()),
            ClientMessage::Player("look".into()),
            ClientMessage::Command("a command's answer".into()),
            ClientMessage::GameOutput(b"you are in a cave".to_vec()),
            ClientMessage::Lamp { on: true },
        ];
        for message in &not_speech {
            assert!(
                !SeatView::Voice.shows(message),
                "{message:?} is not the companion speaking"
            );
        }
    }

    #[test]
    fn the_voice_is_a_login_a_subscription_and_not_a_pane() {
        assert_eq!(SeatView::for_user("voice", "Quill"), SeatView::Voice);
        assert_eq!(SeatView::for_user("VOICE", "Quill"), SeatView::Voice);
        assert!(
            SeatView::is_pane_name("voice", "Quill"),
            "the bridge logs in as `voice`, so the name has to be accepted"
        );
        assert!(
            !SeatView::Voice.accepts_input(),
            "there is nothing to type at a subscription"
        );
        assert!(
            SeatView::Voice.greeting("Quill").is_none(),
            "and nothing to read on it either"
        );
    }

    #[test]
    fn the_voice_is_not_metered_while_a_pane_is() {
        let said = ClientMessage::Companion("the grate looks loose".into());
        let written = ClientMessage::GameOutput(b"you are in a cave".to_vec());

        // The two voices a person reads are metered, wherever they are read.
        assert!(is_paced(SeatView::Companion, &said));
        assert!(is_paced(SeatView::Game, &written));
        assert!(is_paced(SeatView::All, &said));

        // The voice is not a reader. Metering it held the words back from the speaker until the text
        // had finished appearing, which is exactly the behaviour this rule exists to prevent.
        assert!(!is_paced(SeatView::Voice, &said));
        assert!(!is_paced(SeatView::Voice, &written));

        // And the machine is not metered for anybody.
        let notice = ClientMessage::System("a notice".into());
        assert!(!is_paced(SeatView::Companion, &notice));
        assert!(!is_paced(SeatView::Voice, &notice));
    }

    #[test]
    fn a_voice_record_is_one_framed_line() {
        let utterance = "the grate looks loose\nworth a try";
        let record = voice_record(&ClientMessage::Companion(utterance.into()), 7);
        assert!(record.ends_with(b"\n"), "a record is written as a line");

        let line = String::from_utf8(record).expect("utf-8");
        assert_eq!(
            line.matches('\n').count(),
            1,
            "one line, one newline: the prose's own newlines travel *escaped* inside the frame, \
             which is what makes the frame a boundary rather than a guess"
        );
        let parsed: serde_json::Value =
            serde_json::from_str(line.trim_end()).expect("a record is json");
        assert_eq!(parsed["text"], utterance);
        assert_eq!(parsed["seq"], 7);
        assert_eq!(parsed["v"], 1, "the relay is told which wire this is");

        assert!(
            voice_record(&ClientMessage::System("a notice".into()), 8).is_empty(),
            "only speech has a record"
        );
    }

    #[test]
    fn the_voice_is_never_caught_up_on_the_record() {
        let said = vec![ClientMessage::Companion("said before you arrived".into())];
        let game = vec![ClientMessage::GameOutput(b"you are in a cave".to_vec())];

        assert!(
            caught_up_on(SeatView::Voice, said.clone(), game.clone()).is_empty(),
            "a subscription is told nothing that was already said"
        );

        let missed = caught_up_on(SeatView::Companion, said, game.clone());
        assert_eq!(missed.len(), 1);
        assert!(
            matches!(missed[0], ClientMessage::Companion(_)),
            "the conversation pane is caught up on the conversation"
        );

        let missed = caught_up_on(SeatView::All, Vec::new(), game);
        assert_eq!(missed.len(), 1);
        assert!(
            matches!(missed[0], ClientMessage::GameOutput(_)),
            "and the player's own window on the screen"
        );
    }

    /// A line of a terminal, near enough to check an edit with: characters, a cursor, and the
    /// bytes that move it. Deliberately dumb — BS steps left, a printable byte lands under the
    /// cursor — because that is all the editing echo relies on. No escape sequences and no
    /// scrolling, since the edits never need either.
    struct FakeLine {
        screen: Vec<u8>,
        cursor: usize,
    }

    impl FakeLine {
        fn new() -> Self {
            Self {
                screen: Vec::new(),
                cursor: 0,
            }
        }

        fn write(&mut self, bytes: &[u8]) {
            for byte in bytes {
                match byte {
                    0x08 => self.cursor = self.cursor.saturating_sub(1),
                    b' '..=b'~' => {
                        if self.cursor < self.screen.len() {
                            self.screen[self.cursor] = *byte;
                        } else {
                            self.screen.push(*byte);
                        }
                        self.cursor += 1;
                    }
                    other => {
                        panic!("the edit path may only write text and backspaces, got {other:#04x}")
                    }
                }
            }
        }

        fn text(&self) -> String {
            String::from_utf8_lossy(&self.screen).into_owned()
        }
    }

    /// The invariant both edits have to keep: the line on the pane reads as the buffer does,
    /// and the caret is where the buffer says it is. Anything past the end of the line is a
    /// blank the player cannot see (that is what the blanking space in `echo_erase` is for), so
    /// only the text up to the end of the buffer is compared.
    fn assert_line_matches(line: &FakeLine, buffer: &str, caret: usize) {
        assert_eq!(
            &line.text()[..buffer.len()],
            buffer,
            "the pane and the buffer have to read the same"
        );
        assert_eq!(
            line.cursor, caret,
            "the caret has to be where the buffer says"
        );
    }

    #[test]
    fn inserting_in_the_middle_of_a_line_leaves_the_caret_after_the_new_character() {
        // "take |lamp" → the caret steps back over "lamp", then "the " goes in. This is the
        // case a player is actually in: a typo noticed halfway through a sentence.
        let mut line = FakeLine::new();
        let mut buffer = String::from("take lamp");
        let mut caret = buffer.len();
        line.write(buffer.as_bytes());

        for _ in 0..4 {
            assert!(caret > 0);
            caret -= 1;
            line.write(&[0x08]);
        }
        assert_line_matches(&line, &buffer, caret);

        for byte in b"the " {
            let tail = buffer.as_bytes()[caret..].to_vec();
            line.write(&echo_insert(*byte, &tail));
            buffer.insert(caret, *byte as char);
            caret += 1;
        }

        assert_eq!(buffer, "take the lamp");
        assert_line_matches(&line, &buffer, caret);
    }

    #[test]
    fn erasing_in_the_middle_of_a_line_pulls_the_tail_back() {
        // A doubled letter corrected in place: "lampp" with the caret at the end of the line,
        // one Left, then Backspace. The tail behind the hole is "p", so it has to be pulled
        // back over it.
        let mut line = FakeLine::new();
        let mut buffer = String::from("take the lampp");
        let mut caret = buffer.len();
        line.write(buffer.as_bytes());

        caret -= 1; // Left
        line.write(&[0x08]);
        assert_line_matches(&line, &buffer, caret);

        caret -= 1; // Backspace: the caret steps back onto the character it erases
        buffer.remove(caret);
        let tail = buffer.as_bytes()[caret..].to_vec();
        line.write(&echo_erase(&tail));

        assert_eq!(buffer, "take the lamp");
        assert_line_matches(&line, &buffer, caret);
    }

    /* --- the echo on a line that has wrapped (B2) ----------------------------------------- */

    /// A pane, near enough to check a wrapped line with.
    ///
    /// A grid, a cursor, and the handful of things an echo is allowed to say to it: text, `\b`,
    /// `\r`, `\n`, and the cursor moves by row and column. Real enough that the invariant can be
    /// stated the way a player would state it — what the pane shows reads like the line, and the
    /// cursor is where the caret is — rather than as a byte string nobody can read.
    ///
    /// The one subtlety it models deliberately is the **pending wrap**: a character written in the
    /// last column leaves the cursor on that row, and the *next* character goes to the row below.
    /// A pane does that, and arithmetic that assumes otherwise is off by one exactly where the
    /// bug was.
    struct FakeScreen {
        cols: usize,
        rows: usize,
        cells: Vec<u8>,
        row: usize,
        col: usize,
        pending: bool,
    }

    impl FakeScreen {
        fn new(cols: usize, rows: usize) -> Self {
            Self {
                cols,
                rows,
                cells: vec![b' '; cols * rows],
                row: 0,
                col: 0,
                pending: false,
            }
        }

        /// A pane showing a line already, with the cursor standing where the caret is — which is
        /// what the pane the echo is written to actually looks like, and therefore the only honest
        /// way to check an edit against it.
        fn showing(cols: usize, rows: usize, text: &str, caret_at: usize) -> Self {
            let mut screen = Self::new(cols, rows);
            screen.write(text.as_bytes());
            // The caret's place is `caret_at` columns into the text: on the row below the one that
            // fills, when the text ends exactly at the pane's edge... which is a wrap the pane has
            // not taken, so it is still standing on the row above, at its last column.
            if caret_at > 0 && caret_at % cols == 0 {
                screen.row = caret_at / cols - 1;
                screen.col = cols - 1;
                screen.pending = true;
            } else {
                screen.row = caret_at / cols;
                screen.col = caret_at % cols;
                screen.pending = false;
            }
            screen
        }

        /// One row down, scrolling if the pane is at the bottom — which is what a pane the player
        /// has filled does.
        fn down(&mut self) {
            if self.row + 1 == self.rows {
                self.cells.drain(0..self.cols);
                self.cells.extend(std::iter::repeat_n(b' ', self.cols));
            } else {
                self.row += 1;
            }
            self.pending = false;
        }

        fn put(&mut self, byte: u8) {
            if self.pending {
                self.down();
                self.col = 0;
            }
            self.cells[self.row * self.cols + self.col] = byte;
            if self.col + 1 == self.cols {
                self.pending = true;
            } else {
                self.col += 1;
            }
        }

        fn write(&mut self, bytes: &[u8]) {
            let mut index = 0;
            while index < bytes.len() {
                match bytes[index] {
                    0x08 => {
                        self.col = self.col.saturating_sub(1);
                        self.pending = false;
                    }
                    b'\r' => {
                        self.col = 0;
                        self.pending = false;
                    }
                    b'\n' => self.down(),
                    0x1b => {
                        index += self.escape(&bytes[index..]);
                        continue;
                    }
                    byte => self.put(byte),
                }
                index += 1;
            }
        }

        /// The escape at the front of `bytes`, and how long it was. Panics on anything this echo
        /// has no business writing, which is itself worth knowing.
        fn escape(&mut self, bytes: &[u8]) -> usize {
            assert_eq!(bytes[0], 0x1b);
            assert_eq!(bytes[1], b'[', "only CSI escapes here");
            let mut index = 2;
            while bytes.get(index).is_some_and(u8::is_ascii_digit) {
                index += 1;
            }
            let number: usize = String::from_utf8_lossy(&bytes[2..index])
                .parse()
                .unwrap_or(1);
            match bytes[index] {
                b'A' => {
                    self.row = self.row.saturating_sub(number);
                    self.pending = false;
                }
                b'B' => {
                    self.row = (self.row + number).min(self.rows - 1);
                    self.pending = false;
                }
                b'G' => {
                    self.col = (number.saturating_sub(1)).min(self.cols - 1);
                    self.pending = false;
                }
                b'J' => {
                    let from = self.row * self.cols + self.col;
                    self.cells[from..].fill(b' ');
                    self.pending = false;
                }
                other => panic!(
                    "an echo wrote a CSI it has no business writing: {}",
                    other as char
                ),
            }
            index + 1
        }

        /// The rows as a reader sees them, right-trimmed.
        fn rows_text(&self) -> Vec<String> {
            self.cells
                .chunks(self.cols)
                .map(|row| String::from_utf8_lossy(row).trim_end().to_string())
                .collect()
        }

        /// Where the cursor is, counted the way the line editor counts: the position the *next*
        /// character goes to.
        fn the_cursor(&self) -> (usize, usize) {
            if self.pending {
                (self.row + 1, 0)
            } else {
                (self.row, self.col)
            }
        }
    }

    /// Build the row the echo is deciding about: the line as the edit left it, in a pane of a
    /// given size.
    fn the_row<'a>(
        line: &'a str,
        caret: usize,
        pane: Option<(usize, usize)>,
        ours: bool,
    ) -> Row<'a> {
        Row {
            line,
            caret,
            prompt: PROMPT_COLS,
            pane: pane.map(|(cols, rows)| Pane { cols, rows }),
            ours,
        }
    }

    /// The invariant every way of writing an edit has to keep, stated the way a player would state
    /// it: **what the pane shows reads like the line, and the cursor is where the caret is.**
    ///
    /// Compared with the line beginning at the top of the pane, because that is where it begins:
    /// the ways of writing an edit that can put the caret back *keep* the line where it is, and one
    /// that moved it would be readable as a second copy of the same line above the first. A line
    /// longer than the pane cannot do that and is checked separately.
    fn assert_the_line_reads_and_the_caret_is_placed(
        screen: &FakeScreen,
        line: &str,
        caret: usize,
    ) {
        let text = format!("{PROMPT}{line}");
        let wanted: Vec<String> = text
            .as_bytes()
            .chunks(screen.cols)
            .map(|row| String::from_utf8_lossy(row).trim_end().to_string())
            .collect();
        let shown = screen.rows_text();
        for (row, want) in wanted.iter().enumerate() {
            assert_eq!(
                &shown[row],
                want,
                "row {row} of the pane is not row {row} of the line\npane:\n{}",
                shown.join("\n")
            );
        }

        let (row, col) = screen.the_cursor();
        assert_eq!(
            (row, col),
            (
                (PROMPT_COLS + caret) / screen.cols,
                (PROMPT_COLS + caret) % screen.cols
            ),
            "the caret has to be where the buffer says\npane:\n{}",
            shown.join("\n")
        );
    }

    #[test]
    fn an_edit_that_stays_on_the_row_steps_back_over_the_tail() {
        // The arithmetic this always had, and it is kept for the case it was right for: a line
        // whose tail fits between the caret and the pane's edge. Nothing is redrawn from the start
        // of the row, so the prompt in front of the line is never touched.
        let row = the_row("take the lamp", 9, Some((80, 24)), true);
        let echo = the_way_to_write_it(&row, TheEdit::Inserted);
        assert!(!echo.the_caret_goes_to_the_end());
        assert_eq!(echo, Echo::Stepped(echo_insert(b' ', b"lamp")));
    }

    #[test]
    fn an_edit_that_would_cross_a_wrap_writes_the_line_again() {
        // The bug: the tail redrawn onto the row below the one it belonged to, so the pane filled
        // up with fragments of the same line and the caret's place in the line and its place on the
        // screen drifted apart. A line that has wrapped is written again from the row it is on, and
        // the caret is put back by row and column rather than by backspace, which cannot cross a
        // wrap.
        let line = "look at the poodle and then walk north along the road";
        let caret = 12;
        let mut buffer = String::from(line);
        buffer.insert(caret, 'X');
        let placed = caret + 1;

        let row = the_row(&buffer, placed, Some((30, 3)), true);
        let echo = the_way_to_write_it(&row, TheEdit::Inserted);
        let bytes = String::from_utf8(echo.into_bytes()).expect("utf-8");
        assert!(
            bytes.starts_with(&format!("\r\x1b[J{PROMPT}{buffer}")),
            "the whole line has to be written again: {bytes:?}"
        );

        // And the pane it is written to reads like the line, with the cursor where the caret is.
        let mut screen =
            FakeScreen::showing(30, 3, &format!("{PROMPT}{line}"), PROMPT_COLS + caret);
        screen.write(bytes.as_bytes());
        assert_eq!(
            buffer,
            "look at the Xpoodle and then walk north along the road"
        );
        assert_the_line_reads_and_the_caret_is_placed(&screen, &buffer, placed);
    }

    #[test]
    fn an_edit_in_the_middle_of_a_wrapped_line_keeps_the_caret_where_it_was() {
        // The case the bug actually came from: an arrow key back into the middle of a long line,
        // then a character typed. The caret stays where the player left it — that is the whole
        // point of writing the line again rather than following the tail.
        let line = "look at the poodle and then walk north along the road";
        let caret = 24;
        let mut buffer = String::from(line);
        buffer.insert(caret, 'X');
        let row = the_row(&buffer, caret + 1, Some((30, 3)), true);
        let bytes = the_way_to_write_it(&row, TheEdit::Inserted).into_bytes();

        let mut screen =
            FakeScreen::showing(30, 3, &format!("{PROMPT}{line}"), PROMPT_COLS + caret);
        screen.write(&bytes);
        assert_eq!(
            buffer,
            "look at the poodle and tXhen walk north along the road"
        );
        assert_the_line_reads_and_the_caret_is_placed(&screen, &buffer, caret + 1);
    }

    #[test]
    fn an_edit_in_the_middle_of_a_wrapped_line_erases_and_stays_put() {
        // The same, for a character taken out: the hole is closed by the tail, and the caret is
        // left where the erasing put it.
        let line = "look at the poodle and then walk north along the road";
        let caret = 24;
        let mut buffer = String::from(line);
        buffer.remove(caret);
        let row = the_row(&buffer, caret, Some((30, 3)), true);
        let bytes = the_way_to_write_it(&row, TheEdit::Erased).into_bytes();

        let mut screen =
            FakeScreen::showing(30, 3, &format!("{PROMPT}{line}"), PROMPT_COLS + caret);
        screen.write(&bytes);
        assert_the_line_reads_and_the_caret_is_placed(&screen, &buffer, caret);
    }

    #[test]
    fn a_line_longer_than_the_pane_follows_the_tail_instead_of_pretending() {
        // There is nowhere on the screen for a caret in the middle of a line longer than the pane
        // itself, so the pane is not asked to pretend there is: the tail is written and the caret
        // goes to the end of the line, where it can be shown and where the next character belongs.
        // The player loses their place in the middle of the line and keeps every character of it.
        let line = "x".repeat(200);
        let caret = 40;
        let mut buffer = String::from(&line);
        buffer.insert(caret, 'X');
        let placed = caret + 1;

        let row = the_row(&buffer, placed, Some((40, 3)), true);
        let echo = the_way_to_write_it(&row, TheEdit::Inserted);
        assert!(echo.the_caret_goes_to_the_end());
        let bytes = match &echo {
            Echo::Followed(bytes) => bytes.clone(),
            other => panic!("a line longer than the pane cannot be written again: {other:?}"),
        };
        assert_eq!(bytes, echo_insert_onward(b'X', line[caret..].as_bytes()));

        // The line on the pane is still the line: the tail is written over the old tail, from the
        // caret on, so nothing in front of the caret moves — and the caret lands on the end.
        let mut screen =
            FakeScreen::showing(40, 3, &format!("{PROMPT}{line}"), PROMPT_COLS + caret);
        screen.write(&bytes);
        let after = format!("{PROMPT}{buffer}");
        let shown = screen.rows_text();
        assert_eq!(
            shown.last().map(|row| row.len()),
            Some(after.len() % 40),
            "the bottom row of the pane has to be the end of the line\npane:\n{}",
            shown.join("\n")
        );
        let (row, col) = screen.the_cursor();
        assert_eq!(
            (row, col),
            (screen.rows - 1, after.len() % screen.cols),
            "the caret belongs at the end of the line, which is the bottom of the pane\npane:\n{}",
            shown.join("\n")
        );
    }

    #[test]
    fn a_screen_row_is_never_written_again() {
        // A screen's row belongs to the story (01_frame.md §3 (`F10`)), and the player's line is not the only
        // thing on it: writing it again would erase whatever the story had printed there. So a row
        // that is not the player's own never gets the rewrite, however long the line is — it gets
        // the tail, followed, which cannot damage anything to the left of the caret.
        let line = "look at the poodle and then walk north along the road";
        let caret = 12;
        let mut buffer = String::from(line);
        buffer.insert(caret, 'X');
        let row = the_row(&buffer, caret + 1, Some((30, 3)), false);
        let echo = the_way_to_write_it(&row, TheEdit::Inserted);
        assert!(echo.the_caret_goes_to_the_end());
        assert!(matches!(echo, Echo::Followed(_)));

        // The same line on the typing pane's own row *is* written again: the difference is whose
        // row it is, and nothing else.
        let ours = the_row(&buffer, caret + 1, Some((30, 3)), true);
        assert!(matches!(
            the_way_to_write_it(&ours, TheEdit::Inserted),
            Echo::Again(_)
        ));
    }

    #[test]
    fn a_pane_that_never_said_its_size_keeps_the_arithmetic_it_had() {
        // A client without a pty — a pipe, a script — never says how wide it is, and never sends an
        // arrow key either. It gets exactly what it always got.
        let line = "look at the poodle and then walk north along the road";
        for pane in [None, Some((0, 0))] {
            let row = the_row(line, 12, pane, true);
            let echo = the_way_to_write_it(&row, TheEdit::Inserted);
            assert!(
                matches!(echo, Echo::Stepped(_)),
                "{pane:?} is a pane that told us nothing: {echo:?}"
            );
        }
    }

    #[test]
    fn the_way_a_line_is_written_again_puts_the_caret_back_row_and_column() {
        // The steps back are by row and column, which is the part that a backspace cannot do: from
        // the end of a wrapped line, a `\b` would stop at the pane's edge and the caret would be
        // left in the wrong column of the wrong row.
        let line = "abcdefghij"; // 10 columns of line, in a pane 8 wide: one full row and a bit
        let row = the_row(line, 2, Some((8, 3)), true);
        let bytes =
            String::from_utf8(echo_the_line_again(&row, Pane { cols: 8, rows: 3 })).expect("utf-8");
        assert_eq!(bytes, "\r\x1b[J>> abcdefghij\x1b[1A\r\x1b[6G");

        // The caret was two characters into the line, so it is two columns past the prompt on the
        // line's first row — where the cursor was standing, which is why nothing steps up first.
        let mut screen = FakeScreen::showing(8, 3, &format!("{PROMPT}{line}"), PROMPT_COLS + 2);
        screen.write(bytes.as_bytes());
        assert_the_line_reads_and_the_caret_is_placed(&screen, line, 2);
    }

    #[test]
    fn a_line_that_ends_exactly_at_the_panes_edge_puts_its_caret_on_the_row_below() {
        // The pending wrap, which is the case the arithmetic is easiest to get wrong: a full row of
        // text leaves the cursor *on* that row, and the caret belongs on the next one.
        let line = "abcde"; // with the prompt, exactly 8 columns — one full row
        let row = the_row(line, line.len(), Some((8, 3)), true);
        let bytes =
            String::from_utf8(echo_the_line_again(&row, Pane { cols: 8, rows: 3 })).expect("utf-8");
        let mut screen =
            FakeScreen::showing(8, 3, &format!("{PROMPT}{line}"), line.len() + PROMPT_COLS);
        screen.write(bytes.as_bytes());
        assert_eq!(screen.the_cursor(), (1, 0));
        assert!(
            screen
                .rows_text()
                .join("")
                .starts_with(&format!("{PROMPT}{line}")),
            "the line has to read as itself\npane:\n{}",
            screen.rows_text().join("\n")
        );
    }

    #[test]
    fn newlines_are_translated_for_a_terminal() {
        assert_eq!(to_crlf(b"a\nb\n"), b"a\r\nb\r\n");
        // Already-CRLF data is left alone.
        assert_eq!(to_crlf(b"a\r\nb"), b"a\r\nb");
        assert_eq!(to_crlf(b"no newline"), b"no newline");
    }

    #[test]
    fn game_output_is_never_decorated() {
        let rendered = render(
            ClientMessage::GameOutput(b"You are in a forest.\n>\n".to_vec()),
            "companion",
            &Palette::plain(),
        );
        let text = String::from_utf8(rendered).unwrap();
        assert!(!text.contains("[companion]"));
        assert!(!text.contains("[system]"));
        assert_eq!(text, "You are in a forest.\r\n>\r\n");
    }

    #[test]
    fn companion_and_system_lines_are_tagged_per_line() {
        let text = String::from_utf8(render(
            ClientMessage::Companion("the grate looks loose\nworth a try".to_string()),
            "companion",
            &Palette::plain(),
        ))
        .unwrap();
        assert_eq!(
            text,
            "\r\n[companion] the grate looks loose\r\n[companion] worth a try\r\n"
        );

        let text = String::from_utf8(render(
            ClientMessage::System("saved advent".to_string()),
            "companion",
            &Palette::plain(),
        ))
        .unwrap();
        assert_eq!(text, "\r\n[system] saved advent\r\n");
    }

    #[test]
    fn the_players_own_words_are_tagged_and_coloured_as_theirs() {
        // In the companion's pane the conversation has two sides, and they must not look
        // alike: `[me] what about the desk?` followed by `[Quill] …`.
        let palette = Palette::from_lookup(Policy::Always, true, Some("xterm"), |_| None);
        let text = String::from_utf8(render(
            ClientMessage::Player("what about the desk?".to_string()),
            "Quill",
            &palette,
        ))
        .unwrap();

        assert!(text.contains("[me] what about the desk?"));
        assert!(text.contains(&palette.start(Role::Player)));
        assert!(
            !text.contains(&palette.start(Role::Speech)),
            "not the companion's colour"
        );
        assert!(
            text.contains(palette.reset()),
            "and it must not bleed into the next line"
        );

        // With colours off it is still legible, just plain.
        let plain = String::from_utf8(render(
            ClientMessage::Player("what about the desk?".to_string()),
            "Quill",
            &Palette::plain(),
        ))
        .unwrap();
        assert_eq!(plain, "\r\n[me] what about the desk?\r\n");
    }

    #[test]
    fn a_named_companion_speaks_under_its_own_name() {
        // The tag is the companion's name when it has one, so the player reads
        // `[Quill]` rather than a category label. System notices keep their own tag,
        // and game text is untouched either way.
        let text = String::from_utf8(render(
            ClientMessage::Companion("the grate looks loose".to_string()),
            "Quill",
            &Palette::plain(),
        ))
        .unwrap();
        assert_eq!(text, "\r\n[Quill] the grate looks loose\r\n");
    }

    #[test]
    fn each_pane_shows_only_its_own_kind_of_message() {
        let game = ClientMessage::GameOutput(b"You are in a valley.\n>".to_vec());
        let speech = ClientMessage::Companion("the grate looks loose".into());
        let notice = ClientMessage::System("saved advent (auto)".into());
        let said = ClientMessage::Player("what about the desk?".into());

        // The game pane is pristine: no companion, no notices, ever — and no conversation
        // either, since the player's words to the companion are not the game.
        assert!(SeatView::Game.shows(&game));
        assert!(!SeatView::Game.shows(&speech));
        assert!(!SeatView::Game.shows(&notice));
        assert!(!SeatView::Game.shows(&said));

        // The companion pane is everything that is not the game, so notices land there
        // rather than interrupting the game's scroll, and the player's own words land there
        // so the pane reads as a conversation rather than as replies to thin air.
        assert!(!SeatView::Companion.shows(&game));
        assert!(SeatView::Companion.shows(&speech));
        assert!(SeatView::Companion.shows(&notice));
        assert!(SeatView::Companion.shows(&said));

        // Commands written into the game are on both panes — the screen shows what was
        // typed, the conversation shows what the companion did — except the machinery,
        // which is the core's own doing and belongs on the screen where it happened.
        let command = ClientMessage::Input {
            line: "north".into(),
            source: InputSource::Agent,
        };
        let checkpoint = ClientMessage::Input {
            line: "save".into(),
            source: InputSource::Scripted,
        };
        assert!(SeatView::Game.shows(&command));
        assert!(SeatView::Game.shows(&checkpoint));
        assert!(SeatView::Companion.shows(&command));
        assert!(!SeatView::Companion.shows(&checkpoint));

        // The typing pane shows nothing: your keystrokes echo where you type them.
        assert!(!SeatView::Input.shows(&game));
        assert!(!SeatView::Input.shows(&speech));
        assert!(!SeatView::Input.shows(&notice));
        assert!(!SeatView::Input.shows(&said));

        // And one window still gets everything, unchanged.
        assert!(SeatView::All.shows(&game));
        assert!(SeatView::All.shows(&speech));
        assert!(SeatView::All.shows(&notice));
        assert!(SeatView::All.shows(&said));
    }

    #[test]
    fn the_lamp_travels_as_an_escape_and_lands_on_no_line() {
        // C2. What the page is told is an OSC, so the pane prints nothing: the light is a fact
        // about the companion, and a reader scrolling back through the evening must not find
        // "working" written down anywhere in it. No newline is part of that — a lamp is not a line,
        // and it must not move the cursor or take one.
        let lit = render(ClientMessage::Lamp { on: true }, "Quill", &Palette::plain());
        assert_eq!(
            String::from_utf8(lit.clone()).expect("utf-8"),
            "\x1b]1337;ic-turn=start\x07"
        );
        let out = render(
            ClientMessage::Lamp { on: false },
            "Quill",
            &Palette::plain(),
        );
        assert_eq!(
            String::from_utf8(out).expect("utf-8"),
            "\x1b]1337;ic-turn=end\x07"
        );

        // The tags are a property of a *line*, and a lamp is not one: `/tagsoff` does not change it.
        let untagged = super::render(
            ClientMessage::Lamp { on: true },
            "Quill",
            &Palette::plain(),
            false,
        );
        assert_eq!(untagged, lit);
    }

    #[test]
    fn the_hold_travels_as_an_escape_on_a_number_of_its_own() {
        // B3. The pane the player is typing in is told that the companion is waiting, and it is
        // told in the only way that cannot damage the line being protected: an escape the terminal
        // swallows. Two things are load-bearing. The number is its own, so a page that knows the
        // lamp cannot mistake one for the other; and there is no newline anywhere in it, because a
        // newline is a line, and this is exactly the moment when taking a line would push the
        // player's half-written sentence up the screen.
        assert_ne!(HOLD_OSC, LAMP_OSC);

        let holding = hold_escape(true);
        assert_eq!(
            String::from_utf8(holding.clone()).expect("utf-8"),
            "\x1b]1338;ic-hold=on\x07"
        );
        let released = hold_escape(false);
        assert_eq!(
            String::from_utf8(released.clone()).expect("utf-8"),
            "\x1b]1338;ic-hold=off\x07"
        );
        for escape in [&holding, &released] {
            assert!(
                !escape.contains(&b'\n') && !escape.contains(&b'\r'),
                "the hold must not take or move a line"
            );
        }
    }

    #[test]
    fn an_escape_is_not_a_line_and_does_not_move_the_cursor() {
        // The bug this exists to stop, measured live on 2026-09-22: with a companion connected, the
        // typing pane lost its `>> ` on every line submitted. The line editor writes the hold
        // between the newline echo and the prompt request — newline, hold, line, prompt — and the
        // pane's writer decides whether a prompt is due by asking whether the cursor is at the start
        // of an empty line. The hold cleared that flag on its way past, so the prompt was dropped
        // *and* the debt consumed, and the pane sat with nowhere to type until the next turn ended.
        // The lamp was exempt from this and the hold was not, and nothing said so.
        assert!(!it_moves_the_cursor(&ClientMessage::Lamp { on: true }));
        assert!(!it_moves_the_cursor(&ClientMessage::Hold { on: false }));

        // Everything else does, the prompt included: `>> ` leaves the cursor mid-line, which is
        // exactly what stops the next `ready` printing a second one.
        assert!(it_moves_the_cursor(&ClientMessage::Ready));
        assert!(it_moves_the_cursor(&ClientMessage::Echo(b"\r\n".to_vec())));
        assert!(it_moves_the_cursor(&ClientMessage::Echo(b"x".to_vec())));
    }

    #[test]
    fn the_hold_renders_as_the_escape_it_always_was() {
        // The message is what the pane's writer needs in order to tell an escape from ink. The
        // bytes are unchanged, so the page's handler and every terminal that ignores the number see
        // exactly what they saw before (03_session.md §7 (`S44`–`S45`)).
        for (on, expected) in [
            (true, "\x1b]1338;ic-hold=on\x07"),
            (false, "\x1b]1338;ic-hold=off\x07"),
        ] {
            assert_eq!(
                String::from_utf8(super::render(
                    ClientMessage::Hold { on },
                    "Quill",
                    &Palette::plain(),
                    true,
                ))
                .expect("utf-8"),
                expected
            );
        }
    }

    #[test]
    fn the_lamp_reaches_the_pane_that_shows_the_conversation_and_not_the_screen() {
        // The lamps are drawn on a monitor's chassis, and the fact they watch is the companion's —
        // so the pane that carries it is the pane that shows the companion. A screen stays a screen
        // (01_frame.md §3 (`F10`)), and the typing pane shows its own prompt and nothing else.
        let lamp = ClientMessage::Lamp { on: true };
        assert!(SeatView::Companion.shows(&lamp));
        assert!(SeatView::All.shows(&lamp));
        assert!(!SeatView::Game.shows(&lamp));
        assert!(!SeatView::Input.shows(&lamp));
    }

    #[test]
    fn a_login_name_picks_the_pane() {
        assert_eq!(SeatView::for_user("game", "Quill"), SeatView::Game);
        assert_eq!(SeatView::for_user("me", "Quill"), SeatView::Input);
        assert_eq!(SeatView::for_user("input", "Quill"), SeatView::Input);
        assert_eq!(
            SeatView::for_user("companion", "Quill"),
            SeatView::Companion
        );
        // The configured name works too, however it is capitalised.
        assert_eq!(SeatView::for_user("quill", "Quill"), SeatView::Companion);
        assert_eq!(SeatView::for_user("QUILL", "Quill"), SeatView::Companion);
        assert_eq!(SeatView::for_user("  game  ", "Quill"), SeatView::Game);

        // Anything else is the player themselves: one window, everything, as before.
        assert_eq!(SeatView::for_user("player", "Quill"), SeatView::All);
        assert_eq!(SeatView::for_user("", "Quill"), SeatView::All);

        assert!(SeatView::is_pane_name("game", "Quill"));
        assert!(SeatView::is_pane_name("quill", "Quill"));
        assert!(
            !SeatView::is_pane_name("player", "Quill"),
            "the player is not a pane"
        );
    }

    #[test]
    fn the_screen_shows_no_chatter_of_ours() {
        // Issue #20: the game pane is a screen, and the first thing on a screen is what
        // the game said — not a note about which pane you are in.
        assert!(
            SeatView::Game.greeting("Quill").is_none(),
            "a screen does not announce itself"
        );
        assert!(SeatView::Companion.greeting("Quill").is_some());
        assert!(SeatView::Input.greeting("Quill").is_some());
        assert!(
            SeatView::All.greeting("Quill").is_none(),
            "a normal window needs no announcement"
        );

        // The pane that explains itself names the companion it is explaining, so a rename does
        // not leave the wrong name sitting at the top of the window you read it in.
        let named = SeatView::Companion.greeting("James").expect("greets");
        assert!(named.contains("James"), "{named}");
    }

    #[test]
    fn the_panes_you_read_in_say_that_they_are_read_only() {
        // The player has to be told how to get out of a pane that ignores the keyboard.
        for view in [SeatView::Game, SeatView::Companion] {
            assert!(!view.accepts_input(), "{view:?} ignores the keyboard");
        }

        // The game pane cannot say so, so it is unit-tested elsewhere that a first attempt
        // at typing prints one hint per connection (the pane policy, protocol §11.2); the
        // companion's pane greets, because it is a pane you have to know how to use.
        let greeting = SeatView::Companion
            .greeting("Quill")
            .expect("the companion pane greets");
        assert!(greeting.contains("ctrl-d"), "{greeting}");
        assert!(greeting.contains("input pane"), "{greeting}");
    }

    #[test]
    fn only_the_panes_you_type_in_accept_the_keyboard() {
        // Two panes are places to read. Leaning on the keyboard while reading the game used
        // to become a line the companion had to interpret.
        assert!(!SeatView::Game.accepts_input());
        assert!(!SeatView::Companion.accepts_input());

        // The player's own window and the typing pane take input as before.
        assert!(SeatView::All.accepts_input());
        assert!(SeatView::Input.accepts_input());
    }

    #[test]
    fn a_space_on_the_front_of_the_line_is_the_players_own_signal() {
        // E5. The *line*, not the caret: a space anywhere else is an ordinary space, and a space at
        // the front is the player saying "wait while I write this". Nothing is killed for it — the
        // hold is taken at the loop, so what waits is the next turn the companion would have taken.
        assert!(the_line_holds_the_companion(" hold on"));
        assert!(the_line_holds_the_companion(" "));
        assert!(!the_line_holds_the_companion("hold on"));
        assert!(!the_line_holds_the_companion(""));
        assert!(
            !the_line_holds_the_companion("\ta tab is not the signal"),
            "one character, and it is a space"
        );
        // A paste or an input method that leaves a space in front is the falsifier in 03_session.md §6 (`S41`–`S43`):
        // the signal is meant to be reached for, never hit. It is reported rather than obeyed by
        // this layer, so the agent can be told to stop holding for a while without a rebuild.
        assert!(the_line_holds_the_companion(" pasted text"));
    }

    #[test]
    fn with_the_tags_off_a_line_is_the_same_line_without_its_prefix() {
        // `/tagsoff` (03_session.md §2.1 (`S11`)): the words, the colours and the layout are untouched, and the
        // `[system]` in front of them goes. Everything the player asked to keep keeps happening.
        let palette = Palette::from_lookup(Policy::Always, true, Some("xterm"), |_| None);

        let speech = String::from_utf8(super::render(
            ClientMessage::Companion("the grate looks loose".to_string()),
            "Quill",
            &palette,
            false,
        ))
        .unwrap();
        assert!(speech.contains("the grate looks loose"), "{speech:?}");
        assert!(!speech.contains("[Quill]"), "{speech:?}");
        assert!(
            speech.contains(&palette.start(Role::Speech)),
            "the colour is not part of the switch: {speech:?}"
        );
        assert!(speech.starts_with("\r\n") && speech.ends_with("\r\n"));

        let notice = String::from_utf8(super::render(
            ClientMessage::System("saved advent".to_string()),
            "Quill",
            &Palette::plain(),
            false,
        ))
        .unwrap();
        assert_eq!(notice, "\r\nsaved advent\r\n");

        let mine = String::from_utf8(super::render(
            ClientMessage::Player("what about the desk?".to_string()),
            "Quill",
            &Palette::plain(),
            false,
        ))
        .unwrap();
        assert_eq!(mine, "\r\nwhat about the desk?\r\n");

        // A multi-line block is still one line per line, and a stream still opens once.
        let block = String::from_utf8(super::render(
            ClientMessage::Companion("one\ntwo".to_string()),
            "Quill",
            &Palette::plain(),
            false,
        ))
        .unwrap();
        assert_eq!(block, "\r\none\r\ntwo\r\n");

        let mut pane = PaneRenderer {
            tags: false,
            ..PaneRenderer::default()
        };
        let first = String::from_utf8(pane.render(
            ClientMessage::ThoughtDelta("The gate is ".into()),
            "Quill",
            &Palette::plain(),
        ))
        .unwrap();
        let second = String::from_utf8(pane.render(
            ClientMessage::ThoughtDelta("locked.".into()),
            "Quill",
            &Palette::plain(),
        ))
        .unwrap();
        assert_eq!(first, "\r\nThe gate is ");
        assert_eq!(second, "locked.");

        // And the prompt the typing pane lives in never had a tag to lose.
        let prompt = String::from_utf8(super::render(
            ClientMessage::Ready,
            "Quill",
            &Palette::plain(),
            false,
        ))
        .unwrap();
        assert_eq!(prompt, ">> ");
    }

    #[test]
    fn companion_blocks_start_on_a_fresh_line() {
        // The game's prompt leaves the cursor at column 0, but a block still opens
        // with a newline so it can never collide with a partial game line.
        let text = String::from_utf8(render(
            ClientMessage::Companion("hi".into()),
            "companion",
            &Palette::plain(),
        ))
        .unwrap();
        assert!(text.starts_with("\r\n"));
        assert!(text.ends_with("\r\n"));
    }

    #[test]
    fn a_command_is_echoed_inline_after_the_prompt() {
        // frotz reads from a pipe and echoes nothing, so without this the game pane shows
        // the game answering questions nobody seems to have asked. No leading newline: the
        // `>` prompt is already sitting at the end of the previous line, so this makes it
        // read as `>north`, exactly as a session would look.
        let text = String::from_utf8(render(
            ClientMessage::Input {
                line: "north".to_string(),
                source: InputSource::Agent,
            },
            "companion",
            &Palette::plain(),
        ))
        .unwrap();

        assert_eq!(text, "north\r\n");
        assert!(!text.starts_with('\r'), "it must land on the prompt line");
    }

    #[test]
    fn a_panes_own_bytes_are_written_exactly_as_they_arrived() {
        // The keystroke echo, the prompt and the read-only hint travel in the pane's own stream so
        // that it has one writer (03_session.md §5 (`S35`–`S40`)). They are already terminal bytes — a Return's echo
        // is a carriage return and a newline, an erase is a backspace and what it overwrites — so
        // nothing is rendered and nothing is translated. Rendering them would put a tag on the
        // player's own typing; translating them would double the carriage return.
        let bytes = render(
            ClientMessage::Echo(b"\x08 \x08".to_vec()),
            "Quill",
            &Palette::plain(),
        );
        assert_eq!(bytes, b"\x08 \x08");

        // And every view shows them, because a message addressed to one pane cannot be filtered by
        // the view that pane happens to be: the read-only panes are exactly the ones whose one-line
        // hint has to arrive.
        for view in [
            SeatView::All,
            SeatView::Game,
            SeatView::Companion,
            SeatView::Input,
        ] {
            assert!(
                view.shows(&ClientMessage::Echo(b"x".to_vec())),
                "{view:?} has to show a pane its own bytes"
            );
        }
    }

    #[test]
    fn a_streamed_thought_opens_with_the_tag_and_then_just_talks() {
        // The first chunk of a stream is the one that opens the block; after that the words
        // arrive as words. This is the whole reason a pane needs to remember anything at all.
        let palette = Palette::plain();
        let mut pane = PaneRenderer::default();

        let first = String::from_utf8(pane.render(
            ClientMessage::ThoughtDelta("The gate is ".into()),
            "Quill",
            &palette,
        ))
        .unwrap();
        let second = String::from_utf8(pane.render(
            ClientMessage::ThoughtDelta("locked.".into()),
            "Quill",
            &palette,
        ))
        .unwrap();

        assert_eq!(first, "\r\n[think] The gate is ");
        assert_eq!(second, "locked.");
    }

    #[test]
    fn the_finished_thought_closes_a_stream_without_saying_it_again() {
        // The finished thought still arrives, because the log and `/think` read it. The pane
        // has already shown every word of it, so all it needs is the end of the paragraph.
        let palette = Palette::plain();
        let mut pane = PaneRenderer::default();

        pane.render(
            ClientMessage::ThoughtDelta("The gate is locked.\n".into()),
            "Quill",
            &palette,
        );
        let close = pane.render(
            ClientMessage::Thought("The gate is locked.".into()),
            "Quill",
            &palette,
        );

        assert!(close.is_empty(), "nothing to add: {close:?}");

        // And one that did not end on a line break gets one, so the next block starts clean.
        let mut half = PaneRenderer::default();
        half.render(
            ClientMessage::ThoughtDelta("mid-sentence".into()),
            "Quill",
            &palette,
        );
        let closed = half.render(
            ClientMessage::Thought("mid-sentence".into()),
            "Quill",
            &palette,
        );
        assert_eq!(String::from_utf8(closed).unwrap(), "\r\n");
    }

    #[test]
    fn a_chunk_with_no_words_does_not_open_a_block() {
        // B6. A provider chunks its reasoning as it likes, and a chunk of nothing but the line break
        // it starts with is ordinary. Opening the block on it printed a tag alone on its line, which
        // reads as a thought that came back empty — and then the next call's block arrives, which is
        // exactly what was reported: an empty thinking block, and then another.
        let palette = Palette::plain();
        let mut pane = PaneRenderer::default();

        for nothing in ["", "\n", "\n\n", " \n "] {
            let bytes = pane.render(
                ClientMessage::ThoughtDelta(nothing.into()),
                "Quill",
                &palette,
            );
            assert!(bytes.is_empty(), "{nothing:?} said nothing: {bytes:?}");
        }

        // The first words are what opens the block, with the blank line the provider sent first
        // dropped rather than shown.
        let first = String::from_utf8(pane.render(
            ClientMessage::ThoughtDelta("\n\nThe gate is ".into()),
            "Quill",
            &palette,
        ))
        .unwrap();
        assert_eq!(first, "\r\n[think] The gate is ");

        // And while a block *is* open, a chunk with nothing in it is passed through untouched: a
        // stream's own line breaks are the reasoning's own, not ours to tidy.
        let inside = pane.render(ClientMessage::ThoughtDelta("\n".into()), "Quill", &palette);
        assert_eq!(String::from_utf8(inside).unwrap(), "\r\n");
    }

    #[test]
    fn a_thought_that_came_back_empty_is_not_a_blank_block() {
        // B6. Forty-one calls in one session produced no reasoning at all, and none of them was an
        // empty thought — but an empty one, if it ever arrives, has nothing to say and takes no line
        // to say it on. A blank line on the pane would read as a thought the player had missed.
        let palette = Palette::plain();
        let bytes = PaneRenderer::default().render(
            ClientMessage::Thought(String::new()),
            "Quill",
            &palette,
        );
        assert!(bytes.is_empty(), "nothing to show: {bytes:?}");
        let blank = PaneRenderer::default().render(
            ClientMessage::Thought("   \n".into()),
            "Quill",
            &palette,
        );
        assert!(blank.is_empty(), "nothing but space: {blank:?}");
    }

    #[test]
    fn the_lamp_going_out_closes_a_thought_that_was_left_open() {
        // B6, the third state: a pass called off mid-flight never sends its finished thought, so
        // nothing closed the block. A block left open is worse than a missing one — the next thing
        // written lands on the same line as the half-finished thought. The lamp going out happens in
        // a `finally`, so it covers every way a pass can end, cancelled ones included.
        let palette = Palette::plain();
        let mut pane = PaneRenderer::default();
        pane.render(
            ClientMessage::ThoughtDelta("the lamp is brass".into()),
            "Quill",
            &palette,
        );

        let out =
            String::from_utf8(pane.render(ClientMessage::Lamp { on: false }, "Quill", &palette))
                .unwrap();
        assert_eq!(out, format!("\r\n\x1b]{};ic-turn=end\x07", LAMP_OSC));

        // The next thing written starts on its own line rather than finishing somebody else's
        // sentence.
        let next = String::from_utf8(pane.render(
            ClientMessage::Companion("Right — let's go.".into()),
            "Quill",
            &palette,
        ))
        .unwrap();
        assert!(next.starts_with("\r\n[Quill]"), "{next:?}");

        // A stream that already ended on a line break needs no new one.
        let mut tidy = PaneRenderer::default();
        tidy.render(
            ClientMessage::ThoughtDelta("the lamp is brass.\n".into()),
            "Quill",
            &palette,
        );
        let out =
            String::from_utf8(tidy.render(ClientMessage::Lamp { on: false }, "Quill", &palette))
                .unwrap();
        assert_eq!(out, format!("\x1b]{};ic-turn=end\x07", LAMP_OSC));
    }

    #[test]
    fn a_thought_that_was_never_streamed_is_still_a_block() {
        // A client that does not stream — the scripted model in the tests, or an agent that
        // failed before its first chunk — behaves exactly as it always did.
        let bytes = PaneRenderer::default().render(
            ClientMessage::Thought("hmm".into()),
            "Quill",
            &Palette::plain(),
        );

        assert_eq!(String::from_utf8(bytes).unwrap(), "\r\n[think] hmm\r\n");
    }

    #[test]
    fn a_forced_command_is_the_command_and_nothing_else() {
        // A screen shows `north`, not `north (you took the keyboard)`: the annotation was
        // ours, not the game's, and the announcement is already in the companion's pane
        // (issue #20).
        let text = String::from_utf8(render(
            ClientMessage::Input {
                line: "north".to_string(),
                source: InputSource::Forced,
            },
            "companion",
            &Palette::plain(),
        ))
        .unwrap();

        assert_eq!(text, "north\r\n");
    }

    #[test]
    fn colours_mark_the_roles_without_touching_the_game() {
        let palette = Palette::from_lookup(Policy::Always, true, Some("xterm"), |_| None);

        let command = String::from_utf8(render(
            ClientMessage::Input {
                line: "north".to_string(),
                source: InputSource::Agent,
            },
            "Quill",
            &palette,
        ))
        .unwrap();
        let speech = String::from_utf8(render(
            ClientMessage::Companion("going north".to_string()),
            "Quill",
            &palette,
        ))
        .unwrap();
        let thought = String::from_utf8(render(
            ClientMessage::Thought("the stream has a direction".to_string()),
            "Quill",
            &palette,
        ))
        .unwrap();

        // Each carries its own colour, and they are all different from each other.
        assert!(command.contains(&palette.start(Role::Command)));
        assert!(speech.contains(&palette.start(Role::Speech)));
        assert!(thought.contains(&palette.start(Role::Thought)));
        assert_ne!(palette.start(Role::Command), palette.start(Role::Speech));
        assert_ne!(palette.start(Role::Speech), palette.start(Role::Thought));

        // Every coloured line is closed, or the rest of the pane would inherit it.
        assert!(command.ends_with("\r\n") && command.contains(palette.reset()));
        assert!(speech.contains(palette.reset()));

        // The game itself is never coloured, even with colours on.
        let game = String::from_utf8(render(
            ClientMessage::GameOutput(b"You are in a valley.\n".to_vec()),
            "Quill",
            &palette,
        ))
        .unwrap();
        assert_eq!(game, "You are in a valley.\r\n");
        assert!(
            !game.contains('\x1b'),
            "game text must never carry an escape"
        );
    }

    #[test]
    fn the_typing_pane_gets_a_prompt_when_the_game_is_ready() {
        let palette = Palette::from_lookup(Policy::Always, true, Some("xterm"), |_| None);

        let ready = String::from_utf8(render(ClientMessage::Ready, "Quill", &palette)).unwrap();
        assert!(ready.contains(">> "));
        assert!(!ready.contains('\n'), "it is a prompt, not a line");

        // With colour off it is still a prompt, just plain.
        let plain =
            String::from_utf8(render(ClientMessage::Ready, "Quill", &Palette::plain())).unwrap();
        assert_eq!(plain, ">> ");
    }

    #[test]
    fn a_line_the_core_answers_brings_its_own_prompt_back() {
        // The core answers these, and `prompt_the_pane` puts the prompt back afterwards
        // (issue #25), so laying a second one down here would strand a prompt above the answer.
        for line in ["/cost", "/help", "  /thinkoff", ".north", ".take lamp"] {
            assert!(
                the_core_answers_the_line(line),
                "{line:?} is the core's to answer"
            );
        }

        // Everything else is answered by nobody, so the pane lays its own prompt down. The last
        // four are the awkward ones from the core's own routing: `.` alone, `. `, `..` and — as
        // this test found — `. north`, which the `/help` text documents as `. <command>` while
        // `Router` accepts only `.north`. Whichever way that is settled, the *pane* is still the
        // one that has to prompt for it, because the core answers it with nothing.
        for line in [
            "",
            "   ",
            "hello there",
            "wait - the trun is rotten",
            ".",
            ". ",
            "..",
            ". north",
            "no/slash",
        ] {
            assert!(
                !the_core_answers_the_line(line),
                "{line:?} is the pane's own to prompt for"
            );
        }
    }

    #[test]
    fn a_seat_is_owed_once_however_the_connection_ends() {
        // Measured 2026-10-01: two SSH seats opened and never closed, because the only hook was
        // `channel_close` — and `russh` calls that only when the client *says* it is going. A client
        // that dies says nothing, so the seat outlived the connection, the core went on thinking
        // somebody was at the keyboard, `player_absent` was never emitted, and the companion kept
        // playing an errand in an empty room. The destructor is the hook that catches every ending,
        // so the rule these two share is the thing worth pinning: the seat is *taken*, once.
        let mut held = Some(42);
        assert_eq!(the_seat_the_connection_owes(&mut held), Some(42));
        assert_eq!(
            the_seat_the_connection_owes(&mut held),
            None,
            "a connection that has given its seat back owes nothing, so a polite goodbye and a \
             destructor cannot both report the same departure"
        );

        // A connection that never got a seat — a rejected login, or a client that opened no
        // session channel — owes nothing at all.
        let mut none = None;
        assert_eq!(the_seat_the_connection_owes(&mut none), None);
    }
}
