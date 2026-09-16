using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Status codes returned by drawing document load operations.
/// </summary>
public enum DrawingLoadStatus
{
    Success,
    NotFound,
    MigrationRequired,
    UnsupportedVersion,
    InvalidData,
    IoFailure,
    Cancelled
}

/// <summary>
/// Status codes returned by drawing document save operations.
/// </summary>
public enum DrawingSaveStatus
{
    Success,
    IoFailure,
    Cancelled
}

/// <summary>
/// Strongly-typed result of a drawing document load operation.
/// </summary>
public readonly record struct DrawingLoadResult(
    DrawingLoadStatus Status,
    DrawingDocumentState? Document,
    string? ErrorMessage = null
)
{
    public bool IsSuccess => Status == DrawingLoadStatus.Success;

    public static DrawingLoadResult Succeeded(DrawingDocumentState document)
        => new(DrawingLoadStatus.Success, document);

    public static DrawingLoadResult NotFoundResult()
        => new(DrawingLoadStatus.NotFound, null);

    public static DrawingLoadResult MigrationRequiredResult(string message)
        => new(DrawingLoadStatus.MigrationRequired, null, message);

    public static DrawingLoadResult UnsupportedVersionResult(string message)
        => new(DrawingLoadStatus.UnsupportedVersion, null, message);

    public static DrawingLoadResult InvalidDataResult(string message)
        => new(DrawingLoadStatus.InvalidData, null, message);

    public static DrawingLoadResult IoFailureResult(string message)
        => new(DrawingLoadStatus.IoFailure, null, message);

    public static DrawingLoadResult CancelledResult()
        => new(DrawingLoadStatus.Cancelled, null, "Operation was cancelled.");
}

/// <summary>
/// Strongly-typed result of a drawing document save operation.
/// </summary>
public readonly record struct DrawingSaveResult(
    DrawingSaveStatus Status,
    long CommittedRevision,
    string? ErrorMessage = null
)
{
    public bool IsSuccess => Status == DrawingSaveStatus.Success;

    public static DrawingSaveResult Succeeded(long revision)
        => new(DrawingSaveStatus.Success, revision);

    public static DrawingSaveResult IoFailureResult(string message)
        => new(DrawingSaveStatus.IoFailure, 0, message);

    public static DrawingSaveResult CancelledResult()
        => new(DrawingSaveStatus.Cancelled, 0, "Save operation was cancelled before atomic commit.");
}

/// <summary>
/// File-backed repository contract for V2 drawing documents.
/// Provides strictly-typed results, atomic persistence with backups, and revision sequencing.
/// </summary>
public interface IDrawingDocumentRepository
{
    /// <summary>
    /// Loads a drawing document asynchronously for the specified ticker and timeframe.
    /// Returns MigrationRequired if the file exists in legacy V1 format.
    /// </summary>
    Task<DrawingLoadResult> LoadDocumentAsync(DrawingDocumentKey key, CancellationToken ct = default);

    /// <summary>
    /// Atomically saves a drawing document with pre-replacement backup and monotonic revision sequencing.
    /// </summary>
    Task<DrawingSaveResult> SaveDocumentAsync(DrawingDocumentState document, CancellationToken ct = default);
}
