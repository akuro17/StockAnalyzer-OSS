using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>A resolved symbol in canonical form and whether the ticker list holds it (false only for a symbol used through allowUnregistered).</summary>
internal readonly record struct ResolvedSymbol(string Symbol, bool IsRegistered);

/// <summary>
/// The one definition of symbol input handling for tools: a symbol must be well formed (checked before any lookup), is
/// canonicalised, and must be one of the available tickers. Responses always carry the canonical form.
/// </summary>
internal sealed class SymbolResolver
{
    /// <summary>The name of the argument that lets a call use symbols that are not in the ticker list but have stored daily bars.</summary>
    public const string AllowUnregisteredArgument = "allowUnregistered";

    /// <summary>The most symbol names a warning spells out; the rest is counted.</summary>
    private const int MaxNamesListed = 10;

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

    /// <summary>
    /// <see cref="ResolveAsync(string, string, CancellationToken)"/> when <paramref name="allowUnregistered"/> is false (the default of every tool);
    /// <see cref="ResolveStoredAsync"/> when it is true.
    /// </summary>
    public async Task<ResolvedSymbol> ResolveAsync(string symbol, string parameter, bool allowUnregistered, CancellationToken cancellationToken)
        => allowUnregistered
            ? await ResolveStoredAsync(symbol, parameter, cancellationToken).ConfigureAwait(false)
            : new ResolvedSymbol(await ResolveAsync(symbol, parameter, cancellationToken).ConfigureAwait(false), true);

    /// <summary>
    /// A symbol in the ticker list, or one that is not listed but has stored daily bars. A symbol that is in neither case is SYMBOL_NOT_FOUND.
    /// Nothing is fetched: only bars already stored are looked at.
    /// </summary>
    public async Task<ResolvedSymbol> ResolveStoredAsync(string symbol, string parameter, CancellationToken cancellationToken)
    {
        string canonical = Canonical(symbol, parameter);
        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        if (available.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            return new ResolvedSymbol(canonical, true);
        }

        await RequireStoredBarsAsync(canonical, parameter, cancellationToken).ConfigureAwait(false);
        return new ResolvedSymbol(canonical, false);
    }

    /// <summary>True when at least one daily bar of the (canonical) symbol is stored.</summary>
    public async Task<bool> HasStoredBarsAsync(string canonicalSymbol, CancellationToken cancellationToken)
        => (await _gateway.GetCandlesAsync(canonicalSymbol, TimeFrame.D1, 1, cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <summary>SYMBOL_NOT_FOUND unless the (canonical) symbol has stored daily bars.</summary>
    public async Task RequireStoredBarsAsync(string canonicalSymbol, string parameter, CancellationToken cancellationToken)
    {
        if (!await HasStoredBarsAsync(canonicalSymbol, cancellationToken).ConfigureAwait(false))
        {
            throw NotStored(canonicalSymbol, parameter);
        }
    }

    /// <summary>The SYMBOL_NOT_FOUND of a symbol that is neither in the ticker list nor has stored daily bars.</summary>
    public static ToolException NotStored(string canonicalSymbol, string parameter) => new(
        ToolErrorCodes.SymbolNotFound,
        $"Symbol '{canonicalSymbol}' is not in the ticker list and has no stored daily bars.",
        parameter,
        "Check the symbol; allowUnregistered only reads bars that are already stored, it never fetches data.");

    /// <summary>The warning that names the symbols a call used although the ticker list does not hold them.</summary>
    public static string OffListWarning(IReadOnlyCollection<string> symbols)
        => $"{symbols.Count} symbol(s) are not in the ticker list and were used from their stored daily bars because {AllowUnregisteredArgument} is true: {NameList(symbols)}.";

    /// <summary>The names in ordinal order, at most <see cref="MaxNamesListed"/> of them, then the count of the rest.</summary>
    public static string NameList(IEnumerable<string> symbols)
    {
        List<string> sorted = symbols.OrderBy(s => s, StringComparer.Ordinal).ToList();
        string listed = string.Join(", ", sorted.Take(MaxNamesListed));
        return sorted.Count > MaxNamesListed ? $"{listed}, and {sorted.Count - MaxNamesListed} more" : listed;
    }
}
