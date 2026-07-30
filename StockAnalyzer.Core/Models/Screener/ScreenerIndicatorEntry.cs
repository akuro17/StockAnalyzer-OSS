using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Represents a registered screening condition entry.
/// Can compare a Left-hand Indicator against either a scalar Numeric Value
/// or a Right-hand Indicator, each with independent TimeFrames and Offsets.
/// </summary>
public class ScreenerIndicatorEntry
{
    /// <summary>
    /// Left-hand indicator configuration (比較元).
    /// </summary>
    public ScreenerIndicatorSideConfig LeftHand { get; set; } = new();

    /// <summary>
    /// Comparison operator (&gt;, &gt;=, &lt;, &lt;=, ==, !=, CrossAbove, CrossBelow).
    /// </summary>
    public ComparisonOperator Operator { get; set; } = ComparisonOperator.GreaterThan;

    /// <summary>
    /// Target mode for the right-hand side (NumericValue vs Indicator).
    /// </summary>
    public RightHandTargetMode TargetMode { get; set; } = RightHandTargetMode.NumericValue;

    /// <summary>
    /// Static numeric value when TargetMode is NumericValue.
    /// </summary>
    public decimal RightNumericValue { get; set; }

    /// <summary>
    /// Static string value when TargetMode is StringValue.
    /// </summary>
    public string RightStringValue { get; set; } = string.Empty;

    /// <summary>
    /// Right-hand indicator configuration (比較先) when TargetMode is Indicator.
    /// </summary>
    public ScreenerIndicatorSideConfig RightHand { get; set; } = new();

    /// <summary>
    /// Category type of the catalog item (Indicator, Column, Criteria).
    /// </summary>
    public ScreenerItemCategoryType CategoryType { get; set; } = ScreenerItemCategoryType.Indicator;

    /// <summary>
    /// Gets whether this entry represents a Criteria item (where TimeFrame display is omitted).
    /// </summary>
    public bool IsCriteria => CategoryType == ScreenerItemCategoryType.Criteria;

    /// <summary>
    /// Accurate, human-readable display string for TimeFrame (e.g. Day, Week, Month).
    /// </summary>
    public string TimeFrameDisplayName => TimeFrame switch
    {
        TimeFrame.D1 => "Day",
        TimeFrame.W1 => "Week",
        TimeFrame.MN1 => "Month",
        TimeFrame.H1 => "1 Hour",
        TimeFrame.H4 => "4 Hours",
        TimeFrame.M5 => "5 Min",
        TimeFrame.M15 => "15 Min",
        TimeFrame.M30 => "30 Min",
        _ => TimeFrame.ToString()
    };

    private IEnumerable<object> NumericParameters => LeftHand.Parameters.Values.Where(v => v is not bool);

    /// <summary>
    /// Gets whether LeftHand has any numeric parameters (Period).
    /// </summary>
    public bool HasPeriod => NumericParameters.Any();

    /// <summary>
    /// Gets formatted period string (e.g. "14" or "12, 26, 9") for display in condition sub-row.
    /// </summary>
    public string PeriodDisplayName
    {
        get
        {
            var nums = NumericParameters.ToList();
            if (nums.Count == 0) return string.Empty;
            return string.Join(", ", nums);
        }
    }

    /// <summary>
    /// Whether this entry is enabled for screening.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    // Backward-compatibility forwarding properties for LeftHand
    public IndicatorType IndicatorType
    {
        get => LeftHand.IndicatorType;
        set => LeftHand.IndicatorType = value;
    }

    public Dictionary<string, object> Parameters
    {
        get => LeftHand.Parameters;
        set => LeftHand.Parameters = value;
    }

    public TimeFrame TimeFrame
    {
        get => LeftHand.TimeFrame;
        set => LeftHand.TimeFrame = value;
    }

    public int Offset
    {
        get => LeftHand.Offset;
        set => LeftHand.Offset = value;
    }

    public string OutputName
    {
        get => LeftHand.OutputName;
        set => LeftHand.OutputName = value;
    }

    /// <summary>
    /// Display text representing the entire comparison condition.
    /// </summary>
    public string DisplayName
    {
        get
        {
            string leftStr = LeftHand.DisplayName;
            string opStr = Operator.ToSymbolString();
            if (TargetMode == RightHandTargetMode.NumericValue)
            {
                return $"{leftStr} {opStr} {RightNumericValue}";
            }
            if (TargetMode == RightHandTargetMode.StringValue)
            {
                return $"{leftStr} {opStr} \"{RightStringValue}\"";
            }
            return $"{leftStr} {opStr} {RightHand.DisplayName}";
        }
    }
}
