namespace StockAnalyzer.Avalonia.Common;

public class LocalizationSettings
{
    public string ResourcePath { get; set; } = "Resources/Locales";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ResourcePath))
            throw new System.InvalidOperationException("LocalizationSettings: ResourcePath cannot be empty.");
    }
}
