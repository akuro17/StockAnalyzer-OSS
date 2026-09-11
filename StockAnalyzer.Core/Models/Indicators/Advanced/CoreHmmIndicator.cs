using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Gaussian Hidden Markov Model (HMM) regime detection indicator.
/// Computes causal, forward-filtered regime probability for the bull state
/// using a pre-allocated scratchpad and scaled Baum-Welch EM algorithm.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HiddenMarkovModel)]
public class CoreHmmIndicator : CoreIndicatorBase
{
    public int States { get; set; } = 2;
    public int Period { get; set; } = 100;
    public int MaxIterations { get; set; } = 30;
    public double Tolerance { get; set; } = 1e-4;

    public override string Name => $"Hidden Markov Model ({Period},{States})";
    public override bool IsOverlay => false;

    private HmmWorkspace? _workspace;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHmmParameter p)
        {
            States = p.States;
            Period = p.Period;
            MaxIterations = p.MaxIterations;
            Tolerance = p.Tolerance;
            _workspace = null;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = candles.Count;
        int w = Period;
        int k = States;

        // If not enough data for at least one full estimation window
        if (count <= w)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        // Fill warm-up bars
        for (int t = 0; t < w; t++)
        {
            _values.Add(null);
        }

        var ws = GetOrCreateWorkspace(w, k);

        // Rolling causal estimation window
        for (int t = w; t < count; t++)
        {
            // 1. Extract log-return observation series of length W
            bool hasInvalidPrice = false;
            for (int m = 0; m < w; m++)
            {
                decimal currDec = priceSeries[t - w + 1 + m] ?? 0m;
                decimal prevDec = priceSeries[t - w + m] ?? 0m;
                double pCurr = (double)currDec;
                double pPrev = (double)prevDec;

                if (pCurr <= 1e-12 || pPrev <= 1e-12 || double.IsNaN(pCurr) || double.IsNaN(pPrev) || double.IsInfinity(pCurr) || double.IsInfinity(pPrev))
                {
                    hasInvalidPrice = true;
                    break;
                }

                double ret = Math.Log(pCurr / pPrev);
                if (double.IsNaN(ret) || double.IsInfinity(ret))
                {
                    hasInvalidPrice = true;
                    break;
                }
                ws.X[m] = ret;
            }

            if (hasInvalidPrice)
            {
                _values.Add(null);
                continue;
            }

            // 2. Deterministic parameter initialization
            double sumX = 0.0;
            for (int m = 0; m < w; m++)
            {
                sumX += ws.X[m];
            }
            double muG = sumX / w;

            double sumSqDiff = 0.0;
            for (int m = 0; m < w; m++)
            {
                double diff = ws.X[m] - muG;
                sumSqDiff += diff * diff;
            }
            double sigmaG2 = Math.Max(sumSqDiff / w, 1e-6);
            double stdG = Math.Sqrt(sigmaG2);

            for (int i = 0; i < k; i++)
            {
                ws.Pi[i] = 1.0 / k;
            }

            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++)
                {
                    ws.Transition[i, j] = (i == j) ? 0.8 : (0.2 / (k - 1));
                }
            }

            for (int i = 0; i < k; i++)
            {
                ws.Mu[i] = muG + stdG * ((2.0 * i) / (k - 1.0) - 1.0);
                ws.SigmaSq[i] = sigmaG2;
            }

            // 3. Scaled Baum-Welch EM Algorithm
            double prevLogLikelihood = double.NegativeInfinity;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                // Forward pass
                double sumAlpha0 = 0.0;
                for (int i = 0; i < k; i++)
                {
                    double b_i = ComputeEmission(ws.X[0], ws.Mu[i], ws.SigmaSq[i]);
                    double aPrime = ws.Pi[i] * b_i;
                    ws.Alpha[0, i] = aPrime;
                    sumAlpha0 += aPrime;
                }
                if (sumAlpha0 > 1e-250 && !double.IsNaN(sumAlpha0) && !double.IsInfinity(sumAlpha0))
                {
                    double c0 = 1.0 / sumAlpha0;
                    ws.CScale[0] = c0;
                    for (int i = 0; i < k; i++)
                    {
                        ws.Alpha[0, i] *= c0;
                    }
                }
                else
                {
                    ws.CScale[0] = 1.0;
                    for (int i = 0; i < k; i++)
                    {
                        ws.Alpha[0, i] = 1.0 / k;
                    }
                }

                for (int m = 1; m < w; m++)
                {
                    double sumAlphaM = 0.0;
                    for (int j = 0; j < k; j++)
                    {
                        double prevSum = 0.0;
                        for (int i = 0; i < k; i++)
                        {
                            prevSum += ws.Alpha[m - 1, i] * ws.Transition[i, j];
                        }
                        double b_j = ComputeEmission(ws.X[m], ws.Mu[j], ws.SigmaSq[j]);
                        double aPrime = prevSum * b_j;
                        ws.Alpha[m, j] = aPrime;
                        sumAlphaM += aPrime;
                    }
                    if (sumAlphaM > 1e-250 && !double.IsNaN(sumAlphaM) && !double.IsInfinity(sumAlphaM))
                    {
                        double cm = 1.0 / sumAlphaM;
                        ws.CScale[m] = cm;
                        for (int j = 0; j < k; j++)
                        {
                            ws.Alpha[m, j] *= cm;
                        }
                    }
                    else
                    {
                        ws.CScale[m] = 1.0;
                        for (int j = 0; j < k; j++)
                        {
                            ws.Alpha[m, j] = 1.0 / k;
                        }
                    }
                }

                // Backward pass
                for (int i = 0; i < k; i++)
                {
                    ws.Beta[w - 1, i] = ws.CScale[w - 1];
                }
                for (int m = w - 2; m >= 0; m--)
                {
                    for (int i = 0; i < k; i++)
                    {
                        double sumTrans = 0.0;
                        for (int j = 0; j < k; j++)
                        {
                            double b_j = ComputeEmission(ws.X[m + 1], ws.Mu[j], ws.SigmaSq[j]);
                            sumTrans += ws.Transition[i, j] * b_j * ws.Beta[m + 1, j];
                        }
                        ws.Beta[m, i] = ws.CScale[m] * sumTrans;
                    }
                }

                // Expectation (E-step)
                for (int m = 0; m < w; m++)
                {
                    double sumGamma = 0.0;
                    for (int l = 0; l < k; l++)
                    {
                        sumGamma += ws.Alpha[m, l] * ws.Beta[m, l];
                    }
                    double denomGamma = Math.Max(sumGamma, 1e-250);
                    for (int i = 0; i < k; i++)
                    {
                        ws.Gamma[m, i] = (ws.Alpha[m, i] * ws.Beta[m, i]) / denomGamma;
                    }
                }

                for (int m = 0; m < w - 1; m++)
                {
                    double sumXi = 0.0;
                    for (int l1 = 0; l1 < k; l1++)
                    {
                        for (int l2 = 0; l2 < k; l2++)
                        {
                            double b_l2 = ComputeEmission(ws.X[m + 1], ws.Mu[l2], ws.SigmaSq[l2]);
                            sumXi += ws.Alpha[m, l1] * ws.Transition[l1, l2] * b_l2 * ws.Beta[m + 1, l2];
                        }
                    }
                    double denomXi = Math.Max(sumXi, 1e-250);
                    for (int i = 0; i < k; i++)
                    {
                        for (int j = 0; j < k; j++)
                        {
                            double b_j = ComputeEmission(ws.X[m + 1], ws.Mu[j], ws.SigmaSq[j]);
                            ws.Xi[m, i, j] = (ws.Alpha[m, i] * ws.Transition[i, j] * b_j * ws.Beta[m + 1, j]) / denomXi;
                        }
                    }
                }

                // Maximization (M-step)
                for (int i = 0; i < k; i++)
                {
                    ws.Pi[i] = ws.Gamma[0, i];
                }

                for (int i = 0; i < k; i++)
                {
                    double sumGammaI = 0.0;
                    for (int m = 0; m < w - 1; m++)
                    {
                        sumGammaI += ws.Gamma[m, i];
                    }

                    if (sumGammaI > 1e-12)
                    {
                        double sumRowA = 0.0;
                        for (int j = 0; j < k; j++)
                        {
                            double sumXiIJ = 0.0;
                            for (int m = 0; m < w - 1; m++)
                            {
                                sumXiIJ += ws.Xi[m, i, j];
                            }
                            ws.Transition[i, j] = sumXiIJ / sumGammaI;
                            sumRowA += ws.Transition[i, j];
                        }
                        double normRowA = Math.Max(sumRowA, 1e-12);
                        for (int j = 0; j < k; j++)
                        {
                            ws.Transition[i, j] /= normRowA;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < k; j++)
                        {
                            ws.Transition[i, j] = (i == j) ? 0.8 : (0.2 / (k - 1));
                        }
                    }
                }

                for (int i = 0; i < k; i++)
                {
                    double sumGammaIAll = 0.0;
                    double sumGammaIX = 0.0;
                    for (int m = 0; m < w; m++)
                    {
                        double g = ws.Gamma[m, i];
                        sumGammaIAll += g;
                        sumGammaIX += g * ws.X[m];
                    }

                    if (sumGammaIAll > 1e-12)
                    {
                        double newMu = sumGammaIX / sumGammaIAll;
                        ws.Mu[i] = newMu;

                        double sumGammaIVar = 0.0;
                        for (int m = 0; m < w; m++)
                        {
                            double g = ws.Gamma[m, i];
                            double diff = ws.X[m] - newMu;
                            sumGammaIVar += g * diff * diff;
                        }
                        ws.SigmaSq[i] = Math.Max(sumGammaIVar / sumGammaIAll, 1e-6);
                    }
                    else
                    {
                        ws.SigmaSq[i] = Math.Max(sigmaG2, 1e-6);
                    }
                }

                // Log-likelihood convergence check
                double logLikelihood = 0.0;
                for (int m = 0; m < w; m++)
                {
                    logLikelihood -= Math.Log(ws.CScale[m]);
                }

                if (iter >= 1 && Math.Abs(logLikelihood - prevLogLikelihood) <= Tolerance)
                {
                    break;
                }
                prevLogLikelihood = logLikelihood;
            }

            // 4. Final Forward Pass
            double finalSumAlpha0 = 0.0;
            for (int i = 0; i < k; i++)
            {
                double b_i = ComputeEmission(ws.X[0], ws.Mu[i], ws.SigmaSq[i]);
                double aPrime = ws.Pi[i] * b_i;
                ws.Alpha[0, i] = aPrime;
                finalSumAlpha0 += aPrime;
            }
            if (finalSumAlpha0 > 1e-250 && !double.IsNaN(finalSumAlpha0) && !double.IsInfinity(finalSumAlpha0))
            {
                double finalC0 = 1.0 / finalSumAlpha0;
                for (int i = 0; i < k; i++)
                {
                    ws.Alpha[0, i] *= finalC0;
                }
            }
            else
            {
                for (int i = 0; i < k; i++)
                {
                    ws.Alpha[0, i] = 1.0 / k;
                }
            }

            for (int m = 1; m < w; m++)
            {
                double finalSumAlphaM = 0.0;
                for (int j = 0; j < k; j++)
                {
                    double prevSum = 0.0;
                    for (int i = 0; i < k; i++)
                    {
                        prevSum += ws.Alpha[m - 1, i] * ws.Transition[i, j];
                    }
                    double b_j = ComputeEmission(ws.X[m], ws.Mu[j], ws.SigmaSq[j]);
                    double aPrime = prevSum * b_j;
                    ws.Alpha[m, j] = aPrime;
                    finalSumAlphaM += aPrime;
                }
                if (finalSumAlphaM > 1e-250 && !double.IsNaN(finalSumAlphaM) && !double.IsInfinity(finalSumAlphaM))
                {
                    double finalCm = 1.0 / finalSumAlphaM;
                    for (int j = 0; j < k; j++)
                    {
                        ws.Alpha[m, j] *= finalCm;
                    }
                }
                else
                {
                    for (int j = 0; j < k; j++)
                    {
                        ws.Alpha[m, j] = 1.0 / k;
                    }
                }
            }

            // 5. Deterministic Bull State Selection (Max Mu -> Min SigmaSq -> Min index)
            int bullState = 0;
            for (int i = 1; i < k; i++)
            {
                double diffMu = ws.Mu[i] - ws.Mu[bullState];
                if (diffMu > 1e-12)
                {
                    bullState = i;
                }
                else if (Math.Abs(diffMu) <= 1e-12)
                {
                    double diffVar = ws.SigmaSq[i] - ws.SigmaSq[bullState];
                    if (diffVar < -1e-12)
                    {
                        bullState = i;
                    }
                }
            }

            // 6. Causal Filtered Output Value
            double bullProb = ws.Alpha[w - 1, bullState];
            if (double.IsNaN(bullProb) || double.IsInfinity(bullProb))
            {
                _values.Add(null);
            }
            else
            {
                decimal rawPercent = (decimal)bullProb * 100.0m;
                decimal rounded = Math.Round(rawPercent, 4, MidpointRounding.AwayFromZero);
                decimal clamped = Math.Clamp(rounded, 0.0000m, 100.0000m);
                _values.Add(clamped);
            }
        }

        return IndicatorResult.Success(_values);
    }

    private static double ComputeEmission(double x, double mu, double sigmaSq)
    {
        double denom = Math.Sqrt(2.0 * Math.PI * sigmaSq);
        double diff = x - mu;
        double exponent = -(diff * diff) / (2.0 * sigmaSq);
        double density = (1.0 / denom) * Math.Exp(exponent);
        if (double.IsNaN(density) || density < 1e-250)
        {
            return 1e-250;
        }
        return density;
    }

    private HmmWorkspace GetOrCreateWorkspace(int period, int states)
    {
        if (_workspace == null || _workspace.Period != period || _workspace.States != states)
        {
            _workspace = new HmmWorkspace(period, states);
        }
        return _workspace;
    }

    private sealed class HmmWorkspace
    {
        public readonly int Period;
        public readonly int States;
        public readonly double[] X;
        public readonly double[,] Alpha;
        public readonly double[,] Beta;
        public readonly double[,] Gamma;
        public readonly double[,,] Xi;
        public readonly double[] CScale;
        public readonly double[] Pi;
        public readonly double[,] Transition;
        public readonly double[] Mu;
        public readonly double[] SigmaSq;

        public HmmWorkspace(int period, int states)
        {
            Period = period;
            States = states;
            X = new double[period];
            Alpha = new double[period, states];
            Beta = new double[period, states];
            Gamma = new double[period, states];
            Xi = new double[period, states, states];
            CScale = new double[period];
            Pi = new double[states];
            Transition = new double[states, states];
            Mu = new double[states];
            SigmaSq = new double[states];
        }
    }
}
