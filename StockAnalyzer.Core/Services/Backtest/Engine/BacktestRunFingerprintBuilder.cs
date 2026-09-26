using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Serialization;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Builds the versioned canonical <see cref="BacktestRunFingerprint"/> (owner decision G5, A1 (Run fingerprint)):
/// <c>H = SHA256(Encode(domain tag, schema version, execution-semantics version, frozen input, frozen configuration, strategy manifest,
/// ordered full output, run status, diagnostic))</c>. Two steps so the identity of the INPUT is frozen before the run: <see cref="Freeze"/> copies
/// everything the run will read (bars, the foreign-frame map, configuration, the strategy's own settings) into bytes, and <see cref="Seal"/> adds the
/// output once it exists. The hash fields themselves are excluded; the legacy <c>ReproducibilityHash</c> is untouched.
/// Enums are encoded as their int value, except the catalog enums <see cref="IndicatorType"/> and <see cref="PriceType"/> (extended in the middle over
/// time), which are encoded by name.
/// </summary>
public sealed class BacktestRunFingerprintBuilder
{
    /// <summary>Version of the canonical byte layout below; bump on any layout change.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Version of the strategy-manifest layout of the supported strategies.</summary>
    private const int StrategyManifestVersion = 2;

    private const string DomainTag = "StockAnalyzer.Core.Backtest.RunFingerprint";
    private const string YFinanceApproximateDomainTag = "StockAnalyzer.Core.Backtest.YFinanceApproximate.RunFingerprint";
    private const string ConditionBasedStrategyType = "ConditionBased";
    private const string NoOpStrategyType = "NoOp";

    private static readonly JsonSerializerOptions ParameterJsonOptions = new() { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };

    private readonly byte[]? _frozenPrefix;
    private readonly string? _unavailableReason;
    private readonly byte[] _frozenConfiguration;
    private readonly string _strategyName;
    private readonly ExecutionModel _executionModel;

    private BacktestRunFingerprintBuilder(byte[]? frozenPrefix, string? unavailableReason, byte[] frozenConfiguration, string strategyName, ExecutionModel executionModel)
    {
        _frozenPrefix = frozenPrefix;
        _unavailableReason = unavailableReason;
        _frozenConfiguration = frozenConfiguration;
        _strategyName = strategyName;
        _executionModel = executionModel;
    }

