//! The frotz process.
//!
//! # Why pipes rather than a PTY
//!
//! The original design (and the architecture diagram in the spec) assumed a PTY.
//! Measuring real `dfrotz` 2.54 behaviour changed that decision, and the change is
//! deliberate and evidence-backed:
//!
//! * **The PTY echoes input.** With a PTY, everything the core writes to frotz is
//!   echoed back into the readable stream, so the agent would see its own commands
//!   interleaved with the game's output and the turn framer would have to filter
//!   them out. Over pipes there is no echo at all.
//! * **The PTY rewrites newlines.** Terminal output processing (`ONLCR`) turns
//!   every `\n` into `\r\n`, which would put carriage returns into every protocol
//!   string. Over pipes the byte stream is exactly what frotz printed.
//! * **The PTY needs termios surgery.** Fixing the two problems above means
//!   clearing `ECHO`/`OPOST` on the slave device via `ptsname` + `tcsetattr`,
//!   which is real complexity guarding against a problem that simply does not
//!   exist without a PTY.
//! * **A PTY is not needed for interactivity.** Traffic is line-at-a-time by
//!   design (§2.5), frotz's dumb interface is built for redirected I/O, and it
//!   flushes per turn over a pipe (measured). The `>` prompt arrives immediately.
//!
//! So: plain `stdin`/`stdout` pipes. `G9` has the full note.
//!
//! # Flags
//!
//! * `-m` disables `***MORE***` paging. Mandatory: at a MORE prompt frotz consumes
//!   the *next line* of our input as a keypress, which would silently eat commands.
//! * `-q` silences frotz's own startup chatter, so the first framed turn is the
//!   game's intro rather than interpreter noise.
//! * `-p` forces plain ASCII output. Belt and braces: it guarantees no escape
//!   sequence can ever enter the event stream regardless of `TERM`.
//! * `-L <file>` restores a saved game at startup. Note that `-R` is *not* restore
//!   — it is a restricted read/write path — and `-L` on a missing file is fatal,
//!   so the caller must only pass a checkpoint that exists.
//! * `-T` asks dFrotzTooled for the tool channel: events, one JSON object per line,
//!   on **stderr**, and commands on **stdin** in the `\`-command namespace the dumb
//!   interface already had (02_game_boundary.md §2 (`G5`–`G7`), `tool.rs`). A stock `dfrotz` rejects the
//!   flag outright, which is exactly how `supports_tools` finds out — the core runs
//!   beside an unmodified interpreter with no special case in the game path.

use anyhow::{Context, Result};
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::time::Duration;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
use tokio::process::{Child, ChildStdin, Command};
use tokio::sync::mpsc;
use tracing::{debug, info, warn};

use crate::tool::{self, Event as ToolEvent};

/// Debian's `frotz` package installs the dumb interface here; the daemon's
/// default is set in `main.rs` and can be overridden with `INFOCOMPANION_DFROTZ`.
const BASE_ARGS: [&str; 3] = ["-m", "-q", "-p"];
/// Everything needed to start one frotz.
#[derive(Debug, Clone)]
pub struct FrotzConfig {
    /// The interpreter binary.
    pub program: PathBuf,
    /// The story file.
    pub game: PathBuf,
    /// How long a polite exit (stdin EOF) is given before the process is killed.
    pub exit_grace: Duration,
    /// Optional checkpoint to restore at startup (`-L`).
    pub restore: Option<PathBuf>,
    /// Ask for the tool channel (`-T`).
    ///
    /// Only ever set on an interpreter that has said it understands the flag (see
    /// `supports_tools`): passing it to a stock `dfrotz` makes the interpreter print its
    /// usage and exit, so this is a decision, not a hope.
    pub tools: bool,
    /// The only directory frotz may read or write through a filename prompt (`-R`).
    ///
    /// The manual recommends this "if Frotz is run in a restricted environment, by a front
    /// end, or by a **chatbot**" — which is us, three times over. Without it, a filename the
    /// story or the companion invents is opened wherever it points.
    ///
    /// It intercepts `os_read_file_name`, and it works by **replacing the whole path with
    /// `<restricted>/<basename>`**, not by checking it. So a full path typed at the save
    /// prompt does not land where it says: it lands in the restricted directory under its own
    /// last component. `checkpoint_stages` therefore types the basename, and this is the reason.
    pub restricted: Option<PathBuf>,
}

impl FrotzConfig {
    /// The command line frotz will be started with, for logging.
    pub fn command_line(&self) -> String {
        let mut parts: Vec<String> = vec![self.program.display().to_string()];
        parts.extend(BASE_ARGS.iter().map(|s| s.to_string()));
        if self.restricted.is_some() || self.restore.is_some() || self.tools {
            // Argument order is part of the interface: `-R` and `-L` are read before the
            // story file, and `-T` is a flag with nothing after it.
        }
        if let Some(restricted) = &self.restricted {
            parts.push("-R".into());
            parts.push(restricted.display().to_string());
        }
        if let Some(restore) = &self.restore {
            parts.push("-L".into());
            parts.push(restore.display().to_string());
        }
        if self.tools {
            parts.push("-T".into());
        }
        parts.push(self.game.display().to_string());
        parts.join(" ")
    }
}

