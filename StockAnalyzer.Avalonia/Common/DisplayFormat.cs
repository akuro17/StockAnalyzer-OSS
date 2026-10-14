using System;
using System.Globalization;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// The single definition of how the Data tab turns a price, a volume, a change, a date or a comparison value into text: the format strings (the
/// <c>Data*</c> constants of <see cref="ChartConstants"/>) and the culture. The culture is <see cref="CultureInfo.InvariantCulture"/>, so the text does not depend
/// on the culture of the machine or of the test host (SA_ARCHITECTURE_RULES, Culture Invariance); the application sets en-US at startup, which looks the same.
/// </summary>
public static class DisplayFormat
{
    /// <summary>The culture every Data tab text is formatted with.</summary>
    public static CultureInfo Culture => CultureInfo.InvariantCulture;

    /// <summary>Open, High, Low, Close, an indicator value, a reversal or update price.</summary>
    public static string Price(decimal value) => value.ToString(ChartConstants.DataPriceFormat, Culture);

    /// <summary>Volume of a candle.</summary>
    public static string Volume(long value) => value.ToString(ChartConstants.DataVolumeFormat, Culture);

    /// <summary>Volume or average volume of a Reverse Watch point.</summary>
    public static string Volume(decimal value) => value.ToString(ChartConstants.DataVolumeFormat, Culture);

    /// <summary>Date of an element without a time of day.</summary>
    public static string Date(DateTime value) => value.ToString(ChartConstants.DataDateFormat, Culture);

    /// <summary>Date and time of an intraday element.</summary>
    public static string DateAndTime(DateTime value) => value.ToString(ChartConstants.DataDateTimeFormat, Culture);

    /// <summary>The element date: the time of day is shown only when it is not midnight.</summary>
    public static string Timestamp(DateTime value) =>
        value.Hour == 0 && value.Minute == 0 ? Date(value) : DateAndTime(value);

    /// <summary>The difference against the previous close; a positive value carries a plus sign.</summary>
    public static string ChangeAmount(decimal difference) =>
        (difference > 0 ? ChartConstants.DataPositiveSign : string.Empty) + Price(difference);

    /// <summary>The difference against the previous close in percent; a positive value carries a plus sign.</summary>
    public static string ChangeRatio(decimal percent) =>
        (percent > 0 ? ChartConstants.DataPositiveSign : string.Empty) + percent.ToString(ChartConstants.DataChangeRatioFormat, Culture) + ChartConstants.DataPercentSuffix;

    /// <summary>Absolute close of a comparison symbol.</summary>
    public static string ComparisonPrice(decimal value) => value.ToString(ChartConstants.DataComparisonPriceFormat, Culture);

    /// <summary>Fixed two decimals (a comparison value of an unknown mode).</summary>
    private static string FixedTwoDecimals(decimal value) => value.ToString(ChartConstants.DataFixedTwoDecimalsFormat, Culture);

    /// <summary>A count of elements.</summary>
    public static string Count(int value) => value.ToString(Culture);

    /// <summary>A number of calendar days.</summary>
    public static string Days(int value) => value.ToString(Culture);

    /// <summary>The value of a comparison symbol in the unit of the comparison mode.</summary>
    public static string ComparisonValue(ComparisonMode mode, decimal value) => mode switch
    {
        ComparisonMode.Performance => value.ToString(ChartConstants.DefaultRelativePerformanceFormat, Culture) + ChartConstants.DefaultRelativePerformanceSuffix,
        ComparisonMode.Ratio => value.ToString(ChartConstants.DefaultRatioFormat, Culture),
        ComparisonMode.ZScore => ChartConstants.ZScorePrefix + value.ToString(ChartConstants.DefaultZScoreFormat, Culture),
        ComparisonMode.Spread => value.ToString(ChartConstants.DefaultSpreadFormat, Culture),
        _ => FixedTwoDecimals(value)
    };
}
