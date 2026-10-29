using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The one definition of how the comparison symbols a screener condition names are checked (shared by sa_run_screener and sa_filter_tickers):
/// each must be a stored symbol, there are at most <see cref="McpLimits.MaxSecondarySymbolsPerCall"/> different ones, and a symbol that is also
/// screened is allowed (it is compared with itself). With allowUnregistered a symbol that is not in the ticker list is accepted when it has stored daily bars.
/// </summary>
internal sealed class ScreenerComparisonSymbolResolver
{
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly McpLimits _limits;

    public ScreenerComparisonSymbolResolver(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _limits = limits;
    }

    /// <summary>
    /// The comparison symbols, canonical and distinct. SYMBOL_NOT_FOUND names the parameter that holds the symbol. Symbols used through
    /// <paramref name="allowUnregistered"/> are added to <paramref name="warnings"/> as one warning.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveAsync(
        IReadOnlyList<ScreenerComparisonSymbol> named, string argument, bool allowUnregistered, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        if (named.Count == 0)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        var known = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        var symbols = new List<string>();
        var offList = new List<string>();
        foreach (ScreenerComparisonSymbol item in named)
        {
            string canonical = SymbolResolver.Canonical(item.Symbol, item.Path);
            if (!known.Contains(canonical))
            {
                if (!allowUnregistered)
                {
                    throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonical}' is not available.", item.Path, "Call sa_list_tickers to see the available symbols.");
                }

                await _symbols.RequireStoredBarsAsync(canonical, item.Path, cancellationToken).ConfigureAwait(false);
                if (!offList.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    offList.Add(canonical);
                }
            }

            if (!symbols.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                symbols.Add(canonical);
            }
        }

        if (symbols.Count > _limits.MaxSecondarySymbolsPerCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxSecondarySymbolsPerCall} different comparison symbols per call.",
                argument,
                "Use fewer different ComparisonSymbol values.");
        }

        if (offList.Count > 0)
        {
            warnings.Add(SymbolResolver.OffListWarning(offList));
        }

        return symbols;
    }
}
