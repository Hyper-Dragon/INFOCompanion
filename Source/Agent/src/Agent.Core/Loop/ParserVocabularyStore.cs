using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Core.Loop;

/// <summary>
/// One thing the companion typed, and what the game answered with (§10.4).
/// <para>
/// This is the unit of what the harness remembers about a parser, and it replaced a bare word and
/// a refused flag on 2026-09-21 because a verdict is the one thing the harness cannot honestly
/// make. The word is a fact about the parser's dictionary; everything else is a fact about one
/// attempt at one wording.
/// </para>
/// </summary>
/// <param name="Typed">The line the companion sent.</param>
/// <param name="GameSaid">The sentence it got back, in the game's own words.</param>
/// <param name="WordUnknown">
/// True when the parser has no such word. The only part of a refusal that is a verdict rather than
/// history, and therefore the only part worth steering the model away from anything with.
/// </param>
public readonly record struct ParserAttempt(string Typed, string GameSaid, bool WordUnknown);

/// <summary>
/// What this story's parser said back to what was typed, kept between sessions (§10.4 help text).
/// <para>
/// "Help text for the model, never a gate" is unchanged, and this file is now stricter about what
/// it claims than it used to be. It held, until 2026-09-21, the same two sets
/// <see cref="ParserFeedback"/> builds in a session — a word and whether it had been refused —
/// which meant a sentence about the object ("You can't see any letter here!") was written down as
/// a verdict on the verb. Those verdicts were replayed at every load, so five perfectly good verbs
/// were taken out of the companion's hands for the rest of an evening and would have been for
/// every evening after it. What is kept now is the attempt: <em>this was typed, and this came
/// back</em>, which is history the model can reason from rather than a rule it must not
/// contradict.
/// </para>
/// <para>
/// Append-only JSON lines, one record per attempt:
/// <c>{"typed":…,"game_said":…,"word_unknown":…,"at":…}</c>. Reading tolerates anything — a
/// corrupt line is skipped, a missing file is an empty memory, and a line with no <c>typed</c>
/// field is skipped too, which is what retires the old word-verdict shape without a migration.
/// Neither case is ever an error the companion has to hear about.
/// </para>
/// </summary>
public sealed class ParserVocabularyStore
{
    private readonly string _directory;

    public ParserVocabularyStore(string stateDirectory)
    {
        _directory = Path.Combine(stateDirectory, "vocabulary");
    }

    /// <summary>Where this game's words live. Always inside the agent's own state directory.</summary>
    public string PathFor(string stem) => Path.Combine(_directory, stem + ".jsonl");

    /// <summary>
    /// Every attempt remembered for this game, oldest first. Never throws: an unreadable memory is
    /// an empty one.
    /// </summary>
    public IReadOnlyList<ParserAttempt> Read(string stem)
    {
        var path = PathFor(stem);
        var attempts = new List<ParserAttempt>();
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                try
                {
                    var record = JsonNode.Parse(trimmed) as JsonObject;
                    var typed = record?["typed"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrEmpty(typed))
                    {
                        continue;
                    }

                    attempts.Add(new ParserAttempt(
                        typed,
                        record?["game_said"]?.GetValue<string>() ?? string.Empty,
                        record?["word_unknown"]?.GetValue<bool>() ?? false));
                }
                catch (Exception)
                {
                    // One bad line is one lost attempt, not a lost memory.
                }
            }
        }
        catch (Exception)
        {
            return [];
        }

        return attempts;
    }

    /// <summary>
    /// Append one attempt. Best-effort by contract: an attempt that cannot be written costs a hint
    /// next time, and must never cost a turn now.
    /// </summary>
    public void Append(string stem, ParserAttempt attempt)
    {
        Directory.CreateDirectory(_directory);
        var record = JsonSerializer.Serialize(new
        {
            typed = attempt.Typed,
            game_said = attempt.GameSaid,
            word_unknown = attempt.WordUnknown,
            at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        });
        File.AppendAllText(PathFor(stem), record + "\n");
    }
}
