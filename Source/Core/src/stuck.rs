//! Is the story getting anywhere? (02_game_boundary.md §7 (`G35`–`G40`))
//!
//! The one thing about a game that the harness cannot read is *what its prose means* — an
//! end-of-story menu is a `read_line` behind a `>` banner, in the story's own words and its own
//! language, and no recogniser the harness could write would generalise. So the harness does not
//! read it. It counts, using four signals that need no reading at all:
//!
//! * **the counters** — the score and the moves, which the interpreter holds for the versions where
//!   it owns the status line;
//! * **the place** — the location object, or, for a story whose status line is its own, the
//!   fingerprints of what it has printed;
//! * **repetition** — the same paragraph arriving again and again. This is the one signal a maze
//!   can fake (see `a_maze`, and the measurement in `signals`): a corridor of identical cells
//!   prints one screen for every step of a walk that is working perfectly;
//! * **barrenness** — a turn that neither had a word accepted nor produced anything the companion
//!   can read.
//!
//! **Two of the four holding for [`WINDOW`] turns means stuck**, and a signal that cannot be read
//! does not count: a Version 5 story may keep its own status line (so the counters and the location
//! are simply absent), and a turn that is a bare keystroke has nothing in it to fingerprint. That
//! asymmetry is deliberate. The failure this exists to prevent — *we are not getting anywhere* — is
//! much cheaper than the failure it could cause, which is a harness that stops playing a game the
//! player is enjoying. So the detector is quiet when it is unsure, and it says so when it fires.
//!
//! Two behaviours hang off it (both in `game.rs`): **no checkpoint while stuck** — a recovery point
//! recorded from a position the story will not move out of is worse than none, and is exactly how a
//! fork inside a fog-bound maze became the place a session came back to — and **no more feeding the
//! story**, which is the harness's half of a conversation the player then has with the companion.
//!
//! Nothing here is story-specific, and nothing here is a phrase list: every input is a number, an
//! object number, or a hash.

use std::collections::{HashMap, HashSet, VecDeque};

/// How many turns are held as evidence.
///
/// Longer than the three or four turns a person would call "stuck" out loud, because the cost of
/// being wrong is asymmetric: a detector that fires late costs a few wasted turns, and one that
/// fires early costs the player the game.
pub const WINDOW: usize = 6;

/// How many of the four signals must hold at once.
pub const SIGNALS: usize = 2;

/// How many times one fingerprint must appear inside the window for *repetition* to hold.
pub const REPEATS: usize = 3;

/// How many (place, line) pairs are remembered before the oldest evidence is dropped.
///
/// Only the *contrast* matters — one line, two answers — so a bounded map keeps everything the
/// detector needs and cannot grow with a long evening.
pub const ANSWERS: usize = 128;

/// One turn, reduced to what can be counted rather than read.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Turn {
    /// The interpreter's move counter, when it has one and it owns the status line.
    pub moves: Option<i64>,
    /// The interpreter's score, same condition.
    pub score: Option<i64>,
    /// The location object, same condition.
    pub location: Option<u32>,
    /// An index of what the story printed — empty when it printed nothing.
    pub fingerprint: String,
    /// Did the story's own dictionary accept a word this turn? `None` on a turn that took no line.
    pub accepted: Option<bool>,
    /// Could anything at all be read back? The framed output, or the interpreter's screen.
    pub produced: bool,
    /// What the harness typed to get here; empty when the story spoke without being asked.
    ///
    /// Not a signal — a command is a string, and this module reads nothing — but the one thing
    /// that makes *"you have already tried that"* answerable without asking the model.
    pub line: String,
}

/// Which of the four signals held on the last [`Detector::observe`].
///
/// Kept as data rather than recomputed for the log: the first question anybody asks about a
/// detector that fired is *which signals*, and the answer has to be the one that was true then.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Held {
    pub counters: bool,
    pub place: bool,
    pub repetition: bool,
    pub barren: bool,
}

impl Held {
    pub fn count(self) -> usize {
        [self.counters, self.place, self.repetition, self.barren]
            .into_iter()
            .filter(|held| *held)
            .count()
    }

