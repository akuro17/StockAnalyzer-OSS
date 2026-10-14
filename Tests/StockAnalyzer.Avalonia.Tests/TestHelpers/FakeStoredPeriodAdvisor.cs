#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.StoredPeriod;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>A scripted <see cref="IStoredPeriodAdvisor"/>: each family answers through a replaceable function and records its calls.</summary>
public sealed class FakeStoredPeriodAdvisor : IStoredPeriodAdvisor
{
    private readonly object _gate = new();
    public List<(string Symbol, int Required, DateOnly? Bound)> RlCalls { get; } = new();
    public List<(IReadOnlyList<string> Symbols, TrainingTimeframe Timeframe, int Required, DateOnly? Bound)> TrainingCalls { get; } = new();
    public List<IReadOnlyList<string>> LatestCalls { get; } = new();

    public Func<string, int, DateOnly?, Task<StoredPeriodProposal>> Rl { get; set; } = (_, required, _) => Task.FromResult(StoredPeriodProposal.None(StoredPeriodStatus.NoData, required));
    public Func<IReadOnlyList<string>, Task<DateOnly?>> Latest { get; set; } = _ => Task.FromResult<DateOnly?>(null);
    public Func<IReadOnlyList<string>, TrainingTimeframe, int, DateOnly?, Task<StoredPeriodProposal>> Training { get; set; }
        = (_, _, required, _) => Task.FromResult(StoredPeriodProposal.None(StoredPeriodStatus.NoData, required));

    public static StoredPeriodProposal Found(DateOnly start, DateOnly latest, int required)
        => new(StoredPeriodStatus.Found, start, latest, required, required, Array.Empty<string>());

    public static StoredPeriodProposal Short(DateOnly start, DateOnly latest, int required, int available, params string[] shortSymbols)
        => new(StoredPeriodStatus.InsufficientHistory, start, latest, required, available, shortSymbols);

    public Task<StoredPeriodProposal> ProposeForRlAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken)
    {
        lock (_gate) RlCalls.Add((symbol, requiredBars, endBoundInclusive));
        return Rl(symbol, requiredBars, endBoundInclusive);
    }

    public Task<DateOnly?> LatestFinalizedDateAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken)
    {
        lock (_gate) LatestCalls.Add(symbols);
        return Latest(symbols);
    }

    public Task<StoredPeriodProposal> ProposeForTrainingAsync(IReadOnlyList<string> symbols, TrainingTimeframe timeframe, int requiredBars,
        DateOnly? endBoundInclusive, CancellationToken cancellationToken)
    {
        lock (_gate) TrainingCalls.Add((symbols, timeframe, requiredBars, endBoundInclusive));
        return Training(symbols, timeframe, requiredBars, endBoundInclusive);
    }
}
