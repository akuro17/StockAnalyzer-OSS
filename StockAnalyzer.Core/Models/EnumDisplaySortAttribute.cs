using System;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Opts an enum type or enum parameter property into localized display-name sorting
/// inside dynamic parameter UI dropdowns without altering underlying enum integer values.
/// </summary>
[AttributeUsage(AttributeTargets.Enum | AttributeTargets.Property)]
public sealed class EnumDisplaySortAttribute : Attribute
{
    /// <summary>
    /// True (default) sorts the localized display names alphabetically. False keeps the declaration order of the enum members
    /// while still using the localized display names (for ordered scales such as Same as Chart / Weekly / Monthly).
    /// </summary>
    public bool SortAlphabetically { get; init; } = true;
}
