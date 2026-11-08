using System.Globalization;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The map as it is read: the answer the tool gives, and the two lines the header carries
/// (06_tool_surface.md §3 (`T24`)).
/// <para>
/// The fixture is the shape of the evening of 2026-09-29 and none of its prose: a gallery with an
/// `east` in generation 1, a corridor with an `east` in generation 3, one line that did not go, and
/// one room that exists only on the far side of the fork.
/// </para>
/// </summary>
public class MapReadingTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-map-tests", Guid.NewGuid().ToString("N"));

    public MapReadingTests()
    {
        Directory.CreateDirectory(Path.Combine(_state, "observations"));
        Directory.CreateDirectory(Path.Combine(_state, "runs"));

        Row(1, "look", "The gallery is long and cold.", 131, 1);
        Row(2, "east", "Your bedroom. A bed stands against the far wall.", 33, 1);
        Row(3, "west", "The gallery is long and cold.", 131, 1);
        Row(4, "southeast", "That door is locked.", 131, 1);
        Row(5, "look", "The corridor runs east towards an office.", 222, 3);
        Row(6, "east", "The office is small and tidy.", 54, 3);
        Row(7, "examine desk", "The desk is bare and polished.", 54, 3);
        Row(8, "open drawer", "The drawer is locked.", 54, 3);
        Row(9, "west", "The corridor runs east towards an office.", 222, 3);
        Row(10, "examine desk", "The desk is bare and polished.", 222, 3);
        Row(11, "open drawer", "The drawer is locked.", 222, 3);
        Row(12, "southeast", "Tamara's room is locked.", 222, 3);
        // A second `east` in the same generation, out of a different room, so the two answers can be
        // held against each other without the fork getting in the way.
        Row(13, "look", "A landing with three doors off it.", 400, 3);
        Row(14, "east", "A narrow study.", 401, 3);

        File.WriteAllLines(
            ObservationLog.PathFor(_state, "moonmist"),
            _rows);
        File.WriteAllText(
            Path.Combine(_state, "runs", "moonmist.json"),
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

    private readonly List<string> _rows = [];

    private void Row(int turn, string input, string output, long location, long generation) =>
        _rows.Add(
            $$"""{"turn":{{turn}},"input":"{{input}}","output":"{{output}}","fingerprint":"f#{{turn}}","run":1,"generation":{{generation}},"location":{{location.ToString(CultureInfo.InvariantCulture)}}}""");

    private WorldState.Document World() => WorldState.Build(_state, "moonmist");

    private MapReading.Reading At(string? room) => MapReading.Describe(World(), room, roomHere: null);

    // -- the tool ---------------------------------------------------------------------------------

    [Fact]
    public void The_same_direction_is_two_answers_and_neither_is_offered_for_the_other()
    {
        // The whole evening in one pair of assertions: `east` out of the corridor is the office and
        // `east` out of the landing is the study, and a map keyed on the direction alone cannot tell
        // them apart — which is how a companion holding the right record walked into the wrong room
        // and said so confidently.
        var corridor = At("222");
        var landing = At("400");

        Assert.Contains("room 54", corridor.Prose);
        Assert.DoesNotContain("room 401", corridor.Prose);
        Assert.Contains("room 401", landing.Prose);
        Assert.DoesNotContain("room 54", landing.Prose);
    }

    [Fact]
    public void A_room_from_a_world_this_one_replaced_is_not_offered_as_a_place_to_go()
    {
        // The gallery was walked in generation 1 and this is generation 3, so what it says about
        // `east` is not an answer about anything the companion can reach — and saying *which* world
        // it belongs to is the difference between history and a walk that ends in the wrong room.
        var stale = At("131");

        Assert.Contains("last seen in generation 1", stale.Prose);
        Assert.Contains("history", stale.Prose);
        Assert.DoesNotContain("->", stale.Prose);
        Assert.DoesNotContain("room 33", stale.Prose);
    }

    [Fact]
    public void A_line_that_did_not_go_is_not_a_way_out()
    {
        var corridor = At("222");

        Assert.Contains("southeast — did not go: \"Tamara's room is locked.\"", corridor.Prose);
    }

    [Fact]
    public void A_room_nobody_has_ever_seen_is_not_a_room_the_story_mentioned()
    {
        var missing = At("999");

        Assert.Contains("Nothing on the record matches", missing.Prose);
        Assert.Contains("is not a place the record has been", missing.Prose);
    }

    [Fact]
    public void The_answer_carries_the_facts_the_sentence_carries()
    {
        // Both, from the one read: a model reading a room number out of a sentence is the quiet
        // failure this arrangement exists to remove.
        var facts = System.Text.Json.JsonSerializer.Serialize(
            MapReading.Describe(World(), "222", roomHere: null).Facts);

        Assert.Contains("\"room\":222", facts);
        Assert.Contains("\"generation\":3", facts);
        Assert.Contains("\"typed\":\"east\"", facts);
        Assert.Contains("\"outcome\":\"moved\"", facts);
        Assert.Contains("\"room\":54", facts);
        // The line that did not go, and the attempts, from the same one read. The apostrophe is not
        // asserted: the writer escapes it, and what is being held here is that the game's own words
        // reach the block at all.
        Assert.Contains("\"outcome\":\"blocked\"", facts);
        Assert.Contains("room is locked.", facts);
        Assert.Contains("\"verb\":\"examine\"", facts);
    }

    [Fact]
    public void Asking_nothing_asks_about_where_you_are()
    {
        var here = MapReading.Describe(World(), null, roomHere: 54);

        Assert.Contains("The map from room 54", here.Prose);
    }

    [Fact]
    public void Too_many_ways_out_are_counted_rather_than_listed()
    {
        // The header rides every call, and a block that grows competes for the model's regard with
        // the transcript it is there to help with (`M11.2`).
        string[] ways =
        [
            "north", "south", "west",
            "northeast", "northwest", "southeast", "southwest",
            "up", "down", "in", "out",
        ];

        // Each way out needs the player back in the corridor before it: a move is only a move from
        // the room it was typed in, which is the whole point of the document.
        var turn = 20;
        var room = 500;
        foreach (var way in ways)
        {
            Row(turn++, "west", "The corridor runs east towards an office.", 222, 3);
            Row(turn++, way, "Somewhere else entirely.", room++, 3);
        }

        File.WriteAllLines(ObservationLog.PathFor(_state, "moonmist"), _rows);

        var world = World();
        var corridor = world.Places.Single(place => place.Number == 222);
        var moves = world.Edges.Count(
            edge => edge.From == corridor.Id && edge.Gen == 3 && edge.Outcome == "moved");
        var fromHere = MapReading.FromHere(world, 222);

        // One room, more ways out of it than the header will name, and the rest counted.
        Assert.True(moves > MapReading.FromHereLimit, $"the fixture has enough ways out: {moves}");
        Assert.Contains("east to room 54", fromHere);
        Assert.Contains($"{moves - MapReading.FromHereLimit} more", fromHere);
        Assert.True(
            fromHere.Length < 700,
            $"the header's map lines stay short ({fromHere.Length} characters): {fromHere}");
    }

    // -- the header ---------------------------------------------------------------------------------

    [Fact]
    public void The_header_says_what_is_out_of_here_and_what_has_been_tried_here()
    {
        var world = World();

        var fromHere = MapReading.FromHere(world, 222);
        Assert.Contains("Out of here, the record has:", fromHere);
        Assert.Contains("east to room 54", fromHere);
        Assert.Contains("Already tried here: examine desk, open drawer", fromHere);

        // A room the interpreter has never reported leaves the header with nothing to add rather than
        // with a guess about which room it is.
        Assert.Equal(string.Empty, MapReading.FromHere(world, null));
        Assert.Equal(string.Empty, MapReading.FromHere(world, 999));
    }
}
