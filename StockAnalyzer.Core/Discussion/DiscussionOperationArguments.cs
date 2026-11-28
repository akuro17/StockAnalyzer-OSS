using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The keys each operation of the board accepts at the top level of its arguments object (spec 5.12, wire version 4). The gateway of the process that holds the board
/// refuses any other key before it reads a value, so that a hub that is older than its client never ignores a new optional argument in silence; the refusal is local to
/// the call that used the key and tells what to do. One definition of the lists: they are built from <see cref="DiscussionArgumentNames"/>, and
/// <c>DiscussionOperationArgumentsParityTests</c> compares them with the input schema of every discussion tool.
/// <para>
/// A list holds exactly the keys that the gateway method of the operation reads. The names are the eleven that <see cref="DiscussionHubGateway"/> can receive: the ten
/// wire operations (<see cref="DiscussionOperations.IsWireOperation"/>) and the local name <see cref="DiscussionOperations.Status"/>, which only the process that holds
/// the board calls itself. <see cref="DiscussionOperations.Status"/> and <see cref="DiscussionOperations.StatusHubPart"/> share ONE key set: a remote process forwards the
/// caller's arguments unchanged as <c>status_hub</c> and the board validates <c>commandTail</c> there, while the asking process reads the same value for its own command log.
/// </para>
/// <para>
/// Condition of the "no wire bump for a new optional argument" rule: a client forwards exactly the keys its caller sent. It never adds a key for an omitted argument, not even
/// with the default value, because an older hub that holds this check would then refuse every ordinary call.
/// </para>
/// </summary>
internal static class DiscussionOperationArguments
{
    private static readonly string[] SessionAndToken =
    [
        DiscussionArgumentNames.SessionId,
        DiscussionArgumentNames.ParticipantToken,
    ];

    /// <summary>The one key set of the status, registered under both its names.</summary>
    private static readonly string[] StatusKeys =
    [
        DiscussionArgumentNames.SessionId,
        DiscussionArgumentNames.AuditTail,
        DiscussionArgumentNames.CommandTail,
        DiscussionArgumentNames.IncludeContexts,
    ];

    /// <summary>The one definition of the lists; the two lookup forms below are built from it.</summary>
    private static readonly Dictionary<string, string[]> Source = new(StringComparer.Ordinal)
    {
        [DiscussionOperations.Create] =
        [
            DiscussionArgumentNames.Title,
            DiscussionArgumentNames.Topic,
            DiscussionArgumentNames.TurnMode,
        ],
        [DiscussionOperations.Join] =
        [
            DiscussionArgumentNames.SessionId,
            DiscussionArgumentNames.DisplayName,
        ],
        [DiscussionOperations.Leave] = SessionAndToken,
        [DiscussionOperations.Post] =
        [
            DiscussionArgumentNames.SessionId,
            DiscussionArgumentNames.ParticipantToken,
            DiscussionArgumentNames.Content,
            DiscussionArgumentNames.Pass,
            DiscussionArgumentNames.ReplyToSequence,
            DiscussionArgumentNames.ContextIds,
            DiscussionArgumentNames.BasedOnSequence,
            DiscussionArgumentNames.ClientRequestId,
            DiscussionArgumentNames.Interpretation,
        ],
        [DiscussionOperations.Read] =
        [
            DiscussionArgumentNames.SessionId,
            DiscussionArgumentNames.ParticipantToken,
            DiscussionArgumentNames.AfterSequence,
            DiscussionArgumentNames.Limit,
            DiscussionArgumentNames.WaitSeconds,
            DiscussionArgumentNames.Compact,
            DiscussionArgumentNames.HeaderMode,
            DiscussionArgumentNames.ExcludeEvents,
            DiscussionArgumentNames.ExcludeOwn,
            DiscussionArgumentNames.Tail,
            DiscussionArgumentNames.View,
            DiscussionArgumentNames.WaitForTurn,
        ],
        [DiscussionOperations.Status] = StatusKeys,
        [DiscussionOperations.StatusHubPart] = StatusKeys,
        [DiscussionOperations.Close] = SessionAndToken,
        [DiscussionOperations.AttachPrecheck] = SessionAndToken,
        [DiscussionOperations.AttachCommit] =
        [
            DiscussionArgumentNames.SessionId,
            DiscussionArgumentNames.ParticipantToken,
            DiscussionArgumentNames.Tool,
            DiscussionArgumentNames.Arguments,
            DiscussionArgumentNames.Result,
            DiscussionArgumentNames.Note,
            DiscussionArgumentNames.AnalysisIntent,
        ],
        [DiscussionOperations.GetAnalysis] =
        [
            DiscussionArgumentNames.SessionId,
            DiscussionArgumentNames.ParticipantToken,
            DiscussionArgumentNames.ContextId,
            DiscussionArgumentNames.IncludeResult,
            DiscussionArgumentNames.Paths,
        ],
    };

    /// <summary>Lookup by text: for the tests, the schema comparison and the documentation of the lists.</summary>
    private static readonly FrozenDictionary<string, FrozenSet<string>> Lists = Source.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Lookup by UTF-8 bytes: the hot form, so that checking a request compares the property names of the parsed JSON in place and never creates a string for a known key.</summary>
    private static readonly FrozenDictionary<string, byte[][]> Utf8Lists = Source.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Select(key => Encoding.UTF8.GetBytes(key)).ToArray(), StringComparer.Ordinal);

    /// <summary>Every operation name that has a list (the ten wire operations and the local status).</summary>
    public static IReadOnlyCollection<string> Operations => Lists.Keys;

    /// <summary>True when the gateway has a list for the operation; for any other name the check does not apply and the gateway keeps its own answer.</summary>
    public static bool HasOperation(string operation) => Lists.ContainsKey(operation);

    /// <summary>The accepted keys of the operation; empty for a name without a list.</summary>
    public static IReadOnlyCollection<string> For(string operation)
        => Lists.TryGetValue(operation, out FrozenSet<string>? keys) ? keys : Array.Empty<string>();

    /// <summary>True when the key (ordinal, case-sensitive) is accepted by the operation; false for a name without a list.</summary>
    public static bool IsKnown(string operation, string key)
        => Lists.TryGetValue(operation, out FrozenSet<string>? keys) && keys.Contains(key);

    /// <summary>
    /// The same test for a property of a parsed arguments object, without creating the name as a string: <see cref="JsonProperty.NameEquals(ReadOnlySpan{byte})"/> compares the
    /// unescaped name ordinally, so an escaped spelling of a known key (a JSON unicode escape for one of its letters) is the same key, exactly as with the text form.
    /// </summary>
    public static bool IsKnown(string operation, JsonProperty property)
    {
        if (!Utf8Lists.TryGetValue(operation, out byte[][]? keys))
        {
            return false;
        }

        foreach (byte[] key in keys)
        {
            if (property.NameEquals(key))
            {
                return true;
            }
        }

        return false;
    }
}
