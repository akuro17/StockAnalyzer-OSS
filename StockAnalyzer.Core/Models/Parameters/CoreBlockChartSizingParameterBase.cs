using System;
using System.ComponentModel;
using System.Globalization;
using StockAnalyzer.Core.Models.Indicators.Chart;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The size settings shared by the Renko, Point and Figure and Kagi overlays: the inputs of <see cref="Utilities.AutoBoxSizeCalculator"/> that the corresponding chart types also have (sizing mode, fixed size,
/// percentage, ATR period and multiplier, rounding, fallback), with the same defaults and ranges. They are independent copies: nothing here reads or writes <c>GlobalChartSettings</c>.
/// The class names of the concrete parameter classes are PERSISTED (WorkspacePolymorphicResolver uses the type name): NEVER rename them after release.
/// </summary>
public abstract class CoreBlockChartSizingParameterBase : CoreIndicatorParameterBase
{
    /// <summary>The wording of the size settings (also the tooltip of the sizing mode): the overlay size is fixed once, at the anchor bar, so it can differ from the size of the corresponding non-time-series chart.</summary>
    public const string SizingModeDescription =
        "In Auto ATR and Percentage modes the box size is fixed once, at the anchor bar (the first valid bar for which a positive size can be resolved: the bar after the ATR period for Auto ATR, the first bar for Percentage), " +
        "so that values of past bars never change. It can therefore differ from the box size of the corresponding non-time-series chart, which is derived from the whole series.";

    private ChartSizingMode _sizingMode = IndicatorDefaultConstants.BlockChartOverlayDefaultSizingMode;

    [DisplayName("Sizing Mode")]
    [Description(SizingModeDescription)]
    [Category("Size")]
    [EnumDisplaySort(SortAlphabetically = false)]
    public ChartSizingMode SizingMode
    {
        get => _sizingMode;
        set => SetProperty(ref _sizingMode, value);
    }

    private decimal _manualSize = ChartConstants.DefaultBoxSize;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinSize, IndicatorDefaultConstants.BlockChartOverlayMaxSize)]
    [DisplayName("Manual Size")]
    [Description("Fixed box / brick / reversal size in price units (used by the Fixed mode and by the Fixed fallback of Auto ATR).")]
    [Category("Size")]
    public decimal ManualSize
    {
        get => _manualSize;
        set => SetProperty(ref _manualSize, value);
    }

    private decimal _percentage = ChartConstants.DefaultRenkoBrickPercent;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinPercentage, IndicatorDefaultConstants.BlockChartOverlayMaxPercentage)]
    [DisplayName("Percentage")]
    [Description("Size as a percentage of the price at the anchor bar (Percentage mode).")]
    [Category("Size")]
    public decimal Percentage
    {
        get => _percentage;
        set => SetProperty(ref _percentage, value);
    }

    private int _atrPeriod = ChartConstants.DefaultAtrPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinAtrPeriod, IndicatorDefaultConstants.BlockChartOverlayMaxAtrPeriod)]
    [DisplayName("ATR Period")]
    [Description("Period (bars) of the average true range (Auto ATR mode). The size is resolved at the bar after this period.")]
    [Category("Size")]
    public int AtrPeriod
    {
        get => _atrPeriod;
        set => SetProperty(ref _atrPeriod, value);
    }

    private decimal _atrMultiplier = ChartConstants.DefaultAtrMultiplier;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinAtrMultiplier, IndicatorDefaultConstants.BlockChartOverlayMaxAtrMultiplier)]
    [DisplayName("ATR Multiplier")]
    [Description("The size is the ATR times this multiplier (Auto ATR mode).")]
    [Category("Size")]
    public decimal AtrMultiplier
    {
        get => _atrMultiplier;
        set => SetProperty(ref _atrMultiplier, value);
    }

    private ChartRoundingMode _roundingMode = IndicatorDefaultConstants.BlockChartOverlayDefaultRoundingMode;

    [DisplayName("Rounding Mode")]
    [Description("How the resolved size and the first grid line are rounded.")]
    [Category("Size")]
    [EnumDisplaySort(SortAlphabetically = false)]
    public ChartRoundingMode RoundingMode
    {
        get => _roundingMode;
        set => SetProperty(ref _roundingMode, value);
    }

    private AutoFallbackMode _fallbackMode = IndicatorDefaultConstants.BlockChartOverlayDefaultFallbackMode;

    [DisplayName("Fallback Mode")]
    [Description("The size used by Auto ATR when the ATR is zero: a percentage of the price or the fixed size.")]
    [Category("Size")]
    [EnumDisplaySort(SortAlphabetically = false)]
    public AutoFallbackMode FallbackMode
    {
        get => _fallbackMode;
        set => SetProperty(ref _fallbackMode, value);
    }

    /// <summary>The size inputs as the value the overlay driver takes (a method, not a property, so that the settings panel does not list it).</summary>
    public BlockChartSizingSpec ToSizingSpec()
        => new(SizingMode, ManualSize, Percentage, AtrPeriod, AtrMultiplier, RoundingMode, FallbackMode);

    /// <summary>Short text of the size settings for the display name: "Fixed 5", "1%" or "ATR14 x1" (invariant culture).</summary>
    protected string DescribeSizing() => SizingMode switch
    {
        ChartSizingMode.Fixed => string.Create(CultureInfo.InvariantCulture, $"Fixed {ManualSize:0.####}"),
        ChartSizingMode.Percentage => string.Create(CultureInfo.InvariantCulture, $"{Percentage:0.##}%"),
        _ => string.Create(CultureInfo.InvariantCulture, $"ATR{AtrPeriod} x{AtrMultiplier:0.##}"),
    };

    /// <summary>Range checks of the size settings (per field, with the same bounds the attributes carry).</summary>
    protected void ValidateSizing()
    {
        if (!Enum.IsDefined(SizingMode)) throw new ArgumentOutOfRangeException(nameof(SizingMode));
        if (!Enum.IsDefined(RoundingMode)) throw new ArgumentOutOfRangeException(nameof(RoundingMode));
        if (!Enum.IsDefined(FallbackMode)) throw new ArgumentOutOfRangeException(nameof(FallbackMode));

        ThrowIfOutOfRange(ManualSize, IndicatorDefaultConstants.BlockChartOverlayMinSize, IndicatorDefaultConstants.BlockChartOverlayMaxSize, nameof(ManualSize));
        ThrowIfOutOfRange(Percentage, IndicatorDefaultConstants.BlockChartOverlayMinPercentage, IndicatorDefaultConstants.BlockChartOverlayMaxPercentage, nameof(Percentage));
        ThrowIfOutOfRange(AtrPeriod, IndicatorDefaultConstants.BlockChartOverlayMinAtrPeriod, IndicatorDefaultConstants.BlockChartOverlayMaxAtrPeriod, nameof(AtrPeriod));
        ThrowIfOutOfRange(AtrMultiplier, IndicatorDefaultConstants.BlockChartOverlayMinAtrMultiplier, IndicatorDefaultConstants.BlockChartOverlayMaxAtrMultiplier, nameof(AtrMultiplier));
    }
}
