using System.Globalization;
using System.Text;

namespace Agent.Notebook;

/// <summary>
/// What is true of a thing at a place, and since when — the state *read* over the document
/// (05_memory.md §8 (`M41`)).
/// <para>
/// Nothing here is stored. The document keeps one row per value the game has printed about a thing,
/// with the first and last turn each was seen on; what is <em>possible</em>, what is <em>current</em>,
/// and what <em>changed</em> are all worked out from those rows each time somebody asks. That is the
/// whole design: current is a question about a fork, and a stored answer is the wrong fork from the
/// moment the world is reloaded.
/// </para>
/// <para>
/// Every value is the game's own words, quoted. Nothing here decides that *"closed and unlocked"*
/// means locked, and nothing converts a value into a flag — the possible states of a thing are
/// exactly the strings the game has printed about it, which is what stops the harness inventing an
/// aspect it would then have to fill in (`M40.2`).
/// </para>
/// </summary>
public static class StateReading
{
    /// <summary>A change the record shows, and whatever the player did in between.</summary>
    /// <param name="From">The value that was observed before.</param>
    /// <param name="To">The value observed at <paramref name="At"/>.</param>
    /// <param name="At">The turn the new value was first seen on.</param>
    /// <param name="By">
    /// The commands typed at that place in between, verbatim, oldest first. Empty is a
    /// **first-class answer**: the world changed and nothing the player typed did it.
    /// </param>
    public sealed record Change(string From, string To, long At, IReadOnlyList<string> By);

    /// <summary>One thing, asked about in one way, as the record has it.</summary>
    public sealed record State(
        string Property,
        IReadOnlyList<string> Values,
        string? Current,
        long? Since,
        long? LastSeen,
        IReadOnlyList<Change> Changes);

    /// <summary>One answer: what a person would read, and the same facts as JSON.</summary>
    public sealed record Reading(string Prose, object Facts);

