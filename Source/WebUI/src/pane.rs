//! One pane: a browser socket on one side, an SSH shell on the other, and nothing in between.
//!
//! Every byte that reaches a screen is rendered by the daemon. This file moves bytes and
//! understands none of them: a text frame is control (a resize), a binary frame is terminal
//! bytes. If it ever needs to know what a message from the daemon *means*, it has stopped
//! being this project.

use std::sync::Arc;

use anyhow::Result;
use axum::extract::ws::{Message, WebSocket};
use futures_util::stream::{SplitSink, SplitStream};
use futures_util::{SinkExt, StreamExt};
use russh::client::{self, Config, Handle};
use russh::{Channel, ChannelMsg};

/// The client half of the daemon's own SSH library.
///
/// Using the same crate on both ends is the point: there is no third-party client to disagree
/// with our server, and when the two ends do differ it is one thing in one repository to fix.
struct Client;

#[async_trait::async_trait]
impl client::Handler for Client {
    type Error = anyhow::Error;

    /// The daemon mints a fresh host key inside the container on every start, so there is
    /// nothing to pin it to. The connection never leaves loopback.
    async fn check_server_key(
        &mut self,
        _server_public_key: &russh::keys::key::PublicKey,
    ) -> Result<bool, Self::Error> {
        Ok(true)
    }
}

/// Where the session is and what it wants.
pub struct SessionSettings {
    pub host: String,
    pub port: u16,
    pub password: String,
}

/// Serve one pane until either end goes away.
pub async fn run(
    socket: WebSocket,
    seat: String,
    cols: u32,
    rows: u32,
    settings: Arc<SessionSettings>,
) {
    let (mut sink, mut stream) = socket.split();

    match open(&seat, cols, rows, &settings).await {
        Ok((handle, mut channel)) => {
            eprintln!("webui: INFO pane {seat} opened ({cols}x{rows})");
            let _ = pump(&mut sink, &mut stream, &mut channel).await;
            let _ = channel.close().await;
            drop(handle);
            eprintln!("webui: INFO pane {seat} closed");
        }
        Err(err) => {
            // An empty screen and a hung one look identical, so the failure is printed where
            // the output would have been — in the notice colour the daemon uses for its own.
            let message = format!(
                "cannot reach the session at {}:{} — {err}",
                settings.host, settings.port
            );
            eprintln!("webui: WARNING {message}");
            let line = format!("\r\n\x1b[33m[webui] {message}\x1b[0m\r\n");
            let _ = sink.send(Message::Binary(line.into_bytes())).await;
        }
    }

    let _ = sink.close().await;
}

/// Open the shell, and hand back the session handle with it.
///
/// The handle is returned rather than dropped because it *is* the connection: the channel
/// borrows a sender that the session's task owns, and letting the handle go closes the pane
/// underneath us.
async fn open(
    seat: &str,
    cols: u32,
    rows: u32,
    settings: &SessionSettings,
) -> Result<(Handle<Client>, Channel<client::Msg>)> {
    let config = Arc::new(Config::default());
    let mut handle =
        client::connect(config, (settings.host.as_str(), settings.port), Client).await?;

    if !handle
        .authenticate_password(seat, settings.password.clone())
        .await?
    {
        anyhow::bail!("the session refused the login `{seat}`");
    }

    let channel = handle.channel_open_session().await?;
    // A pty, so the daemon knows it is talking to a terminal and colours the panes. The
    // terminal's size travels with it; a resize later goes out as a window change.
    channel
        .request_pty(true, "xterm-256color", cols, rows, 0, 0, &[])
        .await?;
    channel.request_shell(true).await?;
    Ok((handle, channel))
}

/// A seat held open to be *read* rather than drawn: the voice's connection (08_webui_tts.md §2).
///
/// The same connection a pane gets — a pty and a shell — because the daemon tells a terminal what
/// it is talking to by the terminal's own shape, and a seat that asked for something else would be
/// a different kind of arrival. What is different is what happens next: nothing is typed into it,
/// nothing is drawn on it, and it is read as *lines*, because the voice seat's wire is one framed
/// record per line and a record split across two SSH packets is ordinary.
pub struct HeldSeat {
    /// The connection itself, held because dropping it closes the seat underneath us.
    #[allow(dead_code)]
    handle: Handle<Client>,
    channel: Channel<client::Msg>,
    /// Bytes read but not yet a whole line.
    pending: Vec<u8>,
}

