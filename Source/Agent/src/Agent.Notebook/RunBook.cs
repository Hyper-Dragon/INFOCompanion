using System.Text.Json;

namespace Agent.Notebook;

/// <summary>
/// Which attempt at this game is in progress, and which fork of the world inside it
/// (02_game_boundary.md §6 (`G27`–`G34`)).
/// <para>
/// The core keeps this bookkeeping and writes it to <c>&lt;state&gt;/runs/&lt;stem&gt;.json</c>. The
/// companion reads it here rather than working it out, because **nothing on the record says a world
/// was forked**: a restore is machinery and never lands as a turn (`G33`), so the record cannot
/// answer the question and the one writer can.
/// </para>
/// <para>
/// Read freshly for every reply rather than kept in the loop. The handshake states the attempt and
/// the fork once, and a restore in the middle of an evening changes the fork without sending another
/// handshake — so a value taken once at the start is wrong for the rest of the night, in exactly the
/// way that hurts most (05_memory.md §2 (`M13.3`)).
/// </para>
/// </summary>
public static class RunBook
{
    /// <summary><c>&lt;state&gt;/runs/&lt;stem&gt;.json</c> — one small file per game.</summary>
    public static string PathFor(string stateDirectory, string gameStem) =>
        Path.Combine(stateDirectory, "runs", gameStem + ".json");

    /// <summary>One attempt, as the book records it.</summary>
    /// <param name="Number">The number a person would say: the first attempt is 1.</param>
    /// <param name="Generation">Which fork of the world inside it. Starts at 1.</param>
    public readonly record struct Attempt(long Number, long Generation);

    /// <summary>
    /// The attempt in progress, or <c>null</c> when the book says nothing readable — a game nobody
    /// has played, an attempt that has ended, or a file half-written.
    /// <para>
    /// Bookkeeping is never worth failing a reply over, and <c>null</c> here is **cannot say** rather
    /// than a guess at 1: a companion told it is in the first attempt of a game it has played three
    /// times will reason about the wrong world, which is the whole of what this type exists to
    /// prevent.
    /// </para>
    /// </summary>
    public static Attempt? Current(string stateDirectory, string gameStem)
    {
        if (string.IsNullOrWhiteSpace(gameStem))
        {
            return null;
        }

        string text;
        try
        {
            var path = PathFor(stateDirectory, gameStem);
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("current", out var current)
                || current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Zero is the record's own word for a field that predates it, and it is kept that way
            // rather than defaulted to a number that would read as a fact (G29).
            var number = current.TryGetProperty("number", out var n) && n.TryGetInt64(out var numberValue)
                ? numberValue
                : 0;
            var generation = current.TryGetProperty("generation", out var g) && g.TryGetInt64(out var generationValue)
                ? generationValue
                : 0;

            return new Attempt(number, generation);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
