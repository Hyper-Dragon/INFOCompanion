using System.Text.Json;

namespace Agent.Core;

/// <summary>
/// The shape of the state directory (02_game_boundary.md §9 (`G51`)).
/// <para>
/// The mirror of the core's own guard, and deliberately the weaker half of it: the core
/// **claims** a directory — refusing one somebody has already played in — while the agent only
/// ever **reads** one, so all it needs to know is that the directory is claimed, and claimed in
/// the shape this build reads. It never writes the marker. One writer, or the two processes can
/// come to disagree about what the directory is.
/// </para>
/// <para>
/// Checked in <c>Program</c> before anything reads the state directory, because the point of a
/// refusal is that nothing has been read yet.
/// </para>
/// </summary>
internal static class StateShape
{
    /// <summary>The shape this build reads. Raised together with the core's own constant.</summary>
    public const long Shape = 1;

    /// <summary>The marker's own name, inside the state directory.</summary>
    public const string Marker = "state-shape.json";

    /// <summary>
    /// The reason to refuse this state directory, or <c>null</c> to carry on.
    /// <para>
    /// A refusal is a sentence for a person, naming what was found and what to do about it: an
    /// unclaimed directory, a shape written by another build, or a marker that will not parse.
    /// Nothing here repairs or migrates anything.
    /// </para>
    /// </summary>
    public static string? Refuse(string stateDirectory)
    {
        var file = Path.Combine(stateDirectory, Marker);

        if (!File.Exists(file))
        {
            return $"the state directory at {stateDirectory} has not been claimed by the core, so "
                + "this build cannot read it. Start the core against it, or start from an empty "
                + "state directory and a new game.";
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException error)
        {
            return $"the shape marker at {file} could not be read ({error.Message}).";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("shape", out var value)
                || !value.TryGetInt64(out var shape))
            {
                return $"{file} carries no shape this build can read.";
            }

            return shape == Shape
                ? null
                : $"the state directory at {stateDirectory} is written in shape {shape}, and this "
                    + $"build reads shape {Shape}. Nothing has been migrated: start from an empty "
                    + "state directory and a new game.";
        }
        catch (JsonException)
        {
            return $"{file} is not a shape marker this build can read.";
        }
    }
}
