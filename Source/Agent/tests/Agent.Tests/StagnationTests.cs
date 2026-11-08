using System.Globalization;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// A stretch of play that is going nowhere, reported as a fact (04_companion.md `C39`).
/// <para>
/// The target is measured: 00:49:38 → 00:55:33 on 2026-09-29, twenty-five turns of sweeping one room
/// with every verb the player could justify, ending in *"sheesh"*. The fixture is that shape with the
/// words invented.
/// </para>
/// </summary>
public class StagnationTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-stagnation-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _rows = [];

    public StagnationTests()
    {
        Directory.CreateDirectory(Path.Combine(_state, "observations"));
        Directory.CreateDirectory(Path.Combine(_state, "runs"));
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

    private void Row(int turn, string input, string output, long location) =>
        _rows.Add(
            $$"""{"turn":{{turn}},"input":"{{input}}","output":"{{output}}","fingerprint":"f#{{turn}}","run":1,"generation":3,"location":{{location.ToString(CultureInfo.InvariantCulture)}}}""");

    private WorldState.Document Write()
    {
        File.WriteAllLines(ObservationLog.PathFor(_state, "moonmist"), _rows);
        File.WriteAllText(
            Path.Combine(_state, "runs", "moonmist.json"),
            """{"current":{"number":1,"started":"2026-09-29T00:41:00Z","generation":3}}""");
        return WorldState.Build(_state, "moonmist");
    }

    /// <summary>The game room, swept with one verb after another, with nothing to show for it.</summary>
    private void Sweep(int from, int turns)
    {
        for (var i = 0; i < turns; i++)
        {
            Row(from + i, $"examine thing{i % 4}", "You find nothing of interest.", 96);
        }
    }

    [Fact]
    public void A_long_sweep_with_nothing_new_in_it_is_reported()
    {
        Row(1, "look", "The game room, with a gun rack and a desk.", 96);
        Sweep(2, 24);

        var world = Write();
        var fact = Stagnation.Fact(world, 25, turnsPlayed: 25);

        Assert.NotNull(fact);
        Assert.Contains("Nothing new for", fact!);
        Assert.Contains("no room entered for the first time", fact!);
        Assert.Contains("rather than a verdict", fact!);
    }

    [Fact]
    public void A_stretch_that_found_something_is_not_reported()
    {
        // A new room, or one new thing said about anything, and the window is not a sweep.
        Row(1, "look", "The game room, with a gun rack and a desk.", 96);
        for (var i = 0; i < 20; i++)
        {
            Row(2 + i, "examine desk", "The desk is bare.", 96);
        }

        Row(22, "east", "The office is small and tidy.", 54);

        var world = Write();

        Assert.Null(Stagnation.Fact(world, 23, turnsPlayed: 23));
    }

    [Fact]
    public void A_window_with_barely_any_turns_in_it_is_not_a_wander()
    {
        // The start of an evening is not stagnation, and neither is a window the companion spent away.
        Row(1, "look", "The game room.", 96);
        for (var i = 0; i < 4; i++)
        {
            Row(2 + i, "examine thing", "Nothing of interest.", 96);
        }

        var world = Write();

        Assert.Null(Stagnation.Fact(world, 6, turnsPlayed: 5));
    }

    [Fact]
    public void The_stretch_carries_its_own_numbers_and_known_ground_does_not_clear_it()
    {
        // `§25` item 4: the delta was built and told as a sentence, and what a signal needs is
        // something it can be acted on — so the same stretch is returned as numbers too. A move to a
        // room already known is not news: the record did not grow, so the stretch stands and reports
        // how many of those turns went somewhere.
        // Every first in the *first four turns* — two rooms, two things examined — and then twenty turns
        // that meet nothing for the first time. The window is why they have to be early: a stretch that
        // looks back twenty turns still sees anything first met inside it, so a fixture that puts a new
        // thing at turn 6 is a fixture that found something, and it is the fixture's fault rather than
        // the reader's.
        Row(1, "look", "The game room, with a gun rack and a desk.", 96);
        Row(2, "east", "The office is small and tidy.", 54);
        Row(3, "examine thing0", "You find nothing of interest.", 54);
        Row(4, "examine thing1", "You find nothing of interest.", 54);

        // Two moves inside the window, both through ground already known — which is not news, and is
        // exactly what the sentence has to say out loud.
        Row(5, "west", "The game room, with a gun rack and a desk.", 96);
        Row(6, "east", "The office is small and tidy.", 54);
        for (var i = 0; i < 17; i++)
        {
            Row(7 + i, $"examine thing{i % 2}", "You find nothing of interest.", 54);
        }

        var world = Write();
        var stretch = Stagnation.Look(world, 24, turnsPlayed: 19);

        Assert.NotNull(stretch);
        Assert.Equal(19, stretch!.Turns);
        Assert.Equal(2, stretch.Moved);

        // One computation, two shapes: the sentence and the numbers cannot come to disagree about the
        // stretch a prompt is reporting and a consult is acting on.
        Assert.Equal(stretch.Prose, Stagnation.Fact(world, 24, turnsPlayed: 19));
        Assert.Contains($"{stretch.Turns} turns", stretch.Prose);
        Assert.Contains($"{stretch.Moved} of those turns", stretch.Prose);
    }

    [Fact]
    public void The_window_is_counted_from_the_record_and_not_from_what_it_ledgered()
    {
        // The defect this parameter exists for, measured on 2026-10-01: nineteen turns of Wishbringer's
        // Steep Trail, and the document's ledger held two of them, because it holds the lines worth
        // reporting on and a stretch is made of turns that were worth nothing. Counted there, the
        // window came to two turns — under half of a twenty-turn window, which is "too young to be a
        // wander" — so the fact did not fire on the one stretch it was written for.
        //
        // So: the same bare ledger, and the count the record itself gives. It fires.
        // What is *said* in a turn counts as learned, so the fixture's own firsts have to fall outside
        // the window it asks about — the same trap the numbers test above records. Here the window at
        // turn 22 starts at turn 2, so the last new thing named is in turn 2 and everything after it
        // repeats something already said.
        Row(1, "look", "The game room, with a gun rack and a desk.", 96);
        Row(2, "examine thing0", "You find nothing of interest.", 96);
        for (var i = 0; i < 20; i++)
        {
            Row(3 + i, "examine thing0", "You find nothing of interest.", 96);
        }

        var world = Write();

        Assert.Null(Stagnation.Fact(world, 22, turnsPlayed: 4));
        Assert.NotNull(Stagnation.Fact(world, 22, turnsPlayed: 22));
    }

    [Fact]
    public void The_silence_says_which_silence_it_is()
    {
        // `C39.9`: a stretch that cannot fire and one with nothing to say read the same in a log — a
        // missing row — and two sittings on 2026-10-01 were spent working out which. The counts are
        // the same numbers `Look` decides on, so the two cannot drift: whatever `Look` refused on is
        // named here.
        Row(1, "look", "The game room, with a gun rack and a desk.", 96);
        Row(2, "east", "The office is small and tidy.", 54);
        for (var i = 0; i < 20; i++)
        {
            Row(3 + i, "examine desk", "The desk is bare.", 54);
        }

        var world = Write();

        // The window saw one room for the first time, so the record grew and that is why the fact is
        // not being put. The counts agree with the reading rather than with the story.
        var grown = Stagnation.Counts(world, 22, turnsPlayed: 22);
        Assert.NotNull(grown);
        Assert.Equal(22, grown!.Turns);
        Assert.True(grown.FoundSomething, $"new places={grown.NewPlaces} things={grown.NewThings}");
        Assert.Contains("the record grew", grown.Why);
        Assert.Null(Stagnation.Look(world, 22, turnsPlayed: 22));

        // Too little played is its own answer, and it is the one that must not be mistaken for a
        // sweep that found nothing.
        var young = Stagnation.Counts(world, 22, turnsPlayed: 4);
        Assert.NotNull(young);
        Assert.True(young!.TooYoung);
        Assert.Contains("too young", young.Why);

        // And no world at all is nothing to read rather than a stretch.
        Assert.Null(Stagnation.Counts(null, 22, turnsPlayed: 22));
        Assert.Null(Stagnation.Counts(world, 0, turnsPlayed: 0));
    }

    [Fact]
    public void Nothing_is_said_without_a_document_or_a_fork()
    {
        Assert.Null(Stagnation.Fact(null, 500, turnsPlayed: 500));
        Assert.Null(Stagnation.Fact(Write(), 0, turnsPlayed: 0));
    }
}
