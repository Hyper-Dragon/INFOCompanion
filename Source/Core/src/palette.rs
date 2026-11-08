//! Colours for the terminal seats (§iteration 01).
//!
//! The panes only work if you can tell at a glance who is speaking: the game's text, the
//! companion's words, its commands, its reasoning, and the core's notices. That is a job
//! for colour, and colour is a job for the *core* here, because frotz is run in plain-ASCII
//! mode (`-p`) so the game itself never emits an escape sequence. We own the escapes, which
//! is exactly why using them is safe.
//!
//! Two rules keep it from becoming a liability:
//!
//! * **Game bytes are never wrapped.** Only whole lines we generate ourselves are coloured,
//!   so a game description can never be cut in half by an escape sequence, and a terminal
//!   that does not understand colour still shows the game perfectly.
//! * **Colours are configuration, not code.** Each role has an environment variable, so
//!   changing the palette is a `.env` edit rather than a rebuild.
//!
//! Values are ANSI SGR parameters (`36`, `1;33`, `90`), validated on the way in: anything
//! that is not digits and semicolons is ignored, so a typo cannot smuggle arbitrary escape
//! sequences onto someone's terminal. `off` (or `none`) switches a role's colour off.

/// What a piece of output *is*, for colouring purposes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Role {
    /// A game command the core wrote to frotz: the companion's, or the player's forced one.
    Command,
    /// The companion speaking to the player.
    Speech,
    /// The companion's reasoning, shown by `/think`.
    Thought,
    /// A core notice: checkpoints, connection events, `/status`.
    Notice,
    /// The input prompt in the typing pane.
    Prompt,
    /// The player's own words, shown in the companion's pane so the conversation reads
    /// as a conversation rather than as replies to things nobody appears to have said.
    Player,
}

impl Role {
    /// The environment variable that sets this role's colour.
    pub fn env_var(self) -> &'static str {
        match self {
            Role::Command => "INFOCOMPANION_COLOR_COMMAND",
            Role::Speech => "INFOCOMPANION_COLOR_SPEECH",
            Role::Thought => "INFOCOMPANION_COLOR_THOUGHT",
            Role::Notice => "INFOCOMPANION_COLOR_NOTICE",
            Role::Prompt => "INFOCOMPANION_COLOR_PROMPT",
            Role::Player => "INFOCOMPANION_COLOR_PLAYER",
        }
    }

    /// The default SGR parameters: legible on a dark terminal, and distinct enough to tell
    /// apart at a glance without being a christmas tree.
    ///
    /// Retuned for the three surfaces (03_session.md §2 (`S4`–`S11`)). The game screen is a green phosphor tube
    /// in the page, and the two roles that land on it have to live with that: a command echo is
    /// bright cyan, which stands off a green tube the way it stands off anything, and the prompt
    /// has left the green family altogether so that the strip you type in is not the game's
    /// colour. The companion's own roles — its voice, its reasoning, and what the player said
    /// back — are unchanged: they were already the amber machine the page is built from.
    fn default_sgr(self) -> &'static str {
        match self {
            // Bright cyan: a command is something *someone did*, standing out from the game's
            // plain text without competing with it — and legible on a green tube.
            Role::Command => "1;36",
            // Bright white: the voice you are meant to read.
            Role::Speech => "1;37",
            // Dim grey: reasoning is a peephole, so it should read as background.
            Role::Thought => "90",
            // Yellow: notices are the system talking, and are worth noticing.
            Role::Notice => "33",
            // Bright blue: your turn. Not the game's green, because this prompt lives in the
            // strip you type in rather than on the screen you read, and the two are different
            // machines (03_session.md §2 (`S4`–`S11`)).
            Role::Prompt => "1;34",
            // Bright magenta: the player's own words. Warm, unmistakably not the
            // companion's, and not the colour of a command either.
            Role::Player => "1;35",
        }
    }

    /// Every role, for tests and documentation.
    pub fn all() -> [Role; 6] {
        [
            Role::Command,
            Role::Speech,
            Role::Thought,
            Role::Notice,
            Role::Prompt,
            Role::Player,
        ]
    }
}

