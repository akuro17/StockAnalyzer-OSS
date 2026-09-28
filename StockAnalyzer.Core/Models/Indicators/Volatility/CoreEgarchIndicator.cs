using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    /// <summary>
    /// EGARCH(P, P, Q) conditional volatility (Nelson 1991) estimated by Gaussian QMLE on a rolling window and refitted every
    /// <see cref="RefitInterval"/> bars. Output: conditional standard deviation of the bar's return in percent per bar (no annualization).
    /// Causal: the value at bar s uses returns up to bar s−1 only, and coefficients estimated from returns before s.
    /// </summary>
    [StockAnalyzerIndicator(IndicatorType.Egarch)]
    public class CoreEgarchIndicator : CoreIndicatorBase
    {
        public override string Name => "EGARCH";
        public string ShortName => $"EGARCH({P},{Q})";
        public override bool IsOverlay => false;

        public int P { get; set; } = IndicatorDefaultConstants.EgarchDefaultOrder;
        public int Q { get; set; } = IndicatorDefaultConstants.EgarchDefaultOrder;
        public int Period { get; set; } = IndicatorDefaultConstants.EgarchDefaultPeriod;
        public int RefitInterval { get; set; } = IndicatorDefaultConstants.EgarchDefaultRefitInterval;
        public int MaxIterations { get; set; } = IndicatorDefaultConstants.EgarchDefaultMaxIterations;
        public decimal MaxSigmaRatio { get; set; } = (decimal)IndicatorDefaultConstants.EgarchDefaultMaxSigmaRatio;
        public decimal MinSigmaRatio { get; set; } = (decimal)IndicatorDefaultConstants.EgarchDefaultMinSigmaRatio;

        /// <summary>Re-estimations attempted by the last successful <c>Calculate</c> (diagnostics only; does not affect the values).</summary>
        public int LastRefitCount { get; private set; }

        /// <summary>Re-estimations of the last calculation that found no usable fit; their bars carry empty values.</summary>
        public int LastFailedRefitCount { get; private set; }

        /// <summary>Re-estimations of the last calculation that stopped at the iteration cap (the best point found was still used).</summary>
        public int LastNonConvergedRefitCount { get; private set; }

        /// <summary>
        /// Re-estimations of the last calculation that left at least one bar empty as unstable: the block filter reached a variance guard (collapse or
        /// run-away of σ; that bar and the rest of the block are empty) or a bar's σ left the plausibility band (that bar is empty).
        /// </summary>
        public int LastUnstableRefitCount { get; private set; }

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreEgarchParameter p)
            {
                P = p.P;
                Q = p.Q;
                Period = p.Period;
                RefitInterval = p.RefitInterval;
                MaxIterations = p.MaxIterations;
                MaxSigmaRatio = p.MaxSigmaRatio;
                MinSigmaRatio = p.MinSigmaRatio;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var parameters = new CoreEgarchParameter { P = P, Q = Q, Period = Period, RefitInterval = RefitInterval, MaxIterations = MaxIterations, MaxSigmaRatio = MaxSigmaRatio, MinSigmaRatio = MinSigmaRatio };
            try
            {
                parameters.Validate();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return IndicatorResult.Failure(ex.Message);
            }

            double[] prices = PriceDataHelper.ExtractDoubleSeries(candles, PriceSource);
            int n = prices.Length;

            // returns[i] = 100·ln(P_i / P_{i-1}) (percent); returns[0] has no predecessor.
            var returns = new double[n];
            returns[0] = double.NaN;
            for (int i = 1; i < n; i++)
            {
                returns[i] = EgarchMath.PercentLogReturn(prices[i - 1], prices[i]);
            }

            var volatility = new double[n];
            Array.Fill(volatility, double.NaN);
            int failedRefits = 0;
            int nonConvergedRefits = 0;
            int unstableRefits = 0;

            // First refit at bar Period+1: its window is the Period returns before that bar, so no output uses its own bar's return.
            int firstFit = Period + 1;
            int fitCount = n > firstFit ? (n - 1 - firstFit) / RefitInterval + 1 : 0;
            var fitOptions = new EgarchFitOptions(MaxIterations: MaxIterations);
            int p = P, q = Q, period = Period, interval = RefitInterval;
            double minSigmaRatio = (double)MinSigmaRatio, maxSigmaRatio = (double)MaxSigmaRatio;

            // Refits are independent of each other, and every one reads only returns before its own bars, so running them in parallel is deterministic and causal.
            Parallel.For(0, fitCount, k =>
            {
                int fitBar = firstFit + k * interval;
                int blockEnd = Math.Min(fitBar + interval - 1, n - 1);
                int windowStart = fitBar - period;

                ReadOnlySpan<double> window = returns.AsSpan(windowStart, period);
                Span<double> theta = stackalloc double[EgarchMath.ParameterCount(p, q)];
                EgarchFitResult fit = EgarchMath.Fit(window, p, q, theta, fitOptions);
                if (!fit.Success)
                {
                    Interlocked.Increment(ref failedRefits);
                    return;
                }

                if (!fit.Converged)
                {
                    Interlocked.Increment(ref nonConvergedRefits);
                }

                // σ for bars fitBar..blockEnd: run the fixed-θ filter over the returns from the window start up to the bar before blockEnd.
                ReadOnlySpan<double> filtered = returns.AsSpan(windowStart, blockEnd - windowStart);
                double[] sigma = new double[filtered.Length + 1];
                EgarchMath.FilterOneStepAhead(filtered, theta, p, q, fit.Initialization, sigma, fitBar - windowStart, out int firstGuardedIndex);

                // Once the filter hits a variance guard it has diverged (σ collapsed, then ran away): from that bar to the end of the block show
                // nothing rather than guard-level numbers. Independently, a bar whose σ is outside the plausibility band is left empty on its own.
                // Both depend only on returns before the bar, so bars are never changed by later ones (causal); the next refit starts fresh.
                int firstGuardedBar = firstGuardedIndex >= 0 ? windowStart + firstGuardedIndex : int.MaxValue;
                bool blanked = false;
                for (int bar = fitBar; bar <= blockEnd; bar++)
                {
                    double barSigma = sigma[bar - windowStart];
                    if (bar >= firstGuardedBar || !EgarchMath.IsSigmaPlausible(barSigma, fit.Initialization, minSigmaRatio, maxSigmaRatio))
                    {
                        blanked = true;
                        continue;
                    }

                    volatility[bar] = barSigma;
                }

                if (blanked)
                {
                    Interlocked.Increment(ref unstableRefits);
                }
            });

            LastRefitCount = fitCount;
            LastFailedRefitCount = failedRefits;
            LastNonConvergedRefitCount = nonConvergedRefits;
            LastUnstableRefitCount = unstableRefits;

            _values.Clear();
            NullableDecimalConversion.AppendTo(volatility, _values);
            return IndicatorResult.Success(_values);
        }
    }
}
