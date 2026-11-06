using System.Globalization;
using System.Text.Json;

namespace Agent.Notebook;

/// <summary>
/// The world as the record shows it: one derived document per game, holding where the player has
/// been, what each line did, and what the game has printed about the things in it
/// (05_memory.md §3 (`M40`)).
/// <para>
/// <b>Derived, and only derived.</b> Delete the file and it rebuilds from
/// <c>observations/&lt;stem&gt;.jsonl</c> and the run book with nothing lost. That is the whole of
/// the design: a projection that cannot be rebuilt is a second truth, and a second truth drifts —
/// which is exactly what happened to the notebook, and why the notebook is being re-founded rather
/// than extended.
/// </para>
/// <para>
/// <b>The model never writes it.</b> Every row is a reading of a command the player typed, or of
/// the work of a verb over such a command. Reading the story's <em>prose</em> is the one thing the
/// harness must never do (`G31`), so nothing here decides what a sentence means: a value is the
/// game's own words, quoted, and the words are never interpreted. What the companion makes of them
/// lives in its notes.
/// </para>
/// <para>
/// <b>Rows are observations, not conclusions.</b> A place is somewhere the interpreter said the
/// player was. An edge is one line at one place and what came of it. A thing-row is one value the
/// game printed about one thing. Nothing here decides which of several values is <em>current</em>:
/// that is a question asked of this document, in the fork it is asked about, and a fork is a
/// different world (05_memory.md §2 (`M13`)).
/// </para>
/// </summary>
public static class WorldState
{
    /// <summary>The subdirectory of the state directory, beside <c>observations/</c> and <c>runs/</c>.</summary>
    public const string Folder = "world";

    /// <summary><c>&lt;state&gt;/world/&lt;stem&gt;.json</c> — one document per game.</summary>
    public static string PathFor(string stateDirectory, string gameStem) =>
        Path.Combine(stateDirectory, Folder, gameStem + ".json");

    /// <summary>
    /// When something was seen: the game's own turn number, and the fork of the world it was seen
    /// in.
    /// <para>
    /// A turn number alone would be a lie the first time the world was reloaded, because the same
    /// numbers come round again in the next fork. Zero means the row predates the record's own
    /// stamp and says so rather than claiming the first attempt (`M13.1`).
    /// </para>
    /// </summary>
    public sealed record Seen(long Turn, long Gen);

    /// <summary>
    /// Somewhere the player has stood.
    /// <para>
    /// <paramref name="Id"/> is this document's own handle — a small number, given in the order the
    /// places were first met — and it exists because a place can be met <em>twice</em> under two
    /// different kinds of identity: a story whose interpreter reports the room number on some turns
    /// and not others would otherwise grow a second circle for every room walked back into. The
    /// handle is what an edge points at; the identity is <paramref name="Number"/> and
    /// <paramref name="Names"/>.
    /// </para>
    /// <para>
    /// <paramref name="Number"/> is the interpreter's own object number for the room, which is the
    /// one place identity in the record that is not a reading of the prose (`02_game_boundary.md`
    /// §4.3). It is null when the record never carried one, and null is <em>cannot say</em> rather
    /// than a guess. <paramref name="Names"/> are the titles the story printed, in the order they
    /// arrived; a room renamed mid-story has two, and the first is the one a later name is joined by.
    /// </para>
    /// </summary>
    public sealed record Place(
        string Id,
        long? Number,
        IReadOnlyList<string> Names,
        Seen FirstSeen,
        Seen LastSeen);

