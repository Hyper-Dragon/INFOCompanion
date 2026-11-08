using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The saves of the game in play, as the companion reads them (06_tool_surface.md §2.3 (`T14`–`T16`)).
/// <para>
/// The failure this whole item exists for: on 2026-09-17 the folder held <c>v</c>, <c>sh</c>,
/// <c>br</c> and <c>ad</c> while the notebook faithfully recorded <c>village.sav</c>,
/// <c>shoppe.sav</c>, <c>bridge.sav</c> and <c>advent.sav</c>. Both were true. Believing a
/// filename was the mistake, so what is asserted here is that a name is always reported *with the
/// note the core wrote beside it* — the turn it was taken at, which is what makes a save a
/// position rather than a fact about a file.
/// </para>
/// </summary>
public class SaveStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infocompanion-save-tests", Guid.NewGuid().ToString("N"));

    public SaveStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private string Write(string stem, string name, string? note)
    {
        var directory = SaveStore.DirectoryFor(_directory, stem);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "not a real save");
        if (note is not null)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".json"), note);
        }

        return path;
    }

    [Fact]
    public void The_folder_is_where_the_core_writes_it()
    {
        Assert.Equal(
            Path.Combine(_directory, "saves", "advent"),
            SaveStore.DirectoryFor(_directory, "advent"));
    }

    [Fact]
    public void Nothing_to_report_is_null_rather_than_an_empty_list()
    {
        // A companion told "there are no saves" when the folder is merely unreadable has been
        // lied to, and the caller says something different for each.
        Assert.Null(SaveStore.Describe(_directory, "advent"));
        Assert.Null(SaveStore.Describe(_directory, ""));
    }

    [Fact]
    public void Each_save_carries_the_turn_it_was_taken_at()
    {
        Write("advent", "advent.sav",
            """{"game":"advent","saved_at":"2026-09-18T18:31:04Z","reason":"auto","turn":42,"run":2,"generation":1,"bytes":852}""");

        var text = SaveStore.Describe(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("advent.sav", text);
        Assert.Contains("turn 42", text);
        Assert.Contains("attempt 2", text);
        Assert.Contains("taken automatically", text);
        Assert.Contains("a restart comes back to", text);
        // The distinction the whole tool rests on.
        Assert.Contains("not a memory of the conversation", text);
    }

    [Fact]
    public void A_note_that_cannot_be_read_leaves_the_position_sound()
    {
        Write("advent", "village.sav", null);

        var text = SaveStore.Describe(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("village.sav", text);
        Assert.Contains("no note beside it", text);
    }

    [Fact]
    public void A_corrupt_note_is_not_a_crash()
    {
        Write("advent", "village.sav", "{ this is not json");

        var text = SaveStore.Describe(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("village.sav", text);
        Assert.Contains("no note beside it", text);
    }

    [Fact]
    public void Every_save_in_the_folder_is_listed()
    {
        Write("advent", "advent.sav", """{"game":"advent","saved_at":"2026-09-18T18:31:04Z","reason":"auto","turn":42,"run":1,"generation":1,"bytes":852}""");
        Write("advent", "village.sav", """{"game":"advent","saved_at":"2026-09-17T21:02:11Z","reason":"manual","turn":17,"run":1,"generation":1,"bytes":700}""");

        var text = SaveStore.Describe(_directory, "advent");

        Assert.NotNull(text);
        Assert.Contains("advent.sav", text);
        Assert.Contains("village.sav", text);
        Assert.Contains("turn 17", text);
        Assert.Contains("taken when the player asked for one", text);
    }

    [Fact]
    public void Another_game_s_saves_are_not_this_game_s()
    {
        Write("zork1", "zork1.sav",
            """{"game":"zork1","saved_at":"2026-09-18T18:31:04Z","reason":"auto","turn":9,"run":1,"generation":1,"bytes":812}""");

        Assert.Null(SaveStore.Describe(_directory, "advent"));
    }
}
