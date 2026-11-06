using System.Globalization;
using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The world as the record shows it (05_memory.md §3 (`M40`)).
/// <para>
/// The fixture is hand-built and synthetic on purpose: the real record that found these defects is
/// an evening of Infocom prose, so it cannot be committed, and what has to survive in CI is the
/// <em>shapes</em> rather than the words. It reproduces the five the plan names — two `east` edges
/// leaving two different places, a fork with an edge behind it, a toggle pair on one thing, a place
/// named with no number of its own, and a line the record cannot place at all — and a sixth that the
/// 2026-10-02 UAT found: a room printed again while the interpreter's number held still.
/// </para>
/// </summary>
public class WorldStateTests : IDisposable
{
    private const string Stem = "moonmist";

    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-world-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _rows = [];

    public WorldStateTests()
    {
        Directory.CreateDirectory(_state);

        // Generation 1: the gallery and the bedroom, an east that works and a southeast that does
        // not, and a line with nothing to place it by.
        Row(1, "look", "The gallery is long and cold.", 131, 1);
        Row(2, "east", "Your bedroom. A bed stands against the far wall.", 33, 1);
        Row(3, "west", "The gallery is long and cold.", 131, 1);
        Row(4, "southeast", "That door is locked.", 131, 1);
        Row(5, "east", "A draught moves past you.", null, 1);

        // Generation 3: the same world reloaded, so the turn numbers come round again and the
        // record holds two of everything. This is the fork the whole document is stamped for.
        Row(6, "look", "The corridor runs east towards an office.", 222, 3);
        Row(7, "east", "The office is small and tidy.", 54, 3);
        Row(8, "west", "The corridor runs east towards an office.", 222, 3);
        Row(9, "east", "The office is small and tidy.", 54, 3);
        Row(10, "examine door", "The door is closed and unlocked.", 54, 3);
        Row(11, "open door", "The door swings open.", 54, 3);
        Row(12, "close door", "The door is now closed.", 54, 3);
        Row(13, "examine door", "The door is now open.", 54, 3);
        Row(14, "southeast", "Tamara's room is locked.", 54, 3);

        // A room with no number of its own: a title the story printed, on a line that goes somewhere.
        _rows.Add(
            """{"turn":15,"input":"north","output":"Hilltop\nThe trail winds down.","fingerprint":"f#15","run":1,"generation":3,"location":null}""");

        // And a line with neither a number nor a title, so the record cannot say where it went.
        Row(16, "south", "The wind picks up.", null, 3);

        // The blind maze (`M33.8`): a room printed again, and the interpreter's number did not move.
        // Turn 17 puts the player back in the corridor, because the two rows above left them in a
        // place with no number of its own; turn 18 is the shape the Steep Trail made famous — the
        // story answers a compass move with a room, so the number is the only thing that did not
        // change.
        Row(17, "look", "The corridor runs east towards an office.", 222, 3);
        // Written as JSON by hand, because a name only counts when the story prints it on a line of
        // its own and `Row` interpolates the output into the row without escaping it — a real
        // newline makes the line unparseable, which loses the row without saying so.
        _rows.Add(
            """{"turn":18,"input":"west","output":"Corridor\nThe corridor runs east towards an office.","fingerprint":"f#18","run":1,"generation":3,"location":222}""");

        Directory.CreateDirectory(Path.Combine(_state, "observations"));
        File.WriteAllLines(ObservationLog.PathFor(_state, Stem), _rows);

        Directory.CreateDirectory(Path.Combine(_state, "runs"));
        File.WriteAllText(
            Path.Combine(_state, "runs", Stem + ".json"),
            """{"current":{"number":1,"started":"2026-09-29T00:41:00Z","generation":3}}""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private void Row(int turn, string input, string output, long? location, long generation)
    {
        var at = location is { } value
            ? value.ToString(CultureInfo.InvariantCulture)
            : "null";
        _rows.Add(
            $$"""{"turn":{{turn}},"input":"{{input}}","output":"{{output}}","fingerprint":"f#{{turn}}","run":1,"generation":{{generation}},"location":{{at}}}""");
    }

    private WorldState.Document World() => WorldState.Build(_state, Stem);

    private static string HandleOf(WorldState.Document world, long number) =>
        Assert.Single(world.Places.Where(place => place.Number == number)).Id;

    private static WorldState.Edge Edge(
        WorldState.Document world, long generation, long from, string typed, string outcome) =>
        Assert.Single(
            world.Edges.Where(
                edge => edge.Gen == generation
                    && edge.From == HandleOf(world, from)
                    && edge.Typed == typed
                    && edge.Outcome == outcome));

    // -- places and edges -------------------------------------------------------------------------

    [Fact]
    public void The_same_direction_out_of_two_places_is_two_edges()
    {
        // The evening of 2026-09-29 in one assertion: `east` out of the gallery is the bedroom and
        // `east` out of the corridor is the office, and neither was ever offered for the other
        // because the record did not say which room the line was typed in.
        var world = World();

        var gallery = Edge(world, 1, 131, "east", "moved");
        var corridor = Edge(world, 3, 222, "east", "moved");

        Assert.Equal(HandleOf(world, 33), gallery.To);
        Assert.Equal(HandleOf(world, 54), corridor.To);
        Assert.NotEqual(gallery.From, corridor.From);
        Assert.Equal(2, corridor.Times);
    }

    [Fact]
    public void A_line_that_did_not_move_the_player_is_blocked_and_carries_the_games_own_words()
    {
        var world = World();

        var blocked = Edge(world, 3, 54, "southeast", "blocked");

        Assert.Null(blocked.To);
        Assert.Equal("Tamara's room is locked.", blocked.Result);
        // And it is not a move: nothing out of this place went southeast.
        Assert.DoesNotContain(
            world.Edges,
            edge => edge.From == blocked.From && edge.Typed == "southeast" && edge.Outcome == "moved");
    }

    [Fact]
    public void A_look_is_not_a_failed_move()
    {
        // Only a line that goes somewhere is an edge. Filing `look` as a move that did not happen is
        // how a map fills up with directions nobody typed.
        var world = World();

        Assert.DoesNotContain(world.Edges, edge => edge.Typed is "look" or "examine door");
    }

    [Fact]
    public void The_fork_keeps_the_world_it_replaced_and_says_which_world_each_edge_belongs_to()
    {
        var world = World();

        Assert.Equal(3, world.Header.Generation);
        Assert.Equal(6, world.Header.ForkTurn);
        // The newest turn on the record, which includes the two rows for the re-printed room.
        Assert.Equal(18, world.Header.AsOfTurn);
        Assert.Equal(1, world.Header.Run);

        // The gallery was walked in generation 1 and is not on this side of the fork; the corridor
        // has been seen in both and is stamped with the newer one.
        Assert.Equal(1, world.Places.Single(place => place.Number == 131).LastSeen.Gen);
        Assert.Equal(3, world.Places.Single(place => place.Number == 222).LastSeen.Gen);
        Assert.DoesNotContain(world.Edges, edge => edge.Gen == 3 && edge.From == HandleOf(world, 131));
    }

    [Fact]
    public void A_place_the_interpreter_never_numbered_is_known_by_the_title_the_story_printed()
    {
        var world = World();

        var hilltop = Assert.Single(world.Places.Where(place => place.Number is null));

        Assert.Equal(["Hilltop"], hilltop.Names);
        Assert.Equal("moved", Edge(world, 3, 54, "north", "moved").Outcome);
        Assert.Equal(hilltop.Id, Edge(world, 3, 54, "north", "moved").To);
    }

    [Fact]
    public void A_line_the_record_cannot_place_is_its_own_outcome_rather_than_an_omission()
    {
        // Neither a number nor a title: the record cannot say where this line left the player, and
        // the alternative to saying so is a move that happened and is nowhere in the document.
        var world = World();

        // One on each side of the fork: the fork does not excuse the record from saying so.
        var unplaced = Assert.Single(
            world.Edges.Where(edge => edge.Outcome == "unplaced" && edge.Gen == 3));

        Assert.Equal("south", unplaced.Typed);
        Assert.Null(unplaced.To);
        // Filed against the place the player was in when they typed it, which is where the turn
        // before put them: the title-only room with no number of its own.
        Assert.Equal(Assert.Single(world.Places.Where(place => place.Number is null)).Id, unplaced.From);
        Assert.Single(world.Edges.Where(edge => edge.Outcome == "unplaced" && edge.Gen == 1));
    }

    [Fact]
    public void A_missing_location_never_becomes_a_room_number()
    {
        // Nothing in the document may invent a place. A place exists because the interpreter named
        // one, or because the story printed a title on a line that went somewhere.
        var world = World();

        Assert.All(world.Places, place => Assert.True(place.Number is not null || place.Names.Count > 0));
        Assert.Equal(5, world.Places.Count);
        Assert.Equal(3, world.Places.Count(place => place.LastSeen.Gen == 3));
    }

    [Fact]
    public void A_story_echoing_a_move_is_not_naming_a_room()
    {
        // Moonmist, read on 2026-09-29: every numbered room took `(You are now in the courtyard.)` as
        // its name, because a bracketed aside does not end in a full stop at the point the test
        // looked. A remark is not a sign.
        var output = "(You are now in the courtyard.)\nYou are standing on warm flagstones.";

        Assert.Equal(string.Empty, TurnReading.RoomName(output));
        Assert.Equal("Hilltop", TurnReading.RoomName("Hilltop\n(You are now on the hilltop.)"));
    }

    // -- things, and what was done with them -------------------------------------------------------

    [Fact]
    public void Both_values_of_a_toggle_are_kept_with_the_turn_each_was_seen_on()
    {
        var world = World();
        var office = HandleOf(world, 54);

        var states = world.Things
            .Where(thing => thing.Place == office && thing.Name == "door" && thing.Property == "looked at")
            .ToList();

        Assert.Equal(2, states.Count);
        Assert.Contains(states, state => state.Value == "The door is closed and unlocked.");
        Assert.Contains(states, state => state.Value == "The door is now open.");
        Assert.Equal(10, states.Single(state => state.Value.Contains("unlocked")).FirstSeen);
        Assert.Equal(13, states.Single(state => state.Value.Contains("now open")).LastSeen);
    }

    [Fact]
    public void What_was_done_to_a_thing_is_recorded_as_the_attempt_it_was()
    {
        // The ledger is unaggregated on purpose: `open` and `close` return different words, and a
        // later `examine` returning to the first value is a fact rather than a duplicate.
        var world = World();

        var ledger = world.Actions.Where(action => action.Object == "door").ToList();

        Assert.Equal(4, ledger.Count);
        Assert.Equal(["examine", "open", "close", "examine"], ledger.Select(action => action.Verb));
        Assert.Equal(
            ["The door is closed and unlocked.", "The door swings open.", "The door is now closed.", "The door is now open."],
            ledger.Select(action => action.Result));
        Assert.All(ledger, action => Assert.Equal(HandleOf(world, 54), action.Place));
    }

    [Fact]
    public void A_thing_row_counts_how_often_the_same_words_came_back()
    {
        var world = World();

        var office = HandleOf(world, 54);
        var examined = world.Things.Single(
            thing => thing.Place == office
                && thing.Name == "door"
                && thing.Property == "looked at"
                && thing.Value == "The door is now open.");

        Assert.Equal(1, examined.Times);
        Assert.Equal(13, examined.FirstSeen);
        Assert.Equal(13, examined.LastSeen);
    }

    // -- derived, and only derived -----------------------------------------------------------------

    [Fact]
    public void The_document_is_rebuilt_from_the_record_byte_for_byte()
    {
        var first = WorldState.Rebuild(_state, Stem);
        var written = File.ReadAllText(WorldState.PathFor(_state, Stem));

        File.Delete(WorldState.PathFor(_state, Stem));
        var again = WorldState.Rebuild(_state, Stem);

        // The bytes, not the records: a record holding lists compares those by reference, so
        // `Assert.Equal(first, again)` would pass or fail on allocation rather than on content.
        Assert.Equal(WorldState.ToJson(first), WorldState.ToJson(again));
        Assert.Equal(written, File.ReadAllText(WorldState.PathFor(_state, Stem)));
    }

    [Fact]
    public void Folding_the_rest_of_the_record_in_ends_with_the_document_a_full_rebuild_produces()
    {
        // The two halves of this block, held to each other: a document built from part of the record
        // and folded forward must end up byte-identical to one built from all of it. If they can
        // disagree then one of them is a second truth, and the one that is wrong is the one nobody
        // ever rebuilds.
        var turns = ObservationLog.All(_state, Stem);
        Assert.NotEmpty(turns);
        var halfway = turns.Count / 2;

        // Where a session that opened halfway through would have got to, and then the turns that
        // landed after it.
        var folder = Path.Combine(_state, "folded");
        Directory.CreateDirectory(Path.Combine(folder, "observations"));
        Directory.CreateDirectory(Path.Combine(folder, "runs"));
        File.WriteAllLines(ObservationLog.PathFor(folder, Stem), _rows.Take(halfway));
        File.Copy(
            Path.Combine(_state, "runs", Stem + ".json"),
            Path.Combine(folder, "runs", Stem + ".json"));

        WorldState.Rebuild(folder, Stem);
        foreach (var turn in turns.Skip(halfway))
        {
            WorldState.Append(folder, Stem, turn);
        }

        Assert.Equal(
            WorldState.ToJson(WorldState.Build(_state, Stem)),
            File.ReadAllText(WorldState.PathFor(folder, Stem)));
    }

    [Fact]
    public void Folding_a_turn_into_a_missing_document_rebuilds_rather_than_starting_from_nothing()
    {
        var turns = ObservationLog.All(_state, Stem);

        WorldState.Append(_state, Stem, turns[^1]);

        var world = WorldState.Read(_state, Stem);
        Assert.NotNull(world);
        Assert.Equal(turns.Count, world!.Header.AsOfTurn);
        Assert.True(world.Places.Count > 0, "a projection that starts from nothing is worse than one that costs a second");
    }

    [Fact]
    public void A_document_that_cannot_be_read_is_cannot_say_rather_than_an_empty_world()
    {
        var path = WorldState.PathFor(_state, Stem);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"header\":{\"stem\"");

        Assert.Null(WorldState.Read(_state, Stem));
    }

    [Fact]
    public void The_header_says_cannot_say_when_the_run_book_does()
    {
        // A first attempt has no fork to point at, and a game nobody has played has no attempt.
        File.WriteAllText(
            Path.Combine(_state, "runs", Stem + ".json"),
            """{"current":{"number":1,"started":"2026-09-29T00:41:00Z","generation":1}}""");

        var first = WorldState.Build(_state, Stem);
        Assert.Equal(1, first.Header.Generation);
        Assert.Null(first.Header.ForkTurn);

        File.WriteAllText(Path.Combine(_state, "runs", Stem + ".json"), """{"current":null}""");

        var unknown = WorldState.Build(_state, Stem);
        Assert.Null(unknown.Header.Generation);
        Assert.Null(unknown.Header.Run);
    }

    [Fact]
    public void A_room_printed_again_without_a_new_number_is_not_read_as_did_not_go()
    {
        // 05_memory.md §6.4 (`M33.8`). Measured on Wishbringer's Steep Trail, 2026-10-02: six
        // segments share object 80, so `west` printed a room and kept the number, and the record
        // said `west did not go: "Steep Trail"` — a claim the number alone cannot support. The
        // companion believed it over its own eyes for the better part of an hour, re-derived the
        // loop three times and wrote four notes about a map that could not see the rooms.
        //
        // What separates the two cases is measurable and is already read for the map's labels: a
        // wall answers with a sentence, and a blind maze answers with a room.
        var world = World();

        Edge(world, generation: 3, from: 222, typed: "west", outcome: "reprinted");
        // The contrast, unchanged: a refusal is still a refusal.
        Edge(world, generation: 3, from: 54, typed: "southeast", outcome: "blocked");

        // And both readers say what was measured. The pushed header is the one that rides every
        // call, so it is the one that has to stop asserting the conclusion — and it has to stop in
        // *both* directions: measured 2026-10-02, live, in Wishbringer's fog, `north — same room
        // number, and a room was printed again: "Fog"` came back to the player as *"north loops me
        // back into the same room"*, and the companion stopped re-trying a direction that was
        // demonstrably moving it (the same way out answered differently on two printings).
        var fromHere = MapReading.FromHere(world, 222);
        Assert.Contains("a room was printed again and the number did not change", fromHere);
        Assert.Contains("cannot say whether that moved you", fromHere);
        Assert.DoesNotContain("same room number", fromHere);
        Assert.DoesNotContain("did not go", fromHere);
        // One number holding several rooms is what made the merged rows read as one room's exits, and
        // a room with one fatal door is a wall. The list says so — as an identity it cannot vouch
        // for, not as a warning about its rows — and — the half that was missing — it carries the
        // *walk*: the lines in the order they were typed, which is the only thing a place whose
        // screens all read alike can be navigated by, and the only thing that can be turned round.
        // Measured 2026-10-02: the way down from Wishbringer's cliff was the climb up the Steep Trail
        // with every move inverted, and nothing in the record could express it.
        Assert.Contains("printed more than once", fromHere);
        Assert.Contains("cannot say how many rooms answer to this number", fromHere);
        Assert.Contains("The lines that came in, in order", fromHere);
        Assert.Contains("carries a walk rather than a map", fromHere);
        // And the identity comes **before** the rows it qualifies: a reader who skims a list of lines
        // takes the list, which is how the same block was read as one room with four walls and no
        // exits (measured 2026-10-02, live, in the fog).
        var identity = fromHere.IndexOf("printed more than once", StringComparison.Ordinal);
        Assert.True(identity >= 0, fromHere);
        Assert.True(identity < fromHere.IndexOf("a room was printed again", StringComparison.Ordinal), fromHere);
        Assert.True(identity < fromHere.IndexOf("The lines that came in", StringComparison.Ordinal), fromHere);
        // And the walk it carries is the walking, not a list of rooms: the newest line that arrived
        // in room 222, with the room it was typed in and the turn it was typed on.
        Assert.Contains("west in room 54 (turn 8)", fromHere);
    }

    // -- walks ------------------------------------------------------------------------------------
    // 05_memory.md §6.4 (`M33.9`): a walk is the lines typed in one place while it was not left, in
    // order, and a route is the chain of walks that reached a place. Measured 2026-10-02: the way
    // down Wishbringer's cliff is the climb up the Steep Trail with every move inverted, and the
    // record could not express it — the companion's own notebook held "Cliff Bottom → Steep Trail
    // (a chain of rooms) → Cliff Edge", places joined by arrows and not one move.

    private static WorldState.Walk Walked(WorldState.Document world, long number, int startTurn) =>
        Assert.Single(
            world.Walks,
            walk => walk.Place == HandleOf(world, number) && walk.StartTurn == startTurn);

    [Fact]
    public void What_was_typed_in_one_place_is_kept_in_order_and_the_walk_ends_where_the_place_does()
    {
        var world = World();

        Assert.NotEmpty(world.Walks);
        // The walks are in the order they were walked — the record's order, never sorted by turn
        // number, which a reload rewinds (`M13`) — so the last walk in the document is the newest
        // walking, and this fixture's last one is the reprinted corridor at turn 18.
        Assert.Equal(HandleOf(world, 131), world.Walks[0].Place);
        Assert.Equal(HandleOf(world, 222), world.Walks[^1].Place);
        Assert.Equal(18, world.Walks[^1].StartTurn);
        Assert.All(world.Walks, walk =>
        {
            Assert.NotEmpty(walk.Steps);
            Assert.Contains(world.Places, place => place.Id == walk.Place);
            Assert.Equal(walk.StartTurn, walk.Steps[0].Turn);
            Assert.Equal(walk.EndTurn, walk.Steps[^1].Turn);
            Assert.Equal(
                walk.Steps.Select(step => step.Turn),
                walk.Steps.Select(step => step.Turn).OrderBy(turn => turn));
            // Only the step that carried the player away can say where it left them: the walk is
            // over at that point, and the next line is typed somewhere else.
            Assert.All(walk.Steps.Take(walk.Steps.Count - 1), step => Assert.Null(step.To));
        });

        // The corridor's first walk is the line that carried the player out of it, and nothing
        // follows that line, because the next line was typed in the office.
        var outOfCorridor = Walked(world, 222, startTurn: 7);
        var left = Assert.Single(outOfCorridor.Steps);
        Assert.Equal("east", left.Typed);
        Assert.Equal(HandleOf(world, 54), left.To);

        // The office, walked twice without leaving it: a line that could have moved the player and
        // did not is a step — and it says nothing about where it left them, because it left them
        // here — while everything that could not have moved them is not a step at all (`examine
        // door` and `open door` are on the thing rows and in the ledger, and a walk that carried
        // them would be a transcript rather than a walk).
        var lingered = Walked(world, 54, startTurn: 14);
        var hilltop = Assert.Single(world.Places, place => place.Names.Contains("Hilltop")).Id;
        Assert.Equal(
            new (string, string, string?)[]
            {
                ("southeast", "blocked", null),
                ("north", "moved", hilltop),
            },
            lingered.Steps.Select(step => (step.Typed, step.Outcome, step.To)).ToList());
        Assert.Equal(15, lingered.EndTurn);
    }

    [Fact]
    public void A_walk_is_a_run_in_the_record_and_the_turn_numbers_do_not_order_it()
    {
        // 05_memory.md §2 (`M13`) and §6.4 (`M33.9`). A reload rewinds the interpreter's counter, so
        // the rows either side of one share numbers — measured 2026-10-02: the companion walked out
        // of Wishbringer's fog *east* at turn 84, then typed `RESTORE` at turn 85 and was dropped
        // back into it, and the rows after that run 109, 109, 110, 111, 112, 113, 114 … 128, 124,
        // 125 … inside one fork. Ordering a walk by those numbers puts a later step first, which is
        // how a route reads as nonsense; the record's own order is the walking order.
        var state = Path.Combine(_state, "rewind");
        Directory.CreateDirectory(Path.Combine(state, "observations"));
        File.WriteAllLines(
            ObservationLog.PathFor(state, Stem),
            [
                """{"turn":1,"input":"look","output":"A dark trail.\nThe trail starts here.","fingerprint":"f#1","run":1,"generation":1,"location":80}""",
                """{"turn":2,"input":"north","output":"Cliff Edge\nThe drop is sheer.","fingerprint":"f#2","run":1,"generation":1,"location":81}""",
                """{"turn":3,"input":"north","output":"Ledge\nThe wind is sharp.","fingerprint":"f#3","run":1,"generation":1,"location":82}""",
                """{"turn":2,"input":"south","output":"Cliff Edge\nThe drop is sheer.","fingerprint":"f#4","run":1,"generation":1,"location":81}""",
                """{"turn":3,"input":"south","output":"A dark trail.\nThe trail starts here.","fingerprint":"f#5","run":1,"generation":1,"location":80}""",
                """{"turn":4,"input":"west","output":"Trail\nThe trail starts here.","fingerprint":"f#6","run":1,"generation":1,"location":80}""",
            ]);

        Directory.CreateDirectory(Path.Combine(state, "runs"));
        File.WriteAllText(
            Path.Combine(state, "runs", Stem + ".json"),
            """{"current":{"number":1,"started":"2026-10-02T21:00:00Z","generation":1}}""");

        var world = WorldState.Build(state, Stem);

        // Five walks, in the order the lines were walked: out of the trail, out of the cliff, out of
        // the ledge, back through the cliff and back onto the trail — and the last one is a step
        // that stayed put, in the trail, because `west` is a line that could have moved the player.
        Assert.Equal(
            new long?[] { 80, 81, 82, 81, 80 },
            world.Walks
                .Select(walk => world.Places.Single(place => place.Id == walk.Place).Number)
                .ToList());
        Assert.Equal(
            new[] { 2L, 3L, 2L, 3L, 4L },
            world.Walks.Select(walk => walk.StartTurn).ToList());

        // And the route that came in is read in that order, not by the numbers. The newest arrival
        // at the trail is the rewound `south` at turn 3, and the walk it is built on is the newest
        // arrival at the cliff — which by the numbers ties with the climb at turn 2 and is settled
        // only by which of the two was walked later.
        var trail = MapReading.FromHere(world, 80);
        Assert.Contains(
            "The lines that came in, in order: south in room 82 (turn 2) · south in room 81 (turn 3).",
            trail);
    }

    [Fact]
    public void A_route_is_the_chain_of_walks_that_reached_a_place_so_the_ascent_survives()
    {
        // The cliff in miniature, and the whole point of the walk: a trail, a cliff and a fog, one
        // line typed between each, and two lines typed in the fog that put a room on the screen
        // without moving the interpreter's number. Nothing here can be navigated by room: the fog's
        // cells share a number, so the only identity they have is the order they were walked in.
        var state = Path.Combine(_state, "cliff");
        Directory.CreateDirectory(Path.Combine(state, "observations"));
        File.WriteAllLines(
            ObservationLog.PathFor(state, Stem),
            [
                """{"turn":1,"input":"look","output":"The trail starts here.","fingerprint":"f#1","run":1,"generation":1,"location":80}""",
                """{"turn":2,"input":"climb","output":"Cliff Edge\nThe drop is sheer.","fingerprint":"f#2","run":1,"generation":1,"location":81}""",
                """{"turn":3,"input":"down","output":"Fog\nGrey nothing.","fingerprint":"f#3","run":1,"generation":1,"location":82}""",
                """{"turn":4,"input":"north","output":"Fog\nGrey nothing.","fingerprint":"f#4","run":1,"generation":1,"location":82}""",
                """{"turn":5,"input":"east","output":"Fog\nGrey nothing.","fingerprint":"f#5","run":1,"generation":1,"location":82}""",
            ]);

        Directory.CreateDirectory(Path.Combine(state, "runs"));
        File.WriteAllText(
            Path.Combine(state, "runs", Stem + ".json"),
            """{"current":{"number":1,"started":"2026-10-02T21:00:00Z","generation":1}}""");

        var world = WorldState.Build(state, Stem);

        // The way in is two walks — the climb in room 80 and the step off the cliff in room 81 — and
        // both are carried, in the order they were walked, which is what can be turned round.
        Assert.Contains(
            "The lines that came in, in order: climb in room 80 (turn 2) · down in room 81 (turn 3).",
            MapReading.FromHere(world, 82));

        // The fog is a walk rather than a room: two lines typed in it, each putting a room on the
        // screen while the number stood still, and neither of them arriving anywhere.
        var cells = Assert.Single(world.Walks, walk => walk.Place == HandleOf(world, 82));
        Assert.Equal(
            new (string, string, string?)[]
            {
                ("north", "reprinted", null),
                ("east", "reprinted", null),
            },
            cells.Steps.Select(step => (step.Typed, step.Outcome, step.To)).ToList());
    }
}

/// <summary>
/// The loop's own hooks into the world document: it is rebuilt when a session opens and folded in
/// one turn at a time after that (05_memory.md §3 (`M40`)).
/// </summary>
public class WorldStateLoopTests
{
    private const string Welcome =
        "{\"v\":1,\"type\":\"welcome\",\"seq\":1,\"ts\":\"now\",\"payload\":{\"game_title\":\"Wishbringer\",\"game_stem\":\"wishbringer\",\"turn_count\":0,\"transcript_tail\":[],\"agent_name\":\"Quill\",\"run\":3,\"generation\":2,\"resumed\":false,\"resumed_from\":null}}";

    private static string Turn(ulong n, string input) =>
        "{\"v\":1,\"type\":\"turn_complete\",\"seq\":" + (n + 100) + ",\"ts\":\"now\",\"payload\":{\"input\":\""
        + input + "\",\"output\":\"A dim landing.\",\"elapsed_ms\":4,\"turn\":" + n
        + ",\"awaiting_command\":true,\"machinery\":false}}";

    [Fact]
    public async Task A_session_open_writes_the_document_and_a_landed_turn_is_folded_into_it()
    {
        // Written where the record and the run book live, so the bundle and the catch-up page find
        // it without being told about it. A turn landing is what moves `as_of_turn` off zero, which
        // is the assertion that says the fold ran rather than the rebuild alone.
        var llm = new ScriptedLlm().Reply("Evening.");
        await using var harness = LoopHarness.Start(llm, Welcome, Turn(1, "look"));
        await harness.UntilAsync(text => text.Contains("Evening"));

        var world = WorldState.Read(harness.StateDirectory, "wishbringer");

        Assert.NotNull(world);
        Assert.Equal(1, world!.Header.AsOfTurn);
        // And *cannot say* about the fork, because the harness has no run book on disk. The header
        // reads the book rather than the handshake on purpose: a restore changes the fork without
        // sending another handshake, so a value taken from the welcome would be wrong for the rest
        // of the evening (05_memory.md §2 (`M9.2`)).
        Assert.Null(world.Header.Generation);
    }
}
