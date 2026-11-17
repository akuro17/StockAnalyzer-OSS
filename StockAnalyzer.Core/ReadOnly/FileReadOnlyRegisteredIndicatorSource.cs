using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads <c>source_indicators.json</c> and <c>dynamic_period_drivers.json</c> as the application's <c>IndicatorListStore</c> parses them (same options, same healing), opened with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> so the application can keep saving. Unlike the store, an unreadable or damaged file is never copied to a
/// <c>.corrupted</c> file and never replaced: it is <see cref="DataUnavailableException"/>.
/// </summary>
public sealed class FileReadOnlyRegisteredIndicatorSource : IReadOnlyRegisteredIndicatorSource
{
    private readonly string _sourceIndicatorsPath;
    private readonly string _dynamicPeriodDriversPath;

    public FileReadOnlyRegisteredIndicatorSource(string sourceIndicatorsPath, string dynamicPeriodDriversPath)
    {
        _sourceIndicatorsPath = sourceIndicatorsPath ?? throw new ArgumentNullException(nameof(sourceIndicatorsPath));
        _dynamicPeriodDriversPath = dynamicPeriodDriversPath ?? throw new ArgumentNullException(nameof(dynamicPeriodDriversPath));
    }

    public Task<IReadOnlyList<CoreIndicatorSettings>> GetSourceIndicatorsAsync(CancellationToken cancellationToken = default) => ReadAsync(_sourceIndicatorsPath, cancellationToken);

    public Task<IReadOnlyList<CoreIndicatorSettings>> GetDynamicPeriodDriversAsync(CancellationToken cancellationToken = default) => ReadAsync(_dynamicPeriodDriversPath, cancellationToken);

    private static async Task<IReadOnlyList<CoreIndicatorSettings>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<CoreIndicatorSettings>();
            }

            string json;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            using (var reader = new StreamReader(stream))
            {
                json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            return IndicatorListStore.ParseItems(json, IndicatorListStore.CreateJsonOptions());
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<CoreIndicatorSettings>(); // replaced between the check and the open
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<CoreIndicatorSettings>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new DataUnavailableException("The registered indicator file cannot be read.", error);
        }
    }
}