impl HeldSeat {
    /// The next whole line the daemon wrote, without its newline, or `None` when the seat closed.
    pub async fn next_line(&mut self) -> Option<Vec<u8>> {
        loop {
            if let Some(end) = self.pending.iter().position(|byte| *byte == b'\n') {
                let mut line: Vec<u8> = self.pending.drain(..=end).collect();
                // The newline is the frame's rather than the record's: the record itself is
                // json, so anything the prose contained arrives escaped inside it.
                line.pop();
                if line.last() == Some(&b'\r') {
                    line.pop();
                }
                return Some(line);
            }
            match self.channel.wait().await {
                Some(ChannelMsg::Data { data }) | Some(ChannelMsg::ExtendedData { data, .. }) => {
                    self.pending.extend_from_slice(&data);
                }
                Some(ChannelMsg::Eof) | Some(ChannelMsg::Close) | None => return None,
                _ => {}
            }
        }
    }

    /// Say goodbye on the way out, so the daemon unregisters the seat rather than waiting for the
    /// socket to die.
    pub async fn close(self) {
        let _ = self.channel.close().await;
    }
}

/// Hold the voice's seat open, and hand back something to read lines from.
pub async fn hold(seat: &str, settings: &SessionSettings) -> Result<HeldSeat> {
    let (handle, channel) = open(seat, 80, 24, settings).await?;
    Ok(HeldSeat {
        handle,
        channel,
        pending: Vec::new(),
    })
}

/// Type one line into a pane, and close: the whole of what a page can ask a session for.
///
/// The games page's *generate* button has no route into a session except the one everything else
/// uses — a command typed at a pane (07_webui.md §5 (`W18`–`W25`)) — and the command is the daemon's, so the bridge
/// does not have to understand it. A pty is requested for the same reason a pane has one: the
/// daemon reads a line from a terminal, and a line from anything else is a different kind of
/// arrival.
///
/// The pane stays open until the daemon has read the line and answered, and then goes away. Nobody
/// sees the answer: it belongs to the pane that asked, and that pane is a browser tab that has
/// already been told to come back later.
pub async fn type_a_line(
    settings: &SessionSettings,
    seat: &str,
    line: &str,
    settle: std::time::Duration,
) -> Result<()> {
    let (handle, channel) = open(seat, 80, 24, settings).await?;

    let mut bytes = line.as_bytes().to_vec();
    bytes.push(b'\n');
    channel.data(&bytes[..]).await?;
    // One window change is the cheapest way to know the daemon has taken it: the request is
    // answered only once the channel is up. Then a moment for the line to be read, because a
    // channel closed in the same breath loses what was written into it.
    channel.window_change(80, 24, 0, 0).await?;
    tokio::time::sleep(settle).await;
    let _ = channel.close().await;
    drop(handle);
    Ok(())
}

/// Move bytes whichever way they are going, until one end stops.
///
/// One task with a `select!` rather than two: a single loop means a single writer on each
/// side, so no frame can be interleaved with another and there is no lock to get wrong.
async fn pump(
    sink: &mut SplitSink<WebSocket, Message>,
    stream: &mut SplitStream<WebSocket>,
    channel: &mut Channel<client::Msg>,
) -> Result<()> {
    loop {
        tokio::select! {
            from_daemon = channel.wait() => match from_daemon {
                Some(ChannelMsg::Data { data })
                | Some(ChannelMsg::ExtendedData { data, .. }) => {
                    if sink.send(Message::Binary(data.to_vec())).await.is_err() {
                        return Ok(());
                    }
                }
                Some(ChannelMsg::Eof) | Some(ChannelMsg::Close) | None => return Ok(()),
                _ => {}
            },

            from_browser = stream.next() => match from_browser {
                Some(Ok(Message::Binary(bytes))) => {
                    channel.data(&bytes[..]).await?;
                }
                Some(Ok(Message::Text(text))) => control(channel, &text).await,
                // Pings are answered by the frame layer; a pong is somebody else's answer.
                Some(Ok(Message::Ping(_))) | Some(Ok(Message::Pong(_))) => {}
                Some(Ok(Message::Close(_))) | None | Some(Err(_)) => return Ok(()),
            },
        }
    }
}

/// Text frames are control, and the only control there is is a resize.
async fn control(channel: &Channel<client::Msg>, text: &str) {
    let Ok(message) = serde_json::from_str::<serde_json::Value>(text) else {
        return;
    };
    let Some(size) = message.get("resize").and_then(|value| value.as_array()) else {
        return;
    };
    if size.len() != 2 {
        return;
    }
    let (Some(cols), Some(rows)) = (size[0].as_u64(), size[1].as_u64()) else {
        return;
    };
    let _ = channel.window_change(cols as u32, rows as u32, 0, 0).await;
}
