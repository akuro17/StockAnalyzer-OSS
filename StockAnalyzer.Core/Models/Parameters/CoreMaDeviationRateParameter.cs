using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter class for MA Deviation Rate indicator, allowing user selection of baseline moving average type and period.
/// Discovered automatically by <see cref="StockAnalyzer.Core.Models.Indicators.CoreIndicatorBase.GetDefaultSettings"/>.
/// </summary>
public class CoreMaDeviationRateParameter : CoreIndicatorParameterBase
{
    private int _period = 25;
    private MovingAverageType _maType = MovingAverageType.SMA;

    [CoreParameterRange(1, 1000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Moving average period for deviation calculation.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("MA Type")]
    [Description("Type of moving average used as the baseline.")]
    public MovingAverageType MaType
    {
        get => _maType;
        set => SetProperty(ref _maType, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {MaType})";

    public override void Validate()
    {
        if (Period <= 0)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be positive.");
        if (!Enum.IsDefined(typeof(MovingAverageType), MaType))
            throw new ArgumentOutOfRangeException(nameof(MaType), "Undefined moving average type.");
    }
}
