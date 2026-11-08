using System.Globalization;
using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The notebook as the reading rather than the record, and the one thing the harness checks about it
/// (05_memory.md §1 (`M2.3`)).
/// </summary>
public class PlanPlacesTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-plan-tests", Guid.NewGuid().ToString("N"));

    public PlanPlacesTests()
    {
        Directory.CreateDirectory(Path.Combine(_state, "observations"));
        Directory.CreateDirectory(Path.Combine(_state, "runs"));

        Row(1, "look", "The gallery is long and cold.", 131, 1);
        Row(2, "look", "The corridor runs east towards an office.", 222, 3);
        Row(3, "east", "The office is small and tidy.", 54, 3);

        File.WriteAllLines(ObservationLog.PathFor(_state, "moonmist"), _rows);
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

    [Fact]
    public void A_place_is_read_the_way_the_harness_writes_it()
    {
        // `room 222` and `loc 222` are places; a turn number, a time and a count are not. Only the
        // written form is read, and everything else in a note is left alone.
        var named = PlanPlaces.Named(
            "## Current goals and plan\n\n- Get back to room 222\n- Ask at loc 54 about the poodle\n"
            + "- We tried this at turn 311 and again at 08:15, three times\n");

        Assert.Equal([222L, 54L], named);
    }

    [Fact]
    public void A_room_only_this_world_has_resolves_and_one_from_the_last_world_does_not()
    {
        var world = World();

        Assert.Empty(PlanPlaces.Unresolved(world, "back to room 222, then room 54"));
        Assert.Equal([131L], PlanPlaces.Unresolved(world, "back to room 222, then room 131"));
    }

    [Fact]
    public void A_plan_naming_a_room_this_world_does_not_have_is_said_out_loud()
    {
        var report = PlanPlaces.Report(World(), "## Current goals and plan\n\n- Get back to room 131\n");

        Assert.Contains("Your plan names room 131", report);
        Assert.Contains("this world does not have it", report);
        Assert.Contains("cannot be run", report);

        // And nothing at all when every place it names is one it can reach: this rides every call.
        Assert.Equal(string.Empty, PlanPlaces.Report(World(), "back to room 222"));
        Assert.Equal(string.Empty, PlanPlaces.Report(World(), "no places named here"));
    }

    [Fact]
    public void With_no_world_document_every_named_place_is_unresolved()
    {
        Assert.Equal([222L], PlanPlaces.Unresolved(null, "room 222"));
    }

    [Fact]
    public void The_notebook_is_told_it_is_a_reading_and_not_a_record()
    {
        // The acceptance test for the whole block: a line the notebook holds that a look-up could
        // have answered is a line in the wrong place.
        var assembler = new ContextAssembler(new AgentConfig(), "PERSONA");

        var assembled = assembler.Assemble("## Current goals and plan\n\n- back to room 222", [], "hello");
        var system = assembled.Messages[0].Content;

        Assert.Contains("This is your reading of the game, not a record of it", system);
        Assert.Contains("A line here that a look-up could have answered is a line in the wrong place", system);
        Assert.Contains("Write a place as `room <number>`", system);
    }

    [Fact]
    public void And_the_unresolved_places_ride_beside_it()
    {
        var assembler = new ContextAssembler(new AgentConfig(), "PERSONA");

        var assembled = assembler.Assemble(
            "## Current goals and plan\n\n- back to room 131",
            [],
            "hello",
            unresolvedPlaces: PlanPlaces.Report(World(), "back to room 131"));

        Assert.Contains("Your plan names room 131", assembled.Messages[0].Content);
    }
}
