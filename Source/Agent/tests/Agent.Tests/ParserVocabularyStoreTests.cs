using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// What a story's parser said back, kept between sessions (03_session.md §9–§10 (`S58`–`S76`)).
/// <para>
/// The store is help text for the model and never a gate (01_frame.md's invariant), so everything here
/// is about what is remembered, not about what is refused. Since 2026-09-21 what is remembered is
/// the <em>attempt</em> — the line and the sentence that came back — because a verdict on the verb
/// is the one thing this file cannot honestly hold: five good words were taken off the table by
/// five sentences about objects, and replayed at every load after that.
/// </para>
/// </summary>
public class ParserVocabularyStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infocompanion-vocabulary-tests", Guid.NewGuid().ToString("N"));

    public ParserVocabularyStoreTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>An attempt whose refusal was about the object rather than about the verb.</summary>
    private static ParserAttempt Scope(string typed, string said) =>
        new(typed, said, WordUnknown: false);

    [Fact]
    public void What_one_evening_typed_the_next_remembers()
    {
        var store = new ParserVocabularyStore(_directory);
        store.Append("wishbringer", Scope("get bone", "You can't see any bone here!"));
        store.Append(
            "wishbringer",
            new ParserAttempt("follow stream", "That's not a verb I recognise.", WordUnknown: true));

        var attempts = store.Read("wishbringer");

        Assert.Equal(2, attempts.Count);
        Assert.Equal("get bone", attempts[0].Typed);
        Assert.Equal("You can't see any bone here!", attempts[0].GameSaid);
        Assert.False(attempts[0].WordUnknown);
        Assert.True(attempts[1].WordUnknown);
    }

    [Fact]
    public void A_line_from_the_old_shape_is_skipped_rather_than_believed()
    {
        // The file used to hold {"word":"examine","refused":true} — a verdict, and the verdict is
        // what the harness cannot honestly make. Reading one of those back would resurrect exactly
        // the claim the new shape exists to stop making, so the old lines are inert: no migration,
        // and the five words the 2026-09-21 session wrongly recorded stop being applied at the
        // next load.
        var store = new ParserVocabularyStore(_directory);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathFor("wishbringer"))!);
        File.WriteAllText(
            store.PathFor("wishbringer"),
            "{\"word\":\"examine\",\"refused\":true,\"at\":\"2026-09-21T22:24:51Z\"}\n");
        store.Append("wishbringer", Scope("examine can", "You can't see any can here!"));

        var attempts = store.Read("wishbringer");

        Assert.Equal("examine can", Assert.Single(attempts).Typed);
    }

    [Fact]
    public void Each_game_keeps_its_own_attempts()
    {
        var store = new ParserVocabularyStore(_directory);
        store.Append("advent", Scope("follow stream", "I don't know the word \"follow\"."));
        store.Append("witness", new ParserAttempt("follow", string.Empty, WordUnknown: true));

        Assert.Equal("follow stream", Assert.Single(store.Read("advent")).Typed);
        Assert.Equal("follow", Assert.Single(store.Read("witness")).Typed);
        Assert.Empty(store.Read("moonmist"));
    }

    [Fact]
    public void A_corrupt_line_is_one_lost_attempt_and_a_missing_file_is_an_empty_memory()
    {
        var store = new ParserVocabularyStore(_directory);
        store.Append("catapole", Scope("norte", "You can't see any norte here!"));
        File.AppendAllText(store.PathFor("catapole"), "this is not json\n");

        Assert.Single(store.Read("catapole"));
        Assert.Empty(store.Read("never-played"));
    }

    [Fact]
    public void The_attempts_reach_the_feedback_as_history()
    {
        // The wiring the loop does: load the dictionary, then what this game said last time. What
        // comes out is the note the model reads — history it can reason from, and one word verdict
        // said as a verdict.
        var store = new ParserVocabularyStore(_directory);
        store.Append("wishbringer", Scope("get bone", "You can't see any bone here!"));
        store.Append(
            "wishbringer",
            new ParserAttempt("follow stream", "That's not a verb I recognise.", WordUnknown: true));

        var feedback = new ParserFeedback();
        feedback.SetKnownVocabulary(["look", "take", "get", "north"], resolution: 6);
        foreach (var attempt in store.Read("wishbringer"))
        {
            feedback.NoteAttempt(attempt);
        }

        var note = feedback.ContextNote();
        Assert.NotNull(note);
        Assert.Contains("You can't see any bone here!", note);
        Assert.Contains("no word for: follow", note);
    }

    [Fact]
    public void An_attempt_already_remembered_is_not_news()
    {
        // The bools are what stop the file growing by a line every time the same thing is typed.
        var feedback = new ParserFeedback();

        Assert.True(feedback.NoteAttempt(Scope("get bone", "You can't see any bone here!")));
        Assert.False(feedback.NoteAttempt(Scope("get bone", "You can't see any bone here!")));
        Assert.True(feedback.NoteRejected("chase"));
        Assert.False(feedback.NoteRejected("chase"));
        Assert.True(feedback.NoteAccepted("east"));
        Assert.False(feedback.NoteAccepted("east"));
        Assert.False(feedback.NoteAccepted("chase"));
    }
}
