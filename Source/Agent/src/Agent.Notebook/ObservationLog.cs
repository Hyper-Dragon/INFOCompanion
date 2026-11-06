using System.Text;
using System.Text.Json;

namespace Agent.Notebook;

/// <summary>
/// The turns the core watched, as the companion should read them (issue #22).
/// <para>
/// The core writes down every turn — the line that was typed and the text that came back —
/// and interprets none of it. This reads that file back as a short block the model can reason
/// over, and it interprets nothing either: the fingerprints keep their readable prefixes and
/// two that look alike stay two entries, because deciding whether they are the same place is
/// exactly the judgement that belongs to the model.
/// </para>
/// <para>
/// It exists to cover one specific gap: the transcript window is measured in tens of turns,
/// and a long game outlives it. It is <b>not a map</b>, and must never be read as one — the
/// records it replaces were worse than nothing because they stated a topology the core could
/// not know.
/// </para>
/// </summary>
public static class ObservationLog
{
    /// <summary>How many recent turns to put in front of the model.</summary>
    public const int DefaultLimit = 60;

    /// <summary>
    /// How much of one turn's output to quote.
    /// <para>
    /// The value in force is <c>observation_quote_limit</c> in <c>agent-config.json</c>; this is the
    /// shipped default and what a caller that does not say gets.
    /// </para>
    /// </summary>
    public const int QuoteLimit = 300;

    /// <summary><c>&lt;state&gt;/observations/&lt;stem&gt;.jsonl</c>.</summary>
    public static string PathFor(string stateDirectory, string gameStem) =>
        Path.Combine(stateDirectory, "observations", gameStem + ".jsonl");

    /// <summary>One turn as the record has it, for the harness to render rather than to read.</summary>
    /// <param name="Turn">The game's own turn number, as the core counted it.</param>
    /// <param name="Input">What was typed, exactly.</param>
    /// <param name="Output">What the screen said back, exactly.</param>
    /// <param name="Fingerprint">The room as the story printed it, plus the core's short hash.</param>
    /// <param name="Location">
    /// Where the interpreter said the player was, as its own object number for the room (B4), or
    /// <c>null</c> when it cannot say — an interpreter without the tool channel, or a story that
    /// keeps no status line.
    /// <para>
    /// The one place identity the record has. The fingerprint is an <em>index</em> for finding
    /// similar turns and never an identity: the same room prints differently between visits, and a
    /// turn that did not move prints something too — the first attempt at a map keyed by fingerprint
    /// made twenty-two places out of a handful of rooms, with `Taken.` among them.
    /// </para>
    /// </param>
    public sealed record RecordedTurn(
        int Turn,
        string Input,
        string Output,
        string Fingerprint,
        long? Location = null,
        long Run = 0,
        long Generation = 0);

