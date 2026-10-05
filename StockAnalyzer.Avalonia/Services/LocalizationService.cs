using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.Services;

public class LocalizationService : ILocalizationService
{
    public string GetString(string key)
    {
        return LocalizationManager.Instance.Get(key);
    }

    public bool TryGetString(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        return LocalizationManager.Instance.TryGet(key, out value);
    }

    public string this[string key] => GetString(key);
}
