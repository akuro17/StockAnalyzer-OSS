using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreClassicPivotPointsParameter : CoreIndicatorParameterBase
{
    private PivotSourcePeriod _sourcePeriod = PivotSourcePeriod.PreviousBar;

    [DisplayName("Source Period")]
    [Description("Bars whose high, low and close give the levels: PreviousBar (the previous bar), Week (the previous ISO week) or Month (the previous calendar month). With Week or Month every bar of a period shows the same levels, and a bar sees only periods before its own.")]
    public PivotSourcePeriod SourcePeriod
    {
        get => _sourcePeriod;
        set => SetProperty(ref _sourcePeriod, value);
    }

    public override string GetDisplayName(string type) => SourcePeriod == PivotSourcePeriod.PreviousBar ? type : $"{type} ({SourcePeriod})";

    public override void Validate()
    {
        if (!Enum.IsDefined(SourcePeriod)) throw new ArgumentOutOfRangeException(nameof(SourcePeriod));
    }
}
