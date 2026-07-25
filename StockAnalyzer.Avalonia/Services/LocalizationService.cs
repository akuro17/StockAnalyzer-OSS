using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.Services;

public class LocalizationService : ILocalizationService
{
    public string GetString(string key)
    {
        return LocalizationManager.Instance.Get(key);
    }
}