    /// One line for the log, naming only the signals that held.
    pub fn describe(self) -> String {
        let mut names = Vec::new();
        if self.counters {
            names.push("counters");
        }
        if self.place {
            names.push("place");
        }
        if self.repetition {
            names.push("repetition");
        }
        if self.barren {
            names.push("barren");
        }
        names.join("+")
    }
}

/// The evidence, and whether it adds up to stuck.
#[derive(Debug)]
pub struct Detector {
    window: VecDeque<Evidence>,
    /// Every fingerprint seen in this game, so "a place the harness has not seen" is answerable.
    seen: HashSet<String>,
    /// What each line has been answered with, per place: the first answer, and whether a different
    /// one has since arrived from the same place. See [`Detector::a_maze`] — this is the only
    /// evidence in the module that separates a corridor from a wall, and it is kept the way the
    /// module keeps everything else: **a place is the interpreter's object number**, `None` for a
    /// story that reports none (its own status line, or none at all), and a line is a hash, because
    /// nothing here reads anything (`G40`).
    answers: HashMap<(Option<u32>, u64), (u64, bool)>,
    stuck: bool,
    /// The signals behind the verdict in force.
    held: Held,
}

/// One turn in the window, and whether its place was new *at the time*.
///
/// The freshness is captured as it happens rather than recomputed later, because "new" is a fact
/// about the moment: a fog that has been printed six times was new on the first of them, and a
/// detector that asks the question afterwards cannot tell that from a window full of new rooms.
#[derive(Debug, Clone)]
struct Evidence {
    turn: Turn,
    fresh: bool,
}

impl Default for Detector {
    fn default() -> Self {
        Self::new()
    }
}

impl Detector {
    pub fn new() -> Self {
        Self {
            window: VecDeque::with_capacity(WINDOW),
            seen: HashSet::new(),
            answers: HashMap::new(),
            stuck: false,
            held: Held {
                counters: false,
                place: false,
                repetition: false,
                barren: false,
            },
        }
    }

    pub fn stuck(&self) -> bool {
        self.stuck
    }

    /// The signals behind the verdict in force, for the log.
    pub fn held(&self) -> Held {
        self.held
    }

    /// Has this line already been typed **from here**?
    ///
    /// The treadmill this exists to stop is not a slow player, it is the same command going in
    /// again and again with the world not moving — and the harness can see that without reading a
    /// word of the story. A *different* line is always allowed through, because the whole point of
    /// the verdict is that whoever is holding the keyboard should think of something else.
    ///
    /// **Here**, and not anywhere in the window, which is the difference between a refusal and a
    /// lock. A repeat from a place you have already tried it in is somebody walking into the same
    /// wall; the same line from somewhere else is somebody executing a route, and a route is what
    /// the companion has when it has just worked the place out. Measured 2026-10-01 on
    /// Wishbringer's Steep Trail — six rooms sharing one location object, reached by a spiral —
    /// where a companion that had just derived *"south, then up"* was refused the `south`, because
    /// it had been typed three turns and one room earlier. The player was refused it too, and the
    /// only way out of the game was a restore.
    ///
    /// The newest turn's fingerprint is the place the line would be typed from. An empty one — a
    /// bare keystroke, a turn the story printed nothing for — cannot tell places apart, and there
    /// the whole window is asked instead, which is the older and stricter rule.
    pub fn already_tried(&self, line: &str) -> bool {
        let line = line.trim();
        if line.is_empty() {
            return false;
        }

        let here = self
            .window
            .back()
            .map(|evidence| evidence.turn.fingerprint.as_str())
            .unwrap_or_default();

        self.window.iter().any(|evidence| {
            Self::hash(evidence.turn.line.trim()) == Self::hash(line)
                && (here.is_empty() || evidence.turn.fingerprint == here)
        })
    }

    /// One line's answer, remembered against its place.
    fn remember(&mut self, turn: &Turn) {
        let key = (turn.location, Self::hash(turn.line.trim()));
        let answer = Self::hash(&turn.fingerprint);
        if self.answers.len() >= ANSWERS && !self.answers.contains_key(&key) {
            // Newest evidence is worth more than the oldest here, and what is lost by starting
            // again is one repeat that might have been caught.
            self.answers.clear();
        }
        match self.answers.get_mut(&key) {
            Some((first, differed)) => {
                if !*differed && *first != answer {
                    *differed = true;
                }
            }
            None => {
                self.answers.insert(key, (answer, false));
            }
        }
    }

