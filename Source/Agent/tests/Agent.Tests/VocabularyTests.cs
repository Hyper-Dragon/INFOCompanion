using System.Text.Json;
using Agent.Core.Loop;

namespace Agent.Tests;

/// <summary>
/// Whether a word exists at all (05_memory.md §8 (`M42`)).
/// <para>
/// Measured 2026-09-29: `pockets`, `cape`, `heads` and `soot` cost the companion turns to discover
/// they are not words — *"Pockets isn't a word, so that's out"* — while the story's own word list was
/// already in the daemon's hands. The difference between a word the game has never heard of and one it
/// heard and refused is the difference between changing the word and changing the plan.
/// </para>
/// </summary>
public class VocabularyTests
{
    private const string Welcome =
        """{"v":1,"type":"welcome","seq":1,"ts":"now","payload":{"game_title":"Wishbringer","game_stem":"wishbringer","turn_count":0,"transcript_tail":[],"agent_name":"Quill","run":1,"generation":1,"resumed":false,"resumed_from":null}}""";

    /// <summary>The story's parser words, which is where the core states them.</summary>
    private const string Vocabulary =
        """{"v":1,"type":"vocabulary","seq":2,"ts":"now","payload":{"game_stem":"wishbringer","version":3,"resolution":6,"words":["look","take","north"]}}""";

    private const string PlayerLine =
        """{"v":1,"type":"chat","seq":900,"ts":"now","payload":{"speaker":"player","text":"is soot a word?"}}""";

    /// <summary>Ask the game's dictionary one word, through the tool the companion would use.</summary>
    private static async Task<string> Asked(string word)
    {
        var llm = new ScriptedLlm();
        llm.Reply(calls: [(CompanionTools.State, "{\"word\":\"" + word + "\"}")]);
        llm.Reply("Right.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(Vocabulary);
        seat.Say(PlayerLine);
        await harness.UntilAsync(
            _ => llm.Requests.Any(request => request.Any(message => message.ToolCallId is not null)),
            timeoutMs: 20000);

        foreach (var sent in Enumerable.Reverse(llm.Requests))
        {
            var result = sent.FirstOrDefault(message => message.ToolCallId is not null);
            if (result is not null)
            {
                return result.Content;
            }
        }

        throw new InvalidOperationException("the call never came back with a result");
    }

    [Fact]
    public async Task A_word_the_game_does_not_hold_is_not_a_word()
    {
        var result = await Asked("soot");

        Assert.Contains("not in this game's dictionary", result);
        Assert.Contains("a different word for the same thing may", result);
    }

    [Fact]
    public async Task A_word_the_game_holds_is_a_word()
    {
        var result = await Asked("take");

        Assert.Contains("is a word the game's own dictionary holds", result);
    }

    [Fact]
    public async Task The_answer_carries_the_verdict_as_a_fact()
    {
        var block = JsonDocument.Parse((await Asked("look")).Split("\n\n").Last()).RootElement;

        Assert.Equal("look", block.GetProperty("word").GetString());
        Assert.True(block.GetProperty("known").GetBoolean());
    }

    [Fact]
    public async Task No_word_list_is_cannot_say_rather_than_a_no()
    {
        // A story whose vocabulary has not arrived is not a story that does not know the word, and
        // the one answer that would send the companion looking for another word is the wrong one.
        var llm = new ScriptedLlm();
        llm.Reply(calls: [(CompanionTools.State, "{\"word\":\"soot\"}")]);
        llm.Reply("Right.");

        var seat = new OpenSeat();
        await using var harness = LoopHarness.StartOn(llm, seat);

        seat.Say(Welcome);
        seat.Say(PlayerLine);
        await harness.UntilAsync(
            _ => llm.Requests.Any(request => request.Any(message => message.ToolCallId is not null)),
            timeoutMs: 20000);

        var result = Enumerable.Reverse(llm.Requests)
            .Select(sent => sent.FirstOrDefault(message => message.ToolCallId is not null))
            .FirstOrDefault(result => result is not null)
            ?? throw new InvalidOperationException("the call never came back with a result");

        Assert.Contains("cannot say", result.Content);
        Assert.DoesNotContain("not in this game's dictionary", result.Content);
    }
}
