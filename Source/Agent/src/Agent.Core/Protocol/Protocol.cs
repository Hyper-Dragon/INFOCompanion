using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Agent.Core.Protocol;

/// <summary>Protocol facts shared by both ends.</summary>
public static class SeatProtocol
{
    /// <summary>Must match the core's <c>PROTOCOL_VERSION</c>; a mismatch is fatal.</summary>
    public const int Version = 1;

    public const string ClientName = "infocompanion-agent";
}

/// <summary>Message types on the wire (§6.3, §6.4).</summary>
public static class MessageTypes
{
    // core → agent
    public const string Welcome = "welcome";
    public const string GameOutput = "game_output";
    public const string TurnComplete = "turn_complete";
    public const string System = "system";

    /// <summary>
    /// The words the current game's parser recognises, read from the story file's own
    /// dictionary. Additive on top of §6.3: an older client that does not know the
    /// type ignores it and falls back to learning from the parser's refusals.
    /// </summary>
    public const string Vocabulary = "vocabulary";

    // agent → core
    public const string Hello = "hello";
    public const string GameInput = "game_input";
    public const string Chat = "chat";

    /// <summary>
    /// The player asked the companion to type one line — <c>/type</c> (03_session.md §8.1 (`S53`)). A
    /// <b>request</b>, not a route: the companion may object once and then types it.
    /// Additive on top of §6.4; an agent that does not know the type ignores it, which is
    /// exactly how <c>/type</c> behaved before — the core typed the line itself.
    /// </summary>
    public const string Handover = "handover";

    /// <summary>
    /// The player is composing a line and has not sent it (issue #42). Additive the same way as
    /// the rest: an agent that does not know the type ignores it, and one that never receives it
    /// behaves exactly as before.
    /// </summary>
    public const string PlayerTyping = "player_typing";

    /// <summary>
    /// What the line being composed in a pane's box looks like, in the one respect the companion is
    /// allowed to act on (03_session.md §6 (`S41`–`S43`)): whether its first character is a space. Additive like the rest — an
    /// agent that does not know the type ignores it and behaves exactly as it did before, which is
    /// the whole contract for a new message here.
    /// <para>
    /// One bit, and never the text: the same rule as <see cref="PlayerTyping"/>, which carries
    /// nothing at all.
    /// </para>
    /// </summary>
    public const string PlayerComposing = "player_composing";

    /// <summary>
    /// The companion's reasoning, said out loud (04_companion.md §4 (`C26`–`C28`)). Additive: a core that does
    /// not know the type ignores it, and the reasoning was previously only ever written to
    /// the agent's own log — which is why it read as terse.
    /// </summary>
    public const string Thought = "thought";

    /// <summary>
    /// One chunk of that reasoning, as it is written. Additive the same way: the finished
    /// <see cref="Thought"/> still carries the whole text, so the log and <c>/think</c> lose
    /// nothing. It exists because a companion thinking hard looked like a hung terminal —
    /// measured, 163 of 248 calls in one evening left the screen silent for two seconds or
    /// more, and the longest were twenty-two.
    /// </summary>
    public const string ThoughtDelta = "thought_delta";

    public const string Ask = "ask";
    public const string Proposal = "proposal";

    /// <summary>
    /// "Still thinking" — sent once when a model call has been in flight longer than
    /// <c>working_indicator_seconds</c>. It carries nothing: the core owns the words, exactly as
    /// it does for proposals. Additive on top of §6.4; a core that does not know the type ignores
    /// it, and nothing about the call changes either way.
    /// <para>
    /// Measured on 2026-09-16: p95 was 15.9s, the longest call was 45.7s, and once the player
    /// asked "james...you there?" mid-delivery. The thought deltas do not start until the model
    /// does, so up to that point a companion thinking hard and a hung terminal are the same
    /// screen.
    /// </para>
    /// </summary>
    public const string Working = "working";

