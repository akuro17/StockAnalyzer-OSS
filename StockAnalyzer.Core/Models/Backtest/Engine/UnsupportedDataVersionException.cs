using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Thrown when BacktestInput.DataVersion does not match a version this engine build understands.
/// DERIVED CONVENTION: the source spec (Y:\0915 Backtesting\01_P1_SimulationEngine.md section 5.2
/// item 2) requires rejecting an "unknown" DataVersion but does not define what versions are known.
/// This engine currently understands only <see cref="BacktestInput.CurrentDataVersion"/>. Flagged
/// for confirmation in Y:\Temp\sa_implementation_plan_BacktestEngineP1.md — adjust the accepted set
/// here if the real requirement is a range or a compatibility list instead of an exact match.
/// </summary>
public sealed class UnsupportedDataVersionException : Exception
{
    public int DataVersion { get; }

    public UnsupportedDataVersionException(int dataVersion)
        : base($"Unsupported BacktestInput.DataVersion: {dataVersion}. Expected {BacktestInput.CurrentDataVersion}.")
    {
        DataVersion = dataVersion;
    }
}