/// When to colour at all.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Policy {
    /// Colour when the client asked for a terminal and its `TERM` is not `dumb`.
    Auto,
    /// Always colour, terminal or not. For piping into something that understands escapes.
    Always,
    /// Never colour: plain text, which is what the tests and logs want.
    Never,
}
impl Policy {
    /// Read the policy from `INFOCOMPANION_COLORS`.
    pub fn from_env(value: Option<&str>) -> Policy {
        match value.map(str::trim).map(str::to_ascii_lowercase).as_deref() {
            Some("always") | Some("1") | Some("yes") | Some("on") | Some("true") => Policy::Always,
            Some("never") | Some("0") | Some("no") | Some("off") | Some("false") => Policy::Never,
            _ => Policy::Auto,
        }
    }

    /// Should this connection be coloured?
    ///
    /// `has_terminal` is whether the client asked for a PTY: a pipe or a scripted capture
    /// gets plain bytes unless someone asked for colour explicitly.
    ///
    /// A PTY with no `TERM` still counts as a terminal. Windows' own `ssh` does not always
    /// send one, and the alternative — quietly turning colour off for the person who asked
    /// for it — is worse than colouring a terminal that turns out not to want it. `dumb`,
    /// which is the one way a client can say "I am not a terminal", is still respected.
    pub fn applies(self, has_terminal: bool, term: Option<&str>) -> bool {
        match self {
            Policy::Always => true,
            Policy::Never => false,
            Policy::Auto => {
                let term = term.unwrap_or("").trim().to_ascii_lowercase();
                has_terminal && term != "dumb"
            }
        }
    }
}

/// The resolved palette for one connection.
#[derive(Debug, Clone)]
pub struct Palette {
    enabled: bool,
    command: String,
    speech: String,
    thought: String,
    notice: String,
    prompt: String,
    player: String,
}

impl Palette {
    /// A palette that colours nothing.
    pub fn plain() -> Palette {
        Palette {
            enabled: false,
            command: String::new(),
            speech: String::new(),
            thought: String::new(),
            notice: String::new(),
            prompt: String::new(),
            player: String::new(),
        }
    }

    /// Resolve the palette from the environment, for a connection with these properties.
    pub fn from_lookup(
        policy: Policy,
        has_terminal: bool,
        term: Option<&str>,
        lookup: impl Fn(&str) -> Option<String>,
    ) -> Palette {
        let enabled = policy.applies(has_terminal, term);
        let mut palette = Palette {
            enabled,
            ..Palette::plain()
        };

        for role in Role::all() {
            let configured = lookup(role.env_var());
            let sgr = match configured.as_deref().map(str::trim) {
                // An explicit opt-out, for a role you would rather leave plain.
                Some("none") | Some("off") | Some("no") | Some("false") | Some("plain") => {
                    String::new()
                }
                // An empty or unrecognised value keeps the default. Deliberate: this is also
                // what docker-compose passes for an unset variable (`${VAR:-}`), and a blank
                // line in `.env` meaning "no colours at all" would be a nasty surprise.
                Some(value) => match validated(value) {
                    Some(sgr) => sgr,
                    None => role.default_sgr().to_string(),
                },
                None => role.default_sgr().to_string(),
            };
            palette.set(role, sgr);
        }

        palette
    }

    /// Resolve from the real environment.
    pub fn from_env(has_terminal: bool, term: Option<&str>) -> Palette {
        let policy = Policy::from_env(std::env::var("INFOCOMPANION_COLORS").ok().as_deref());
        Palette::from_lookup(policy, has_terminal, term, |name| std::env::var(name).ok())
    }

