using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Immutable, validated backtest run input. Validation runs in the exact order specified by
/// Y:\0915 Backtesting\01_P1_SimulationEngine.md section 5.2 (steps 1,2,4,6 below — step 3's
/// config-value bounds live on BacktestConfiguration itself, step 5 is a "do not silently repair"
/// design rule with nothing to execute). A constructor (not independent init-properties) is used
/// deliberately so validation order cannot depend on the caller's property-initializer order.
/// </summary>
public sealed class BacktestInput
{
    /// <summary>
    /// DERIVED CONVENTION — see UnsupportedDataVersionException's remarks. The only DataVersion
    /// this engine build currently accepts.
    /// </summary>
    public const int CurrentDataVersion = 1;

    public ImmutableArray<CandleData> Bars { get; }
    public string Symbol { get; }
    public TimeFrame Frame { get; }
    public int DataVersion { get; }
    public DateTime EvaluationStartUtc { get; }
    public DateTime EvaluationEndUtc { get; }
    public int HistoryStartIndex { get; }
    public int TradingStartIndex { get; }

    public BacktestInput(
        ImmutableArray<CandleData> bars,
        string symbol,
        TimeFrame frame,
        int dataVersion,
        DateTime evaluationStartUtc,
        DateTime evaluationEndUtc,
        int historyStartIndex,
        int tradingStartIndex)
    {
        // Step 1: null / default-struct checks.
        if (symbol is null) throw new ArgumentNullException(nameof(symbol));
        if (bars.IsDefault) throw new ArgumentException("Bars must be an initialized ImmutableArray, not the default value.", nameof(bars));

        // Step 2: Symbol / Frame / DataVersion / evaluation-range Kind checks.
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol must not be blank.", nameof(symbol));
        }
        string normalizedSymbol = symbol.Normalize(NormalizationForm.FormC);
        if (!Enum.IsDefined(typeof(TimeFrame), frame))
        {
            throw new ArgumentException($"Frame value {frame} is not a defined TimeFrame.", nameof(frame));
        }
        if (dataVersion != CurrentDataVersion)
        {
            throw new UnsupportedDataVersionException(dataVersion);
        }
        if (evaluationStartUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("EvaluationStartUtc.Kind must be DateTimeKind.Utc.", nameof(evaluationStartUtc));
        }
        if (evaluationEndUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("EvaluationEndUtc.Kind must be DateTimeKind.Utc.", nameof(evaluationEndUtc));
        }

        // Step 4: per-bar chronology, Kind, IsValid(), and OHLC > 0 (IsValid() alone does not check positivity).
        for (int i = 0; i < bars.Length; i++)
        {
            CandleData bar = bars[i];
            if (bar.Timestamp.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException($"Bars[{i}].Timestamp.Kind must be DateTimeKind.Utc.", nameof(bars));
            }
            if (i > 0 && bar.Timestamp <= bars[i - 1].Timestamp)
            {
                throw new ArgumentException($"Bars[{i}].Timestamp must be strictly greater than Bars[{i - 1}].Timestamp (chronological order, no duplicates).", nameof(bars));
            }
            if (!bar.IsValid())
            {
                throw new ArgumentException($"Bars[{i}] failed CandleData.IsValid() (OHLC ordering or negative Volume).", nameof(bars));
            }
            if (bar.Open <= 0m || bar.High <= 0m || bar.Low <= 0m || bar.Close <= 0m)
            {
                throw new ArgumentException($"Bars[{i}] must have all of Open/High/Low/Close > 0.", nameof(bars));
            }
        }

        // Step 5 (design rule, nothing to execute): no auto-merge, auto-sort, clamp, or Volume repair is performed above — bad input is a hard error.

        // Step 6: HistoryStartIndex / TradingStartIndex bounds.
        if (historyStartIndex < 0 || historyStartIndex > bars.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(historyStartIndex), historyStartIndex, $"HistoryStartIndex must be in [0, {bars.Length}].");
        }
        if (tradingStartIndex < historyStartIndex || tradingStartIndex > bars.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(tradingStartIndex), tradingStartIndex, $"TradingStartIndex must be in [HistoryStartIndex={historyStartIndex}, {bars.Length}].");
        }

        Bars = bars;
        Symbol = normalizedSymbol;
        Frame = frame;
        DataVersion = dataVersion;
        EvaluationStartUtc = evaluationStartUtc;
        EvaluationEndUtc = evaluationEndUtc;
        HistoryStartIndex = historyStartIndex;
        TradingStartIndex = tradingStartIndex;
    }
}
