using System;

namespace StockAnalyzer.Core.Interfaces;

public static class LocalizationServiceExtensions
{
    public static string GetFormattedString(this ILocalizationService service, string key, string fallbackFormat, params object[] args)
    {
        if (service == null) throw new ArgumentNullException(nameof(service));
        
        string format = service.GetString(key);
        if (string.IsNullOrEmpty(format))
        {
            format = fallbackFormat;
        }

        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            return fallbackFormat != null ? string.Format(fallbackFormat, args) : format;
        }
    }
}
