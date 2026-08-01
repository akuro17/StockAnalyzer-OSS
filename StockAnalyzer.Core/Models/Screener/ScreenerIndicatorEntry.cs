using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Represents a registered screening condition entry.
/// Can compare a Left-hand Indicator against either a scalar Numeric Value
/// or a Right-hand Indicator, each with independent TimeFrames and Offsets.
/// </summary>
public partial class ScreenerIndicatorEntry : ObservableObject, IScreeningCondition
{
    public string Name => DisplayName;

    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        return IsMet(candles, default);
    }

    public bool IsMet(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
    {
        if (candles == null || candles.Count == 0) return false;

        // Column fundamental metrics: If metadata is null (data missing), strictly exclude from screening results
        if (LeftHand?.CategoryType == ScreenerItemCategoryType.Column)
        {
            decimal? leftValNullable = ScreenerValueExtractor.Default.ExtractValueNullable(LeftHand, candles, metadata);
            if (!leftValNullable.HasValue)
            {
                return false;
            }

            decimal leftValue = leftValNullable.Value;
            decimal rightValue = TargetMode == RightHandTargetMode.NumericValue
                ? RightNumericValue
                : ScreenerValueExtractor.Default.ExtractValue(RightHand, candles, metadata);

            return Operator switch
            {
                ComparisonOperator.GreaterThan => leftValue > rightValue,
                ComparisonOperator.LessThan => leftValue < rightValue,
                ComparisonOperator.GreaterThanOrEqual => leftValue >= rightValue,
                ComparisonOperator.LessThanOrEqual => leftValue <= rightValue,
                ComparisonOperator.Equal => leftValue == rightValue,
                ComparisonOperator.NotEqual => leftValue != rightValue,
                _ => leftValue > rightValue
            };
        }

        decimal leftVal = ScreenerValueExtractor.Default.ExtractValue(LeftHand, candles, metadata);
        decimal rightVal = TargetMode == RightHandTargetMode.NumericValue
            ? RightNumericValue
            : ScreenerValueExtractor.Default.ExtractValue(RightHand, candles, metadata);

        return Operator switch
        {
            ComparisonOperator.GreaterThan => leftVal > rightVal,
            ComparisonOperator.LessThan => leftVal < rightVal,
            ComparisonOperator.GreaterThanOrEqual => leftVal >= rightVal,
            ComparisonOperator.LessThanOrEqual => leftVal <= rightVal,
            _ => leftVal > rightVal
        };
    }

    public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles)
    {
        return ValueTask.FromResult(IsMet(candles));
    }

    public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, Portfolio.TickerMetadata metadata)
    {
        return ValueTask.FromResult(IsMet(candles, metadata));
    }

    /// <summary>
    /// Uppercase identifier label (A, B, C...).
    /// </summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>
    /// Logical operator combining this condition with the next condition (And ∩ / Or ∪).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogicalOperatorSymbol))]
    private LogicalOperator _logicalOperator = LogicalOperator.And;

    /// <summary>
    /// Symbol representation of the logical operator ("∩" for AND, "∪" for OR).
    /// </summary>
    public string LogicalOperatorSymbol => LogicalOperator == LogicalOperator.Or ? "∪" : "∩";

    /// <summary>
    /// Left-hand indicator configuration (Source under comparison).
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
    /// Right-hand indicator configuration (Target under comparison) when TargetMode is Indicator.
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
    [ObservableProperty]
    private bool _isEnabled = true;

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
