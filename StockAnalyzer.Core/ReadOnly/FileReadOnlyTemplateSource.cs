using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads the Screener and backtest-condition templates from their folders with the JSON options <c>TemplateService</c> writes them with. The folders
/// are only listed and opened for reading; when a folder does not exist the listing is empty (nothing is created).
/// <para>
/// <c>TemplateService.EnsureMigratedAsync</c> is not run: it only migrates the legacy Indicator and Column template files into their own
/// folders, which neither read ever touches (plan S-13a / A-09 would report a pending migration for a type it cannot affect).
/// </para>
/// </summary>
public sealed class FileReadOnlyTemplateSource : IReadOnlyTemplateSource
{
    /// <summary>The options <c>TemplateService</c> uses (a test compares the two so they cannot drift apart unnoticed).</summary>
    public static JsonSerializerOptions CreateJsonOptions() => new()
    {
        WriteIndented = true,
        TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new IndicatorColorJsonConverter() },
    };

    private readonly ReadOnlyTemplateFolder<ScreenerIndicatorTemplate> _screeners;
    private readonly ReadOnlyTemplateFolder<BacktestConditionTemplate>? _backtestConditions;

    /// <param name="screenerDirectory">The Screener template folder (see <see cref="PathDiscovery.ResolveTemplatesDirectoryWithoutCreating"/>).</param>
    /// <param name="backtestConditionDirectory">The backtest-condition template folder; null serves no backtest templates (an empty listing).</param>
    public FileReadOnlyTemplateSource(string screenerDirectory, string? backtestConditionDirectory = null)
    {
        _screeners = new ReadOnlyTemplateFolder<ScreenerIndicatorTemplate>(screenerDirectory ?? throw new ArgumentNullException(nameof(screenerDirectory)), CreateJsonOptions());
        _backtestConditions = backtestConditionDirectory is null
            ? null
            : new ReadOnlyTemplateFolder<BacktestConditionTemplate>(backtestConditionDirectory, CreateJsonOptions());
    }

    public async Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<ScreenerIndicatorTemplate> templates, int unreadable) = await _screeners.ListAsync(cancellationToken).ConfigureAwait(false);
        return new ScreenerTemplateListing(templates, unreadable);
    }

    public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => _screeners.GetAsync(id, cancellationToken);

    public async Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (_backtestConditions is null)
        {
            return new BacktestConditionTemplateListing(Array.Empty<BacktestConditionTemplate>(), 0);
        }

        (IReadOnlyList<BacktestConditionTemplate> templates, int unreadable) = await _backtestConditions.ListAsync(cancellationToken).ConfigureAwait(false);
        return new BacktestConditionTemplateListing(templates, unreadable);
    }

    public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => _backtestConditions is null
            ? Task.FromResult<BacktestConditionTemplate?>(null)
            : _backtestConditions.GetAsync(id, cancellationToken);
}
