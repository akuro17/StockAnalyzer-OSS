using System;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Markup;

/// <summary>
/// XAML Markup Extension for localized strings.
/// Usage: Text="{Localize Key}" or Content="{Localize Btn_OK}"
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    /// <summary>
    /// The localization key to look up.
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// Creates a new instance of the LocalizeExtension.
    /// </summary>
    public LocalizeExtension()
    {
    }
    
    /// <summary>
    /// Creates a new instance of the LocalizeExtension with the specified key.
    /// </summary>
    /// <param name="key">The localization key</param>
    public LocalizeExtension(string key)
    {
        Key = key;
    }
    
    /// <summary>
    /// Provides the localized string value.
    /// </summary>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return string.Empty;
            
        return LocalizationManager.Instance.Get(Key);
    }
}
