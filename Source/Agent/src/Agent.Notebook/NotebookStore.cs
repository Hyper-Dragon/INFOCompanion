using System.Text;

namespace Agent.Notebook;

/// <summary>
/// The companion's memory for one game (§10.5): a plain markdown file the player can
/// read, and correct.
/// <para>
/// Two things here are deliberately *code*, not prompt:
/// </para>
/// <list type="bullet">
/// <item>the section structure is repaired after every write, so a careless rewrite
/// by the model cannot quietly lose "Player corrections";</item>
/// <item>a player correction is appended the moment it is recognised, before any
/// model call, because "player corrections always win" must not depend on the model
/// noticing (§10.5).</item>
/// </list>
/// </summary>
public sealed class NotebookStore
{
    /// <summary>The sections the notebook is guaranteed to have (§10.5).</summary>
    public static readonly string[] RequiredSections =
    [
        "Current goals and plan",
        "Map notes",
        "Key facts",
        "Open questions",
        "Dead ends",
        "Working theories",
        "Player preferences",
        "Player corrections",
        "Discarded",
    ];

    /// <summary>
    /// The sections that are empty until something happens, and so say nothing about a rewrite when they
    /// are empty after one.
    /// <para>
    /// Nothing has been discarded yet, and the player may simply not have corrected the companion. An
    /// empty one of these is the ordinary state of a game for most of its life, so warning about it is a
    /// warning nobody can act on — measured 2026-10-01, on a game nobody had played for an hour:
    /// <c>required notebook sections are empty after a rewrite {sections: [Player corrections, Discarded]}</c>.
    /// The other sections stay, because "Current goals and plan" reading <c>_(nothing yet)_</c> while the
    /// evening's content sits in sections the model invented is a real defect and the one this measures.
    /// </para>
    /// </summary>
    public static readonly string[] SectionsThatOnlyFillWhenSomethingHappens =
    [
        "Player corrections",
        "Discarded",
    ];

    /// <summary>Phrases that mark a line as the player correcting the companion.</summary>
    private static readonly string[] CorrectionMarkers =
    [
        "actually",
        "remember",
        "note that",
        "you're wrong",
        "you are wrong",
        "i told you",
        "not true",
        "correction",
        "the password is",
        "it's not",
        "its not",
        "we already",
    ];

    /// <summary>
    /// How many tokens the notebook may take of the prompt when nothing else says.
    /// <para>
    /// The value in force is <c>notebook_token_budget</c> in <c>agent-config.json</c>; this is the
    /// shipped default, and the reason it lives here rather than in the configuration type is that
    /// this assembly cannot see that type — the dependency runs the other way.
    /// </para>
    /// </summary>
    public const int DefaultTokenBudget = 6000;

    public NotebookStore(string stateDirectory, string gameStem, int tokenBudget = DefaultTokenBudget)
    {
        StateDirectory = stateDirectory;
        GameStem = gameStem;
        TokenBudget = tokenBudget;
    }

    public string StateDirectory { get; }

    public string GameStem { get; private set; }

    public int TokenBudget { get; }

    /// <summary><c>&lt;state&gt;/notebook/&lt;stem&gt;.md</c>.</summary>
    public string FilePath => Path.Combine(StateDirectory, "notebook", GameStem + ".md");

    /// <summary>Point the notebook at another game. The old game's notes stay on disk.</summary>
    public void Retarget(string gameStem) => GameStem = gameStem;