    fn set(&mut self, role: Role, sgr: String) {
        match role {
            Role::Command => self.command = sgr,
            Role::Speech => self.speech = sgr,
            Role::Thought => self.thought = sgr,
            Role::Notice => self.notice = sgr,
            Role::Prompt => self.prompt = sgr,
            Role::Player => self.player = sgr,
        }
    }

    fn sgr(&self, role: Role) -> &str {
        match role {
            Role::Command => &self.command,
            Role::Speech => &self.speech,
            Role::Thought => &self.thought,
            Role::Notice => &self.notice,
            Role::Prompt => &self.prompt,
            Role::Player => &self.player,
        }
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    /// The escape sequence that starts this role, or an empty string when it has none.
    pub fn start(&self, role: Role) -> String {
        let sgr = self.sgr(role);
        if !self.enabled || sgr.is_empty() {
            String::new()
        } else {
            format!("\x1b[{sgr}m")
        }
    }

    /// The escape sequence that ends any colour, or an empty string when colour is off.
    pub fn reset(&self) -> &'static str {
        if self.enabled {
            "\x1b[0m"
        } else {
            ""
        }
    }

    /// Wrap one line in this role's colour.
    pub fn paint(&self, role: Role, text: &str) -> String {
        let start = self.start(role);
        if start.is_empty() {
            return text.to_string();
        }
        format!("{start}{text}{}", self.reset())
    }
}

