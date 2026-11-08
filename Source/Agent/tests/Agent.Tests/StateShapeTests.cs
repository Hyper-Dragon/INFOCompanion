using Agent.Core;

namespace Agent.Tests;

/// <summary>
/// The agent's half of the state-directory guard (02_game_boundary.md §9 (`G51`)).
/// <para>
/// The core claims a directory; the agent only reads one. These tests hold the agent to
/// <em>reading nothing it has not been admitted to</em>: an unclaimed directory, a shape written
/// by another build and a marker that will not parse are all refusals, and each names the fix.
/// </para>
/// </summary>
public class StateShapeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ic-state-shape-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test over.
        }
    }

    private string Claimed(long shape)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, StateShape.Marker),
            $"{{\"shape\": {shape}}}");
        return _root;
    }

    [Fact]
    public void A_directory_claimed_in_this_shape_is_admitted()
    {
        Assert.Null(StateShape.Refuse(Claimed(StateShape.Shape)));
    }

    [Fact]
    public void A_directory_that_was_never_claimed_is_refused()
    {
        var refusal = StateShape.Refuse(_root);

        Assert.NotNull(refusal);
        Assert.Contains("has not been claimed", refusal);
        Assert.Contains(_root, refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void A_shape_written_by_another_build_is_refused_whichever_way_round(long shape)
    {
        var refusal = StateShape.Refuse(Claimed(shape));

        Assert.NotNull(refusal);
        Assert.Contains($"shape {shape}", refusal);
        Assert.Contains($"reads shape {StateShape.Shape}", refusal);
        Assert.Contains("Nothing has been migrated", refusal);
    }

    [Fact]
    public void A_marker_with_no_shape_is_refused()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, StateShape.Marker), "{}");

        var refusal = StateShape.Refuse(_root);

        Assert.NotNull(refusal);
        Assert.Contains("no shape", refusal);
    }

    [Fact]
    public void A_marker_that_will_not_parse_is_refused_rather_than_read_as_fresh()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, StateShape.Marker), "not json");

        var refusal = StateShape.Refuse(_root);

        Assert.NotNull(refusal);
        Assert.Contains("not a shape marker", refusal);
    }

    [Fact]
    public void The_two_processes_agree_on_the_marker_and_the_shape()
    {
        // The name and the number are a contract between two languages. This is the only place
        // they can be compared inside one build, so it is checked here rather than discovered
        // from a container that will not start.
        Assert.Equal("state-shape.json", StateShape.Marker);
        Assert.Equal(1L, StateShape.Shape);
    }
}
