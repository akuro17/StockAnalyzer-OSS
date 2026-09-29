using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreThreeLineBreakParameter : CoreIndicatorParameterBase
{
    private int _numberOfLines = 3;

    [DisplayName("Number of Lines")]
    [Description("Number of consecutive opposing lines required for a reversal.")]
    [CoreParameterRange(1, 100)]
    public int NumberOfLines
    {
        get => _numberOfLines;
        set => SetProperty(ref _numberOfLines, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({NumberOfLines})";

    public override void Validate()
    {
        if (NumberOfLines <= 0) throw new ArgumentOutOfRangeException(nameof(NumberOfLines));
    }
}
