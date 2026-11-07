using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Keeps a timestamped COPY of a stored file that is about to be protected from automatic overwrite
/// (<c>&lt;file&gt;.corrupt.&lt;UTC yyyyMMddTHHmmssZ&gt;</c>, naming in <see cref="CorruptedFile"/>). The original is never moved or modified.
/// A copy identical to the newest existing one is not made again, and only the newest <see cref="MaxPreservedCopies"/> copies are kept.
/// Failures are reported by the return value, never thrown: preserving is a best-effort safety net and must not stop a start-up.
/// </summary>
public sealed class CorruptFilePreserver
{
    /// <summary>
    /// How many preserved copies of one file are kept. A fixed constant on purpose (owner decision Q-1'): the count must not
    /// depend on a settings file, because the settings file itself may be the damaged one.
    /// </summary>
    public const int MaxPreservedCopies = 5;

    /// <summary>Suffix of the name a copy has while it is being written; it is never a valid preserved-copy name.</summary>
    public const string TemporarySuffix = ".tmp";

    private readonly ILogger<CorruptFilePreserver> _logger;
    private readonly TimeProvider _timeProvider;

    public CorruptFilePreserver(ILogger<CorruptFilePreserver>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<CorruptFilePreserver>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Makes sure a preserved copy of <paramref name="filePath"/> with its current content exists.
    /// </summary>
    /// <param name="filePath">The file to preserve.</param>
    /// <param name="preservedPath">The new copy, or the existing newest copy when its content is identical; null when the result is false.</param>
    /// <returns>True when a copy with the current content exists; false when the file is absent or copying failed (logged).</returns>
    /// <exception cref="ArgumentException">When <paramref name="filePath"/> is null, empty or whitespace.</exception>
    public bool TryPreserve(string filePath, out string? preservedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        preservedPath = null;

        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var existing = ListCopies(filePath);
            if (existing.Count > 0 && ContentHash.OfFile(existing[^1].Path) == ContentHash.OfFile(filePath))
            {
                preservedPath = existing[^1].Path;
                return true;
            }

            var created = Copy(filePath, existing);
            preservedPath = created;
            ApplyRetention(filePath, created);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not preserve a copy of {FilePath}; the original is untouched.", filePath);
            preservedPath = null;
            return false;
        }
    }

    private string Copy(string filePath, List<(string Path, DateTime Utc, int Ordinal)> existing)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Continue after the highest ordinal already used in this second (not "first free"): retention may have deleted
        // the low ordinals, and reusing one would make the newest copy sort as the oldest.
        var second = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var highestInSecond = existing.Where(c => c.Utc == second).Select(c => c.Ordinal).DefaultIfEmpty(0).Max();
        var ordinal = Math.Max(CorruptedFile.FirstPreservedOrdinal, highestInSecond + 1);
        var target = CorruptedFile.PreservedPathFor(filePath, now, ordinal);
        while (File.Exists(target))
        {
            // A file this listing did not count (e.g. created concurrently): move on to the next free ordinal.
            target = CorruptedFile.PreservedPathFor(filePath, now, ++ordinal);
        }

        // Copy under a temporary name and move it into place, so a copy that is interrupted (disk full, power loss) never appears under the
        // final name and is never taken for a preserved copy later. The temporary name does not parse as a preserved suffix, so it is
        // ignored by the listing and the retention; a leftover is overwritten by the next attempt of the same target.
        var temporary = target + TemporarySuffix;
        try
        {
            File.Copy(filePath, temporary, overwrite: true);
            File.Move(temporary, target, overwrite: false);
        }
        catch
        {
            TryDeleteTemporary(temporary);
            throw;
        }

        _logger.LogWarning("Preserved a copy of {FilePath} as {PreservedPath}.", filePath, target);
        return target;
    }

    private void TryDeleteTemporary(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the unfinished copy {TemporaryPath}.", temporary);
        }
    }

    /// <summary>Deletes the oldest copies beyond the limit. The copy just made is never deleted, even if the clock moved backwards.</summary>
    private void ApplyRetention(string filePath, string justCreated)
    {
        var copies = ListCopies(filePath);
        var excess = copies.Count - MaxPreservedCopies;
        if (excess <= 0)
        {
            return;
        }

        foreach (var old in copies.Where(c => !string.Equals(c.Path, justCreated, StringComparison.OrdinalIgnoreCase)).Take(excess))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete the old preserved copy {PreservedPath}.", old.Path);
            }
        }
    }

    /// <summary>Preserved copies of <paramref name="filePath"/>, oldest first (timestamp, then ordinal). Other files are ignored.</summary>
    private static List<(string Path, DateTime Utc, int Ordinal)> ListCopies(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var fileName = Path.GetFileName(filePath);
        var prefixLength = fileName.Length + CorruptedFile.PreservedMarker.Length;

        var copies = new List<(string Path, DateTime Utc, int Ordinal)>();
        foreach (var candidate in Directory.EnumerateFiles(directory, CorruptedFile.PreservedSearchPattern(fileName)))
        {
            var name = Path.GetFileName(candidate);
            if (name.StartsWith(fileName + CorruptedFile.PreservedMarker, StringComparison.OrdinalIgnoreCase)
                && CorruptedFile.TryParsePreservedSuffix(name[prefixLength..], out var utc, out var ordinal))
            {
                copies.Add((candidate, utc, ordinal));
            }
        }

        copies.Sort((a, b) => a.Utc != b.Utc ? a.Utc.CompareTo(b.Utc) : a.Ordinal.CompareTo(b.Ordinal));
        return copies;
    }
}
