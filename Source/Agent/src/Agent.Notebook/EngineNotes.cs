namespace Agent.Notebook;

/// <summary>
/// What is true of these engines at all, rather than of any one story.
/// <para>
/// The companion reads it on every call, beside its notebook and its memories, because these are
/// the facts a person who grew up with these games would not have to be told: a menu is answered
/// with a key, a form is filled a field at a time, and some of these games paint the screen instead
/// of printing to it.
/// </para>
/// <para>
/// All three were paid for on 2026-09-19, when <c>border_zone</c> and <c>bureaucracy</c> were both
/// called unplayable while the companion typed verbs at a chapter menu and a whole name into a
/// five-wide field. They are deliberately generic: no story's own words are in them and nothing
/// here matches on text, which is the rule this file exists to respect. The core may never carry a
/// per-game table of spellings, and neither may the companion's knowledge of the engines.
/// </para>
/// <para>
/// <b>Read-only for now.</b> The appending rule — a fact goes in when it has been learned twice, or
    /// once and plainly structural — is the playbook's (the memory rules), and
/// it needs bookkeeping this does not have: knowing whether something has been met twice is not a
/// property of one file. Until that lands, this is a seed a hand can extend; the file is
/// <c>&lt;state&gt;/notebook/_engine.md</c> and anything in it is read as it stands.
/// </para>
/// </summary>
public sealed class EngineNotes
{
    /// <summary>
    /// The reserved stem. Reserved the same way the memories' is: notebooks are named after story
    /// files, and no story file discovery produces a leading underscore.
    /// </summary>
    public const string Stem = "_engine";

    public EngineNotes(string stateDirectory)
    {
        StateDirectory = stateDirectory;
    }

    public string StateDirectory { get; }

    /// <summary><c>&lt;state&gt;/notebook/_engine.md</c>.</summary>
    public string FilePath => Path.Combine(StateDirectory, "notebook", Stem + ".md");

    /// <summary>
    /// The file as it stands, or the seed when there is no file yet.
    /// <para>
    /// A missing file and the seed are the same thing here, deliberately. These are facts about the
    /// engines rather than anything anybody observed, so there is nothing to be lost by starting
    /// from them, and an instance that has never been played has to be as useful as one that has.
    /// </para>
    /// </summary>
    public string Read()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (IOException)
        {
            // A file that cannot be read is a file that is not there. Nothing here is worth
            // failing a model call over.
        }

        return Seed;
    }

    /// <summary>What was known before a single evening had been played.</summary>
    public const string Seed = """
        # What is true of these games

        Not of any one story: of the machines these stories ran on.

        - **A game may be painted rather than printed.** Some of them draw their own screen and
          leave it standing, so a turn that produced no output is not a turn that produced nothing.
          What is on the screen is the answer.
        - **A menu is answered with a key, not a word.** When a game is offering a choice — which
          chapter, are you sure, a `(R)estore?` — it wants the digit or the letter. A verb typed at
          it is wasted. `press_key` is the call for that; `type` is for a line.
        - **A form is filled in one field at a time, and it is not a command prompt.** Some of these
          games open with a questionnaire. Characters go into a field, the field ends, and the next
          one begins. Parser vocabulary is no help at all here, and a whole line is taken as that
          first field's answer however long it is.
        """;
}