    /// <summary>
    /// One line at one place, and what came of it.
    /// <para>
    /// <paramref name="Outcome"/> is the whole point of the type:
    /// </para>
    /// <list type="bullet">
    /// <item><c>moved</c> — the player went somewhere, and <paramref name="To"/> says where;</item>
    /// <item><c>blocked</c> — the line asked to go somewhere and the player is still where they
    /// were. A locked door, a wall, and a direction with no exit are the same outcome and stay
    /// three different facts, because <see cref="Result"/> is the game's own words;</item>
    /// <item><c>unplaced</c> — the line asked to go somewhere and the record cannot say where the
    /// player ended up, because the interpreter reported no room and the story printed no title.
    /// It is its own outcome rather than an omission: the alternative is a move that happened and
    /// is nowhere in the document, which is the silence this whole design exists to remove.</item>
    /// </list>
    /// <para>
    /// A row is keyed on <c>(fork, from, typed, outcome, to)</c>, so `east` that led to the office
    /// and `east` that led to the bedroom are two rows and neither is overwritten. Two rows for one
    /// line in one fork is not a contradiction — it is a door that was locked and was later opened,
    /// and both halves of that are true.
    /// </para>
    /// </summary>
    public sealed record Edge(
        long Gen,
        string From,
        string Typed,
        string? To,
        string Outcome,
        string? Result,
        long FirstSeen,
        long LastSeen,
        long Times);

    /// <summary>
    /// A value the game printed about a thing at a place, and when it printed it
    /// (05_memory.md §3 (`M40.2`)).
    /// <para>
    /// <paramref name="Property"/> is the verb the player typed, and nothing cleverer is available:
    /// what a thing's state <em>is</em> cannot be worked out without reading the story, and an
    /// aspect the harness invented ("up position", "locked state") would have to be filled with a
    /// value nobody observed. The verb is a fact, the thing is a fact, the value is the game's own
    /// words, and the reading of them is the companion's business.
    /// </para>
    /// <para>
    /// One row per distinct value rather than one row per turn: the door that was closed and then
    /// opened and then closed again has two rows with counts on them, and the set of its possible
    /// states is simply the values. <em>Current</em> is not stored, because current is a question
    /// about a fork.
    /// </para>
    /// </summary>
    public sealed record Thing(
        string Place,
        string Name,
        string Property,
        string Value,
        long Gen,
        long FirstSeen,
        long LastSeen,
        long Times);

    /// <summary>
    /// One attempt: what was typed at what place, and what the game said back, in the game's own
    /// words (05_memory.md §3 (`M40.3`)).
    /// <para>
    /// Append-only and unaggregated, so "what have I already tried here, and what did each one
    /// return" is a search rather than a reconstruction. The rows are the same rows
    /// <see cref="Thing"/> is gathered from, kept whole: a thing-row says what is known about a
    /// thing, and these say what was done and what happened, every time.
    /// </para>
    /// <para>
    /// <paramref name="Place"/> may be null — the line was typed where the record cannot say. The
    /// row is still written: a command that was typed is a command that was typed.
    /// </para>
    /// </summary>
    public sealed record Action(
        string? Place,
        string Verb,
        string Object,
        string Result,
        long Turn,
        long Gen);

    /// <summary>
    /// A walk: the lines typed while the player stayed in one place, in the order they were typed.
    /// <para>
    /// **A place whose cells cannot be told apart has no rooms, and a walk is what it has instead**
    /// (05_memory.md (`M33.9`)). A story may answer one object number with a dozen cells — a maze
    /// prints one screen in every one of them — so no identity the record can key on distinguishes
    /// them, and the order the lines were typed in is the only thing they do not share. A route is a
    /// sequence, and a sequence can be replayed and turned round.
    /// </para>
    /// <para>
    /// A walk ends when a step leaves the place, so its last step is the one that left. Its steps
    /// are the lines that could move — what <see cref="Edge"/> records — so `look` is nobody's step,
    /// and a line that went nowhere is still a step, because *I tried that here and it did not go* is
    /// half of what a route is.
    /// </para>
    /// <para>
    /// **The order is the record's, and the turn numbers are not it**: a reload rewinds the
    /// interpreter's counter, so the rows either side of one share numbers (05_memory.md (`M13`)),
    /// and a walk ordered by them would put a later step first. The walks in the document are in the
    /// order they were walked, and that is what a reader replays.
    /// </para>
    /// <para>
    /// A **route** is a chain of walks: each one's last step arrives at the place the next one was
    /// walked in. That is the whole of what a path is for a story that cannot be mapped.
    /// </para>
    /// </summary>
    public sealed record Walk(
        long Gen,
        string Place,
        long StartTurn,
        long EndTurn,
        IReadOnlyList<WalkStep> Steps);

