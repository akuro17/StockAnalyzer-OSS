using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Lets one writer at a time work on a file path. <see cref="AtomicJsonFile.SaveAsync{T}"/> writes through the fixed name
/// <c>&lt;file&gt;.tmp</c> opened with <see cref="FileShare.None"/>, so two overlapping saves of the same file make the second one
/// fail with an <see cref="IOException"/> and its (newer) state is lost. A caller that can be invoked repeatedly (settings saved on
/// every change) takes the gate around the save instead.
/// </summary>
/// <remarks>
/// Guarantees: one holder per path at a time; different paths run in parallel. It does NOT guarantee the order in which waiting
/// callers proceed (<see cref="SemaphoreSlim"/> does not document it), so each caller must write the snapshot of its own call.
/// The table holds an entry only while somebody holds or waits for it, so it does not grow with the number of paths used.
/// </remarks>
public static class SerializedPathGate
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private static readonly object TableLock = new();
    private static readonly Dictionary<string, Entry> Table = new(StringComparer.FromComparison(PathDiscovery.PathComparison));

    /// <summary>True while somebody holds or waits for <paramref name="path"/>. For tests.</summary>
    internal static bool IsTracked(string path)
    {
        lock (TableLock) return Table.ContainsKey(Path.GetFullPath(path));
    }

    /// <summary>
    /// Waits until no other caller holds <paramref name="path"/>, then returns a holder whose <see cref="IDisposable.Dispose"/>
    /// (idempotent) releases it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting; nothing stays tracked.</exception>
    public static async ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must not be empty.", nameof(path));

        string key = Path.GetFullPath(path);
        Entry entry;
        lock (TableLock)
        {
            if (!Table.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                Table.Add(key, entry);
            }
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Holder(key, entry);
    }

    private static void Leave(string key, Entry entry)
    {
        lock (TableLock)
        {
            if (--entry.References == 0)
            {
                Table.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Holder : IDisposable
    {
        private readonly string _key;
        private readonly Entry _entry;
        private int _released;

        public Holder(string key, Entry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _entry.Semaphore.Release();
            Leave(_key, _entry);
        }
    }
}
