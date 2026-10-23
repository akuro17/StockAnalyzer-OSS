using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>The screener templates found in the template folder, and how many files in it could not be read.</summary>
public sealed record ScreenerTemplateListing(IReadOnlyList<ScreenerIndicatorTemplate> Templates, int UnreadableFileCount);

/// <summary>The backtest-condition templates found in their folder, and how many files in it could not be read.</summary>
public sealed record BacktestConditionTemplateListing(IReadOnlyList<BacktestConditionTemplate> Templates, int UnreadableFileCount);

/// <summary>
/// Reads the user's saved Screener and backtest-condition templates without changing anything: no folder is created, no legacy file is migrated,
/// no file is written. (<c>TemplateService</c> migrates legacy files and creates folders when it reads, so it cannot be used by a read-only host.)
/// </summary>
public interface IReadOnlyTemplateSource
{
    /// <summary>Every readable backtest-condition template, favourites first and then by name. An absent folder is an empty listing.</summary>
    Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The backtest-condition template with <paramref name="id"/>, or null when there is none or its file cannot be read.</summary>
    Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every readable Screener template, favourites first and then by name, as the Screener tab lists them. An absent folder is an empty listing.</summary>
    Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The Screener template with <paramref name="id"/>, or null when there is none or its file cannot be read.</summary>
    Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default);
}
