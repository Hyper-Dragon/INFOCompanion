using Agent.Core.Config;
using Agent.Core.Loop;
using Agent.Notebook;

namespace Agent.Tests;

/// <summary>
/// A reset must be "as if the game had never been played" (§5.4).
/// <para>
/// Reported by the player: after a reset the companion kept talking about people from
/// "an earlier run" and called them its own notes. It was not lying — the pre-reset turns
/// were still in its context, so the run it had been asked to forget was still in front of
/// it. The files were being deleted correctly; the memory was not being forgotten.
/// </para>
/// <para>
/// These use the same pieces the loop uses, so they pin the property rather than the
/// wiring: what the model is shown, before and after the reset.
/// </para>
/// </summary>
public class ResetTests
{
    private static AgentConfig Config() => new()
    {
        Model = "deepseek-chat",
        TranscriptTurns = 40,
        TranscriptTokenBudget = 12000,
        NotebookTokenBudget = 6000,
    };

    /// <summary>Exactly what the loop does on a reset, minus the log lines.</summary>
    private static void ForgetTheRun(Transcript transcript)
    {
        transcript.Clear();
    }

    [Fact]
    public void Before_a_reset_the_old_run_is_part_of_what_the_model_sees()
    {
        var transcript = new Transcript();
        transcript.AddGameTurn("look", "You are in the driveway entrance of the Linder property.", 1, byCompanion: false);
        transcript.AddCompanion("Phong says the Linders are in the living room.");

        var context = Render(transcript, "## Key facts\n- Phong is the house man at the Linder house\n");

        Assert.Contains("Linder", context);
        Assert.Contains("Phong", context);
    }

    [Fact]
    public void After_a_reset_nothing_from_the_run_reaches_the_model()
    {
        var transcript = new Transcript();
        transcript.AddGameTurn("look", "You are in the driveway entrance of the Linder property.", 1, byCompanion: false);
        transcript.AddCompanion("Phong says the Linders are in the living room.");
        transcript.AddPlayerLine("who have we met?");

        ForgetTheRun(transcript);

        // The core deletes the notebook file, so the store hands back a fresh skeleton
        // for this game — which is the other half of the promise.
        var context = Render(transcript, NotebookStore.Template("witness"));

        Assert.DoesNotContain("Linder", context);
        Assert.DoesNotContain("Phong", context);
        Assert.DoesNotContain("Monica", context);
        Assert.DoesNotContain("living room", context);

        // And the skeleton really is fresh, rather than the old notes with a new heading.
        Assert.Contains("## Current goals and plan", context);
    }

    [Fact]
    public void The_notebook_on_disk_being_gone_reads_as_a_fresh_start()
    {
        // The core removes the file; the companion must not resurrect it from memory.
        var directory = Path.Combine(Path.GetTempPath(), "ic-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new NotebookStore(directory, "witness");
            store.Write("## Current goals and plan\n- talk to Phong about the Linders\n");

            File.Delete(store.FilePath);

            var text = store.Read();
            Assert.DoesNotContain("Phong", text);
            Assert.Contains("## Current goals and plan", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Render(Transcript transcript, string notebook)
    {
        var assembler = new ContextAssembler(Config(), "You are a companion in a game.");
        var assembled = assembler.Assemble(notebook, transcript.Items, "player said: who have we met?");
        return string.Join("\n", assembled.Messages.Select(message => message.Content));
    }
}
