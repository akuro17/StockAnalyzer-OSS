using System;

namespace StockAnalyzer.Core.Models.Attributes;

/// <summary>
/// Attribute to exclude a property from being automatically added to the indicator result.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class IndicatorResultIgnoreAttribute : Attribute
{
}
