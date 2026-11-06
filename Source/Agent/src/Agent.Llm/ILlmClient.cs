namespace Agent.Llm;

/// <summary>
/// The one thing the loop needs from a model: hand it a conversation, get a reply (§9.3).
/// <para>
/// This exists so the loop can be driven by a <b>scripted</b> model. Nearly every
/// behavioural rule the companion has — whether it acts when nobody asked, whether it keeps
/// talking over its own question, whether a handover gets typed — lives in the loop rather
/// than in the parser, so the only way to test the rules is to run the loop with a model in
/// it. A test that needs an HTTP call, an API key and a live game is not a test anybody
/// runs, which is why these rules kept being "verified" by reading the log afterwards and
/// deciding it looked about right.
/// </para>
/// <para>
/// Deliberately narrower than <see cref="OpenAiCompatibleClient"/>: one method, one endpoint name.
/// Anything a double has to implement is something a double can get wrong.
/// </para>
/// </summary>
public interface ILlmClient
{
    /// <summary>The endpoint actually used, for logs and diagnostics.</summary>
    string Endpoint { get; }

    /// <summary>Send the conversation and return the reply.</summary>
    /// <param name="options">
    /// Per-call overrides. Null means "whatever the settings say", which is the ordinary case.
    /// </param>
    /// <param name="onReasoning">
    /// Each chunk of the reply's reasoning, as it is written, when the provider streams it.
    /// <para>
    /// The finished reply still carries the whole thinking (<see cref="ChatResult.Reasoning"/>),
    /// so a caller that passes null behaves exactly as before — which is what keeps every test
    /// double honest. The callback is awaited in order, so a sink that writes to a socket sees
    /// the words in the order the model produced them.
    /// </para>
    /// </param>
    Task<ChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken,
        CallOptions? options = null,
        Func<string, Task>? onReasoning = null);
}
