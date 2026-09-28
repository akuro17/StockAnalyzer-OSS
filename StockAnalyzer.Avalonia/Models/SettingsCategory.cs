using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Models;

/// <summary>
/// Represents a category item in the settings sidebar.
/// </summary>
public record SettingsCategory(
    string Key,
    string TitleKey,
    string IconKey,
    bool IsImplemented = true,
    IReadOnlyList<SettingsCategory>? Children = null
)
{
    public IReadOnlyList<SettingsCategory> Children { get; init; } = Children ?? Array.Empty<SettingsCategory>();
}
