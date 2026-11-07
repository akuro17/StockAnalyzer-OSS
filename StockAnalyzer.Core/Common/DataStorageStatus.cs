namespace StockAnalyzer.Core.Common;

/// <summary>Why the persistent data folder (<c>Data/</c>) cannot be written.</summary>
public enum DataStorageReadOnlyReason
{
    /// <summary>The data folder is writable.</summary>
    None,

    /// <summary>The data folder does not exist and could not be created.</summary>
    DataRootCannotBeCreated,

    /// <summary>The data folder exists but a file cannot be created and written in it.</summary>
    DataRootNotWritable
}

/// <summary>
/// The result of probing the data folder once at start-up.
/// </summary>
/// <param name="IsWritable">True when a file could be created, written and flushed in <paramref name="DataRoot"/>.</param>
/// <param name="Reason"><see cref="DataStorageReadOnlyReason.None"/> exactly when <paramref name="IsWritable"/> is true.</param>
/// <param name="DataRoot">The folder that was probed (not sanitised; format it before showing it).</param>
/// <param name="Detail">A short sanitised description of the failure, or of a non-fatal oddity (probe file left behind); null when nothing noteworthy happened.</param>
public sealed record DataStorageStatus(bool IsWritable, DataStorageReadOnlyReason Reason, string DataRoot, string? Detail);

/// <summary>
/// Whether the persistent data folder is writable. The answer is fixed for the life of the process: a permission fix is picked up
/// by restarting the application, never by re-probing.
/// </summary>
public interface IDataStorageState
{
    DataStorageStatus Current { get; }
}

/// <summary>The one instance registered in the container, holding the status probed before the container was built.</summary>
public sealed class DataStorageState : IDataStorageState
{
    public DataStorageState(DataStorageStatus status)
    {
        Current = status ?? throw new System.ArgumentNullException(nameof(status));
    }

    public DataStorageStatus Current { get; }
}