/// Accept an SGR parameter list, or reject it.
///
/// Only digits and semicolons survive; anything longer than a few parameters, or containing
/// a letter, an escape, or a control character, is refused. This is the difference between
/// a colour setting and a way to write arbitrary escapes to someone's terminal.
fn validated(value: &str) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() || trimmed.len() > 16 {
        return None;
    }
    if !trimmed.chars().all(|c| c.is_ascii_digit() || c == ';') {
        return None;
    }
    Some(trimmed.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn palette(policy: Policy, env: &[(&str, &str)]) -> Palette {
        Palette::from_lookup(policy, true, Some("xterm-256color"), |name| {
            env.iter()
                .find(|(key, _)| *key == name)
                .map(|(_, value)| value.to_string())
        })
    }

    #[test]
    fn every_role_has_a_colour_and_a_variable_to_override_it() {
        for role in Role::all() {
            assert!(
                role.env_var().starts_with("INFOCOMPANION_COLOR_"),
                "{role:?} needs a documented variable"
            );
            let sgr = role.default_sgr();
            assert!(
                validated(sgr).is_some(),
                "{role:?}'s default must be a sane SGR value"
            );
        }
    }

    #[test]
    fn a_role_can_be_recoloured_from_the_environment() {
        let palette = palette(
            Policy::Always,
            &[
                ("INFOCOMPANION_COLOR_COMMAND", "1;34"),
                ("INFOCOMPANION_COLOR_SPEECH", "95"),
                ("INFOCOMPANION_COLOR_PLAYER", "4;35"),
            ],
        );

        assert_eq!(
            palette.paint(Role::Command, "north"),
            "\x1b[1;34mnorth\x1b[0m"
        );
        assert_eq!(palette.paint(Role::Speech, "hi"), "\x1b[95mhi\x1b[0m");
        assert_eq!(
            palette.paint(Role::Player, "what about the desk?"),
            "\x1b[4;35mwhat about the desk?\x1b[0m"
        );
        // Everything not set keeps its default.
        assert_eq!(
            palette.paint(Role::Thought, "hmm"),
            format!("\x1b[{}mhmm\x1b[0m", Role::Thought.default_sgr())
        );
    }

    #[test]
    fn the_roles_are_visibly_different_by_default() {
        // The point of the palette: the things that can appear in one pane must not look
        // alike. In the companion's pane that is its speech, your words, and its commands.
        let palette = palette(Policy::Always, &[]);
        let starts: Vec<String> = Role::all().iter().map(|r| palette.start(*r)).collect();
        let unique: std::collections::HashSet<&String> = starts.iter().collect();

        assert_eq!(
            unique.len(),
            Role::all().len(),
            "every role needs its own colour: {starts:?}"
        );
        assert_ne!(palette.start(Role::Player), palette.start(Role::Speech));
        assert_ne!(palette.start(Role::Player), palette.start(Role::Command));
    }

    #[test]
    fn a_value_that_could_smuggle_an_escape_is_refused() {
        // Security, and not theoretical: colours come from .env, and .env is editable by
        // anything that can write the file.
        for hostile in [
            "36;rm -rf /",
            "\x1b[2J",
            "36\x07",
            "36m\x1b[31",
            "\u{1b}]0;title\u{7}",
        ] {
            assert!(
                validated(hostile).is_none(),
                "{hostile:?} must not be accepted as an SGR value"
            );
        }

        // A refused value falls back to the default rather than turning the pane off.
        let palette = palette(
            Policy::Always,
            &[("INFOCOMPANION_COLOR_COMMAND", "\x1b[2J")],
        );
        assert_eq!(
            palette.start(Role::Command),
            format!("\x1b[{}m", Role::Command.default_sgr())
        );
    }

    #[test]
    fn a_role_can_be_switched_off_by_name_and_keeps_its_default_when_blank() {
        let off = palette(Policy::Always, &[("INFOCOMPANION_COLOR_PROMPT", "off")]);
        assert_eq!(off.start(Role::Prompt), "");
        assert_eq!(off.paint(Role::Prompt, ">> "), ">> ");

        // Blank keeps the default: compose passes `${VAR:-}` for anything unset, so an
        // empty value here must not silently strip the palette.
        let blank = palette(
            Policy::Always,
            &[
                ("INFOCOMPANION_COLOR_PROMPT", ""),
                ("INFOCOMPANION_COLOR_COMMAND", "   "),
            ],
        );
        assert_eq!(
            blank.start(Role::Prompt),
            format!("\x1b[{}m", Role::Prompt.default_sgr())
        );
        assert_eq!(
            blank.start(Role::Command),
            format!("\x1b[{}m", Role::Command.default_sgr())
        );

        // A colour switched off only affects that role.
        assert_eq!(
            off.start(Role::Speech),
            format!("\x1b[{}m", Role::Speech.default_sgr())
        );
    }

    #[test]
    fn auto_colours_a_terminal_and_leaves_a_pipe_alone() {
        // A script capturing output, or `--stdio`, must not get escapes.
        assert!(Policy::Auto.applies(true, Some("xterm-256color")));
        assert!(!Policy::Auto.applies(false, Some("xterm-256color")));
        assert!(!Policy::Auto.applies(true, Some("dumb")));
        // A PTY with no TERM is still someone's terminal: Windows' `ssh` does not always
        // send one, and colouring it beats quietly turning colour off.
        assert!(Policy::Auto.applies(true, None));
        assert!(Policy::Auto.applies(true, Some("")));
        assert!(Policy::Always.applies(false, Some("dumb")));
        assert!(!Policy::Never.applies(true, Some("xterm-256color")));
    }

    #[test]
    fn the_policy_is_read_from_the_environment() {
        assert_eq!(Policy::from_env(Some("always")), Policy::Always);
        assert_eq!(Policy::from_env(Some("NEVER")), Policy::Never);
        assert_eq!(Policy::from_env(Some(" off ")), Policy::Never);
        assert_eq!(Policy::from_env(Some("1")), Policy::Always);
        // Absent or unrecognised means auto, which is the safe answer.
        assert_eq!(Policy::from_env(None), Policy::Auto);
        assert_eq!(Policy::from_env(Some("purple")), Policy::Auto);
    }

    #[test]
    fn a_plain_palette_produces_no_escapes_at_all() {
        let palette = Palette::plain();
        assert!(!palette.is_enabled());
        assert_eq!(palette.paint(Role::Command, "north"), "north");
        assert_eq!(palette.paint(Role::Speech, "hello"), "hello");
        assert_eq!(palette.reset(), "");
    }
}