    /// <summary>
    /// Freezes the identity of the input, configuration and strategy by encoding them NOW. A strategy that is not a built-in with a known manifest
    /// (<see cref="ConditionBasedBacktestStrategy"/>, <see cref="NoOpBacktestStrategy"/>) cannot state its settings, so the result is an unavailable
    /// fingerprint with an explicit reason - its <see cref="IBacktestStrategy.Name"/> is never substituted and called complete.
    /// </summary>
    public static BacktestRunFingerprintBuilder Freeze(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(strategy);

        using var writer = new CanonicalWriter();
        writer.String(configuration.ExecutionModel == ExecutionModel.YFinanceApproximate
            ? YFinanceApproximateDomainTag
            : DomainTag);
        writer.Int32(SchemaVersion);
        writer.Int32(BacktestExecutionSemantics.Version);
        WriteInput(writer, input);
        WriteConfiguration(writer, configuration);

        // Kept beside the prefix so Seal can prove that a result was produced under this exact configuration and strategy.
        byte[] frozenConfiguration = EncodeConfiguration(configuration);
        string strategyName = strategy.Name;

        try
        {
            if (!TryWriteStrategyManifest(writer, strategy, out string? reason))
            {
                return new BacktestRunFingerprintBuilder(null, reason, frozenConfiguration, strategyName, configuration.ExecutionModel);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or JsonException)
        {
            return new BacktestRunFingerprintBuilder(null, $"the strategy manifest contains a value that has no canonical encoding: {ex.Message}", frozenConfiguration, strategyName, configuration.ExecutionModel);
        }

        return new BacktestRunFingerprintBuilder(writer.ToArray(), null, frozenConfiguration, strategyName, configuration.ExecutionModel);
    }

    /// <summary>
    /// Adds the full ordered output, status and diagnostic to the frozen identity and hashes the whole preimage.
    /// Throws <see cref="ArgumentException"/> when <paramref name="result"/> was not produced under the frozen configuration and strategy.
    /// </summary>
    public BacktestRunFingerprint Seal(BacktestResult result, BacktestRunDiagnostic? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureResultBelongsToFrozenRun(result);
        if (_frozenPrefix is not { } prefix)
        {
            // Invariant: a builder without a frozen prefix always carries the reason it is unavailable.
            return BacktestRunFingerprint.Unavailable(_unavailableReason ?? throw new InvalidOperationException("An unavailable fingerprint builder must state its reason."));
        }

        byte[] hash = SHA256.HashData(Encode(prefix, result, diagnostic));
        return new BacktestRunFingerprint(ImmutableArray.Create(hash), SchemaVersion, BacktestExecutionSemantics.Version);
    }

    /// <summary>The exact bytes that are hashed (tests compare preimages, not only digests). Null when the fingerprint is unavailable.</summary>
    internal byte[]? EncodePreimage(BacktestResult result, BacktestRunDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureResultBelongsToFrozenRun(result);
        return _frozenPrefix is { } prefix ? Encode(prefix, result, diagnostic) : null;
    }

    /// <summary>
    /// The frozen input is committed before the run, so a result of another run must not be able to borrow it. The configuration is compared by its canonical
    /// bytes (an equal copy is accepted); the strategy by name. The zero-bar path (<see cref="BacktestResult.CreateEmpty"/>) records no strategy name, so
    /// there an empty name is the only accepted alternative.
    /// </summary>
    private void EnsureResultBelongsToFrozenRun(BacktestResult result)
    {
        if (result.Configuration.ExecutionModel != _executionModel)
        {
            throw new ArgumentException("The result execution model differs from the frozen run.", nameof(result));
        }
        if (!EncodeConfiguration(result.Configuration).AsSpan().SequenceEqual(_frozenConfiguration))
        {
            throw new ArgumentException("The result was produced under a different configuration than the one that was frozen.", nameof(result));
        }

        bool sameStrategy = string.Equals(result.StrategyName, _strategyName, StringComparison.Ordinal)
            || (result.IsInsufficientData && result.StrategyName.Length == 0);
        if (!sameStrategy)
        {
            throw new ArgumentException($"The result was produced by strategy '{result.StrategyName}', not by the frozen strategy '{_strategyName}'.", nameof(result));
        }
    }

    private static byte[] EncodeConfiguration(BacktestConfiguration configuration)
    {
        using var writer = new CanonicalWriter();
        WriteConfiguration(writer, configuration);
        return writer.ToArray();
    }

    private static byte[] Encode(byte[] prefix, BacktestResult result, BacktestRunDiagnostic? diagnostic)
    {
        using var writer = new CanonicalWriter();
        writer.Bytes(prefix);
        WriteOutput(writer, result, diagnostic);
        return writer.ToArray();
    }

    private static void WriteInput(CanonicalWriter writer, BacktestInput input)
    {
        writer.String(input.Symbol);
        writer.Enum(input.Frame);
        writer.Int32(input.DataVersion);
        writer.Timestamp(input.EvaluationStartUtc);
        writer.Timestamp(input.EvaluationEndUtc);
        writer.Int32(input.HistoryStartIndex);
        writer.Int32(input.TradingStartIndex);
        WriteBars(writer, input.Bars);

        if (input.AdditionalTimeframeBars is null)
        {
            writer.Bool(false);
            return;
        }

        writer.Bool(true);
        KeyValuePair<TimeFrame, ImmutableArray<CandleData>>[] frames = input.AdditionalTimeframeBars.OrderBy(pair => (int)pair.Key).ToArray();
        writer.Count(frames.Length);
        foreach (KeyValuePair<TimeFrame, ImmutableArray<CandleData>> frame in frames)
        {
            writer.Enum(frame.Key);
            WriteBars(writer, frame.Value);
        }
    }

    private static void WriteBars(CanonicalWriter writer, ImmutableArray<CandleData> bars)
    {
        writer.Count(bars.Length);
        foreach (CandleData bar in bars)
        {
            writer.Timestamp(bar.Timestamp);
            writer.Decimal(bar.Open);
            writer.Decimal(bar.High);
            writer.Decimal(bar.Low);
            writer.Decimal(bar.Close);
            writer.Int64(bar.Volume);
        }
    }

    private static void WriteConfiguration(CanonicalWriter writer, BacktestConfiguration configuration)
    {
        writer.Decimal(configuration.InitialCapital);
        writer.Decimal(configuration.CommissionFlat);
        writer.Decimal(configuration.CommissionPerUnit);
        writer.Decimal(configuration.SlippageRatio);
        writer.Int32(configuration.TradingDaysPerYear);
        writer.Enum(configuration.SizingModel);
        writer.Decimal(configuration.SizingParameter);
        writer.Decimal(configuration.InitialMarginRatio);
        writer.Decimal(configuration.MaintenanceMarginRatio);
        writer.Decimal(configuration.LiquidationPenaltyRatio);
    }

    private static bool TryWriteStrategyManifest(CanonicalWriter writer, IBacktestStrategy strategy, out string? unavailableReason)
    {
        unavailableReason = null;
        switch (strategy)
        {
            case ConditionBasedBacktestStrategy conditionBased:
                writer.String(ConditionBasedStrategyType);
                writer.Int32(StrategyManifestVersion);
                writer.Count(conditionBased.Entries.Count);
                foreach (BacktestConditionEntry entry in conditionBased.Entries)
                {
                    WriteConditionEntry(writer, entry);
                }
                writer.Bool(conditionBased.RiskManagement is not null);
                if (conditionBased.RiskManagement is { } risk)
                {
                    writer.NullableDecimal(risk.StopLossPercent);
                    writer.NullableDecimal(risk.TakeProfitPercent);
                }
                return true;

            case NoOpBacktestStrategy noOp:
                // The requested indicators are the whole of its configuration; read as data, never through the strategy call the engine makes once per run.
                IReadOnlyList<StrategyIndicatorRequest> requests = noOp.RequiredIndicators;
                writer.String(NoOpStrategyType);
                writer.Int32(StrategyManifestVersion);
                writer.Count(requests.Count);
                foreach (StrategyIndicatorRequest request in requests)
                {
                    writer.String(request.Key);
                    writer.String(request.Type.ToString());
                    WriteParameters(writer, request.Parameters);
                    writer.NullableEnum(request.Frame);
                    writer.String(request.OutputName);
                    writer.String(request.PriceSource?.ToString());
                    writer.Bool(request.StrictOutputName);
                }
                return true;

            default:
                unavailableReason = $"strategy type '{strategy.GetType().FullName}' does not provide a stable manifest of its settings, so a complete fingerprint is not possible";
                return false;
        }
    }

    private static void WriteConditionEntry(CanonicalWriter writer, BacktestConditionEntry entry)
    {
        WriteConditionSide(writer, entry.Left);
        writer.Enum(entry.Operator);
        writer.Enum(entry.TargetMode);
        writer.Decimal(entry.RightNumericValue);
        writer.Bool(entry.Right is not null);
        if (entry.Right is not null) WriteConditionSide(writer, entry.Right);
        writer.Enum(entry.LogicalOperator);
        writer.Enum(entry.Role);
        writer.Enum(entry.Position);
    }

    private static void WriteConditionSide(CanonicalWriter writer, BacktestConditionSide side)
    {
        writer.String(side.IndicatorType.ToString());
        WriteParameters(writer, side.Parameters);
        writer.String(side.OutputName);
        writer.Int32(side.Offset);
        writer.NullableEnum(side.Frame);
        writer.String(side.PriceSource?.ToString());
    }

    /// <summary>Concrete parameter values with an explicit type discriminator ("$type"), properties in ordinal order so the bytes do not depend on declaration order.</summary>
    private static void WriteParameters(CanonicalWriter writer, CoreIndicatorParameterBase? parameters)
    {
        if (parameters is null)
        {
            writer.String(null);
            return;
        }

        string json = JsonSerializer.Serialize(parameters, typeof(CoreIndicatorParameterBase), ParameterJsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            WriteCanonicalJson(document.RootElement, jsonWriter);
        }
        writer.String(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray()) WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new NotSupportedException($"JSON value kind '{element.ValueKind}' has no canonical encoding.");
        }
    }

