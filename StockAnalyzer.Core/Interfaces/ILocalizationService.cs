namespace StockAnalyzer.Core.Interfaces;

public interface ILocalizationService
{
    string GetString(string key);
    string this[string key] => GetString(key);
}

public class NullLocalizationService : ILocalizationService
{
    public static NullLocalizationService Instance { get; } = new();
    public string GetString(string key) => key;
}
