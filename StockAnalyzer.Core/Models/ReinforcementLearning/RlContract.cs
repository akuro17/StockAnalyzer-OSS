using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Single owner of the adopted T15 wire, observation, algorithm and resource constants.</summary>
public static class RlContract
{
    public const string SpecificationId = "SA-RL-LOCAL-STORED-D1-20261004";
    public const int SchemaVersion = 1;
    public const int WindowLength = 64;
    public const int EpisodeHorizon = 128;
    public const int SupportLength = WindowLength + EpisodeHorizon;
    public const int MarketChannelCount = 5;
    public const int AccountFieldCount = 16;
    public const int ObservationLength = WindowLength * MarketChannelCount + AccountFieldCount;
    public const int ActionCount = 3;
    public const decimal FixedQuantity = BacktestDefaults.SizingParameter;
    public const decimal InitialMarginRatio = 1m;
    public const decimal MaxPositionQuantity = 10_000m;
    public const decimal RewardScale = 1m;
    public const decimal DrawdownPenalty = 0m;
    public const decimal Gamma = 0.99m;
    public const decimal LearningRate = 0.0001m;
    public const decimal AdamBeta1 = 0.9m;
    public const decimal AdamBeta2 = 0.999m;
    public const decimal AdamEpsilon = 0.00000001m;
    public const decimal AdamWeightDecay = 0m;
    public const bool AdamAmsGrad = false;
    public const decimal HuberDelta = 1m;
    public const int ReplayCapacity = 5_000;
    public const int LearningStarts = 500;
    public const int BatchSize = 64;
    public const int TrainFrequency = 4;
    public const int GradientSteps = 1;
    public const int TdSteps = 1;
    public const int TargetUpdateInterval = 1_000;
    public const decimal TargetTau = 1m;
    public const decimal MaxGradientNorm = 1m;
    public const decimal ExplorationInitial = 1m;
    public const decimal ExplorationFinal = 0.05m;
    public const decimal ExplorationFraction = 0.20m;
    public const int TrainingTransitionsPerSeed = 10_000;
    public const int SmokeTrainingTransitions = 1_024;
    public const int SmokeCheckpointInterval = 512;
    public const int ValidationCheckpointInterval = 5_000;
    public const int EvaluationEpisodeCount = 5;
    public const int EnvironmentSeedOffset = 1_000;
    public const int ExplorationSeedOffset = 2_000;
    public const int RandomBaselineSeedOffset = 3_000;
    public const int CpuThreads = 1;
    public const int NetworkWidth = 64;
    public const int NetworkHiddenLayerCount = 2;
    public const int PartitionDenominator = 5;
    public const int TrainPartitionNumerator = 3;
    public const int ValidationPartitionNumerator = 4;
    public const int OnnxOpset = 18;
    public const string OnnxInputName = "observation";
    public const string OnnxOutputName = "q_values";
    public const float AbsoluteTolerance = 1e-5f;
    public const float RelativeTolerance = 1e-4f;
    public const long BufferByteLimit = 128L * 1024 * 1024;
    public const int ProcessedTransitionLimit = 100_000;
    public const int EpisodeStartLimit = 1_000;
    public const int EnvironmentCount = 1;
    public const int IpcFrameByteLimit = 1024 * 1024;
    public const int JobDeadlineMinutes = 120;
    public const int IpcTimeoutSeconds = 5;
    public const int CancellationTimeoutSeconds = 10;
    public const int DiagnosticCheckpointLimit = 3;
    public const int RollbackGenerationCount = 5;
    public const int InferenceWarmupCalls = 100;
    public const int InferenceMeasuredCalls = 1_000;
    public const int SmaFastPeriod = 20;
    public const int SmaSlowPeriod = 60;
    public const string SmaBaselineId = "rl.rule.sma_cross.20.60.v1";
    public const string RandomBaselineId = "rl.random.uniform3.v1";
    public const string HoldBaselineId = "rl.hold.v1";
    public const string BuyHoldBaselineId = "rl.buy_hold.v1";
    public const string StableBaselinesVersion = "2.7.0";
    public const string GymnasiumVersion = "1.2.1";
    public const decimal PanelHeight = 180m;
    public const decimal PanelVerticalMargin = 12m;
    public const decimal HudInset = 12m;
    public const decimal StrokeThickness = 1.5m;
    public const decimal Opacity = 1m;
    public const decimal AxisPaddingRatio = 0.05m;
    public const decimal FlatAxisHalfRange = 0.01m;
    public const int DisplayDecimals = 6;
    public const int SizeDisplayDecimals = 2;
    public const int StopDisplayDecimals = 2;

