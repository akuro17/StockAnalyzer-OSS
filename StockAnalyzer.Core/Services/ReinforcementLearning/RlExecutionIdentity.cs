#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Checks the captured executable tree before reviewed continuation can launch a worker.</summary>
internal static class RlExecutionIdentity
{
    internal static void Verify(string manifestPath, CancellationToken ct = default)
    {
        var manifest = RlCheckpointStore.Read<JsonElement>(manifestPath);
        if (!manifest.TryGetProperty("execution_roots", out var rootsJson) || rootsJson.ValueKind != JsonValueKind.Array
            || !manifest.TryGetProperty("execution_files", out var filesJson) || filesJson.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("execution_links", out var linksJson) || linksJson.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unavailable: executable dependency payload evidence is missing.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = rootsJson.EnumerateArray().Select(value => OwnedAbsolute(value.GetString())).Distinct(comparer).ToArray();
        if (roots.Length == 0) throw new InvalidDataException("Executable dependency roots are empty.");
        bool IsOwned(string path) => roots.Any(root => comparer.Equals(root, path)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison));
        var files = new Dictionary<string, string>(comparer);
        foreach (var entry in filesJson.EnumerateObject())
        {
            string path = OwnedAbsolute(entry.Name);
            if (!IsOwned(path) || !ContentHash.IsLowerHex64(entry.Value.GetString()) || !files.TryAdd(path, entry.Value.GetString()!))
                throw new InvalidDataException("Executable dependency file identity is invalid.");
        }
        var links = new Dictionary<string, string>(comparer);
        foreach (var entry in linksJson.EnumerateObject())
        {
            string path = OwnedAbsolute(entry.Name), target = OwnedAbsolute(entry.Value.GetString());
            if (!IsOwned(path) || !IsOwned(target) || !links.TryAdd(path, target))
                throw new InvalidDataException("Executable dependency link identity is invalid.");
        }
        if (files.Count == 0) throw new InvalidDataException("Executable dependency payload is empty.");
        var observed = new HashSet<string>(comparer);
        var observedLinks = new HashSet<string>(comparer);
        foreach (string root in roots)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out string? directory))
            {
                ct.ThrowIfCancellationRequested();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        FileSystemInfo info = isDirectory ? new DirectoryInfo(entry) : new FileInfo(entry);
                        string target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                            ?? throw new InvalidDataException("Unresolved executable dependency link.");
                        if (!links.TryGetValue(entry, out string? expectedTarget) || !comparer.Equals(target, expectedTarget))
                            throw new InvalidDataException("Unavailable: executable dependency link changed.");
                        observedLinks.Add(entry);
                    }
                    if (isDirectory)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                    }
                    else if (observed.Add(entry))
                    {
                        if (!files.TryGetValue(entry, out string? hash) || ContentHash.OfFile(entry) != hash)
                            throw new InvalidDataException("Unavailable: executable dependency payload changed.");
                    }
                }
            }
        }
        if (observed.Count != files.Count || observedLinks.Count != links.Count)
            throw new InvalidDataException("Unavailable: executable dependency tree coverage changed.");
    }
    private static string OwnedAbsolute(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Absolute executable dependency path required.");
        return Path.GetFullPath(path);
    }
}
