using Agent.Llm;

namespace Agent.Core.Loop;

/// <summary>
/// The companion's tool surface (04_companion.md §2 (`C6`–`C13`)): everything it *does*, as schemas.
/// <para>
/// This replaces the tag contract — <c>[say]</c>, <c>[ask]</c>, <c>[act]</c>, <c>[think]</c>,
/// <c>&lt;notebook&gt;</c>, <c>&lt;memories&gt;</c> — and the reason is in the lessons: a
/// delimiter contract is only as good as the examples in context, and the PoC watched a model
/// invent a fifth dialect twice in one evening. A tool call cannot be mis-bracketed, and a call
/// whose arguments will not parse is a refusal the harness can report, rather than a fragment of
/// prose the model reads back as lore.
/// </para>
/// <para>
/// What is left in the reply is <b>speech and nothing else</b>: no tags, no delimiters, no
/// continuation rules. Prose is prose. The reasoning arrives on its own channel from the
/// provider, so the model no longer has to label its own thinking with a marker and hope the
/// marker is read the way it meant it.
/// </para>
/// <para>
/// Note what is deliberately <b>not</b> here. There is no tool to read the screen: the screen
/// already arrives as the turn. There is no tool to look things up, because the only world is
/// the game and the only way to ask it anything is to type at it. A surface this small is one
/// a reader can hold in their head, and one the model cannot wander outside of.
/// </para>
/// </summary>
public static class CompanionTools
{
    /// <summary>Type one line into the game.</summary>
    public const string Type = "type";

    /// <summary>
    /// Press one key, at a prompt that is not asking for a command.
    /// <para>
    /// The second of the two shapes a Z-machine story can ask for, and the one the tag era had
    /// no way to express: a character input, which is what a `<Pulse una Tecla>`, a chapter menu
    /// or an `[Press any key to begin.]` is actually waiting for. With no argument it is a bare
    /// Return, and that is the universal answer — measured against dfrotz 2.54, where an empty
    /// line is reported to the game as a single carriage return and walks the story straight on
    /// to its next prompt.
    /// </para>
    /// <para>
    /// It is a separate call from <see cref="Type"/> rather than "a `type` with nothing in it"
    /// so that nobody ever has to decide what an empty command line means.
    /// </para>
    /// </summary>
    public const string PressKey = "press_key";

    /// <summary>
    /// Take on a job the player has handed over (§4.17).
    /// <para>
    /// The channel that lets the model correct the harness's own reading of a line. The
    /// tables in <see cref="UtteranceClassifier"/> recognise an errand by its shape, which
    /// fails on a spoken instruction — the one that starts mid-paragraph — and on every
    /// language those tables were not written in. The model has just read the line with the
    /// whole conversation in front of it, so when it disagrees, this is how it says so; what
    /// it says becomes the errand, with the errand's budget and the errand's endings.
    /// </para>
    /// </summary>
    public const string Errand = "errand";

    /// <summary>Ask the player something, and wait for the answer.</summary>
    public const string Ask = "ask";

    /// <summary>
    /// The end of an errand, said out loud (§5.3).
    /// <para>
    /// An errand used to end with the <em>absence</em> of a call: the brief promised that a reply
    /// which moved nothing <em>was</em> the report, and the repair detected it. That made every
    /// report two calls — the report, and the pass that worked out that it had been one — and it
    /// made "it said it was going and did not go" and "it has finished" the same reply, which is
    /// the one distinction the miss instrument exists to make and could not.
    /// </para>
    /// <para>
    /// Offered only while there is an errand in hand: with no errand there is nothing to call in.
    /// </para>
    /// </summary>
    public const string ReportBack = "report_back";

    /// <summary>Search the record of the play (05_memory.md §2 (`M9`–`M11`)): the companion's own eyes, on demand.</summary>
    public const string Recall = "recall";

    /// <summary>
    /// The map, and what has already been tried on it (06_tool_surface.md §3 (`T24`)).
    /// <para>
    /// The one question the record could never be asked by searching: `recall` finds a word, and a
    /// direction is not a word — `east` out of the gallery and `east` out of the first-floor
    /// corridor are the same eight characters with two different answers, and the evening of
    /// 2026-09-29 was lost to exactly that (`05_memory.md` §8). So the lookup is by <em>room</em>,
    /// and the answer is what every line typed in that room did.
    /// </para>
    /// </summary>
    public const string Map = "map";

