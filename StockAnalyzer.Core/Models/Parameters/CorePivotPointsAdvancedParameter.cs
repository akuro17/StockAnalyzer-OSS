using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.Chart;

namespace StockAnalyzer.Core.Models.Parameters;

public class CorePivotPointsAdvancedParameter : CoreIndicatorParameterBase
{
    private PivotPointType _pivotType = PivotPointType.Standard;

    [DisplayName("Pivot Type")]
    [Description("Pivot formula: Standard (floor), Fibonacci, Woodie (weights the previous close twice) or Camarilla. Levels of a bar come from the previous bar's high, low and close.")]
    public PivotPointType PivotType
    {
        get => _pivotType;
        set => SetProperty(ref _pivotType, value);
    }

    private PivotSourcePeriod _sourcePeriod = PivotSourcePeriod.PreviousBar;

    [DisplayName("Source Period")]
    [Description("Bars whose high, low and close give the levels: PreviousBar (the previous bar), Week (the previous ISO week) or Month (the previous calendar month). With Week or Month every bar of a period shows the same levels. Woodie then uses the close of that previous period.")]
    public PivotSourcePeriod SourcePeriod
    {
        get => _sourcePeriod;
        set => SetProperty(ref _sourcePeriod, value);
    }

    public override string GetDisplayName(string type) =>
        SourcePeriod == PivotSourcePeriod.PreviousBar ? $"{type} ({PivotType})" : $"{type} ({PivotType}, {SourcePeriod})";

    public override void Validate()
    {
        if (!Enum.IsDefined(PivotType)) throw new ArgumentOutOfRangeException(nameof(PivotType));
        if (!Enum.IsDefined(SourcePeriod)) throw new ArgumentOutOfRangeException(nameof(SourcePeriod));
    }
}