    /// <summary>
    /// One step of a walk: the line, what came of it, and where that left the player.
    /// <para>
    /// <see cref="To"/> is where the step <em>left</em> them, and it is null when it left them where
    /// they stood — an action (`open door`) ends in the place it was typed in, and a step that
    /// "arrived" at the place it started from would let a walk be read as the route into its own
    /// room. The place it was typed in is the walk's own <see cref="Walk.Place"/>.
    /// </para>
    /// </summary>
    public sealed record WalkStep(
        long Turn,
        string Typed,
        string Outcome,
        string? To);

    /// <summary>
    /// What the document is about, and as of when.
    /// <para>
    /// <paramref name="Run"/> and <paramref name="Generation"/> are read from the run book rather
    /// than worked out from the record, because a restore is machinery and never lands as a turn —
    /// nothing in the record says a world was forked (02_game_boundary.md §6 (`G33`)). Null is
    /// <em>cannot say</em>, and readers that filter by fork must say so rather than assume the first.
    /// </para>
    /// <para>
    /// <paramref name="ForkTurn"/> is the first turn of the fork in hand, and null for a first
    /// attempt, where there is no fork to point at.
    /// </para>
    /// </summary>
    public sealed record Header(
        string Stem,
        long? Run,
        long? Generation,
        long? ForkTurn,
        long AsOfTurn);

    /// <summary>The document itself, as it is written to disk.</summary>
    public sealed record Document(
        Header Header,
        IReadOnlyList<Place> Places,
        IReadOnlyList<Edge> Edges,
        IReadOnlyList<Walk> Walks,
        IReadOnlyList<Thing> Things,
        IReadOnlyList<Action> Actions);

    /// <summary>
    /// How it is written. Indented, because a person opening this file should be able to read it,
    /// and snake case, because the field names are part of the file's contract with whatever reads
    /// it later and a policy is one place to look rather than forty attributes.
    /// </summary>
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>
    /// Build the whole document from the record and the run book. This is the ground truth: it
    /// reads nothing but the two files, so it cannot inherit a mistake from an earlier build.
    /// </summary>
    public static Document Build(string stateDirectory, string gameStem)
    {
        var book = RunBook.Current(stateDirectory, gameStem);
        var world = new Accumulator
        {
            Stem = gameStem,
            Run = book?.Number,
            Generation = book is { Generation: > 0 } ? book.Value.Generation : null,
        };

        foreach (var turn in ObservationLog.All(stateDirectory, gameStem))
        {
            Step(world, turn);
        }

        return Freeze(world);
    }

    /// <summary>
    /// Build it and write it. Returns the document written, so a caller can log what it holds.
    /// </summary>
    public static Document Rebuild(string stateDirectory, string gameStem)
    {
        var document = Build(stateDirectory, gameStem);
        Write(stateDirectory, gameStem, document);
        return document;
    }

    /// <summary>
    /// Fold one landed turn into the document on disk.
    /// <para>
    /// The cheap path, and deliberately not the only one: the whole document is rebuilt whenever a
    /// session opens, so this can be wrong for one turn and be corrected by the next open rather
    /// than being the place the truth lives. When the document is missing or unreadable this falls
    /// back to the full rebuild, because a projection that quietly starts from nothing is worse
    /// than one that costs a second.
    /// </para>
    /// <para>
    /// Never throws: a projection that cannot be written is worth less than a game that keeps
    /// playing, and the next open rebuilds it.
    /// </para>
    /// </summary>
    public static void Append(string stateDirectory, string gameStem, ObservationLog.RecordedTurn turn)
    {
        try
        {
            var existing = Read(stateDirectory, gameStem);
            if (existing is null)
            {
                Rebuild(stateDirectory, gameStem);
                return;
            }

            var world = Thaw(existing);
            Step(world, turn);
            Write(stateDirectory, gameStem, Freeze(world));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing here is worth failing a turn over.
        }
    }

