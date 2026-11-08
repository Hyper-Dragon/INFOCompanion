using System.Text.RegularExpressions;

namespace Agent.Notebook;

/// <summary>
/// The places a piece of the companion's own writing names, and whether the world it is playing
/// actually has them (05_memory.md §1 (`M2.3`)).
/// <para>
/// A plan is state-dependent: *"get us back to our room"* cannot be run without knowing which room,
/// and the errand that failed on 2026-09-29 was built out of a plan that named a place the
/// companion could not identify. So a place written down is resolved against the world document,
/// and one that resolves to nothing is **said out loud** rather than left for a move to discover.
/// </para>
/// <para>
/// Only places written the way the harness writes them are read — <c>room 222</c>, or <c>loc 222</c>
/// as the notes have it. A bare number is not read at all, because a note is full of bare numbers
/// that are not rooms: turn numbers, times of day, counts of things, and the year in a date. Nothing
/// here reads prose; it reads one written form and ignores everything else.
/// </para>
/// </summary>
public static partial class PlanPlaces
{
    /// <summary>The written form: `room 222`, `Room 222`, `loc 222`, `room #222`.</summary>
    [GeneratedRegex(@"\b(?:room|loc)\s*#?\s*(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceWritten();

    /// <summary>The room numbers named in a piece of writing, oldest mention first, each once.</summary>
    public static IReadOnlyList<long> Named(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new List<long>();
        foreach (Match match in PlaceWritten().Matches(text))
        {
            if (long.TryParse(match.Groups[1].Value, out var number) && !seen.Contains(number))
            {
                seen.Add(number);
            }
        }

        return seen;
    }

    /// <summary>
    /// The named rooms the world document does not have in the fork being played — the ones an errand
    /// built on them cannot reach.
    /// </summary>
    public static IReadOnlyList<long> Unresolved(WorldState.Document? world, string? text)
    {
        var named = Named(text);
        if (named.Count == 0)
        {
            return [];
        }

        if (world is null)
        {
            return named;
        }

        var generation = world.Header.Generation;
        return
        [
            .. named.Where(number => !world.Places.Any(
                place => place.Number == number && (generation is null || place.LastSeen.Gen == generation))),
        ];
    }

    /// <summary>
    /// What to tell the companion about them, or nothing when every place it named is one it can
    /// reach. One sentence: this rides the prompt on every call, so it says what is wrong and stops.
    /// </summary>
    public static string Report(WorldState.Document? world, string? text)
    {
        var unresolved = Unresolved(world, text);
        if (unresolved.Count == 0)
        {
            return string.Empty;
        }

        var rooms = string.Join(", ", unresolved.Select(number => $"room {number}"));
        return $"\n\nYour plan names {rooms}, and this world does not have "
            + (unresolved.Count == 1 ? "it" : "them")
            + ". Either it was walked in a world that has since been replaced, or it was never anywhere "
            + "but in the notes. An errand built on it cannot be run: ask the map what this world has, "
            + "or go and find the place before planning with it.";
    }
}
