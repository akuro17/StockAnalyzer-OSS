using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>Why a line of the pipe could not be used. The router turns every kind into HUB_UNAVAILABLE (or LIMIT_EXCEEDED for an oversize request).</summary>
internal enum DiscussionWireProblem
{
    /// <summary>The line is longer than <see cref="DiscussionLimits.WireMaxLineBytes"/>.</summary>
    TooLong,

    /// <summary>The peer closed the pipe in the middle of a line.</summary>
    Truncated,

    /// <summary>The line is not the JSON object of the wire.</summary>
    Malformed,

    /// <summary>The line is JSON of the wire but carries another wire version.</summary>
    VersionMismatch,
}

internal sealed class DiscussionWireException : Exception
{
    public DiscussionWireException(DiscussionWireProblem problem, string message)
        : base(message)
    {
        Problem = problem;
    }

    public DiscussionWireProblem Problem { get; }
}

/// <summary>
/// The line protocol between two servers of one user (spec 5.12): one request line and one reply line per connection, UTF-8, LF, at most
/// <see cref="DiscussionLimits.WireMaxLineBytes"/>. The arguments and the result travel as JSON values, not as escaped text.
/// </summary>
internal static class DiscussionWire
{
    private const byte LineFeed = (byte)'\n';
    private const int ChunkBytes = 8192;
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A request as it is read from the pipe.</summary>
    internal sealed record WireRequest(string Operation, string Caller, string ArgumentsJson);

    // ---- Writing --------------------------------------------------------------------------------------------------------------------------

    /// <exception cref="JsonException">The arguments are not a JSON value.</exception>
    public static byte[] EncodeRequest(DiscussionRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("wire", DiscussionLimits.WireVersion);
            writer.WriteString("op", request.Operation);
            writer.WriteString("caller", request.CallerLabel);
            writer.WritePropertyName("args");
            writer.WriteRawValue(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
            writer.WriteEndObject();
        }

        stream.WriteByte(LineFeed);
        return stream.ToArray();
    }

    public static byte[] EncodeReply(DiscussionReply reply)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("wire", DiscussionLimits.WireVersion);
            writer.WriteBoolean("ok", reply.IsOk);
            if (reply.IsOk)
            {
                writer.WritePropertyName("result");
                writer.WriteRawValue(reply.ResultJson!, skipInputValidation: true);
            }
            else
            {
                DiscussionFailure failure = reply.Failure!;
                writer.WriteStartObject("error");
                writer.WriteString("code", failure.Code);
                writer.WriteString("message", failure.Message);
                if (failure.Parameter is not null)
                {
                    writer.WriteString("param", failure.Parameter);
                }

                if (failure.Hint is not null)
                {
                    writer.WriteString("hint", failure.Hint);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        stream.WriteByte(LineFeed);
        return stream.ToArray();
    }

    // ---- Reading --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads up to the first line feed, never more than the limit. Null when the peer closed the pipe before sending anything.
    /// Bytes after the line feed are left unread (one line per connection).
    /// </summary>
    /// <exception cref="DiscussionWireException">The line is too long or the pipe ended in the middle of it.</exception>
    public static async Task<byte[]?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        byte[] chunk = new byte[ChunkBytes];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length == 0
                    ? null
                    : throw new DiscussionWireException(DiscussionWireProblem.Truncated, "The pipe ended in the middle of a line.");
            }

            int feed = Array.IndexOf(chunk, LineFeed, 0, read);
            int take = feed >= 0 ? feed : read;
            if (line.Length + take > maxBytes)
            {
                throw new DiscussionWireException(DiscussionWireProblem.TooLong, "A line is longer than the wire allows.");
            }

            line.Write(chunk, 0, take);
            if (feed >= 0)
            {
                return line.ToArray();
            }
        }
    }

    /// <exception cref="DiscussionWireException">Not a JSON object of the wire, or another wire version.</exception>
    public static WireRequest ParseRequest(byte[] line)
    {
        using JsonDocument document = ParseObject(line);
        JsonElement root = document.RootElement;
        CheckVersion(root);
        string operation = RequiredString(root, "op");
        string caller = root.TryGetProperty("caller", out JsonElement callerValue) && callerValue.ValueKind == JsonValueKind.String ? callerValue.GetString() ?? string.Empty : string.Empty;
        if (!root.TryGetProperty("args", out JsonElement args) || args.ValueKind != JsonValueKind.Object)
        {
            throw new DiscussionWireException(DiscussionWireProblem.Malformed, "args must be a JSON object.");
        }

        return new WireRequest(operation, caller, args.GetRawText());
    }

    /// <exception cref="DiscussionWireException">Not a JSON object of the wire, or another wire version.</exception>
    public static DiscussionReply ParseReply(byte[] line)
    {
        using JsonDocument document = ParseObject(line);
        JsonElement root = document.RootElement;
        CheckVersion(root);
        if (!root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new DiscussionWireException(DiscussionWireProblem.Malformed, "ok must be true or false.");
        }

        if (ok.GetBoolean())
        {
            if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object)
            {
                throw new DiscussionWireException(DiscussionWireProblem.Malformed, "result must be a JSON object.");
            }

            return DiscussionReply.Ok(result.GetRawText());
        }

        if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
        {
            throw new DiscussionWireException(DiscussionWireProblem.Malformed, "error must be a JSON object.");
        }

        return DiscussionReply.Fail(
            RequiredString(error, "code"),
            RequiredString(error, "message"),
            OptionalString(error, "param"),
            OptionalString(error, "hint"));
    }

    private static JsonDocument ParseObject(byte[] line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw new DiscussionWireException(DiscussionWireProblem.Malformed, "The line is not JSON.");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new DiscussionWireException(DiscussionWireProblem.Malformed, "The line is not a JSON object.");
        }

        return document;
    }

    private static void CheckVersion(JsonElement root)
    {
        if (!root.TryGetProperty("wire", out JsonElement wire) || wire.ValueKind != JsonValueKind.Number || !wire.TryGetInt32(out int version))
        {
            throw new DiscussionWireException(DiscussionWireProblem.Malformed, "wire must be a number.");
        }

        if (version != DiscussionLimits.WireVersion)
        {
            throw new DiscussionWireException(DiscussionWireProblem.VersionMismatch, "The peer speaks another wire version.");
        }
    }

    private static string RequiredString(JsonElement element, string name)
        => OptionalString(element, name) ?? throw new DiscussionWireException(DiscussionWireProblem.Malformed, $"{name} must be a string.");

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