/// Events coming off a running frotz.
#[derive(Debug)]
pub enum FrotzEvent {
    /// Bytes frotz wrote to stdout.
    Data(Vec<u8>),
    /// One line off the tool channel (stderr). Only ever sent when `tools` was asked for.
    Tool(ToolEvent),
    /// stdout hit EOF: frotz has finished or died.
    Eof,
}

/// Does this interpreter understand `-T`?
///
/// Asked rather than assumed, because the flag is an addition to the interpreter's option
/// string and a stock `dfrotz` answers an unknown option by printing its usage and exiting
/// non-zero. `-v` prints the version and exits successfully in every build, so `-T -v` is a
/// one-process question with a clear answer — and the answer decides whether the daemon has
/// a tool channel or the framing it has always had. A missing or unrunnable interpreter is
/// not an error here: it becomes one when the game is started, with the command line in the
/// message.
pub async fn supports_tools(program: &Path) -> bool {
    match Command::new(program).arg("-T").arg("-v").output().await {
        Ok(output) => output.status.success(),
        Err(err) => {
            debug!(interpreter = %program.display(), error = %err, "could not ask about the tool channel");
            false
        }
    }
}

/// A running frotz process. The stdout stream is handed back separately so the
/// owner can hold it as a plain field (which keeps the actor's `select!` loop free
/// of borrow conflicts).
pub struct Frotz {
    child: Child,
    stdin: ChildStdin,
    /// How long a polite exit is given before the process is killed (`exit_grace_ms`).
    exit_grace: Duration,
}

impl Frotz {
    /// Start frotz and begin pumping its stdout into an event stream.
    pub async fn spawn(cfg: &FrotzConfig) -> Result<(Self, mpsc::Receiver<FrotzEvent>)> {
        let mut cmd = Command::new(&cfg.program);
        cmd.args(BASE_ARGS);
        if let Some(restricted) = &cfg.restricted {
            cmd.arg("-R").arg(restricted);
        }
        if let Some(restore) = &cfg.restore {
            cmd.arg("-L").arg(restore);
        }
        if cfg.tools {
            cmd.arg("-T");
        }
        cmd.arg(&cfg.game);
        cmd.stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);

        let mut child = cmd.spawn().with_context(|| {
            format!(
                "failed to start frotz: {} (is the interpreter installed?)",
                cfg.command_line()
            )
        })?;

        let stdin = child.stdin.take().context("frotz stdin was not captured")?;
        let stdout = child
            .stdout
            .take()
            .context("frotz stdout was not captured")?;
        let stderr = child
            .stderr
            .take()
            .context("frotz stderr was not captured")?;

        info!(command = %cfg.command_line(), "started frotz");

        let (tx, events) = mpsc::channel::<FrotzEvent>(256);

        // stdout pump: raw chunks, so the framer sees exactly what frotz wrote.
        let stdout_tx = tx.clone();
        tokio::spawn(async move {
            let mut reader = stdout;
            let mut buf = vec![0u8; 4096];
            loop {
                match reader.read(&mut buf).await {
                    Ok(0) => {
                        let _ = stdout_tx.send(FrotzEvent::Eof).await;
                        break;
                    }
                    Ok(n) => {
                        if stdout_tx
                            .send(FrotzEvent::Data(buf[..n].to_vec()))
                            .await
                            .is_err()
                        {
                            break;
                        }
                    }
                    Err(err) => {
                        debug!(error = %err, "frotz stdout read ended");
                        let _ = stdout_tx.send(FrotzEvent::Eof).await;
                        break;
                    }
                }
            }
        });

