using System.Text.Json.Nodes;
using Agent.Core.Logging;
using Agent.Llm;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// The session log's own numbers.
///
/// What a review of a bundle does is count things: calls by purpose, the cost, how many replies
/// moved nothing. Clipping was the one such fact that existed only as prose — a warn line saying a
/// reply "may be incomplete", and the ceiling written beside it as the number the pass *wanted*
/// rather than the one sent — so whether a sitting had been clipped was argued over rather than read
/// (`§25` item 3).
/// </summary>
public sealed class AgentLogTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "infocompanion-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ChatResult Reply(int prompt, int completion, FinishReason finish) =>
        new("a line", new Usage(prompt, completion), "deepseek-flash", finish);

    private static List<JsonObject> ReadEvents(string path) =>
        File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();

    [Fact]
    public async Task A_clipped_reply_is_counted_and_has_an_event_of_its_own()
    {
        var directory = TempDirectory();
        var log = AgentLog.Start(directory, keep: 2);

        // Two passes cut off — one of them a retry given more room — and one that was fine.
        log.RecordLlmCall(
            "player-line", Reply(1200, 400, FinishReason.Length), cost: 0.001, latencyMs: 900,
            maxTokens: 16384);
        log.RecordLlmCall(
            "player-line", Reply(2100, 500, FinishReason.Length), cost: 0.002, latencyMs: 950,
            maxTokens: 32768, escalated: true);
        log.RecordLlmCall(
            "report", Reply(9000, 3000, FinishReason.Stop), cost: 0.02, latencyMs: 5000,
            maxTokens: 32768);

        await log.DisposeAsync();

        var events = ReadEvents(log.FilePath);

        // Every call says what the pass was allowed. A row that reports a clip and not the ceiling it
        // was given is a row that cannot be read, which is what the prose warn amounted to.
        var calls = events.Where(e => e["kind"]!.GetValue<string>() == "llm_call").ToList();

        Assert.Equal(3, calls.Count);
        Assert.Equal(16384, calls[0]["max_tokens"]!.GetValue<int>());
        Assert.False(calls[0]["escalated"]!.GetValue<bool>());
        Assert.Equal(32768, calls[1]["max_tokens"]!.GetValue<int>());
        Assert.True(calls[1]["escalated"]!.GetValue<bool>());

        // One row per clip to count, rather than a sentence to search for, carrying the three facts a
        // review needs: which pass, what it was allowed, and how big the prompt was beside it.
        var clipped = events.Where(e => e["kind"]!.GetValue<string>() == "reply_truncated").ToList();

        Assert.Equal(2, clipped.Count);
        Assert.Equal("player-line", clipped[0]["purpose"]!.GetValue<string>());
        Assert.Equal(16384, clipped[0]["max_tokens"]!.GetValue<int>());
        Assert.Equal(1200, clipped[0]["prompt_tokens"]!.GetValue<int>());
        Assert.False(clipped[0]["escalated"]!.GetValue<bool>());
        Assert.True(clipped[1]["escalated"]!.GetValue<bool>());

        // And the sitting's own totals, which is the part nothing had: a rate, by pass, as a number.
        var end = Assert.Single(events, e => e["kind"]!.GetValue<string>() == "session_end");

        Assert.Equal(3, end["llm_calls"]!.GetValue<int>());
        Assert.Equal(2, end["truncated_calls"]!.GetValue<int>());
        Assert.Equal(1, end["escalated_calls"]!.GetValue<int>());
        Assert.Equal(2, end["truncated_by_purpose"]!.AsObject()["player-line"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_sitting_with_no_clip_says_so_with_a_zero_rather_than_saying_nothing()
    {
        // The absent fact is the one a review has to be able to read: a summary that omits the count
        // leaves "no clips" and "this build did not look" looking the same. The same goes for the
        // stretches with nothing new in them (`C39.4`).
        var directory = TempDirectory();
        var log = AgentLog.Start(directory, keep: 2);

        log.RecordLlmCall("mission-step", Reply(14000, 300, FinishReason.Stop), cost: 0.01, maxTokens: 16384);

        await log.DisposeAsync();

        var end = Assert.Single(ReadEvents(log.FilePath), e => e["kind"]!.GetValue<string>() == "session_end");

        Assert.Equal(0, end["truncated_calls"]!.GetValue<int>());
        Assert.Equal(0, end["escalated_calls"]!.GetValue<int>());
        Assert.Equal(0, end["stagnation_consulted"]!.GetValue<int>());
        Assert.Empty(end["truncated_by_purpose"]!.AsObject());
    }

    [Fact]
    public async Task The_sitting_is_summarised_when_the_player_goes_rather_than_only_when_the_process_ends()
    {
        // `S61`, and the half that was missing: the numbers belong where the *sitting* ends. Measured
        // 2026-10-01 — a whole sitting, a player disconnecting, a restart and a clean stop, and no log
        // carried a `session_end` at all, because the agent is one of four processes in a container and
        // nothing gives it a graceful exit. Every count written there was unreadable by its only reader.
        var directory = TempDirectory();
        var log = AgentLog.Start(directory, keep: 2);

        log.RecordLlmCall("mission-step", Reply(1400, 300, FinishReason.Stop), cost: 0.01, maxTokens: 16384);
        log.RecordStagnation("mission-step", turns: 19, moved: 2);
        log.SittingEnded();

        // Read once the writer has let go — a log held open reads fine in the gate's container and not
        // at all on Windows, which is the suite's one long-standing exception. Both rows are asserted
        // together, and the `sitting_end` one is only written by the sitting path, so nothing is lost.
        await log.DisposeAsync();

        var events = ReadEvents(log.FilePath);
        var sitting = Assert.Single(events, e => e["kind"]!.GetValue<string>() == "sitting_end");
        var session = Assert.Single(events, e => e["kind"]!.GetValue<string>() == "session_end");

        Assert.Equal(1, sitting["llm_calls"]!.GetValue<int>());
        Assert.Equal(1, sitting["stagnation_consulted"]!.GetValue<int>());
        Assert.Equal(0, sitting["truncated_calls"]!.GetValue<int>());

        // A summary and not the last word: the session's own still lands if the process ever ends
        // cleanly, and the two are the same fields under different kinds.
        Assert.Equal(1, session["llm_calls"]!.GetValue<int>());
        Assert.Equal(2, events.Count(e => e["kind"]!.GetValue<string>() is "sitting_end" or "session_end"));
    }

    [Fact]
    public async Task A_stretch_with_nothing_new_in_it_is_recorded_when_the_harness_acts_on_it()
    {
        // The sentence in the constraint is true of every call for as long as the stretch lasts. What
        // a bundle cannot get from it is whether the harness *noticed* — whether the stretch was ever
        // put to the companion — and that is what this row and this count are for (`C39.4`).
        var directory = TempDirectory();
        var log = AgentLog.Start(directory, keep: 2);

        log.RecordStagnation("mission-step", turns: 19, moved: 6);

        await log.DisposeAsync();

        var events = ReadEvents(log.FilePath);
        var fired = Assert.Single(events, e => e["kind"]!.GetValue<string>() == "stagnation");

        Assert.Equal("mission-step", fired["purpose"]!.GetValue<string>());
        Assert.Equal(19, fired["turns"]!.GetValue<int>());
        Assert.Equal(6, fired["moved"]!.GetValue<int>());

        var end = Assert.Single(events, e => e["kind"]!.GetValue<string>() == "session_end");

        Assert.Equal(1, end["stagnation_consulted"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_sitting_reports_how_often_a_pass_announced_something_and_did_not_do_it()
    {
        // `§30` item 3, and the one number six sittings were read for by hand: `act_missed` per landed
        // turn — 0.125, 0.14, 0.20, 0.23, 0.43, and 0.24 to 0.54 in the 2026-10-02 bundle. It was the
        // most visible thing in every transcript and the least measurable, because reading it meant
        // counting two names out of a log by hand and dividing.
        var directory = TempDirectory();
        var log = AgentLog.Start(directory, keep: 2);

        for (var turn = 1; turn <= 4; turn++)
        {
            log.Event("turn", new { turn });
        }

        log.Event("act_missed", new { purpose = "mission-step" });

        await log.DisposeAsync();

        var end = Assert.Single(ReadEvents(log.FilePath), e => e["kind"]!.GetValue<string>() == "session_end");

        Assert.Equal(1, end["missed_acts"]!.GetValue<int>());
        Assert.Equal(4, end["landed_turns"]!.GetValue<int>());
        Assert.Equal(0.25, end["missed_per_turn"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_rate_over_no_turns_is_absent_rather_than_zero()
    {
        // Zero clips is a fact about a sitting; zero misses per turn is not an answer to "was this
        // sitting bad", and the two must not read the same. The counts stay, whatever they are.
        var directory = TempDirectory();
        var log = AgentLog.Start(directory, keep: 2);

        await log.DisposeAsync();

        var end = Assert.Single(ReadEvents(log.FilePath), e => e["kind"]!.GetValue<string>() == "session_end");

        Assert.Equal(0, end["missed_acts"]!.GetValue<int>());
        Assert.Equal(0, end["landed_turns"]!.GetValue<int>());
        Assert.Null(end["missed_per_turn"]);
    }
}
