namespace Agent.Notebook;

/// <summary>
/// A stretch of play that is going nowhere: a rolling window of turns in which the record shows no
/// room entered for the first time and nothing new said about anything (04_companion.md `C39`).
/// <para>
/// A **delta over the record**, not a counter. `C39` already counts repeats of one action, and the
/// frequency-counting version the notes proposed is the one `C39.3` records as wrong: a companion
/// repeating a verb is doing a different thing from one that has walked six rooms and learned nothing.
/// </para>
/// <para>
/// It reports a **fact** (`F20`) and not a conclusion: how many turns, how many of them went
/// somewhere, and that none of it was new. Whether that means the evening is stuck is the companion's
/// to decide and the player's to say. Measured target: 00:49:38 → 00:55:33 on 2026-09-29, twenty-five
/// turns of sweeping one room, ending in the player's *"sheesh"*.
/// </para>
/// </summary>
public static class Stagnation
{
    /// <summary>How many turns are looked at. Long enough to be a wander, short enough to be this evening.</summary>
    public const int Window = 20;

    /// <summary>
    /// The stretch itself, as numbers: how many of the window's turns happened, and how many of them
    /// went somewhere.
    /// <para>
    /// The prose is what the companion is told; this is what a record can count and a signal can be
    /// acted on. `§25` item 4: the delta was built and told as a sentence in the prompt, and a sentence
    /// nobody acts on can ride for a whole errand — which, since the player's own errand stopped having
    /// a step bound, is the only thing watching one.
    /// </para>
    /// </summary>
    public sealed record Stretch(int Turns, int Moved)
    {
        /// <summary>The fact in words — the same words wherever it is said (04_companion.md `C38`).</summary>
        public string Prose =>
            $"Nothing new for {Turns} turns: no room entered for the first time, and nothing said "
            + $"about anything that had not already been said. {Moved} of those turns went somewhere, and "
            + "all of them somewhere already known. That is what the record shows rather than a verdict on "
            + "it — but a sweep with nothing in it is worth saying out loud before it becomes the evening.";

        public override string ToString() => Prose;
    }

    /// <summary>
    /// The stretch, as the numbers the reading is made of — for the log, not for the companion.
    /// </summary>
    /// <param name="Turns">How many turns of the window happened at all.</param>
    /// <param name="NewPlaces">Rooms entered for the first time inside it.</param>
    /// <param name="NewThings">Things said about for the first time inside it.</param>
    /// <param name="Size">The window those counts were taken over.</param>
    public sealed record Reading(int Turns, int NewPlaces, int NewThings, int Size)
    {
        /// <summary>Too few turns to be a wander, whatever they found.</summary>
        public bool TooYoung => Turns < Size / 2;

        /// <summary>The record grew inside the window, which is the whole of what clears it.</summary>
        public bool FoundSomething => NewPlaces > 0 || NewThings > 0;

        /// <summary>
        /// Why the fact is not being put, in one clause — the whole point of the record (`C39.9`).
        /// </summary>
        public string Why => TooYoung
            ? "too young to be a wander: not half the window has been played"
            : FoundSomething
                ? "the record grew inside the window, so there is nothing to report"
                : "nothing new and nothing moved: this one should have fired";
    }

    /// <summary>
    /// The stretch, or nothing. Nothing whenever the window is too young to mean anything or when
    /// anything in it was new.
    /// </summary>
    /// <param name="turnsPlayed">
    /// How many turns the window actually contained, counted **from the record** — the observation
    /// log's own rows — and not from the document's ledger. The ledger holds the lines worth
    /// reporting on, and a stretch is made of turns that were worth nothing, so counting it there
    /// gave 0-2 turns for a fifteen-turn sweep of a maze and the fact could never fire at all.
    /// Measured 2026-10-01, Wishbringer's Steep Trail: nineteen turns of play, two ledgered actions.
    /// </param>
    public static Stretch? Look(WorldState.Document? world, long currentTurn, int turnsPlayed, int window = Window)
    {
        if (world is null || world.Header.Generation is not { } generation || currentTurn <= 0)
        {
            return null;
        }

        var from = currentTurn - window;
        if (turnsPlayed < window / 2)
        {
            // Too little to be a wander. A window at the start of an evening, or one with the
            // companion away for most of it, is not stagnation — it is an evening that has just begun.
            return null;
        }

        var (newPlaces, newThings) = Firsts(world, generation, from, currentTurn);
        if (newPlaces > 0 || newThings > 0)
        {
            return null;
        }

        var moved = world.Edges.Count(
            edge => edge.Gen == generation && edge.Outcome == "moved" && edge.LastSeen > from && edge.LastSeen <= currentTurn);

        return new Stretch(turnsPlayed, moved);
    }

    /// <summary>
    /// What the window holds, whether or not it is worth reporting (`C39.9`). Null when there is no
    /// world to read at all.
    /// <para>
    /// The same numbers <see cref="Look"/> decides on, handed back instead of turned into a verdict,
    /// because a delta that *cannot* fire and a delta with *nothing to say* produced the same row in
    /// the log, which is none. Measured 2026-10-01, twice in one evening: first an errand that kept
    /// finding new ground, then a twenty-two-turn sweep of a single room built to have nothing in it —
    /// and neither sitting could answer why the fact was never put to the companion. Sixteen turns of
    /// `look` in one room is either a stretch the harness failed to count or a room with something new
    /// in it, and the two want opposite work.
    /// </para>
    /// </summary>
    public static Reading? Counts(WorldState.Document? world, long currentTurn, int turnsPlayed, int window = Window)
    {
        if (world is null || world.Header.Generation is not { } generation || currentTurn <= 0)
        {
            return null;
        }

        var (newPlaces, newThings) = Firsts(world, generation, currentTurn - window, currentTurn);
        return new Reading(turnsPlayed, newPlaces, newThings, window);
    }

    private static (int Places, int Things) Firsts(
        WorldState.Document world,
        long generation,
        long from,
        long currentTurn)
    {
        var places = world.Places.Count(
            place => place.FirstSeen.Gen == generation && place.FirstSeen.Turn > from && place.FirstSeen.Turn <= currentTurn);
        var things = world.Things.Count(
            thing => thing.Gen == generation && thing.FirstSeen > from && thing.FirstSeen <= currentTurn);
        return (places, things);
    }

    /// <summary>
    /// The fact as a sentence, for a prompt that only wants to say it. Null when there is nothing to
    /// say — and the sentence is <see cref="Stretch.Prose"/>, so the prompt and the consult cannot
    /// come to say different things about the same stretch.
    /// </summary>
    public static string? Fact(WorldState.Document? world, long currentTurn, int turnsPlayed, int window = Window) =>
        Look(world, currentTurn, turnsPlayed, window)?.Prose;
}
