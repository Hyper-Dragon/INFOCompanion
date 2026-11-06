using System.Text;

namespace Agent.Notebook;

/// <summary>
/// What Quill remembers about *you*, across games and across evenings (issue #27).
/// <para>
/// The game notebook is per game and holds the evening's work: the plan, the map, the
/// facts. This is the other thing — who the player is, what they enjoy, what happened the
/// last few times you played, and ideas worth carrying into the next story. The player put
/// it as "Quill becomes a persistent friend over many sessions", and that is a different
/// claim from "cross-game memory": what persists is the relationship, not trivia about old
/// games.
/// </para>
/// <para>
/// Two consequences shape the code below:
/// </para>
/// <list type="bullet">
/// <item>it is a <b>fixed name</b>, not a per-game file, so it survives a switch, a restart
/// and a session ending. A full reset is scoped to one game and cannot reach it — forgetting
/// the player must never be a side effect of repairing a notebook (see `/reset`);</item>
/// <item>it is <b>small</b>. It travels in every model call, so it is budgeted hard and
/// labelled in the prompt as a recollection rather than a fact about the game in front of
/// you. A memory that leaks a solution to a story you are playing is worse than no memory.</item>
/// </list>
/// </summary>
public sealed class PersistentMemory
{
    /// <summary>
    /// The reserved stem. It cannot collide with a game: notebooks are named after story
    /// files, and a leading underscore is not a stem any game discovery produces.
    /// </summary>
    public const string Stem = "_companion";

    /// <summary>The sections this store guarantees, in the order they are written.</summary>
    public static readonly string[] Sections =
    [
        "Who the player is",
        "Evenings we have had",
        "Ideas worth carrying",
        "Player corrections",
    ];

    private const string CorrectionsHeading = "Player corrections";
    private const string MarkdownHeading = "## ";

    /// <summary>
    /// How many tokens the memory may take of the prompt when nothing else says.
    /// <para>
    /// The value in force is <c>memory_token_budget</c> in <c>agent-config.json</c>; this is the
    /// shipped default and what a caller that does not say gets.
    /// </para>
    /// </summary>
    public const int DefaultTokenBudget = 900;

    public PersistentMemory(string stateDirectory, int tokenBudget = DefaultTokenBudget)
    {
        StateDirectory = stateDirectory;
        TokenBudget = tokenBudget;
    }

    public string StateDirectory { get; }

    public int TokenBudget { get; }

    /// <summary><c>&lt;state&gt;/notebook/_companion.md</c>.</summary>
    public string FilePath => Path.Combine(StateDirectory, "notebook", Stem + ".md");

