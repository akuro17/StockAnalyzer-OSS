using System;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents an aggregated higher-timeframe (W1/MN1) candle whose primary Candle.Timestamp
/// is stamped with PeriodStart, while preserving FirstTradingDay and LastTradingDay metadata.
/// </summary>
public readonly record struct AggregatedPeriodBar(
    CandleData Candle,
    DateTime PeriodStart,
    DateTime FirstTradingDay,
    DateTime LastTradingDay,
    int ConstituentDailyBarCount);
