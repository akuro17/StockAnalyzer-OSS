using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_find_support_resistance: identifies mathematical support and resistance price levels,
/// hit counts, strength scores, and active boundary levels using Singular Spectrum Analysis (SSA).
/// </summary>
internal sealed class FindSupportResistanceTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string ModeArgument = "mode";
    private const string EmbeddingDimensionArgument = "embeddingDimension";
    private const string NumComponentsArgument = "numComponents";
    private const string AutoRankArgument = "autoRank";
    private const string MaxLevelsArgument = "maxLevels";
    private const string ClusterToleranceArgument = "clusterTolerance";
    private const string MultiplierArgument = "multiplier";

    private static readonly string[] SupportedModes = Enum.GetNames<SsaSupportResistanceMode>();

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public FindSupportResistanceTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_find_support_resistance";

    public string Description =>
        "Extracts support and resistance price levels, hit counts, and strength scores using Singular Spectrum Analysis (SSA). " +
        "Computes active nearest support/resistance levels and distance ratios relative to the current price. Read-only and causal.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"mode\":\"StructuralPivots\",\"maxLevels\":10}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(ModeArgument, $"SSA calculation mode: {string.Join(", ", SupportedModes)}. Default StructuralPivots.", allowedValues: SupportedModes)
        .Integer(EmbeddingDimensionArgument, $"SSA embedding dimension / lag window length (>= 2). Default {IndicatorDefaultConstants.SsaAnomalyDefaultEmbeddingDimension}.", defaultValue: IndicatorDefaultConstants.SsaAnomalyDefaultEmbeddingDimension, minimum: 2)
        .Integer(NumComponentsArgument, $"Number of reconstructed SSA components when autoRank is false (>= 1). Default {IndicatorDefaultConstants.SsaAnomalyDefaultNumComponents}.", defaultValue: IndicatorDefaultConstants.SsaAnomalyDefaultNumComponents, minimum: 1)
        .Boolean(AutoRankArgument, "Automatically estimate signal rank via cumulative energy threshold. Default true.")
        .Integer(MaxLevelsArgument, $"Maximum total support and resistance levels to return (1..{_limits.MaxSupportResistanceLevels}). Default {_limits.MaxSupportResistanceLevels}.", defaultValue: _limits.MaxSupportResistanceLevels, minimum: 1, maximum: _limits.MaxSupportResistanceLevels)
        .Number(ClusterToleranceArgument, "Clustering price tolerance for grouping extrema. Default 0.5.")
        .Number(MultiplierArgument, $"Volatility multiplier for DynamicEnvelopes mode. Default {IndicatorDefaultConstants.SsaResidualBandDefaultMultiplier}.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> symbolWarnings = !resolved.IsRegistered
            ? new[] { SymbolResolver.OffListWarning(new[] { resolved.Symbol }) }
            : Array.Empty<string>();

        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);

        string rawMode = ToolArgs.String(arguments, ModeArgument) ?? nameof(SsaSupportResistanceMode.StructuralPivots);
        if (!Enum.TryParse(rawMode, ignoreCase: true, out SsaSupportResistanceMode ssaMode))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ModeArgument} must be one of: {string.Join(", ", SupportedModes)}.", ModeArgument);
        }

        int embeddingDimension;
        if (ToolArgs.Integer(arguments, EmbeddingDimensionArgument) is { } rawEmbedding)
        {
            if (rawEmbedding < 2)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{EmbeddingDimensionArgument} must be >= 2.", EmbeddingDimensionArgument);
            }
            if (rawEmbedding > _limits.MaxCandlesPerRequest)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, $"{EmbeddingDimensionArgument} cannot exceed {_limits.MaxCandlesPerRequest}.", EmbeddingDimensionArgument);
            }
            embeddingDimension = (int)rawEmbedding;
        }
        else
        {
            embeddingDimension = IndicatorDefaultConstants.SsaAnomalyDefaultEmbeddingDimension;
        }

        int numComponents;
        if (ToolArgs.Integer(arguments, NumComponentsArgument) is { } rawComponents)
        {
            if (rawComponents < 1)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{NumComponentsArgument} must be >= 1.", NumComponentsArgument);
            }
            if (rawComponents > _limits.MaxCandlesPerRequest)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, $"{NumComponentsArgument} cannot exceed {_limits.MaxCandlesPerRequest}.", NumComponentsArgument);
            }
            numComponents = (int)rawComponents;
        }
        else
        {
            numComponents = IndicatorDefaultConstants.SsaAnomalyDefaultNumComponents;
        }

        bool autoRank = ToolArgs.Boolean(arguments, AutoRankArgument) ?? true;

        int maxLevels = ToolArgs.PositiveWithin(arguments, MaxLevelsArgument, _limits.MaxSupportResistanceLevels, _limits.MaxSupportResistanceLevels);
        if (maxLevels < 1)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{MaxLevelsArgument} must be between 1 and {_limits.MaxSupportResistanceLevels}.", MaxLevelsArgument);
        }

        double? rawCluster = ToolArgs.Number(arguments, ClusterToleranceArgument);
        if (rawCluster.HasValue && (rawCluster.Value <= 0 || rawCluster.Value > 1000000.0 || double.IsNaN(rawCluster.Value) || double.IsInfinity(rawCluster.Value)))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ClusterToleranceArgument} must be > 0 and <= 1000000.0.", ClusterToleranceArgument);
        }
        decimal clusterTolerance = rawCluster.HasValue ? (decimal)rawCluster.Value : 0.5m;

        double? rawMultiplier = ToolArgs.Number(arguments, MultiplierArgument);
        if (rawMultiplier.HasValue && (rawMultiplier.Value <= 0 || rawMultiplier.Value > 1000000.0 || double.IsNaN(rawMultiplier.Value) || double.IsInfinity(rawMultiplier.Value)))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{MultiplierArgument} must be > 0 and <= 1000000.0.", MultiplierArgument);
        }
        decimal multiplier = rawMultiplier.HasValue ? (decimal)rawMultiplier.Value : (decimal)IndicatorDefaultConstants.SsaResidualBandDefaultMultiplier;

        List<CandleData> loaded;
        if (endDate is not null)
        {
            loaded = await _loader.LoadUpToAsync(symbol, timeFrame, endDate.Value, lookback, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                var check = await _gateway.GetCandlesAsync(symbol, timeFrame, 1, cancellationToken).ConfigureAwait(false);
                if (check.Count == 0)
                {
                    throw new ToolException(ToolErrorCodes.SymbolNotFound, $"No stored market data exists for {symbol}.", SymbolArgument);
                }

                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist on or before {endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} for {symbol}.", EndDateArgument);
            }
        }
        else
        {
            (loaded, _) = await _loader.LoadAsync(symbol, timeFrame, lookback, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
            }
        }

        if (loaded.Count < SsaSupportResistanceEngine.MinSampleCount)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"At least {SsaSupportResistanceEngine.MinSampleCount} bars are required for SSA support and resistance analysis.", SymbolArgument);
        }

        double[] samples = loaded.Select(c => (double)c.Close).ToArray();
        DateTime[] timestamps = loaded.Select(c => c.Timestamp).ToArray();
        decimal currentPrice = loaded[^1].Close;
        double currentPriceDbl = (double)currentPrice;

        var result = SsaSupportResistanceEngine.Calculate(
            samples,
            timestamps,
            mode: ssaMode,
            embeddingDimension: embeddingDimension,
            numComponents: numComponents,
            autoRank: autoRank,
            detrendMode: SsaDetrendMode.LeastSquaresLinear,
            maxLevelsPerSide: Math.Max(2, maxLevels + 1),
            clusterTolerance: clusterTolerance,
            multiplier: multiplier,
            currentPrice: currentPriceDbl);

        var allLevels = new List<SsaSupportResistanceLevel>();
        if (result.ResistanceLevels != null) allLevels.AddRange(result.ResistanceLevels);
        if (result.SupportLevels != null) allLevels.AddRange(result.SupportLevels);

        // Sort: strengthScore descending, then price descending
        var sortedLevels = allLevels
            .OrderByDescending(l => l.StrengthScore)
            .ThenByDescending(l => l.Price)
            .ToList();

        bool truncated = sortedLevels.Count > maxLevels;
        var returnedLevels = truncated ? sortedLevels.Take(maxLevels).ToList() : sortedLevels;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            if (endDate is not null)
            {
                writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            }
            writer.WriteNumber("lookbackBars", lookback);
            writer.WriteNumber("barCount", loaded.Count);
            writer.WriteString("mode", ssaMode.ToString());
            writer.WriteNumber("currentPrice", currentPrice);

            if (result.ActiveSupport.HasValue)
            {
                writer.WriteNumber("activeSupport", (decimal)result.ActiveSupport.Value);
            }
            else
            {
                writer.WriteNull("activeSupport");
            }

            if (result.ActiveResistance.HasValue)
            {
                writer.WriteNumber("activeResistance", (decimal)result.ActiveResistance.Value);
            }
            else
            {
                writer.WriteNull("activeResistance");
            }

            writer.WriteBoolean("truncated", truncated);

            writer.WriteStartArray("levels");
            foreach (var lvl in returnedLevels)
            {
                decimal price = (decimal)lvl.Price;
                decimal distanceRatio = currentPrice != 0m ? (price - currentPrice) / currentPrice : 0m;
                string latestDate = "";
                if (lvl.LatestIndex >= 0 && lvl.LatestIndex < loaded.Count)
                {
                    latestDate = loaded[lvl.LatestIndex].Timestamp.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture);
                }
                else if (lvl.TargetTime.HasValue)
                {
                    latestDate = lvl.TargetTime.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture);
                }

                writer.WriteStartObject();
                writer.WriteNumber("price", price);
                writer.WriteString("kind", lvl.IsResistance ? "Resistance" : "Support");
                writer.WriteNumber("hits", lvl.Hits);
                writer.WriteNumber("strengthScore", lvl.StrengthScore);
                writer.WriteString("latestDate", latestDate);
                writer.WriteNumber("distanceToLevelRatio", distanceRatio);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (symbolWarnings.Count > 0)
            {
                writer.WriteStartArray("warnings");
                foreach (string w in symbolWarnings)
                {
                    writer.WriteStringValue(w);
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }
}
