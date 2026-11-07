using System;
using System.IO;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Thrown at the entry of a feature whose main purpose is to write below the data folder when that folder cannot be written
/// (read-only mode). It is an <see cref="IOException"/> so that callers which already handle a failed write also handle this one,
/// but it is raised <em>before</em> any long work starts. The message never contains the folder path.
/// </summary>
public sealed class DataStorageReadOnlyException : IOException
{
    public DataStorageReadOnlyException(string operation, DataStorageReadOnlyReason reason)
        : base($"'{operation}' is unavailable because the data folder is read-only ({reason}).")
    {
        Operation = operation;
        Reason = reason;
    }

    /// <summary>The name the feature passed to <see cref="DataStorageGuard.ThrowIfReadOnly"/>.</summary>
    public string Operation { get; }

    public DataStorageReadOnlyReason Reason { get; }
}

/// <summary>
/// The service-entry half of the read-only contract (the other half is the command's <c>CanExecute</c>). A feature calls
/// <see cref="ThrowIfReadOnly"/> before it starts writing; shared writers such as <c>AtomicJsonFile</c> are deliberately
/// <em>not</em> guarded, so a save that is reached some other way still fails with the ordinary write exception.
/// A missing state (tests, designer, tools that do not probe) means "writable", i.e. the behaviour before read-only mode existed.
/// </summary>
public static class DataStorageGuard
{
    public static bool IsWritable(IDataStorageState? state) => state is null || state.Current.IsWritable;

    public static void ThrowIfReadOnly(IDataStorageState? state, string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("The operation name is required.", nameof(operation));
        }

        if (state is { Current.IsWritable: false } readOnly)
        {
            throw new DataStorageReadOnlyException(operation, readOnly.Current.Reason);
        }
    }
}
