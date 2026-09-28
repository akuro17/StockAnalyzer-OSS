using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Interface for services responsible for fetching and synchronizing historical data across multiple symbols.
/// </summary>
public interface IComparisonDataAligner
{
    int MaxComparisonSymbols { get; set; }
    
    Task<ComparisonAlignedData> AlignAsync(
        string primarySymbol, 
        IReadOnlyList<string> comparisonSymbols, 
        TimeFrame timeFrame, 
        int candleCount,
        CancellationToken ct = default);
}
