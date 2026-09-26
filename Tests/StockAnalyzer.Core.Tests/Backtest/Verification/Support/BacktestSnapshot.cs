using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Plain projection of a <see cref="BacktestResult"/> serialized into a canonical JSON text: every field of every order, fill,
/// trade, equity point and signal, plus the configuration and the reproducibility hash. The engine's own record types are
/// serialized directly on purpose - a newly added engine field then shows up as a golden diff instead of being silently ignored.
/// Decimals are written with trailing zeros stripped, so a pure scale change (108.9 vs 108.90) is not a diff but any VALUE
/// change (even 1 minimal unit) is.
/// </summary>
internal sealed record BacktestSnapshot(
    RunStatus Status,
    string StrategyName,
    bool IsInsufficientData,
    string ReproducibilityHashHex,
    BacktestConfiguration Configuration,
    ImmutableArray<BacktestOrder> Orders,
    ImmutableArray<BacktestFill> Fills,
    ImmutableArray<BacktestTrade> Trades,
    ImmutableArray<EquityPoint> EquityPoints,
    ImmutableArray<BacktestSignal> Signals)
{
    /// <summary>Decimal format with up to 28 fractional digits and no trailing zeros.</summary>
    private const string DecimalFormat = "0.############################";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static BacktestSnapshot From(BacktestResult r) => new(
        r.Status, r.StrategyName, r.IsInsufficientData, Convert.ToHexString(r.ReproducibilityHash),
        r.Configuration, r.Orders, r.Fills, r.Trades, r.EquityPoints, r.Signals);

    /// <summary>Canonical, LF-terminated JSON text of the full result.</summary>
    public static string ToCanonicalJson(BacktestResult result)
        => JsonSerializer.Serialize(From(result), Options).Replace("\r\n", "\n") + "\n";

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new NormalizedDecimalConverter());
        return options;
    }

    private sealed class NormalizedDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            string text = value == 0m ? "0" : value.ToString(DecimalFormat, CultureInfo.InvariantCulture);
            writer.WriteRawValue(text);
        }
    }
}
