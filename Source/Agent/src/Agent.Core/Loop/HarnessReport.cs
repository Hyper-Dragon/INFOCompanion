namespace Agent.Core.Loop;

/// <summary>
/// What the harness has to say about its own side of what just happened (05_memory.md §7 (`M37`–`M39`)).
/// <para>
/// The companion reads the screen, its notes and its memories, and all three are about the game.
/// The harness is the only end that knows the rest: that a reply carried no move, that a line was
/// refused at a key prompt, that the provider cut a reply off, that the checkpoint never confirmed.
/// That knowledge used to live only in the log — so a session where the harness was failing quietly
/// looked, from the inside, exactly like a game that was hard, and working out which one it was
/// took a human, a bundle and a file diff.
/// </para>
/// <para>
/// This is that knowledge, in the harness's own words and nothing else. It **measures** — every row
/// is an event the harness already wrote, none of them is a reading of the model's prose, which is
/// the same rule the whole of 02_game_boundary.md §8 (`G41`–`G45`) rests on — and it is bounded twice: one row per kind, and
/// a hard cap on the rows, because the pass that reads it is bounded too and a report the companion
/// has to skim is worse than one it reads.
/// </para>
/// <para>
/// It clears when the world moves. Trouble since the game last answered is this evening's trouble;
/// a report that carried the whole session would be a second log, and the companion would be
/// reading an accounting of the past instead of looking at what has just gone wrong.
/// </para>
/// </summary>
public sealed class HarnessReport
{
    /// <summary>How many rows one report may carry. Six is a screenful, not a file.</summary>
    public const int MaxRows = 6;

    private readonly List<string> _rows = new();
    private readonly HashSet<string> _kinds = new(StringComparer.Ordinal);

    /// <summary>Is there anything to say? False is the common case, and the pass never runs.</summary>
    public bool IsEmpty => _rows.Count == 0;

    /// <summary>The rows, in the order they happened.</summary>
    public IReadOnlyList<string> Rows => _rows;

    /// <summary>
    /// Note one thing that went wrong, in the harness's own words.
    /// <para>
    /// One row per <paramref name="kind"/>: the same failure four times is one fact about the
    /// session rather than four rows of it, and the cap is what stops a bad minute filling the
    /// report with the same sentence. Returns whether the row was kept, so the caller can log the
    /// drop it kept and stay quiet about the one it did not.
    /// </para>
    /// </summary>
    public bool Note(string kind, string row)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        if (_rows.Count >= MaxRows || !_kinds.Add(kind))
        {
            return false;
        }

        _rows.Add(row.Trim());
        return true;
    }

    /// <summary>
    /// The report as the companion reads it: a list, labelled as the harness's own account rather
    /// than as something the story said, because the one thing that must not happen is a fact about
    /// the machinery being taken for lore.
    /// </summary>
    public string Render()
    {
        if (_rows.Count == 0)
        {
            return string.Empty;
        }

        return "From the harness rather than from the game — what it saw on its own side of this, "
            + "in its own words:\n"
            + string.Join("\n", _rows.Select(row => "- " + row));
    }

    /// <summary>Forget everything. Called when the world moves, and once the report has been read.</summary>
    public void Clear()
    {
        _rows.Clear();
        _kinds.Clear();
    }
}
