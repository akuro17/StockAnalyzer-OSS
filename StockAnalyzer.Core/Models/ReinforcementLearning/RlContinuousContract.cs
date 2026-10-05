using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Adopted T16 constants; T15's immutable contract and generations remain unchanged.</summary>
public static class RlContinuousContract
{
    public const string SpecificationId = "SA-RL-PPO-LOCAL-STORED-D1-20261004";
    public const string Target = "reinforcement_learning_size_stop";
    public const string ActiveFile = "continuous_active.json";
    public const string TrainerScript = "training/train_rl_ppo.py";
    public const string DirectionModel = "direction/policy.onnx";
    public const string DirectionScaler = "direction/policy.onnx.scaler.json";
    public const string DirectionManifest = "direction.json";
    public const string OutputName = "normalized_actions";
    public const string ParityField = "normalized_actions";
    public const int ActionCount = 2;
    public const float ActionMinimum = -1f;
    public const float ActionMaximum = 1f;
    public const decimal SizeCoefficient = .15m;
    public const decimal MaximumSizeRatio = .30m;
    public const decimal MinimumStopDistance = .005m;
    public const decimal StopCoefficient = .0225m;
    public const decimal BaselineStopDistance = .02m;
    public const float BaselineSizeAction = 1f;
    public const float BaselineStopAction = -1f / 3f;
    public const string PairedBaselineId = "rl.frozen_direction.fixed_size_stop.v1";
    public const decimal LearningRate = .0003m;
    public const decimal AdamEpsilon = .00001m;
    public const int RolloutSteps = 512;
    public const int Epochs = 10;
    public const int RolloutAuxiliaryFieldCount = 6;
    public const int TrainingTransitionsPerSeed = 10240;
    public const int ValidationCheckpointInterval = 5120;
    public const decimal GaeLambda = .95m;
    public const decimal ClipRange = .2m;
    public const decimal EntropyCoefficient = .01m;
    public const decimal ValueCoefficient = .5m;
    public const decimal MaxGradientNorm = .5m;
    public const decimal TargetKl = .02m;
    public const decimal LogStdInitial = 0m;
    public const decimal ActorOutputGain = .01m;
    public const decimal ValueOutputGain = 1m;
    public const int InitialUnprotectedBars = 1;
    public const int StopSellLatencyBars = 2;
    public const string Disclosure = "Frozen T15 direction is a transfer experiment on different quantity/stop states. Entry budget is not guaranteed filled exposure. Protective stop starts after one unprotected fill bar; Sell with a stop fills no earlier than two bars after the decision. No live orders.";

    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlContinuousContract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral || field.IsDefined(typeof(System.Runtime.CompilerServices.DecimalConstantAttribute), false))
                values.Add(field.Name, field.GetValue(null)!);
        values.Add("DirectionContractHash", RlContract.ContractHash);
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }
    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));

    public static RlContinuousAction Map(float rawSize, float rawStop)
    {
        if (!float.IsFinite(rawSize) || !float.IsFinite(rawStop)) throw new ArgumentException("Actor proposals must be finite float32.");
        float size = Math.Clamp(rawSize, ActionMinimum, ActionMaximum);
        float stop = Math.Clamp(rawStop, ActionMinimum, ActionMaximum);
        return new(rawSize, rawStop, size, stop, size != rawSize || stop != rawStop,
            ((decimal)size + 1m) * SizeCoefficient, MinimumStopDistance + ((decimal)stop + 1m) * StopCoefficient);
    }
}

public sealed record RlContinuousAction(float RawSize, float RawStop, float ClippedSize, float ClippedStop,
    bool WasClipped,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal SizeRatio,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] decimal StopDistance);
public sealed record RlDirectionDependency(string ModelId, string ModelSha256, string ScalerSha256, string ContractHash)
{
    public string? TemporalEvidenceSha256 { get; init; }
}
public sealed record RlContinuousReadout(RlContinuousAction Action, decimal? EstimatedQuantity,
    decimal? EstimatedStopPrice, decimal? ActualStopPrice, string State, string DirectionModelId)
{
    public RlEntryControl? SelectedPolicyControl { get; init; }
    public RlAction? PolicyAction { get; init; }
}