    /// <summary>The document as it is on disk, or null when there is not one to read.</summary>
    public static Document? Read(string stateDirectory, string gameStem)
    {
        if (string.IsNullOrWhiteSpace(gameStem))
        {
            return null;
        }

        try
        {
            var path = PathFor(stateDirectory, gameStem);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Format);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The document as text, exactly as it is written to disk.</summary>
    public static string ToJson(Document document) =>
        JsonSerializer.Serialize(document, Format) + "\n";

    private static void Write(string stateDirectory, string gameStem, Document document)
    {
        var path = PathFor(stateDirectory, gameStem);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson(document));
    }

    // -- the fold ---------------------------------------------------------------------------------

    /// <summary>
    /// The document while it is being built, as mutable rows.
    /// <para>
    /// A fold over the record rather than a second pass over a built document, because the two
    /// halves of this block have to agree: <see cref="Build"/> folds the whole record,
    /// <see cref="Append"/> folds one turn onto a thawed document, and the test is that the two
    /// arrive at the same bytes. If they cannot be the same function of the same rows, one of them
    /// is a second truth.
    /// </para>
    /// </summary>
    private sealed class Accumulator
    {
        public string Stem = string.Empty;
        public long? Run;
        public long? Generation;

        /// <summary>Places in the order they were first met: the handle is the position.</summary>
        public readonly List<MutablePlace> Places = [];

        public readonly Dictionary<string, MutablePlace> ByHandle = new(StringComparer.Ordinal);
        public readonly Dictionary<long, MutablePlace> ByNumber = [];
        public readonly Dictionary<string, MutablePlace> ByName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Edges, keyed on everything an edge is: fork, from, line, outcome and where.</summary>
        public readonly Dictionary<string, MutableEdge> Edges = new(StringComparer.Ordinal);

        public readonly Dictionary<string, MutableThing> Things = new(StringComparer.Ordinal);
        public readonly List<Action> Actions = [];

        /// <summary>Walks in the order they started, and the one still open (05_memory.md (`M33.9`)).</summary>
        public readonly List<MutableWalk> Walks = [];
        public MutableWalk? OpenWalk;

        /// <summary>Where the player was last seen to be. Null until the record says.</summary>
        public MutablePlace? Here;

        /// <summary>The turn the fork in hand began at, or 0 while it has not been seen yet.</summary>
        public long ForkTurn;
        public long AsOfTurn;
    }

    private sealed class MutablePlace
    {
        public required string Handle;
        public long? Number;
        public readonly List<string> Names = [];
        public bool Named;
        public long FirstTurn;
        public long FirstGen;
        public long LastTurn;
        public long LastGen;
    }

    private sealed class MutableEdge
    {
        public long Gen;
        public required string From;
        public required string Typed;
        public string? To;
        public required string Outcome;
        public string? Result;
        public long FirstTurn;
        public long LastTurn;
        public long Times;
    }

    private sealed class MutableThing
    {
        public required string Place;
        public required string Name;
        public required string Property;
        public required string Value;
        public long Gen;
        public long FirstTurn;
        public long LastTurn;
        public long Times;
    }

    private sealed class MutableWalk
    {
        public long Gen;
        public required string Place;
        public long StartTurn;
        public long EndTurn;
        public readonly List<WalkStep> Steps = [];
    }