    /// <summary>The file as it is, or the skeleton if there is nothing yet.</summary>
    public string Read()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return File.ReadAllText(FilePath);
            }
        }
        catch (IOException)
        {
            // Fall through: a companion that cannot read its memories should still play.
        }

        return Template();
    }

    /// <summary>
    /// The memory as the prompt should see it, or <c>null</c> when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Returns null for the bare skeleton, so a first evening costs no prompt tokens and —
    /// more importantly — an empty memory cannot be mistaken for a memory that says
    /// "nothing is known about the player".
    /// </remarks>
    public string? ReadForPrompt()
    {
        var markdown = Read();

        // Anything other than the untouched skeleton counts as a memory. Measured: the model
        // writes a short preamble above the sections rather than filling them in, and an
        // earlier version of this check looked for bullet lines and so threw the whole thing
        // away — a memory the companion could not read is not a memory.
        var untouched = string.Equals(
            Collapse(markdown),
            Collapse(Template()),
            StringComparison.Ordinal);
        if (untouched)
        {
            return null;
        }

        if (EstimateTokens(markdown) <= TokenBudget)
        {
            return markdown;
        }

        // Over budget: take it down to whole lines rather than mid-word, and say it was cut
        // so nobody has to guess whether something is missing.
        var characters = TokenBudget * (int)TokenEstimate.CharsPerToken;
        var kept = markdown.Length > characters ? markdown[..characters] : markdown;
        var lastNewline = kept.LastIndexOf('\n');
        if (lastNewline > 0)
        {
            kept = kept[..lastNewline];
        }

        return kept + "\n\n_(older memories were trimmed to fit — rewrite this file shorter)_";
    }

    /// <summary>
    /// Replace the memory, repairing the section structure and writing atomically.
    /// <para>
    /// Player corrections are merged rather than trusted to survive, exactly as in the game
    /// notebook: what the player said about themselves outranking the companion's own idea
    /// of them is a promise, and it should not depend on the model remembering to copy it.
    /// </para>
    /// </summary>
    public void Write(string markdown)
    {
        var candidate = EnsureSections(markdown);

        var preserved = BulletsIn(Read(), CorrectionsHeading);

        // Same rule as the game notebook: the harness captured these before any model call, so a
        // "(player, …)" bullet in a rewrite is a copy, and a copy that drifted by a word is how
        // one correction ends up in the file four times (measured live on 2026-09-16).
        var incoming = BulletsIn(candidate, CorrectionsHeading)
            .Where(bullet => !NotebookStore.IsHarnessCorrection(bullet))
            .ToList();
        var merged = NotebookStore.CollapseCorrections(preserved.Concat(incoming))
            .Select(bullet => bullet.Trim())
            .Where(bullet => bullet.Length > 0 && !bullet.Contains("nothing yet", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (merged.Count > 0)
        {
            candidate = ReplaceSection(candidate, CorrectionsHeading, string.Join('\n', merged));
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, candidate);
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>
    /// Record something the player said about themselves, which outranks the companion's
    /// own impression of them.
    /// </summary>
    public void AppendPlayerCorrection(string text)
    {
        var markdown = EnsureSections(Read());
        var existing = BulletsIn(markdown, CorrectionsHeading);
        existing.Add($"- (player, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}Z) {text.Trim()}");
        Write(ReplaceSection(markdown, CorrectionsHeading, string.Join('\n', existing)));
    }

    /// <summary>Approximate token count, for the budget check.</summary>
    public static int EstimateTokens(string text) => TokenEstimate.From(text);

    /// <summary>Is the memory over its budget?</summary>
    public bool OverBudget(string markdown) => EstimateTokens(markdown) > TokenBudget;

    /// <summary>The skeleton handed to a companion that has not played with anyone yet.</summary>
    public static string Template()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Memories");
        builder.AppendLine();
        builder.AppendLine("<!-- Quill's memory of the player, kept across games and evenings.");
        builder.AppendLine("     Small on purpose: this travels into every reply, so it should hold what");
        builder.AppendLine("     is worth carrying and nothing that belongs in a game's own notebook. -->");
        builder.AppendLine();
        foreach (var section in Sections)
        {
            builder.AppendLine(Heading(section));
            builder.AppendLine();
            builder.AppendLine("_(nothing yet)_");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>Add any missing section headings, so a careless rewrite cannot lose one.</summary>
    public static string EnsureSections(string markdown)
    {
        var text = markdown ?? string.Empty;
        var missing = Sections
            .Where(section => !text.Contains(Heading(section), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.TrimEnd());
        builder.AppendLine();
        foreach (var section in missing)
        {
            builder.AppendLine();
            builder.AppendLine(Heading(section));
            builder.AppendLine();
            builder.AppendLine("_(nothing yet)_");
        }

        return builder.ToString();
    }

    private static string Heading(string section) => MarkdownHeading + section;

    /// <summary>Whitespace-insensitive comparison, so a reformat does not count as a memory.</summary>
    private static string Collapse(string text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The bullet lines under one heading.</summary>
    private static List<string> BulletsIn(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return [];
        }

        var afterHeading = start + heading.Length;
        var nextSection = markdown.IndexOf("\n## ", afterHeading, StringComparison.Ordinal);
        var body = nextSection < 0 ? markdown[afterHeading..] : markdown[afterHeading..nextSection];

        return body
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Replace a section's body, leaving the headings around it alone.</summary>
    private static string ReplaceSection(string markdown, string heading, string body)
    {
        var start = markdown.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return markdown + "\n" + Heading(heading) + "\n\n" + body + "\n";
        }

        var afterHeading = start + heading.Length;
        var nextSection = markdown.IndexOf("\n## ", afterHeading, StringComparison.Ordinal);
        var tail = nextSection < 0 ? string.Empty : markdown[nextSection..];
        return markdown[..afterHeading] + "\n\n" + body + "\n" + tail;
    }
}
