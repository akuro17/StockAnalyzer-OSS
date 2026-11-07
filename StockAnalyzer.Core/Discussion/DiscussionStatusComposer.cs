using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockAnalyzer.Core.Discussion;

/// <summary>What the status says about the asking process itself, as opposed to the board it talks to.</summary>
/// <param name="Role"><c>hub</c> (this process holds the board) or <c>remote</c> (this process talks to the board of another process).</param>
/// <param name="Sharing">True when other servers of this user and data folder can reach the board.</param>
/// <param name="SharingKey">The <c>dataHash8</c> of the pipe name when sharing, so that the owner can compare two clients; otherwise null.</param>
public sealed record DiscussionProcessInfo(string Role, bool Sharing, string? SharingKey)
{
    public const string RoleHub = "hub";
    public const string RoleRemote = "remote";

    /// <summary>A board that only this process can reach.</summary>
    public static DiscussionProcessInfo LocalOnly { get; } = new(RoleHub, false, null);
}

/// <summary>
/// Builds the whole status from its two parts as data: the hub part (the board's own answer, read as a JSON tree) and the process part (role, sharing, command log
/// of the asking process). The hub's text is never edited; its values are copied into the new object in the order of spec 5.14.
/// </summary>
internal static class DiscussionStatusComposer
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Compose(string hubPartJson, DiscussionProcessInfo process, IReadOnlyList<CommandLogEntry> commands)
    {
        ArgumentNullException.ThrowIfNull(hubPartJson);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(commands);
        using JsonDocument hub = JsonDocument.Parse(hubPartJson);
        JsonElement root = hub.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            CopyRequired(writer, root, "hubEpoch");
            writer.WriteString("role", process.Role);
            writer.WriteBoolean("sharing", process.Sharing);
            if (process.SharingKey is null)
            {
                writer.WriteNull("sharingKey");
            }
            else
            {
                writer.WriteString("sharingKey", process.SharingKey);
            }

            Copy(writer, root, "sessions");
            Copy(writer, root, "session");
            CopyRequired(writer, root, "effectiveLimits");
            CopyRequired(writer, root, "audit");
            writer.WriteStartArray("commands");
            foreach (CommandLogEntry entry in commands)
            {
                WriteCommandEntry(writer, entry);
            }

            writer.WriteEndArray();
            CopyRequired(writer, root, "serverTimeUtc");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The command-log tail of this process as the status shows it (the text of the entries only; values of arguments are never recorded).</summary>
    private static void WriteCommandEntry(Utf8JsonWriter writer, CommandLogEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteNumber("n", entry.N);
        writer.WriteString("utc", DiscussionNames.FormatUtc(entry.Utc));
        writer.WriteString("tool", entry.Tool);
        writer.WriteStartArray("argKeys");
        foreach (string key in entry.ArgKeys)
        {
            writer.WriteStringValue(key);
        }

        writer.WriteEndArray();
        writer.WriteNumber("argBytes", entry.ArgBytes);
        writer.WriteString("outcome", entry.Outcome);
        writer.WriteNumber("ms", entry.Milliseconds);
        writer.WriteString("requestId", entry.RequestId);
        writer.WriteEndObject();
    }

    private static void Copy(Utf8JsonWriter writer, JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    private static void CopyRequired(Utf8JsonWriter writer, JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out _))
        {
            throw new InvalidOperationException($"The hub part of the status has no {name}.");
        }

        Copy(writer, root, name);
    }
}