    /// <summary>
    /// Every turn on the record for this game, oldest first, as data (B5).
    /// <para>
    /// The sibling of <see cref="Recent"/>, and the difference is who is reading. That one is prose
    /// written <em>for a model</em>; this one is the rows themselves, written for the page, which
    /// shows the player the turns they played instead of the model's retelling of them. Nothing is
    /// interpreted here either — a row that will not parse is dropped, because a half-written line
    /// from a crash is not a turn that happened.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RecordedTurn> All(string stateDirectory, string gameStem)
    {
        if (string.IsNullOrWhiteSpace(gameStem))
        {
            return [];
        }

        string[] lines;
        try
        {
            if (!File.Exists(PathFor(stateDirectory, gameStem)))
            {
                return [];
            }

            lines = File.ReadAllLines(PathFor(stateDirectory, gameStem));
        }
        catch (IOException)
        {
            return [];
        }

        var turns = new List<RecordedTurn>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var record = JsonDocument.Parse(line);
                var row = record.RootElement;
                turns.Add(new RecordedTurn(
                    Field(row, "turn")?.GetInt32() ?? 0,
                    Field(row, "input")?.GetString() ?? string.Empty,
                    Field(row, "output")?.GetString() ?? string.Empty,
                    Field(row, "fingerprint")?.GetString() ?? string.Empty,
                    Field(row, "location") is { ValueKind: JsonValueKind.Number } location
                        ? location.GetInt64()
                        : null,
                    Field(row, "run") is { ValueKind: JsonValueKind.Number } run ? run.GetInt64() : 0,
                    Field(row, "generation") is { ValueKind: JsonValueKind.Number } generation
                        ? generation.GetInt64()
                        : 0));
            }
            catch (JsonException)
            {
            }
        }

        return turns;
    }

    /// <summary>One field, whatever case the writer used for it.</summary>
    private static JsonElement? Field(JsonElement row, string name)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// The most recent turns, oldest first, or <c>null</c> when there are none.
    /// <para>
    /// <paramref name="run"/> and <paramref name="generation"/> are which attempt and which fork of
    /// the world the caller is playing in. The file mixes every attempt at the game, and a turn from
    /// an earlier one is labelled as such rather than quoted as though it were this line's history
    /// (05_memory.md §2 (`M9`–`M11`)). Leave them at zero when there is nothing to compare against, and nothing is
    /// labelled.
    /// </para>
    /// </summary>
    public static string? Recent(
        string stateDirectory,
        string gameStem,
        int limit = DefaultLimit,
        int quoteChars = QuoteLimit,
        long run = 0,
        long generation = 0)
    {
        if (string.IsNullOrWhiteSpace(gameStem))
        {
            return null;
        }

        var path = PathFor(stateDirectory, gameStem);
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            // A record we cannot read is worth less than a companion that keeps playing.
            return null;
        }

        var turns = new List<string>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var record = JsonDocument.Parse(line);
                turns.Add(TurnLine(record.RootElement, run, generation, quoteChars));
            }
            catch (JsonException)
            {
                // A half-written line from a crash. It was never written down in full, so as
                // far as this record goes it is not a turn that happened.
            }
        }

        if (turns.Count == 0)
        {
            return null;
        }

        var kept = turns.Count > limit ? turns.GetRange(turns.Count - limit, limit) : turns;

        var text = new StringBuilder();
        text.AppendLine(
            "The core watched every turn, whatever was typed, and wrote down what was on the "
            + "screen. These are the most recent. A fingerprint is the name as the story "
            + "printed it plus a short hash of the first paragraph — an index for finding a "
            + "turn again, not a claim about where anything is. Two that read alike are still "
            + "two entries: deciding what they mean is your job, not the core's:");
        if (turns.Count > kept.Count)
        {
            text.AppendLine($"(the {kept.Count} most recent of {turns.Count})");
        }
        if (run > 0)
        {
            text.AppendLine(AttemptNote(run, generation));
        }

        foreach (var turn in kept)
        {
            text.AppendLine(turn);
        }

        text.Append(
            "A turn with no command is the story speaking on its own. A turn with no output is "
            + "a command the game answered with nothing at all — which is not the same as one "
            + "that did nothing, and not the same as one that failed.");

        return text.ToString();
    }

    /// <summary>How many matching turns a recall hands back by default.</summary>
    public const int SearchLimit = 10;

    /// <summary>
    /// The turns after <paramref name="afterTurn"/> in the attempt in hand, oldest first, or
    /// <c>null</c> when there are none (04_companion.md §10 (`C56`–`C60`)).
    /// <para>
    /// This is what the companion reads when the model has been unreachable and the game went on
    /// without it: the window it missed, from the beginning of that window, so it can bring its
    /// notes up to date instead of guessing at what happened. It is not a search — nothing is being
    /// looked for — and it is not the recent block either: that one is the newest turns whatever they
    /// are, where this one starts at a known place and reads forward.
    /// </para>
    /// <para>
    /// Turns from another attempt are left out, because a restart numbers turns from one again and a
    /// window measured by number would otherwise pick up the whole of the previous attempt. Rows
    /// written before the stamps existed are kept — they are the same game and probably the same
    /// attempt — and they label themselves as unrecorded (05_memory.md §2 (`M9`–`M11`)).
    /// </para>
    /// </summary>
    public static string? Since(
        string stateDirectory,
        string gameStem,
        long afterTurn,
        int limit,
        int quoteChars = QuoteLimit,
        long run = 0,
        long generation = 0)
    {
        if (string.IsNullOrWhiteSpace(gameStem) || limit <= 0)
        {
            return null;
        }

        var path = PathFor(stateDirectory, gameStem);
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }

        var turns = new List<string>();
        var seen = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                var turn = root.TryGetProperty("turn", out var t) ? t.GetInt64() : 0;
                if (turn <= afterTurn)
                {
                    continue;
                }

                var theirs = root.TryGetProperty("run", out var r) ? r.GetInt64() : 0;
                if (run > 0 && theirs != 0 && theirs != run)
                {
                    // Another attempt's turn: not this window, however the numbers read.
                    continue;
                }

                seen++;
                if (turns.Count < limit)
                {
                    turns.Add(TurnLine(root, run, generation, quoteChars));
                }
            }
            catch (JsonException)
            {
                // A half-written line from a crash; it was never a turn.
            }
        }

        if (turns.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(
            $"The core watched every turn while you were not there, and wrote down what was on the"
            + $" screen. These are the {turns.Count} after turn {afterTurn}, oldest first — the window"
            + " you missed. A fingerprint is an index for finding a turn again, not a claim about where"
            + " anything is:");
        if (seen > turns.Count)
        {
            text.AppendLine($"(the first {turns.Count} of {seen} you missed)");
        }
        if (run > 0)
        {
            text.AppendLine(AttemptNote(run, generation));
        }

        foreach (var turn in turns)
        {
            text.AppendLine(turn);
        }

        text.Append(
            "A turn with no command is the story speaking on its own. A turn with no output is a "
            + "command the game answered with nothing at all — which is not the same as one that did "
            + "nothing, and not the same as one that failed.");
        return text.ToString();
    }
    /// <summary>How much of one hit's output is quoted by default.</summary>
    public const int SearchQuoteLimit = 240;

    /// <summary>
    /// Turns in which the query appears — at most <paramref name="limit"/> of them, newest
    /// last (05_memory.md §2 (`M9`–`M11`)). A plain substring match against what was typed and what was
    /// shown, case-insensitive: no interpretation, and no ranking beyond recency, because
    /// deciding what a match means is the model's job and not the core's. How many hits and
    /// how much of each are the caller's to choose; the defaults here are the deployment's
    /// starting point, not a rule.
    /// <para>
    /// <paramref name="run"/> and <paramref name="generation"/> are which attempt and which fork of
    /// the world is being played now. A hit from an earlier attempt is **labelled** rather than
    /// silently cited, and a hit from before a `restore` forked the world says so too: knowledge
    /// survives a fork — that is why a playbook works at all — but where the companion *stood* in a
    /// branch that was reloaded away is not where it stands now (05_memory.md §2 (`M9`–`M11`)).
    /// Measured before this existed: the same `look` came back labelled both turn 1 and turn 13, and
    /// the companion noticed and said so out loud.
    /// </para>
    /// </summary>
    public static string? Search(
        string stateDirectory,
        string gameStem,
        string query,
        int limit = SearchLimit,
        int quoteChars = SearchQuoteLimit,
        long run = 0,
        long generation = 0)
    {
        var matches = Matches(stateDirectory, gameStem, query, limit, run, generation);
        if (matches is null)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(
            $"The record, searched for \"{query}\": {matches.Count} "
            + (matches.Count == 1 ? "turn mentions it" : "turns mention it")
            + ". Each entry is what was typed and what the screen said back.");
        if (run > 0)
        {
            text.AppendLine(AttemptNote(run, generation));
        }
        foreach (var match in matches)
        {
            text.AppendLine(
                $"  turn {match.Turn}{Attribution(match.Run, match.Generation, run, generation)}"
                + (match.Location is { } at ? $", in room {at}" : string.Empty)
                + ": > "
                + Flatten(match.Input, 120)
                + "\n      \"" + Flatten(match.Output, quoteChars) + "\"");
        }

        text.Append("If what you were after is not here, say what you remember and search for another word.");
        return text.ToString();
    }

    /// <summary>
    /// The same search, as facts (04_companion.md §2 (`C6`–`C13`)).
    /// <para>
    /// The companion has to be exact about *which turn* something was on: a citation of turn 12 in
    /// the wrong attempt, or in a branch that was reloaded away, is a confident answer about a world
    /// that is not on the screen. Reading that out of prose is the step this removes — the sentence
    /// and these facts are now rendered from one read, so they cannot disagree.
    /// </para>
    /// <para>
    /// <c>null</c> means the record has nothing matching, which is an answer rather than an error;
    /// an empty list never comes back.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ObservationMatch>? Matches(
        string stateDirectory,
        string gameStem,
        string query,
        int limit = SearchLimit,
        long run = 0,
        long generation = 0)
    {
        if (string.IsNullOrWhiteSpace(gameStem) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var path = PathFor(stateDirectory, gameStem);
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }

        var hits = new List<ObservationMatch>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                var input = root.TryGetProperty("input", out var i) ? i.GetString() : null;
                var output = root.TryGetProperty("output", out var o) ? o.GetString() : null;

                var inInput = input?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                var inOutput = output?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                if (!inInput && !inOutput)
                {
                    continue;
                }

                hits.Add(
                    new ObservationMatch(
                        root.TryGetProperty("turn", out var t) ? t.GetInt64() : 0,
                        root.TryGetProperty("run", out var r) ? r.GetInt64() : 0,
                        root.TryGetProperty("generation", out var g) ? g.GetInt64() : 0,
                        input ?? string.Empty,
                        output ?? string.Empty,
                        root.TryGetProperty("fingerprint", out var f) ? f.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Number
                            ? loc.GetInt64()
                            : null));
            }
            catch (JsonException)
            {
                // A half-written line from a crash; it was never a turn.
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        return hits.Count > limit ? hits.GetRange(hits.Count - limit, limit) : hits;
    }

    /// <summary>
    /// One turn, as a line of a block: its number, where it came from, what was typed and the first
    /// <paramref name="quoteChars"/> characters of what came back. Shared by the recent block and
    /// the window a catch-up reads, so the two cannot describe the same turn differently.
    /// </summary>
    private static string TurnLine(JsonElement root, long run, long generation, int quoteChars)
    {
        var turn = root.TryGetProperty("turn", out var t) ? t.GetInt64() : 0;
        var input = root.TryGetProperty("input", out var i) ? i.GetString() : null;
        var output = root.TryGetProperty("output", out var o) ? o.GetString() : null;
        var fingerprint = root.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;

        var said = Flatten(output, quoteChars);
        return $"  turn {turn}{Attribution(root, run, generation)}: {Describe(input)}  "
            + $"[{Describe(fingerprint)}]"
            + (said.Length == 0 ? " — the game printed nothing" : $"\n      \"{said}\"");
    }

    /// <summary>What the story printed, for the report line.</summary>
    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();

    /// <summary>
    /// One sentence about where this record now stands, for the head of a block: which attempt, and
    /// which fork of the world inside it. Written once because it applies to the whole block, while
    /// each entry that is from somewhere else says so in its own line.
    /// </summary>
    private static string AttemptNote(long run, long generation)
    {
        var note =
            $"The record holds every attempt at this game and every fork of it, and this is attempt "
            + $"{run}{(generation > 0 ? $", generation {generation}" : string.Empty)}. "
            + "An entry that says it is from somewhere else is not this line's history: what you "
            + "know about the game travels between attempts and across a reload, where you were "
            + "does not.";
        return note;
    }

    /// <summary>
    /// " — attempt 2, an earlier attempt", and the like, for one entry; empty when the entry is from
    /// where the caller is standing.
    /// <para>
    /// Four ways a citation can be honest about itself, and the two "does not say" cases are not
    /// decoration: the records on disk predate both stamps, so silence about which attempt or which
    /// branch a turn came from would read as a claim that it is this one's. Zero in the record means
    /// the record does not know — a fact about the record, never a claim about a run.
    /// </para>
    /// </summary>
    private static string Attribution(JsonElement record, long run, long generation)
    {
        return Attribution(
            record.TryGetProperty("run", out var r) ? r.GetInt64() : 0,
            record.TryGetProperty("generation", out var g) ? g.GetInt64() : 0,
            run,
            generation);
    }

    /// <summary>
    /// Where a turn sits relative to the attempt being played, as a phrase or nothing at all.
    /// <para>
    /// Takes the numbers rather than the JSON so that the prose and the facts block are rendered
    /// from the same read (04_companion.md §2 (`C6`–`C13`)): two paths into this much careful wording is two chances to
    /// describe the same turn differently.
    /// </para>
    /// </summary>
    private static string Attribution(long theirs, long theirFork, long run, long generation)
    {
        if (run <= 0)
        {
            // The caller does not know which attempt it is playing, so there is nothing to compare
            // against and nothing to say. Saying "not this one" would be the guess this whole
            // change exists to remove.
            return string.Empty;
        }

        if (theirs == 0)
        {
            return " (an unrecorded attempt — from before the core stamped runs, so not this one)";
        }

        if (theirs != run)
        {
            return $" (attempt {theirs}, an earlier attempt at this game — not this one)";
        }

        // The same attempt. Which branch of it, though, is the other half of the question, and it
        // only has an answer when this side knows its own.
        if (generation <= 0)
        {
            return string.Empty;
        }

        if (theirFork == 0)
        {
            return " (this attempt; the record does not say which branch of it)";
        }

        if (theirFork != generation)
        {
            return $" (earlier in this attempt, before the world was reloaded for generation {generation})";
        }

        return string.Empty;
    }

    /// <summary>
