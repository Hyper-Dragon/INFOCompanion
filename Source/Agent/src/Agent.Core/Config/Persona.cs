namespace Agent.Core.Config;

/// <summary>
/// A personality file, split into its metadata and its body (04_companion.md §11 (`C61`–`C67`)).
/// <para>
/// The block fenced with <c>---</c> at the top is front matter — the convention every tool that
/// reads a markdown file with metadata already understands, and the right home for a name, because
/// a name is metadata *about* the document rather than the first line of it:
/// </para>
/// <code>
/// ---
/// name: James
/// ---
/// </code>
/// <para>
/// Both halves of this are deliberate. The name is read so that a personality owns its own name
/// and nobody has to remember to set an environment variable to match the character they just
/// wrote. The block is removed so the model is never handed a header it might read as
/// instruction — and "might read as instruction" is not hypothetical: a file that opens with a
/// name and a couple of rules is exactly the shape of a system prompt.
/// </para>
/// <para>
/// The daemon reads the same file with the same rule, in <c>Core/src/main.rs</c>. If you change the
/// format, change both, or the companion will be called one thing by the harness and another by
/// itself.
/// </para>
/// </summary>
public static class Persona
{
    /// <summary>Read a personality file's declared name, or null when it declares none.</summary>
    public static string? NameOf(string text) => Split(text).Name;

    /// <summary>The body, with any front matter removed, and the name it declared.</summary>
    public static (string Body, string? Name) Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].Trim().Equals("---", StringComparison.Ordinal))
        {
            return (text, null);
        }

        string? name = null;
        for (var index = 1; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Equals("---", StringComparison.Ordinal))
            {
                return (string.Join('\n', lines[(index + 1)..]), name);
            }

            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["name:".Length..].Trim().Trim('"', '\'').Trim();
                name = value.Length > 0 ? value : null;
            }
        }

        // No closing fence. Treat the whole file as the body rather than eating the character:
        // a missing fence is a typo, and losing the personality is a worse outcome than a stray
        // `---` in the prompt.
        return (text, name);
    }
}
