using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Evaluates a saved <see cref="ScreenerIndicatorTemplate"/> against a set of symbols, combining its
/// entries with the same And/Or convention as the Screener tab itself.
/// </summary>
public interface IScreenerTemplateEvaluator
{
    Task<HashSet<string>> EvaluateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IProgress<int>? progress,
        CancellationToken ct);
}
