using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Singleton service that manages UI localization strings.
/// Loads strings from JSON resource files and provides lookup by key.
/// </summary>
public sealed class LocalizationManager
{
    private static readonly Lazy<LocalizationManager> _instance = new(() => new LocalizationManager());
    
    /// <summary>
    /// Gets the singleton instance of the LocalizationManager.
    /// </summary>
    public static LocalizationManager Instance => _instance.Value;
    
    private Dictionary<string, string> _strings = new();
    private string _currentLanguage = "en";
    
    /// <summary>
    /// Gets the current language code (e.g., "en", "ja").
    /// </summary>
    public string CurrentLanguage => _currentLanguage;
    
    private LocalizationManager()
    {
        // Private constructor for singleton pattern
    }
    
    /// <summary>
    /// Initializes the localization manager with the specified language.
    /// Loads strings from the corresponding JSON file in Resources/Locales/.
    /// </summary>
    /// <param name="languageCode">The language code (e.g., "en", "ja")</param>
    /// <param name="resourcePath">Optional custom path to locales directory. If null, falls back to default Resources/Locales.</param>
    public void Initialize(string languageCode = "en", string? resourcePath = null)
    {
        _currentLanguage = languageCode;
        LoadStrings(languageCode, resourcePath);
    }
    
    /// <summary>
    /// Gets a localized string by key.
    /// Returns the key itself if not found (for easier debugging).
    /// </summary>
    /// <param name="key">The localization key</param>
    /// <returns>The localized string, or the key if not found</returns>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;
            
        return _strings.TryGetValue(key, out var value) ? value : $"[{key}]";
    }
    
    /// <summary>
    /// Indexer for convenient access to localized strings.
    /// </summary>
    public string this[string key] => Get(key);
    
    private void LoadStrings(string languageCode, string? resourcePath)
    {
        try
        {
            // Try to load from embedded resource first (Highest priority for built-in locales)
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"StockAnalyzer.Avalonia.Resources.Locales.{languageCode}.json";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                ParseJson(json);
                return;
            }
            
            // Fallback: Try to load from file system
            var filePath = ResolveLocaleFilePath(languageCode, resourcePath, assembly);
            
            if (filePath != null && File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                ParseJson(json);
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"[LocalizationManager] Could not find localization file for '{languageCode}'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationManager] Error loading localization: {ex.Message}");
        }
    }

    private string? ResolveLocaleFilePath(string languageCode, string? customResourcePath, Assembly assembly)
    {
        var fileName = $"{languageCode}.json";

        // 1. Try custom path if provided
        if (!string.IsNullOrWhiteSpace(customResourcePath))
        {
            var customPath = Path.Combine(customResourcePath, fileName);
            if (File.Exists(customPath))
                return customPath;

            // Also try relative to base directory
            var baseDirCustomPath = Path.Combine(AppContext.BaseDirectory, customResourcePath, fileName);
            if (File.Exists(baseDirCustomPath))
                return baseDirCustomPath;
        }

        // 2. Try default fallback paths
        var defaultResourcePath = Path.Combine("Resources", "Locales");
        
        var assemblyLocation = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        var defaultPath1 = Path.Combine(assemblyLocation, defaultResourcePath, fileName);
        if (File.Exists(defaultPath1))
            return defaultPath1;

        var defaultPath2 = Path.Combine(defaultResourcePath, fileName);
        if (File.Exists(defaultPath2))
            return defaultPath2;

        return null;
    }
    
    private void ParseJson(string json)
    {
        _strings.Clear();
        
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Skip metadata properties (those starting with "_")
            if (property.Name.StartsWith("_"))
                continue;
                
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                _strings[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
    }
}