    /// <summary>
    /// What is true of a thing at a place, and since when (05_memory.md §8 (`M41`)).
    /// <para>
    /// The question that costs turns when it cannot be asked. A door the companion closed three
    /// moves ago, a lever it pulled or did not, a thing whose words it has already read — all of it
    /// was on the record and none of it was reachable, so it was re-<em>discovered</em> by walking
    /// back and looking again.
    /// </para>
    /// <para>
    /// It answers with the game's own words and never with a verdict: what is <em>current</em> and
    /// what <em>changed</em> are worked out in the fork being played, and a change with nothing typed
    /// behind it is its own answer, because the world does move on its own.
    /// </para>
    /// </summary>
    public const string State = "state";

    /// <summary>
    /// A hypothesis about cause and effect (05_memory.md §8 (`M43`)).
    /// <para>
    /// The one thing the companion could not write down about the *game itself*: the notebook holds
    /// what it makes of the place, and the ledger holds what was tried — neither holds *"that lever
    /// opened the passage, and the lantern had to be lit first"*, which is the shape a real puzzle has.
    /// </para>
    /// <para>
    /// It is an **append**: nothing here overwrites anything, an entry may name an earlier one it
    /// kills, and the confidence is the companion's own words rather than a number the harness grades.
    /// The measurements stay in the harness's own document, which is regenerated every session — these
    /// are the companion's, and they are the only thing on disk that must survive that.
    /// </para>
    /// </summary>
    public const string Cause = "cause";

    /// <summary>Read the notebook back, whole or one section of it.</summary>
    public const string ReadNotes = "read_notes";

    /// <summary>
    /// The saves that exist for this game, and where each one sits (06_tool_surface.md §2.3 (`T14`–`T16`)).
    /// <para>
    /// The one lookup that has no other way of being answered: a Z-machine cannot be asked for a
    /// directory listing, and its own `restore` offers a single default name. Each save comes back
    /// with the note the core wrote beside it — the turn it was taken at, the attempt and the fork
    /// — because a save is a position and the conversation may be ahead of it.
    /// </para>
    /// </summary>
    public const string Saves = "saves";

    /// <summary>Replace one section of the notebook, leaving the rest untouched.</summary>
    public const string UpdateNotes = "update_notes";

    /// <summary>Replace the notebook: the companion's memory for this game.</summary>
    public const string WriteNotes = "write_notes";

    /// <summary>Replace the memories: what outlives the game.</summary>
    public const string WriteMemories = "write_memories";

    /// <summary>
    /// The write-up a player coming back to a game needs (05_memory.md §6 (`M27`–`M36`)).
    /// <para>
    /// The one tool that is not about playing, and it is offered on the write-up pass and nowhere
    /// else: what it takes is the report itself, in sections the harness renders into tabs. The map
    /// is the case worth naming — the diagram is drawn by the harness from the moves the core
    /// watched happen (B4), so what the model is asked for there is its own reading of the shape of
    /// the game, which is the half no record holds.
    /// </para>
    /// </summary>
    public const string Report = "report";

    /// <summary>
    /// A note about the harness, the session or the companion itself (05_memory.md §7 (`M37`–`M39`)).
    /// <para>
    /// The one thing the companion could not write down. The notebook is about the story, the
    /// memories are about the player, and the report is a document for somebody coming back to the
    /// game — so "my own note about the fog was right and I died anyway, because a restore had
    /// moved the world behind me" had nowhere to go, and was measured happening in the memories
    /// file instead, where it read as a fact about the player.
    /// </para>
    /// <para>
    /// It is <b>not a message and not a plan</b>: nothing written here is put in front of you
    /// again, nothing here can move the game, and the reader is the person who has to work out what
    /// went wrong with the session.
    /// </para>
    /// <para>
    /// **The description is the whole of the instruction, and it did not work** (checked
    /// 2026-09-21). There is no paragraph about this tool in `engine.txt` — deliberately, 02_game_boundary.md
    /// §10.6 — so the schema is where its job is stated, and the schema that shipped was a list of
    /// restrictions with a vague invitation at the end ("use it when there is something to say").
    /// Measured over one evening: **0 calls in 349 turns**, while *163* of the companion's own
    /// thinking lines were about the machinery — <i>"the harness says one call per turn"</i>,
    /// <i>"the harness insists"</i>, <i>"no record of the actual call being sent"</i>. It was
    /// offered on every pass and never reached for, so the moment was never the problem: what was
    /// missing was a description in words it could recognise from where it was standing, and the
    /// one sentence that says a note may travel beside the move rather than instead of it.
    /// </para>
    /// </summary>
    public const string MetaNote = "meta_note";

