using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Agent.Notebook;

/// <summary>
/// What the companion has made of the world: its own hypotheses about cause and effect, written down
/// where the harness cannot confuse them with what it measured (05_memory.md §8 (`M43`)).
/// <para>
/// Append-only, in its **own** file, and deliberately not in the world document: that document is
/// regenerated from the record at every session open (`M40.1`), so anything written into it is erased
/// by the next open. What the harness measured and what the companion concluded have different
/// lifetimes, and they live in different files for that reason.
/// </para>
/// <para>
/// One entry is one thing the companion decided: a cause, an effect, and — in its own words — how sure
/// it is. The support is turn numbers out of the record, so a hypothesis can be checked against what
/// actually happened rather than believed, and an entry may **retract** an earlier one: the lever that
/// closed a door three turns after it opened one is the whole reason a hypothesis has to be killable.
/// </para>
/// </summary>
public static class Annotations
{
    /// <summary>One entry, as it is written.</summary>
    /// <param name="Id">Which entry this is, counted from 1, so one hypothesis can name another.</param>
    /// <param name="Kind">`cause` for a hypothesis, `reading` for a conclusion about the place itself.</param>
    /// <param name="Cause">What the companion believes did it, in its own words.</param>
    /// <param name="Effect">What it believes happened, in its own words — or the game's.</param>
    /// <param name="Support">Turn numbers out of the record that this rests on. May be empty.</param>
    /// <param name="Confidence">How sure it is, in its own words: *"certain"*, *"a guess"*.</param>
    /// <param name="Retracts">The id of an entry this one kills, or null.</param>
    /// <param name="At">When it was written, UTC.</param>
    public sealed record Entry(
        long Id,
        string Kind,
        string Cause,
        string Effect,
        IReadOnlyList<long> Support,
        string Confidence,
        long? Retracts,
        string At);

    /// <summary><c>&lt;state&gt;/world/&lt;stem&gt;.annotations.jsonl</c> — beside the document, never in it.</summary>
    public static string PathFor(string stateDirectory, string gameStem) =>
        Path.Combine(stateDirectory, WorldState.Folder, gameStem + ".annotations.jsonl");

    /// <summary>Write one entry. Returns it, with the id the store gave it.</summary>
    public static Entry Append(
        string stateDirectory,
        string gameStem,
        string kind,
        string cause,
        string effect,
        IReadOnlyList<long> support,
        string confidence,
        long? retracts = null,
        DateTimeOffset? at = null)
    {
        var entry = new Entry(
            All(stateDirectory, gameStem).Count + 1,
            kind,
            cause.Trim(),
            effect.Trim(),
            support,
            confidence.Trim(),
            retracts,
            (at ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));

        var path = PathFor(stateDirectory, gameStem);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(
            path,
            JsonSerializer.Serialize(entry, Format) + "\n");
        return entry;
    }

    /// <summary>Every entry, oldest first, or none when there is no file.</summary>
    public static IReadOnlyList<Entry> All(string stateDirectory, string gameStem)
    {
        if (string.IsNullOrWhiteSpace(gameStem))
        {
            return [];
        }

        string[] lines;
        try
        {
            var path = PathFor(stateDirectory, gameStem);
            if (!File.Exists(path))
            {
                return [];
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }

        var entries = new List<Entry>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<Entry>(line, Format) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A half-written line from a crash; it was never an entry.
            }
        }

        return entries;
    }

    /// <summary>
    /// The entries nothing has retracted, which is what is still worth telling the companion about.
    /// </summary>
    public static IReadOnlyList<Entry> Live(string stateDirectory, string gameStem)
    {
        var all = All(stateDirectory, gameStem);
        var killed = all.Where(entry => entry.Retracts is not null).Select(entry => entry.Retracts!.Value).ToHashSet();
        return [.. all.Where(entry => !killed.Contains(entry.Id))];
    }

    /// <summary>
    /// What the companion believes, as the block that rides the prompt: bounded, because this is a
    /// growing file in a block that goes out on every call (`M11.2`).
    /// </summary>
    public static string? ReadForPrompt(
        string stateDirectory,
        string gameStem,
        WorldState.Document? world = null,
        int limit = 12)
    {
        var live = Live(stateDirectory, gameStem);
        if (live.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine("These are your own beliefs about how this game works, not facts from the harness.");
        foreach (var entry in live.Skip(Math.Max(0, live.Count - limit)))
        {
            text.Append("- ").Append(entry.Cause).Append(" -> ").Append(entry.Effect);
            text.Append(" (").Append(entry.Confidence);
            if (entry.Support.Count > 0)
            {
                text.Append(": turns ").Append(string.Join(", ", entry.Support));
            }

            text.Append(Confound(world, entry.Support)).AppendLine(")");
        }

        if (live.Count > limit)
        {
            text.AppendLine($"…and {live.Count - limit} earlier ones.");
        }

        return text.ToString();
    }

    /// <summary>
    /// What else the record shows changing in the same window, so a confound is **in front of the
    /// companion** rather than something it has to think to ask about: *"the gong also rang"* is the
    /// whole of what separates a cause from a coincidence.
    /// </summary>
    private static string Confound(WorldState.Document? world, IReadOnlyList<long> support)
    {
        if (world is null || support.Count == 0)
        {
            return string.Empty;
        }

        var from = support.Min();
        var to = support.Max();
        var others = world.Things
            .Where(thing => thing.FirstSeen >= from && thing.LastSeen <= to)
            .Select(thing => $"{thing.Name} said \"{thing.Value}\"")
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        return others.Count == 0
            ? string.Empty
            : " · and at the same time: " + string.Join("; ", others);
    }

    /// <summary>One line per entry, for a person reading the file — the shape `05_memory.md` `M43` asks for.</summary>
    public static string Describe(IReadOnlyList<Entry> entries) =>
        entries.Count == 0
            ? "Nothing has been written down about how this game works yet."
            : string.Join(
                "\n",
                entries.Select(
                    entry => $"{entry.Id}. [{entry.Kind}] {entry.Cause} -> {entry.Effect} "
                        + $"({entry.Confidence}{(entry.Retracts is { } id ? $"; retracts {id}" : string.Empty)})"));

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };
}
