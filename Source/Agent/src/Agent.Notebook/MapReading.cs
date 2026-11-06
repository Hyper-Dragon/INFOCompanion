using System.Globalization;
using System.Text;

namespace Agent.Notebook;

/// <summary>
/// Reading the world document: what the map says, said twice — once in every reply and once to
/// whoever asks for it (05_memory.md §8 (`M40`), 06_tool_surface.md §3 (`T24`)).
/// <para>
/// One renderer for both readers, because the two would otherwise be two answers to one question,
/// and the day they disagreed the model would be quoting the one that was not in front of it. The
/// header is a few lines taken from here; the tool is the whole of it.
/// </para>
/// <para>
/// Nothing here reads the story. Every sentence is built from a row of the document, which was built
/// from what was typed and what the game printed (`G31`).
/// </para>
/// </summary>
public static class MapReading
{
    /// <summary>How many ways out the pushed header names before it counts the rest.</summary>
    public const int FromHereLimit = 8;

    /// <summary>How many attempts the pushed header names before it counts the rest.</summary>
    public const int TriedHereLimit = 6;

    /// <summary>How many lines of a walk the pushed header carries for a place that cannot be told apart.</summary>
    public const int WalkLimit = 10;

    /// <summary>One answer for the tool: what a person would read, and the same facts as JSON.</summary>
    public sealed record Reading(string Prose, object Facts);

    /// <summary>
    /// What the record has about one place: every line that has been typed there, what came of it,
    /// and where it led.
    /// <para>
    /// <paramref name="asked"/> is a room number or a title, or null/empty for *where I am*. A place
    /// that is on the record but not in this fork is a different answer from a place that has never
    /// been seen, and both are different from a place that is here with nothing tried in it.
    /// </para>
    /// </summary>
    public static Reading Describe(WorldState.Document world, string? asked, long? roomHere)
    {
        var generation = world.Header.Generation;
        var wanted = asked?.Trim();
        var place = wanted is { Length: > 0 }
            ? Find(world, wanted)
            : roomHere is { } here
                ? world.Places.FirstOrDefault(candidate => candidate.Number == here)
                // Nothing asked and no room known: the newest sighting is not an answer. Saying which
                // room the player is in when the interpreter never said is the invention this whole
                // design exists to avoid, and it is the one the companion would repeat out loud.
                : null;

        if (place is null)
        {
            return new Reading(
                Nothing(world, wanted, generation),
                new { asked = wanted, found = 0, generation });
        }

        // On the record and not in this world: a room walked in a fork that has been reloaded away.
        // It is worth saying which fork, because the alternative is a room the companion believes it
        // can walk to (05_memory.md §8 (`M40.4`)).
        if (generation is { } current && place.LastSeen.Gen != current)
        {
            return new Reading(
                $"Room {Room(place)} was last seen in generation {place.LastSeen.Gen} at turn "
                + $"{place.LastSeen.Turn}, and this is generation {current}. It is history: nothing "
                + "about it is offered as though it were observed here.",
                new { place = place.Id, room = place.Number, found = 0, generation, seen_in = place.LastSeen.Gen });
        }

        var edges = From(world, place, generation);
        var tries = world.Actions
            .Where(action => action.Place == place.Id && action.Object.Length > 0)
            .Where(action => generation is null || action.Gen == generation)
            .OrderByDescending(action => action.Turn)
            .ToList();

        var prose = new StringBuilder();
        prose.AppendLine(
            $"The map from room {Room(place)}, as of turn {world.Header.AsOfTurn}"
            + (generation is { } gen ? $", generation {gen}." : ", in a fork the run book cannot name."));

        if (edges.Count == 0)
        {
            prose.AppendLine();
            prose.AppendLine(
                "Nothing has been typed here that the record can place: either nobody has tried to "
                + "go anywhere from this room, or the interpreter has never reported it.");
        }
        else
        {
            prose.AppendLine();
            foreach (var line in edges.Select(edge => Line(world, edge)))
            {
                prose.AppendLine("  " + line);
            }
        }

        if (tries.Count > 0)
        {
            prose.AppendLine();
            prose.AppendLine(
                "What has been tried here, most recent first: "
                + string.Join(" · ", tries.Take(12).Select(action => $"{action.Verb} {action.Object}"))
                + (tries.Count > 12 ? $" · and {tries.Count - 12} more" : string.Empty));
        }

        prose.AppendLine();
        prose.Append(edges.Any(edge => edge.Outcome == "reprinted")
            // **The identity is not a room, and the reading must say so rather than assert it.** A
            // number that has printed more than once covers as many rooms as it likes — a blind maze
            // is one number and a dozen cells — so "this room" is a claim the record cannot make,
            // and the closing sentence every other place gets is an overclaim here (`M33.8`). What
            // it *can* say is what was typed and what came back, in order, and that is what the walk
            // line above adds: the one thing a place whose screens read alike can be navigated by.
            ? "This number has printed more than once, so the rows above are not facts about one "
                + "room: the same line out of two of its cells answers differently, and which cell you "
                + "are standing in is not something the record can say. Room numbers are the "
                + "interpreter's own."
            : "Every row is a fact about a line typed in **this** room and no other: the same direction "
                + "out of two rooms leads to two different places. The room numbers are the "
                + "interpreter's own.");

        // The walk only exists in a fork the run book can name: without a generation there is no
        // order to put the lines in, and the rows above already say they are history (`M33.9`).
        if (generation is { } fork && edges.Any(edge => edge.Outcome == "reprinted"))
        {
            prose.AppendLine();
            prose.AppendLine(Route(world, place, fork));
        }

        return new Reading(
            prose.ToString(),
            new
            {
                place = place.Id,
                room = place.Number,
                name = Title(place),
                generation,
                as_of_turn = world.Header.AsOfTurn,
                from = edges.Select(edge => new
                {
                    typed = edge.Typed,
                    outcome = edge.Outcome,
                    room = NumberOf(world, edge.To),
                    name = TitleOf(world, edge.To),
                    said = edge.Result,
                    times = edge.Times,
                    first_seen = edge.FirstSeen,
                    last_seen = edge.LastSeen,
                }),
                tried = tries.Take(12).Select(action => new
                {
                    verb = action.Verb,
                    thing = action.Object,
                    said = action.Result,
                    turn = action.Turn,
                }),
            });
    }

