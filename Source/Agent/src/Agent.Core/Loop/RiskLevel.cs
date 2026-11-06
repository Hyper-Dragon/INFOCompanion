namespace Agent.Core.Loop;

/// <summary>
/// Risk, as a class and as a standing answer (§4.7).
/// <para>
/// The same three words are used for two things on purpose. An <em>act</em> has a class —
/// what it would cost the player if it were wrong — and the <em>player</em> has a standing
/// level: the most they are willing to let run without being asked. The gate is the
/// comparison between them, so "risky" stops being an adjective somebody picked and becomes
/// arithmetic the player can set.
/// </para>
/// <para>
/// Ordered deliberately: <c>Low &lt; Medium &lt; High</c>, because the comparison is the
/// rule.
/// </para>
/// </summary>
public enum RiskLevel
{
    /// <summary>Looking, reading, listening, and moving: the medium of play.</summary>
    Low,

    /// <summary>Handling things: reversible in every story this design cares about.</summary>
    Medium,

    /// <summary>The acts nobody can take back: spending, breaking, burning, giving away.</summary>
    High,
}

/// <summary>Parsing and naming for <see cref="RiskLevel"/>, in the words a player types.</summary>
public static class RiskLevels
{
    /// <summary>
    /// The level a word names. Unknown words and silence mean <c>medium</c>, which is the
    /// gate as it always was — the safe direction for a typo is the behaviour a player
    /// already knows.
    /// </summary>
    public static RiskLevel Parse(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "low" => RiskLevel.Low,
        "high" => RiskLevel.High,
        _ => RiskLevel.Medium,
    };

    /// <summary>The level's own name, as the player types it and as the log records it.</summary>
    public static string Label(RiskLevel level) => level switch
    {
        RiskLevel.Low => "low",
        RiskLevel.High => "high",
        _ => "medium",
    };
}
