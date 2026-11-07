using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Stage 1 of the two-stage workspace restore: turns a workspace file into settings plus a warning list
/// without touching any view model and without writing anything.
/// </summary>
public interface IWorkspaceTolerantReader
{
    /// <summary>
    /// Reads <paramref name="path"/>. File and data problems never throw; they are reported as a <see cref="WorkspaceReadStatus"/>.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="path"/> is null, empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> is cancelled.</exception>
    Task<WorkspaceLoadResult> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IWorkspaceTolerantReader"/>. Decision order (first match wins):
/// Missing, Empty, Unreadable (I/O, syntax, root not an object, SchemaVersion &lt; 1), NewerSchema (SchemaVersion &gt; current),
/// Ok (full deserialisation), Recovered (per-section / per-element rescue).
/// Uses <see cref="WorkspaceSerializationService.SharedJsonOptions"/> so a file that the app wrote reads back identically.
/// </summary>
public sealed class WorkspaceTolerantReader : IWorkspaceTolerantReader
{
    /// <summary>Lowest SchemaVersion this reader treats as a workspace file (same bound as the portable import).</summary>
    private const int MinimumSchemaVersion = 1;

    private const string SchemaVersionKey = nameof(WorkspaceSettings.SchemaVersion);
    private const string LegacyVersionKey = nameof(WorkspaceSettings.Version);

    /// <summary>
    /// Element types whose damaged items are rescued property by property instead of being dropped whole.
    /// The value is the required member: the parent is kept only when that member is a non-empty JSON string.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> NestedRescueRequiredMember = new Dictionary<Type, string>
    {
        [typeof(PanelChartSettings)] = nameof(PanelChartSettings.TabId),
        [typeof(DetachedTabInfo)] = nameof(DetachedTabInfo.TabId)
    };

    /// <summary>Public settable sections of <see cref="WorkspaceSettings"/> in declaration order. The version keys are handled separately.</summary>
    private static readonly PropertyInfo[] TopLevelSections = GetRescuableProperties(typeof(WorkspaceSettings))
        .Where(p => p.Name != SchemaVersionKey && p.Name != LegacyVersionKey)
        .ToArray();

    private readonly ILogger<WorkspaceTolerantReader> _logger;

    public WorkspaceTolerantReader(ILogger<WorkspaceTolerantReader>? logger = null)
    {
        _logger = logger ?? NullLogger<WorkspaceTolerantReader>.Instance;
    }

    public async Task<WorkspaceLoadResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return Result(WorkspaceReadStatus.Missing);
            }

            using var stream = AtomicJsonFile.OpenForRead(path);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The file vanished between the existence check and the open: same meaning as "absent".
            return Result(WorkspaceReadStatus.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Workspace file could not be read: {Path}", path);
            return Unreadable($"The file could not be read: {ExceptionSanitizer.Sanitize(ex.Message)}");
        }