    /// <summary>
    /// The tools a write-up pass is offered.
    /// <para>
    /// The recording set plus <see cref="Report" />, and deliberately not in <see cref="Recording" />
    /// itself: taking stock is not a write-up, and a maintenance pass that could write a page nobody
    /// asked for is a pass that spends money on its own account.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ForTheWriteUp =>
        [.. All.Where(tool => tool.Name
            is Report or MetaNote or WriteNotes or WriteMemories or UpdateNotes or Recall or ReadNotes or Saves or Map or State)];

    /// <summary>
    /// The tools that record rather than move.
    /// <para>
    /// What a maintenance pass is offered. Taking stock is not a turn in front of the player and
    /// has no business moving anything — but it is the one place the notebook and the memories are
    /// normally written, and offering it <em>no</em> tools at all meant the brief could tell it to
    /// write its notes while giving it no way to do it. The split is by what a tool does, not by
    /// which pass is calling.
    /// </para>
    /// <para>
    /// Computed rather than cached: a static initialiser here would run before <see cref="All" />
    /// on the line below it, and would quietly be a list of nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Recording =>
        [.. All.Where(tool => tool.Name
            is WriteNotes or WriteMemories or UpdateNotes or Recall or ReadNotes or Saves or Map or State or MetaNote)];

    /// <summary>
    /// The single tool a write-up pass is offered on its last attempt.
    /// <para>
    /// Not a smaller surface for its own sake: it is the difference between asking for the page and
    /// requiring it. The first attempts offer everything a write-up legitimately needs — the check
    /// of the saves, the correction of a stale note — and a reply that reaches for any of those ends
    /// the pass, which is the defect that made a second write-up produce nothing at all. On the last
    /// attempt there is nothing left to gather, so a list containing one tool is a clearer
    /// instruction than any paragraph, and a model that answers at all writes the page.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ForTheLastWriteUp =>
        [.. All.Where(tool => tool.Name is Report)];

    /// <summary>
    /// Does this tool only read? — the question a retrieval round asks before going another round.
    /// <para>
    /// Asked of the tool rather than answered by a list of names here, and that is the whole point of
    /// the annotations: the round rule used to be spelled <c>is Recall or ReadNotes</c>, three frames
    /// away from the definitions it was talking about, and the day one of them disagreed the failure
    /// was silent. It did disagree — `saves` is a pure read and was still fatal to a write-up pass,
    /// which is the entire 2026-09-20 report bug — so this is now the property that means what it
    /// says. An unknown name is not read-only: a call nobody can answer ends the round.
    /// </para>
    /// </summary>
    public static bool IsReadOnly(string name) =>
        All.FirstOrDefault(tool => tool.Name == name)?.Annotations.ReadOnly ?? false;

    /// <summary>
    /// The tools a pass in front of the player is offered.
    /// <para>
    /// Everything, except that the errand's report is not offered when there is no errand to report
    /// on — the same rule as <see cref="Recording"/>, said from the other side: the surface is what
    /// the pass can legitimately do, and an errand nobody is on has no ending to call in.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> InFrontOf(bool onAnErrand) =>
        onAnErrand ? All : [.. All.Where(tool => tool.Name != ReportBack)];

