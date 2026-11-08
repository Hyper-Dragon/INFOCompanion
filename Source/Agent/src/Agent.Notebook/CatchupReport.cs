using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent.Notebook;

/// <summary>
/// The write-up a player needs to come back to a game after an absence (05_memory.md §6 (`M27`–`M36`)).
/// <para>
/// Two halves, and the split is the point. The <em>model</em> writes what the report says: it has
/// read everything the harness has about the game — the notebook, the memories, the turn record,
/// the words the parser refused, the attempts — and it is the only thing in this system that can
/// turn that into prose. The <em>harness</em> owns every byte of the page it is shown in: the tabs,
/// the styling, and the map, which is drawn from a list of moves rather than from coordinates the
/// model chose. A report that has to be legible in an evening months from now must not depend on
/// the model's mood about drawing, and the same reason applies to it as to the notebook: the
/// harness renders, the model writes.
/// </para>
/// <para>
/// It is deliberately <strong>not in the companion's voice</strong> — the requester's own words —
/// so nothing in the page addresses the player as a character, and no line of the persona's reaches
/// it.
/// </para>
/// </summary>
public static partial class CatchupReport
{
    /// <summary>The sections the harness knows, in the order a reader wants them.</summary>
    public static readonly string[] KnownSections =
    [
        "Where we are",
        // The companion writes this one. It is the only part of the page that is *about people*, and
        // the record cannot answer it — a name, who somebody wanted what from, what they will do if
        // you do that — so it is the model's, and it is prose (B5, decided 2026-09-21).
        CharactersSection,
        "What is left to do",
        "What we have solved",
        "The map",
        // These two are the record's, not the model's. One is what the game itself said was in the
        // player's hands, the other is every turn that happened, and asking a model to retell either
        // is asking it to get them subtly wrong in a document somebody reads months later.
        EventsSection,
        ItemsSection,
        "Worth remembering",
    ];

    /// <summary>The heading the map is drawn from, spelled once.</summary>
    public const string MapSection = "The map";

    /// <summary>Who is in this game and what they want — the only new section the model writes.</summary>
    public const string CharactersSection = "Characters";

    /// <summary>What happened, turn by turn, rendered from the record rather than retold.</summary>
    public const string EventsSection = "Events";

    /// <summary>What the game itself last said the player was carrying, quoted as it said it.</summary>
    public const string ItemsSection = "Items";

    /// <summary>
    /// How many turns the Events tab shows before it summarises the rest.
    /// <para>
    /// A long evening is three hundred turns and change, and a page that has to be legible in a year
    /// is a page somebody scrolls. The most recent ones are the ones a returning player is looking
    /// for; the line at the top says how many came before, so nothing is silently missing.
    /// </para>
    /// </summary>
    public const int EventLimit = 200;

    /// <summary>
    /// The em dash this page's prose is written with, spelled as an escape because everything that
    /// reaches the document above ASCII goes through `Escape` anyway and a literal here would be
    /// one more place for an encoding to drift.
    /// </summary>
    private const string EM = "\u2014";

    /// <summary>The section sign, for citing a rule by number.</summary>
    private const string SEC = "\u00a7";

    /// <summary>
    /// The commands a player types to ask the game what they are carrying.
    /// <para>
    /// Matched against the <em>command</em> and never against the screen, which is the difference
    /// between a table of verbs — the same kind of table this harness uses everywhere to route a
    /// line — and reading the story's prose, which it must never do. A game that spells it
    /// differently simply has no such turn on the record, and the tab says so.
    /// </para>
    /// </summary>
    private static readonly string[] InventoryCommands =
        ["inventory", "i", "inv", "carrying", "what am i carrying"];

