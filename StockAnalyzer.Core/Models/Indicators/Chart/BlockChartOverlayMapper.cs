using System.Buffers;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>The four per-time-bar output lists of a block chart overlay; every list has exactly one entry per input candle.</summary>
public sealed class BlockChartOverlaySeries
{
    public BlockChartOverlaySeries(int count, bool hasUpdate)
    {
        Close = new List<decimal?>(count);
        Reversal = new List<decimal?>(count);
        Update = hasUpdate ? new List<decimal?>(count) : null;
        IsUpTrend = new List<decimal?>(count);
    }

    public List<decimal?> Close { get; }
    public List<decimal?> Reversal { get; }

    /// <summary>Null for the charts that have no Update series (Kagi, Three Line Break): the series is not produced at all.</summary>
    public List<decimal?>? Update { get; }

    /// <summary>1 for an up block, 0 for a down block, null where Close is null. A helper for colouring; it is not a price.</summary>
    public List<decimal?> IsUpTrend { get; }
}

/// <summary>
/// The one driver that turns a block chart engine into per-time-bar series (Renko, Point and Figure, Kagi, Three Line Break all use it). Contract:
/// <list type="number">
/// <item>Output length equals the candle count, always.</item>
/// <item>An INVALID bar (<see cref="IsValidBar"/> false) is ignored at the door, everywhere: it is not fed to the engine, not part of the size resolution (ATR included) and not counted
/// towards the anchor. Its own output is null in EVERY series; the engine, the anchor and the size are not touched, so the next valid bar continues from the state before it.</item>
/// <item>The size is resolved ONCE at the anchor: the first valid-bar index K &gt;= <see cref="BlockChartSizingSpec.FirstAnchorCandidate"/> for which the strict resolver
/// succeeds (positive after rounding). Then the engine is created with that size and fed the valid bars 0..K (the replay) and every later valid bar one by one. Bars before K output null.
/// If no K succeeds the result is null everywhere (a single forward scan; nothing waits or loops).</item>
/// <item>The output of a valid bar at or after K is the engine state after that bar. A bar that adds no block repeats the previous state; before the first block everything is null.</item>
/// </list>
/// Consequently the output of bar i depends only on bars 0..i (the anchor index and size are fixed by bars up to K &lt;= i), so appending bars never changes earlier output.
/// For a sizing-free chart (Three Line Break) pass null: the anchor is the first valid bar and the engine factory receives 0.
/// </summary>
public static class BlockChartOverlayMapper
{
    /// <summary>A bar the engines can process: every price positive and High not below Low. (decimal has no NaN; this is what "invalid value" means for these charts.)</summary>
    public static bool IsValidBar(CoreCandleData bar)
        => bar.Close > 0m && bar.High > 0m && bar.Low > 0m && bar.High >= bar.Low;

    public static BlockChartOverlaySeries Map(
        IReadOnlyList<CoreCandleData> candles,
        BlockChartSizingSpec? sizing,
        Func<decimal, IBlockChartBarEngine> createEngine,
        bool hasUpdate)
    {
        int count = candles.Count;
        var result = new BlockChartOverlaySeries(count, hasUpdate);
        if (count == 0) return result;

        var valid = ArrayPool<CoreCandleData>.Shared.Rent(count);
        try
        {
            int validCount = 0;
            for (int i = 0; i < count; i++)
            {
                var bar = candles[i];
                if (IsValidBar(bar)) valid[validCount++] = bar;
            }

            if (!TryFindAnchor(valid, validCount, sizing, out int anchor, out decimal size))
            {
                for (int i = 0; i < count; i++) AddNull(result);
                return result;
            }

            var engine = createEngine(size);
            for (int k = 0; k < anchor; k++) engine.Feed(valid[k]); // replay: output of these bars stays null

            int ordinal = 0;
            for (int i = 0; i < count; i++)
            {
                if (!IsValidBar(candles[i]))
                {
                    AddNull(result);
                    continue;
                }

                if (ordinal < anchor)
                {
                    ordinal++;
                    AddNull(result);
                    continue;
                }

                engine.Feed(valid[ordinal]);
                ordinal++;
                AddState(result, engine.State);
            }

            return result;
        }
        finally
        {
            ArrayPool<CoreCandleData>.Shared.Return(valid, clearArray: true);
        }
    }

    private static bool TryFindAnchor(CoreCandleData[] valid, int validCount, BlockChartSizingSpec? sizing, out int anchor, out decimal size)
    {
        anchor = 0;
        size = 0m;
        if (validCount == 0) return false;
        if (sizing is null) return true;

        var spec = sizing.Value;
        for (int k = spec.FirstAnchorCandidate; k < validCount; k++)
        {
            if (spec.TryResolve(new ReadOnlySpan<CoreCandleData>(valid, 0, k + 1), out size))
            {
                anchor = k;
                return true;
            }
        }

        size = 0m;
        return false;
    }

    private static void AddNull(BlockChartOverlaySeries result)
    {
        result.Close.Add(null);
        result.Reversal.Add(null);
        result.Update?.Add(null);
        result.IsUpTrend.Add(null);
    }

    private static void AddState(BlockChartOverlaySeries result, BlockChartBarState state)
    {
        if (!state.IsDefined)
        {
            AddNull(result);
            return;
        }

        result.Close.Add(state.Close);
        result.Reversal.Add(state.Reversal);
        result.Update?.Add(state.Update);
        result.IsUpTrend.Add(state.TrendFlag);
    }
}
