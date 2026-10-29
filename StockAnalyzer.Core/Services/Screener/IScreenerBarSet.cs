using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// The bars of one symbol in every time frame a condition reads, for a Screener evaluation in which each side has its own time frame
/// (<see cref="ScreenerIndicatorSideConfig.TimeFrame"/>). Built once per symbol per scan, read only afterwards.
/// </summary>
public interface IScreenerBarSet
{
    /// <summary>The bars of <paramref name="frame"/>, oldest first. Never null: empty when the frame was not loaded or has no bar.</summary>
    IReadOnlyList<CandleData> Get(TimeFrame frame);

    /// <summary>The same bars as <see cref="CoreCandleData"/>, converted once when the set was built. Never null.</summary>
    IReadOnlyList<CoreCandleData> GetCore(TimeFrame frame);
}

/// <summary>
/// An <see cref="IScreenerBarSet"/> over lists that are already built. The <see cref="CoreCandleData"/> form of every frame is made in the constructor
/// (no lazy state, so a set needs no lock).
/// </summary>
public sealed class ScreenerBarSet : IScreenerBarSet
{
    private static readonly IReadOnlyList<CandleData> NoBars = Array.Empty<CandleData>();
    private static readonly IReadOnlyList<CoreCandleData> NoCoreBars = Array.Empty<CoreCandleData>();

    private readonly Dictionary<TimeFrame, IReadOnlyList<CandleData>> _bars;
    private readonly Dictionary<TimeFrame, IReadOnlyList<CoreCandleData>> _coreBars;

    public ScreenerBarSet(IReadOnlyDictionary<TimeFrame, IReadOnlyList<CandleData>> byFrame)
    {
        ArgumentNullException.ThrowIfNull(byFrame);

        _bars = new Dictionary<TimeFrame, IReadOnlyList<CandleData>>(byFrame.Count);
        _coreBars = new Dictionary<TimeFrame, IReadOnlyList<CoreCandleData>>(byFrame.Count);
        foreach (KeyValuePair<TimeFrame, IReadOnlyList<CandleData>> pair in byFrame)
        {
            IReadOnlyList<CandleData> bars = pair.Value ?? NoBars;
            var core = new List<CoreCandleData>(bars.Count);
            for (int i = 0; i < bars.Count; i++)
            {
                CandleData c = bars[i];
                core.Add(new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
            }

            _bars[pair.Key] = bars;
            _coreBars[pair.Key] = core;
        }
    }

    public IReadOnlyList<CandleData> Get(TimeFrame frame) => _bars.TryGetValue(frame, out IReadOnlyList<CandleData>? bars) ? bars : NoBars;

    public IReadOnlyList<CoreCandleData> GetCore(TimeFrame frame) => _coreBars.TryGetValue(frame, out IReadOnlyList<CoreCandleData>? bars) ? bars : NoCoreBars;
}
