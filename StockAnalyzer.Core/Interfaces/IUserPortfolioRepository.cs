using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Factories;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Defines the contract for user portfolio persistence.
/// </summary>
public interface IUserPortfolioRepository
{
    /// <summary>
    /// Loads the saved portfolio asynchronously.
    /// </summary>
    ValueTask<Portfolio> LoadPortfolioAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves the portfolio state asynchronously.
    /// </summary>
    ValueTask SavePortfolioAsync(Portfolio portfolio, CancellationToken ct = default);
}

/// <summary>
/// A Null Object pattern implementation for the IUserPortfolioRepository interface.
/// </summary>
public sealed class NullUserPortfolioRepository : IUserPortfolioRepository
{
    private static readonly NullUserPortfolioRepository _instance = new();

    public static NullUserPortfolioRepository Instance => _instance;

    private NullUserPortfolioRepository() { }

    public ValueTask<Portfolio> LoadPortfolioAsync(CancellationToken ct = default)
    {
        return ValueTask.FromResult(PortfolioFactory.Empty);
    }

    public ValueTask SavePortfolioAsync(Portfolio portfolio, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }
}
