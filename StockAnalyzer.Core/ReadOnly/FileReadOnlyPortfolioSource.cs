using StockAnalyzer.Core.Factories;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads the portfolio files exactly as <see cref="UserPortfolioRepository"/> does (the same reader), but never recovers an unfinished save (that would write) and never creates the folder.
/// A save in progress (<c>portfolio.swap</c>) or an incomplete set of files is <see cref="DataUnavailableException"/>, not "no portfolio": the application is replacing them and the next read sees them whole.
/// The history is rebuilt with <see cref="PortfolioManager.RebuildPortfolio(decimal, IReadOnlyList{Transaction}, CostBasisMethod)"/> (initial cash 0, as the application does outside tests) and the shared <see cref="PortfolioAccountingDefaults.CostBasis"/>,
/// so closed positions, realized profit and the average cost of a partly exited position are as the application shows them.
/// </summary>
public sealed class FileReadOnlyPortfolioSource : IReadOnlyPortfolioSource
{
    private readonly string _portfolioDirectory;
    private readonly DuckDBConnectionManager _database;

    public FileReadOnlyPortfolioSource(string portfolioDirectory, DuckDBConnectionManager database)
    {
        _portfolioDirectory = portfolioDirectory ?? throw new ArgumentNullException(nameof(portfolioDirectory));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<PortfolioReadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        string positions = Path.Combine(_portfolioDirectory, UserPortfolioRepository.PositionsFileName);
        string transactions = Path.Combine(_portfolioDirectory, UserPortfolioRepository.TransactionsFileName);
        string cash = Path.Combine(_portfolioDirectory, UserPortfolioRepository.CashFileName);
        string cashBalances = Path.Combine(_portfolioDirectory, UserPortfolioRepository.CashBalancesFileName);

        if (File.Exists(Path.Combine(_portfolioDirectory, UserPortfolioRepository.SwapMarkerFileName)))
        {
            throw new DataUnavailableException("The portfolio is being saved.");
        }

        string[] core = { positions, transactions, cash };
        if (!core.Any(File.Exists))
        {
            return new PortfolioReadResult(false, PortfolioFactory.Empty);
        }

        if (!core.All(File.Exists))
        {
            throw new DataUnavailableException("The stored portfolio is incomplete.");
        }

        try
        {
            Portfolio? loaded = await UserPortfolioRepository.LoadFromParquetFilesAsync(_database, positions, transactions, cash, cashBalances, cancellationToken).ConfigureAwait(false);
            return loaded is null
                ? new PortfolioReadResult(false, PortfolioFactory.Empty)
                : new PortfolioReadResult(true, new PortfolioManager().RebuildPortfolio(0m, loaded.History, PortfolioAccountingDefaults.CostBasis));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new DataUnavailableException("The stored portfolio cannot be read.", error);
        }
    }
}