    /// A string as a number. Nothing in this module reads a word, so nothing in it needs one.
    fn hash(text: &str) -> u64 {
        use std::hash::{Hash, Hasher};
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        text.hash(&mut hasher);
        hasher.finish()
    }

    /// Is the newest turn's place one that answers the same line in more than one way?
    ///
    /// **This is the one fact that separates a corridor from a wall, and it is the only one this
    /// module has.** A maze whose cells share one location object and print one screen — the one
    /// this was measured on prints `Fog` in every cell and answers to a single object number — is
    /// *identical to a wedge* by every other count: the same place, the same paragraph, turn after
    /// turn. What it cannot fake is an answer that changes: measured live on 2026-10-02, `down` was
    /// refused from one cell of that maze and left another, `south` blocked in one and reprinted in
    /// the next, and `east` killed the player from two of them. A place that has answered one line
    /// two ways is not one place, and a screen that repeats there is a corridor.
    ///
    /// Asked for the place the newest turn is in, and answered the same way for both kinds of story
    /// this plays:
    ///
    /// * **the interpreter's object number**, when the story reports one, scoped to that place — the
    ///   precise form, and it survives a game that walks a dozen rooms between two answers;
    /// * **the sitting, when it reports none** (a story with its own status line has no location at
    ///   all), because there is nothing narrower to key on and a contrast that scrolled out of the
    ///   window is the same fact ten turns later. The cost of that width is that such a story loses
    ///   the repetition signal after one dynamic line — and the module can afford it, because that
    ///   story's second signal is `barren`, which a wedge on a story with no counters reaches
    ///   without help.
    ///
    /// Nothing here is a story, a word list or a phrase: the question is *the same line, in the same
    /// place, answered differently*, asked of an object number, a fingerprint and a hash of what was
    /// typed.
    ///
    /// The failure this prevents is the expensive one, in this module's own terms: a maze read as a
    /// wedge tells the companion to stop sending the only command that can move it, and the walk out
    /// of the maze this was measured on is seven moves of exactly that.
    fn a_maze(&self) -> bool {
        let here = self.window.back().map(|evidence| evidence.turn.location);
        self.answers
            .iter()
            .any(|((place, _), (_, differed))| Some(*place) == here && *differed)
    }

    /// Forget everything: another game, another world, another attempt. Nothing counted here is
    /// true of a story that has just been loaded or a fork that has just been restored.
    pub fn forget(&mut self) {
        self.window.clear();
        self.seen.clear();
        self.answers.clear();
        self.stuck = false;
        self.held = Held {
            counters: false,
            place: false,
            repetition: false,
            barren: false,
        };
    }

    /// Take one turn's evidence and say whether the story is stuck.
    ///
    /// Returns the verdict *after* this turn, so the caller never has to ask twice.
    pub fn observe(&mut self, turn: Turn) -> bool {
        // A place the harness has not seen before is progress, whatever the counters say: a story
        // can print the same paragraph in two rooms, and a new fingerprint is the only evidence
        // that does not depend on the story's own bookkeeping.
        let fresh = !turn.fingerprint.is_empty() && !self.seen.contains(&turn.fingerprint);
        if !turn.fingerprint.is_empty() {
            self.seen.insert(turn.fingerprint.clone());
        }

        // The one fact in this module that separates a corridor from a wall, recorded before the
        // turn is judged so that it is evidence about its own place. See `a_maze`.
        if !turn.line.trim().is_empty() && !turn.fingerprint.is_empty() {
            self.remember(&turn);
        }

        self.window.push_back(Evidence { turn, fresh });
        while self.window.len() > WINDOW {
            self.window.pop_front();
        }

        self.held = self.signals();
        if self.held.count() >= SIGNALS {
            self.stuck = true;
        }

        // Clearing takes one clear sign of life rather than a quiet window: a place that has not
        // been seen, or a turn the story answered in words. Without this a stuck session would stay
        // stuck after the player had worked out what to do.
        if self.stuck && self.alive() {
            self.stuck = false;
            self.held = Held {
                counters: false,
                place: false,
                repetition: false,
                barren: false,
            };
        }

        self.stuck
    }

