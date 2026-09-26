using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Container for drawing objects and layer records partitioned by drawing context.
/// </summary>
public class ChartDrawingPayload
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<ChartDrawingContextType, List<IChartObject>> Objects { get; set; } = new();
    public Dictionary<ChartDrawingContextType, List<DrawingLayerRecord>> Layers { get; set; } = new();

    /// <summary>Link groups per context, addressed by object position (object ids are not persisted in this format).</summary>
    public Dictionary<ChartDrawingContextType, List<DrawingLinkGroupIndexRecord>> LinkGroups { get; set; } = new();
}

/// <summary>
/// Persists and restores chart drawing objects (trend lines, Fibonacci tools, etc.) per
/// ticker+timeframe combination, so they survive symbol/timeframe switches. Day/Week/Monthly
/// (and every other TimeframeType) are stored as fully independent sets.
/// </summary>
public interface IChartDrawingRepository
{
    /// <summary>Loads the saved drawing state for a ticker+timeframe, or null if none exists.</summary>
    Dictionary<ChartDrawingContextType, List<IChartObject>>? Load(string ticker, TimeframeType timeframe);

    /// <summary>Saves the given drawing state for a ticker+timeframe (fire-and-forget, non-blocking).</summary>
    void Save(string ticker, TimeframeType timeframe, Dictionary<ChartDrawingContextType, List<IChartObject>> data);

    /// <summary>
    /// Saves the given drawing state for a ticker+timeframe and completes only once the write
    /// has finished. Use this (instead of the fire-and-forget Save) whenever a Load for a
    /// different key may follow immediately, to avoid a save/load race.
    /// </summary>
    Task SaveAsync(string ticker, TimeframeType timeframe, Dictionary<ChartDrawingContextType, List<IChartObject>> data);

    /// <summary>Loads the complete drawing payload (objects and layers) for a ticker+timeframe, or null if none exists.</summary>
    ChartDrawingPayload? LoadPayload(string ticker, TimeframeType timeframe);

    /// <summary>Saves the complete drawing payload for a ticker+timeframe (fire-and-forget, non-blocking).</summary>
    void SavePayload(string ticker, TimeframeType timeframe, ChartDrawingPayload payload);

    /// <summary>
    /// Saves the complete drawing payload for a ticker+timeframe and completes only once the write
    /// has finished.
    /// </summary>
    Task SavePayloadAsync(string ticker, TimeframeType timeframe, ChartDrawingPayload payload);

    /// <summary>
    /// Current revision of the drawings file for a ticker+timeframe. Increases on every accepted save
    /// (0 until the first save in this process), so a tab can tell whether its loaded state is stale.
    /// </summary>
    long GetRevision(string ticker, TimeframeType timeframe);

    /// <summary>
    /// Loads the payload and reports the revision it corresponds to. Waits for pending writes of the same
    /// file first, so a save that was already accepted is never missed.
    /// </summary>
    ChartDrawingPayload? LoadPayload(string ticker, TimeframeType timeframe, out long revision);

    /// <summary>
    /// Saves only when <paramref name="expectedRevision"/> is still the current revision. Otherwise the file is
    /// left untouched, the rejected payload is preserved in a sibling ".conflict" file, and the outcome is not accepted.
    /// </summary>
    DrawingSaveOutcome SavePayloadIfCurrent(string ticker, TimeframeType timeframe, ChartDrawingPayload payload, long expectedRevision);
}

/// <summary>
/// Result of <see cref="IChartDrawingRepository.SavePayloadIfCurrent"/>.
/// </summary>
/// <param name="IsAccepted">True when the payload was queued for writing.</param>
/// <param name="Revision">The new revision when accepted; the current (newer) revision when rejected.</param>
/// <param name="Completion">Completes when the write (or the conflict backup) has finished.</param>
public readonly record struct DrawingSaveOutcome(bool IsAccepted, long Revision, Task Completion);

