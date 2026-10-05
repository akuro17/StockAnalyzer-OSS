using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>The only decimal-to-ML boundary; never used for financial calculations.</summary>
public static class RlObservationBuilder
{
    public static FeatureSpec FeatureSpec { get; } = new()
    {
        Channels = Array.AsReadOnly(new[]
        {
            new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Open },
            new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.High },
            new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Low },
            new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close },
            new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.Volume },
        }),
        Lags = Array.Empty<int>(),
    };

    public static Dictionary<string, string> CreateMetadata(string symbol, string sourceRevision, string evaluationRevision) => new()
    {
        ["rl_contract_hash"] = RlContract.ContractHash,
        ["target_type"] = RlFiles.Target,
        ["symbol"] = symbol,
        ["timeframe"] = nameof(TimeFrame.D1),
        ["source_revision"] = sourceRevision,
        ["evaluation_revision"] = evaluationRevision,
        ["window_size"] = RlContract.WindowLength.ToString(CultureInfo.InvariantCulture),
        ["channels"] = RlContract.MarketChannelCount.ToString(CultureInfo.InvariantCulture),
        ["feature_mode"] = RlFiles.FeatureMode,
        ["feature_spec"] = JsonSerializer.Serialize(FeatureSpec, TrainingConfigJson.Options),
        ["normalization"] = "fixed_zscore",
        ["lags"] = "[]",
        ["clip_sigma"] = "",
        ["scaler_ref"] = RlFiles.Model + RlFiles.ScalerSuffix,
    };

    public static float[] Build(RlSessionSnapshot account, FixedScaler scaler)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(scaler);
        if (account.MarketWindow.Length != RlContract.WindowLength || account.AccountFields.Length != RlContract.AccountFieldCount)
            throw new InvalidDataException("RL account/market observation dimensions are invalid.");
        var raw = new double[RlContract.WindowLength * RlContract.MarketChannelCount];
        for (int i = 0; i < account.MarketWindow.Length; i++)
        {
            var bar = account.MarketWindow[i];
            int offset = i * RlContract.MarketChannelCount;
            raw[offset] = (double)bar.Open;
            raw[offset + 1] = (double)bar.High;
            raw[offset + 2] = (double)bar.Low;
            raw[offset + 3] = (double)bar.Close;
            raw[offset + 4] = bar.Volume;
        }
        var observation = new float[RlContract.ObservationLength];
        scaler.Transform(raw, observation.AsSpan(0, raw.Length));
        for (int i = 0; i < account.AccountFields.Length; i++)
        {
            float value = (float)account.AccountFields[i];
            if (!float.IsFinite(value)) throw new InvalidDataException("Account ML value is not finite.");
            observation[raw.Length + i] = value;
        }
        return observation;
    }
}
