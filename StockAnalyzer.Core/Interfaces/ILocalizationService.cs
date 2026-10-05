using System;
using System.Diagnostics.CodeAnalysis;

namespace StockAnalyzer.Core.Interfaces;

public interface ILocalizationService
{
    string GetString(string key);
    string this[string key] => GetString(key);

    /// <summary>
    /// Attempts to retrieve a localized string for the specified key.
    /// Returns false if the key is not found or is returned as a missing-key placeholder (e.g. "[key]").
    /// </summary>
    bool TryGetString(string key, [NotNullWhen(true)] out string? value)
    {
        if (string.IsNullOrEmpty(key))
        {
            value = null;
            return false;
        }

        string result = GetString(key);
        if (!string.IsNullOrEmpty(result) &&
            !(result.StartsWith('[') && result.EndsWith(']') && result.Length == key.Length + 2 && result.AsSpan(1, key.Length).SequenceEqual(key)))
        {
            value = result;
            return true;
        }

        value = null;
        return false;
    }
}

public class NullLocalizationService : ILocalizationService
{
    public static NullLocalizationService Instance { get; } = new();
    public string GetString(string key) => key;
    public bool TryGetString(string key, [NotNullWhen(true)] out string? value)
    {
        value = key;
        return !string.IsNullOrEmpty(key);
    }
}
