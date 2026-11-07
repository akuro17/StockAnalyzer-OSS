using System;
using System.IO;
using System.Security;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Decides once per start whether the persistent data folder can be written, by creating, writing, flushing and deleting a one-byte probe file.
/// The probe runs BEFORE the service container is built, because settings managers resolve their folders in their constructors.
/// Only the data root is probed; a sub-folder that is not writable while the root is, is handled by the failing write itself (owner decision Q-C).
/// <para>
/// Time: the probe is a synchronous start-up step with no timeout (owner decision Q-F'; a local disk answers in milliseconds). If a timeout is ever
/// needed (for example a network share that hangs), it MUST be a fixed constant inside the program (for example 3 seconds), not a setting - a hung
/// folder also hangs the reading of a settings file - and on expiry the probe MUST report read-only so the application starts in read-only mode.
/// </para>
/// </summary>
public static class DataStorageProbe
{
    /// <summary>Prefix of the probe file name; the rest is a GUID in the "N" format.</summary>
    public const string ProbeFilePrefix = ".sa_write_probe_";

    /// <summary>The name is tried once more with a new GUID when the first one is taken (a GUID collision is practically impossible; the retry covers a leftover file).</summary>
    private const int MaxNameAttempts = 2;

    /// <summary>Probes <paramref name="dataRoot"/>. Never throws for file-system problems.</summary>
    /// <exception cref="ArgumentException">When <paramref name="dataRoot"/> is null, empty or whitespace.</exception>
    public static DataStorageStatus Evaluate(string dataRoot) =>
        Evaluate(dataRoot, static () => ProbeFilePrefix + Guid.NewGuid().ToString("N"));

    /// <summary>Same as <see cref="Evaluate(string)"/> with the probe file names supplied by <paramref name="nextProbeFileName"/> (tests force a name collision).</summary>
    internal static DataStorageStatus Evaluate(string dataRoot, Func<string> nextProbeFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        try
        {
            if (!Directory.Exists(dataRoot))
            {
                Directory.CreateDirectory(dataRoot);
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return ReadOnly(DataStorageReadOnlyReason.DataRootCannotBeCreated, dataRoot, ex);
        }

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var probePath = Path.Combine(dataRoot, nextProbeFileName());
            var created = false;
            try
            {
                using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }
            }
            catch (IOException ex) when (!created && File.Exists(probePath))
            {
                // The name was already taken by a file that is not ours: try a new name (once).
                lastFailure = ex;
                continue;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                if (created)
                {
                    TryDelete(probePath);
                }

                return ReadOnly(DataStorageReadOnlyReason.DataRootNotWritable, dataRoot, ex);
            }

            return DeleteProbe(probePath, dataRoot);
        }

        return ReadOnly(DataStorageReadOnlyReason.DataRootNotWritable, dataRoot, lastFailure!);
    }

    /// <summary>A probe file that could not be deleted does not make the folder read-only: the write itself worked.</summary>
    private static DataStorageStatus DeleteProbe(string probePath, string dataRoot)
    {
        try
        {
            File.Delete(probePath);
            return new DataStorageStatus(true, DataStorageReadOnlyReason.None, dataRoot, null);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return new DataStorageStatus(true, DataStorageReadOnlyReason.None, dataRoot,
                $"The probe file '{Path.GetFileName(probePath)}' could not be deleted: {ExceptionSanitizer.Sanitize(ex.Message)}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // Nothing more can be done for a file we just created in a folder that now refuses us; the caller reports the original failure.
        }
    }

    private static DataStorageStatus ReadOnly(DataStorageReadOnlyReason reason, string dataRoot, Exception cause) =>
        new(false, reason, dataRoot, $"{cause.GetType().Name}: {ExceptionSanitizer.Sanitize(cause.Message)}");

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException;
}
