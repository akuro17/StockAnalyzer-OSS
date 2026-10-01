using System;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Thrown by <see cref="UserStrategyMetadataRepository.GetStrategyAsync"/> when the ticker's persisted strategy data
/// could not be read. A caller that would write the strategy back must stop: what is stored is unknown, and the write
/// API replaces every field it is given.
/// </summary>
public sealed class StrategyLoadFailedException : Exception
{
    public StrategyLoadFailedException(string ticker)
        : base($"The stored strategy data of '{ticker}' could not be read.")
    {
        Ticker = ticker;
    }

    public string Ticker { get; }
}