        // stderr pump. With the tool channel it carries frames -- one JSON object per line
        // -- and anything that is not a frame is a diagnostic, because that is what this
        // stream was before. Without the channel it is exactly what it always was: the
        // interpreter's own complaints, which belong in the daemon's log and never on the
        // game's stdout stream (§6.1).
        let tool_tx = tx.clone();
        let tools = cfg.tools;
        tokio::spawn(async move {
            let mut lines = BufReader::new(stderr).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                if line.trim().is_empty() {
                    continue;
                }
                if !tools {
                    warn!(target: "frotz", "{}", line.trim_end());
                    continue;
                }
                let event = tool::parse_line(&line);
                if let ToolEvent::Diagnostic(text) = &event {
                    // A line that is not a frame. Keep it in the log where it has always
                    // been; the game path never sees it.
                    warn!(target: "frotz", "{}", text.trim_end());
                    continue;
                }
                if tool_tx.send(FrotzEvent::Tool(event)).await.is_err() {
                    break;
                }
            }
        });

        Ok((
            Self {
                child,
                stdin,
                exit_grace: cfg.exit_grace,
            },
            events,
        ))
    }

    /// Write one complete line to frotz (§6.6: a full line, one unit).
    pub async fn write_line(&mut self, line: &str) -> Result<()> {
        self.write_raw_line(line)
            .await
            .context("failed to write to frotz")
    }

    /// Write one tool command (03_session.md §9–§10 (`S58`–`S76`)).
    ///
    /// It is a line like any other, and it goes to the same place: the interface
    /// intercepts `\`-commands before the story can see them, so a tool call costs no
    /// descriptor, no protocol and no new failure mode — and it never becomes a turn.
    pub async fn write_tool_command(&mut self, command: &str) -> Result<()> {
        self.write_raw_line(command)
            .await
            .context("failed to write a tool command to frotz")
    }

    async fn write_raw_line(&mut self, line: &str) -> Result<()> {
        let mut data = Vec::with_capacity(line.len() + 1);
        data.extend_from_slice(line.as_bytes());
        data.push(b'\n');
        self.stdin
            .write_all(&data)
            .await
            .context("failed to write to frotz")?;
        self.stdin
            .flush()
            .await
            .context("failed to flush frotz stdin")?;
        Ok(())
    }

    /// Close stdin (polite EOF), then kill if frotz does not go quietly.
    pub async fn shutdown(mut self) -> Option<i32> {
        let _ = self.stdin.shutdown().await;
        match tokio::time::timeout(self.exit_grace, self.child.wait()).await {
            Ok(Ok(status)) => status.code(),
            _ => {
                let _ = self.child.kill().await;
                None
            }
        }
    }

    /// Kill frotz immediately (used when switching games or resetting).
    pub async fn kill(&mut self) -> Option<i32> {
        let _ = self.child.kill().await;
        match self.child.wait().await {
            Ok(status) => status.code(),
            Err(_) => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The shipped exit grace. The tests are not about this number, so they take the default
    /// rather than inventing one that would drift from `core-config.json`.
    fn grace() -> Duration {
        Duration::from_millis(crate::config::DEFAULT_EXIT_GRACE_MS)
    }

    #[test]
    fn command_line_includes_the_restore_flag_only_when_asked() {
        let base = FrotzConfig {
            program: PathBuf::from("/usr/games/dfrotz"),
            game: PathBuf::from("/games/advent.z5"),
            exit_grace: grace(),
            restore: None,
            restricted: None,
            tools: false,
        };
        assert_eq!(
            base.command_line(),
            "/usr/games/dfrotz -m -q -p /games/advent.z5"
        );

        let restored = FrotzConfig {
            restore: Some(PathBuf::from("/state/saves/advent.sav")),
            ..base.clone()
        };
        assert_eq!(
            restored.command_line(),
            "/usr/games/dfrotz -m -q -p -L /state/saves/advent.sav /games/advent.z5"
        );

        // The restricted directory, and the order it appears in: before `-L`, because that is
        // how the arguments are assembled and a test that only checked "contains -R" would not
        // notice if it moved somewhere frotz reads differently.
        let restricted = FrotzConfig {
            restricted: Some(PathBuf::from("/state/saves")),
            ..base
        };
        assert_eq!(
            restricted.command_line(),
            "/usr/games/dfrotz -m -q -p -R /state/saves /games/advent.z5"
        );
    }

    #[test]
    fn the_tool_flag_comes_last_before_the_story() {
        // A flag with nothing after it, so the story file stays the only free argument:
        // getopt reads `-T` and the next free word is the story, which is what makes
        // `-T` safe to add to an interface that never had it.
        let tools = FrotzConfig {
            program: PathBuf::from("/usr/games/dfrotz"),
            game: PathBuf::from("/games/advent.z5"),
            exit_grace: grace(),
            restore: None,
            restricted: Some(PathBuf::from("/state/saves/advent")),
            tools: true,
        };
        assert_eq!(
            tools.command_line(),
            "/usr/games/dfrotz -m -q -p -R /state/saves/advent -T /games/advent.z5"
        );
    }

    #[tokio::test]
    async fn missing_interpreter_fails_with_a_readable_error() {
        let cfg = FrotzConfig {
            program: PathBuf::from("/definitely/not/dfrotz"),
            game: PathBuf::from("/games/advent.z5"),
            exit_grace: grace(),
            restore: None,
            restricted: None,
            tools: false,
        };
        let err = Frotz::spawn(&cfg).await.err().expect("spawn must fail");
        let msg = format!("{err:#}");
        assert!(msg.contains("failed to start frotz"), "{msg}");
    }
}
