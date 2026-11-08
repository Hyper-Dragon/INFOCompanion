namespace Agent.Notebook;

/// <summary>
/// The companion's notes about the harness, the session and its own performance (05_memory.md §7 (`M37`–`M39`)).
/// <para>
/// Not about the game. That distinction is the whole reason this file exists: the notebook holds
/// what is true in the story, the memories hold who the player is, and neither is a place for
/// "I typed RESTORE into a process that had already stopped" or "my own note about the fog was
/// right and I died anyway, because a restore had moved the world behind me".
/// </para>
/// <para>
/// The companion wrote that kind of thing anyway — measured, 2026-09-20, in
/// <c>_companion.md</c>, which travels into every reply in every future game: *"my old notes can
/// be ahead of the world rather than wrong"*. That is a workaround for a harness defect, stored as
/// a fact about the player, and it would be read next month in another game as though it were
/// true of him. This file is where it belongs, and the memories are not.
/// </para>
/// <para>
/// Three properties, and each of them is load-bearing:
/// </para>
/// <list type="bullet">
/// <item><b>It is never in the prompt.</b> Nothing here is read back to the model by the harness.
/// A note that could steer a turn would be a second lever on the game that no test covers, and the
/// companion would start writing for the file instead of for the player.</item>
/// <item><b>It is addressed to the builder.</b> The reader is whoever is debugging an evening, so
/// a note says what happened and what was odd, not how the companion feels about it.</item>
/// <item><b>It points at the defect rather than papering over it.</b> Where the companion is
/// working around something, it says <em>what</em> it is working around, because a workaround that
/// is not written down is a bug that never gets fixed.</item>
/// </list>
/// </summary>
public static class MetaNotes
{
    /// <summary><c>&lt;state&gt;/meta/</c> — beside the notebooks, in a directory of its own.</summary>
    public const string DirectoryName = "meta";

    /// <summary>The longest note that is kept. A note is a note; an essay is a mistake.</summary>
    public const int NoteLimit = 2000;

    /// <summary><c>&lt;state&gt;/meta/&lt;stem&gt;.md</c>.</summary>
    public static string PathFor(string stateDirectory, string gameStem) =>
        Path.Combine(stateDirectory, DirectoryName, Trim(gameStem) + ".md");

    /// <summary>
    /// What the companion wrote, with the label it chose.
    /// </summary>
    /// <param name="about">
    /// A word for what the note is about — <c>harness</c>, <c>myself</c>, <c>the session</c>, or
    /// anything else the model finds useful. Used as the heading, and kept short.
    /// </param>
    public static void Append(
        string stateDirectory,
        string gameStem,
        string note,
        string? about,
        DateTimeOffset at) =>
        Write(stateDirectory, gameStem, Label(about), note, at);

    /// <summary>
    /// A line the harness writes for itself.
    /// <para>
    /// Some failures are the harness's to report and not the model's to notice: a write-up pass
    /// that produced no page, a reply that could not be applied. The companion cannot be asked
    /// about them — it has no way to know the harness gave up — so they are written here by the
    /// thing that knows, which is also what makes "failures are named" true rather than polite
    /// (02_game_boundary.md §9 (`G46`–`G49`)).
    /// </para>
    /// </summary>
    public static void AppendFromTheHarness(
        string stateDirectory,
        string gameStem,
        string note,
        DateTimeOffset at) =>
        Write(stateDirectory, gameStem, "harness", note, at);

    private static void Write(
        string stateDirectory,
        string gameStem,
        string about,
        string note,
        DateTimeOffset at)
    {
        var text = (note ?? string.Empty).Trim();
        if (text.Length == 0 || string.IsNullOrWhiteSpace(gameStem))
        {
            return;
        }

        if (text.Length > NoteLimit)
        {
            // Truncated rather than refused: half a note is worth more than none, and the cut says
            // itself so nobody wonders what was lost.
            text = text[..NoteLimit] + "\n\n_(truncated: a note is not an essay)_";
        }

        var path = PathFor(stateDirectory, gameStem);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var fresh = !File.Exists(path);
            using var writer = new StreamWriter(path, append: true);
            if (fresh)
            {
                writer.WriteLine(Header(gameStem));
            }

            writer.WriteLine();
            writer.WriteLine($"## {about} — {at.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
            writer.WriteLine();
            writer.WriteLine(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A note that cannot be written is a lost note, never a failed turn — the same
            // judgement the notebook and the memories make.
        }
    }

    /// <summary>The file's own explanation, written once, for whoever opens it cold.</summary>
    private static string Header(string gameStem) =>
        "<!-- Quill's notes about the harness, the session and itself. Not about the game: what is\n"
        + "     true in the story belongs in the notebook, and who the player is belongs in the\n"
        + "     memories. Nothing in this file is ever put into the prompt — it is a trail for\n"
        + "     whoever has to work out what went wrong, and it exists so that a workaround for a\n"
        + "     harness defect is written down instead of being carried as lore. -->\n"
        + "\n"
        + $"# notes about the harness — {gameStem}";

    /// <summary>A heading for the note, from whatever the model offered.</summary>
    private static string Label(string? about)
    {
        var text = (about ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "a note";
        }

        // One line, no markup: it is a heading in a file a person reads, and a heading that can
        // reformat the file is not a heading.
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length > 60 ? text[..60].TrimEnd() + "…" : text;
    }

    /// <summary>
    /// A stem that is safe to be a file name — the same rule the core applies to a story's own
    /// name, applied again on the way in, because a stem reaches here from a protocol message.
    /// </summary>
    private static string Trim(string stem)
    {
        var cleaned = new string(
            (stem ?? string.Empty)
                .Trim()
                .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                .ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned.ToLowerInvariant();
    }
}