    /// <summary>
    /// One turn, folded in. The rules are the ones the map has always been drawn by — a number beats
    /// a name, a name joins a place that has no number of its own, and a turn that identifies no
    /// place at all leaves the player where they were — with the two things the catch-up report
    /// throws away put back: what came of each line, and the fork it happened in.
    /// </summary>
    private static void Step(Accumulator world, ObservationLog.RecordedTurn turn)
    {
        var typed = turn.Input.Trim();
        var way = CatchupReport.Direction(typed);
        var name = TurnReading.RoomName(turn.Output);
        var was = world.Here;

        // The fold does not sort: it takes the record in the order the core wrote it. The record is
        // append-only, so a turn that arrives out of order is a turn the record has out of order,
        // and sorting here would hide it.
        world.AsOfTurn = Math.Max(world.AsOfTurn, turn.Turn);
        if (world.Generation is { } current && turn.Generation == current && world.ForkTurn == 0)
        {
            world.ForkTurn = turn.Turn;
        }

        var at = Where(world, turn.Location, name, way, turn.Turn, turn.Generation);

        if (at is not null)
        {
            at.LastTurn = turn.Turn;
            at.LastGen = turn.Generation;
        }

        // What the line did, for the ledger, against the place it was typed at rather than the one
        // it ended in: a command acts on the room the player is standing in.
        Ledger(world, was, typed, turn);

        if (at is null)
        {
            // Nothing here says where the player is. That is not a move and not a failed move: the
            // record cannot say, and the ledger above already holds the line.
            if (way is not null && was is not null)
            {
                Record(world, was, typed, way, null, "unplaced", turn);
            }

            return;
        }

        if (was is null || was.LastGen != turn.Generation)
        {
            // Either the first place the record knows about, or the first place it knows about on
            // this side of a fork. Neither is a move: the line on that turn did not take the player
            // anywhere, the world was replaced underneath them, and an edge drawn across the seam
            // would say the gallery is one room away from the office.
            world.Here = at;
            return;
        }

        if (ReferenceEquals(was, at))
        {
            // Asked to go somewhere and still here — and there are two measurably different ways to
            // still be here, which is the whole of this branch (05_memory.md §6.4 (`M33.8`)).
            //
            // The interpreter's number is unchanged either way, so a reader given only the number says
            // "did not go" about both. What tells them apart is whether the story printed a room
            // again: a wall answers with a sentence, and a maze whose rooms share one object number
            // answers with a room. Measured on Wishbringer's Steep Trail, 2026-10-02 — six segments
            // share object 80, so every compass move on it read as `west did not go: "Steep Trail"`,
            // and the companion spent a wedge of that sitting re-deriving a loop that was not
            // happening, then wrote four notes about a map that could not see the rooms.
            //
            // So the edge records which of the two it saw, and names the measurement rather than the
            // conclusion: `reprinted` is "a room came back and the number did not change", which is a
            // fact, where "did not go" is a claim the record is not entitled to make. Only a line that
            // goes somewhere is an edge; `look` is not a failed move.
            if (way is not null)
            {
                Record(
                    world,
                    was,
                    typed,
                    way,
                    null,
                    name.Length > 0 ? "reprinted" : "blocked",
                    turn);
            }

            return;
        }

        if (Verb(typed).Length == 0 || ReportOnly(typed))
        {
            // A room change needs a line that could have caused it, and two cases say it could not. The
            // first is a verb that only reports (`look`), added after the UAT of 2026-09-29 found
            // `1 -- look --> 3`. The second is **no line at all**: a `/restart` moved the player back to
            // the Hilltop and the fold wrote `3 --  --> 1`, a move made by nobody, which the map would
            // then offer as a way out of a room.
            //
            // The place stays recorded — the turn is still evidence of where the player was — and what is
            // refused is the **edge**, because an edge is a claim that this line led there. Where the line
            // only reports, or where there is no line, the honest answer is that the record does not say
            // how they got there.
            world.Here = at;
            return;
        }

        Record(world, was, typed, way, at, "moved", turn);
        world.Here = at;
    }

    /// <summary>
    /// A line that reports rather than acts, so a place seen after one is not an edge from it.
    /// <para>
    /// Deliberately a short list held here rather than the classifier's tables: this is the fold's own
    /// rule about what a line can have caused, not the harness's reading of what the player meant.
    /// </para>
    /// </summary>
    private static readonly string[] ReportOnlyVerbs =
    [
        "look", "l", "examine", "x", "ex", "read", "search", "listen",
        "inventory", "i", "score", "wait", "z",
    ];

