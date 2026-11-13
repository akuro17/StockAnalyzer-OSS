using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreVolumeProfileLevelsParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.VolumeProfilePeriod;

    [CoreParameterRange(IndicatorDefaultConstants.VolumeProfileLevelsMinPeriod, IndicatorDefaultConstants.DefaultPeriodMax)]
    [DisplayName("Period")]
    [Description("Number of bars (counted back from each bar, including it) whose volume profile gives that bar's POC, VAH and VAL.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _rowCount = IndicatorDefaultConstants.VolumeProfileRowCount;

    [CoreParameterRange(IndicatorDefaultConstants.VolumeProfileMinRowCount, IndicatorDefaultConstants.VolumeProfileMaxRowCount)]
    [DisplayName("Row Count")]
    [Description("Number of price rows (bins) of each profile.")]
    [Category("Profile")]
    public int RowCount
    {
        get => _rowCount;
        set => SetProperty(ref _rowCount, value);
    }

    private VolumeDistributionMode _mode = VolumeDistributionMode.Proportional;

    [DisplayName("Mode")]
    [Description("How a bar's volume is assigned to the rows it touches: Proportional (split evenly) or Full (the whole volume to each touched row).")]
    [Category("Profile")]
    public VolumeDistributionMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    private decimal _valueAreaPercentage = IndicatorDefaultConstants.VolumeProfileLevelsDefaultValueAreaPercentage;

    [CoreParameterRange(IndicatorDefaultConstants.VolumeProfileLevelsMinValueAreaPercentage, IndicatorDefaultConstants.VolumeProfileLevelsMaxValueAreaPercentage)]
    [DisplayName("Value Area")]
    [Description("Share of the profile volume the value area (VAH to VAL) covers, as a fraction (0.70 = 70 %).")]
    [Category("Profile")]
    public decimal ValueAreaPercentage
    {
        get => _valueAreaPercentage;
        set => SetProperty(ref _valueAreaPercentage, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {RowCount})";

    // The first values are at bar index Period - 1 (the first full window).
    public override int GetRequiredWarmupBars() => Period - 1;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.VolumeProfileLevelsMinPeriod, IndicatorDefaultConstants.DefaultPeriodMax, nameof(Period));
        ThrowIfOutOfRange(RowCount, IndicatorDefaultConstants.VolumeProfileMinRowCount, IndicatorDefaultConstants.VolumeProfileMaxRowCount, nameof(RowCount));
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        ThrowIfOutOfRange(ValueAreaPercentage, IndicatorDefaultConstants.VolumeProfileLevelsMinValueAreaPercentage, IndicatorDefaultConstants.VolumeProfileLevelsMaxValueAreaPercentage, nameof(ValueAreaPercentage));
    }
}