        var result = Interpret(bytes);
        LogOutcome(path, result);
        return result;
    }

    private WorkspaceLoadResult Interpret(byte[] fileBytes)
    {
        ReadOnlyMemory<byte> content = fileBytes;
        if (content.Span.StartsWith(Encoding.UTF8.Preamble))
        {
            content = content[Encoding.UTF8.Preamble.Length..];
        }

        if (IsJsonWhitespaceOnly(content.Span))
        {
            return Result(WorkspaceReadStatus.Empty);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return Unreadable($"The file is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable($"The JSON root must be an object but is {root.ValueKind}.");
            }

            var members = IndexMembers(root);
            var schema = ReadSchemaVersion(members);

            if (schema.Value is int version)
            {
                if (version < MinimumSchemaVersion)
                {
                    return Unreadable($"SchemaVersion {version} is below the minimum supported version {MinimumSchemaVersion}.");
                }

                if (version > WorkspaceSettings.CurrentSchemaVersion)
                {
                    return new WorkspaceLoadResult(WorkspaceReadStatus.NewerSchema, null, new[]
                    {
                        Lossy(SchemaVersionKey,
                            $"The file has SchemaVersion {version} but this version supports up to {WorkspaceSettings.CurrentSchemaVersion}; it was not loaded so that saving cannot downgrade it.")
                    });
                }
            }

            if (TryDeserializeWhole(content.Span, out var whole, out var wholeFailure))
            {
                return new WorkspaceLoadResult(WorkspaceReadStatus.Ok, whole, Array.Empty<WorkspaceLoadWarning>());
            }

            return Rescue(members, schema, wholeFailure!);
        }
    }

    // ---- rescue ---------------------------------------------------------------------------------------------

    private static WorkspaceLoadResult Rescue(
        IReadOnlyDictionary<string, JsonElement> members,
        SchemaVersionRead schema,
        Exception wholeFailure)
    {
        var warnings = new List<WorkspaceLoadWarning>();
        var settings = new WorkspaceSettings();

        if (schema.IsInvalid)
        {
            warnings.Add(Lossy(SchemaVersionKey, "SchemaVersion is not an integer; the current version was assumed."));
        }
        else if (schema.Value is int version)
        {
            settings.SchemaVersion = version;
        }

        foreach (var section in TopLevelSections)
        {
            if (members.TryGetValue(section.Name, out var element))
            {
                RescueProperty(settings, section, element, section.Name, warnings);
            }
        }

        if (warnings.Count == 0)
        {
            // Every section loaded on its own although the whole did not: keep the invariant "Recovered => Lossy warning".
            warnings.Add(Lossy(string.Empty, $"The workspace could not be read as a whole ({Describe(wholeFailure)}) and was rebuilt section by section."));
        }

        return new WorkspaceLoadResult(WorkspaceReadStatus.Recovered, settings, warnings);
    }

    private static void RescueProperty(object target, PropertyInfo property, JsonElement element, string path, List<WorkspaceLoadWarning> warnings)
    {
        if (!IsList(property.PropertyType, out var elementType) || element.ValueKind == JsonValueKind.Null)
        {
            if (TryDeserialize(element, property.PropertyType, out var value, out var failure))
            {
                property.SetValue(target, value);
            }
            else
            {
                warnings.Add(Lossy(path, $"The section was dropped and its default kept: {Describe(failure!)}"));
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(Lossy(path, $"The section was dropped and its default kept: expected an array but found {element.ValueKind}."));
            return;
        }

        var list = (IList)Activator.CreateInstance(property.PropertyType)!;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            if (TryDeserialize(item, elementType, out var value, out var failure))
            {
                list.Add(value);
            }
            else if (NestedRescueRequiredMember.TryGetValue(elementType, out var requiredMember))
            {
                var rescued = RescueNested(item, elementType, requiredMember, itemPath, failure!, warnings);
                if (rescued is not null)
                {
                    list.Add(rescued);
                }
            }
            else
            {
                warnings.Add(Lossy(itemPath, $"The element was dropped: {Describe(failure!)}"));
            }

            index++;
        }

        property.SetValue(target, list);
    }

    /// <summary>Keeps a damaged parent element when its required member is readable and drops only the damaged children.</summary>
    private static object? RescueNested(
        JsonElement item,
        Type type,
        string requiredMember,
        string path,
        Exception wholeFailure,
        List<WorkspaceLoadWarning> warnings)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            warnings.Add(Lossy(path, $"The element was dropped: expected an object but found {item.ValueKind}."));
            return null;
        }

        var members = IndexMembers(item);
        if (!members.TryGetValue(requiredMember, out var key)
            || key.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(key.GetString()))
        {
            warnings.Add(Lossy(path, $"The element was dropped because its required member '{requiredMember}' is missing or not a non-empty string ({Describe(wholeFailure)})."));
            return null;
        }

        var instance = Activator.CreateInstance(type)!;
        foreach (var property in GetRescuableProperties(type))
        {
            if (members.TryGetValue(property.Name, out var memberElement))
            {
                RescueProperty(instance, property, memberElement, $"{path}.{property.Name}", warnings);
            }
        }

        return instance;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static bool TryDeserializeWhole(ReadOnlySpan<byte> utf8Json, out WorkspaceSettings? settings, out Exception? failure)
    {
        try
        {
            settings = JsonSerializer.Deserialize<WorkspaceSettings>(utf8Json, WorkspaceSerializationService.SharedJsonOptions);
            failure = null;
            return settings is not null;
        }
        catch (Exception ex) when (IsDataFailure(ex))
        {
            settings = null;
            failure = ex;
            return false;
        }
    }

    private static bool TryDeserialize(JsonElement element, Type type, out object? value, out Exception? failure)
    {
        try
        {
            value = JsonSerializer.Deserialize(element, type, WorkspaceSerializationService.SharedJsonOptions);
            failure = null;
            return true;
        }
        catch (Exception ex) when (IsDataFailure(ex))
        {
            value = null;
            failure = ex;
            return false;
        }
    }

    /// <summary>Exceptions the serializer raises for content it cannot map; anything else is a bug and must propagate.</summary>
    private static bool IsDataFailure(Exception ex) =>
        ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException;

    private static SchemaVersionRead ReadSchemaVersion(IReadOnlyDictionary<string, JsonElement> members)
    {
        if (!members.TryGetValue(SchemaVersionKey, out var element) && !members.TryGetValue(LegacyVersionKey, out element))
        {
            return new SchemaVersionRead(null, false);
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var version)
            ? new SchemaVersionRead(version, false)
            : new SchemaVersionRead(null, true);
    }

    /// <summary>Member lookup with the serializer's case-insensitive matching; the last duplicate wins like the serializer.</summary>
    private static Dictionary<string, JsonElement> IndexMembers(JsonElement obj)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in obj.EnumerateObject())
        {
            members[member.Name] = member.Value;
        }

        return members;
    }

    private static IEnumerable<PropertyInfo> GetRescuableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    private static bool IsList(Type type, out Type elementType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static bool IsJsonWhitespaceOnly(ReadOnlySpan<byte> utf8)
    {
        foreach (var b in utf8)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(Exception ex) => ex is JsonException { Path: { Length: > 0 } jsonPath }
        ? $"{ex.GetType().Name} at {jsonPath}: {ex.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    private static WorkspaceLoadResult Result(WorkspaceReadStatus status) =>
        new(status, null, Array.Empty<WorkspaceLoadWarning>());

    private static WorkspaceLoadResult Unreadable(string message) =>
        new(WorkspaceReadStatus.Unreadable, null, new[] { Lossy(string.Empty, message) });

    private static WorkspaceLoadWarning Lossy(string path, string message) =>
        new(WorkspaceWarningSeverity.Lossy, path, message);

    private void LogOutcome(string path, WorkspaceLoadResult result)
    {
        switch (result.Status)
        {
            case WorkspaceReadStatus.Ok:
                _logger.LogInformation("Successfully loaded workspace from {Path}", path);
                break;
            case WorkspaceReadStatus.Empty:
                _logger.LogDebug("Workspace file is empty: {Path}", path);
                break;
            default:
                _logger.LogWarning("Workspace file {Path} read as {Status} with {WarningCount} warning(s)", path, result.Status, result.Warnings.Count);
                break;
        }
    }

    private readonly record struct SchemaVersionRead(int? Value, bool IsInvalid);
}
