using System.Globalization;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The companion's own hypotheses about how a game works: storable, inspectable, and **killable**
/// (05_memory.md §8 (`M43`)).
/// </summary>
public class AnnotationsTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-annotation-tests", Guid.NewGuid().ToString("N"));

    public AnnotationsTests() => Directory.CreateDirectory(_state);

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

    private string ThePath => Annotations.PathFor(_state, "moonmist");

    [Fact]
    public void A_hypothesis_is_stored_in_its_own_file_beside_the_document()
    {
        // Deliberately not in the world document: that one is regenerated from the record at every
        // session open, so anything written into it is erased by the next open. These are the only
        // notes on disk that a rebuild cannot produce.
        Annotations.Append(
            _state, "moonmist", "cause", "pull the lever", "the study door closes", [537, 538], "certain");

        Assert.EndsWith(
            System.IO.Path.Combine("world", "moonmist.annotations.jsonl"),
            ThePath,
            StringComparison.Ordinal);
        Assert.Single(Annotations.All(_state, "moonmist"));
    }

    [Fact]
    public void An_entry_carries_the_turns_it_rests_on_and_the_companions_own_confidence()
    {
        var entry = Annotations.Append(
            _state,
            "moonmist",
            "cause",
            "pull the lever",
            "the study door closes",
            [537, 538],
            "a guess");

        Assert.Equal(1, entry.Id);
        Assert.Equal([537L, 538L], entry.Support);
        Assert.Equal("a guess", entry.Confidence);
        Assert.Null(entry.Retracts);
    }

    [Fact]
    public void A_belief_the_game_contradicted_can_be_killed_without_being_erased()
    {
        // The lever opens the study door at 536 and closes it again at 538. The second pull is what
        // makes "pull means open" false, and a store that can only append is a store where the false
        // belief has to be able to be *named* as dead rather than quietly overwritten.
        var first = Annotations.Append(
            _state, "moonmist", "cause", "pull the lever", "the door opens", [536], "certain");
        Annotations.Append(
            _state, "moonmist", "cause", "pull the lever again", "the door closes", [538], "certain", first.Id);

        var live = Annotations.Live(_state, "moonmist");

        // The killed belief is gone and the killing one is live: what the companion believes now is
        // the correction, not the thing it corrected.
        var only = Assert.Single(live);
        Assert.Equal(2, only.Id);
        // And it is still on the record: a belief that was held is part of what happened.
        Assert.Equal(2, Annotations.All(_state, "moonmist").Count);
    }

    [Fact]
    public void Only_the_live_beliefs_ride_the_prompt()
    {
        var first = Annotations.Append(
            _state, "moonmist", "cause", "pull the lever", "the door opens", [536], "certain");
        Annotations.Append(
            _state, "moonmist", "cause", "light the lantern", "the passage is visible", [600], "probably");
        Annotations.Append(
            _state, "moonmist", "cause", "the lever again", "the door closes", [538], "certain", first.Id);

        var block = Annotations.ReadForPrompt(_state, "moonmist");

        Assert.NotNull(block);
        Assert.Contains("light the lantern -> the passage is visible", block!);
        Assert.Contains("probably", block!);
        // The killed one is gone, and the block says whose words these are.
        Assert.DoesNotContain("the door opens", block!);
        Assert.Contains("not facts from the harness", block!);
    }

    [Fact]
    public void What_else_changed_at_the_same_time_rides_beside_the_belief()
    {
        // The confound, in front of the companion rather than something it has to think to ask about:
        // "the gong also rang" is the whole of what separates a cause from a coincidence.
        Row(537, "pull lever", "The lever moves; the study door closes.", 31, 3);
        Row(539, "wait", "The dinner gong rings somewhere below.", 31, 3);

        var world = WorldState_Document();
        Annotations.Append(
            _state, "moonmist", "cause", "pull the lever", "the door closes", [537, 539], "a guess");

        var block = Annotations.ReadForPrompt(_state, "moonmist", world);

        Assert.NotNull(block);
        Assert.Contains("and at the same time", block!);
        Assert.Contains("The lever moves; the study door closes.", block!);
    }

    [Fact]
    public void A_file_that_is_not_there_is_no_beliefs_rather_than_an_error()
    {
        Assert.Empty(Annotations.All(_state, "nothing-here"));
        Assert.Null(Annotations.ReadForPrompt(_state, "nothing-here"));
        Assert.Contains("Nothing has been written down", Annotations.Describe(Annotations.All(_state, "nothing-here")));
    }

    private readonly List<string> _rows = [];

    private void Row(int turn, string input, string output, long location, long generation) =>
        _rows.Add(
            $$"""{"turn":{{turn}},"input":"{{input}}","output":"{{output}}","fingerprint":"f#{{turn}}","run":1,"generation":{{generation}},"location":{{location.ToString(CultureInfo.InvariantCulture)}}}""");

    private WorldState.Document WorldState_Document()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_state, "observations"));
        Directory.CreateDirectory(System.IO.Path.Combine(_state, "runs"));
        File.WriteAllLines(ObservationLog.PathFor(_state, "moonmist"), _rows);
        File.WriteAllText(
            System.IO.Path.Combine(_state, "runs", "moonmist.json"),
            """{"current":{"number":1,"started":"2026-09-29T00:41:00Z","generation":3}}""");
        return WorldState.Build(_state, "moonmist");
    }
}
