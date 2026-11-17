using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>The stored portfolio as the application would show it, and whether anything was stored (<see cref="Found"/> false: the empty portfolio).</summary>
public sealed record PortfolioReadResult(bool Found, Portfolio Portfolio);

/// <summary>Read-only access to the saved portfolio (positions, transactions, cash). Nothing is created, recovered or written.</summary>
public interface IReadOnlyPortfolioSource
{
    /// <exception cref="DataUnavailableException">The files are incomplete, a save is in progress, or they cannot be read.</exception>
    Task<PortfolioReadResult> LoadAsync(CancellationToken cancellationToken = default);
}
