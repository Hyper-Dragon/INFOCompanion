using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// The game's sidecar: what a story shipped with.
/// <para>
/// Two jobs here. One is <see cref="GameBrief.Read"/> on its own — the file, its absence, and the
/// case where an operator has to be told something went wrong. The other is the wiring, because a
/// sidecar that reads correctly and never reaches the prompt is indistinguishable from no sidecar
/// at all, and it fails silently.
/// </para>
/// </summary>
public sealed class GameBriefTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "infocompanion-brief-tests",
        Guid.NewGuid().ToString("N"));

    public GameBriefTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Sidecar(string stem, string text) =>
        File.WriteAllText(Path.Combine(_directory, GameBrief.FileNameFor(stem)), text);

    private void StandingNote(string text) =>
        File.WriteAllText(Path.Combine(_directory, GameBrief.DefaultFileName), text);

    // -- reading it -------------------------------------------------------------------------

    [Fact]
    public void A_game_with_no_sidecar_is_not_a_problem()
    {
        // The normal case: most games never had a leaflet, and "there isn't one" must not be
        // reported as a fault.
        var (text, problem) = GameBrief.Read(_directory, "witness");

        Assert.Null(text);
        Assert.Null(problem);
    }

    [Fact]
    public void A_sidecar_is_found_beside_the_story_file()
    {
        Sidecar("wishbringer", "Careful where you walk.");

        var (text, problem) = GameBrief.Read(_directory, "wishbringer");

        Assert.Null(problem);
        Assert.Contains("Careful where you walk.", text);
    }

    [Fact]
    public void One_games_sidecar_is_not_another_games()
    {
        Sidecar("wishbringer", "Careful where you walk.");

        var (text, _) = GameBrief.Read(_directory, "witness");

        Assert.Null(text);
    }

    [Fact]
    public void An_empty_sidecar_is_the_same_as_none()
    {
        Sidecar("witness", "   \n\n\t  ");

        var (text, problem) = GameBrief.Read(_directory, "witness");

        Assert.Null(text);
        Assert.Null(problem);
    }

    [Fact]
    public void With_nowhere_to_look_there_is_no_sidecar()
    {
        Assert.Null(GameBrief.Read(null, "witness").Text);
        Assert.Null(GameBrief.Read("   ", "witness").Text);
        Assert.Null(GameBrief.Read(_directory, string.Empty).Text);
    }

    [Fact]
    public void A_sidecar_too_long_to_use_is_cut_and_says_so()
    {
        // Never silently. Half an instruction, presented as though it were all of it, is worse
        // than the companion knowing there is more that it has not read. The cap is a parameter
        // precisely so a deployment can choose its own trade; the default is generous because
        // a transcribed manual is the normal case, not the pathological one.
        Sidecar("wishbringer", new string('x', 500 + 1));

        var (text, problem) = GameBrief.Read(_directory, "wishbringer", maxCharacters: 500);

        Assert.Null(problem);
        Assert.NotNull(text);
        Assert.Contains("too long to include", text);
        Assert.True(text!.Length < 850);
    }

    [Fact]
    public void A_manual_sized_sidecar_comes_through_whole()
    {
        // The box was part of the game. The default cap has room for a full manual, a map and
        // a set of feelies, and the point of the test is that none of it is quietly dropped.
        var manual = new string('x', 12000);
        Sidecar("witness", manual);

        var (text, problem) = GameBrief.Read(_directory, "witness");

        Assert.Null(problem);
        Assert.Equal(manual, text);
    }

    // -- the stand-in for a game that shipped with nothing --------------------------------------

    [Fact]
    public void A_game_with_nothing_of_its_own_gets_the_standing_note()
    {
        // Most games will never have a sidecar. The stand-in is what stops that being silence:
        // it says nothing came with this one, and that the person at the keyboard may be the only
        // one who can still supply the box.
        StandingNote("Nothing came with this game. Ask the player whether they still have it.");

        var (text, problem) = GameBrief.ReadDefault(_directory);

        Assert.Null(problem);
        Assert.Contains("Ask the player whether they still have it", text);
    }

    [Fact]
    public void A_games_own_sidecar_is_never_replaced_by_the_standing_note()
    {
        // Precedence is the whole point: our note applies when the game shipped with nothing, not
        // as a default that stands in front of what it did ship with. Which of the two is used is
        // the loop's decision, so Read keeps telling the truth about the game's own file.
        StandingNote("Nothing came with this game.");
        Sidecar("wishbringer", "Careful where you walk.");

        Assert.Contains("Careful where you walk", GameBrief.Read(_directory, "wishbringer").Text);
        Assert.Null(GameBrief.Read(_directory, "witness").Text);
    }

    [Fact]
    public void With_no_standing_note_either_there_is_still_nothing()
    {
        // An operator who deletes both gets exactly the behaviour from before the stand-in
        // existed, which is the same contract the asset pack has (12_shipped_container.md).
        Assert.Null(GameBrief.ReadDefault(_directory).Text);
        Assert.Null(GameBrief.ReadDefault(null).Text);
        Assert.Null(GameBrief.ReadDefault("   ").Text);
    }

    [Fact]
    public void The_standing_note_is_capped_like_any_other_brief()
    {
        StandingNote(new string('x', 500 + 1));

        var (text, problem) = GameBrief.ReadDefault(_directory, maxCharacters: 500);

        Assert.Null(problem);
        Assert.Contains("too long to include", text);
    }

    // -- reaching the prompt ----------------------------------------------------------------

    [Fact]
    public async Task What_came_with_the_game_reaches_the_model()
    {
        Sidecar("wishbringer", "Careful where you walk. Some steps end the run.");
        var llm = new ScriptedLlm().Types("Steady, then. North, and I am watching for traffic.", "north");

        await using var harness = LoopHarness.StartWithGames(
            llm,
            _directory,
            Game("wishbringer"),
            Handover("north"));

        await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Contains("Careful where you walk", llm.LastPrompt);

        // And labelled as something that came with the game rather than as the companion's own
        // reading of the screen. The honesty rule is about acquired facts, and being handed one
        // is still acquiring it.
        Assert.Contains("What came with this game", llm.LastPrompt);
    }

    [Fact]
    public async Task A_game_with_no_sidecar_adds_nothing_to_the_prompt()
    {
        // No sidecar and no stand-in either: the behaviour from before either existed, and it still
        // holds when an operator deletes `_default.md`. The shipped container carries one, so this
        // is the file-is-gone case rather than the normal one.
        var llm = new ScriptedLlm().Types("On it.", "north");

        await using var harness = LoopHarness.StartWithGames(
            llm,
            _directory,
            Game("witness"),
            Handover("north"));

        await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.DoesNotContain("What came with this game", llm.LastPrompt);
    }

    [Fact]
    public async Task The_standing_note_reaches_the_model_labelled_as_ours()
    {
        // With the stand-in present the block is always there, so the framing has to separate our
        // words from the game's. A companion that thought this came in the box would believe it had
        // been handed facts it never had — and would report on them as though it had.
        StandingNote("Nothing came with this game. Ask the player whether they still have the box.");
        var llm = new ScriptedLlm().Types("On it.", "north");

        await using var harness = LoopHarness.StartWithGames(
            llm,
            _directory,
            Game("witness"),
            Handover("north"));

        await harness.UntilAsync(text => text.Contains("game_input"));

        Assert.Contains("Ask the player whether they still have the box", llm.LastPrompt);
        Assert.Contains("## What came with this game", llm.LastPrompt);
        Assert.Contains("ours rather than the game's", llm.LastPrompt);

        // And never the wording that claims it arrived in the box.
        Assert.DoesNotContain("this came with the game itself", llm.LastPrompt);
    }

    // Three dollars, because the envelope itself ends in two closing braces: with two, the
    // trailing `}}` reads as an escape and the literal will not compile.
    private static string Game(string stem, ulong seq = 1) =>
        $$$"""{"v":1,"type":"system","seq":{{{seq}}},"ts":"now","payload":{"event":"game_changed","detail":"{{{stem}}}"}}""";

    private static string Handover(string line, ulong seq = 2) =>
        $$$"""{"v":1,"type":"handover","seq":{{{seq}}},"ts":"now","payload":{"line":"{{{line}}}"}}""";
}