    /// Did the newest turn move the world?
    ///
    /// Narrowed by two failing tests rather than by taste, and it is now symmetric with the `place`
    /// signal: **the world moved if there is a place in it that has not been seen, or if the
    /// location object changed.** Both earlier versions cleared the verdict on something that is
    /// also true while a story is going nowhere — first "the story answered in words" (which is
    /// exactly what a dead end does, six times over), then "the move counter advanced" (which only
    /// means a command was accepted, and `You can't see any such thing.` is accepted six times
    /// too). Each version made the detector fire and clear on alternate turns, so the verdict never
    /// survived long enough to be reported.
    fn alive(&self) -> bool {
        let Some(newest) = self.window.back() else {
            return false;
        };
        if newest.fresh {
            return true;
        }
        let Some(previous) = self.window.iter().rev().nth(1) else {
            return false;
        };
        matches!(
            (newest.turn.location, previous.turn.location),
            (Some(now), Some(before)) if now != before
        )
    }

    /// Which of the four signals hold across the whole window.
    fn signals(&self) -> Held {
        let window: Vec<&Evidence> = self.window.iter().collect();
        let full = window.len() >= WINDOW;

        // 1. The counters. Both numbers have to be present, because an interpreter that does not
        //    own the status line reports neither — and "unchanged" on two absences would be a
        //    signal invented out of nothing.
        let counters = full
            && window.first().is_some_and(|first| {
                first.turn.moves.is_some()
                    && first.turn.score.is_some()
                    && window.iter().all(|other| {
                        other.turn.moves == first.turn.moves && other.turn.score == first.turn.score
                    })
            });

        // 2. The place. The location object when the interpreter has one; otherwise the story's own
        //    fingerprints, where "nothing new in this window" is the same fact about the world. A
        //    window of turns that printed *nothing* is silence rather than sameness, so it does not
        //    count as this signal — a form being filled is not a maze.
        let by_location = full
            && window.first().is_some_and(|first| {
                first.turn.location.is_some()
                    && window
                        .iter()
                        .all(|other| other.turn.location == first.turn.location)
            });
        let by_fingerprint = full
            && window
                .iter()
                .any(|evidence| !evidence.turn.fingerprint.is_empty())
            && window.iter().all(|evidence| !evidence.fresh);
        let place = by_location || by_fingerprint;

        // 3. Repetition: one fingerprint, again and again, inside the window — **unless this place
        //    is one that has answered the same line two different ways**, in which case the screen
        //    repeating is the shape of the place and not a sign that nothing is happening.
        //
        //    Measured 2026-10-02, live, on Wishbringer's fog: `down north west down south east down`
        //    walked the player from Cliff Edge to Cliff Bottom — every cell of it printing `Fog` —
        //    and the detector called the story stuck three times on the way, telling the companion
        //    *"do not send the same command again"* while that was the only thing that could move
        //    it. The same place had already shown the contrast this signal now reads: `down` was
        //    refused in one cell and left it in another, `south` blocked in one and reprinted in
        //    the next, `east` fatal from two of them. A place that answers one line two ways is not
        //    one place, and a screen that repeats there is a corridor.
        //
        //    The cost of the wider reading is a wedge in a room whose prose has changed at some
        //    point being left alone for a few more turns, and the cost of the narrower one is a
        //    companion that cannot walk a maze at all. The module's own asymmetry settles it.
        let mut counts: HashMap<&str, usize> = HashMap::new();
        for evidence in &window {
            if !evidence.turn.fingerprint.is_empty() {
                *counts
                    .entry(evidence.turn.fingerprint.as_str())
                    .or_default() += 1;
            }
        }
        let repetition = !self.a_maze() && counts.values().any(|count| *count >= REPEATS);

        // 4. Barren: no turn in the window both had a word accepted and showed anything.
        let barren = full
            && window.iter().all(|evidence| {
                let answered = evidence.turn.accepted.unwrap_or(false) && evidence.turn.produced;
                !answered
            });

        Held {
            counters,
            place,
            repetition,
            barren,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn turn(moves: i64, location: u32, fingerprint: &str) -> Turn {
        Turn {
            moves: Some(moves),
            score: Some(0),
            location: Some(location),
            fingerprint: fingerprint.to_string(),
            accepted: Some(true),
            produced: true,
            line: String::new(),
        }
    }

    #[test]
    fn a_walk_through_new_places_is_not_stuck() {
        let mut detector = Detector::new();
        for step in 0..20 {
            let stuck = detector.observe(turn(step, step as u32 + 1, &format!("room {step}")));
            assert!(!stuck, "a new room every turn is the opposite of stuck");
        }
    }

    #[test]
    fn a_story_that_stops_accepting_anything_is_stuck_at_two_signals() {
        // The measured shape of 2026-09-20: the counters frozen, the same paragraph coming back,
        // and the story accepting nothing. Two of the four are enough, and here three hold.
        let mut detector = Detector::new();
        for _ in 0..WINDOW {
            detector.observe(Turn {
                moves: Some(41),
                score: Some(0),
                location: Some(9),
                fingerprint: "the same paragraph".to_string(),
                accepted: Some(false),
                produced: true,
                line: String::new(),
            });
        }
        assert!(detector.stuck());
        assert!(detector.held().counters && detector.held().place);
        assert!(detector.held().repetition && detector.held().barren);
    }

    #[test]
    fn a_walk_whose_every_cell_prints_the_same_screen_is_not_stuck() {
        // Measured 2026-10-02, live, and it is the defect this signal was rebuilt for:
        // `down north west down south east down` walked the player from Cliff Edge to Cliff Bottom
        // through Wishbringer's fog. Every cell prints the same two lines, the location object
        // never changes, and the detector called it stuck three times on the way — telling the
        // companion *"do not send the same command again"* while that was the only thing that
        // could move it. What separates it from a wedge is in the same place: `down` was refused
        // from one cell and moved the player out of another.
        let mut detector = Detector::new();
        detector.observe(refused(100, "down"));

        let walk = ["north", "west", "down", "south", "east", "down"];
        for (step, line) in walk.iter().cycle().take(2 * walk.len()).enumerate() {
            let stuck = detector.observe(fog(101 + step as i64, line));
            assert!(!stuck, "a walk through a maze is not a wedge ({line})");
        }
        assert!(
            !detector.held().repetition,
            "one screen arriving again is the shape of the place"
        );
        assert!(
            detector.held().place,
            "and the place signal still holds — the maze is still one location object"
        );

        // The contrast is doing the work, and this is the proof: the same walk, in a place that
        // has never answered a line two ways, still reads as the wedge it would be.
        let mut blind = Detector::new();
        for (step, line) in walk.iter().cycle().take(2 * walk.len()).enumerate() {
            blind.observe(fog(step as i64, line));
        }
        assert!(
            blind.stuck(),
            "without the contrast there is nothing to tell it apart"
        );
        assert!(blind.held().repetition && blind.held().place);
    }

    #[test]
    fn a_maze_is_not_a_wedge_even_with_no_location_to_read() {
        // The other half of the same question, and the half that needs no status line: a Version 5
        // story keeps its own, so `moves`, `score` and `location` are all absent and the only
        // evidence is what was typed and what came back. Same walk, same contrast, same answer.
        let walk = ["north", "west", "down", "south", "east", "down"];
        let no_status = |turn: Turn| Turn {
            moves: None,
            score: None,
            location: None,
            ..turn
        };

        let mut detector = Detector::new();
        detector.observe(no_status(refused(100, "down")));
        for (step, line) in walk.iter().cycle().take(2 * walk.len()).enumerate() {
            let stuck = detector.observe(no_status(fog(101 + step as i64, line)));
            assert!(
                !stuck,
                "no counters, no location, and still a walk ({line})"
            );
        }

        // And with no contrast anywhere in the window there is nothing to tell it from a wedge.
        // That is the honest limit of counting rather than reading: one screen for a whole walk
        // that always answers the same way is a wedge as far as this module can see.
        let mut blind = Detector::new();
        for (step, line) in walk.iter().cycle().take(2 * walk.len()).enumerate() {
            blind.observe(no_status(fog(step as i64, line)));
        }
        assert!(
            blind.stuck(),
            "one screen, one answer per line, and nothing to separate it from a wall"
        );
    }

    /// One turn of the fog, in the shape the core frames it.
    fn fog(moves: i64, line: &str) -> Turn {
        Turn {
            moves: Some(moves),
            score: Some(23),
            location: Some(101),
            fingerprint: "Fog\nYou're lost in a thick cloud of fog.".to_string(),
            accepted: Some(true),
            produced: true,
            line: line.to_string(),
        }
    }

    /// The same, answered with a refusal instead — the second answer one line can get here.
    fn refused(moves: i64, line: &str) -> Turn {
        Turn {
            fingerprint: "You can't see any downward trails here!".to_string(),
            ..fog(moves, line)
        }
    }

    #[test]
    fn a_story_printing_one_screen_with_nothing_typed_is_stuck() {
        // A Version 5 story keeps its own status line, so moves, score and location are all absent.
        // The fingerprints still say what the counters would have: the same paragraph, arriving
        // again and again, and no new place in the window. Two signals, so it fires — after enough
        // turns for the screen to have been seen before, which is the honest answer rather than a
        // detector that calls the second room of the game a dead end.
        //
        // **Not the maze case** it used to be named after, and the difference is the whole of the
        // 2026-10-02 rebuild: this story is printing with *nothing typed at it*, and the fog is a
        // place a player walks. A maze is not this, and a test that says it is was the belief the
        // defect rested on (see `a_walk_whose_every_cell_prints_the_same_screen_is_not_stuck`).
        let mut detector = Detector::new();
        detector.observe(Turn {
            fingerprint: "a courtyard".to_string(),
            accepted: Some(true),
            produced: true,
            ..Turn::default()
        });

        let mut stuck = false;
        for _ in 0..(WINDOW + 3) {
            stuck = detector.observe(Turn {
                fingerprint: "thick fog".to_string(),
                accepted: Some(true),
                produced: true,
                ..Turn::default()
            });
        }

        assert!(stuck, "one screen, nothing typed, nobody moving");
        assert!(detector.held().repetition && detector.held().place);
        assert!(!detector.held().counters, "there are no counters to read");
    }

    #[test]
    fn a_form_being_filled_a_key_at_a_time_is_not_stuck() {
        // Bureaucracy's licence form, which is where a careless detector would do real damage: the
        // keys go in, the story redraws the same screen, and the story's parser accepts no word on
        // any of those turns. That is *one* signal — barren — and one is not enough. The screen is
        // something read back, too, so the turns are not even barren.
        let mut detector = Detector::new();
        for _ in 0..12 {
            let stuck = detector.observe(Turn {
                fingerprint: String::new(),
                accepted: Some(false),
                produced: true,
                ..Turn::default()
            });
            assert!(!stuck, "a form is slow, not stuck");
        }
    }

    #[test]
    fn sitting_in_one_room_and_looking_around_is_not_stuck() {
        // The move counter advances on every accepted command, so a player examining everything in
        // a room never freezes the counters — and each examination answers with different prose,
        // which is the other half. Same room, new words: one signal, and one is not enough.
        let mut detector = Detector::new();
        for thing in 0..12 {
            detector.observe(turn(
                7 + thing,
                3,
                &format!("the grate, from angle {thing}"),
            ));
        }
        assert!(!detector.stuck());
    }

    #[test]
    fn the_same_answer_six_times_in_one_room_is_stuck() {
        // The pathological version of the same thing, and it should fire: the story said the same
        // words to six different commands and nothing moved. The counters advancing is not progress
        // when the world is not moving, which is why two of the four signals are enough.
        let mut detector = Detector::new();
        for step in 0..WINDOW {
            detector.observe(turn(step as i64, 3, "You can't see any such thing."));
        }
        assert!(detector.stuck());
        assert!(detector.held().place && detector.held().repetition);
    }

    #[test]
    fn a_window_that_is_not_full_never_fires() {
        let mut detector = Detector::new();
        for _ in 0..WINDOW - 1 {
            let stuck = detector.observe(Turn {
                moves: Some(1),
                score: Some(0),
                location: Some(1),
                fingerprint: "nowhere".to_string(),
                accepted: Some(false),
                produced: true,
                line: String::new(),
            });
            assert!(!stuck, "four turns of nothing is not a verdict");
        }
    }

    #[test]
    fn one_new_place_clears_it_again() {
        let mut detector = Detector::new();
        for _ in 0..WINDOW {
            detector.observe(Turn {
                moves: Some(41),
                score: Some(0),
                location: Some(9),
                fingerprint: "the same paragraph".to_string(),
                accepted: Some(false),
                produced: true,
                line: String::new(),
            });
        }
        assert!(detector.stuck());

        detector.observe(turn(42, 10, "somewhere else entirely"));
        assert!(
            !detector.stuck(),
            "the world moved: it is not stuck any more"
        );
    }

    #[test]
    fn the_same_command_inside_the_window_is_recognised_as_already_tried() {
        // The refusal is not about the *command* being wrong — nothing here knows what a command
        // means. It is that the harness can see the same string going in while the world does not
        // move, which is the treadmill rather than a slow player. A different line is always let
        // through, because thinking of something else is the entire way out.
        let mut detector = Detector::new();
        for _ in 0..WINDOW {
            detector.observe(Turn {
                fingerprint: "You can't see any such thing.".to_string(),
                accepted: Some(false),
                produced: true,
                line: "open the gate".to_string(),
                ..Turn::default()
            });
        }

        assert!(detector.already_tried("open the gate"));
        assert!(
            detector.already_tried("  open the gate  "),
            "trimmed, as a prompt trims"
        );
        assert!(!detector.already_tried("unlock the gate"));
        assert!(
            !detector.already_tried(""),
            "an empty line is a keystroke, not a repeat"
        );
    }

    #[test]
    fn the_same_command_from_a_different_place_is_a_plan_rather_than_a_treadmill() {
        // Measured 2026-10-01, Wishbringer's Steep Trail: six rooms sharing one location object (`245`),
        // walked as a spiral. A companion that had just worked the spiral out — `south`, then `up` — was
        // refused the `south`, because it had been typed three turns and one room earlier; the player was
        // refused it too, and the only way out of the game was a restore. The line is the same and the
        // *place* is not, and the place is what the refusal is about.
        let mut detector = Detector::new();
        for fingerprint in ["Steep Trail#aaaa", "Steep Trail#bbbb", "Steep Trail#cccc"] {
            detector.observe(Turn {
                location: Some(245),
                fingerprint: fingerprint.to_string(),
                accepted: Some(true),
                produced: true,
                line: "south".to_string(),
                ..Turn::default()
            });
        }

        // Standing in the newest of those rooms, `south` was attempted here, so it is still refused.
        assert!(detector.already_tried("south"));

        // One more turn, one more room: now the line was typed *somewhere else*, and the route it is
        // part of is the whole of what the companion has to work with.
        detector.observe(Turn {
            location: Some(245),
            fingerprint: "Steep Trail#dddd".to_string(),
            accepted: Some(true),
            produced: true,
            line: "up".to_string(),
            ..Turn::default()
        });

        assert!(
            !detector.already_tried("south"),
            "the same line from a different room is a route rather than a treadmill"
        );
        assert!(
            detector.already_tried("up"),
            "and this one really was tried from here"
        );
    }

    #[test]
    fn a_place_that_cannot_be_told_apart_still_refuses_the_whole_window() {
        // A bare keystroke prints nothing, so the newest turn has no fingerprint and cannot say where
        // "here" is. The whole window is asked instead, which is the older and stricter rule: the
        // refusal may be over-eager, but it is never a lock, because every *different* line goes through.
        let mut detector = Detector::new();
        for fingerprint in ["the same paragraph", "another paragraph"] {
            for _ in 0..3 {
                detector.observe(Turn {
                    fingerprint: fingerprint.to_string(),
                    accepted: Some(false),
                    produced: true,
                    line: "open the gate".to_string(),
                    ..Turn::default()
                });
            }
        }

        detector.observe(Turn {
            fingerprint: String::new(),
            accepted: Some(false),
            produced: false,
            line: String::new(),
            ..Turn::default()
        });

        assert!(detector.already_tried("open the gate"));
    }

    #[test]
    fn another_game_forgets_all_of_it() {
        let mut detector = Detector::new();
        for _ in 0..WINDOW {
            detector.observe(Turn {
                fingerprint: "the same paragraph".to_string(),
                accepted: Some(false),
                produced: true,
                ..Turn::default()
            });
        }
        assert!(detector.stuck());
        detector.forget();
        assert!(!detector.stuck());
        assert!(detector.seen.is_empty());
    }
}
