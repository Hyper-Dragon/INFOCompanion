using System.Globalization;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// State as dated observations: what is possible, what is current, and what changed
/// (05_memory.md §8 (`M41`)).
/// <para>
/// The fixture carries the two shapes that made this necessary — the door trio of `open`, `examine`,
/// `close` where the door ends where it started, and five levers at five places that all pull the
/// same-named thing in five different rooms — with the words invented and the shapes kept.
/// </para>
/// </summary>
public class StateReadingTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "infocompanion-state-tests", Guid.NewGuid().ToString("N"));

    public StateReadingTests()
    {
        Directory.CreateDirectory(Path.Combine(_state, "observations"));
        Directory.CreateDirectory(Path.Combine(_state, "runs"));

        // Generation 1, which a reload will replace: one room, one thing, and nothing more.
        Row(1, "look", "A cold ledge above the sea.", 700, 1);
        Row(2, "examine rock", "The rock is grey and wet.", 700, 1);
        Row(3, "look", "The library, and a door in the north wall.", 155, 1);
        Row(4, "look", "An empty corridor, going nowhere.", 42, 3);

        // Generation 3. The trio: closed and unlocked, opened, examined, closed again.
        Row(507, "look", "The library, and a door in the north wall.", 155, 3);
        Row(508, "examine door", "The door is closed and unlocked.", 155, 3);
        Row(514, "open door", "The door swings open.", 155, 3);
        Row(515, "examine door", "The door is now open.", 155, 3);
        Row(551, "close door", "The door is now closed.", 155, 3);
        Row(560, "examine door", "The door is now closed.", 155, 3);

        // The lever, whose own position the game never reports: what it prints is the door.
        Row(530, "east", "The study, with a door in the north wall.", 31, 3);
        Row(536, "examine door", "The study door is open.", 31, 3);
        Row(537, "pull lever", "The lever moves; the study door closes.", 31, 3);
        Row(538, "examine door", "The study door is closed.", 31, 3);
        Row(539, "pull lever", "The lever moves; the study door opens.", 31, 3);
        Row(540, "examine door", "The study door is open.", 31, 3);

        // Five levers, five places, one name between them — each reached by a move, because a room
        // only changes when a line changes it (05_memory.md §8 (`M40.5`)).
        Row(595, "north", "The library, and a door in the north wall.", 14, 3);
        Row(600, "examine door", "The library door is open.", 14, 3);
        Row(604, "south", "The study, with a door in the north wall.", 31, 3);
        Row(605, "west", "Iris's room, and a door in the wall.", 179, 3);
        Row(606, "examine door", "Iris's door is open.", 179, 3);
        Row(607, "east", "The study, with a door in the north wall.", 31, 3);
        Row(608, "down", "A priest hole, and a door beyond it.", 186, 3);
        Row(609, "examine door", "The priest-hole door is shut.", 186, 3);
        Row(610, "up", "The study, with a door in the north wall.", 31, 3);
        Row(611, "northeast", "Hyde's room, and a door in the wall.", 224, 3);
        Row(612, "examine door", "Hyde's door is open.", 224, 3);

        File.WriteAllLines(ObservationLog.PathFor(_state, "moonmist"), _rows);
        File.WriteAllText(
            Path.Combine(_state, "runs", "moonmist.json"),
            """{"current":{"number":1,"started":"2026-09-29T00:41:00Z","generation":3}}""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private readonly List<string> _rows = [];

    private void Row(int turn, string input, string output, long location, long generation) =>
        _rows.Add(
            $$"""{"turn":{{turn}},"input":"{{input}}","output":"{{output}}","fingerprint":"f#{{turn}}","run":1,"generation":{{generation}},"location":{{location.ToString(CultureInfo.InvariantCulture)}}}""");

    private StateReading.Reading At(string? room, string? thing) =>
        StateReading.Describe(WorldState.Build(_state, "moonmist"), room, roomHere: null, thing);

    // -- possible, and current --------------------------------------------------------------------

    [Fact]
    public void Every_value_the_game_has_printed_is_a_possible_state()
    {
        // No flag, no enum, and no reading of the prose: the possible states of the door are the
        // three sentences the game has printed about it, quoted.
        var reading = At("155", "door");

        Assert.Contains("\"The door is closed and unlocked.\"", reading.Prose);
        Assert.Contains("\"The door is now open.\"", reading.Prose);
        Assert.Contains("\"The door is now closed.\"", reading.Prose);
    }

    [Fact]
    public void Current_is_the_latest_observation_in_the_world_being_played()
    {
        // At loc 31 the door is opened, closed and opened again. "Pull lever means open" is falsified
        // by the game three turns later, and the record says which of the two is true now.
        var reading = At("31", "door");

        Assert.Contains("As of turn 540", reading.Prose);
        Assert.Contains("\"The study door is open.\"", reading.Prose);
        Assert.Contains("first seen at turn 536", reading.Prose);
    }

    [Fact]
    public void A_change_carries_what_was_typed_when_it_happened()
    {
        // The relation is (value) --what was typed--> (value), not (action) => (state): the door was
        // closed and unlocked, `open door` was typed, and the next look found it open.
        var reading = At("155", "door");

        Assert.Contains(
            "Changed: \"The door is closed and unlocked.\" -> \"The door is now open.\" at turn 515, with open door typed in between.",
            reading.Prose);
        Assert.Contains(
            "Changed: \"The door is now open.\" -> \"The door is now closed.\" at turn 560, with close door typed in between.",
            reading.Prose);
    }

    [Fact]
    public void A_world_that_moved_on_its_own_is_its_own_answer()
    {
        // The plan's first-class event: a change with nothing typed behind it. The words are the
        // game's; that nobody pulled anything is the harness's, and it is worth saying.
        var turns = _rows.Count;
        Row(698, "down", "A room with a gong in it.", 900, 3);
        Row(699, "examine gong", "The gong is still.", 900, 3);
        Row(700, "wait", "The staff leave, and the gates are bolted behind them.", 900, 3);
        Row(701, "examine gong", "The gong has fallen silent.", 900, 3);
        File.WriteAllLines(ObservationLog.PathFor(_state, "moonmist"), _rows);

        var reading = At("900", "gong");

        Assert.Contains(
            "Changed: \"The gong is still.\" -> \"The gong has fallen silent.\" at turn 701, and nothing was typed in between.",
            reading.Prose);
        Assert.True(turns > 0);
    }

    // -- and nothing carried across a reload -------------------------------------------------------

    [Fact]
    public void A_thing_only_seen_before_the_fork_is_unknown_here_rather_than_remembered()
    {
        // The rock was examined on the ledge in generation 1. In generation 3 the ledge is history,
        // so what is true of the rock is what has been seen of it here, which is nothing.
        var here = At("155", "door");
        Assert.Contains("As of turn 560", here.Prose);

        var gone = At("700", "rock");

        Assert.Contains("last seen in generation 1", gone.Prose);
        Assert.Contains("Nothing about it is carried over", gone.Prose);
        Assert.DoesNotContain("The rock is grey and wet.", gone.Prose);
    }

    // -- the five levers ---------------------------------------------------------------------------

    [Fact]
    public void One_name_at_five_places_is_five_things()
    {
        // `loc 179` -> Iris's bedroom, `186` -> the priest hole, `14` -> the library, `31` -> the
        // study, `224` -> Hyde's bedroom: five doors, one word in the command, and a notebook that
        // wrote them down as one line about "the lever" is exactly how that gets lost.
        var library = At("14", "door");
        var study = At("31", "door");
        Assert.Contains("The library door is open.", library.Prose);
        Assert.DoesNotContain("study", library.Prose);
        Assert.Contains("The study door is open.", study.Prose);
        Assert.DoesNotContain("library", study.Prose);

        // And the document holds six of them rather than one: a door was named in six rooms. What
        // has to hold is that the *state* of each is read at its own room, which the two answers
        // above are: the library's words and the study's words never appear in each other's answer.
        var world = WorldState.Build(_state, "moonmist");
        var doors = world.Things
            .Where(row => row.Name == "door")
            .Select(row => row.Place)
            .Distinct()
            .Count();

        Assert.Equal(6, doors);
    }

    [Fact]
    public void Asking_about_a_thing_nobody_has_touched_says_which_kind_of_nothing_it_is()
    {
        Assert.Contains("Nothing on the record names \"sword\"", At("155", "sword").Prose);
        Assert.Contains("Nothing has been typed at anything in room 42", At("42", null).Prose);
        Assert.Contains("The record cannot say which room", At(null, null).Prose);
    }

    // -- the ledger ---------------------------------------------------------------------------------

    [Fact]
    public void Everything_already_tried_here_comes_back_in_the_games_own_words()
    {
        // The question James rebuilt by hand over twenty turns in the game room, out of a record
        // that held every row of it all along — and the judgement about which rows matter stays out
        // of the answer, because the last time the harness drew one from a repeated refusal sentence
        // it was wrong.
        var reading = At("31", null);

        Assert.Contains("What has been tried here, most recent first:", reading.Prose);
        Assert.Contains("turn 537  pull lever  ->  \"The lever moves; the study door closes.\"", reading.Prose);
        Assert.Contains("turn 538  examine door  ->  \"The study door is closed.\"", reading.Prose);
        Assert.Contains("a phrase that comes back the same way for a whole class of commands", reading.Prose);
        Assert.Contains("nothing here decides which words matter", reading.Prose);
    }

    [Fact]
    public void The_ledger_carries_the_same_rows_as_facts()
    {
        var facts = System.Text.Json.JsonSerializer.Serialize(At("31", null).Facts);

        Assert.Contains("\"tried\":[", facts);
        Assert.Contains("\"verb\":\"pull\"", facts);
        Assert.Contains("\"turn\":537", facts);
    }

    // -- the facts ---------------------------------------------------------------------------------

    [Fact]
    public void The_answer_carries_the_facts_the_sentence_carries()
    {
        var facts = System.Text.Json.JsonSerializer.Serialize(At("31", "door").Facts);

        Assert.Contains("\"room\":31", facts);
        Assert.Contains("\"asked_with\":\"looked at\"", facts);
        Assert.Contains("\"current\":\"The study door is open.\"", facts);
        Assert.Contains("\"at\":538", facts);
        Assert.Contains("\"by\":[\"pull lever\"]", facts);
    }
}
