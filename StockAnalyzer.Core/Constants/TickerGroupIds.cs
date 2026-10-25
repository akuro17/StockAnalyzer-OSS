namespace StockAnalyzer.Core.Constants;

/// <summary>
/// Single definition of the ids and display names of the three fixed roots of the Tickers-tab group tree
/// (the names equal the English values of the <c>TickerList_Category_*</c> localisation keys).
/// </summary>
public static class TickerGroupIds
{
    public static readonly Guid AllTickers = new("00000000-0000-0000-0000-000000000001");

    public static readonly Guid Watchlists = new("00000000-0000-0000-0000-000000000002");

    public static readonly Guid Portfolios = new("00000000-0000-0000-0000-000000000003");

    public const string AllTickersName = "All Tickers";

    public const string WatchlistsName = "Watchlists";

    public const string PortfoliosName = "Portfolios";
}
