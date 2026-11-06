using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// The page a returning player is given (05_memory.md §6 (`M27`–`M36`)).
/// <para>
/// The model writes the report; the harness writes the page. These tests are about the second half:
/// that the sections become tabs in the order a reader wants them, that the map is drawn from moves
/// rather than from anything the model felt like saying, and that nothing the model wrote can do
/// anything on the page it is rendered onto.
/// </para>
/// </summary>
public class CatchupReportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "infocompanion-report-tests", Guid.NewGuid().ToString("N"));

    public CatchupReportTests() => Directory.CreateDirectory(_directory);

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

    private static CatchupReport.ReportFacts Facts => new(
        "advent",
        new DateTimeOffset(2026, 9, 19, 1, 22, 31, TimeSpan.Zero),
        "deepseek-flash",
        42,
        0.0009m,
        0.49m);

    private static WorldState.Document Walked(params (string Id, long? Number, string Name)[] places)
    {
        var edges = new List<WorldState.Edge>();
        if (places.Length >= 2)
        {
            edges.Add(new WorldState.Edge(
                3, places[0].Id, "east", places[1].Id, "moved", "the office", 430, 430, 1));
        }

        return new WorldState.Document(
            new WorldState.Header("advent", 1, 3, 532, 718),
            [.. places.Select((place, at) => new WorldState.Place(
                place.Id,
                place.Number,
                [place.Name],
                new WorldState.Seen(400 + at, 3),
                new WorldState.Seen(400 + at, 3)))],
            edges,
            [],
            [],
            []);
    }

    [Fact]
    public void The_map_is_drawn_from_the_world_document_and_not_from_a_second_walk()
    {
        // No record is passed at all, so the only thing that can name a place on this page is the
        // document. Before this the report derived its own map from the record while the document
        // derived another, and the two could disagree without either of them looking wrong.
        var html = CatchupReport.Render(
            "advent", Report, Facts, null, Walked(("a", 131, "The Cottage"), ("b", 222, "The Office")));

        Assert.Contains("The Cottage", html);
        Assert.Contains("The Office", html);
    }

    [Fact]
    public void A_name_that_repeats_carries_the_interpreter_number_beside_it()
    {
        // Two rooms with one name are two places, and the number is the only thing that tells them
        // apart — which is Wishbringer's two worlds exactly.
        var html = CatchupReport.Render(
            "advent", Report, Facts, null, Walked(("a", 33, "Valley"), ("b", 54, "Valley")));

        Assert.Contains("Valley [33]", html);
        Assert.Contains("Valley [54]", html);
    }

    [Fact]
    public void A_report_asked_for_before_any_document_exists_is_still_a_page()
    {
        // A state directory in which no game has been opened yet: `Write` looks for the document,
        // does not find one, and the page falls back to the record's own walk rather than failing or
        // drawing nothing. This is the path the live deployment takes, so it is worth a page.
        CatchupReport.Write(_directory, "advent", Report, Facts);

        var html = File.ReadAllText(Path.Combine(_directory, "catchup", "advent.html"));

        Assert.Contains("the well house", html);
    }

    private const string Report = """
        # advent

        ## Where we are

        Inside the well house, with the kit gathered.

        ## Characters

        - The tall thief, who wants the grate open and will not say why.

        ## What is left to do

        - The gully outside is the thread we have not pulled.
        - The grate in the valley is locked.

        ## What we have solved

        - The mailbox: the leaflet was in it, and we kept it.
        - The sewer pipes are too narrow, whatever we do to them.

        ## The map

        - End of road -> Inside the building (the well house, kit here)
        - Inside the building -> Valley (the stream runs out and down)
        - Valley -> Valley (down the dry streambed)

        ## Worth remembering

        - `open grate` is locked, and the keys are probably what it wants.
        """;

    /// <summary>
    /// A turn record, as the core writes one, including an inventory turn — which is the only way
    /// the page can know what is in the player's hands without reading the story's prose (B5).
    /// </summary>
    private static IReadOnlyList<ObservationLog.RecordedTurn> Record =>
    [
        new(1, "look", "End Of Road\nYou are at the end of a road.", "End Of Road#1a2b", 180),
        new(2, "open mailbox", "Opening the small mailbox reveals a leaflet.", "End Of Road#1a2b", 180),
        new(3, "inventory", "You are carrying:\n  a brass lantern\n  a small leaflet", "End Of Road#1a2b", 180),
        new(4, "read leaflet", "\"WELCOME TO ADVENTURE!\"", "End Of Road#1a2b", 180),
        new(5, "go north", "Inside Building\nA well house stands here.", "Inside Building#9f0c", 181),
    ];

    [Fact]
    public void The_sections_become_tabs_and_the_ones_a_reader_wants_come_first()
    {
        var html = CatchupReport.Render("advent", Report, Facts, Record);
        foreach (var heading in CatchupReport.KnownSections)
        {
            Assert.Contains($"<button class=\"tab\"", html);
            Assert.Contains($">{heading}</button>", html);
        }

        // In the order the harness knows them, not the order the model wrote them: a report whose
        // tabs moved about between one evening and the next would be a report nobody could skim.
        var positions = CatchupReport.KnownSections
            .Select(heading => html.IndexOf($">{heading}</button>", StringComparison.Ordinal))
            .ToArray();
        Assert.All(positions, position => Assert.True(position > 0, "every known section is a tab"));
        Assert.Equal(positions.OrderBy(position => position), positions);
    }

    [Fact]
    public void A_section_the_harness_does_not_know_is_kept_after_the_ones_it_does()
    {
        // The model reading the record is the one thing here that knows what matters about a
        // particular game, so a section it invented is a section it thought was worth a tab.
        var html = CatchupReport.Render(
            "advent",
            Report + "\n\n## The things I got wrong\n\n- I thought the grate was a red herring.\n",
            Facts);

        Assert.Contains(">The things I got wrong</button>", html);
        Assert.True(
            html.IndexOf(">Worth remembering</button>", StringComparison.Ordinal)
                < html.IndexOf(">The things I got wrong</button>", StringComparison.Ordinal));
        Assert.Contains("I thought the grate was a red herring", html);
    }

    [Fact]
    public void The_map_is_drawn_from_the_record_rather_than_from_the_model()
    {
        // B4: the diagram is the core's arithmetic, not the model's prose. Every arrow is a move the
        // core watched happen — the line that was typed, and the place the story printed after it —
        // so it needs no cooperation from the write-up and no agreement about what a room is called.
        var html = CatchupReport.Render("advent", Report, Facts, Record);
        var start = html.IndexOf("<svg", StringComparison.Ordinal);
        var end = html.IndexOf("</svg>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the map tab has a diagram");
        var svg = html[start..end];

        // Two places on the record, and the one move between them: turn 5's `go north`.
        Assert.Equal(2, Count(svg, "<circle class=\"room\""));
        Assert.Equal(1, Count(svg, "marker-end=\"url(#arrow)\""));
        Assert.Contains(">End Of Road<", svg);
        Assert.Contains(">Inside Building<", svg);
        Assert.Contains(">go north<", svg);

        // The model's own reading is kept, under the diagram rather than instead of it: this is what
        // the companion made of the moves, and the two are allowed to disagree.
        Assert.Contains("As the companion reads it", html);
        Assert.Contains("the well house, kit here", html);
    }

    [Fact]
    public void Two_places_with_one_name_are_two_circles_and_are_told_apart()
    {
        // The exact failure the old map had: it was keyed by room *name*, so Wishbringer's two
        // worlds collapsed into one node and the same name appeared again and again. The key that
        // works is the interpreter's own location: two worlds are two object numbers, and the number
        // is printed beside a name that repeats so a reader can tell which is which.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Outside Cottage\nA tidy little place, with a poodle in the road.", "Outside Cottage#aaaa1111", 180),
            new(2, "north", "Outside Cottage\nThe same words, upside down.", "Outside Cottage#bbbb2222", 181),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var svg = html[html.IndexOf("<svg", StringComparison.Ordinal)..html.IndexOf("</svg>", StringComparison.Ordinal)];

        Assert.Equal(2, Count(svg, "<circle class=\"room\""));
        Assert.Equal(1, Count(svg, "marker-end=\"url(#arrow)\""));
        Assert.Contains("Outside Cottage [180]", svg);
        Assert.Contains("Outside Cottage [181]", svg);

        // And the diagram says what it is: the record's arithmetic, not a claim about the world.
        Assert.Contains("Drawn from the world document", html);
        Assert.Contains("North is up.", html);
    }

    [Fact]
    public void A_line_that_moved_nothing_is_an_exit_that_did_not_open()
    {
        // The poodle in the road. A turn that leaves the story in the same place is not an edge and
        // is exactly the fact a player wants from a map: the exit is there, and it is not open.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Outside Cottage\nA poodle blocks the road north.", "Outside Cottage#aaaa1111", 180),
            new(2, "north", "Outside Cottage\nThe poodle won't let you go that way.", "Outside Cottage#aaaa1111", 180),
            new(3, "north", "Outside Cottage\nThe poodle won't let you go that way.", "Outside Cottage#aaaa1111", 180),
            new(4, "south", "Hilltop\nThe road bends back up to the hill.", "Hilltop#dddd4444", 181),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);

        // One arrow — the move that worked — and the line that did not, said twice. The arrow is
        // labelled with the word that made it, which is where a reader looks for it.
        Assert.Equal(1, Count(html, "marker-end=\"url(#arrow)\""));
        Assert.Contains(">south<", html);
        Assert.Contains("Tried, and it moved nothing", html);
        Assert.Contains("2 tries", html);
    }

    [Fact]
    public void A_turn_with_no_location_does_not_end_the_run_of_them()
    {
        // A keystroke, or a story that answered with nothing: the interpreter cannot say where the
        // player was, so the turn is not a place — and the place before it stands until another
        // appears. Read naively, such a turn would break the chain and the move after it would look
        // like it came from nowhere.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Outside Cottage\nA poodle blocks the road north.", "Outside Cottage#aaaa1111", 180),
            new(2, "press key", "", "", null),
            new(3, "north", "Narrow Trail\nA steep trail winds upward.", "Narrow Trail#cccc3333", 181),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var svg = html[html.IndexOf("<svg", StringComparison.Ordinal)..html.IndexOf("</svg>", StringComparison.Ordinal)];

        Assert.Equal(2, Count(svg, "<circle class=\"room\""));
        Assert.Equal(1, Count(svg, "marker-end=\"url(#arrow)\""));
        Assert.Contains(">north<", svg);
    }

    [Fact]
    public void A_place_is_named_by_a_title_line_rather_than_by_the_prose()
    {
        // Measured in the first live session: a move turn often begins with the *old* room's parting
        // words — "The poodle yaps a threat as you back away." — and the place that turn arrived in
        // is named on the line after it. A room's name is a title and everything else is prose, and
        // the mark that tells them apart is the punctuation at the end: a sentence has one.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Outside Cottage\nAn angry poodle is blocking your path.", "Outside Cottage#aaaa1111", 94),
            new(2, "up", "The poodle yaps a threat as you back away.\nHilltop\nThe road bends up to the hill.", "Hilltop#bbbb2222", 95),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var svg = html[html.IndexOf("<svg", StringComparison.Ordinal)..html.IndexOf("</svg>", StringComparison.Ordinal)];

        Assert.Contains(">Hilltop<", svg);
        Assert.Contains(">Outside Cottage<", svg);
        Assert.DoesNotContain("yaps a threat", svg);

        // And the edge is the move: from the room the player was in to the room they arrived in.
        Assert.Equal(1, Count(svg, "marker-end=\"url(#arrow)\""));
        Assert.Contains(">up<", svg);
    }

    [Fact]
    public void A_room_first_met_without_a_title_is_named_when_a_turn_prints_one()
    {
        // The interpreter reports the location on a minority of turns, and the turn that reports it
        // is often not the turn that printed the room's title — so the map grows a disc labelled
        // "room 94" that a later turn can name. The number stays the identity; the name is only a
        // better thing to write under the disc.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "You're on a hilltop overlooking the village.\nWhat next?", "hilltop#aaaa", 94),
            new(2, "look", "Hilltop\nYou're on a hilltop overlooking the village.", "hilltop#bbbb", 94),
            new(3, "north", "Post Office\nA little brick building.", "post office#cccc", 95),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var svg = html[html.IndexOf("<svg", StringComparison.Ordinal)..html.IndexOf("</svg>", StringComparison.Ordinal)];

        Assert.Contains(">Hilltop<", svg);
        Assert.Contains(">Post Office<", svg);
        Assert.DoesNotContain(">room 94<", svg);
        Assert.Equal(1, Count(svg, "marker-end=\"url(#arrow)\""));
    }

    [Fact]
    public void A_record_with_no_location_at_all_is_still_joined_by_the_names_it_printed()
    {
        // A Version 5 story draws its own status line, and an interpreter without the tool channel
        // never has one: the record then holds no location anywhere. The name the story printed on
        // a turn that *went* somewhere is the fallback, and it is enough to draw the evening — the
        // first version of this had no fallback at all and drew nothing for four games out of five.
        //
        // It is only consulted on a move turn, which is what keeps a status line out of the names:
        // keying the picture by fingerprint instead made twenty-two places out of a handful of
        // rooms in the first live attempt, `Taken.` among them.
        //
        // The walk is out to the trail and back to the cottage, and the second sight of each name
        // has to join the place the first sight made — otherwise the map grows a disc for every
        // room walked into twice.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "north", "Narrow Trail\nA steep trail winds upward.", "Narrow Trail#cccc3333"),
            new(2, "south", "Outside Cottage\nA poodle blocks the road.", "Outside Cottage#aaaa1111"),
            new(3, "north", "Narrow Trail\nA steep trail winds upward.", "Narrow Trail#cccc3333"),
            new(4, "look", "Taken.\n  a brass lantern", "Taken.#bbbb2222"),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var svg = html[html.IndexOf("<svg", StringComparison.Ordinal)..html.IndexOf("</svg>", StringComparison.Ordinal)];

        // Two rooms and both walks between them, with no location anywhere on the record.
        Assert.Equal(2, Count(svg, "<circle class=\"room\""));
        Assert.Equal(2, Count(svg, "marker-end=\"url(#arrow)\""));
        Assert.Contains(">Outside Cottage<", svg);
        Assert.Contains(">Narrow Trail<", svg);
        Assert.Contains(">north<", svg);
        Assert.Contains(">south<", svg);
        Assert.DoesNotContain(">Taken.<", svg);
    }

    [Fact]
    public void A_map_with_no_record_is_the_prose_it_was_written_as()
    {
        // A game with no record to draw from — every write-up written before this change, and any
        // bundle whose record is missing: the section is still worth reading, and drawing an empty
        // diagram for it would lose it.
        var html = CatchupReport.Render(
            "advent",
            "## The map\n\nNobody has written a map down yet.\n",
            Facts);

        Assert.DoesNotContain("<svg", html);
        Assert.Contains("Nobody has written a map down yet", html);
    }

    [Fact]
    public void Everything_the_model_wrote_is_escaped_onto_the_page()
    {
        var html = CatchupReport.Render(
            "advent",
            "## Worth remembering\n\n- A room called <script>alert('x')</script> is a room.\n",
            Facts);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        // The page's own script is still there, escaped words and all.
        Assert.Contains("querySelectorAll('.tab')", html);
    }

    [Fact]
    public void The_page_says_whose_site_it_is_and_how_to_get_home()
    {
        // The 2026-09-19 UAT: "no navigation back out of /games/ and the sub screens". This page is
        // the far end of that path — *the games -> view* — and it is the one whose chrome is written
        // here rather than in the web interface, because the companion writes the whole file.
        var html = CatchupReport.Render("advent", Report, Facts);

        Assert.Contains("two friends, one keyboard, 1983", html);
        Assert.Contains("<a href=\"/\">the index</a>", html);
        Assert.Contains("<a href=\"/games\">the games</a>", html);
    }

    [Fact]
    public void The_page_shows_what_the_evening_cost_rather_than_what_the_pass_cost()
    {
        // The page said "349 turns on the record · written by deepseek-flash on its own pass ·
        // 0.00204 USD" on 2026-09-21, which is one call's cost standing where a reader looks for
        // the price of the evening. Both numbers exist now; the page shows the one that is not
        // misleading on its own, and the pass's own figure stays in the facts file beside it.
        var html = CatchupReport.Render("advent", Report, Facts);

        Assert.Contains("the evening stands at 0.49 USD", html);
        Assert.DoesNotContain("0.0009", html);
    }

    [Fact]
    public void The_page_and_its_facts_are_written_where_the_browser_looks_for_them()
    {
        var (htmlPath, jsonPath) = CatchupReport.Write(_directory, "advent", Report, Facts);

        Assert.Equal(Path.Combine(_directory, "catchup", "advent.html"), htmlPath);
        Assert.Equal(Path.Combine(_directory, "catchup", "advent.json"), jsonPath);
        Assert.Contains("<html", File.ReadAllText(htmlPath));

        var facts = File.ReadAllText(jsonPath);
        Assert.Contains("\"stem\": \"advent\"", facts);
        Assert.Contains("\"turns\": 42", facts);
        Assert.Contains("2026-09-19", facts);
        // Both costs, because they are different numbers and only one of them is the evening's.
        Assert.Contains("\"write_up_cost_usd\": 0.0009", facts);
        Assert.Contains("\"session_cost_usd\": 0.49", facts);

        // And the date the games list shows comes back out of it.
        var when = CatchupReport.GeneratedAt(_directory, "advent");
        Assert.NotNull(when);
        Assert.Equal(2026, when.Value.Year);
        Assert.Equal(31, when.Value.Second);

        // A game that has never been written up has no date, and neither has a file that is not a
        // report — a half-written one is a report with no date rather than a broken page.
        Assert.Null(CatchupReport.GeneratedAt(_directory, "zork1"));
        File.WriteAllText(jsonPath, "{\"stem\":");
        Assert.Null(CatchupReport.GeneratedAt(_directory, "advent"));
    }

    [Fact]
    public void A_report_with_nothing_in_it_still_makes_a_page()
    {
        var html = CatchupReport.Render("advent", string.Empty, Facts);

        Assert.Contains("<html", html);
        Assert.Contains("The report", html);
        Assert.Contains("Nothing on this one.", html);
    }

    [Fact]
    public void The_items_and_the_events_come_from_the_record_rather_than_from_the_model()
    {
        // B5: the model writes what only it knows — who people are, what threads are open — and the
        // two sections that are *data* are rendered from the core's own record instead. One is what
        // the game itself said was in the player's hands, the other is every turn that happened;
        // asking a model to retell either is asking it to get them subtly wrong in a document
        // somebody reads months later, which is the same reason the map is drawn by the harness.
        var html = CatchupReport.Render("advent", Report, Facts, Record);

        // The inventory is the game's own words, from turn 3, under its own tab: quoted rather than
        // summarised, because what is in somebody's hands is the thing a summary gets quietly wrong.
        Assert.Contains(">Items</button>", html);
        Assert.Contains("a brass lantern", html);
        Assert.Contains("own last list", html);
        Assert.Contains("Asked at turn 3 of 5", html);

        // What was done with each thing, which is not the same list as what is in hand: the
        // mailbox was opened on turn 2 and the leaflet read on turn 4.
        Assert.Contains("<strong>mailbox</strong>", html);
        Assert.Contains("opened (turn 2)", html);
        Assert.Contains("<strong>leaflet</strong>", html);
        Assert.Contains("read (turn 4)", html);

        // Every turn is there, read through the two tables: a move is where it went, and a line
        // about a thing is what was done with it.
        Assert.Contains(">Events</button>", html);
        Assert.Contains("then went north to <strong>Inside Building</strong>", html);
        Assert.Contains("opened the mailbox", html);
        Assert.Contains("checked what was being carried", html);

        // And they sit in the order the harness knows, which is after the map and before the notes.
        var events = html.IndexOf(">Events</button>", StringComparison.Ordinal);
        var items = html.IndexOf(">Items</button>", StringComparison.Ordinal);
        var map = html.IndexOf(">The map</button>", StringComparison.Ordinal);
        var notes = html.IndexOf(">Worth remembering</button>", StringComparison.Ordinal);
        Assert.True(map < events && events < items && items < notes, $"{map} {events} {items} {notes}");
    }

    [Fact]
    public void A_record_with_no_inventory_turn_still_says_what_was_done()
    {
        // The tab is a list of the things somebody typed at and what became of them, which is a
        // fact about the evening and does not need a particular command to have been typed. Only
        // the game's own last list waits on the inventory, and a game that spells it another way
        // simply has none: a heading with nothing under it claims the harness looked and found
        // nothing, so the heading is left out rather than shown empty.
        var without = Record.Where(turn => turn.Input != "inventory").ToList();
        var html = CatchupReport.Render("advent", Report, Facts, without);

        Assert.Contains(">Items</button>", html);
        Assert.Contains("read (turn 4)", html);
        Assert.DoesNotContain("own last list", html);
        Assert.DoesNotContain("a brass lantern", html);
        Assert.Contains(">Events</button>", html);
    }

    [Fact]
    public void A_report_with_no_record_at_all_is_still_the_page_it_was()
    {
        // Every write-up before this change, and any bundle whose record is missing: the model's
        // sections are the whole page, and nothing is claimed about turns that were not read.
        var html = CatchupReport.Render("advent", Report, Facts);

        Assert.DoesNotContain(">Events</button>", html);
        Assert.DoesNotContain(">Items</button>", html);
        Assert.Contains(">Characters</button>", html);
        Assert.Contains(">The map</button>", html);
    }

    [Fact]
    public void The_record_is_read_back_the_way_the_core_wrote_it()
    {
        // The reader the page depends on, tested where it lives: field names in whatever case the
        // writer used, a half-written line from a crash skipped rather than guessed at, and the
        // order kept as it was written.
        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(Path.Combine(state, "observations"));
        File.WriteAllLines(
            Path.Combine(state, "observations", "advent.jsonl"),
            [
                """{"turn":1,"input":"look","output":"You are in a room.","fingerprint":"Room#aaaa"}""",
                """{"Turn":2,"Input":"north","Output":"A corridor.","Fingerprint":"Hall#bbbb"}""",
                // A half-written line from a crash: not JSON, so not a turn that happened.
                "{\"turn\":3,\"input\":\"look\"",
                "",
            ]);

        var turns = ObservationLog.All(state, "advent");

        Assert.Equal(2, turns.Count);
        Assert.Equal("look", turns[0].Input);
        Assert.Equal("north", turns[1].Input);
        Assert.Equal("Hall#bbbb", turns[1].Fingerprint);
        Assert.Empty(ObservationLog.All(state, "a-game-nobody-played"));
    }

    [Fact]
    public void The_map_puts_north_above_and_east_to_the_right_of_the_room_it_was_walked_from()
    {
        // The map has to look like a map, which is a claim about geometry rather than about markup:
        // a room reached by `north` is drawn one cell up from the room it was reached from, `east`
        // is one cell right, and a room's own number in the evening's order has nothing to do with
        // where it goes. The first version put them in a lattice four to a row, which is why it was
        // correct as a diagram and unreadable as a map.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Hall\nA narrow hall.", "Hall#aaaa", 10),
            new(2, "north", "Attic\nDust and boxes.", "Attic#bbbb", 11),
            new(3, "east", "Rooftop\nWind, and a long way down.", "Rooftop#cccc", 12),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var rooms = Rooms(html);
        Assert.Equal(3, rooms.Count);
        var hall = rooms.Single(room => room.Name == "Hall");
        var attic = rooms.Single(room => room.Name == "Attic");
        var rooftop = rooms.Single(room => room.Name == "Rooftop");

        Assert.True(attic.Y < hall.Y, $"the attic is above the hall: {attic} {hall}");
        Assert.Equal(hall.X, attic.X);
        Assert.True(rooftop.X > attic.X, $"the rooftop is east of the attic: {rooftop} {attic}");
        Assert.Equal(attic.Y, rooftop.Y);
    }

    [Fact]
    public void The_events_say_what_happened_rather_than_what_was_typed()
    {
        // A row per turn is a log. What the tab is for is the evening as somebody would tell it — "in
        // the attic we took the lantern, tried `xyzzy` and tried south" — because the sequence is the
        // thing a returning player lost, not the individual keystrokes. Four shapes: a move, something
        // done with a thing, a verb that is a whole act, and anything else, which is quoted as the
        // attempt it was because the page does not pretend to know what `xyzzy` did.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Hall\nA narrow hall.", "Hall#aaaa", 10),
            new(2, "north", "Attic\nDust and boxes.", "Attic#bbbb", 11),
            new(3, "take lantern", "Taken.", "Attic#bbbb", 11),
            new(4, "xyzzy", "A hollow voice says \"Fool.\".", "Attic#bbbb", 11),
            new(5, "south", "The way is blocked by a crate.", "Attic#bbbb", 11),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);

        Assert.Contains("In <strong>Hall</strong> we looked around", html);
        Assert.Contains("then went north to <strong>Attic</strong>", html);
        Assert.Contains("took the lantern", html);
        Assert.Contains("<code>xyzzy</code>", html);
        Assert.Contains("tried south", html);

        // The game's own words are the reason, and the sentence's own full stop is the page's job.
        Assert.Contains("The way is blocked by a crate", html);
        Assert.DoesNotContain("blocked by a crate.", html);

        // And it is not a list of commands: no turn numbers on the front of rows any more.
        Assert.DoesNotContain("<strong>5.</strong>", html);
    }

    [Fact]
    public void Doing_the_same_thing_again_is_one_clause_with_the_count_on_it()
    {
        // The roll-up, measured first in the live record: five examinations of the same poodle are one
        // fact about an evening, and five rows of it are a log. The count is the honest way to say it,
        // and it is per place, because in a new place it is a new thing to say.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Outside Cottage\nA poodle blocks the road.", "cottage#aaaa", 10),
            new(2, "examine poodle", "The poodle is tiny.", "cottage#aaaa", 10),
            new(3, "examine poodle", "The poodle is tiny.", "cottage#aaaa", 10),
            new(4, "examine poodle", "The poodle is tiny.", "cottage#aaaa", 10),
            new(5, "examine cottage", "It is a tidy little place.", "cottage#aaaa", 10),
            new(6, "examine cottage", "It is a tidy little place.", "cottage#aaaa", 10),
            new(7, "north", "Hilltop\nA hill above the village.", "hilltop#bbbb", 11),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);
        var events = html[html.IndexOf(">Events</button>", StringComparison.Ordinal)..];

        Assert.Contains("looked at the poodle 3 times", events);
        Assert.Contains("looked at the cottage twice", events);
        Assert.Equal(1, Count(events, "looked at the poodle"));

        // One paragraph to a place, and the move out of it closes the paragraph.
        Assert.Contains("In <strong>Outside Cottage</strong> we", events);
        Assert.Contains("then went north to <strong>Hilltop</strong>.", events);
    }

    [Fact]
    public void A_move_that_moved_nothing_says_why_in_the_games_own_words()
    {
        // This is what makes it a history rather than a summary: *we tried north, then we went west*
        // is a summary of nothing. The poodle in the road is the reason the next thing tried was the
        // thing that was tried, and the game said it in as many words.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Outside Cottage\nA poodle blocks the road.", "cottage#aaaa", 10),
            new(2, "north", "The poodle won't let you go that way.", "cottage#aaaa", 10),
            new(3, "knock on door", "The poodle won't let you near the cottage.", "cottage#aaaa", 10),
            new(4, "west", "Hilltop\nA hill above the village.", "hilltop#bbbb", 11),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);

        Assert.Contains("In <strong>Outside Cottage</strong> we", html);
        Assert.Contains("tried north \u2014 <em>The poodle won", html);
        Assert.Contains("let you go that way</em>", html);
        Assert.Contains("knocked on the door", html);
        Assert.Contains("then went west to <strong>Hilltop</strong>", html);
    }

    [Fact]
    public void A_story_speaking_by_itself_gets_a_line_of_its_own()
    {
        // The licence, a restore's acknowledgement, a scene change: nobody's hand was on the keyboard
        // and the turn belongs to no place, so it cannot be rolled into a paragraph — and dropping it
        // would lose the one thing on the record the player did not do.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "", "\"Behind you!\" cries the Princess.\n\nHilltop\nA hill above the village.", "hilltop#aaaa", 10),
            new(2, "north", "Post Office\nA little brick building.", "office#bbbb", 11),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);

        Assert.Contains("The story spoke on its own: ", html);
        Assert.Contains("Behind you!", html);
        Assert.Contains("In <strong>Hilltop</strong> we went north to <strong>Post Office</strong>.", html);
    }

    [Fact]
    public void The_items_say_what_was_done_with_each_thing_in_the_order_it_was_done()
    {
        // What happened to the things, not what is in hand: the mailbox was opened, the lantern was
        // taken and then put down, and the list is the evening's rather than a snapshot of it. The
        // game's own last answer is quoted underneath, which is the only thing on the page that
        // knows what is in the player's hands now.
        var record = new List<ObservationLog.RecordedTurn>
        {
            new(1, "look", "Hall\nA narrow hall.", "Hall#aaaa", 10),
            new(2, "take lantern", "Taken.", "Hall#aaaa", 10),
            new(3, "inventory", "You are carrying:\n  a brass lantern", "Hall#aaaa", 10),
            new(4, "drop lantern", "Dropped.", "Hall#aaaa", 10),
            new(5, "open mailbox", "Opening the small mailbox reveals a leaflet.", "Hall#aaaa", 10),
        };

        var html = CatchupReport.Render("advent", Report, Facts, record);

        Assert.Contains("<strong>lantern</strong>", html);
        Assert.Contains("took (turn 2), put down (turn 4)", html);
        Assert.Contains("opened (turn 5)", html);
        Assert.True(
            html.IndexOf("<strong>lantern</strong>", StringComparison.Ordinal)
                < html.IndexOf("<strong>mailbox</strong>", StringComparison.Ordinal),
            "the lantern was handled first, so it is listed first");
        Assert.Contains("own last list", html);
        Assert.Contains("a brass lantern", html);
    }

    private static int Count(string text, string needle) =>
        text.Split(needle).Length - 1;

    /// <summary>
    /// The rooms on the diagram, in the order they are drawn: the disc, and the name underneath it.
    /// <para>
    /// Read out of the svg rather than asserted against a whole string, because the two things
    /// these tests are about are where a room *is* and which side of another room it is on — and
    /// that is a relationship between two numbers rather than a piece of text.
    /// </para>
    /// </summary>
    private static List<(string Name, int X, int Y)> Rooms(string html)
    {
        var svg = html[html.IndexOf("<svg", StringComparison.Ordinal)..html.IndexOf("</svg>", StringComparison.Ordinal)];
        var rooms = new List<(string Name, int X, int Y)>();
        foreach (var disc in svg.Split("<circle class=\"room\" ").Skip(1))
        {
            var x = int.Parse(disc[(disc.IndexOf("cx=\"", StringComparison.Ordinal) + 4)..].Split('"')[0]);
            var y = int.Parse(disc[(disc.IndexOf("cy=\"", StringComparison.Ordinal) + 4)..].Split('"')[0]);
            var name = disc.Split("text-anchor=\"middle\">")[1].Split('<')[0];
            rooms.Add((name, x, y));
        }

        return rooms;
    }
}
