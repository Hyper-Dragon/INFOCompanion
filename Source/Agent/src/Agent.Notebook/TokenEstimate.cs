namespace Agent.Notebook;

/// <summary>
/// The one guess about tokenisation this project makes, in one place.
/// <para>
/// There is no tokeniser here on purpose: the real one is the provider's, it counts differently for
/// every model, and shipping a copy of it to decide whether to trim a notebook would be a large
/// dependency for a number used to choose between "send it" and "trim it to whole lines".
/// </para>
/// <para>
/// It is a single type because it used to be three copies of the same constant — in the notebook's
/// budget check, the memory's, and the context assembler's — which is how three readers of the same
/// file end up disagreeing about how big it is. Changing the number here changes it for all three,
/// which is the only way they can be expected to agree.
/// </para>
/// </summary>
public static class TokenEstimate
{
    /// <summary>Rough characters-per-token ratio (no tokeniser here).</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>An estimate of a text's tokens, rounded up. Null and empty are no tokens.</summary>
    public static int From(string? text) =>
        (int)Math.Ceiling((text?.Length ?? 0) / CharsPerToken);
}