    private static void WriteOutput(CanonicalWriter writer, BacktestResult result, BacktestRunDiagnostic? diagnostic)
    {
        writer.String(result.StrategyName);
        writer.Enum(result.Status);
        writer.Bool(result.IsInsufficientData);

        writer.Count(result.Orders.Length);
        foreach (BacktestOrder order in result.Orders)
        {
            writer.Int64(order.OrderId);
            writer.Enum(order.Side);
            writer.Enum(order.Type);
            writer.Decimal(order.Quantity);
            writer.NullableDecimal(order.LimitPrice);
            writer.NullableDecimal(order.StopPrice);
            writer.Enum(order.Status);
            writer.Bool(order.TimeInForce.IsGoodTilCancelled);
            writer.Int32(order.TimeInForce.ExpiryBar);
            writer.Int32(order.SubmittedBar);
            writer.Int32(order.EarliestFillBar);
            writer.Bool(order.StopActivated);
            writer.NullableEnum(order.ExpiredReason);
            writer.NullableEnum(order.RejectedReason);
        }

        writer.Count(result.Fills.Length);
        foreach (BacktestFill fill in result.Fills)
        {
            writer.Int64(fill.FillId);
            writer.Int64(fill.OrderId);
            writer.Int32(fill.BarIndex);
            writer.Timestamp(fill.FillTime);
            writer.Enum(fill.Side);
            writer.Decimal(fill.Price);
            writer.Decimal(fill.Quantity);
            writer.Decimal(fill.Commission);
            writer.Decimal(fill.SlippageAmount);
        }

        writer.Count(result.Trades.Length);
        foreach (BacktestTrade trade in result.Trades)
        {
            writer.Int64(trade.TradeId);
            writer.Enum(trade.Side);
            writer.Int32(trade.EntryBar);
            writer.Timestamp(trade.EntryTime);
            writer.Decimal(trade.EntryPrice);
            writer.Int32(trade.ExitBar);
            writer.Timestamp(trade.ExitTime);
            writer.Decimal(trade.ExitPrice);
            writer.Decimal(trade.Quantity);
            writer.Decimal(trade.ClosedGross);
            writer.Decimal(trade.ClosedNet);
            writer.Decimal(trade.EntryFee);
            writer.Decimal(trade.ExitFee);
            writer.Int32(trade.HoldingBars);
            writer.Bool(trade.IsForcedLiquidation);
        }

        writer.Count(result.EquityPoints.Length);
        foreach (EquityPoint point in result.EquityPoints)
        {
            writer.Int32(point.BarIndex);
            writer.Timestamp(point.Timestamp);
            writer.Decimal(point.Equity);
            writer.Decimal(point.Cash);
            writer.Decimal(point.MarketValue);
            writer.Decimal(point.HeldMargin);
        }

        writer.Count(result.Signals.Length);
        foreach (BacktestSignal signal in result.Signals)
        {
            writer.Enum(signal.Type);
            writer.Int32(signal.BarIndex);
            writer.Timestamp(signal.SignalTime);
            writer.String(signal.Reason);
        }

        // The diagnostic's Message is a localizable display string, so it is not part of the identity; its structured fields are.
        writer.Bool(diagnostic is not null);
        if (diagnostic is not null)
        {
            writer.Enum(diagnostic.Code);
            writer.Enum(diagnostic.Phase);
            writer.NullableInt32(diagnostic.BarIndex);
            writer.Enum(diagnostic.Operation);
        }
    }
}