    /// <summary>
    /// The companion has taken a turn and the first model call is away (03_session.md §7 (`S44`–`S45`)). Sent once
    /// per turn, before the first call leaves; the core turns it into an escape the page watches,
    /// which is what lets the lamp say "working" through the seconds in which nothing has arrived
    /// yet. Additive on top of §6.4 — a core that does not know the type ignores it.
    /// <para>
    /// Per <em>turn</em> and not per call: one reply is several calls (retrieval rounds, and the
    /// repair of a move that missed), so a fact per call would make the lamp strobe.
    /// </para>
    /// </summary>
    public const string TurnStarted = "turn_started";

    /// <summary>
    /// That turn's reply has been delivered. The other half of <see cref="TurnStarted"/>, and sent
    /// only when one was sent: a pass that made no call at all — a tick that decided to say nothing
    /// — lights nothing and sends nothing.
    /// </summary>
    public const string TurnFinished = "turn_finished";

    /// <summary>
    /// One tool or extension call, for the panel while the tool trace is on (03_session.md §4.3 (`S32`–`S34`)).
    /// Diagnosis rather than conversation: the core renders it as a notice, and only while
    /// <c>/debugon</c> is set — which is also why nothing is sent when the trace is off.
    /// </summary>
    public const string Tool = "tool";
}

/// <summary>One envelope, exactly as it appears on the wire.</summary>
public sealed class Envelope
{
    [JsonPropertyName("v")]
    public ulong V { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("seq")]
    public ulong Seq { get; set; }