    /// <summary>
    /// The state of one thing, or of everything the record knows about at one place.
    /// <para>
    /// <paramref name="asked"/> is a room by number or printed name, or null for the room the player
    /// is in; <paramref name="thing"/> narrows it to one thing, by part of its name.
    /// </para>
    /// </summary>
    public static Reading Describe(WorldState.Document world, string? asked, long? roomHere, string? thing)
    {
        var generation = world.Header.Generation;
        var place = asked is { Length: > 0 } wanted
            ? MapReading.Find(world, wanted)
            : roomHere is { } room
                ? world.Places.FirstOrDefault(candidate => candidate.Number == room)
                : null;

        if (place is null)
        {
            return new Reading(
                asked is { Length: > 0 }
                    ? $"Nothing on the record matches \"{asked}\", so there is no state to read for it."
                    : "The record cannot say which room this is, so there is no state to read. Ask "
                        + "about a room by its number or its name.",
                new { asked, found = 0, generation });
        }

        var everything = world.Things.Where(row => row.Place == place.Id).ToList();
        var wantedThing = thing?.Trim();
        var named = wantedThing is { Length: > 0 }
            ? everything.Where(row => row.Name.Contains(wantedThing, StringComparison.OrdinalIgnoreCase)).ToList()
            : everything;

        var here = generation is null ? named : named.Where(row => row.Gen == generation).ToList();
        if (here.Count == 0)
        {
            return new Reading(
                Nothing(world, place, everything, wantedThing, generation),
                new { place = place.Id, room = place.Number, found = 0, generation });
        }

        var things = here
            .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                States = group
                    .GroupBy(row => row.Property, StringComparer.Ordinal)
                    .OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property => Read(world, place, property.Key, [.. property.OrderBy(row => row.FirstSeen)]))
                    .ToList(),
            })
            .ToList();

        var prose = new StringBuilder();
        prose.AppendLine(
            $"Room {place.Number?.ToString(CultureInfo.InvariantCulture) ?? MapReading.Title(place)}"
            + $", as the record has it"
            + (generation is { } gen ? $", generation {gen}:" : ", in a fork the run book cannot name:"));
        foreach (var known in things)
        {
            prose.AppendLine();
            prose.AppendLine($"  {known.Name}");
            foreach (var state in known.States)
            {
                prose.AppendLine(
                    $"    {state.Property}: "
                    + string.Join(
                        " · ",
                        state.Values.Select(value => $"\"{value}\"")));
                prose.AppendLine(
                    state.Current is { } current
                        ? $"      As of turn {state.LastSeen}, it is \"{current}\""
                            + $", first seen at turn {state.Since}."
                        : "      Nothing has been seen of it in this world.");
                foreach (var change in state.Changes)
                {
                    prose.AppendLine(
                        $"      Changed: \"{change.From}\" -> \"{change.To}\" at turn {change.At}"
                        + (change.By.Count == 0
                            ? ", and nothing was typed in between."
                            : $", with {string.Join(", then ", change.By)} typed in between."));
                }
            }
        }

        prose.AppendLine();
        prose.Append(
            "The values are the game's own words, quoted as it printed them, and nothing here decides "
            + "what they mean. A value met again later is on one line with its last sighting beside it, "
            + "so a thing that went back and forth shows the values and the changes between them rather "
            + "than every step. Nothing is carried across a reload: what is true here is what has been "
            + "seen here.");

        // The ledger, and only when nothing in particular was asked about: "what have I already tried
        // here, and what did each one return" is the question that was rebuilt by hand over twenty
        // turns in one room, and every one of these rows was already on the record.
        var tries = wantedThing is { Length: > 0 }
            ? []
            : world.Actions
                .Where(action => action.Place == place.Id)
                .Where(action => generation is null || action.Gen == generation)
                .OrderByDescending(action => action.Turn)
                .ToList();

        if (tries.Count > 0)
        {
            prose.AppendLine();
            prose.AppendLine("What has been tried here, most recent first:");
            foreach (var action in tries.Take(20))
            {
                prose.AppendLine(
                    $"  turn {action.Turn}  {action.Verb} {action.Object}  ->  \"{action.Result}\"");
            }

            if (tries.Count > 20)
            {
                prose.AppendLine($"  and {tries.Count - 20} more before these.");
            }

            prose.Append(
                "Every one of these was typed in **this** room. The words after the arrow are the "
                + "game's own, quoted: a phrase that comes back the same way for a whole class of "
                + "commands is the game brushing something off, and nothing here decides which words "
                + "matter.");
        }

        return new Reading(
            prose.ToString(),
            new
            {
                place = place.Id,
                room = place.Number,
                generation,
                found = things.Count,
                things = things.Select(known => new
                {
                    name = known.Name,
                    states = known.States.Select(state => new
                    {
                        asked_with = state.Property,
                        values = state.Values,
                        current = state.Current,
                        since = state.Since,
                        last_seen = state.LastSeen,
                        changes = state.Changes.Select(change => new
                        {
                            from = change.From,
                            to = change.To,
                            at = change.At,
                            by = change.By,
                        }),
                    }),
                }),
                tried = tries.Take(20).Select(action => new
                {
                    verb = action.Verb,
                    thing = action.Object,
                    said = action.Result,
                    turn = action.Turn,
                }),
            });
    }

    /// <summary>
    /// One property of one thing, read out of its rows.
    /// <para>
    /// The rows are ordered by the turn each value was first seen on, which is the order the game
    /// printed them in. A change is two neighbours with different words; what caused it is whatever
    /// was typed at this thing's place in between, and an empty answer there is the fact that the
    /// world moved on its own.
    /// </para>
    /// </summary>
    private static State Read(
        WorldState.Document world,
        WorldState.Place place,
        string property,
        List<WorldState.Thing> rows)
    {
        var current = rows.OrderByDescending(row => row.LastSeen).ThenBy(row => row.Value, StringComparer.Ordinal).First();

        var changes = new List<Change>();
        // The window a change is measured over: from the change before it, not from the row before
        // it. A value met again later has a `last_seen` on the far side of this change — a door
        // opened, closed and opened again leaves "open" stamped with the *later* turn — and a window
        // drawn from that would be empty and would lose the command that did it.
        var since = 0L;
        for (var i = 0; i + 1 < rows.Count; i++)
        {
            var before = rows[i];
            var after = rows[i + 1];
            if (string.Equals(before.Value, after.Value, StringComparison.Ordinal))
            {
                continue;
            }

            var by = world.Actions
                .Where(action => action.Place == place.Id)
                .Where(action => action.Gen == after.Gen)
                .Where(action => action.Turn > since && action.Turn <= after.FirstSeen)
                // A look is not an actor: what is reported here is what was *typed*, and the
                // observation that found a change is the same kind of act as the one before it — so
                // an evening of examining a door would otherwise read as an evening of opening it.
                // What is *not* filtered is the thing the command named, because the two are often
                // different: the record prints the door, and what the player pulled is the lever.
                .Where(action => CatchupReport.ThingAndAction($"{action.Verb} {action.Object}") is not { } pair
                    || !string.Equals(pair.Action, property, StringComparison.Ordinal))
                .OrderBy(action => action.Turn)
                .Select(action => $"{action.Verb} {action.Object}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            changes.Add(new Change(before.Value, after.Value, after.FirstSeen, by));
            since = after.FirstSeen;
        }

        return new State(
            property,
            [.. rows.Select(row => row.Value).Distinct(StringComparer.Ordinal)],
            current.Value,
            current.FirstSeen,
            current.LastSeen,
            changes);
    }

    /// <summary>
    /// Why there is nothing to say, which is three different facts: nothing has ever been typed at a
    /// thing here, this thing was only known in a world that has been replaced, and this room was
    /// only ever known in one.
    /// </summary>
    private static string Nothing(
        WorldState.Document world,
        WorldState.Place place,
        List<WorldState.Thing> everything,
        string? thing,
        long? generation)
    {
        var name = thing is { Length: > 0 } wanted ? $"\"{wanted}\"" : "that thing";

        if (everything.Count > 0 && generation is { } gen)
        {
            var seen = everything.Where(row => row.Gen != gen).ToList();
            if (seen.Count > 0)
            {
                var fork = seen.Max(row => row.Gen);
                return $"{name} was last seen in generation {fork}, and this is generation {gen}. "
                    + "Nothing about it is carried over: what is true of it here is what has been "
                    + "seen of it here, and nothing has been.";
            }
        }

        if (thing is { Length: > 0 })
        {
            return $"Nothing on the record names {name} at room "
                + $"{place.Number?.ToString(CultureInfo.InvariantCulture) ?? MapReading.Title(place)}. "
                + "A thing only gets here by being named in a command that was typed at it, so a "
                + "thing the story mentioned and nobody touched is not in this answer.";
        }

        return $"Nothing has been typed at anything in room "
            + $"{place.Number?.ToString(CultureInfo.InvariantCulture) ?? MapReading.Title(place)} that "
            + "the record can read as a thing: no `examine`, no `open`, no `take`. What is here is "
            + "what the story printed, and the story's prose is not the harness's to read.";
    }
}
