using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

public interface IScreenerService
{
    Task<List<string>> ScreenAsync(
        ScreeningCriteria criteria,
        IProgress<int> progress,
        CancellationToken ct);

    Task<List<string>> ScreenAsync(
        List<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IProgress<int> progress,
        CancellationToken ct);
}