    public static ImmutableArray<int> CandidateSeeds { get; } = ImmutableArray.Create(42, 43, 44);
    public static ImmutableArray<string> MarketChannelNames { get; } = ImmutableArray.Create("Open", "High", "Low", "Close", "Volume");
    public static ImmutableArray<string> AccountFieldNames { get; } = ImmutableArray.Create(
        "position_long", "quantity_ratio", "cash_ratio", "held_margin_ratio", "equity_ratio", "unrealized_ratio",
        "pending_entry", "pending_exit", "held_bars_ratio", "entry_price_ratio", "drawdown_ratio", "remaining_decisions_ratio",
        "active_stop", "stop_price_ratio", "retained_stop_distance", "deferred_market_exit");

    /// <summary>Finite-Q greedy selection with exact Hold, Buy, Sell tie precedence.</summary>
    public static RlAction SelectAction(ReadOnlySpan<float> qValues)
    {
        if (qValues.Length != ActionCount) throw new ArgumentException("Exactly three Q values are required.", nameof(qValues));
        int best = 0;
        for (int i = 0; i < qValues.Length; i++)
        {
            if (!float.IsFinite(qValues[i])) throw new ArgumentException("Q values must be finite.", nameof(qValues));
            if (qValues[i] > qValues[best]) best = i;
        }
        return (RlAction)best;
    }

    /// <summary>Capture the selected costs without borrowing its non-RL sizing/margin identity.</summary>
    public static BacktestConfiguration CreateConfiguration(BacktestConfiguration selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        selected.Validate();
        return new BacktestConfiguration
        {
            ExecutionModel = ExecutionModel.YFinanceApproximate,
            InitialCapital = selected.InitialCapital,
            CommissionFlat = selected.CommissionFlat,
            CommissionPerUnit = selected.CommissionPerUnit,
            SlippageRatio = selected.SlippageRatio,
            LiquidationPenaltyRatio = selected.LiquidationPenaltyRatio,
            TradingDaysPerYear = selected.TradingDaysPerYear,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = FixedQuantity,
            InitialMarginRatio = InitialMarginRatio,
            MaintenanceMarginRatio = BacktestDefaults.MaintenanceMarginRatio,
        };
    }

    /// <summary>Named constants transported to Python; never duplicate numerical defaults there.</summary>
    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlContract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            // Decimal constants are emitted as init-only fields with DecimalConstantAttribute, not CLR literals.
            if (field.IsLiteral || field.IsDefined(typeof(System.Runtime.CompilerServices.DecimalConstantAttribute), false))
                values.Add(field.Name, field.GetValue(null)!);
        values.Add(nameof(CandidateSeeds), CandidateSeeds);
        values.Add(nameof(MarketChannelNames), MarketChannelNames);
        values.Add(nameof(AccountFieldNames), AccountFieldNames);
        values.Add("Actions", new[] { nameof(RlAction.Hold), nameof(RlAction.Buy), nameof(RlAction.Sell) });
        values.Add("InitialCapital", BacktestDefaults.InitialCapital);
        values.Add("MaintenanceMarginRatio", BacktestDefaults.MaintenanceMarginRatio);
        values.Add("ExecutionModel", (int)ExecutionModel.YFinanceApproximate);
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));
}