    /// <summary>
    /// The two lines the pushed header carries: the ways out of the room the player is in, and what
    /// has already been tried there (06_tool_surface.md §3 (`T24`)).
    /// <para>
    /// Short on purpose. The header rides every call, and a block that grows competes for the
    /// model's regard with the transcript it is there to help with (`M11.2`) — so this names a
    /// handful and counts the rest rather than listing a room swept with thirty verbs.
    /// </para>
    /// <para>
    /// Empty when there is nothing to say: no room, no fork, or nothing ever typed here.
    /// </para>
    /// </summary>
    public static string FromHere(WorldState.Document world, long? roomHere)
    {
        if (world.Header.Generation is not { } generation || roomHere is not { } room)
        {
            return string.Empty;
        }

        var here = world.Places.FirstOrDefault(place => place.Number == room);
        if (here is null)
        {
            return string.Empty;
        }

        var edges = From(world, here, generation);
        var sentences = new List<string>();

        // **What the number is worth comes FIRST, before the rows it qualifies** (`M33.8`). It used
        // to arrive after them, and a reader skimming a list of five lines takes the list: measured
        // 2026-10-02, live, on Wishbringer's fog, the same block was read two ways by the same
        // companion inside one evening — once as *"this room name has come up more than once, so
        // those lines may not all be about one place. The fog may be several cells all printing the
        // same words"*, and once, four turns later, as *"a room with four walls and no exits"* —
        // because the warning sat under the rows and the rows read as one room's answer. The
        // sentence below is the same one it quoted both times; where it sits is the difference.
        var reprinted = edges.Any(edge => edge.Outcome == "reprinted");
        if (reprinted)
        {
            sentences.Add(
                "This number has printed more than once, so the rows below are every answer given "
                + "out of it and not the ways out of the screen in front of you: the record cannot "
                + "say how many rooms answer to this number, or which one you are in.");
        }

        var ways = edges.Where(edge => edge.Outcome == "moved").ToList();
        if (ways.Count > 0)
        {
            sentences.Add(
                "Out of here, the record has: "
                + string.Join(
                    "; ",
                    ways.Take(FromHereLimit).Select(edge => $"{edge.Typed} to room {RoomOf(world, edge.To)}"))
                + (ways.Count > FromHereLimit ? $"; and {ways.Count - FromHereLimit} more" : string.Empty)
                + ".");
        }

        var refused = edges.Where(edge => edge.Outcome is "blocked" or "reprinted").ToList();        foreach (var edge in refused.Take(FromHereLimit))
        {
            // Two lines, because they are two different facts (`M33.8`). `did not go` is the wall:
            // the story answered with a sentence and the room is the same one. `reprinted` is the
            // blind maze — a room came back and the interpreter's number did not change — and it says
            // so instead of denying the move, because on the Steep Trail the move was real.
            //
            // **And it must not say "same room" either**, which is the same overclaim the other way
            // up. Measured 2026-10-02, live, on Wishbringer's fog: the pushed line read `north —
            // same room number, and a room was printed again: "Fog".` and the companion's next reply
            // said *"north just prints the same fog"* and *"north loops me back into the same room"*
            // — so it stopped re-trying a direction that was moving it, because every cell of the
            // maze shares one number and the line told it the number was the room. The detail view's
            // own wording already refuses to conclude (`Line` below); this is that wording, in the
            // one place the companion reads every turn.
            sentences.Add(
                edge.Outcome == "reprinted"
                    ? $"{edge.Typed} — a room was printed again and the number did not change, so "
                        + $"the record cannot say whether that moved you: \"{edge.Result}\"."
                    : $"{edge.Typed} did not go: \"{edge.Result}\".");
        }

        // **The walk, and it is the half that was missing** (`M33.8`). A place that prints one
        // screen for every cell cannot be reached by any question about the *room*: ask what the
        // ways out of here are and the answer is a wall, a reprint and a fatal drop, which is a
        // sealed room with one door and it is death. The question that answers it is *how did I get
        // in, in what order* — a route is a sequence, and a sequence can be turned round.
        //
        // Measured 2026-10-02, live: `down north west down south east down` walked Wishbringer's fog
        // from Cliff Edge to Cliff Bottom, and it is the climb up the Steep Trail with every move
        // inverted. The companion had those same moves in front of it that evening — one room each,
        // in order, forty turns back — and its own notebook kept the ascent as *"Cliff Bottom →
        // Steep Trail (a chain of rooms, all printed 'Steep Trail', winding up) → Cliff Edge"*:
        // places joined by arrows, and not one move in it. You cannot reverse a list of rooms.
        if (reprinted)
        {
            sentences.Add(
                Route(world, here, generation)
                + " A place whose screens all read alike carries a walk rather than a map.");
        }

        var tries = world.Actions
            .Where(action => action.Place == here.Id && action.Gen == generation && action.Object.Length > 0)
            .Select(action => $"{action.Verb} {action.Object}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tries.Count > 0)
        {
            sentences.Add(
                "Already tried here: "
                + string.Join(", ", tries.Take(TriedHereLimit))
                + (tries.Count > TriedHereLimit ? $", and {tries.Count - TriedHereLimit} more" : string.Empty)
                + ".");
        }

        return sentences.Count == 0 ? string.Empty : string.Join(' ', sentences);
    }

    /// <summary>
    /// The route that reached a place: the walks that got here, oldest first, each line with the room
    /// it was typed in.
    /// <para>
    /// This exists because a route is a sequence and nothing else the record holds is (`M33.9`). A
    /// place whose cells all print the same screen has no rooms to offer, no ways out and no map:
    /// what it has is what was typed and what came back, and the only thing that can be done with a
    /// sequence is to carry on with it or turn it round. Its absence is measured: the companion that
    /// spent twenty turns in Wishbringer's fog had the ascent in its own context and kept it in its
    /// own notebook as *"Cliff Bottom → Steep Trail (a chain of rooms) → Cliff Edge"* — places joined
    /// by arrows and not one move — which is not a thing anybody can reverse.
    /// </para>
    /// <para>
    /// A route is a chain of walks rather than one, because a walk stops where a place does: the
    /// climb up a trail and the step off its top are two walks, and the way in is both of them.
    /// </para>
    /// <para>
    /// The walk that arrived is the last one that did — *last* by the document's order, which is the
    /// order the lines were walked in, rather than the largest turn number: a reload rewinds the
    /// interpreter's counter, so the numbers are not in walking order after a fork (`M13`, measured
    /// 2026-10-02: the fog was walked out at turn 84 and restored into at 85).
    /// </para>
    /// </summary>
    private static string Route(WorldState.Document world, WorldState.Place place, long generation)
    {
        var chain = new List<WorldState.Walk>();
        var at = place.Id;
        var seen = new HashSet<string>(StringComparer.Ordinal) { place.Id };

        for (var hop = 0; hop < WalkLimit; hop++)
        {
            var arriving = world.Walks
                .LastOrDefault(walk => walk.Gen == generation && walk.Steps.Count > 0 && walk.Steps[^1].To == at);

            if (arriving is null || !seen.Add(arriving.Place))
            {
                break;
            }

            chain.Insert(0, arriving);
            at = arriving.Place;
        }

        if (chain.Count == 0)
        {
            return "The record has no lines that arrived here.";
        }

        var steps = new List<string>();
        foreach (var walk in chain)
        {
            steps.AddRange(walk.Steps.Select(step =>
                $"{step.Typed} in room {RoomOf(world, walk.Place)} (turn {step.Turn})"));
        }

        return "The lines that came in, in order: "
            + string.Join(" · ", steps.TakeLast(WalkLimit))
            + ".";
    }

    /// <summary>Every line the record has out of one place, in this fork, each line once.</summary>
    private static List<WorldState.Edge> From(WorldState.Document world, WorldState.Place place, long? generation) =>
        world.Edges
            .Where(edge => edge.From == place.Id)
            .Where(edge => generation is null || edge.Gen == generation)
            // One row per (typed, outcome, where), so the same word tried twice with the same answer
            // is one line with a count on it — and a door that was locked and later opened is two,
            // which is the whole point of the outcome.
            .OrderBy(edge => edge.Typed, StringComparer.Ordinal)
            .ThenByDescending(edge => edge.LastSeen)
            .ToList();

    /// <summary>One row, for a person reading the answer.</summary>
    private static string Line(WorldState.Document world, WorldState.Edge edge)
    {
        var to = edge.To is { } handle && world.Places.FirstOrDefault(place => place.Id == handle) is { } place
            ? $"room {Room(place)}"
            : "somewhere the record cannot name";

        return edge.Outcome switch
        {
            "moved" =>
                $"{edge.Typed} -> {to} — seen {Times(edge.Times)}, last at turn {edge.LastSeen}.",
            "blocked" =>
                $"{edge.Typed} — did not go: \"{edge.Result}\" (turn {edge.LastSeen})",
            "reprinted" =>
                $"{edge.Typed} — a room was printed again and the interpreter's number did not "
                + $"change, so the record cannot say whether that moved you: \"{edge.Result}\" "
                + $"(turn {edge.LastSeen})",
            _ =>
                $"{edge.Typed} — the record cannot say where it left you (turn {edge.LastSeen})",
        };
    }

    private static string Nothing(WorldState.Document world, string? asked, long? generation) =>
        asked is { Length: > 0 }
            ? $"Nothing on the record matches \"{asked}\": no room has been seen with that number, "
                + "and no room has printed that name. A place the story has only mentioned is not a "
                + "place the record has been."
            : world.Places.Count == 0
                ? "No room has ever been recorded for this game, so there is no map at all yet."
                : "The record cannot say which room you are in — the interpreter has reported no "
                    + $"location this evening (generation {generation?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}). "
                    + "Ask about a room by its number or its name and the answer will be about that room.";

    /// <summary>A room by number or by the title the story printed, exactly first and loosely after.</summary>
    internal static WorldState.Place? Find(WorldState.Document world, string asked)
    {
        if (long.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return world.Places.FirstOrDefault(place => place.Number == number);
        }

        return world.Places.FirstOrDefault(
                place => place.Names.Any(name => string.Equals(name, asked, StringComparison.OrdinalIgnoreCase)))
            ?? world.Places.FirstOrDefault(
                place => place.Names.Any(name => name.Contains(asked, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Room(WorldState.Place place) =>
        place.Number?.ToString(CultureInfo.InvariantCulture) ?? Title(place);

    internal static string Title(WorldState.Place place) =>
        place.Names.Count > 0 ? place.Names[0] : "a room the story has not named";

    private static string RoomOf(WorldState.Document world, string? handle) =>
        handle is { } id && world.Places.FirstOrDefault(place => place.Id == id) is { } place
            ? Room(place)
            : "?";

    private static long? NumberOf(WorldState.Document world, string? handle) =>
        handle is { } id ? world.Places.FirstOrDefault(place => place.Id == id)?.Number : null;

    private static string? TitleOf(WorldState.Document world, string? handle) =>
        handle is { } id && world.Places.FirstOrDefault(place => place.Id == id) is { } place
            ? Title(place)
            : null;

    /// <summary>"once" or "3 times", because "1 times" is the kind of thing that makes a reader doubt everything else.</summary>
    private static string Times(long count) => count == 1 ? "once" : $"{count} times";
}