/// One turn the record matched, as a fact rather than a line of prose (04_companion.md §2 (`C6`–`C13`)).
/// <para>
/// The attempt and the fork travel with the turn because a citation without them is the failure this
/// whole area was rebuilt for: *"the same look… labelled both turn 1 and turn 13"*, and a note about
/// a coin that did not exist yet because the world had been rewound underneath it. The names here are
/// the ones the facts block carries, so the shape is part of the tool surface.
/// </para>
/// </summary>
/// <param name="Turn">The game's own turn number.</param>
/// <param name="Run">Which attempt at this game the turn belongs to; 0 predates the field.</param>
/// <param name="Generation">Which fork of the world inside that attempt; 0 predates the field.</param>
/// <param name="Input">What was typed, or empty when the story spoke by itself.</param>
/// <param name="Output">What the screen said back, in full.</param>
/// <param name="Fingerprint">The turn's index into the record — a label, never an identity.</param>
/// <param name="Location">
/// Where the interpreter said the player was, as its own object number for the room (`G23`), or
/// <c>null</c> when it could not say.
/// <para>
/// It travels with the turn for the same reason the attempt and the fork do, and it was the one
/// that was missing. **A direction is only true of the room it was typed in**: on the 2026-09-29
/// record <c>east</c> left the gallery for the bedroom and left the first-floor corridor for the
/// office, and a citation without the room cannot tell the two apart.
/// </para>
/// </param>
public readonly record struct ObservationMatch(
    long Turn,
    long Run,
    long Generation,
    string Input,
    string Output,
    string Fingerprint,
    long? Location = null);

/// <summary>One line, at most <paramref name="limit"/> characters.</summary>
    private static string Flatten(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= limit ? flat : flat[..(limit - 1)] + "…";
    }
}
