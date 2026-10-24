using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The one definition of symbol input handling for tools: a symbol must be well formed (checked before any lookup), is
/// canonicalised, and must be one of the available tickers. Responses always carry the canonical form.
/// </summary>
internal sealed class SymbolResolver
{
    private readonly IReadOnlyStockAnalyzerGateway _gateway;

    public SymbolResolver(IReadOnlyStockAnalyzerGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>Validates the shape of the symbol and returns its canonical form, without a lookup.</summary>
    public static string Canonical(string symbol, string parameter)
    {
        if (!TickerSymbol.IsWellFormed(symbol))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "The symbol is not well formed.",
                parameter,
                $"Use at most {TickerSymbol.MaxLength} characters without path separators, ':' or control characters.");
        }

        return TickerSymbol.Canonicalize(symbol);
    }

    /// <summary>Canonical symbol that is also in the ticker list; otherwise SYMBOL_NOT_FOUND.</summary>
    public async Task<string> ResolveAsync(string symbol, string parameter, CancellationToken cancellationToken)
    {
        string canonical = Canonical(symbol, parameter);
        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        if (!available.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonical}' is not available.", parameter, "Call sa_list_tickers to see the available symbols.");
        }

        return canonical;
    }
}
