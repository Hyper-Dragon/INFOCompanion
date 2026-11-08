namespace Agent.Notebook;

/// <summary>
/// Two things read off a turn's own text, for the block that says where a reply stands
/// (05_memory.md §2 (`M9.2`)).
/// <para>
/// Both are readings of what was **typed or printed**, never of what it means. The room's name is a
/// line the story printed; the last thing examined is a verb and its object out of the player's own
/// command. Neither is a claim about the story (`G31`), and neither is an identity — the
/// interpreter's own room number is the identity (`G23`), and a name is something to read.
/// </para>
/// <para>
/// They live here rather than in the catch-up report because two callers now need the same rule:
/// the report labels its diagram with them, and the loop puts them in every reply. A second copy of
/// "which line is a name" is a second answer to a question that has one.
/// </para>
/// </summary>
public static class TurnReading
{
    /// <summary>
    /// The room's name as the story printed it: the first line of the turn that is a name rather
    /// than a sentence.
    /// <para>
    /// Two other rules were tried against the live record first and both are wrong. The <em>first
    /// line</em> is wrong because a move turn often opens with the old room's parting words, so a
    /// place came out called "The poodle yaps a threat as you back away." The <em>shortest line</em>
    /// is wrong because the shortest thing on the screen is usually a quip or a prompt rather than a
    /// title: on Wishbringer's record it named rooms "What next?", "postcards." and "You can't go
    /// that way."
    /// </para>
    /// <para>
    /// Most turns print no name at all — a turn that did not move still prints something, and the
    /// title of a room is drawn on the status line rather than into the text
    /// (02_game_boundary.md §4.3 (`G23`)). So this arrives rarely, the answer when nothing qualifies
    /// is empty rather than a guess, and a caller carries the last one forward rather than treating
    /// an empty answer as news.
    /// </para>
    /// </summary>
    public static string RoomName(string output) =>
        output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length is > 0 and <= 40 && !IsASentence(line)) ?? string.Empty;

    /// <summary>
    /// What the player last examined, out of their own command: the noun after a look verb.
    /// <para>
    /// A verb table over the <em>command</em>, which is the one kind of phrase list this harness is
    /// allowed (02_game_boundary.md §8 (`G45`)): it matches what a person typed rather than reading
    /// the story's prose, and the model is free to ignore it. Empty means the command was not an
    /// examination, so a caller carries the last one forward.
    /// </para>
    /// </summary>
    public static string LastExamined(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var words = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return string.Empty;
        }

        var verb = words[0];
        var rest = words[1..];

        if (verb.Equals("look", StringComparison.OrdinalIgnoreCase))
        {
            // "look at the desk" examines the desk; a bare "look" examines nothing.
            if (!rest[0].Equals("at", StringComparison.OrdinalIgnoreCase) || rest.Length < 2)
            {
                return string.Empty;
            }

            rest = rest[1..];
        }
        else if (!ExaminationVerbs.Contains(verb))
        {
            return string.Empty;
        }

        return string.Join(' ', rest);
    }

    /// <summary>The verbs that mean *I looked at this thing*.</summary>
    private static readonly HashSet<string> ExaminationVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "examine",
        "x",
        "inspect",
        "search",
        "feel",
        "touch",
    };

    /// <summary>
    /// Whether a line is prose rather than a title, which is the whole of the test: a sentence ends
    /// in a mark, and a name does not.
    /// <para>
    /// A third shape turned up on the live record on 2026-09-29, reading an evening of Moonmist with
    /// an eye on what the world document would call each room: `<c>(You are now in the courtyard.)</c>`
    /// — the story's own echo of a move, wrapped in brackets. It is a remark rather than a sign, and
    /// every one of that game's numbered rooms took one as its name, which would have put a sentence
    /// where the companion is told a room's name goes. A line that opens a bracket is an aside.
    /// </para>
    /// <para>
    /// And so is the half of one that arrives on a later line. Measured 2026-10-01 on Wishbringer: the
    /// world document held a place called <c>as you go up.)</c>, because the aside was too long for the
    /// length rule on its first line and its *tail* was short enough to pass — a fragment of a sentence
    /// that had already been rejected, presented as a name. A closing bracket is the same evidence as an
    /// opening one, wherever on the line it falls.
    /// </para>
    /// </summary>
    private static bool IsASentence(string line) =>
        line.StartsWith('(') || line.StartsWith('[')
        || line.EndsWith('.') || line.EndsWith('!') || line.EndsWith('?')
        || line.EndsWith(':') || line.EndsWith(';') || line.EndsWith('"')
        || line.EndsWith(')') || line.EndsWith(']');
}