    [JsonPropertyName("ts")]
    public string Ts { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    /// <summary>Deserialise the payload, returning null if it does not fit the shape.</summary>
    public T? PayloadAs<T>()
        where T : class =>
        PayloadAs<T>(out _);

    /// <summary>
    /// Deserialise the payload, reporting <em>why</em> it did not fit the shape.
    /// <para>
    /// The reason is returned rather than thrown, because every caller treats an unreadable
    /// payload as "not for me" and this type sits below the logger. Callers that warn about a
    /// payload they did not understand must pass it on: a field the two ends disagree about is a
    /// bug in this repository, and the last one (a nullable turn number declared non-nullable)
    /// spent its whole life disguised as a line reading "payload was not understood", which
    /// looks like a routing problem rather than a schema one.
    /// </para>
    /// </summary>
    /// <param name="problem">Why the payload could not be read; null when it could.</param>
    public T? PayloadAs<T>(out string? problem)
        where T : class
    {
        problem = null;
        if (Payload is null)
        {
            return null;
        }

        try
        {
            return Payload.Deserialize<T>(ProtocolJson.Options);
        }
        catch (JsonException ex)
        {
            // The message names the field and the position, which is the whole diagnosis:
            // "The JSON value could not be converted to System.Int64. Path: $.resumed_at_turn".
            problem = $"{typeof(T).Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Read a top-level string field without a typed DTO.</summary>
    public string? StringField(string name) =>
        Payload is JsonObject jobject && jobject.TryGetPropertyValue(name, out var value)
            ? value?.GetValue<string>()
            : null;
}

/// <summary>Serialisation settings for everything on the wire.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>RFC 3339 UTC, the timestamp format the protocol uses.</summary>
public static class Timestamps
{
    public static string Rfc3339Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Rfc3339(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>`player_composing` — what the player's line in the box begins with (03_session.md §6 (`S41`–`S43`)).</summary>
public sealed class ComposingPayload
{
    /// <summary>
    /// Does the line being composed begin with a space?
    /// <para>
    /// The requester's own signal: *"a space typed as the first character of a line, in a pane that
    /// accepts input, is the signal to pause the companion while the player types."* The field is
    /// named for the fact rather than for the hold, because the core reports what is in the box and
    /// the companion decides what it means.
    /// </para>
    /// </summary>
    [JsonPropertyName("starts_with_space")]
    public bool StartsWithSpace { get; set; }
}

/// <summary>`welcome` — the handshake snapshot (§6.3).</summary>
public sealed class WelcomePayload
{
    [JsonPropertyName("game_title")]
    public string GameTitle { get; set; } = string.Empty;

    /// <summary>
    /// The game's short name. An additive field on top of §6.3: the agent needs it
    /// to find <c>state/notebook/&lt;stem&gt;.md</c>, and deriving it from a title is
    /// guesswork.
    /// </summary>
    [JsonPropertyName("game_stem")]
    public string GameStem { get; set; } = string.Empty;

    [JsonPropertyName("turn_count")]
    public long TurnCount { get; set; }

    [JsonPropertyName("transcript_tail")]
    public List<string>? TranscriptTail { get; set; }

    /// <summary>
    /// What the player calls the companion. Additive on top of §6.3, so the persona
    /// does not have to hardcode a name and renaming is one line of configuration.
    /// </summary>
    [JsonPropertyName("agent_name")]
    public string AgentName { get; set; } = "companion";

    /// <summary>
    /// Which attempt at this game this is (02_game_boundary.md §6 (`G27`–`G34`)). The core keeps the bookkeeping and hands
    /// the number over; whether the companion ever says it out loud is the companion's business.
    /// Zero means the core has no run book for this game.
    /// </summary>
    [JsonPropertyName("run")]
    public long Run { get; set; }

    /// <summary>
    /// Which fork of the world inside that attempt: a restore moves the companion without making
    /// this a different go (02_game_boundary.md §6 (`G27`–`G34`)).
    /// </summary>
    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    /// <summary>
    /// Did the core come back to a position rather than starting the story? Together with
    /// <see cref="ResumedFrom"/> this is the fact that stops a companion claiming a world its notes
    /// remember and the screen does not.
    /// </summary>
    [JsonPropertyName("resumed")]
    public bool Resumed { get; set; }

    /// <summary>The save the core came back with, when it did.</summary>
    [JsonPropertyName("resumed_from")]
    public string? ResumedFrom { get; set; }

    /// <summary>
    /// The core's turn counter when that checkpoint was taken (05_memory.md §5 (`M23`–`M26`)).
    /// <para>
    /// Load-bearing rather than polite: the autosave runs every few turns, so the world that just
    /// came back can be several moves behind the conversation. Without the turn the companion
    /// picks the conversation up where it left it and describes a world the checkpoint has already
    /// undone — which is on record, on 09-17, as a confident theory about a vanishing stone.
    /// </para>
    /// <para>
    /// <b>Null means the core has no note beside the checkpoint</b> — it serialises <c>None</c> as
    /// null (05_memory.md §5 (`M23`–`M26`)) — and only the companion's own state spells that absence as zero. It is
    /// nullable because a fresh start has no record to read, while a resumed one always does.
    /// </para>
    /// </summary>
    [JsonPropertyName("resumed_at_turn")]
    public long? ResumedAtTurn { get; set; }

    /// <summary>When the checkpoint was written, RFC 3339, when the core knows.</summary>
    [JsonPropertyName("resumed_saved_at")]
    public string? ResumedSavedAt { get; set; }
}

/// <summary>
/// `vocabulary` — the words the current game's parser recognises (§10.4).
/// <para>
/// Read by the core out of the story file's dictionary, which is the only
/// authoritative source: Adventure's parser does not know `follow`, and The
/// Witness's does, so a fixed list of English verbs can never be right.
/// </para>
/// </summary>
public sealed class VocabularyPayload
{
    [JsonPropertyName("game_stem")]
    public string GameStem { get; set; } = string.Empty;

    /// <summary>Z-machine version the words were read from.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>
    /// Z-characters of text a dictionary entry holds: 6 for V1-3, 9 for V4+. Typed
    /// words are cut to this before lookup, which is how a V3 game treats
    /// "inventory" and the stored "invent" as the same word.
    /// </summary>
    [JsonPropertyName("resolution")]
    public int Resolution { get; set; }

    [JsonPropertyName("words")]
    public List<string>? Words { get; set; }
}

/// <summary>`game_output` — a chunk of raw frotz text (§6.3).</summary>
public sealed class GameOutputPayload
{
    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

/// <summary>`turn_complete` — a framed turn (§6.3, §7).</summary>
public sealed class TurnCompletePayload
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// The interpreter's own screen, when the story was waiting for a key (issue #19).
    /// <para>
    /// <c>awaiting_command: false</c> says <em>"the game is waiting for something that is not a
    /// command; the companion is the only thing that can answer"</em> — and until this field
    /// existed on this side, the companion was asked to answer a prompt whose words it had never
    /// been shown. The dumb interface prints a row only when a cell has changed <em>and</em> is not
    /// blank, so a licence form or a chapter menu drawn once and left alone is on the screen for the
    /// whole game and in the output for none of it.
    /// </para>
    /// <para>
    /// Found 2026-09-19: the core had been sending this since issue #19 and this type dropped it on
    /// the floor, which is why bureaucracy and border_zone read as unplayable for a week.
    /// </para>
    /// <para>
    /// <c>null</c> when the core sent none — an interpreter without the tool channel, or an
    /// ordinary turn that came back to its <c>&gt;</c> prompt.
    /// </para>
    /// </summary>
    [JsonPropertyName("screen")]
    public string? Screen { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("turn")]
    public long Turn { get; set; }

    /// <summary>
    /// Did the turn end at frotz's own <c>&gt;</c> prompt, ready for a command?
    /// <para>
    /// <c>false</c> means the game is waiting for something that is not a command — a
    /// chapter menu, a licence form, a <c>(R)estore?</c> offer. The words are the story's,
    /// so the core cannot read them and no wording table could cover them; the companion is
    /// the only thing in the system that can answer, and this flag is how it is asked
    /// (issue #19).
    /// </para>
    /// <para>
    /// Defaults to <c>true</c>, so a core that does not send the field behaves as it always
    /// did: every turn looks like an ordinary one.
    /// </para>
    /// </summary>
    [JsonPropertyName("awaiting_command")]
    public bool AwaitingCommand { get; set; } = true;
    /// <summary>
    /// Where the interpreter says the player is: its own object number for the room
    /// (02_game_boundary.md §4.3 (<c>G23</c>)).
    /// <para>
    /// The one place identity the record has, and a fact rather than a reading — the room's
    /// <em>name</em> is drawn into the top row of the screen by cursor addressing, so it is in no
    /// turn's output and nothing here may treat a picture as words.
    /// </para>
    /// <para>
    /// <c>null</c> is <b>cannot say</b>, never a place: a story that keeps its own status line has
    /// no counters for the interpreter to report (02_game_boundary.md §7 (<c>G38</c>)), and an
    /// interpreter with no tool channel cannot be asked.
    /// </para>
    /// </summary>
    [JsonPropertyName("location")]
    public long? Location { get; set; }

    /// <summary>The interpreter's own count of the player's moves, where it keeps the status line.</summary>
    [JsonPropertyName("moves")]
    public long? Moves { get; set; }

    /// <summary>The interpreter's own score, in the same sense as <see cref="Moves"/>.</summary>
    [JsonPropertyName("score")]
    public long? Score { get; set; }

    /// <summary>
    /// The hour on the interpreter's own clock, for a game that keeps one instead of counters.
    /// <para>
    /// A story with a clock has the time of day on its status line where a scored game has the
    /// score and the moves, and <c>null</c> means this game keeps no clock — never midnight.
    /// </para>
    /// </summary>
    [JsonPropertyName("hours")]
    public long? Hours { get; set; }

    /// <summary>The minute on that clock, in the same sense as <see cref="Hours"/>.</summary>
    [JsonPropertyName("minutes")]
    public long? Minutes { get; set; }}

/// <summary>`chat` — a player line sent to the companion (§6.3).</summary>
public sealed class ChatPayload
{
    [JsonPropertyName("speaker")]
    public string Speaker { get; set; } = "player";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>`system` — a session event (§6.3).</summary>
public sealed class SystemPayload
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>
    /// Free text. One documented convention: for <c>game_changed</c> this is the
    /// game's stem, so the companion knows which notebook to load.
    /// </summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}
