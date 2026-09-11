using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

/// <summary>
/// Singular Spectrum Analysis (SSA) Structural Anomaly Indicator.
/// Computes strictly causal rolling structural residuals, scale-invariant Z-scores,
/// and tracks anomalous market regimes (Bullish spike / Bearish breakdown) using a
/// hysteresis state machine without future look-ahead bias.
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.SSAAnomaly)]
public class CoreSSAAnomalyIndicator : CoreIndicatorBase
{
    public const string ZScoreSeriesName = IndicatorResult.MainSeriesName;
    public const string AnomalyStateSeriesName = "AnomalyState";
    public const string EnterThresholdSeriesName = "EnterThreshold";
    public const string ExitThresholdSeriesName = "ExitThreshold";

    public int WindowSize { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultWindowSize;
    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultEmbeddingDimension;
    public int NumComponents { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultNumComponents;
    public bool AutoRank { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultAutoRank;
    public SsaDetrendMode DetrendMethod { get; set; } = SsaDetrendMode.LeastSquaresLinear;
    public override PriceType PriceSource { get; set; } = PriceType.Close;
    public decimal EnterThreshold { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultEnterThreshold;
    public decimal ExitThreshold { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultExitThreshold;
    public int CoolDownPeriod { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultCoolDownPeriod;
    public int MinDuration { get; set; } = IndicatorDefaultConstants.SsaAnomalyDefaultMinDuration;

    public override string Name => $"SSA Anomaly ({WindowSize}, {EmbeddingDimension}, {NumComponents})";
    public override bool IsOverlay => false;

    public IReadOnlyList<decimal?> ZScore => _values;
    public List<decimal?> AnomalyStateSeries { get; } = new();
    public List<decimal?> EnterThresholdSeries { get; } = new();
    public List<decimal?> ExitThresholdSeries { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSSAAnomalyParameter p)
        {
            WindowSize = p.WindowSize;
            EmbeddingDimension = p.EmbeddingDimension;
            NumComponents = p.NumComponents;
            AutoRank = p.AutoRank;
            DetrendMethod = p.DetrendMethod;
            PriceSource = p.PriceSource;
            EnterThreshold = p.EnterThreshold;
            ExitThreshold = p.ExitThreshold;
            CoolDownPeriod = p.CoolDownPeriod;
            MinDuration = p.MinDuration;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        AnomalyStateSeries.Clear();
        EnterThresholdSeries.Clear();
        ExitThresholdSeries.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = candles.Count;
        int window = Math.Max(SsaDecompositionEngine.MinSampleCount, WindowSize);

        if (n < window)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
                AnomalyStateSeries.Add(null);
                EnterThresholdSeries.Add(null);
                ExitThresholdSeries.Add(null);
            }

            return CreateResultDictionary();
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        double[] doublePrices = new double[n];
        for (int i = 0; i < n; i++)
        {
            doublePrices[i] = (double)(priceSeries[i] ?? 0m);
        }

        // Fill warm-up periods
        for (int i = 0; i < window - 1; i++)
        {
            _values.Add(null);
            AnomalyStateSeries.Add(null);
            EnterThresholdSeries.Add(null);
            ExitThresholdSeries.Add(null);
        }

        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, window / 2));
        int k = window - l + 1;

        double[] windowBuffer = new double[window];
        double[] causalResiduals = new double[n];

        double enterThreshDbl = (double)EnterThreshold;
        double exitThreshDbl = (double)ExitThreshold;

        // Causal Hysteresis State Machine
        SsaAnomalyState state = SsaAnomalyState.Normal;
        int stateStartBar = 0;
        double peakZ = 0.0;
        int coolDownCount = 0;

        for (int t = window - 1; t < n; t++)
        {
            // Copy causal past window [t - window + 1 .. t]
            Array.Copy(doublePrices, t - window + 1, windowBuffer, 0, window);

            int r = NumComponents;
            double causalReconstructed;
            if (AutoRank)
            {
                var decomp = SsaDecompositionEngine.Decompose(windowBuffer, l, DetrendMethod);
                if (decomp.SortedIndices.Length >= l)
                {
                    r = SsaRankSelector.EstimateSignalRank(
                        decomp.Eigenvalues,
                        SsaRankSelectionMethod.CumulativeEnergy,
                        targetEnergy: 0.90,
                        maxRank: l - 1);
                }
                else
                {
                    r = Math.Clamp(NumComponents, 1, Math.Min(l - 1, k));
                }

                causalReconstructed = SsaDecompositionEngine.ComputeCausalEndpoint(windowBuffer, decomp, l, r, DetrendMethod);
            }
            else
            {
                r = Math.Clamp(NumComponents, 1, Math.Min(l - 1, k));
                causalReconstructed = SsaDecompositionEngine.ComputeCausalEndpoint(windowBuffer, l, r, DetrendMethod);
            }
            if (double.IsNaN(causalReconstructed))
            {
                causalReconstructed = doublePrices[t];
            }

            double res = doublePrices[t] - causalReconstructed;
            causalResiduals[t] = res;

            // Rolling variance of causal residuals over available causal window
            int resStart = Math.Max(window - 1, t - window + 1);
            int resCount = t - resStart + 1;
            double sumSqRes = 0.0;
            double sumAbsPrice = 0.0;
            for (int j = resStart; j <= t; j++)
            {
                sumSqRes += causalResiduals[j] * causalResiduals[j];
                sumAbsPrice += Math.Abs(doublePrices[j]);
            }

            double sigmaRes = Math.Sqrt(sumSqRes / resCount);
            double meanPrice = sumAbsPrice / resCount;
            double epsilonSigma = Math.Max(SsaAnomalyDetectionEngine.AbsoluteFloorEpsilon, meanPrice * SsaAnomalyDetectionEngine.RelativeEpsilonFactor);

            double z;
            if (sigmaRes <= epsilonSigma)
            {
                z = 0.0;
            }
            else
            {
                z = Math.Clamp(res / sigmaRes, -SsaAnomalyDetectionEngine.MaxZClamp, SsaAnomalyDetectionEngine.MaxZClamp);
            }

            // Update Causal Hysteresis State Machine
            switch (state)
            {
                case SsaAnomalyState.Normal:
                    if (z >= enterThreshDbl)
                    {
                        state = SsaAnomalyState.Bullish;
                        stateStartBar = t;
                        peakZ = z;
                        coolDownCount = 0;
                    }
                    else if (z <= -enterThreshDbl)
                    {
                        state = SsaAnomalyState.Bearish;
                        stateStartBar = t;
                        peakZ = z;
                        coolDownCount = 0;
                    }
                    break;

                case SsaAnomalyState.Bullish:
                    if (z > peakZ) peakZ = z;

                    // Direct Reversal
                    if (z <= -enterThreshDbl)
                    {
                        state = SsaAnomalyState.Bearish;
                        stateStartBar = t;
                        peakZ = z;
                        coolDownCount = 0;
                    }
                    else if (z < exitThreshDbl)
                    {
                        coolDownCount++;
                        if (coolDownCount >= CoolDownPeriod)
                        {
                            state = SsaAnomalyState.Normal;
                            coolDownCount = 0;
                        }
                    }
                    else
                    {
                        coolDownCount = 0;
                    }
                    break;

                case SsaAnomalyState.Bearish:
                    if (z < peakZ) peakZ = z;

                    // Direct Reversal
                    if (z >= enterThreshDbl)
                    {
                        state = SsaAnomalyState.Bullish;
                        stateStartBar = t;
                        peakZ = z;
                        coolDownCount = 0;
                    }
                    else if (z > -exitThreshDbl)
                    {
                        coolDownCount++;
                        if (coolDownCount >= CoolDownPeriod)
                        {
                            state = SsaAnomalyState.Normal;
                            coolDownCount = 0;
                        }
                    }
                    else
                    {
                        coolDownCount = 0;
                    }
                    break;
            }

            decimal stateValue = 0m;
            if (state == SsaAnomalyState.Bullish)
            {
                stateValue = 1m;
            }
            else if (state == SsaAnomalyState.Bearish)
            {
                stateValue = -1m;
            }

            _values.Add((decimal)z);
            AnomalyStateSeries.Add(stateValue);
            EnterThresholdSeries.Add(EnterThreshold);
            ExitThresholdSeries.Add(ExitThreshold);
        }

        return CreateResultDictionary();
    }

    private IIndicatorResult CreateResultDictionary()
    {
        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { ZScoreSeriesName, _values },
            { "ZScore", _values },
            { AnomalyStateSeriesName, AnomalyStateSeries },
            { EnterThresholdSeriesName, EnterThresholdSeries },
            { ExitThresholdSeriesName, ExitThresholdSeries }
        });
    }
}
