using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>What the drawing file of a symbol and time frame holds.</summary>
public enum DrawingDocumentReadStatus
{
    /// <summary>There is no drawing file (or the symbol cannot have one).</summary>
    NotFound,

    /// <summary>The file is a legacy V1 file (no "version" property). It is left as it is; the application migrates it when the chart is opened and saved.</summary>
    LegacyFormat,

    /// <summary>The file is a V2 <see cref="DrawingDocumentState"/>.</summary>
    Read,
}

/// <summary>The outcome of reading a drawing file; <see cref="Document"/> is set only for <see cref="DrawingDocumentReadStatus.Read"/>.</summary>
public sealed record DrawingDocumentReadResult(DrawingDocumentReadStatus Status, DrawingDocumentState? Document = null);

/// <summary>Read-only access to the saved chart drawings (Data/Drawings). Nothing is created, migrated, backed up or written.</summary>
public interface IReadOnlyDrawingDocumentSource
{
    /// <summary>Reads the drawing document of the symbol on a daily, weekly or monthly chart.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The time frame is not D1, W1 or MN1 (the application stores drawings per daily, weekly and monthly chart only).</exception>
    /// <exception cref="DataUnavailableException">The file exists but cannot be read now: it is being replaced, damaged, of an unsupported version or of another symbol.</exception>
    Task<DrawingDocumentReadResult> ReadAsync(string symbol, TimeFrame timeFrame, CancellationToken cancellationToken = default);
}
