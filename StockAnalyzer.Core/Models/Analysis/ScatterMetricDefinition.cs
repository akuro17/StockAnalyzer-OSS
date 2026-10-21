using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// One user-registered scatter metric: the thing plotted on the X or Y axis. The value comes from the existing Screener value
/// extraction, so <see cref="Source"/> is the same single-side configuration a Screener condition holds (indicator, output series,
/// parameters, time frame, Offset in bars of that time frame before the latest bar of the ticker's own series; or a Column).
/// </summary>
public sealed class ScatterMetricDefinition
{
    private const char KeySeparator = '|';
    private const string TimeFrameLabelPrefix = " [";
    private const string OffsetLabelPrefix = " [-";
    private const string OffsetLabelSuffix = "]";
    private const string RoundTripFormat = "R";

    // 29 significant digits is the precision of a decimal; this format also drops trailing zeros, so 2.5 and 2.50 are one value.
    private const string DecimalFormat = "G29";

    /// <summary>Identity of the entry; assigned once and never changed, so a selection survives an edit of the entry.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The metric definition. Owned by this instance; callers that keep it use <see cref="Clone"/>.</summary>
    public ScreenerIndicatorSideConfig Source { get; set; } = new();

    /// <summary>
    /// Display text of the axis, the combo boxes and the Active list: the Screener column title (name and parameters),
    /// followed by <c> [W1]</c> or <c> [MN1]</c> when the time frame is not Day and the title does not name it already (an Indicator title does; a
    /// Column or Price title, which has a custom display name, does not), and by <c> [-n]</c> when <see cref="ScreenerIndicatorSideConfig.Offset"/> n
    /// (in bars of the metric's time frame) is above 0. A Day label is unchanged.
    /// </summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            string title = Source.GetColumnHeaderTitle();
            if (!string.IsNullOrWhiteSpace(Source.CustomDisplayName) && Source.TimeFrame != TimeFrame.D1)
            {
                title = string.Concat(title, TimeFrameLabelPrefix, Source.TimeFrame.ToString(), OffsetLabelSuffix);
            }

            return Source.Offset > 0
                ? string.Concat(title, OffsetLabelPrefix, Source.Offset.ToString(CultureInfo.InvariantCulture), OffsetLabelSuffix)
                : title;
        }
    }

    /// <summary>
    /// Identity of the CONTENT (without <see cref="Id"/>): category, indicator type, names, output, Offset, time frame, then the parameters
    /// ordered by key (ordinal). Parameter values are normalised so the key is the same before and after a JSON round trip
    /// (a number is its invariant round-trip text, a bool is lower case, an enum is its number).
    /// </summary>
    [JsonIgnore]
    public string ContentKey
    {
        get
        {
            var key = new StringBuilder();
            key.Append(Source.CategoryType).Append(KeySeparator)
               .Append(Source.IndicatorType).Append(KeySeparator)
               .Append(Source.CustomDisplayName).Append(KeySeparator)
               .Append(Source.OutputName).Append(KeySeparator)
               .Append(Source.Offset.ToString(CultureInfo.InvariantCulture)).Append(KeySeparator)
               .Append(Source.TimeFrame);

            if (Source.Parameters != null)
            {
                foreach (KeyValuePair<string, object> parameter in Source.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    key.Append(KeySeparator).Append(parameter.Key).Append('=').Append(NormalizeValue(parameter.Value));
                }
            }

            return key.ToString();
        }
    }

    /// <summary>A deep copy: same <see cref="Id"/>, an independent <see cref="Source"/>.</summary>
    public ScatterMetricDefinition Clone() => new() { Id = Id, Source = Source.Clone() };

    private static string NormalizeValue(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number => element.TryGetInt64(out long whole)
                        ? whole.ToString(CultureInfo.InvariantCulture)
                        : element.TryGetDouble(out double number)
                            ? number.ToString(RoundTripFormat, CultureInfo.InvariantCulture)
                            : element.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    _ => element.GetRawText(),
                };
            case bool flag:
                return flag ? "true" : "false";
            case Enum enumValue:
                return Convert.ToString(Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) ?? string.Empty;
            case string text:
                return text;
            // Integers and decimals keep their own digits: through a double, two values beyond 2^53 (or 17 significant digits) would share a key and an edit would count as no change.
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            case decimal number:
                return number.ToString(DecimalFormat, CultureInfo.InvariantCulture);
            case float or double:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(RoundTripFormat, CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
