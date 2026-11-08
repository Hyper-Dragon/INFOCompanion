using System.Runtime.InteropServices;
using System.Text;
using Agent.Core.Config;
using Agent.Core.Logging;
using Agent.Core.Loop;
using Agent.Core.Protocol;
using Agent.Llm;

namespace Agent.Core;

/// <summary>
/// Entry point for the co-player agent.
/// <para>
/// The agent is a **seat**: a child process of the Rust daemon that speaks JSONL over
/// its stdin/stdout. There is no network port and no auth surface between them — the
/// OS pipe is the authenticated channel (§2.3).
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // stdout belongs to the protocol alone (§6.1). The real stdout is captured for
        // the seat, and Console.Out is pointed at stderr, so even a stray
        // Console.WriteLine elsewhere in the process cannot corrupt the wire.
        var stdoutForProtocol = Console.OpenStandardOutput();
        var stderr = Console.Error;
        Console.SetOut(stderr);

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var stdout = new StreamWriter(stdoutForProtocol, new UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n", // JSONL framing is \n, never \r\n
        };

        var verbose = args.Any(argument => argument is "--verbose" or "-v");
        var stateDirectory = Environment.GetEnvironmentVariable("INFOCOMPANION_STATE_DIR") is { Length: > 0 } configured
            ? configured
            : "/state";

        // The state directory must already be claimed, and in the shape this build reads
        // (02_game_boundary.md §6.4 (`G46`)): there is no backward compatibility, so an unclaimed
        // or differently-shaped directory is refused rather than half-read. Nothing below this
        // point touches the directory, and the log is on stderr because the agent's own log lives
        // in it.
        if (StateShape.Refuse(stateDirectory) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 1;
        }

        var (config, configSource, configProblem) = AgentConfig.Load(stateDirectory);

        // The endpoint and the model are the file's, unless the environment names them: an address
        // is deployment rather than tuning, and the thing a person changes when they point this at
        // another provider. Without this, a deployment with no mount could not move off the
        // endpoint baked into the config file, which is the one setting whose whole purpose is to
        // be somebody else's.
        config = config.WithEndpoint(
            Environment.GetEnvironmentVariable("INFOCOMPANION_LLM_BASE_URL"),
            Environment.GetEnvironmentVariable("INFOCOMPANION_LLM_MODEL"));

        await using var log = AgentLog.Start(Path.Combine(stateDirectory, "logs"), config.LogKeep, verbose);

        log.Info(
            "agent starting",
            new
            {
                version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0",
                protocol = SeatProtocol.Version,
                state_directory = stateDirectory,
                config = configSource ?? "(built-in defaults)",
                endpoint = config.BaseUrl,
                model = config.Model,
                pid = Environment.ProcessId,
            });

        if (configProblem is not null)
        {
            log.Warn("could not read the config file; using the documented defaults", new { problem = configProblem });
        }

        var enginePath = ResolvePrompt("INFOCOMPANION_ENGINE_PATH", config.EnginePath, "engine.txt");

        // Which personality: an explicit full path wins, otherwise the slug. The daemon resolves
        // it by the same rule — if you change one, change the other, or the companion will be
        // called one thing by the harness and another by itself.
        var slug = Environment.GetEnvironmentVariable("INFOCOMPANION_PERSONA");
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = config.Persona;
        }

        slug = string.IsNullOrWhiteSpace(slug) ? "james" : slug.Trim();
        var personaPath = ResolvePrompt(
            "INFOCOMPANION_PROMPT_PATH",
            Path.Combine(config.PersonaDirectory, slug + ".txt"),
            slug + ".txt");

        if (enginePath is null || personaPath is null)
        {
            log.Error(
                "no prompts found; the companion cannot run without the engine and a personality",
                new
                {
                    engine = enginePath,
                    persona = personaPath,
                    configured_engine = config.EnginePath,
                    configured_persona = config.Persona,
                });
            return 1;
        }

        var engine = await File.ReadAllTextAsync(enginePath).ConfigureAwait(false);
        var (persona, personaName) = Persona.Split(await File.ReadAllTextAsync(personaPath).ConfigureAwait(false));

        // The player's name and pronouns are configuration, because the player has no file to
        // declare them in — and the companion's pronouns are here too rather than in the persona,
        // because nobody should have to edit a character file to stop a model guessing at
        // somebody's gender.
        var playerName = Environment.GetEnvironmentVariable("INFOCOMPANION_PLAYER_NAME");
        var playerPronouns = Environment.GetEnvironmentVariable("INFOCOMPANION_PLAYER_PRONOUNS");
        var personaPronouns = Environment.GetEnvironmentVariable("INFOCOMPANION_AGENT_PRONOUNS");

        // Engine first, then the character: the frame is shared by every personality, and a
        // personality is what sits on top of it.
        var systemPrompt = engine.TrimEnd() + "\n\n" + persona.Trim() + "\n";
        log.Info(
            "prompts loaded",
            new
            {
                engine = enginePath,
                persona = personaPath,
                name = personaName,
                player = playerName,
                agent_pronouns = personaPronouns,
                player_pronouns = playerPronouns,
                characters = systemPrompt.Length,
            });

        // The key comes from the environment only: never the repo, never the config
        // file (§9.3). Any OpenAI-compatible endpoint's key belongs here, so the variable is named
        // for the endpoint rather than for one vendor (10_delivery.md §8.1 (`D18`)).
        var apiKey = Environment.GetEnvironmentVariable("INFOCOMPANION_LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // The name this used to carry said which vendor the project was built against. It is
            // still read, for one release, and named when it is what carried the key: a migration
            // that happens silently is one nobody finishes.
            apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                log.Warn(
                    "the model's key came from DEEPSEEK_API_KEY: set INFOCOMPANION_LLM_API_KEY instead, the old name is to be retired");
            }
        }

        OpenAiCompatibleClient? llm = null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            log.Warn(
                "INFOCOMPANION_LLM_API_KEY is not set: the companion will hold its seat but stay silent, and will say so to the player");
        }
        else
        {
            llm = new OpenAiCompatibleClient(config.ToLlmSettings(), apiKey, message => log.Warn("llm: " + message));
        }

        // Where the story files are, so a game's optional sidecar can be found beside it. The
        // daemon is told the same thing by the same variable, because it is the same directory.
        var gamesDirectory = Environment.GetEnvironmentVariable("INFOCOMPANION_GAMES_DIR") is { Length: > 0 } configuredGames
            ? configuredGames
            : config.GamesDirectory;

        await using var seat = new SeatConnection(
            stdin,
            stdout,
            message => log.Warn("seat: " + message),
            config.SeatWriterWaitSeconds);
        var loop = new AgentLoop(
            seat,
            config,
            log,
            stateDirectory,
            systemPrompt,
            llm,
            personaName,
            playerName,
            personaPronouns,
            playerPronouns,
            gamesDirectory);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        // And the same thing on the signal a container actually sends. `docker compose stop` and
        // `restart` both SIGTERM this process rather than pressing Ctrl-C, and .NET does not unwind
        // `await using` for it: the loop never returns, the log is never disposed, and the sitting's
        // own summary — the counts a bundle is read for — is never written. Measured 2026-10-01 across
        // a full sitting, a disconnect, a restart and a clean stop: no log carried a `session_end`.
        // Whether the signal exists at all is the platform's business; on Windows there is none to
        // register, and Ctrl-C above is the whole of it there.
        using var termination = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    cancellation.Cancel();
                });

        try
        {
            await loop.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.Info("cancelled");
        }
        catch (Exception ex)
        {
            log.Error("the loop ended unexpectedly", new { error = ex.ToString() });
            return 1;
        }

        // stdin closing is the core's way of saying goodbye (§8.2).
        log.Info("stdin closed by the core; exiting cleanly", new { sent = seat.Sent, received = seat.Received });
        llm?.Dispose();
        return 0;
    }

    /// <summary>
    /// Find one prompt file: explicit environment override, then the configured path, then
    /// beside the binary (which keeps local development working without the container).
    /// </summary>
    private static string? ResolvePrompt(string environmentVariable, string configured, string besideBinary)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment)
        {
            candidates.Add(fromEnvironment);
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, besideBinary));
        return candidates.FirstOrDefault(File.Exists);
    }
}
