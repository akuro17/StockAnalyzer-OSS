using System;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Opts an enum type or enum parameter property into localized display-name sorting
/// inside dynamic parameter UI dropdowns without altering underlying enum integer values.
/// </summary>
[AttributeUsage(AttributeTargets.Enum | AttributeTargets.Property)]
public sealed class EnumDisplaySortAttribute : Attribute
{
}
