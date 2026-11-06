//! What each pane is, and what the page is told about them.
//!
//! The daemon picks a view from the *login name*, so a pane here is a login name and a label.
//! Nothing else about a pane is decided in this process: colour, layout and what a pane may
//! show all belong to the daemon, and this file is only the list of names.

/// The two names a page needs before it can call anyone anything.
///
/// Both are read from the same configuration the daemon reads, so a page cannot disagree with
/// the session about who is playing or who is talking.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Names {
    /// The companion: `INFOCOMPANION_PERSONA`.
    pub agent: String,
    /// The player: `INFOCOMPANION_PLAYER_NAME`.
    pub player: String,
}

/// The monitor: a screen you watch, with the beige plastic and the lamp on it. The game.
pub const MONITOR: &str = "monitor";

/// The panel: the same plastic as the strip below it, no artwork and no lamp, filling the pane
/// the way a monitor does. The companion's view is mostly words, and words do not need a bezel
/// drawn round them.
pub const PANEL: &str = "panel";

/// The strip: the two or three lines you type into, which is a strip of plastic on the desk
/// rather than a screen on the wall.
pub const STRIP: &str = "strip";

/// One pane: the seat to log in as, what to call it, and two things the page wants to know.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Pane {
    pub seat: String,
    pub label: String,
    /// Which chassis to draw it on — see [`MONITOR`], [`PANEL`] and [`STRIP`].
    pub shape: &'static str,
    /// Does this pane take the cursor when the page loads?
    pub focus: bool,
}

impl Pane {
    fn new(seat: &str, label: String, names: &Names) -> Self {
        let kind = seat.trim().to_lowercase();
        Self {
            seat: seat.trim().to_string(),
            label,
            // Decided here, once, rather than in the browser: what a pane *is* comes from the
            // seat the daemon gave it, and the page is only told the answer.
            shape: shape_for(&kind, names),
            // The pane you type in takes the cursor on load.
            focus: matches!(kind.as_str(), "me" | "input"),
        }
    }
}

/// Which chassis a seat is drawn on.
///
/// One place, for the same reason the names are in one place: the browser is told the answer
/// rather than working it out from a login name. Anything the daemon does not define as one of
/// its own views is a monitor, because a screen is the safe assumption for a view we have never
/// heard of.
fn shape_for(kind: &str, names: &Names) -> &'static str {
    let agent = names.agent.trim().to_lowercase();
    match kind {
        "me" | "input" => STRIP,
        "companion" | "quill" => PANEL,
        other if !agent.is_empty() && other == agent => PANEL,
        _ => MONITOR,
    }
}

/// What a pane is called on its signage.
///
/// `companion` and `quill` are the daemon's own aliases for whichever personality is running,
/// so both take the personality's name: the pane is a person, not a category. The pane you type
/// into is the player's, so it takes the player's name — and if the player has no name, it is
/// simply `ME`, because that is what the daemon calls that view.
pub fn label_for(seat: &str, names: &Names) -> String {
    let kind = seat.trim().to_lowercase();
    let agent = names.agent.trim().to_uppercase();
    match kind.as_str() {
        "game" => "THE GAME".to_string(),
        "companion" | "quill" => agent,
        "me" | "input" => player_or_me(&names.player),
        "player" | "all" => "THE SESSION".to_string(),
        other if !agent.is_empty() && other == agent.to_lowercase() => agent,
        other => other.to_uppercase(),
    }
}

/// The player's name, or `ME` when nobody has said what it is.
fn player_or_me(player: &str) -> String {
    let player = player.trim();
    if player.is_empty() {
        "ME".to_string()
    } else {
        player.to_uppercase()
    }
}

/// Build a list of panes from a comma-separated list of seats, with optional labels.
///
/// Seats are configuration rather than HTML, so `INFOCOMPANION_PANE_MULTI=game,me` is a
/// two-screen page without a file being edited.
pub fn build(seats: &str, labels: &str, names: &Names) -> Vec<Pane> {
    let seats: Vec<&str> = seats
        .split(',')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .collect();
    let given: Vec<&str> = labels.split(',').map(str::trim).collect();
    seats
        .iter()
        .enumerate()
        .map(|(index, seat)| {
            let label = given
                .get(index)
                .filter(|name| !name.is_empty())
                .map(|name| name.to_string())
                .unwrap_or_else(|| label_for(seat, names));
            Pane::new(seat, label, names)
        })
        .collect()
}