    /// <summary>Is this line one of the verbs that only report?</summary>
    private static bool ReportOnly(string typed)
    {
        var word = Verb(typed);
        return word.Length > 0
            && ReportOnlyVerbs.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which place a turn puts the player in, or null when the record does not say.
    /// <para>
    /// The three cases, in the order they are applied: the interpreter's own number if it gave one;
    /// the name the story printed, joined to a place that has no number of its own rather than
    /// making a second one for the same room; and nothing at all — a turn with neither a number nor
    /// a title is a turn the record cannot place, and inventing one is the defect this type is
    /// built to avoid.
    /// </para>
    /// </summary>
    private static MutablePlace? Where(
        Accumulator world,
        long? location,
        string name,
        string? way,
        int turn,
        long gen)
    {
        if (location is { } number)
        {
            var at = world.ByNumber.TryGetValue(number, out var byNumber)
                ? byNumber
                : name.Length > 0
                    && world.ByName.TryGetValue(name, out var byItsName)
                    && byItsName.Number is null
                        ? byItsName
                        : Make(world, name, number, turn, gen);

            if (at.Number is null)
            {
                at.Number = number;
                world.ByNumber[number] = at;
            }

            if (name.Length > 0 && !at.Named)
            {
                // A place first met on a turn that printed no title takes the first title that
                // arrives afterwards: the same room, and "Post Office" is a better thing to write
                // under a disc than "room 94".
                at.Names.Add(name);
                at.Named = true;
                world.ByName.TryAdd(name, at);
            }

            return at;
        }

        if (way is not null && name.Length > 0)
        {
            // A line that goes somewhere, and the story answered with a room: the name is the only
            // thing left to identify it by, and a name already known is the room it was given to.
            return world.ByName.TryGetValue(name, out var seen) ? seen : Make(world, name, null, turn, gen);
        }

        return null;
    }

    private static MutablePlace Make(Accumulator world, string name, long? number, int turn, long gen)
    {
        var place = new MutablePlace
        {
            Handle = (world.Places.Count + 1).ToString(CultureInfo.InvariantCulture),
            Number = number,
            FirstTurn = turn,
            FirstGen = gen,
            LastTurn = turn,
            LastGen = gen,
        };

        if (name.Length > 0)
        {
            place.Names.Add(name);
            place.Named = true;
            world.ByName.TryAdd(name, place);
        }

        world.Places.Add(place);
        world.ByHandle[place.Handle] = place;
        if (number is { } known)
        {
            world.ByNumber[known] = place;
        }

        return place;
    }

    private static void Record(
        Accumulator world,
        MutablePlace from,
        string typed,
        string? way,
        MutablePlace? to,
        string outcome,
        ObservationLog.RecordedTurn turn)
    {
        var key = string.Join(
            '\u001f',
            turn.Generation.ToString(CultureInfo.InvariantCulture),
            from.Handle,
            typed.ToLowerInvariant(),
            outcome,
            to?.Handle ?? "-");

        if (!world.Edges.TryGetValue(key, out var edge))
        {
            edge = new MutableEdge
            {
                Gen = turn.Generation,
                From = from.Handle,
                Typed = way ?? typed.ToLowerInvariant(),
                To = to?.Handle,
                Outcome = outcome,
                Result = CatchupReport.Gist(turn.Output),
                FirstTurn = turn.Turn,
                LastTurn = turn.Turn,
            };

            world.Edges[key] = edge;
        }

        edge.LastTurn = turn.Turn;
        edge.Times++;

        // The walk, folded where every edge is written so that no call site can forget it
        // (05_memory.md (`M33.9`)): a step joins the walk open in this place, and a step that leaves
        // closes it — the next step, from wherever the player landed, opens the next one.
        if (world.OpenWalk is not { } open || open.Place != from.Handle || open.Gen != turn.Generation)
        {
            open = new MutableWalk
            {
                Gen = turn.Generation,
                Place = from.Handle,
                StartTurn = turn.Turn,
            };

            world.Walks.Add(open);
            world.OpenWalk = open;
        }

        open.Steps.Add(new WalkStep(
            turn.Turn,
            typed,
            outcome,
            to is not null && !ReferenceEquals(to, from) ? to.Handle : null));
        open.EndTurn = turn.Turn;
    }

    /// <summary>
    /// The ledger row for a turn whose command is a verb over a thing, and the thing-row gathered
    /// out of it.
    /// <para>
    /// Both come from the <em>command</em>, which is the only place a list of things can come from
    /// without reading the story: something the story mentioned in passing and nobody ever typed at
    /// is not in here, and that is the honest limit of what the harness knows.
    /// </para>
    /// </summary>
    private static void Ledger(Accumulator world, MutablePlace? was, string typed, ObservationLog.RecordedTurn turn)
    {
        if (CatchupReport.ThingAndAction(typed) is not { } named)
        {
            return;
        }

        var (thing, action) = named;
        var result = CatchupReport.Gist(turn.Output);
        world.Actions.Add(
            new Action(was?.Handle, Verb(typed), thing, result, turn.Turn, turn.Generation));

        var key = string.Join(
            '\u001f',
            was?.Handle ?? "-",
            thing.ToLowerInvariant(),
            action,
            result);

        if (!world.Things.TryGetValue(key, out var known))
        {
            known = new MutableThing
            {
                Place = was?.Handle ?? "-",
                Name = thing,
                Property = action,
                Value = result,
                Gen = turn.Generation,
                FirstTurn = turn.Turn,
                LastTurn = turn.Turn,
            };

            world.Things[key] = known;
        }

        // The fork is stamped with the latest sighting rather than the first: a value seen in two
        // forks is a value whose *current* reading is the newer one, and a row that kept the older
        // stamp would put a door's state back in the world it came from.
        known.Gen = turn.Generation;
        known.LastTurn = turn.Turn;
        known.Times++;
    }

    /// <summary>
    /// The verb as the player typed it, which is the key a ledger row is filed under: `x` and
    /// `examine` are the same act and stay spelled differently, because the row is a record of what
    /// was typed and the normalising is a reading.
    /// </summary>
    private static string Verb(string typed)
    {
        var words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? string.Empty : words[0].ToLowerInvariant();
    }

    // -- freezing and thawing ---------------------------------------------------------------------

    private static Document Freeze(Accumulator world)
    {
        var places = world.Places
            .Select(place => new Place(
                place.Handle,
                place.Number,
                [.. place.Names],
                new Seen(place.FirstTurn, place.FirstGen),
                new Seen(place.LastTurn, place.LastGen)))
            .ToList();

        var edges = world.Edges.Values
            .OrderBy(edge => edge.Gen)
            .ThenBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.Typed, StringComparer.Ordinal)
            .ThenBy(edge => edge.Outcome, StringComparer.Ordinal)
            .ThenBy(edge => edge.To ?? string.Empty, StringComparer.Ordinal)
            .Select(edge => new Edge(
                edge.Gen,
                edge.From,
                edge.Typed,
                edge.To,
                edge.Outcome,
                edge.Result,
                edge.FirstTurn,
                edge.LastTurn,
                edge.Times))
            .ToList();

        var things = world.Things.Values            .OrderBy(thing => thing.Place, StringComparer.Ordinal)
            .ThenBy(thing => thing.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(thing => thing.Property, StringComparer.Ordinal)
            .ThenBy(thing => thing.Value, StringComparer.Ordinal)
            .Select(thing => new Thing(
                thing.Place,
                thing.Name,
                thing.Property,
                thing.Value,
                thing.Gen,
                thing.FirstTurn,
                thing.LastTurn,
                thing.Times))
            .ToList();

        return new Document(
            new Header(
                world.Stem,
                world.Run,
                world.Generation,
                world.Generation is > 1 && world.ForkTurn > 0 ? world.ForkTurn : null,
                world.AsOfTurn),
            places,
            edges,
            // Walks are written in the order they were walked — the fold's own order, never sorted
            // by turn number, which a reload rewinds (`M13`).
            world.Walks
                .Select(walk => new Walk(walk.Gen, walk.Place, walk.StartTurn, walk.EndTurn, [.. walk.Steps]))
                .ToList(),
            things,
            [.. world.Actions]);
    }

    /// <summary>
    /// The document back as a foldable accumulator, so a landed turn can be added without reading
    /// the record again.
    /// <para>
    /// Everything the fold needs is in the document, which is the property that makes the two paths
    /// comparable at all: the handles are the places' identities, the names and numbers are the two
    /// lookups rebuilt from them, and where the player is now is the place with the newest sighting
    /// — which is true because every turn that identifies a place stamps it.
    /// </para>
    /// </summary>
    private static Accumulator Thaw(Document document)
    {
        var world = new Accumulator
        {
            Stem = document.Header.Stem,
            Run = document.Header.Run,
            Generation = document.Header.Generation,
            ForkTurn = document.Header.ForkTurn ?? 0,
        };

        foreach (var row in document.Places)
        {
            var place = new MutablePlace
            {
                Handle = row.Id,
                Number = row.Number,
                Named = row.Names.Count > 0,
                FirstTurn = row.FirstSeen.Turn,
                FirstGen = row.FirstSeen.Gen,
                LastTurn = row.LastSeen.Turn,
                LastGen = row.LastSeen.Gen,
            };

            place.Names.AddRange(row.Names);
            world.Places.Add(place);
            world.ByHandle[place.Handle] = place;
            if (row.Number is { } number)
            {
                world.ByNumber[number] = place;
            }

            if (row.Names.Count > 0)
            {
                world.ByName.TryAdd(row.Names[0], place);
            }
        }

        world.Places.Sort((left, right) => Ordinal(left.Handle).CompareTo(Ordinal(right.Handle)));

        foreach (var row in document.Walks)
        {
            var walk = new MutableWalk
            {
                Gen = row.Gen,
                Place = row.Place,
                StartTurn = row.StartTurn,
                EndTurn = row.EndTurn,
            };

            walk.Steps.AddRange(row.Steps);
            world.Walks.Add(walk);
        }

        // The walk a landed turn continues is the last one walked in the fork the turn belongs to:
        // its place decides whether the new step joins it or opens the next one.
        world.OpenWalk = world.Walks
            .LastOrDefault(walk => walk.Gen == world.Generation);
        world.Here = world.Places
            .OrderByDescending(place => place.LastTurn)
            .FirstOrDefault() is { LastTurn: > 0 } newest
                ? world.ByHandle[newest.Handle]
                : null;

        foreach (var row in document.Edges)
        {
            var edge = new MutableEdge
            {
                Gen = row.Gen,
                From = row.From,
                Typed = row.Typed,
                To = row.To,
                Outcome = row.Outcome,
                Result = row.Result,
                FirstTurn = row.FirstSeen,
                LastTurn = row.LastSeen,
                Times = row.Times,
            };

            world.Edges[EdgeKey(edge)] = edge;
        }

        foreach (var row in document.Things)
        {
            world.Things[ThingKey(row.Place, row.Name, row.Property, row.Value)] = new MutableThing
            {
                Place = row.Place,
                Name = row.Name,
                Property = row.Property,
                Value = row.Value,
                Gen = row.Gen,
                FirstTurn = row.FirstSeen,
                LastTurn = row.LastSeen,
                Times = row.Times,
            };
        }

        world.Actions.AddRange(document.Actions);
        world.AsOfTurn = Math.Max(document.Header.AsOfTurn, world.Places.Count == 0 ? 0 : world.Places.Max(place => place.LastTurn));
        return world;
    }

    private static string EdgeKey(MutableEdge edge) =>
        string.Join(
            '\u001f',
            edge.Gen.ToString(CultureInfo.InvariantCulture),
            edge.From,
            edge.Typed.ToLowerInvariant(),
            edge.Outcome,
            edge.To ?? "-");

    private static string ThingKey(string place, string name, string property, string value) =>
        string.Join('\u001f', place, name.ToLowerInvariant(), property, value);

    private static long Ordinal(string handle) =>
        long.TryParse(handle, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
