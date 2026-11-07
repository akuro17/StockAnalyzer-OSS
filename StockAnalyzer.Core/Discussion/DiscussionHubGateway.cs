using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The gateway of the process that holds the board: it reads the JSON arguments, calls the <see cref="DiscussionHub"/> and writes the JSON results of spec 5.14.
/// It re-checks the type of every argument (the request may have come over a pipe from another process), never lets an exception of the hub leave as anything but a
/// <see cref="DiscussionReply"/> failure, and writes strings with the relaxed encoder so that a result is no larger than its text (UTF-8 bytes stay UTF-8 bytes).
/// </summary>
public sealed class DiscussionHubGateway : IDiscussionGateway
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly DiscussionHub _hub;
    private readonly DiscussionSettings _settings;
    private readonly ICommandLog? _commandLog;
    private readonly string _protocolText;

    /// <param name="hub">The board.</param>
    /// <param name="settings">The settings the hub was built with (reported as the effective limits).</param>
    /// <param name="commandLog">The command ring of this process, reported by the status. Null reports an empty list.</param>
    /// <param name="protocolSuffix">Text appended to the protocol text a joining participant receives (the research disclaimer of the MCP tools). Null appends nothing.</param>
    public DiscussionHubGateway(DiscussionHub hub, DiscussionSettings settings, ICommandLog? commandLog = null, string? protocolSuffix = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(settings);
        _hub = hub;
        _settings = settings;
        _commandLog = commandLog;
        _protocolText = string.IsNullOrEmpty(protocolSuffix) ? DiscussionProtocolText.Text : $"{DiscussionProtocolText.Text} {protocolSuffix}";
    }

    public async Task<DiscussionReply> ExecuteAsync(DiscussionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("arguments", "arguments must be a JSON object.");
            }

            var arguments = new Arguments(document.RootElement);
            string json = request.Operation switch
            {
                DiscussionOperations.Create => Create(arguments),
                DiscussionOperations.Join => Join(arguments),
                DiscussionOperations.Leave => Leave(arguments),
                DiscussionOperations.Post => Post(arguments),
                DiscussionOperations.Read => await ReadAsync(arguments, cancellationToken).ConfigureAwait(false),
                DiscussionOperations.Status => Status(StatusHubPart(arguments), arguments),
                DiscussionOperations.StatusHubPart => StatusHubPart(arguments),
                DiscussionOperations.Close => Close(arguments),
                DiscussionOperations.AttachPrecheck => AttachPrecheck(arguments),
                DiscussionOperations.AttachCommit => AttachCommit(document.RootElement, arguments),
                DiscussionOperations.GetAnalysis => GetAnalysis(arguments),
                _ => throw Invalid("operation", "The operation is not known."),
            };
            return DiscussionReply.Ok(json);
        }
        catch (DiscussionException error)
        {
            return DiscussionReply.Fail(error.Code, error.Message, error.Parameter, error.Hint);
        }
        catch (JsonException)
        {
            return DiscussionReply.Fail(DiscussionErrorCodes.InvalidArgument, "The arguments are not valid JSON.", "arguments");
        }
        catch (ObjectDisposedException)
        {
            return DiscussionReply.Fail(DiscussionErrorCodes.HubUnavailable, "The discussion board has been shut down.", hint: "Restart the MCP server.");
        }
    }

    // ---- Argument reading ---------------------------------------------------------------------------------------------------------------

    private static DiscussionException Invalid(string parameter, string message)
        => new(DiscussionErrorCodes.InvalidArgument, message, parameter);

    private readonly struct Arguments
    {
        private readonly JsonElement _root;

        public Arguments(JsonElement root)
        {
            _root = root;
        }

        private bool TryGet(string name, out JsonElement value)
            => _root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

        public string? String(string name)
        {
            if (!TryGet(name, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid(name, $"{name} must be a string.");
        }

        public long? Integer(string name)
        {
            if (!TryGet(name, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : throw Invalid(name, $"{name} must be a whole number.");
        }

        public bool? Boolean(string name)
        {
            if (!TryGet(name, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid(name, $"{name} must be true or false.");
        }

        public long[]? IntegerArray(string name)
        {
            if (!TryGet(name, out JsonElement value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw Invalid(name, $"{name} must be an array of whole numbers.");
            }

            var items = new List<long>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                items.Add(item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long number) ? number : throw Invalid(name, $"{name} must be an array of whole numbers."));
            }

            return items.ToArray();
        }

        public int IntegerOr(string name, int defaultValue)
        {
            long? value = Integer(name);
            if (value is null)
            {
                return defaultValue;
            }

            return value is < int.MinValue or > int.MaxValue ? throw Invalid(name, $"{name} is out of range.") : (int)value;
        }
    }

    // ---- Operations ---------------------------------------------------------------------------------------------------------------------

    private string Create(Arguments arguments)
    {
        DiscussionTurnMode mode = DiscussionTurnMode.Free;
        if (arguments.String("turnMode") is { } name && !DiscussionNames.TryParseTurnMode(name, out mode))
        {
            throw Invalid("turnMode", "turnMode must be free or roundRobin.");
        }

        DiscussionCreateResult created = _hub.Create(arguments.String("title"), arguments.String("topic"), mode);
        return Build(writer =>
        {
            DiscussionSessionHeader session = created.Session;
            writer.WriteStartObject();
            writer.WriteString("sessionId", session.Id);
            writer.WriteString("title", session.Title);
            WriteNullableString(writer, "topic", session.Topic);
            writer.WriteString("turnMode", DiscussionNames.ToWire(session.TurnMode));
            writer.WriteString("state", DiscussionNames.ToWire(session.State));
            writer.WriteString("createdUtc", DiscussionNames.FormatUtc(created.CreatedUtc));
            writer.WriteString("hubEpoch", session.HubEpoch);
            writer.WritePropertyName("limits");
            WriteSessionLimits(writer);
            writer.WriteEndObject();
        });
    }

    private string Join(Arguments arguments)
    {
        DiscussionJoinResult joined = _hub.Join(arguments.String("sessionId"), arguments.String("displayName"));
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("participantToken", joined.ParticipantToken);
            writer.WriteString("handle", joined.Handle);
            writer.WriteBoolean("rejoined", joined.Rejoined);
            writer.WritePropertyName("session");
            WriteHeader(writer, joined.Session);
            writer.WriteNumber("lastSequence", joined.LastSequence);
            writer.WriteString("protocol", _protocolText);
            writer.WriteEndObject();
        });
    }

    private string Leave(Arguments arguments)
    {
        DiscussionLeaveResult left = _hub.Leave(arguments.String("sessionId"), arguments.String("participantToken"));
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("alreadyLeft", left.AlreadyLeft);
            writer.WritePropertyName("session");
            WriteHeader(writer, left.Session);
            writer.WriteEndObject();
        });
    }

    private string Post(Arguments arguments)
    {
        var request = new DiscussionPostRequest(
            arguments.String("content"),
            arguments.Boolean("pass") ?? false,
            arguments.Integer("replyToSequence"),
            arguments.IntegerArray("contextIds"),
            arguments.Integer("basedOnSequence"),
            arguments.String("clientRequestId"));
        DiscussionPostResult posted = _hub.Post(arguments.String("sessionId"), arguments.String("participantToken"), request);
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", posted.Sequence);
            writer.WriteString("createdUtc", DiscussionNames.FormatUtc(posted.CreatedUtc));
            writer.WriteNumber("newerMessages", posted.NewerMessages);
            writer.WriteBoolean("duplicate", posted.Duplicate);
            WriteFloor(writer, "floor", posted.Floor);
            writer.WriteEndObject();
        });
    }

    private async Task<string> ReadAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        DiscussionReadResult read = await _hub.ReadAsync(
            arguments.String("sessionId"),
            arguments.String("participantToken"),
            arguments.Integer("afterSequence") ?? 0,
            arguments.Integer("limit") is { } limit ? CheckedInt("limit", limit) : null,
            arguments.IntegerOr("waitSeconds", 0),
            cancellationToken).ConfigureAwait(false);
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("messages");
            foreach (DiscussionReadMessage message in read.Messages)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", message.Sequence);
                WriteNullableString(writer, "handle", message.Handle);
                WriteNullableString(writer, "displayName", message.DisplayName);
                writer.WriteString("kind", DiscussionNames.ToWire(message.Kind));
                writer.WriteString("origin", DiscussionNames.OriginOf(message.Kind));
                writer.WriteString("content", message.Content);
                if (message.ReplyToSequence is { } replyTo)
                {
                    writer.WriteNumber("replyToSequence", replyTo);
                }
                else
                {
                    writer.WriteNull("replyToSequence");
                }

                writer.WriteStartArray("contextIds");
                foreach (long id in message.ContextIds)
                {
                    writer.WriteNumberValue(id);
                }

                writer.WriteEndArray();
                writer.WriteString("createdUtc", DiscussionNames.FormatUtc(message.CreatedUtc));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("nextAfterSequence", read.NextAfterSequence);
            writer.WriteNumber("lastSequence", read.LastSequence);
            writer.WriteBoolean("hasMore", read.HasMore);
            writer.WriteBoolean("timedOut", read.TimedOut);
            writer.WritePropertyName("session");
            WriteHeader(writer, read.Session);
            writer.WriteEndObject();
        });
    }

    private string Close(Arguments arguments)
    {
        DiscussionCloseResult closed = _hub.Close(arguments.String("sessionId"), arguments.String("participantToken"));
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("alreadyClosed", closed.AlreadyClosed);
            writer.WriteString("state", DiscussionNames.ToWire(closed.State));
            WriteNullableString(writer, "closeReason", DiscussionNames.ToWire(closed.CloseReason));
            writer.WriteNumber("messageCount", closed.MessageCount);
            writer.WritePropertyName("session");
            WriteHeader(writer, closed.Session);
            writer.WriteEndObject();
        });
    }

    private string AttachPrecheck(Arguments arguments)
    {
        _hub.AttachPrecheck(arguments.String("sessionId"), arguments.String("participantToken"));
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Stores a snapshot. The arguments are made canonical here (the one place that decides what counts as the same arguments) and the result text must be valid JSON; it is
    /// stored as it came, character for character.
    /// </summary>
    private string AttachCommit(JsonElement root, Arguments arguments)
    {
        string? canonicalArguments = null;
        if (root.TryGetProperty("arguments", out JsonElement rawArguments) && rawArguments.ValueKind != JsonValueKind.Null)
        {
            canonicalArguments = DiscussionCanonicalJson.Canonicalize(rawArguments);
        }
        else
        {
            canonicalArguments = "{}";
        }

        string? result = arguments.String("result");
        if (result is not null)
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(result);
            }
            catch (JsonException)
            {
                throw Invalid("result", "result must be JSON text.");
            }
        }

        DiscussionAttachResult attached = _hub.AttachCommit(
            arguments.String("sessionId"), arguments.String("participantToken"), arguments.String("tool"), canonicalArguments, result, arguments.String("note"));
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("contextId", attached.ContextId);
            writer.WriteBoolean("reused", attached.Reused);
            writer.WriteString("sha256", attached.Sha256);
            writer.WriteNumber("resultBytes", attached.ResultBytes);
            writer.WriteNumber("sequence", attached.Sequence);
            writer.WriteEndObject();
        });
    }

    private string GetAnalysis(Arguments arguments)
    {
        long contextId = arguments.Integer("contextId") ?? throw Invalid("contextId", "contextId is required.");
        bool includeResult = arguments.Boolean("includeResult") ?? true;
        DiscussionAnalysisResult analysis = _hub.GetAnalysis(arguments.String("sessionId"), arguments.String("participantToken"), contextId);
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("contextId", analysis.ContextId);
            writer.WriteString("tool", analysis.Tool);
            writer.WritePropertyName("arguments");
            writer.WriteRawValue(analysis.ArgumentsJson);
            writer.WriteString("sha256", analysis.Sha256);
            writer.WriteString("attachedBy", analysis.AttachedByHandle);
            WriteNullableString(writer, "note", analysis.Note);
            writer.WriteString("createdUtc", DiscussionNames.FormatUtc(analysis.CreatedUtc));
            writer.WriteNumber("resultBytes", analysis.ResultBytes);
            if (includeResult)
            {
                writer.WritePropertyName("result");
                writer.WriteRawValue(analysis.ResultJson, skipInputValidation: true);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// The whole status of a board that is not shared: the hub part (this board) and the process part (this process alone) composed as data
    /// by <see cref="DiscussionStatusComposer"/>. A shared board is composed by the router, which knows the role and the sharing key.
    /// </summary>
    private string Status(string hubPart, Arguments arguments)
    {
        int commandTail = arguments.IntegerOr("commandTail", 0);
        IReadOnlyList<CommandLogEntry> commands = _commandLog is null || commandTail == 0 ? Array.Empty<CommandLogEntry>() : _commandLog.Tail(commandTail);
        return DiscussionStatusComposer.Compose(hubPart, DiscussionProcessInfo.LocalOnly, commands);
    }

    /// <summary>The part of the status that belongs to the board: its epoch, the discussions, the limits it runs with, its audit and its clock. Also validates both tails.</summary>
    private string StatusHubPart(Arguments arguments)
    {
        string? sessionId = arguments.String("sessionId");
        int auditTail = arguments.IntegerOr("auditTail", 0);
        int commandTail = arguments.IntegerOr("commandTail", 0);
        if (commandTail < 0 || commandTail > DiscussionLimits.StatusTailMax)
        {
            throw new DiscussionException(DiscussionErrorCodes.LimitExceeded, $"commandTail must be 0 to {DiscussionLimits.StatusTailMax}.", "commandTail");
        }

        DiscussionStatusResult status = _hub.Status(sessionId, auditTail);
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("hubEpoch", status.HubEpoch);
            if (sessionId is null)
            {
                writer.WriteStartArray("sessions");
                foreach (DiscussionSessionHeader header in status.Sessions)
                {
                    WriteHeader(writer, header);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WritePropertyName("session");
                WriteHeader(writer, status.Sessions[0]);
            }

            writer.WritePropertyName("effectiveLimits");
            WriteEffectiveLimits(writer);
            writer.WriteStartArray("audit");
            foreach (DiscussionAuditEntry entry in status.Audit)
            {
                WriteAuditEntry(writer, entry);
            }

            writer.WriteEndArray();
            writer.WriteString("serverTimeUtc", DiscussionNames.FormatUtc(status.ServerTimeUtc));
            writer.WriteEndObject();
        });
    }

    private static int CheckedInt(string name, long value)
        => value is < int.MinValue or > int.MaxValue ? throw Invalid(name, $"{name} is out of range.") : (int)value;

    // ---- JSON writers -------------------------------------------------------------------------------------------------------------------

    private static string Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteFloor(Utf8JsonWriter writer, string name, DiscussionFloorInfo? floor)
    {
        if (floor is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("handle", floor.Handle);
        writer.WriteNumber("secondsLeft", floor.SecondsLeft);
        writer.WriteEndObject();
    }

    /// <summary>The session header of spec 5.7. Written as a value: the caller has written the property name (or is inside an array).</summary>
    private static void WriteHeader(Utf8JsonWriter writer, DiscussionSessionHeader header)
    {
        writer.WriteStartObject();
        writer.WriteString("id", header.Id);
        writer.WriteString("title", header.Title);
        WriteNullableString(writer, "topic", header.Topic);
        writer.WriteString("state", DiscussionNames.ToWire(header.State));
        WriteNullableString(writer, "closeReason", DiscussionNames.ToWire(header.CloseReason));
        writer.WriteString("turnMode", DiscussionNames.ToWire(header.TurnMode));
        WriteFloor(writer, "floor", header.Floor);
        writer.WriteNumber("messageCount", header.MessageCount);
        writer.WriteNumber("remainingMessages", header.RemainingMessages);
        writer.WriteStartArray("participants");
        foreach (DiscussionParticipantInfo participant in header.Participants)
        {
            writer.WriteStartObject();
            writer.WriteString("handle", participant.Handle);
            writer.WriteString("displayName", participant.DisplayName);
            writer.WriteString("state", DiscussionNames.ToWire(participant.State));
            writer.WriteNumber("lastReadSequence", participant.LastReadSequence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("contextCount", header.ContextCount);
        writer.WriteString("hubEpoch", header.HubEpoch);
        writer.WriteEndObject();
    }

    /// <summary>The limits a participant needs to plan its use of one discussion (the result of create).</summary>
    private void WriteSessionLimits(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("maxParticipants", _settings.MaxParticipantsPerSession);
        writer.WriteNumber("maxMessages", _settings.MaxMessagesPerSession);
        writer.WriteNumber("maxMessageBytes", _settings.MaxMessageBytes);
        writer.WriteNumber("maxContexts", _settings.MaxContextsPerSession);
        writer.WriteNumber("maxContextBytes", _settings.MaxContextBytes);
        writer.WriteNumber("maxWaitSeconds", _settings.MaxWaitSeconds);
        writer.WriteNumber("maxPostsPerMinute", _settings.MaxPostsPerMinute);
        writer.WriteNumber("idleTimeoutSeconds", _settings.IdleTimeoutSeconds);
        writer.WriteNumber("maxSessionAgeSeconds", _settings.MaxSessionAgeSeconds);
        writer.WriteEndObject();
    }

    /// <summary>Every tunable value the board runs with (the status).</summary>
    private void WriteEffectiveLimits(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("maxActiveSessions", _settings.MaxActiveSessions);
        writer.WriteNumber("maxParticipantsPerSession", _settings.MaxParticipantsPerSession);
        writer.WriteNumber("maxMessagesPerSession", _settings.MaxMessagesPerSession);
        writer.WriteNumber("maxMessageBytes", _settings.MaxMessageBytes);
        writer.WriteNumber("maxContextsPerSession", _settings.MaxContextsPerSession);
        writer.WriteNumber("maxContextBytes", _settings.MaxContextBytes);
        writer.WriteNumber("maxTotalBytes", _settings.MaxTotalBytes);
        writer.WriteNumber("idleTimeoutSeconds", _settings.IdleTimeoutSeconds);
        writer.WriteNumber("maxSessionAgeSeconds", _settings.MaxSessionAgeSeconds);
        writer.WriteNumber("closedRetentionSeconds", _settings.ClosedRetentionSeconds);
        writer.WriteNumber("awaySeconds", _settings.AwaySeconds);
        writer.WriteNumber("floorTimeoutSeconds", _settings.FloorTimeoutSeconds);
        writer.WriteNumber("maxWaitSeconds", _settings.MaxWaitSeconds);
        writer.WriteNumber("maxPostsPerMinute", _settings.MaxPostsPerMinute);
        writer.WriteNumber("defaultReadLimit", _settings.DefaultReadLimit);
        writer.WriteNumber("maxReadLimit", _settings.MaxReadLimit);
        writer.WriteNumber("auditCapacity", _settings.AuditCapacity);
        writer.WriteNumber("commandLogCapacity", _settings.CommandLogCapacity);
        writer.WriteBoolean("commandLogToFile", _settings.CommandLogToFile);
        writer.WriteEndObject();
    }

    private static void WriteAuditEntry(Utf8JsonWriter writer, DiscussionAuditEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteNumber("n", entry.N);
        writer.WriteString("utc", DiscussionNames.FormatUtc(entry.Utc));
        writer.WriteString("event", entry.Event);
        if (entry.SessionId is not null)
        {
            writer.WriteString("sessionId", entry.SessionId);
        }

        if (entry.Handle is not null)
        {
            writer.WriteString("handle", entry.Handle);
        }

        if (entry.Sequence is { } sequence)
        {
            writer.WriteNumber("sequence", sequence);
        }

        if (entry.Code is not null)
        {
            writer.WriteString("code", entry.Code);
        }

        if (entry.Detail is not null)
        {
            writer.WriteString("detail", entry.Detail);
        }

        writer.WriteEndObject();
    }

}
