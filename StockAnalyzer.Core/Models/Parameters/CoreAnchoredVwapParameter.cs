using System;
using System.ComponentModel;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAnchoredVwapParameter : CoreIndicatorParameterBase
{
    private int _anchorIndex = 0;

    [DisplayName("Anchor Index")]
    [Description("Anchor bar index for Anchored VWAP. Used only while Anchor Date is empty.")]
    [CoreParameterRange(0, 1000000)]
    public int AnchorIndex
    {
        get => _anchorIndex;
        set => SetProperty(ref _anchorIndex, value);
    }

    private string _anchorDate = string.Empty;

    [DisplayName("Anchor Date")]
    [Description("Anchor date as yyyy-MM-dd. The anchor is the first bar whose date is on or after it (a holiday anchors to the next trading day); a date after the last bar gives no value. Empty = use Anchor Index.")]
    public string AnchorDate
    {
        get => _anchorDate;
        set => SetProperty(ref _anchorDate, value ?? string.Empty);
    }

    private bool _showBands;

    [DisplayName("Show Bands")]
    [Description("Calculate and draw the UpperBand and LowerBand: VWAP plus or minus the multiplier times the volume-weighted standard deviation since the anchor. When off, only the VWAP line is calculated.")]
    public bool ShowBands
    {
        get => _showBands;
        set => SetProperty(ref _showBands, value);
    }

    private decimal _stdDevMultiplier = IndicatorDefaultConstants.VwapStdDevMultiplier;

    [DisplayName("StdDev Multiplier")]
    [Description("Band width in volume-weighted standard deviations. Used only when Show Bands is on.")]
    [CoreParameterRange(IndicatorDefaultConstants.VwapMinStdDevMultiplier, IndicatorDefaultConstants.VwapMaxStdDevMultiplier)]
    public decimal StdDevMultiplier
    {
        get => _stdDevMultiplier;
        set => SetProperty(ref _stdDevMultiplier, value);
    }

    // No anchor given (no date, index 0 = the default) shows the plain name; an explicit date or a non-zero index is shown.
    public override string GetDisplayName(string type) =>
        !string.IsNullOrEmpty(AnchorDate) ? $"{type} ({AnchorDate})"
        : AnchorIndex == 0 ? type
        : $"{type} (Idx: {AnchorIndex})";

    public override void Validate()
    {
        if (AnchorIndex < 0) throw new ArgumentOutOfRangeException(nameof(AnchorIndex));
        if (!string.IsNullOrEmpty(AnchorDate) && !TryParseAnchorDate(AnchorDate, out _))
            throw new ArgumentOutOfRangeException(nameof(AnchorDate));
        if (StdDevMultiplier < (decimal)IndicatorDefaultConstants.VwapMinStdDevMultiplier || StdDevMultiplier > (decimal)IndicatorDefaultConstants.VwapMaxStdDevMultiplier)
            throw new ArgumentOutOfRangeException(nameof(StdDevMultiplier));
    }

    /// <summary>The one definition of the accepted anchor date text (yyyy-MM-dd, invariant culture).</summary>
    public static bool TryParseAnchorDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text, IndicatorDefaultConstants.AnchoredVwapDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
