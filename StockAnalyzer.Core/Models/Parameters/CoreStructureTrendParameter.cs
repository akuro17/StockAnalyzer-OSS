using System;
using System.ComponentModel;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters of the Higher High / Higher Low flag (plan ChartPatterns56 v2, run V2-10, integration spec 5.2 class 5;
/// engine <c>HigherHighHigherLowEngine</c>). NEVER rename or move: the class name is the JSON type discriminator of the workspace.
/// The flag is a state, not an event.
/// </summary>
public class CoreStructureTrendParameter : CoreIndicatorParameterBase
{
    private decimal _zigZagThresholdPercent = PatternFlagParameterLimits.StructureTrendZigZagDefault;
    private decimal _minSwingAtrMultiplier = PatternFlagParameterLimits.StructureTrendMinSwingAtrDefault;
    private decimal _minStepUpPercent = PatternFlagParameterLimits.StructureTrendMinStepUpPercentDefault;
    private int _minPivotIntervalBars = PatternFlagParameterLimits.StructureTrendMinPivotIntervalDefault;
    private int _maxPivotIntervalBars = PatternFlagParameterLimits.StructureTrendMaxPivotIntervalDefault;
    private int _maxLastPivotAgeBars = PatternFlagParameterLimits.StructureTrendMaxLastPivotAgeDefault;
    private bool _requireCloseAboveLastLow = PatternFlagParameterLimits.StructureTrendRequireCloseAboveLastLowDefault;

    [DisplayName("ZigZag Threshold (%)")]
    [Description("Minimum percentage swing that makes a confirmed pivot. Unit: percent of price.")]
    [Category("Swing")]
    [CoreParameterRange(PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.StructureTrendZigZagMax)]
    public decimal ZigZagThresholdPercent
    {
        get => _zigZagThresholdPercent;
        set => SetProperty(ref _zigZagThresholdPercent, value);
    }

    [DisplayName("Min Swing (ATR)")]
    [Description("Minimum swing amplitude in context ATR(14) multiples before reversal confirmation. 0 switches the ATR test off.")]
    [Category("Swing")]
    [CoreParameterRange(PatternFlagParameterLimits.StructureTrendMinSwingAtrMin, PatternFlagParameterLimits.StructureTrendMinSwingAtrMax)]
    public decimal MinSwingAtrMultiplier
    {
        get => _minSwingAtrMultiplier;
        set => SetProperty(ref _minSwingAtrMultiplier, value);
    }

    [DisplayName("Min Step-Up (%)")]
    [Description("Minimum percent step-up for HH and HL, required together with 0.50 * ATR(14). Setting 0 disables only the percentage floor. Unit: percent of prior pivot price.")]
    [Category("Geometry")]
    [CoreParameterRange(PatternFlagParameterLimits.StructureTrendMinStepUpPercentMin, PatternFlagParameterLimits.StructureTrendMinStepUpPercentMax)]
    public decimal MinStepUpPercent
    {
        get => _minStepUpPercent;
        set => SetProperty(ref _minStepUpPercent, value);
    }

    [DisplayName("Min Pivot Interval (bars)")]
    [Description("Minimum bar distance between consecutive confirmed highs (H1..H2) and consecutive confirmed lows (L1..L2). Unit: bars.")]
    [Category("Duration")]
    [CoreParameterRange(PatternFlagParameterLimits.StructureTrendMinPivotIntervalMin, PatternFlagParameterLimits.StructureTrendMinPivotIntervalMax)]
    public int MinPivotIntervalBars
    {
        get => _minPivotIntervalBars;
        set => SetProperty(ref _minPivotIntervalBars, value);
    }

    [DisplayName("Max Pivot Interval (bars)")]
    [Description("Maximum bar distance between consecutive confirmed highs (H1..H2) and consecutive confirmed lows (L1..L2). Unit: bars.")]
    [Category("Duration")]
    [CoreParameterRange(PatternFlagParameterLimits.StructureTrendMaxPivotIntervalMin, PatternFlagParameterLimits.StructureTrendMaxPivotIntervalMax)]
    public int MaxPivotIntervalBars
    {
        get => _maxPivotIntervalBars;
        set => SetProperty(ref _maxPivotIntervalBars, value);
    }

    [DisplayName("Max Last Pivot Age (bars)")]
    [Description("Maximum bars since the latest confirmation among the four structure pivots (excludes stale sideways regimes). Unit: bars.")]
    [Category("Duration")]
    [CoreParameterRange(PatternFlagParameterLimits.StructureTrendMaxLastPivotAgeMin, PatternFlagParameterLimits.StructureTrendMaxLastPivotAgeMax)]
    public int MaxLastPivotAgeBars
    {
        get => _maxLastPivotAgeBars;
        set => SetProperty(ref _maxLastPivotAgeBars, value);
    }

    [DisplayName("Require Close Above Last Low")]
    [Description("When on, requires current close strictly beyond the latest opposing pivot (C_t > L_2 for HH/HL, or C_t < H_2 for LL/LH) to keep the structure active.")]
    [Category("Validity")]
    public bool RequireCloseAboveLastLow
    {
        get => _requireCloseAboveLastLow;
        set => SetProperty(ref _requireCloseAboveLastLow, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({ZigZagThresholdPercent}%)";

    /// <summary>Bars requested as warm-up (a request, never a minimum; decision U2-16 A, plan R-28).</summary>
    public override int GetRequiredWarmupBars() => Math.Max(
        Math.Max(
            PatternSpecConstants.StructureTrendMinBars,
            (PatternSpecConstants.StructureTrendSpanIntervalMultiplier * MaxPivotIntervalBars)
            + MaxLastPivotAgeBars
            + PatternSpecConstants.StructureTrendWarmupMarginBars),
        PatternFlagParameterLimits.WarmupBars);

    public override void Validate()
    {
        ThrowIfOutOfRange(ZigZagThresholdPercent, PatternFlagParameterLimits.ZigZagMin, PatternFlagParameterLimits.StructureTrendZigZagMax, nameof(ZigZagThresholdPercent));
        ThrowIfOutOfRange(MinSwingAtrMultiplier, PatternFlagParameterLimits.StructureTrendMinSwingAtrMin, PatternFlagParameterLimits.StructureTrendMinSwingAtrMax, nameof(MinSwingAtrMultiplier));
        ThrowIfOutOfRange(MinStepUpPercent, PatternFlagParameterLimits.StructureTrendMinStepUpPercentMin, PatternFlagParameterLimits.StructureTrendMinStepUpPercentMax, nameof(MinStepUpPercent));
        ThrowIfOutOfRange(MinPivotIntervalBars, PatternFlagParameterLimits.StructureTrendMinPivotIntervalMin, PatternFlagParameterLimits.StructureTrendMinPivotIntervalMax, nameof(MinPivotIntervalBars));
        ThrowIfOutOfRange(MaxPivotIntervalBars, PatternFlagParameterLimits.StructureTrendMaxPivotIntervalMin, PatternFlagParameterLimits.StructureTrendMaxPivotIntervalMax, nameof(MaxPivotIntervalBars));
        ThrowIfOutOfRange(MaxLastPivotAgeBars, PatternFlagParameterLimits.StructureTrendMaxLastPivotAgeMin, PatternFlagParameterLimits.StructureTrendMaxLastPivotAgeMax, nameof(MaxLastPivotAgeBars));
        if (MinPivotIntervalBars > MaxPivotIntervalBars)
        {
            throw new ArgumentOutOfRangeException(nameof(MinPivotIntervalBars), "MinPivotIntervalBars must not exceed MaxPivotIntervalBars.");
        }
    }
}
