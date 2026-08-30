using System;

namespace StockAnalyzer.Core.Models;

[AttributeUsage(AttributeTargets.Property)]
public sealed class CoreParameterRangeAttribute : Attribute
{
    public object Minimum { get; }
    public object Maximum { get; }

    public CoreParameterRangeAttribute(object minimum, object maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }
}
