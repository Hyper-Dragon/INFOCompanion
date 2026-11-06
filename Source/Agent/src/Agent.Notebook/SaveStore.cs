using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Notebook;

/// <summary>
/// The saves of the game in play, as the companion should read them (06_tool_surface.md §2.3 (`T14`–`T16`)).
/// <para>
/// This is the one lookup the game genuinely cannot answer. A Z-machine has no way to be asked for
/// a directory listing, and its own <c>restore</c> offers a single default name — so "which saves
/// are there, and where does each one sit" is a question only the core's own bookkeeping can
/// answer, because the core is the thing that typed the filename.
/// </para>
/// <para>
/// Why this had to exist at all: on 2026-09-17 the folder held
/// <c>ad  afterbridge.sav  afternest.sav  br  l  sh  v  wishbringer.sav</c> while the notebook
/// faithfully recorded <c>village.sav, shoppe.sav, bridge.sav…</c>. Both were true — the interpreter
/// was chopping basenames to fit a path, and the core had no way to notice. The chopping is fixed;
/// believing a filename is still not a plan, so each name here is reported with the note the core
/// wrote beside the file at the moment it committed.
/// </para>
/// <para>
/// A save is a <b>position</b>, not a memory: the room, what is being carried, and how the game is
/// set up. Nothing in it holds the conversation. That is why the turn matters — the autosave runs
/// every few turns, so the world behind a restore can be several moves behind what was being said
/// when the session stopped.
/// </para>
/// </summary>
public static class SaveStore
{
    /// <summary><c>&lt;state&gt;/saves/&lt;stem&gt;</c> — one folder per game.</summary>
    public static string DirectoryFor(string stateDirectory, string gameStem) =>
        Path.Combine(stateDirectory, "saves", gameStem);

    /// <summary>
    /// What is in this game's save folder, or <c>null</c> when there is nothing to report.
    /// </summary>
    public static string? Describe(string stateDirectory, string gameStem)
    {
        var entries = List(stateDirectory, gameStem);
        if (entries.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append("Saves for ").Append(gameStem).Append(':').Append('\n');

        foreach (var entry in entries)
        {
            text.Append("\n  ").Append(entry.File);
            if (entry.Turn is null)
            {
                text.Append("  — no note beside it: ")
                    .Append(Describe(entry.Bytes))
                    .Append(", and nothing on record about when it was written");
            }
            else
            {
                text.Append("  — ")
                    .Append(entry.Reason switch
                    {
                        "auto" => "taken automatically",
                        "manual" => "taken when the player asked for one",
                        "switch" => "taken as the session left this game",
                        "shutdown" => "taken as the session closed",
                        _ => "taken (" + entry.Reason + ")",
                    })
                    .Append(", at turn ").Append((entry.Turn ?? 0).ToString(CultureInfo.InvariantCulture))
                    .Append(", attempt ").Append(entry.Run.ToString(CultureInfo.InvariantCulture))
                    .Append(", fork ").Append(entry.Fork.ToString(CultureInfo.InvariantCulture))
                    .Append(", written ").Append(entry.SavedAt);

                if (entry.IsTheRestartTarget)
                {
                    text.Append(" — this is the one a restart comes back to");
                }
            }
        }

        text.Append("\n\nA save is a position in the world, not a memory of the conversation: the "
            + "room, what is being carried, and how the game is set up. The autosave runs every few "
            + "turns, so a world restored from one can be several moves behind what was last said. If "
            + "a note beside a file cannot be read, the position is still sound — the note is only "
            + "what the core happened to know about it.");

        return text.ToString();
    }

    /// <summary>
    /// The same saves as facts, for a caller that has to be exact about them (04_companion.md §2 (`C6`–`C13`)).
    /// <para>
    /// Read once and rendered twice, so the sentence and the facts cannot disagree — and the facts
    /// exist because the sentence is not always enough. What a save *is* here is a turn, an attempt
    /// and a fork, and the companion has to quote those numbers back when it says which world a
    /// position belongs to. A model reading them out of prose is a model that will eventually read
    /// them slightly wrong, and a wrong fork is a confident answer about the wrong branch.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SaveEntry> List(string stateDirectory, string gameStem)
    {
        if (string.IsNullOrWhiteSpace(gameStem))
        {
            return [];
        }

        var directory = DirectoryFor(stateDirectory, gameStem);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory.GetFiles(directory, "*.sav");
        if (files.Length == 0)
        {
            return [];
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var entries = new List<SaveEntry>(files.Length);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var note = Read(file);
            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Size is a nicety; the name and the note are the point.
            }

            entries.Add(
                new SaveEntry(
                    name,
                    Path.GetFileNameWithoutExtension(name),
                    note?.Turn,
                    note?.Run ?? 0,
                    note?.Generation ?? 0,
                    note?.Reason ?? string.Empty,
                    note?.SavedAt ?? string.Empty,
                    size,
                    name.Equals(gameStem + ".sav", StringComparison.OrdinalIgnoreCase)));
        }

        return entries;
    }

    private static string Describe(long size) =>
        size > 0
            ? size.ToString("N0", CultureInfo.InvariantCulture) + " bytes"
            : "size unknown";

    /// <summary>The core's note beside a checkpoint, or <c>null</c> when it cannot be read.</summary>
    private static SaveNote? Read(string savePath)
    {
        var notePath = Path.ChangeExtension(savePath, ".json");
        if (!File.Exists(notePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SaveNote>(File.ReadAllText(notePath));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// One save, as a fact rather than a sentence (04_companion.md §2 (`C6`–`C13`)).
/// <para>
/// The names are the ones the facts block carries, so the shape is part of the tool surface and not
/// an accident of this class. <see cref="Turn"/> is nullable on purpose: a file with no note beside
/// it is a real state — the position is sound and nothing is known about when it was taken — and a
/// zero would say "turn zero", which is a different and wrong claim.
/// </para>
/// </summary>
/// <param name="File">The filename alone, as it appears in the folder.</param>
/// <param name="Name">The filename without <c>.sav</c> — what a restore names.</param>
/// <param name="Turn">The core's turn when it was taken, or null when there is no note.</param>
/// <param name="Run">Which attempt at this game it belongs to.</param>
/// <param name="Fork">Which fork of the world inside that attempt.</param>
/// <param name="Reason">Why it was taken: <c>auto</c>, <c>manual</c>, <c>switch</c>, <c>shutdown</c>.</param>
/// <param name="SavedAt">RFC 3339, or empty when there is no note.</param>
/// <param name="Bytes">The file's size, or zero when it could not be read.</param>
/// <param name="IsTheRestartTarget">This is the game's own checkpoint, which a restart returns to.</param>
public readonly record struct SaveEntry(
    string File,
    string Name,
    long? Turn,
    long Run,
    long Fork,
    string Reason,
    string SavedAt,
    long Bytes,
    bool IsTheRestartTarget);

/// <summary>
/// What the core knew about a save at the moment it committed — written by the core beside the
/// file, and deliberately not interpreted here.
/// </summary>
internal sealed class SaveNote
{
    [JsonPropertyName("game")]
    public string Game { get; set; } = string.Empty;

    /// <summary>RFC 3339, at the moment the checkpoint committed.</summary>
    [JsonPropertyName("saved_at")]
    public string SavedAt { get; set; } = string.Empty;

    /// <summary><c>auto</c>, <c>manual</c>, <c>switch</c> or <c>shutdown</c>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>The core's turn counter when the save was taken.</summary>
    [JsonPropertyName("turn")]
    public long Turn { get; set; }

    [JsonPropertyName("run")]
    public long Run { get; set; }

    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}