    /// <summary>
    /// The words a player types to go somewhere, and which way that is on the page.
    /// <para>
    /// Read from the <em>command</em>, like <see cref="InventoryCommands"/> and never from the
    /// screen. This is what the map is joined by. The record carries the interpreter's own
    /// location on a minority of turns — in the live state on 2026-09-22, twelve of
    /// Wishbringer's forty-seven, two places in all, and none whatever of the other four games on
    /// the same machine — so a map keyed on it alone has almost nothing to draw, which is
    /// what the first version of this shipped. The direction that was typed is on every move
    /// there has ever been.
    /// </para>
    /// <para>
    /// Up, down, in and out are not compass points and are not folded into one: a well house, a
    /// tree and a grating are each reached by one of them, and calling that north would put two
    /// rooms on one disc. They are placed by the fallback (see <see cref="Diagram"/>) and the
    /// arrow carries the word, which is where a reader looks for it anyway.
    /// </para>
    /// </summary>
    internal static readonly Dictionary<string, bool> MovementWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = true, ["n"] = true, ["south"] = true, ["s"] = true,
        ["east"] = true, ["e"] = true, ["west"] = true, ["w"] = true,
        ["northeast"] = true, ["ne"] = true, ["northwest"] = true, ["nw"] = true,
        ["southeast"] = true, ["se"] = true, ["southwest"] = true, ["sw"] = true,
        ["up"] = true, ["u"] = true, ["climb"] = true, ["down"] = true, ["d"] = true,
        ["descend"] = true, ["in"] = true, ["enter"] = true, ["out"] = true,
        ["exit"] = true, ["leave"] = true,
    };

    /// <summary>
    /// What the player's own line did with a thing, in the past tense, for the Items tab.
    /// <para>
    /// A table of verbs over the <em>command</em>, which is the only place a list of things can
    /// come from without reading the story: something the story mentioned in passing and nobody
    /// ever typed at is not on that page, and the tab says so rather than pretending. The verb
    /// says what happened to it, because `take` is not `drop` and `give` is not `put`, and the
    /// turn is beside each one so the line itself can be read.
    /// </para>
    /// </summary>
    internal static readonly Dictionary<string, string> ThingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["take"] = "took", ["get"] = "took", ["grab"] = "took", ["carry"] = "took",
        ["drop"] = "put down", ["put"] = "put away", ["give"] = "gave away",
        ["offer"] = "offered", ["show"] = "showed", ["open"] = "opened",
        ["close"] = "closed", ["shut"] = "closed", ["lock"] = "locked",
        ["unlock"] = "unlocked", ["read"] = "read", ["examine"] = "looked at",
        ["x"] = "looked at", ["look"] = "looked at", ["search"] = "searched",
        ["feel"] = "felt", ["touch"] = "touched", ["pet"] = "petted", ["rub"] = "rubbed",
        ["wear"] = "wore", ["remove"] = "took off", ["eat"] = "ate", ["drink"] = "drank",
        ["light"] = "lit", ["burn"] = "burned", ["cut"] = "cut", ["break"] = "broke",
        ["throw"] = "threw", ["push"] = "pushed", ["pull"] = "pulled", ["fill"] = "filled",
        ["empty"] = "emptied", ["tie"] = "tied", ["untie"] = "untied", ["buy"] = "bought",
        ["attack"] = "attacked", ["kill"] = "attacked", ["repair"] = "repaired",
        ["turn"] = "turned", ["move"] = "moved", ["wave"] = "waved", ["shake"] = "shook",
        ["knock"] = "knocked on", ["listen"] = "listened to", ["wind"] = "wound",
    };

    /// <summary>What the report knew when it was written, and what it cost.</summary>
    /// <param name="Stem">The game it is about.</param>
    /// <param name="GeneratedAt">When the pass wrote it, UTC.</param>
    /// <param name="Model">Which model wrote it, as the endpoint reported.</param>
    /// <param name="Turns">How many turns the record held when it was written.</param>
    /// <param name="WriteUpCostUsd">
    /// What this pass cost, from the same pricing the rest of the log uses. The pass's own number:
    /// useful in a log, and misleading next to `Turns` — see <paramref name="SessionCostUsd"/>.
    /// </param>
    /// <param name="SessionCostUsd">
    /// What the evening has cost altogether.
    /// <para>
    /// Both numbers, because the page carried only the first and read as though it were the second:
    /// on 2026-09-21 the Aide-mémoire said *"349 turns on the record · written by deepseek-flash on
    /// its own pass · 0.00204 USD"*, which is one model call's cost presented in the place a reader
    /// looks for the price of the evening. The session figure is the one the page shows; the pass
    /// figure stays in the facts file, where somebody working out what a write-up costs wants it.
    /// </para>
    /// </param>
    public sealed record ReportFacts(
        string Stem,
        DateTimeOffset GeneratedAt,
        string Model,
        int Turns,
        decimal WriteUpCostUsd,
        decimal SessionCostUsd);

    /// <summary>
    /// The whole page: one string of HTML, no external anything.
    /// <para>
    /// Self-contained on purpose. It is written into the state directory and served by whatever is
    /// in front of that directory, so a page that needed a stylesheet from somewhere else would be
    /// a page that broke the first time it was read from a laptop in a kitchen.
    /// </para>
    /// </summary>
    public static string Render(
        string stem,
        string markdown,
        ReportFacts facts,
        IReadOnlyList<ObservationLog.RecordedTurn>? record = null,
        WorldState.Document? world = null)
    {
        var sections = Split(markdown);
        if (sections.Count == 0)
        {
            // A report with nothing in it still has to be a page: an empty model reply would
            // otherwise produce a document with no tabs at all, which reads as a broken page rather
            // than as a report that had nothing to say.
            sections.Add(("The report", string.Empty));
        }

        // The record's own two sections, added after the model's and never asked of it (B5), and
        // then the whole lot put in the order a reader wants them — which is the same ordering
        // `Split` applies, so the two record sections land where the heading list puts them:
        // after the map, before the notes.
        sections.AddRange(HarnessSections(record, world));
        sections = [.. sections.OrderBy(section => KnownIndex(section.Heading))];

        var tabs = new StringBuilder();
        var panels = new StringBuilder();

        for (var index = 0; index < sections.Count; index++)
        {
            var (heading, body) = sections[index];
            var id = $"tab-{index}";
            var active = index == 0 ? " aria-selected=\"true\"" : string.Empty;
            tabs.AppendLine(
                $"      <button class=\"tab\" role=\"tab\" id=\"{id}\" data-panel=\"panel-{index}\"{active}>"
                + $"{Escape(heading)}</button>");

            var html = heading.Equals(MapSection, StringComparison.OrdinalIgnoreCase)
                ? Map(body, record, world)
                : Prose(body);
            panels.AppendLine(
                $"      <section class=\"panel\" role=\"tabpanel\" id=\"panel-{index}\" aria-labelledby=\"{id}\""
                + (index == 0 ? string.Empty : " hidden")
                + ">");
            panels.AppendLine(html);
            panels.AppendLine("      </section>");
        }

        var title = Escape($"Aide-mémoire — {stem}");
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>
            {{Style}}
            </style>
            </head>
            <body>
              <main class="terminal">
                <header class="masthead">
                  <p class="eyebrow">INFOCOMPANION · two friends, one keyboard, 1983</p>
                  <h1>{{Escape(stem)}}</h1>
                  <p class="meta"><a href="/">the index</a> · <a href="/games">the games</a> ·
                  <a href="/term">the terminal</a></p>
                  <p class="meta">
            {{Escape(facts.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}} UTC
                    · {{facts.Turns.ToString(CultureInfo.InvariantCulture)}} turns on the record
                    · written by {{Escape(facts.Model)}} on its own pass
                    · the evening stands at {{facts.SessionCostUsd.ToString("0.00", CultureInfo.InvariantCulture)}} USD
                  </p>
                </header>
                <nav class="tabs" role="tablist">
            {{tabs.ToString().TrimEnd()}}
                </nav>
                <div class="panels">
            {{panels.ToString().TrimEnd()}}
                </div>
                <footer class="colophon">
                  Written by the companion from everything it holds about this game. It is not in its
                  own voice: this is a document about the evening, not a message from the other player.
                </footer>
              </main>
              <script>
            (function () {
              var tabs = Array.prototype.slice.call(document.querySelectorAll('.tab'));
              function show(index) {
                tabs.forEach(function (tab, i) {
                  tab.setAttribute('aria-selected', i === index ? 'true' : 'false');
                  var panel = document.getElementById(tab.getAttribute('data-panel'));
                  if (panel) { panel.hidden = i !== index; }
                });
              }
              tabs.forEach(function (tab, i) {
                tab.addEventListener('click', function () { show(i); });
              });
            })();
              </script>
            </body>
            </html>
            """;
    }

    /// <summary>Write the page and its facts, and say where they went.</summary>
    public static (string Html, string Facts) Write(
        string stateDirectory,
        string stem,
        string markdown,
        ReportFacts facts,
        IReadOnlyList<ObservationLog.RecordedTurn>? record = null)
    {
        // The document is the one walk (`M40`), and it is current: the loop keeps it in step as turns
        // land. `Read` returns null for a state directory that holds no document yet — a session in
        // which no game has been opened — and the page falls back to the record's own walk rather
        // than drawing nothing.
        var world = WorldState.Read(stateDirectory, stem);
        var directory = Path.Combine(stateDirectory, "catchup");
        Directory.CreateDirectory(directory);
        var html = Path.Combine(directory, $"{stem}.html");
        var json = Path.Combine(directory, $"{stem}.json");
        File.WriteAllText(html, Render(stem, markdown, facts, record, world));
        File.WriteAllText(
            json,
            JsonSerializer.Serialize(
                new
                {
                    stem,
                    generated_at = facts.GeneratedAt,
                    model = facts.Model,
                    turns = facts.Turns,
                    write_up_cost_usd = facts.WriteUpCostUsd,
                    session_cost_usd = facts.SessionCostUsd,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        return (html, json);
    }

    /// <summary>When a game's report was last written, if it ever was — read by the browser's list.</summary>
    public static DateTimeOffset? GeneratedAt(string stateDirectory, string stem)
    {
        var path = Path.Combine(stateDirectory, "catchup", $"{stem}.json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("generated_at", out var when)
                && when.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A half-written file is a report with no date rather than a broken page.
            return null;
        }
    }

    /// <summary>
    /// The report's own markdown, split into sections.
    /// <para>
    /// The five the harness knows come first, in the order a reader wants them, and anything else
    /// the model wrote is kept as a section of its own after them. Kept rather than dropped: the
    /// model reading the record is the one thing here that knows what matters about a particular
    /// game, and a section it invented is a section it thought was worth a tab.
    /// </para>
    /// </summary>
    private static List<(string Heading, string Body)> Split(string markdown)
    {
        var found = new List<(string Heading, string Body)>();
        var heading = string.Empty;
        var body = new StringBuilder();

        void Close()
        {
            if (heading.Length == 0 && body.ToString().Trim().Length == 0)
            {
                return;
            }

            found.Add((heading.Length == 0 ? "The report" : heading, body.ToString().Trim()));
            body.Clear();
        }

        foreach (var raw in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                heading = line[3..].Trim();
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                // A title is not a section: the page has its own masthead.
                continue;
            }

            body.AppendLine(line);
        }

        Close();

        return [.. found
            .OrderBy(section => KnownIndex(section.Heading))
            .ThenBy(section => section.Heading, StringComparer.OrdinalIgnoreCase)];
    }

    private static int KnownIndex(string heading)
    {
        for (var i = 0; i < KnownSections.Length; i++)
        {
            if (KnownSections[i].Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return KnownSections.Length;
    }

    /// <summary>
    /// The two sections the harness fills from the record instead of asking the model for them (B5).
    /// <para>
    /// Both are *data*: one is what the game itself said was in the player's hands, the other is
    /// every turn that happened. A model retelling either is a model getting them subtly wrong in a
    /// document somebody reads months later, which is the same reason the map is drawn by the
    /// harness rather than by the model's hand.
    /// </para>
    /// <para>
    /// A section is left out entirely when the record has nothing for it — there is no record at
    /// all — because an empty tab claims the harness looked and found nothing, and a page that says
    /// nothing is better than a page that misleads. Both of them are built from turns rather than
    /// from an answer to a question, so neither waits on a particular command having been typed.
    /// </para>
    /// </summary>
    private static List<(string Heading, string Body)> HarnessSections(
        IReadOnlyList<ObservationLog.RecordedTurn>? record,
        WorldState.Document? world)
    {
        var sections = new List<(string Heading, string Body)>();
        if (record is null || record.Count == 0)
        {
            return sections;
        }

        sections.Add((EventsSection, Events(record, world)));
        sections.Add((ItemsSection, Items(record)));

        return sections;
    }

    /// <summary>
    /// How many clauses one place's paragraph carries before the rest of its turns are counted.
    /// </summary>
    public const int EventClauses = 12;

    /// <summary>One thing that happened, and what the game said about it.</summary>
    private sealed record Clause(string Words, string? Why, bool Move, int Count);

    /// <summary>
    /// What happened, told one place at a time: a paragraph per place, in the order the evening
    /// reached them, ending with the way out of it.
    /// <para>
    /// A row per turn is a log, and a log is not what a returning player wants: they want the
    /// evening as somebody would tell it — *in the cottage we read the signpost and petted the
    /// poodle, tried north and the poodle would not let us, then went west to the hilltop* — because
    /// the sequence is the thing that was lost, not the individual keystrokes. So the turns are read
    /// through the same two tables the rest of the page is built from, rolled up by place, and the
    /// joins carry the reason: a move that moved nothing is followed by the game's own words for why,
    /// which is what makes the next thing tried look inevitable rather than arbitrary.
    /// </para>
    /// <para>
    /// The shapes a clause takes, in the order they are asked about: a move is *went north to
    /// Hilltop* and it ends the paragraph; something done with a thing is *took the leaflet*; a verb
    /// that is a whole act on its own is *looked around*; and anything else is quoted as the attempt
    /// it was, because the page does not pretend to know what `xyzzy` did. Anything the story said
    /// with nobody's hand on the keyboard — the licence, a restore's acknowledgement, a scene
    /// change — stands as a note of its own, since it belongs to no place.
    /// </para>
    /// <para>
    /// Two roll-ups do the work. A thing done more than once in one place is one clause with a
    /// count: five examinations of the same poodle are one fact about an evening. And the game's own
    /// words are kept exactly where they answer a question the narrative asks, which is what makes
    /// it a story rather than a summary — *we tried north, then we went west* is a summary of
    /// nothing. What the record holds is in any debug bundle, every turn of it as it was typed.
    /// </para>
    /// </summary>
    private static string Events(IReadOnlyList<ObservationLog.RecordedTurn> record, WorldState.Document? world)
    {
        var observed = Shows(record, world);
        var shown = record.Count > EventLimit
            ? record.Skip(record.Count - EventLimit).ToList()
            : [.. record];
        var offset = record.Count - shown.Count;

        var text = new StringBuilder();
        text.AppendLine(
            "What happened, told one place at a time: each paragraph is what was done while the "
            + "player was there and then the way out of it, read through the same two tables the rest "
            + "of the page is built from " + EM + " so `take leaflet` reads as *took the leaflet*. Where "
            + "the game answered, its own words are quoted, because that is the reason the next thing "
            + "tried was the thing that was tried. Doing the same thing three times is said once, with "
            + "the count, and the record itself is in any debug bundle, every turn of it exactly as it "
            + "was typed.");
        if (shown.Count < record.Count)
        {
            text.AppendLine();
            text.AppendLine(
                $"({shown.Count} most recent of {record.Count} turns " + EM + " the earlier ones are in the "
                + "record itself, which is in any debug bundle.)");
        }

        text.AppendLine();

        var clauses = new List<Clause>();
        var here = Place(observed, offset);
        var counted = 0;

        void Add(string words, string? why, bool move)
        {
            if (clauses.Count >= EventClauses)
            {
                counted++;
                return;
            }

            if (!move)
            {
                var seen = clauses.FindIndex(
                    clause => !clause.Move && clause.Words == words && clause.Why == why);
                if (seen >= 0)
                {
                    clauses[seen] = clauses[seen] with { Count = clauses[seen].Count + 1 };
                    return;
                }
            }

            clauses.Add(new Clause(words, why, move, 1));
        }

        void Close()
        {
            if (clauses.Count == 0)
            {
                counted = 0;
                return;
            }

            if (counted > 0)
            {
                clauses.Add(new Clause($"did {counted} more thing{(counted == 1 ? string.Empty : "s")} here", null, false, 1));
                counted = 0;
            }

            text.AppendLine(Paragraph(here, clauses));
            text.AppendLine();
            clauses.Clear();
        }

        for (var index = 0; index < shown.Count; index++)
        {
            var turn = shown[index];
            var typed = turn.Input.Trim();
            var said = Gist(turn.Output);
            var at = Place(observed, offset + index);
            var before = Place(observed, offset + index - 1);
            var moved = at is not null && !string.Equals(at, before, StringComparison.Ordinal);

            if (typed.Length == 0)
            {
                // Nobody's hand was on the keyboard: the story talking to itself. It belongs to no
                // place and cannot be rolled up into one, so it stands as a note of its own.
                Close();
                text.AppendLine(said.Length == 0
                    ? "The story went on by itself for a moment."
                    : $"The story spoke on its own: _{Clip(said, 120)}_");
                text.AppendLine();
                here = at ?? here;
                continue;
            }

            if (Direction(typed) is { } way)
            {
                Add(moved ? MoveClause(way, at) : TryClause(way), moved ? null : said, moved);
                if (moved)
                {
                    Close();
                    here = at;
                }

                continue;
            }

            if (ThingAndAction(typed) is { } done)
            {
                Add($"{done.Action} {Article(done.Thing)}", null, false);
                continue;
            }

            if (typed.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var whole]
                && WholeLineWords.TryGetValue(whole, out var doneAlone))
            {
                Add(doneAlone, null, false);
                continue;
            }

            Add($"tried `{typed}`", said, false);
        }

        Close();

        return text.ToString();
    }

    /// <summary>
    /// One place's paragraph: the clauses in the order they were done, joined into one sentence.
    /// </summary>
    private static string Paragraph(string? where, List<Clause> clauses)
    {
        var body = new StringBuilder();
        for (var index = 0; index < clauses.Count; index++)
        {
            var clause = clauses[index];
            var words = clause.Count switch
            {
                1 => clause.Words,
                2 => $"{clause.Words} twice",
                _ => $"{clause.Words} {clause.Count} times",
            };
            if (clause.Why is { Length: > 0 } why)
            {
                words = $"{words} {EM} _{Clip(TrimMarks(why), 90)}_";
            }

            if (index > 0)
            {
                body.Append(index == clauses.Count - 1 ? (clause.Move ? ", then " : " and ") : ", ");
            }

            body.Append(words);
        }

        return where is null ? $"We {body}." : $"In **{where}** we {body}.";
    }

    /// <summary>
    /// A move as the narrative says it: *went north to Hilltop*, *climbed up to Hilltop*, *went in to
    /// the Post Office*. A bearing needs no help; a way that is a verb does, because "went enter" is
    /// the keyboard talking.
    /// </summary>
    private static string MoveClause(string way, string? to) =>
        $"{(MoveWords.TryGetValue(way, out var how) ? how : $"went {way} to")} **{to}**";

    /// <summary>A move that moved nothing: *tried north*, *tried to enter*.</summary>
    private static string TryClause(string way) =>
        $"tried {(TryWords.TryGetValue(way, out var which) ? which : way)}";

    /// <summary>
    /// A quoted reply with the sentence's own mark taken off the end, because the page's sentence
    /// supplies one: the game's words are quoted inside the narrative, so they are a clause of it
    /// rather than a sentence of their own.
    /// </summary>
    private static string TrimMarks(string said)
    {
        var quote = said.EndsWith('"') ? "\"" : string.Empty;
        var bare = quote.Length == 0 ? said : said[..^1];
        return bare.TrimEnd('.', '!', '?') + quote;
    }

    /// <summary>The ways that are a verb rather than a bearing, in the narrative's own words.</summary>
    private static readonly Dictionary<string, string> MoveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["in"] = "went in to", ["enter"] = "went in to", ["out"] = "went out to",
        ["exit"] = "left by the way out to", ["leave"] = "left for",
        ["up"] = "climbed up to", ["u"] = "climbed up to", ["climb"] = "climbed to",
        ["down"] = "climbed down to", ["d"] = "climbed down to", ["descend"] = "climbed down to",
    };

    /// <summary>The same ways, for a move that moved nothing.</summary>
    private static readonly Dictionary<string, string> TryWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["in"] = "to go in", ["enter"] = "to enter", ["out"] = "to go out",
        ["exit"] = "to leave by the way out", ["leave"] = "to leave",
        ["up"] = "to climb up", ["u"] = "to climb up", ["climb"] = "to climb",
        ["down"] = "to climb down", ["d"] = "to climb down", ["descend"] = "to climb down",
    };

    /// <summary>
    /// Where the player was after the turn at this index, as the graph read it, or null when the
    /// record said nothing about it. Out of range is null: the first turn has no turn before it.
    /// </summary>
    private static string? Place(Observed observed, int index) =>
        index >= 0 && index < observed.Here.Count && observed.Here[index] is { } at
            ? observed.Places[at]
            : null;

    /// <summary>
    /// A thing as it was typed, with an article in front of it when the player did not use one: the
    /// Events tab is prose, and *looked at poodle* is a parser talking rather than a friend telling
    /// you what happened. A capital is left alone, because a name does not take one.
    /// </summary>
    private static string Article(string thing) =>
        thing.Length > 1 && char.IsUpper(thing[0])
            ? thing
            : thing.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                || thing.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
                || thing.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
                || thing.StartsWith("my ", StringComparison.OrdinalIgnoreCase)
                ? thing
                : $"the {thing}";

    /// <summary>
    /// A thing without its article, for the Items tab: *the poodle* and *poodle* are one thing on
    /// that page, and a label reads better than a phrase.
    /// </summary>
    private static string Bare(string thing) =>
        thing.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? thing[4..]
        : thing.StartsWith("an ", StringComparison.OrdinalIgnoreCase) ? thing[3..]
        : thing.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ? thing[2..]
        : thing.StartsWith("my ", StringComparison.OrdinalIgnoreCase) ? thing[3..]
        : thing;

    /// <summary>
    /// The lines that are a whole act on their own, for the Events tab: a verb with no thing after
    /// it, in the past tense. `look` is not an attempt at a word — it is looking around — and a page
    /// that quotes it as `` `look` `` is a page reading the keyboard back to the player.
    /// <para>
    /// The list is deliberately short and the fallback is honest: anything not here is quoted as the
    /// attempt it was, which is what an unknown verb deserves.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> WholeLineWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["look"] = "looked around", ["l"] = "looked around",
        ["listen"] = "listened", ["wait"] = "waited", ["z"] = "waited",
        ["inventory"] = "checked what was being carried",
        ["i"] = "checked what was being carried",
        ["inv"] = "checked what was being carried",
        ["carrying"] = "checked what was being carried",
        ["score"] = "asked for the score", ["jump"] = "jumped", ["pray"] = "prayed",
        ["smell"] = "sniffed the air", ["taste"] = "tasted the air", ["wake"] = "woke up",
        ["sing"] = "sang", ["swim"] = "swam", ["yes"] = "said yes", ["no"] = "said no",
    };

    /// <summary>
    /// The things the player's own lines named, and what was done with them.
    /// <para>
    /// Read off the <em>commands</em> and never off the story, which is the same rule the map and
    /// the events follow: a thing the game mentioned in passing and nobody ever typed at is not
    /// here, and the tab says so rather than pretending to a list it cannot have. What it does have
    /// is better than a list of what is in somebody's hands: for each thing, everything that was
    /// done with it in order, with the turn beside each one, so *took it, then gave it away* is
    /// readable at a glance.
    /// </para>
    /// <para>
    /// The game's own answer to the inventory is kept underneath, quoted as it was given. It is the
    /// only thing on this page that knows what is in the player's hands *now*, and it is a snapshot
    /// of the turn it was asked on: a list of things done says what happened, and neither of them
    /// says what the other does.
    /// </para>
    /// </summary>
    private static string Items(IReadOnlyList<ObservationLog.RecordedTurn> record)
    {
        var done = new List<(int Turn, string Thing, string Action)>();
        foreach (var turn in record)
        {
            if (ThingAndAction(turn.Input.Trim()) is { } thing)
            {
                done.Add((turn.Turn, thing.Thing, thing.Action));
            }
        }

        var text = new StringBuilder();
        text.AppendLine(
            "The things somebody typed at on the record, and what was done with each: oldest "
            + "thing first, and within a thing, in the order it happened. Read off the commands "
            + "rather than off the story, so something the game mentioned and nobody ever handled "
            + "is not here " + EM + " and the turn is beside each action, so the line itself can be read.");

        if (done.Count == 0)
        {
            text.AppendLine();
            text.AppendLine("- _(nobody typed at anything on the record)_");
        }
        else
        {
            text.AppendLine();
            foreach (var thing in done
                .GroupBy(entry => Bare(entry.Thing), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Min(entry => entry.Turn)))
            {
                var actions = string.Join(
                    ", ",
                    thing.Select(entry => $"{entry.Action} (turn {entry.Turn})"));
                text.AppendLine($"- **{thing.Key}** {EM} {actions}");
            }
        }

        if (TheLastTimeTheyLooked(record) is { } looked)
        {
            text.AppendLine();
            text.AppendLine("### The game's own last list");
            text.AppendLine();
            text.AppendLine(
                $"Asked at turn {looked.Turn} of {record.Count}, and quoted rather than summarised: "
                + "what is in somebody's hands is the thing a summary gets quietly wrong, and the "
                + "answer was true at that turn " + EM + " whatever has happened since is above.");
            text.AppendLine();

            var lines = looked.Output
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            if (lines.Count == 0)
            {
                // The command was typed and the game answered with nothing at all, which is a fact
                // about that turn rather than a failure of this page.
                text.AppendLine("- _(the game said nothing back to that one)_");
            }
            else
            {
                foreach (var line in lines)
                {
                    text.AppendLine($"- {line}");
                }
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Which way a typed line goes, or null when it does not go anywhere.
    /// <para>
    /// The first word, plus the word after `go`. Nothing else is looked at: `look in the case`
    /// starts with a verb, not a direction, and a line with a direction in the middle of it is a
    /// line about something else.
    /// </para>
    /// </summary>
    internal static string? Direction(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length && i < 2; i++)
        {
            if (MovementWords.ContainsKey(words[i]))
            {
                return words[i].ToLowerInvariant();
            }

            if (i == 0 && !words[0].Equals("go", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// The thing a line is about and what was done with it, when the line is one of the shapes
    /// this knows. Null for everything else, which is most lines: one it cannot read is left to
    /// the Events tab rather than guessed at.
    /// <para>
    /// The little words are what makes this a table rather than a reading. `knock on the door`,
    /// `look in the case` and `listen to the radio` put a preposition between the verb and the
    /// thing, and `give envelope to woman` and `put lamp in case` put a whole destination after
    /// it — so the thing is what stands between the verb and the first preposition, exactly as it
    /// was typed. Nothing is inferred about what any of it is.
    /// </para>
    /// </summary>
    internal static (string Thing, string Action)? ThingAndAction(string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0 || !ThingWords.TryGetValue(words[0], out var action))
        {
            return null;
        }

        var rest = words.Skip(1).ToList();
        if (rest.Count > 0 && Preposition(rest[0]))
        {
            // The preposition belongs to the verb: "look in the case" is a look at a case.
            rest.RemoveAt(0);
        }

        var ends = rest.FindIndex(Preposition);
        if (ends >= 0)
        {
            rest = [.. rest.Take(ends)];
        }

        // As it was typed, article and all: the Events tab reads as prose, and the Items tab takes
        // the article off for its label (`Bare`).
        var thing = string.Join(' ', rest).Trim();
        return thing.Length == 0 ? null : (thing, action);
    }

    private static bool Preposition(string word) =>
        word is "to" or "at" or "in" or "into" or "on" or "onto" or "under" or "behind" or "with";

    /// <summary>The most recent turn whose <em>command</em> was a request for the inventory.</summary>
    private static ObservationLog.RecordedTurn? TheLastTimeTheyLooked(
        IReadOnlyList<ObservationLog.RecordedTurn> record)
    {
        for (var i = record.Count - 1; i >= 0; i--)
        {
            var typed = record[i].Input.Trim().ToLowerInvariant().TrimEnd('.', '!');
            if (InventoryCommands.Contains(typed))
            {
                return record[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The first line of a turn's output, as one short line for a list.
    /// <para>
    /// Trimmed of the markdown marks rather than escaped for them: this is a gist for a reader
    /// scrolling a timeline, and a room whose description opens with an asterisk is a room, not the
    /// start of an emphasis run that swallows the next four hundred characters.
    /// </para>
    /// </summary>
    internal static string Gist(string output)
    {
        var first = output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;

        var flat = string.Join(' ', first.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Clip(flat.Replace("`", "'").Replace("*", string.Empty).Replace("_", string.Empty), 120);
    }

    /// <summary>Ordinary prose: paragraphs, bullets and headings below the tab's own.</summary>
    private static string Prose(string body)
    {
        var html = new StringBuilder();
        var inList = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            var bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                if (!inList)
                {
                    html.AppendLine("        <ul>");
                    inList = true;
                }

                html.AppendLine($"          <li>{Inline(bullet.Groups[1].Value)}</li>");
                continue;
            }

            if (inList)
            {
                html.AppendLine("        </ul>");
                inList = false;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                html.AppendLine($"        <h3>{Inline(line[4..])}</h3>");
                continue;
            }

            html.AppendLine($"        <p>{Inline(line)}</p>");
        }

        if (inList)
        {
            html.AppendLine("        </ul>");
        }

        return html.Length == 0
            ? "        <p class=\"empty\">Nothing on this one.</p>"
            : html.ToString().TrimEnd();
    }

    /// <summary>
    /// The map, drawn from the turns the core watched (B4).
    /// <para>
    /// The drawing is the harness's and the <em>facts</em> are the record's: every arrow is one move
    /// the core saw happen, and it is drawn from two turns — the line that was typed, and the place
    /// the story then printed. Nothing is asked of the model for it, which is the whole point: the
    /// 2026-09-21 report came out as prose fragments and repeated names because a diagram keyed by
    /// room <em>name</em> cannot tell Wishbringer's two worlds apart and depends on the model
    /// bothering to write a strict move list. A fingerprint can, and the record always has one.
    /// </para>
    /// <para>
    /// Three things it deliberately does not claim. It is not a topology: the core witnesses a move
    /// and never decides that two descriptions are the same room, so two fingerprints stay two
    /// circles even when they share a name — and where a name repeats, the hash that tells them
    /// apart is printed beside it. It is not complete: it starts where the record starts. And it is
    /// not the companion's reading, which is prose and is kept below the diagram rather than thrown
    /// away.
    /// </para>
    /// </summary>
    private static string Map(string body, IReadOnlyList<ObservationLog.RecordedTurn>? record,
        WorldState.Document? world)
    {
        var observed = Shows(record, world);
        if (observed.Places.Count == 0)
        {
            // No record at all, or a game that has printed no place yet: the section is still worth
            // reading as the companion wrote it, and an empty diagram would lose it.
            return Prose(body);
        }

        var html = new StringBuilder();
        html.AppendLine(
            "        <p class=\"map-note\">Drawn from the world document and not from the write-up's own "
            + "prose: each arrow is a move the core watched happen — the line that was typed, and "
            + "the place the story printed afterwards. North is up. Two places the interpreter gave "
            + "different object numbers to are two circles whatever they are called (02_game_boundary.md " + SEC + "6); "
            + "where it gives no number at all the place is the name the story printed, which is a "
            + "reading and is why the number is shown when a name repeats. A move with no side to it "
            + "" + EM + " `up`, `enter the window` " + EM + " is drawn beside the room it was made from, and the "
            + "arrow carries the word that did it.</p>");
        html.AppendLine();
        html.AppendLine(Diagram(observed));

        if (observed.Tried.Count > 0)
        {
            html.AppendLine();
            html.AppendLine(
                "        <h3>Tried, and it moved nothing</h3>\n        <p>A line that left the story in "
                + "the same place is an exit that is there and is not open — the poodle in the road. "
                + "These are what the record says about them.</p>");
            html.AppendLine(Prose(string.Join(
                '\n',
                observed.Tried.Select(tried =>
                    $"- **{observed.Places[tried.At]}** — {tried.Tried} ({tried.Count} "
                    + (tried.Count == 1 ? "try" : "tries") + ")"))));
        }

        if (body.Trim().Length > 0)
        {
            html.AppendLine();
            html.AppendLine("        <h3>As the companion reads it</h3>");
            html.AppendLine(Prose(body));
        }

        return html.ToString().TrimEnd();
    }

    /// <summary>
    /// What the record shows: the places, the moves between them, and the moves that moved nothing.
    /// <para>
    /// `Way` is the direction of a move when every line that made it agrees on one, and null
    /// otherwise — which is what the picture is laid out by, and what the Events tab says the
    /// player did.
    /// </para>
    /// <para>
    /// `Here` is where the player was after each turn of the record, in the order the record has
    /// them, or null before the first place the record knows about. It is the same run of turns the
    /// graph was built from, so a place index in it is an index into `Places`: the Events tab reads
    /// both ends of a move out of it rather than guessing from the words.
    /// </para>
    /// </summary>
    private sealed record Observed(
        List<string> Places,
        List<(int From, int To, string Note, string? Way)> Edges,
        List<(int At, string Tried, int Count)> Tried,
        List<int?> Here);

    /// <summary>
    /// The graph the record holds.
    /// <para>
    /// A place is the interpreter's own <b>location</b> object number wherever it gives one (B4),
    /// and where it does not it is the name the story printed on the turn the move was made. Both
    /// halves are needed and the first version of this had only the first:
    /// </para>
    /// <para>
    /// <list type="bullet">
    /// <item>a <b>fingerprint</b> is an index for finding similar turns and never an identity — the
    /// same room prints differently between visits, and a turn that did not move prints something
    /// too. Keyed by fingerprint this made twenty-two places out of a handful of rooms, with
    /// `Taken.` among the names;</item>
    /// <item>the interpreter's <b>location</b> is the story's own answer to *where is the player*,
    /// which makes it the one identity in the record that is not a reading of the prose. It is
    /// also on a minority of turns in practice — in the live state on 2026-09-22, twelve of
    /// Wishbringer's forty-seven and none of the other four games — so a map keyed on it alone
    /// draws two circles and calls it an evening;</item>
    /// <item>the <b>name</b> the story printed is the fallback, and it is a reading: a name is not
    /// an identity, two rooms can share one, and a room can be renamed. It is only consulted on a
    /// turn whose line actually goes somewhere, which is what keeps `Taken.` out of the names, and
    /// only a line that is not a sentence counts as a name at all (see <see cref="RoomName"/>). A
    /// place first met without one is labelled with its number and takes the first title that
    /// arrives afterwards.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The rules that fall out of that, in the order they are applied: a number beats a name; a
    /// name joins a place that has no number of its own, so a game whose interpreter reports the
    /// location on some turns and not others does not grow a second circle for every room walked
    /// back into; and two places the interpreter gave different numbers to stay two places
    /// whatever they are called, which is Wishbringer's two worlds exactly. The number is printed
    /// beside a name that repeats, so a reader can tell which is which.
    /// </para>
    /// <para>
    /// Two neighbours are a move. A turn with neither a location nor a line that goes anywhere —
    /// a keystroke, a story that answered with nothing — is not a place and does not end the run
    /// of them, so the place before it stands until another appears. A line that left the game where
    /// it was is recorded as <em>tried</em> rather than as an edge, because that is what it is: an
    /// exit that exists and does not open.
    /// </para>
    /// </summary>
    private static Observed TheRecordShows(IReadOnlyList<ObservationLog.RecordedTurn>? record)
    {
        var byLocation = new Dictionary<long, int>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var places = new List<string>();
        var numbers = new List<long?>();
        var named = new List<bool>();
        // Keyed, because the same pair moved between twice is one arrow with two tries on it.
        var edges = new Dictionary<(int From, int To), (List<string> Lines, HashSet<string> Ways, int Count)>();
        var tried = new Dictionary<(int At, string Line), int>();
        int? here = null;
        var hereAfter = new List<int?>();

        int Place(string name, long? number)
        {
            places.Add(name.Length > 0 ? name : number is { } at ? $"room {at}" : "a place");
            numbers.Add(number);
            named.Add(name.Length > 0);
            if (number is { } known)
            {
                byLocation[known] = places.Count - 1;
            }

            // The first name a place was given is the one a later line is joined by: a room whose
            // description changes has still been named once, and re-naming it here would make the
            // map depend on the order the story printed things in.
            if (name.Length > 0)
            {
                byName.TryAdd(name, places.Count - 1);
            }

            return places.Count - 1;
        }

        foreach (var turn in record ?? [])
        {
            var typed = turn.Input.Trim();
            var way = Direction(typed);
            var name = TurnReading.RoomName(turn.Output);
            int? at;

            if (turn.Location is { } location)
            {
                at = byLocation.TryGetValue(location, out var byNumber)
                    ? byNumber
                    : name.Length > 0 && byName.TryGetValue(name, out var byItsName) && numbers[byItsName] is null
                        ? byItsName
                        : Place(name, location);

                // A place first met on a turn that printed no title carries the interpreter's own
                // number as its label until a turn that prints one arrives: the same room, and
                // "Post Office" is a better thing to write under a disc than "room 94". Only a
                // place with no name of its own is renamed, so the first title a room was given
                // still wins over every later one.
                if (name.Length > 0 && !named[at.Value])
                {
                    places[at.Value] = name;
                    named[at.Value] = true;
                    byName.TryAdd(name, at.Value);
                }
            }
            else if (way is not null && name.Length > 0)
            {
                // A line that goes somewhere, and the story answered with a room: the name is the
                // only thing left to identify it by, and a name already on the page is the room it
                // was already given.
                at = byName.TryGetValue(name, out var seen) ? seen : Place(name, null);
            }
            else
            {
                // Nothing here says where the player was, so this turn is not a place. A line that
                // went nowhere is still worth recording, against whatever room they are in.
                if (typed.Length > 0 && here is not null)
                {
                    tried[(here.Value, typed)] = tried.GetValueOrDefault((here.Value, typed)) + 1;
                }

                hereAfter.Add(here);
                continue;
            }

            if (here is null)
            {
                // The first place the record knows about. The line typed on that turn is not a
                // move: nothing says where the player was before it.
                here = at;
                hereAfter.Add(here);
                continue;
            }

            if (here == at)
            {
                if (typed.Length > 0)
                {
                    tried[(at.Value, typed)] = tried.GetValueOrDefault((at.Value, typed)) + 1;
                }

                hereAfter.Add(here);
                continue;
            }

            var key = (here.Value, at.Value);
            if (!edges.TryGetValue(key, out var edge))
            {
                edge = (Lines: [], Ways: new HashSet<string>(StringComparer.Ordinal), Count: 0);
            }

            if (typed.Length > 0 && !edge.Lines.Contains(typed, StringComparer.Ordinal))
            {
                edge.Lines.Add(typed);
            }

            if (way is not null)
            {
                edge.Ways.Add(way);
            }

            edges[key] = (edge.Lines, edge.Ways, edge.Count + 1);
            here = at;
            hereAfter.Add(here);
        }

        // A name that appears twice is two places with one name, and the interpreter's own number
        // is what tells them apart.
        //
        // Brackets and no middot: everything written into this page goes through `Escape`, which
        // encodes every character above ASCII as a numeric reference, so a pretty separator in the
        // source arrives in the document as `&#183;` and reads as nothing at all.
        var repeated = places.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < places.Count; i++)
        {
            if (repeated.Contains(places[i]) && numbers[i] is { } number)
            {
                places[i] = $"{places[i]} [{number}]";
            }
        }

        return new Observed(
            places,
            [.. edges.Select(edge => (
                edge.Key.From,
                edge.Key.To,
                Note: edge.Value.Lines.Count == 0
                    ? $"{edge.Value.Count}×"
                    : string.Join(", ", edge.Value.Lines)
                        + (edge.Value.Count > 1 ? $" ({edge.Value.Count}×)" : string.Empty),
                Way: edge.Value.Ways.Count == 1 ? edge.Value.Ways.First() : null))],
            [.. tried.Select(entry => (entry.Key.At, entry.Key.Line, entry.Value))
                .OrderBy(entry => entry.At)
                .ThenBy(entry => entry.Line, StringComparer.Ordinal)],
            hereAfter);
    }

    /// <summary>
    /// The graph, from the world document when there is one and from the record when there is not.
    /// <para>
    /// One walk, read by both consumers: before this the report derived its own map from the record
    /// while the world document derived another, so the page and the document could disagree about
    /// where the evening went without either of them looking wrong. The document is the one walk —
    /// rebuilt from the record at every session open — and the page reads it.
    /// </para>
    /// <para>
    /// <paramref name="world"/> is null for a state directory that has no document yet, which is a
    /// session that has not opened a game: the record's own walk still answers, because a page that
    /// draws nothing is worse than a page that draws yesterday's shape.
    /// </para>
    /// </summary>
    private static Observed Shows(
        IReadOnlyList<ObservationLog.RecordedTurn>? record,
        WorldState.Document? world)
    {
        var perTurn = TheRecordShows(record);
        if (world is null || world.Places.Count == 0)
        {
            return perTurn;
        }

        var fromWorld = TheWorldShows(world);
        if (fromWorld.Places.Count == 0)
        {
            return perTurn;
        }

        // `Here` is the record's either way, because it is a reading of the turns in order and the
        // document, which is keyed by place, does not hold it. The two walks number their places in
        // the order they first met them, so the indices travel by label; a place the document does
        // not have becomes null, which is *cannot say* rather than a guess.
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var at = 0; at < fromWorld.Places.Count; at++)
        {
            index.TryAdd(fromWorld.Places[at], at);
        }

        var here = perTurn.Here
            .Select(at => at is { } was && was >= 0 && was < perTurn.Places.Count
                && index.TryGetValue(perTurn.Places[was], out var now)
                    ? (int?)now
                    : null)
            .ToList();

        return fromWorld with { Here = here };
    }

    /// <summary>
    /// The graph the world document holds, in the shape the page already draws (`M40`).
    /// <para>
    /// Three differences from the record's walk, and each is a claim the document can support and the
    /// record could not. A place is identified by the interpreter's own number where it has one, so
    /// walking back into a room is not a second circle; a move is keyed on the line <em>and</em> where
    /// it led, so `east` to the office and `east` to the bedroom are two arrows and neither is
    /// overwritten; and a name that repeats carries the number beside it, which is how the two
    /// Wishbringer worlds are told apart.
    /// </para>
    /// <para>
    /// Two consequences worth knowing when the two walks are compared. Only a line that <b>asked to go
    /// somewhere</b> is an edge here, so *tried, and it moved nothing* is the attempted movements
    /// rather than everything typed that did not move. And a line that moved but whose destination
    /// nobody can say (`unplaced`) is in neither table: drawing it would invent a place, and calling
    /// it *moved nothing* would be false.
    /// </para>
    /// </summary>
    private static Observed TheWorldShows(WorldState.Document world)
    {
        var places = new List<string>();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var place in world.Places)
        {
            byId[place.Id] = places.Count;
            places.Add(place.Names.Count > 0
                ? place.Names[0]
                : place.Number is { } number ? $"room {number}" : "a place");
        }

        // A name that appears twice is two places with one name, and the interpreter's own number is
        // what tells them apart. Brackets and no middot, for the same reason as the record's walk:
        // everything written into this page goes through `Escape`, which encodes every character above
        // ASCII as a numeric reference, so a pretty separator arrives as `&#183;` and reads as nothing.
        var repeated = places.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < places.Count; i++)
        {
            if (repeated.Contains(places[i]) && world.Places[i].Number is { } number)
            {
                places[i] = $"{places[i]} [{number}]";
            }
        }

        var edges = world.Edges
            .Where(edge => edge.Outcome == "moved" && edge.To is not null
                && byId.ContainsKey(edge.From) && byId.ContainsKey(edge.To))
            .GroupBy(edge => (From: byId[edge.From], To: byId[edge.To!]))
            .Select(group =>
            {
                var typed = group.Select(edge => edge.Typed).Distinct(StringComparer.Ordinal).ToList();
                var ways = typed.Select(Direction).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var times = (int)group.Sum(edge => edge.Times);
                return (group.Key.From, group.Key.To,
                    Note: string.Join(", ", typed) + (times > 1 ? $" ({times}×)" : string.Empty),
                    Way: typed.Count == 1 && ways.Count == 1 ? ways[0] : null);
            })
            .OrderBy(edge => edge.From)
            .ThenBy(edge => edge.To)
            .ToList();

        var tried = world.Edges
            .Where(edge => edge.Outcome == "blocked" && byId.ContainsKey(edge.From))
            .GroupBy(edge => (At: byId[edge.From], edge.Typed))
            .Select(group => (group.Key.At, group.Key.Typed, (int)group.Sum(edge => edge.Times)))
            .OrderBy(entry => entry.At)
            .ThenBy(entry => entry.Typed, StringComparer.Ordinal)
            .ToList();

        // `Here` is the caller's to fill: see `Shows`.
        return new Observed(places, edges, tried, []);
    }

    /// <summary>
    /// The observed moves as a picture.
    /// <para>
    /// <b>Laid out by the moves, so that it looks like a map.</b> The first place sits at the origin
    /// and every other one is placed a cell in the direction of the move that reached it: north is
    /// up, east is right, and two rooms joined by `north` are drawn one above the other. The order
    /// the places were first seen in is not used for anything — the first version of this put them
    /// in a lattice four to a row, which is a diagram of a list rather than of a place, and it is
    /// why the map did not read as one.
    /// </para>
    /// <para>
    /// Two things have no side to sit on, and both fall back to the nearest free cell: a move whose
    /// direction is not a compass point (`up`, `enter the window`), and a cell another room already
    /// has. Two rooms cannot share a disc, and the arrow carries the word that was typed, which is
    /// where a reader looks for it anyway.
    /// </para>
    /// <para>
    /// A walk over the edges rather than a force simulation: positions have to be the same every
    /// time the page is opened, because a picture that rearranges itself between two readings of the
    /// same record is a picture nobody can learn a map from.
    /// </para>
    /// </summary>
    private static string Diagram(Observed observed)
    {
        var nodes = observed.Places;
        var edges = observed.Edges;
        if (nodes.Count == 0)
        {
            return string.Empty;
        }

        const int Cell = 230;
        const int Radius = 30;
        var taken = new HashSet<(int X, int Y)>();
        var placed = new (int X, int Y)[nodes.Count];

        bool IsFree((int X, int Y) cell) => !taken.Contains(cell);

        // Rings, outward: the cell itself if it is free, then the eight around it, then the
        // sixteen. Finite and deterministic, and it never lands on a room.
        (int X, int Y) Nearest((int X, int Y) cell)
        {
            for (var ring = 0; ring <= nodes.Count; ring++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    for (var dy = -ring; dy <= ring; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }

                        var candidate = (X: cell.X + dx, Y: cell.Y + dy);
                        if (IsFree(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return cell;
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            // The move that reached this place, which is the first edge that ends on it: the
            // layout follows the walking, not the order the places were found in.
            var reached = observed.Edges.FirstOrDefault(edge => edge.To == index);
            var anchor = index == 0 ? (X: 0, Y: 0) : placed[reached.From];
            var wanted = index == 0 || reached.Way is null ? anchor : Step(anchor, reached.Way);
            placed[index] = IsFree(wanted) ? wanted : Nearest(anchor);
            taken.Add(placed[index]);
        }

        var minX = placed.Min(cell => cell.X);
        var minY = placed.Min(cell => cell.Y);

        // One cell of grid beyond the last room, and no more: the first place is drawn a cell in from
        // the edge, so this leaves a margin wide enough for a disc and the name under it and stops
        // there. A diagram that is mostly empty grid reads as a diagram with nothing in it.
        var width = ((placed.Max(cell => cell.X) - minX) + 2) * Cell;
        var height = ((placed.Max(cell => cell.Y) - minY) + 2) * Cell;
        var places = nodes
            .Select((name, index) => (
                Name: name,
                X: Cell + ((placed[index].X - minX) * Cell),
                Y: Cell + ((placed[index].Y - minY) * Cell)))
            .ToList();

        var svg = new StringBuilder();
        svg.AppendLine(
            $"        <svg class=\"map\" viewBox=\"0 0 {width} {height}\" role=\"img\" "
            + $"aria-label=\"a map of {nodes.Count} known places\">");
        svg.AppendLine("          <defs>");
        svg.AppendLine(
            "            <marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" "
            + "markerWidth=\"6\" markerHeight=\"6\" orient=\"auto-start-reverse\">"
            + "<path d=\"M 0 0 L 10 5 L 0 10 z\" class=\"arrowhead\"/></marker>");
        svg.AppendLine("          </defs>");

        foreach (var (from, to, note, _) in edges)
        {
            if (from < 0 || to < 0 || from == to)
            {
                continue;
            }

            var a = places[from];
            var b = places[to];

            // Rim to rim rather than centre to centre, along the line the two rooms actually lie on:
            // a map puts rooms side by side as often as one above the other, and an arrow drawn on
            // the assumption of \"below\" runs straight through the circles it connects.
            var dx = (double)(b.X - a.X);
            var dy = (double)(b.Y - a.Y);
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 1)
            {
                continue;
            }

            var ux = dx / length;
            var uy = dy / length;
            var start = (X: a.X + (ux * Radius), Y: a.Y + (uy * Radius));
            var end = (X: b.X - (ux * Radius), Y: b.Y - (uy * Radius));
            svg.AppendLine(
                $"          <line class=\"edge\" x1=\"{Round(start.X)}\" y1=\"{Round(start.Y)}\" "
                + $"x2=\"{Round(end.X)}\" y2=\"{Round(end.Y)}\" marker-end=\"url(#arrow)\"/>");
            if (note.Length > 0)
            {
                // Beside the line rather than on it, on the side a reader's eye is not travelling
                // along: a note struck through by its own arrow is a note nobody reads.
                var x = (start.X + end.X) / 2;
                var y = ((start.Y + end.Y) / 2) + (uy >= 0 ? -10 : 18);
                svg.AppendLine(
                    $"          <text class=\"note\" x=\"{Round(x)}\" y=\"{Round(y)}\" text-anchor=\"middle\">"
                    + $"{Escape(Clip(note, 30))}</text>");
            }
        }

        foreach (var place in places)
        {
            svg.AppendLine(
                $"          <circle class=\"room\" cx=\"{place.X}\" cy=\"{place.Y}\" r=\"{Radius}\"/>");
            // The name goes *under* the disc rather than inside it: a room called \"20-foot
            // depression\" is wider than any circle, and a label that spills over its neighbours is
            // a map nobody can read.
            svg.AppendLine(
                $"          <text class=\"room-name\" x=\"{place.X}\" y=\"{place.Y + Radius + 18}\" "
                + $"text-anchor=\"middle\">{Escape(Clip(place.Name, 26))}</text>");
        }

        svg.AppendLine("        </svg>");
        return svg.ToString().TrimEnd();
    }

    /// <summary>
    /// One cell in the direction a line went, for the picture. The four that are not a compass
    /// point come back unchanged, so the caller puts the room in the nearest free cell instead of
    /// pretending that `up` is north (see <see cref=\"MovementWords\"/>).
    /// </summary>
    private static (int X, int Y) Step((int X, int Y) from, string way) => way switch
    {
        "north" or "n" => (from.X, from.Y - 1),
        "south" or "s" => (from.X, from.Y + 1),
        "east" or "e" => (from.X + 1, from.Y),
        "west" or "w" => (from.X - 1, from.Y),
        "northeast" or "ne" => (from.X + 1, from.Y - 1),
        "northwest" or "nw" => (from.X - 1, from.Y - 1),
        "southeast" or "se" => (from.X + 1, from.Y + 1),
        "southwest" or "sw" => (from.X - 1, from.Y + 1),
        _ => from,
    };

    private static string Round(double value) =>
        Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";

    /// <summary>
    /// One line of the model's prose, with the two marks a model actually writes rendered: bold and
    /// code. Everything is escaped first and the marks are substituted afterwards, so a room called
    /// <c>&lt;b&gt;</c> is still a room and a report cannot become a document that does something.
    /// </summary>
    private static string Inline(string text)
    {
        var html = Escape(text);
        html = Bold().Replace(html, "<strong>$1</strong>");
        html = Code().Replace(html, "<code>$1</code>");
        html = Italic().Replace(html, "<em>$1</em>");
        return html;
    }

    /// <summary>
    /// The model's words, into a page. Everything it wrote is escaped: a room called
    /// <c>&lt;script&gt;</c> is a room, and a notebook full of angle brackets must not become a
    /// document that does something.
    /// </summary>
    private static string Escape(string text) => WebUtility.HtmlEncode(text ?? string.Empty);

    [GeneratedRegex(@"^[-*]\s+(.+)$")]
    private static partial Regex Bullet();

    /// <summary>`**bold**`, which is how a model writes emphasis.</summary>
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    /// <summary>`` `code` ``, which is how a model writes a command or a word from the game.</summary>
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Code();

    /// <summary>
    /// `_emphasis_`, which is how the Events tab quotes the game's own words back at a reader
    /// without appearing to be the page's own sentence. Underscores and not asterisks: a single
    /// asterisk is used as a bullet in a notebook, and the two would be one mark doing two jobs.
    /// </summary>
    [GeneratedRegex(@"_([^_]+)_")]
    private static partial Regex Italic();

    /// <summary>
    /// The look: a phosphor tube that has been repurposed as a filing cabinet.
    /// <para>
    /// Both halves are deliberate. The 1983 half is what this whole product is — amber on near
    /// black, a scanline, a monospace face, a cursor-ish rule under the masthead. The other half is
    /// the one the report itself belongs to: a document produced *about* an evening by an
    /// institution, so its type is the calm centred serif of a memo, its rules are hairlines, and
    /// the only strong colour is the red of a form's stamp. Nothing here is a joke at either
    /// expense.
    /// </para>
    /// </summary>
    private const string Style = """
            :root {
              --tube: #0d0f0c;
              --phosphor: #f0b429;
              --phosphor-dim: #8a6a1f;
              --memo: #f3efe3;
              --memo-ink: #22312e;
              --stamp: #b4322a;
              --teal: #123331;
            }
            * { box-sizing: border-box; }
            html, body { margin: 0; padding: 0; background: var(--tube); }
            body {
              font-family: "Iowan Old Style", "Palatino Linotype", Georgia, serif;
              color: var(--memo-ink);
              padding: 2.5rem 1rem 4rem;
            }
            .terminal {
              max-width: 62rem;
              margin: 0 auto;
              background: var(--memo);
              border: 1px solid #d8d2c0;
              box-shadow: 0 0 0 1px #000, 0 18px 60px rgba(0,0,0,.6);
            }
            .masthead {
              padding: 1.6rem 1.8rem 1.1rem;
              border-bottom: 2px solid var(--teal);
              background:
                repeating-linear-gradient(to bottom, rgba(18,51,49,.06) 0 1px, transparent 1px 4px),
                var(--memo);
            }
            .eyebrow {
              margin: 0;
              font-family: ui-monospace, "SFMono-Regular", "Cascadia Mono", Menlo, monospace;
              font-size: .72rem;
              letter-spacing: .34em;
              text-transform: uppercase;
              color: var(--phosphor-dim);
            }
            .masthead h1 {
              margin: .25rem 0 .5rem;
              font-size: 2.1rem;
              letter-spacing: .02em;
              color: var(--teal);
            }
            .meta {
              margin: 0;
              font-family: ui-monospace, "SFMono-Regular", "Cascadia Mono", Menlo, monospace;
              font-size: .74rem;
              line-height: 1.6;
              color: #5c6b66;
            }
            .tabs {
              display: flex;
              flex-wrap: wrap;
              gap: .25rem;
              padding: .8rem 1.2rem 0;
              border-bottom: 1px solid #d8d2c0;
            }
            .tab {
              appearance: none;
              border: 1px solid #d8d2c0;
              border-bottom: none;
              background: #e7e1d1;
              color: var(--memo-ink);
              font-family: ui-monospace, "SFMono-Regular", "Cascadia Mono", Menlo, monospace;
              font-size: .78rem;
              letter-spacing: .04em;
              padding: .45rem .8rem;
              cursor: pointer;
            }
            .tab[aria-selected="true"] {
              background: var(--memo);
              color: var(--stamp);
              font-weight: 600;
              box-shadow: inset 0 3px 0 var(--stamp);
            }
            .panels { padding: 1.4rem 1.8rem 2rem; }
            .panel h3 { margin: 1.4rem 0 .3rem; font-size: 1rem; color: var(--teal); }
            .panel p { margin: .7rem 0; line-height: 1.65; }
            .panel ul { margin: .6rem 0; padding-left: 1.2rem; }
            .panel li { margin: .3rem 0; line-height: 1.6; }
            .panel .empty { color: #8a8577; font-style: italic; }
            .map {
              width: 100%;
              height: auto;
              background:
                repeating-linear-gradient(to right, rgba(18,51,49,.07) 0 1px, transparent 1px 26px),
                repeating-linear-gradient(to bottom, rgba(18,51,49,.07) 0 1px, transparent 1px 26px),
                #fbf8ef;
              border: 1px solid #d8d2c0;
            }
            .room { fill: var(--tube); stroke: var(--phosphor); stroke-width: 2; }
            .room-name {
              fill: var(--phosphor);
              font-family: ui-monospace, "SFMono-Regular", "Cascadia Mono", Menlo, monospace;
              font-size: 13px;
            }
            .edge { stroke: var(--teal); stroke-width: 2; }
            .arrowhead { fill: var(--teal); }
            .note {
              fill: var(--stamp);
              font-family: ui-monospace, "SFMono-Regular", "Cascadia Mono", Menlo, monospace;
              font-size: 11px;
            }
            .colophon {
              padding: 1rem 1.8rem 1.4rem;
              border-top: 1px solid #d8d2c0;
              font-size: .78rem;
              line-height: 1.6;
              color: #6d6a60;
            }
            """;
}