    /// <summary>
    /// The same list minus the tools that write a whole file, for a pass that owes somebody a line.
    /// <para>
    /// A pass in front of the player has one job — the line — and the file writers are the one thing
    /// it can spend that job on instead. Measured 2026-10-02, on a real sitting: of the five
    /// *Nothing came of that, and nothing moved.* lines it wrote, **two were a pass that rewrote the
    /// notebook and said nothing** (one a repair, one an unprompted pass), so the only thing the
    /// player read was the harness's own sentence.
    /// </para>
    /// <para>
    /// This is <see cref="ForAGreeting"/>'s rule said once rather than twice: a list that cannot
    /// write the notebook cannot spend the greeting on it, and the same is true of a repair, a
    /// question, or anything else whose output was meant to be words. The *section* writes stay —
    /// <see cref="UpdateNotes"/> is cheap, it does not invalidate the whole prefix, and keeping the
    /// notebook current is ordinary play rather than a seam.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> WithoutTheWholeFileWrites(
        IReadOnlyList<ToolDefinition> tools) =>
        [.. tools.Where(tool => tool.Name is not (WriteNotes or WriteMemories))];

    /// <summary>
    /// The tools a greeting is offered: it may look, and it may speak, and that is the whole surface.
    /// <para>
    /// A greeting's entire output is one line to somebody who has just sat down, and the *first* line
    /// at that. Measured 2026-10-01: the join pass of a real sitting spent four model calls — three
    /// rounds of retrieval and then a notebook rewrite that emptied two sections and wrote 747 tokens
    /// — and greeted nobody, so the player's first sight of the companion was the harness saying
    /// *"Nothing came of that, and nothing moved."* The notebook was genuinely stale (the sitting had
    /// just been restored into a new generation) and that is exactly the point: it is a different
    /// pass's work, and the greeting has no business doing it. A surface is a clearer instruction
    /// than a paragraph, and a list that cannot write the notebook cannot spend the greeting on it.
    /// </para>
    /// <para>
    /// Reads are kept in full, because the greeting is worth writing from what the companion knows:
    /// the room, the map, whatever it wrote down before. <see cref="Ask" /> is kept for the same
    /// reason a person keeps it — *"shall I carry on?"* is a thing a greeting may say.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ForAGreeting(bool onAnErrand) =>
    [
        .. All.Where(tool =>
            tool.Annotations.ReadOnly
            || tool.Name == Ask
            || (onAnErrand && tool.Name == ReportBack)),
    ];

    /// <summary>
    /// The tools a line from the room is offered: everything that **reads**, and nothing else.
    /// <para>
    /// There is no `ask` in it, and that is the point of the surface rather than an omission: this
    /// pass is not allowed to want anything, and a question is a thing that wants an answer. There is
    /// nothing that moves and nothing that writes, for the same reason — it is the one pass in the
    /// system whose only job is to say something to somebody because they are both there
    /// (04_companion.md §5 (`C71`)).
    /// </para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ForALineFromTheRoom =>
        [.. All.Where(tool => tool.Annotations.ReadOnly)];


    /// <summary>
    /// Does this tool move the game? — the question the miss instrument asks before calling a reply
    /// <em>a move that never landed</em>.
    /// <para>
    /// The same property the pass subsets are built on (<see cref="ToolAnnotations.ActsOnTheGame" />),
    /// asked by name for the same reason <see cref="IsReadOnly"/> is: the answer has to come from the
    /// tool rather than from a list of names here, or the day they disagree the failure is silent.
    /// An unknown name does not move the game.
    /// </para>
    /// </summary>
    public static bool ActsOnTheGame(string name) =>
        All.FirstOrDefault(tool => tool.Name == name)?.Annotations.ActsOnTheGame ?? false;

    /// <summary>The tools as the model sees them, in the order they are worth reading.</summary>
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        ToolDefinition.Of(
            Type,
            "Type one line into the game, exactly as you would type it at the prompt. One line "
            + "per call, and one call per turn: the game has to answer before you can move again. "
            + "This is the only way you move, so a reply that says you are doing something and "
            + "carries no call has done nothing: if the words say you are going, this call is in "
            + "that same reply. Your friend is reading, so say what you are doing and why beside "
            + "it. Not at a prompt that is waiting for a key: there, the line is refused and it is "
            + "`press_key` you want.",
            """
            {
              "type": "object",
              "properties": {
                "line": {
                  "type": "string",
                  "description": "The command, exactly as a person would type it at the prompt."
                }
              },
              "required": ["line"]
            }
            """,
            ToolAnnotations.Acts),

        ToolDefinition.Of(
            PressKey,
            "Press a key, at a prompt that is not asking for a command. Use it when the game is "
            + "waiting for a keystroke rather than a line: a menu, a `[MORE]`, a `(press any key)`, "
            + "a `Pulse una Tecla`, a form. Leave the key out to press Return, which is what all of "
            + "those will take. **A key prompt may need more than one key** — a form's field or a "
            + "name is typed a character at a time — and giving this call the whole word sends its "
            + "characters in order, one prompt at a time, which is how such a screen is filled. Do "
            + "not use it at a `>` prompt, where a blank line is only a wasted turn.",
            """
            {
              "type": "object",
              "properties": {
                "key": {
                  "type": "string",
                  "description": "The key or keys to press, e.g. \"y\", \"2\" or a name for a form. Omit to press Return."
                }
              }
            }
            """,
            ToolAnnotations.Acts),

        ToolDefinition.Of(
            Errand,
            "Take on a job your friend has handed you. Call it when they have asked you for "
            + "something to go and do — scout it, work it out, run it through to the end — and you "
            + "are taking it on: put the job in your own words as the goal, then get on with it, "
            + "step by step, without asking permission for each one. Do not call it for a single "
            + "move they asked you to make (that is `type`), and not for something you decided to "
            + "do yourself.",
            """
            {
              "type": "object",
              "properties": {
                "goal": {
                  "type": "string",
                  "description": "The job, in your own words — what finishing it will look like."
                }
              },
              "required": ["goal"]
            }
            """,
            ToolAnnotations.Controls),

        ToolDefinition.Of(
            Ask,
            "Ask your friend a question and wait for the answer. Asking is something you do, not "
            + "something they work out: it stops your clock and stops you answering yourself. Use "
            + "it when the decision is genuinely theirs — risky, irreversible, expensive, or a "
            + "matter of taste — and not to check a plan they have already handed you.",
            """
            {
              "type": "object",
              "properties": {
                "text": {
                  "type": "string",
                  "description": "The question, in the words you would say out loud."
                }
              },
              "required": ["text"]
            }
            """,
            ToolAnnotations.Controls),

        ToolDefinition.Of(
            Recall,
            "Search the record of this game — every command that has been typed and what the screen "
            + "said back. Use it to check what you actually saw: what the game said about something, "
            + "when you tried a thing, how a room described itself. Ask for a word or phrase that "
            + "would have appeared on screen. You get excerpts back before you have to reply, so use "
            + "it whenever your memory is thinner than your confidence. A search that finds nothing "
            + "is an answer too. The result ends with the same turns as a JSON block — quote the turn, "
            + "attempt and fork from that rather than from the sentence. **And read every turn at its "
            + "own room**: the same direction leaves two different rooms depending on where it was "
            + "typed, so what you learned is only true of the room you learned it in.",
            """
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "A word or phrase that appeared on the screen, e.g. \"joystick\" or \"coin\"."
                }
              },
              "required": ["query"]
            }
            """,
            ToolAnnotations.Reads,
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string" },
                "found": { "type": "integer" },
                "matches": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "Turn": { "type": "integer" },
                      "Run": { "type": "integer" },
                      "Generation": { "type": "integer" },
                      "Input": { "type": "string" },
                      "Output": { "type": "string" },
                      "Fingerprint": { "type": "string" },
                      "Location": { "type": ["integer", "null"] }
                    }
                  }
                }
              }
            }
            """),

        ToolDefinition.Of(
            Map,
            "Look at the map: where a room leads, and what has already been tried there. Leave the "
            + "room out to ask about the one you are in. Ask about a room by its number — the number "
            + "the interpreter gives, the one in the header and in your notes — and the answer is "
            + "every line the record has typed in that room, what each one did, and where it led. "
            + "**The same direction out of two rooms goes to two different places**, which is why this "
            + "takes a room and not a direction. A room that has only been walked in a world this one "
            + "replaced comes back saying which world it belongs to; a room nobody has ever been in "
            + "comes back saying that, because the story mentioning a place is not the record having "
            + "been there.",
            """
            {
              "type": "object",
              "properties": {
                "room": {
                  "type": "string",
                  "description": "A room's number, or the name the story printed for it. Omit to ask about the room you are in."
                }
              }
            }
            """,
            ToolAnnotations.Reads,
            """
            {
              "type": "object",
              "properties": {
                "asked": { "type": ["string", "null"] },
                "found": { "type": "integer" },
                "generation": { "type": ["integer", "null"] },
                "place": { "type": "string" },
                "room": { "type": ["integer", "null"] },
                "name": { "type": ["string", "null"] },
                "as_of_turn": { "type": "integer" },
                "from": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "typed": { "type": "string" },
                      "outcome": { "type": "string" },
                      "room": { "type": ["integer", "null"] },
                      "name": { "type": ["string", "null"] },
                      "said": { "type": ["string", "null"] },
                      "times": { "type": "integer" },
                      "first_seen": { "type": "integer" },
                      "last_seen": { "type": "integer" }
                    }
                  }
                },
                "tried": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "verb": { "type": "string" },
                      "thing": { "type": "string" },
                      "said": { "type": "string" },
                      "turn": { "type": "integer" }
                    }
                  }
                },
                "word": { "type": ["string", "null"] },
                "known": { "type": ["boolean", "null"] }
              }
            }
            """),

        ToolDefinition.Of(
            State,
            "What the game has said about a thing at a place, and since when. Give a `thing` to ask "
            + "about one — part of its name is enough — or leave it out to see everything anyone has "
            + "typed at in this room **and every command that has already been tried here with what "
            + "each one returned**. Leave `room` out for the room you are in. You get every value the "
            + "game has printed about it, which one is current **in the world you are playing**, and "
            + "what has changed and what was typed when it did. Use it instead of walking back to look "
            + "at something again: if the game has already told you, this is cheaper than going there. "
            + "Nothing is carried across a reload, so after a restore the answer is what has been seen "
            + "since it — and if nothing has, it says so rather than repeating the old world. Give "
            + "`word` instead to ask whether the story's own dictionary holds a word at all, which is "
            + "the difference between a word this game has never heard of and one it heard and "
            + "refused.",
            """
            {
              "type": "object",
              "properties": {
                "room": {
                  "type": "string",
                  "description": "A room's number, or the name the story printed for it. Omit for the room you are in."
                },
                "thing": {
                  "type": "string",
                  "description": "Part of a thing's name, e.g. \"door\". Omit for everything at the room, and for the list of what has been tried there."
                },
                "word": {
                  "type": "string",
                  "description": "A single word to check against the story's own dictionary, e.g. \"soot\"."
                }
              }
            }
            """,
            ToolAnnotations.Reads,
            """
            {
              "type": "object",
              "properties": {
                "asked": { "type": ["string", "null"] },
                "found": { "type": "integer" },
                "generation": { "type": ["integer", "null"] },
                "place": { "type": "string" },
                "room": { "type": ["integer", "null"] },
                "things": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "states": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": {
                            "asked_with": { "type": "string" },
                            "values": { "type": "array", "items": { "type": "string" } },
                            "current": { "type": ["string", "null"] },
                            "since": { "type": ["integer", "null"] },
                            "last_seen": { "type": ["integer", "null"] },
                            "changes": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "from": { "type": "string" },
                                  "to": { "type": "string" },
                                  "at": { "type": "integer" },
                                  "by": { "type": "array", "items": { "type": "string" } }
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """),

        ToolDefinition.Of(
            ReadNotes,
            "Read your notebook back. Leave the section out to read the whole of it; give a "
            + "heading — or part of one — to read just that section. The notebook is already in "
            + "front of you on every call; reach for this when you want a section cleanly, or to "
            + "check what is actually written before you change it.",
            """
            {
              "type": "object",
              "properties": {
                "section": {
                  "type": "string",
                  "description": "A heading, or part of one, e.g. \"Open questions\". Omit for the whole notebook."
                }
              }
            }
            """,
            ToolAnnotations.Reads),

        ToolDefinition.Of(
            Saves,
            "See the saves that exist for this game: their names, and when each was taken and at "
            + "which turn. Use it when your notes mention a save you are not sure still exists, or "
            + "when you want to know which positions are on the table at all. A save is a position, "
            + "not a memory: the world behind it can be several moves behind the conversation. The "
            + "result ends with the same saves as a JSON block — take the turn, attempt and fork from "
            + "that, not from the sentence, when you say which world a position belongs to.",
            """
            {
              "type": "object",
              "properties": {}
            }
            """,
            ToolAnnotations.Reads,
            """
            {
              "type": "object",
              "properties": {
                "game": { "type": "string" },
                "found": { "type": "integer" },
                "saves": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "File": { "type": "string" },
                      "Name": { "type": "string" },
                      "Turn": { "type": ["integer", "null"] },
                      "Run": { "type": "integer" },
                      "Fork": { "type": "integer" },
                      "Reason": { "type": "string" },
                      "SavedAt": { "type": "string" },
                      "Bytes": { "type": "integer" },
                      "IsTheRestartTarget": { "type": "boolean" }
                    }
                  }
                }
              }
            }
            """),

        ToolDefinition.Of(
            Cause,
            "Write down what you think made something happen — a hypothesis about *this game's* workings, "
            + "so you do not have to work it out twice. Give the cause and the effect in your own words, "
            + "cite the turn numbers it rests on if it rests on particular moments, and say how sure you "
            + "are in your own words (`certain`, `probably`, `a guess`) — a guess written down as a guess "
            + "is worth more than the same guess written down as a fact. **When the game does something "
            + "that kills one of your own beliefs, say so**: give `retracts` the number of the entry it "
            + "kills. A lever that closed a door three turns after it opened one is why this has to be "
            + "possible. This is for the game, not the machinery (that is `meta_note`) and not the story's "
            + "own state (the harness measures that).",
            """
            {
              "type": "object",
              "properties": {
                "cause": {
                  "type": "string",
                  "description": "What you believe did it, in your own words."
                },
                "effect": {
                  "type": "string",
                  "description": "What you believe happened, in your own words or the game's."
                },
                "support": {
                  "type": "string",
                  "description": "Turn numbers this rests on, e.g. \"537, 538\", if it rests on particular moments."
                },
                "confidence": {
                  "type": "string",
                  "description": "How sure you are, in your own words."
                },
                "retracts": {
                  "type": ["integer", "null"],
                  "description": "The number of an earlier entry this one kills, if it kills one."
                }
              },
              "required": ["cause", "effect"]
            }
            """,
            ToolAnnotations.Adds),

        ToolDefinition.Of(
            WriteNotes,
            "Replace your notebook for this game with the complete markdown you want it to hold "
            + "from now on. It is a replacement, not an edit: whatever you leave out is gone. "
            + "Call it when you have learned something that changes what you are doing — not "
            + "every turn. Only what is true of the story belongs here; a fault of the machinery "
            + "is a `meta_note`.",
            """
            {
              "type": "object",
              "properties": {
                "markdown": {
                  "type": "string",
                  "description": "The whole notebook, including the headings it should keep."
                }
              },
              "required": ["markdown"]
            }
            """,
            ToolAnnotations.Writes),

        ToolDefinition.Of(
            UpdateNotes,
            "Replace ONE section of your notebook, keeping the rest untouched. Give the heading "
            + "as it is written in the notebook and the complete new text for that section — not a "
            + "patch. This is how you fix a note that has gone stale without risking the rest of "
            + "the file.",
            """
            {
              "type": "object",
              "properties": {
                "section": {
                  "type": "string",
                  "description": "The heading of the section to replace, e.g. \"Open questions\"."
                },
                "markdown": {
                  "type": "string",
                  "description": "The complete new text for this section, not a patch."
                }
              },
              "required": ["section", "markdown"]
            }
            """,
            ToolAnnotations.Writes),

        ToolDefinition.Of(
            WriteMemories,
            "Replace the memories that outlive this game: who your friend is, the evenings you "
            + "have had together, ideas worth carrying to the next one. A replacement, not an "
            + "edit, and a few bullet lines rather than an essay. This file is read back to you "
            + "in every future game, so a workaround for the machinery written down here is read "
            + "later as a fact about your friend — that is a `meta_note` instead.",
            """
            {
              "type": "object",
              "properties": {
                "markdown": {
                  "type": "string",
                  "description": "The whole memories file, headings included."
                }
              },
              "required": ["markdown"]
            }
            """,
            ToolAnnotations.Writes),

        ToolDefinition.Of(
            Report,
            "Write the report for a player returning to this game after a long absence. Not in your "
            + "own voice: it is a document about the evening, not a message to your friend, so the "
            + "second person belongs to them and the humour belongs to neither. Use exactly these "
            + "second-level headings, in this order, and leave one out only if there is genuinely "
            + "nothing to put in it:\n"
            + "`## Where we are` — the position, the inventory, what is true right now.\n"
            + "`## Characters` — everyone the story has shown you and what each of them wants: who "
            + "they are, what they have asked for, what you know about how they will behave. It is "
            + "the one part of this page that cannot be looked up, because it is a reading of people "
            + "rather than a fact about the record.\n"
            + "`## What is left to do` — every open thread, including the ones you have parked, and "
            + "what would move each of them.\n"
            + "`## What we have solved` — what is already done, and what it cost to learn: the dead "
            + "ends, the doors that are locked for good, the things we were wrong about.\n"
            + "`## The map` — how you read the shape of the game: which places connect to which, "
            + "and what you make of it. The diagram is not yours and you are not asked for it — the "
            + "harness draws it from the moves the core watched happen, for every turn on the "
            + "record, so what is wanted here is the reading the record cannot give: which of those "
            + "places you believe are the same room, what the dead ends have in common, where you "
            + "think two worlds meet.\n"
            + "`## Worth remembering` — the things a person would otherwise have to find out again: "
            + "a password, a trick, a room that matters, a warning.\n"
            + "Two more tabs are written for you from the core's own record — `Items`, which is the "
            + "game's last answer to the inventory command, and `Events`, which is every turn that "
            + "happened — so do not write those two: quoted is what is wanted there, not retold.\n"
            + "Bullet lines rather than paragraphs where a list is what it is, and no preamble: the "
            + "page has its own heading.",
            """
            {
              "type": "object",
              "properties": {
                "markdown": {
                  "type": "string",
                  "description": "The report, with the second-level headings it asks for."
                }
              },
              "required": ["markdown"]
            }
            """,
            ToolAnnotations.Writes),

        ToolDefinition.Of(
            MetaNote,
            "Write down a time the machinery got in the way — not the story. **You are the only one "
            + "who sees it happen, and the reader is the person who has to fix it**, so if you do "
            + "not write it down, nobody can. The moments it is for, in the words this harness uses:\n"
            + "- you were told your last reply carried no move, and you had thought you had sent it\n"
            + "- a line of yours was refused, stopped, or dropped before it reached the game, or the "
            + "screen stayed put when you expected an answer\n"
            + "- the world moved under you: a restore, a fork, a save older than your notes, a wait "
            + "that produced nothing at all\n"
            + "- the screen was asking something you had not been told about — a form, a key, a menu\n"
            + "- something about your own performance you would want to know next time\n"
            + "Call it in the **same reply** as whatever else you are doing — it is a second call "
            + "beside the move, never a turn of its own — and one line is enough. Nothing here is "
            + "ever read back to you, so the plain truth about the machinery is the whole of what is "
            + "wanted. What is true of the *story* is the notebook, and what is to say to your "
            + "friend is speech.",
            """
            {
              "type": "object",
              "properties": {
                "note": {
                  "type": "string",
                  "description": "What happened, in one or two lines, and what would have helped."
                },
                "about": {
                  "type": "string",
                  "description": "A word or two for what this is about, used as the heading — harness, myself, the session."
                }
              },
              "required": ["note"]
            }
            """,
            ToolAnnotations.Adds),

        ToolDefinition.Of(
            ReportBack,
            "Call the errand in: it is finished, or you have gone as far as it is worth going, or "
            + "you have learned something your friend should hear now. What you say beside the call "
            + "is the report — what you found, what it cost, what is still open — so it has to stand "
            + "on its own, and it is the last thing said about this errand. Anything worth keeping in "
            + "the notebook goes in the same reply. The errand is over the moment this is called: "
            + "there is nothing left in hand, so do not call it to pause, and do not call it in the "
            + "same reply as a move — one or the other, never both.",
            """
            {
              "type": "object",
              "properties": {}
            }
            """,
            ToolAnnotations.Controls),
    ];
}