    /// <summary>Read the notebook, or the skeleton if it does not exist yet.</summary>
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
            // Fall through to the skeleton: the companion should keep playing even
            // if its notes are momentarily unreadable.
        }

        return Template(GameStem);
    }

    /// <summary>
    /// Replace the notebook, repairing the section structure and writing atomically
    /// so a crash cannot leave a half-written memory behind.
    /// <para>
    /// Player corrections are merged in rather than trusted to survive: if the model
    /// forgets them in a rewrite, they are still there afterwards, because a player's
    /// correction outranking the companion's own notes is a promise (§10.5) and not a
    /// suggestion.
    /// </para>
    /// </summary>
    public void Write(string markdown)
    {
        var candidate = EnsureSections(markdown);
        candidate = DropDuplicateSections(candidate);

        var preserved = BulletsIn(Read(), CorrectionsHeading);

        // Corrections are captured in code before any model call, so the preserved set is the
        // truth; what arrives in a rewrite is copies of it, sometimes drifted by a word and
        // sometimes cut off mid-sentence. Measured live on 2026-09-16: the same three
        // corrections ended up in the file three and four times, because an exact-match dedupe
        // cannot see that a copy and its full text are the same fact (05_memory.md (`M1`–`M39`)).
        var incoming = BulletsIn(candidate, CorrectionsHeading).ToList();
        var merged = CollapseCorrections(preserved.Concat(incoming))
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

    /// <summary>Append one player correction, which outranks anything the companion wrote.</summary>
    public void AppendPlayerCorrection(string text)
    {
        var markdown = EnsureSections(Read());
        var existing = BulletsIn(markdown, CorrectionsHeading);
        existing.Add($"- (player, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}Z) {text.Trim()}");
        Write(ReplaceSection(markdown, CorrectionsHeading, string.Join('\n', existing)));
    }

    /// <summary>
    /// Read one section by its heading, or <c>null</c> when nothing matches (04_companion.md).
    /// The heading is matched loosely — case-insensitively, either containing the other —
    /// because the model should be able to ask for "open questions" and get the section.
    /// </summary>
    public string? Section(string heading)
    {
        var lines = Read().Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith('#'))
            {
                continue;
            }

            var title = lines[index].TrimStart('#').Trim();
            if (!HeadingMatches(title, heading))
            {
                continue;
            }

            var body = new StringBuilder();
            for (var next = index + 1; next < lines.Length && !lines[next].StartsWith('#'); next++)
            {
                body.AppendLine(lines[next]);
            }

            var text = body.ToString().Trim();
            return text.Length == 0 ? $"({title} — nothing written yet)" : text;
        }

        return null;
    }

    /// <summary>
    /// Replace one section's body, keeping every other line of the notebook exactly as it is
    /// (04_companion.md). Returns false when no heading matches, so the caller can say so.
    /// </summary>
    public bool UpdateSection(string heading, string markdown)
    {
        heading = NormaliseHeading(heading);
        var lines = Read().Replace("\r\n", "\n").Split('\n').ToList();

        var start = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].StartsWith('#'))
            {
                var title = lines[index].TrimStart('#').Trim();
                if (HeadingMatches(title, heading))
                {
                    start = index;
                    break;
                }
            }
        }

        if (start < 0)
        {
            return false;
        }

        var end = start + 1;
        while (end < lines.Count && !lines[end].StartsWith('#'))
        {
            end++;
        }

        var replacement = new List<string> { lines[start], string.Empty };
        var body = markdown.Replace("\r\n", "\n").Trim();
        if (body.Length > 0)
        {
            replacement.AddRange(body.Split('\n'));
        }

        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, replacement);
        Write(string.Join('\n', lines));
        return true;
    }

    /// <summary>Loose heading match: either side may be the more specific one.</summary>
    private static bool HeadingMatches(string title, string query)
    {
        var wanted = query.Trim();
        return wanted.Length == 0
            || title.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || wanted.Contains(title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A section name as the model wrote it, cleaned: heading marks stripped, whitespace
    /// squeezed. Measured live on 2026-09-16 — a rewrite arrived with a section called
    /// "## Wish-props (from the booklet)", heading mark and all, and the mark then travelled into
    /// the update event and the file. The model meant the section called "Wish-props".
    /// </summary>
    public static string NormaliseHeading(string heading)
    {
        var trimmed = (heading ?? string.Empty).Trim().TrimStart('#').Trim();
        return string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Is this bullet one the harness itself writes for a player correction?</summary>
    public static bool IsHarnessCorrection(string bullet) =>
        bullet.TrimStart().StartsWith("- (player,", StringComparison.OrdinalIgnoreCase)
        || bullet.TrimStart().StartsWith("- (player)", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One copy of each correction, longest wording wins.
    /// <para>
    /// Exact-match dedupe cannot see that a copy and its full text are the same fact, and a
    /// truncation mid-write is exactly how fragments appear (measured live on 2026-09-16: the
    /// same correction three and four times over, several cut off at "…go to the"). Entries are
    /// therefore compared by content — the "(player, …)" prefix the harness writes is stripped
    /// before comparing, so a copy that kept the prefix and a copy that dropped it are still one
    /// correction — and by prefix, so the shorter one that leads the longer one is the same
    /// correction, twice.
    /// </para>
    /// </summary>
    public static IEnumerable<string> CollapseCorrections(IEnumerable<string> bullets)
    {
        var kept = new List<(string Text, string Key)>();
        foreach (var bullet in bullets)
        {
            var text = bullet.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var key = CorrectionKey(text);
            if (key.Length == 0)
            {
                continue;
            }

            var absorbed = false;
            for (var index = kept.Count - 1; index >= 0; index--)
            {
                var other = kept[index];
                if (string.Equals(other.Key, key, StringComparison.Ordinal))
                {
                    if (text.Length > other.Text.Length)
                    {
                        kept[index] = (text, key);
                    }

                    absorbed = true;
                    break;
                }

                if (other.Key.StartsWith(key, StringComparison.Ordinal))
                {
                    absorbed = true; // a shorter fragment of an entry already kept
                    break;
                }

                if (key.StartsWith(other.Key, StringComparison.Ordinal))
                {
                    kept.RemoveAt(index); // the kept one was the fragment
                }
            }

            if (!absorbed)
            {
                kept.Add((text, key));
            }
        }

        return kept.Select(entry => entry.Text);
    }

    /// <summary>
    /// What a correction says, with the harness's own marking taken off — "- (player, 2026-09-16
    /// 22:31Z) the coal is in the cellar" and "- the coal is in the cellar" are one correction.
    /// </summary>
    private static string CorrectionKey(string bullet)
    {
        var text = bullet.Trim();
        if (text.StartsWith('-'))
        {
            text = text[1..].Trim();
        }

        if (text.StartsWith("(player", StringComparison.OrdinalIgnoreCase))
        {
            var close = text.IndexOf(')');
            if (close >= 0)
            {
                text = text[(close + 1)..].Trim();
            }
        }

        return Normalise(text);
    }

    /// <summary>Comparison key for a correction: case, whitespace and trailing punctuation out.</summary>
    private static string Normalise(string text)
    {
        var lowered = text.Trim().ToLowerInvariant();
        var squeezed = new System.Text.StringBuilder(lowered.Length);
        var lastWasSpace = false;
        foreach (var character in lowered)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace && lastWasSpace)
            {
                continue;
            }

            squeezed.Append(isSpace ? ' ' : character);
            lastWasSpace = isSpace;
        }

        return squeezed.ToString().Trim().TrimEnd('.', ',', ';', ':', '!', '?', '…', '—', '-');
    }

    /// <summary>
    /// The headings a notebook has, in the order the file has them, without their marks.
    /// <para>
    /// For the log rather than for the prompt. A rewrite replaces the file, so the only way to
    /// say which sections it left out is to compare the two sets of headings by name — and a
    /// section that goes missing is worth a row of its own, because nothing else in the record
    /// says that part of the past has gone (measured 2026-09-22).
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Headings(string markdown) =>
        (markdown ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.StartsWith('#'))
            .Select(line => line.TrimStart('#').Trim())
            .Where(title => title.Length > 0)
            .ToList();

    public static IReadOnlyList<string> EmptySectionsWorthReporting(string markdown) =>
        EmptyRequiredSections(markdown)
            .Where(section => !SectionsThatOnlyFillWhenSomethingHappens
                .Contains(section, StringComparer.Ordinal))
            .ToList();

    /// <summary>
    /// The required sections still empty in this notebook.
    /// <para>
    /// Measured live on 2026-09-16: the canonical "Current goals and plan" and "Discarded" sat at
    /// "_nothing yet_" at the close of an evening whose content the model had written into
    /// sections of its own — a presentation defect, not an understanding one. This measures it;
    /// nothing parses the model's prose to fix it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> EmptyRequiredSections(string markdown)
    {
        var text = (markdown ?? string.Empty).Replace("\r\n", "\n");
        var empty = new List<string>();
        foreach (var section in RequiredSections)
        {
            var heading = Heading(section);
            var start = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                empty.Add(section);
                continue;
            }

            var after = start + heading.Length;
            var next = text.IndexOf("\n## ", after, StringComparison.Ordinal);
            var body = (next < 0 ? text[after..] : text[after..next]).Trim();
            if (body.Length == 0 || body.Contains("(nothing yet)", StringComparison.OrdinalIgnoreCase))
            {
                empty.Add(section);
            }
        }

        return empty;
    }

    /// <summary>Does this player line read like a correction of the companion's notes?</summary>
    public static bool LooksLikeCorrection(string playerLine)
    {
        var lowered = playerLine.ToLowerInvariant();
        return CorrectionMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Approximate token count for budget checks.</summary>
    public static int EstimateTokens(string text) => TokenEstimate.From(text);

    /// <summary>Is the notebook over its budget? The companion is asked to prune.</summary>
    public bool OverBudget(string markdown) => EstimateTokens(markdown) > TokenBudget;

    /// <summary>The skeleton handed to a brand new notebook.</summary>
    public static string Template(string gameStem)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Notebook — {gameStem}");
        builder.AppendLine();
        builder.AppendLine("<!-- The companion's long-term memory for this game. It is a working document,");
        builder.AppendLine("     not a log: keep it under a few thousand tokens and prune ruthlessly.");
        builder.AppendLine("     The player may read this and correct it — their corrections win. -->");
        builder.AppendLine();
        foreach (var section in RequiredSections)
        {
            builder.AppendLine(Heading(section));
            builder.AppendLine();
            builder.AppendLine("_(nothing yet)_");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Add any missing section headings, so the model cannot lose a section by
    /// forgetting to write it.
    /// </summary>
    public static string EnsureSections(string markdown)
    {
        var text = markdown ?? string.Empty;
        var missing = RequiredSections
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

    private static string Heading(string section) => "## " + section;

    /// <summary>
    /// Keep the first copy of every heading and drop the rest, bodies included. A rewrite
    /// that repeats a section — measured live on 2026-09-16, an adventure notebook with
    /// "## Map notes" written twice — is a slip of the pen, not a request for two
    /// sections, and `/notes` should never show the same heading twice.
    /// </summary>
    private static string DropDuplicateSections(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var dropping = false;

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                if (seen.Add(line))
                {
                    dropping = false;
                }
                else
                {
                    dropping = true;
                    continue;
                }
            }

            if (!dropping)
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Write a rewrite the model sent as a notebook block, unless it is a truncation of
    /// one: a notebook that lost its tail replaces nothing, because the old one still has
    /// every section and the player's corrections in it (issue #18).
    /// </summary>
    /// <returns>True when the notebook was written.</returns>
    public bool TryWriteRewrite(string markdown, bool truncated)
    {
        if (truncated && !HasEverySection(markdown))
        {
            return false;
        }

        Write(markdown);
        return true;
    }

    /// <summary>
    /// Does this markdown already carry every section heading? A rewrite that does not was
    /// cut off mid-write, and writing it would replace a whole notebook with a fragment
    /// (issue #18).
    /// </summary>
    public static bool HasEverySection(string markdown)
    {
        var text = markdown ?? string.Empty;
        return RequiredSections.All(section =>
            text.Contains(Heading(section), StringComparison.OrdinalIgnoreCase));
    }

    private static string CorrectionsHeading => Heading("Player corrections");

    /// <summary>The bullet lines belonging to a section.</summary>
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
            return markdown + "\n" + heading + "\n\n" + body + "\n";
        }

        var afterHeading = start + heading.Length;
        var nextSection = markdown.IndexOf("\n## ", afterHeading, StringComparison.Ordinal);
        var tail = nextSection < 0 ? string.Empty : markdown[nextSection..];
        return markdown[..afterHeading] + "\n\n" + body + "\n" + tail;
    }
}
