using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Fixed per-channel z-score of the portfolio observation. It reuses <see cref="FixedScaler"/> for the transform; the sidecar rules differ
/// from the composed-feature scaler of T15/T16 (log-ratio channels, portfolio contract hash), so loading and validation live here.
/// The statistics are fitted once on the training data and frozen into the model artifact; they are never refitted at inference.
/// </summary>
public static class RlPortfolioScaler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static string SidecarName(string modelPath) => Path.GetFileName(modelPath) + RlFiles.ScalerSuffix;

    /// <summary>A scaler of the given observation variant (default v1); the variant is recorded as the contract hash of the sidecar.</summary>
    public static FixedScaler Create(IReadOnlyList<FixedScalerStatistic> statistics, RlPortfolioObservationLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        layout ??= RlPortfolioObservationLayout.V1;
        return new FixedScaler(FixedScaler.CurrentSchemaVersion, layout.ScalerFeatureMode, RlContract.WindowLength,
            Array.Empty<int>(), null, layout.ChannelNames.ToArray(), statistics.ToArray(), layout.ContractHash);
    }

    /// <summary>
    /// Population statistics per channel over every window element of every training decision: the training symbols of each training-cell block,
    /// at each decision index from the first decision to the last. Overlapping windows are counted as often as the policy sees them.
    /// </summary>
    public static FixedScaler Fit(IRlPanelBars bars, RlPanelCatalog catalog, RlPortfolioObservationLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(catalog);
        layout ??= RlPortfolioObservationLayout.V1;
        int channels = layout.MarketChannelCount, window = RlContract.WindowLength;
        var count = new long[channels];
        var mean = new double[channels];
        var m2 = new double[channels];
        var min = Enumerable.Repeat(double.PositiveInfinity, channels).ToArray();
        var max = Enumerable.Repeat(double.NegativeInfinity, channels).ToArray();
        var raw = new double[window * channels];
        void Accumulate(double[] window)
        {
            for (int i = 0; i < window.Length; i++)
            {
                int channel = i % channels;
                double value = window[i];
                if (!double.IsFinite(value)) throw new InvalidDataException("A training feature is not finite.");
                count[channel]++;
                double delta = value - mean[channel];
                mean[channel] += delta / count[channel];
                m2[channel] += delta * (value - mean[channel]);
                if (value < min[channel]) min[channel] = value;
                if (value > max[channel]) max[channel] = value;
            }
        }
        bool crossSectional = channels != RlContract.MarketChannelCount;
        foreach (var block in catalog.CellBlocks(RlPortfolioContract.TrainPartition, RlPanelSymbolGroup.Train))
        {
            var symbols = catalog.EligibleIn(block, RlPanelSymbolGroup.Train);
            if (crossSectional)
            {
                // The cross-sectional channels depend on the whole set of training symbols of a decision, so the loop runs decision by decision.
                var windows = symbols.Select(_ => new double[raw.Length]).ToArray();
                for (int anchor = block.StartIndex + window - 1; anchor < block.EndExclusive - 1; anchor++)
                {
                    var available = RlPortfolioWindowFeatures.Fill(bars, layout, symbols, anchor, block.StartIndex, windows);
                    for (int k = 0; k < windows.Length; k++)
                    {
                        if (!available[k]) throw new InvalidDataException("A training block lacks a bar inside an observation window.");
                        Accumulate(windows[k]);
                    }
                }
                continue;
            }
            foreach (int symbol in symbols)
                for (int anchor = block.StartIndex + window - 1; anchor < block.EndExclusive - 1; anchor++)
                {
                    if (!RlPortfolioObservationBuilder.TryFillRawWindow(bars, symbol, anchor, raw))
                        throw new InvalidDataException("A training block lacks a bar inside an observation window.");
                    Accumulate(raw);
                }
        }
        if (count[0] == 0) throw new InvalidDataException("The scaler needs at least one training decision.");
        return Create(Enumerable.Range(0, channels)
            .Select(c => new FixedScalerStatistic(mean[c], Math.Sqrt(m2[c] / count[c]), min[c], max[c])).ToArray(), layout);
    }

    public static void Save(string modelPath, FixedScaler scaler)
    {
        ArgumentNullException.ThrowIfNull(scaler);
        Validate(scaler);
        AtomicJsonFile.Save(modelPath + RlFiles.ScalerSuffix, scaler, JsonOptions, preserveBackup: true);
    }

    /// <summary>Loads the sidecar the model's <c>scaler_ref</c> names and validates it against the portfolio contract and the model metadata.</summary>
    public static FixedScaler Load(string modelPath, IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.TryGetValue(PredictionModelMetadata.ScalerReferenceKey, out var reference) || reference != SidecarName(modelPath))
            throw new InvalidDataException("ONNX scaler_ref must name its own generation sidecar.");
        var scaler = JsonSerializer.Deserialize<FixedScaler>(File.ReadAllText(modelPath + RlFiles.ScalerSuffix), JsonOptions)
            ?? throw new InvalidDataException("Scaler sidecar is empty.");
        var layout = Validate(scaler);
        if (metadata.GetValueOrDefault(PredictionModelMetadata.WindowSizeKey) != RlContract.WindowLength.ToString(CultureInfo.InvariantCulture)
            || metadata.GetValueOrDefault(RlPortfolioContract.MetadataChannels) != layout.MarketChannelCount.ToString(CultureInfo.InvariantCulture)
            || (metadata.TryGetValue(RlPortfolioContract.MetadataContractHash, out var modelHash) && modelHash != scaler.FeatureContractHash))
            throw new InvalidDataException("Scaler shape disagrees with the ONNX metadata.");
        return scaler;
    }

    /// <summary>Validates a sidecar against the observation variant its own contract hash names, and returns that variant.</summary>
    private static RlPortfolioObservationLayout Validate(FixedScaler scaler)
    {
        if (!RlPortfolioObservationLayout.TryFromContractHash(scaler.FeatureContractHash, out var layout)
            || scaler.SchemaVersion != FixedScaler.CurrentSchemaVersion || scaler.FeatureMode != layout.ScalerFeatureMode
            || scaler.WindowSize != RlContract.WindowLength || scaler.Lags is not { Length: 0 } || scaler.ClipSigma is not null
            || scaler.ChannelIdentities is null || !scaler.ChannelIdentities.SequenceEqual(layout.ChannelNames, StringComparer.Ordinal)
            || scaler.Statistics is null || scaler.Statistics.Length != layout.MarketChannelCount)
            throw new InvalidDataException("Portfolio scaler schema, feature mode, shape or contract hash is invalid.");
        foreach (var s in scaler.Statistics)
            if (s is null || !double.IsFinite(s.Mean) || !double.IsFinite(s.Std) || !double.IsFinite(s.Min) || !double.IsFinite(s.Max)
                || s.Std < 0 || s.Min > s.Max)
                throw new InvalidDataException("Scaler statistics are invalid.");
        return layout;
    }
}
