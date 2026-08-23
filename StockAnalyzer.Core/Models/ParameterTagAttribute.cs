using System;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Marks an indicator parameter property with a metadata tag that UI-building code
/// (e.g. ParameterViewBuilder) can use to conditionally include/exclude the property
/// from a rendered settings panel, independent of the property's data type or range.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class ParameterTagAttribute : Attribute
{
    public string Tag { get; }

    public ParameterTagAttribute(string tag)
    {
        Tag = tag;
    }
}