/// The panes, as javascript the page reads (`window.IC`).
///
/// Seats are told to the page rather than baked into it: an operator changes an environment
/// variable and the pages follow, with no HTML to edit. The voice rides with them — it is a name
/// the deployment set, and the page offers the switch only when there is one
/// (08_webui_tts.md §4 (`V15`)).
pub fn config_js(
    single: &[Pane],
    multi: &[Pane],
    daemon_host: &str,
    daemon_port: u16,
    voice: Option<&str>,
) -> String {
    let pane = |p: &Pane| {
        serde_json::json!({
            "seat": p.seat,
            "label": p.label,
            "shape": p.shape,
            "focus": p.focus,
        })
    };
    let config = serde_json::json!({
        "single": single.iter().map(pane).collect::<Vec<_>>(),
        "multi": multi.iter().map(pane).collect::<Vec<_>>(),
        "daemon": { "host": daemon_host, "port": daemon_port },
        // `null` when the deployment named no voice, which is how the page knows not to offer a
        // switch at all (08_webui_tts.md §4 (`V15`)).
        "voice": voice,
    });
    format!(
        "window.IC = {};\n",
        serde_json::to_string_pretty(&config).unwrap_or_else(|_| "{}".into())
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The names a page is built for: the companion from the persona, the player from the same
    /// configuration the daemon reads.
    fn names() -> Names {
        Names {
            agent: "james".to_string(),
            player: "Peter".to_string(),
        }
    }

    #[test]
    fn a_pane_is_named_for_what_it_is() {
        let names = names();
        assert_eq!(label_for("game", &names), "THE GAME");
        assert_eq!(label_for("companion", &names), "JAMES");
        assert_eq!(label_for("quill", &names), "JAMES");
        assert_eq!(label_for("james", &names), "JAMES");
        assert_eq!(label_for("player", &names), "THE SESSION");
        assert_eq!(label_for("gandalf", &names), "GANDALF");
    }

    /// The pane you type into is the player's, so it says who the player is. A player with no
    /// name is `ME`, which is what the daemon calls that view.
    #[test]
    fn the_pane_you_type_in_is_named_after_the_player_in_the_config() {
        let named = names();
        assert_eq!(label_for("me", &named), "PETER");
        assert_eq!(label_for("input", &named), "PETER");

        let anonymous = Names {
            agent: "james".to_string(),
            player: String::new(),
        };
        assert_eq!(label_for("me", &anonymous), "ME");
        assert_eq!(label_for("input", &anonymous), "ME");
    }

    /// A pane is drawn on one of three chassis, and the seat decides which: the game gets the
    /// plastic, the companion's words get a plain panel, and you type into a strip.
    #[test]
    fn the_three_screen_page_knows_which_pane_wants_the_room() {
        let panes = build("game,companion,me", "", &names());
        assert_eq!(panes.len(), 3);
        assert_eq!(panes[0].seat, "game");
        assert_eq!(panes[0].shape, MONITOR, "the game gets the monitor");
        assert_eq!(
            panes[1].shape, PANEL,
            "the companion's words get no artwork"
        );
        assert_eq!(panes[1].label, "JAMES");
        assert_eq!(
            panes[2].shape, STRIP,
            "the pane you type in is not a screen"
        );
        assert!(panes[2].focus, "the pane you type in takes the cursor");
        assert!(!panes[0].focus);
    }

    /// The personality's own name is a companion seat, so it is drawn the same way whichever of
    /// the daemon's aliases is configured — and a view we have never heard of gets a monitor,
    /// because a screen is the safe assumption.
    #[test]
    fn a_pane_is_drawn_by_what_it_is_and_a_stranger_gets_a_monitor() {
        assert_eq!(shape_for("quill", &names()), PANEL);
        assert_eq!(shape_for("james", &names()), PANEL);
        assert_eq!(shape_for("input", &names()), STRIP);
        assert_eq!(shape_for("game", &names()), MONITOR);
        assert_eq!(shape_for("player", &names()), MONITOR);
        assert_eq!(shape_for("gandalf", &names()), MONITOR);
    }

    #[test]
    fn labels_can_be_overridden_without_touching_html() {
        let panes = build("game,me", "THE STORY,YOU", &names());
        assert_eq!(panes[0].label, "THE STORY");
        assert_eq!(panes[1].label, "YOU");
    }

    #[test]
    fn two_panes_is_a_page_too() {
        assert_eq!(build("game,me", "", &names()).len(), 2);
        assert_eq!(build("player", "", &names())[0].seat, "player");
        assert!(build("", "", &names()).is_empty());
    }

    #[test]
    fn the_config_the_page_gets_is_javascript() {
        let text = config_js(
            &build("player", "", &names()),
            &build("game,companion,me", "", &names()),
            "127.0.0.1",
            2222,
            Some("bm_fable"),
        );
        assert!(text.starts_with("window.IC = {"));
        let body = text
            .split_once('=')
            .unwrap()
            .1
            .trim_end()
            .trim_end_matches(';');
        let parsed: serde_json::Value = serde_json::from_str(body).expect("valid json");
        assert_eq!(parsed["multi"][0]["seat"], "game");
        assert_eq!(parsed["multi"][0]["shape"], "monitor");
        assert_eq!(parsed["multi"][1]["shape"], "panel");
        assert_eq!(parsed["multi"][2]["shape"], "strip");
        assert_eq!(parsed["multi"][2]["focus"], true);
        assert_eq!(parsed["daemon"]["port"], 2222);
        assert_eq!(
            parsed["voice"], "bm_fable",
            "the deployment's voice reaches the page, which is what makes the switch possible"
        );
    }

    /// A deployment with no voice named is a page with no switch: the setting's absence is the
    /// answer to "why is there nothing to press" (08_webui_tts.md §4 (`V15`)).
    #[test]
    fn a_page_is_told_the_voice_only_when_there_is_one() {
        let text = config_js(
            &build("player", "", &names()),
            &build("game,companion,me", "", &names()),
            "127.0.0.1",
            2222,
            None,
        );
        let body = text
            .split_once('=')
            .unwrap()
            .1
            .trim_end()
            .trim_end_matches(';');
        let parsed: serde_json::Value = serde_json::from_str(body).expect("valid json");
        assert!(
            parsed["voice"].is_null(),
            "nothing named, nothing to switch on"
        );
    }
}
