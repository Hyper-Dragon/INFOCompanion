namespace Agent.Core.Loop;

/// <summary>
/// A game's own sidecar: what came with the game.
/// <para>
/// Optional, and the whole of it is one file. A story called <c>witness.z5</c> may have a
/// <c>witness.md</c> sitting next to it, holding whatever a person would have had in their hands
/// before sitting down at the keyboard: the manual, the map, the letter, the feelies, the notes on
/// the packaging — and, alongside those, the odd piece of ancillary knowledge about how this
/// particular game behaves (*"moving is dangerous here; think before you walk"*). Text transcribed
/// from the original release belongs here as much as anything an operator writes down, and that is
/// rather the point: in 1983 the box was part of the game, and one of the two people at the
/// keyboard has read it.
/// </para>
/// <para>
/// <b>It is immutable.</b> It belongs to the game, it ships with the game, and nothing in this
/// program ever writes, moves or repairs it — the same rule as the story file itself, and the
/// directory it lives in is mounted read-only to say so. The companion's own memory of a game is
/// a different file in a different place (<c>&lt;state&gt;/notebook/&lt;stem&gt;.md</c>, which it
/// rewrites constantly); this one is read-only input.
/// </para>
/// <para>
/// <b>It is what came in the box, not a solution sheet.</b> The distinction is the operator's to
/// keep and the prompt states it. A map or a manual covers a good deal of the same ground as a
/// walkthrough, and only one of them was ever in the box. The companion is also told where this
/// came from, so it can never present somebody else's note as its own discovery — the honesty rule
/// is about acquired facts, and being handed a fact is still acquiring it.
/// </para>
/// <para>
/// <b>And most games have none, which is what the stand-in is for.</b> A game whose box is long
/// gone, or was never ours, would otherwise be silent — and silence reads to a companion as "there
/// is nothing to know" rather than "nobody here has looked". <see cref="DefaultFileName"/> is read
/// in its place: it says so, and it turns the absence into an act, which is to ask the one person
/// at the keyboard who might still have the box. Because that text is ours rather than the game's,
/// the prompt labels it as ours; see <see cref="ContextAssembler.GameBriefBlock"/>.
/// </para>
/// </summary>
public static class GameBrief
{
    /// <summary>
    /// How much of a sidecar goes in front of the model by default.
    /// <para>
    /// Generous on purpose. The box was part of the game, and a manual, a map and a set of
    /// feelies transcribed in full is exactly the material this mechanism exists to carry;
    /// losing half a manual to a small arithmetic bound is the failure this file must not
    /// have. What remains is a safety valve for a pathological file — and when it does bite,
    /// the cut is labelled in the text the model is given rather than left to look complete.
    /// </para>
    /// <para>
    /// Overridable per deployment with <c>game_brief_max_chars</c> (see
    /// <see cref="Agent.Core.Config.AgentConfig"/>): what fits is a fact about the model and
    /// the budget, not about this code. Rebuilds are not needed — the config is bind-mounted.
    /// </para>
    /// </summary>
    public const int DefaultMaxCharacters = 32000;

    /// <summary>The file name a game's sidecar must have.</summary>
    public static string FileNameFor(string stem) => stem + ".md";

    /// <summary>
    /// The file that stands in for a game's own sidecar when the game has none.
    /// <para>
    /// The leading underscore is the convention for the file that belongs to no particular game,
    /// the same one <c>&lt;state&gt;/notebook/_companion.md</c> uses — and no story file is named
    /// this way, so the two namespaces cannot collide.
    /// </para>
    /// </summary>
    public const string DefaultFileName = "_default.md";

    /// <summary>
    /// Read the standing note that stands in for the paperwork of a game that shipped with none.
    /// </summary>
    /// <param name="gamesDirectory">Where the story files live. Empty means nowhere to look.</param>
    /// <returns>
    /// The text, or null when there is no stand-in either, plus a <em>problem</em> string when one
    /// was found and could not be read — reported on the same terms as a game's own sidecar, and
    /// for the same reason: an operator who edits this file and gets the permissions wrong must
    /// not be left with silence.
    /// <para>
    /// Nothing here decides when this is used. Falling back to it is the loop's call, not this
    /// class's, so that "the game has no sidecar of its own" stays a fact <see cref="Read"/> can
    /// state honestly while the fallback is visible in one place.
    /// </para>
    /// </returns>
    public static (string? Text, string? Problem) ReadDefault(
        string? gamesDirectory,
        int maxCharacters = DefaultMaxCharacters)
    {
        if (string.IsNullOrWhiteSpace(gamesDirectory))
        {
            return (null, null);
        }

        return ReadFile(Path.Combine(gamesDirectory.Trim(), DefaultFileName), maxCharacters);
    }

    /// <summary>
    /// Read the sidecar for one game, if there is one.
    /// </summary>
    /// <param name="gamesDirectory">Where the story files live. Empty means no sidecars.</param>
    /// <param name="stem">The game's stem, the same one the notebook is keyed on.</param>
    /// <returns>
    /// The text, or null when there is no sidecar or it is empty, plus a <em>problem</em> string
    /// when one was found and could not be read.
    /// <para>
    /// The problem is reported separately rather than folded into "no sidecar" on purpose: an
    /// operator who has written instructions and typo'd the permissions gets silence otherwise,
    /// and silence is exactly the failure this codebase keeps having to remove. A missing file is
    /// not a problem — it is the normal case.
    /// </para>
    /// </returns>
    public static (string? Text, string? Problem) Read(
        string? gamesDirectory,
        string stem,
        int maxCharacters = DefaultMaxCharacters)
    {
        if (string.IsNullOrWhiteSpace(gamesDirectory) || string.IsNullOrWhiteSpace(stem))
        {
            return (null, null);
        }

        var path = Path.Combine(gamesDirectory.Trim(), FileNameFor(stem.Trim()));
        return ReadFile(path, maxCharacters);
    }

    /// <summary>
    /// Read one brief file: the text, or null when there is nothing usable there.
    /// <para>
    /// One reader for both files, because the rules that matter are about the <em>text</em> rather
    /// than about which file it came from: whitespace-only means absent, an unreadable file is a
    /// problem rather than an absence, and a cut is always labelled.
    /// </para>
    /// </summary>
    private static (string? Text, string? Problem) ReadFile(string path, int maxCharacters)
    {
        if (!File.Exists(path))
        {
            return (null, null);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{path} exists but could not be read: {ex.Message}");
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return (null, null);
        }

        if (maxCharacters > 0 && text.Length > maxCharacters)
        {
            text = text[..maxCharacters]
                + "\n\n_(the rest of this file was too long to include — ask, and it can be read "
                + "in full)_";
        }

        return (text, null);
    }
}
