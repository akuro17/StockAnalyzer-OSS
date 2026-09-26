using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.MathUtils
{
    public class NelderMeadOptimizerTests
    {
        [Fact]
        public void Minimize_Quadratic_FindsMinimum()
        {
            Span<double> x = stackalloc double[] { 4.0, -3.0 };
            var result = NelderMeadOptimizer.Minimize(p => (p[0] - 1.5) * (p[0] - 1.5) + 2.0 * (p[1] + 0.5) * (p[1] + 0.5), x);

            Assert.True(result.Converged);
            Assert.Equal(1.5, x[0], 4);
            Assert.Equal(-0.5, x[1], 4);
            Assert.Equal(0.0, result.Value, 8);
        }

        [Fact]
        public void Minimize_Rosenbrock_ConvergesWithRestarts()
        {
            Span<double> x = stackalloc double[] { -1.2, 1.0 };
            NelderMeadResult result = default;
            for (int run = 0; run < 3; run++)
            {
                result = NelderMeadOptimizer.Minimize(
                    p => 100.0 * (p[1] - p[0] * p[0]) * (p[1] - p[0] * p[0]) + (1.0 - p[0]) * (1.0 - p[0]), x,
                    new NelderMeadOptions(MaxIterations: 2000));
            }

            Assert.Equal(1.0, x[0], 3);
            Assert.Equal(1.0, x[1], 3);
        }

        [Fact]
        public void Minimize_RejectedRegion_IsRespected()
        {
            // Unconstrained minimum is at x = -2; the objective rejects x < 1, so the constrained minimum is at the boundary 1.
            Span<double> x = stackalloc double[] { 3.0 };
            NelderMeadOptimizer.Minimize(p => p[0] < 1.0 ? double.PositiveInfinity : (p[0] + 2.0) * (p[0] + 2.0), x);

            Assert.InRange(x[0], 1.0, 1.0001);
        }

        [Fact]
        public void Minimize_EmptyParameterVector_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Span<double> x = Span<double>.Empty;
                NelderMeadOptimizer.Minimize(_ => 0.0, x);
            });
        }
    }

    /// <summary>
    /// Golden values come from arch 8.0.0: <c>arch_model(r, vol='EGARCH', p=1, o=1, q=1, dist='Normal', rescale=False).fit()</c> on the
    /// series produced by <see cref="Series"/> (identical generator implemented in the reference script).
    /// </summary>
    public class EgarchMathTests
    {
        private readonly ITestOutputHelper _output;

        public EgarchMathTests(ITestOutputHelper output) => _output = output;

        // Agreement target for "looks identical on the chart": conditional volatility within 0.5 % relative.
        private const double VolatilityRelativeTolerance = 5e-3;

        private static double[] Series(int count, ulong seed, double mu, double omega, double alpha, double gamma, double beta, params (int Index, double Shock)[] jumps)
        {
            ulong state = seed;
            double Uniform()
            {
                state = unchecked(6364136223846793005UL * state + 1442695040888963407UL);
                return ((state >> 11) + 0.5) / (double)(1UL << 53);
            }

            double lnSigma2 = omega / (1.0 - beta);
            double zPrev = 0.0;
            var r = new double[count];
            for (int t = 0; t < count; t++)
            {
                double u1 = Uniform();
                double u2 = Uniform();
                double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                if (t > 0)
                {
                    lnSigma2 = omega + alpha * (Math.Abs(zPrev) - Math.Sqrt(2.0 / Math.PI)) + gamma * zPrev + beta * lnSigma2;
                }
                r[t] = mu + Math.Exp(0.5 * lnSigma2) * z;
                zPrev = z;
            }
            foreach (var (index, shock) in jumps)
            {
                r[index] += shock;
            }
            return r;
        }

        private static void AssertRelative(double expected, double actual)
            => Assert.True(Math.Abs(actual - expected) <= VolatilityRelativeTolerance * Math.Abs(expected), $"expected {expected}, actual {actual}");

        [Fact]
        public void Fit_MatchesArch_OnThousandObservations()
        {
            double[] r = Series(1000, 12345, 0.04, 0.02, 0.15, -0.08, 0.96);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(1, 1)];
            var fit = EgarchMath.Fit(r, 1, 1, theta);

            Assert.True(fit.Success);
            // The optimum must be at least as good as arch's (SLSQP stops at ftol 1e-6).
            Assert.True(fit.LogLikelihood >= -1563.5463178317987 - 1e-3, $"log-likelihood {fit.LogLikelihood}");
            Assert.Equal(0.060380984138594836, theta[0], 2);   // mu
            Assert.Equal(0.008106506225526946, theta[1], 2);   // omega
            Assert.Equal(0.1246969790796506, theta[2], 2);     // alpha
            Assert.Equal(-0.05516848235729042, theta[3], 2);   // gamma
            Assert.Equal(0.9732255980584421, theta[4], 2);     // beta

            var sigma = new double[r.Length + 1];
            EgarchMath.FilterOneStepAhead(r, theta, 1, 1, fit.Initialization, sigma);
            AssertRelative(1.2057468401260463, sigma[0]);
            AssertRelative(1.1704107934981218, sigma[1]);
            AssertRelative(1.1705496471652108, sigma[10]);
            AssertRelative(0.9133271992221368, sigma[500]);
            AssertRelative(1.468712094981637, sigma[999]);
        }

        /// <summary>
        /// arch 8.0.0 references (Y:\Temp\claude\egarch_golden_multi.py) for several sample types and orders. Every scenario must reach a
        /// log-likelihood at least arch's; where the likelihood is well identified the sampled σ path must also agree within the stated tolerance.
        /// </summary>
        public static IEnumerable<object[]> ArchScenarios()
        {
            int[] indexes = { 0, 1, 10, 100, 300, 600, 900, 1200, 1400, 1499 };
            yield return new object[] { "S1 persistent 1,1", 2024UL, new[] { 0.03, 0.02, 0.15, -0.08, 0.96 }, Array.Empty<(int, double)>(), 1, 1, -2506.0069472705563, indexes,
                new[] { 1.2031506760121542, 1.2077484473626152, 1.0581965316346666, 1.7261842992709306, 1.2460907125243648, 1.3266022174612804, 1.6616351030128897, 0.9729075038076701, 1.240512914421178, 1.802024664438709 }, 1e-3 };
            yield return new object[] { "S2 low persistence 1,1", 31UL, new[] { 0.0, 0.05, 0.2, -0.1, 0.8 }, Array.Empty<(int, double)>(), 1, 1, -2265.2510504766988, indexes,
                new[] { 0.9252051127077533, 0.891001410836145, 1.0358785507745423, 1.0919734685651479, 0.9645958418412837, 1.1770589347451317, 1.285191412130673, 1.2351921908314132, 1.1094003655016111, 1.2542993377218012 }, 1e-3 };
            yield return new object[] { "S3 jumps 1,1", 99UL, new[] { 0.03, 0.02, 0.15, -0.08, 0.96 }, new[] { (500, 8.0), (1000, -10.0) }, 1, 1, -2511.946597322858, indexes,
                new[] { 1.557943508287415, 1.6333318164388255, 1.568620313285197, 1.1231784703852807, 1.2193223601977585, 0.8141688061999676, 1.3124651908061227, 1.2582847054614892, 1.5427557636944065, 1.5484874430605158 }, 1e-3 };
            yield return new object[] { "S4 order 2,1", 7UL, new[] { 0.03, 0.02, 0.15, -0.08, 0.96 }, Array.Empty<(int, double)>(), 2, 1, -2560.1817293616023, indexes,
                new[] { 1.223534449361706, 1.2882044850681196, 1.4107728242238098, 1.4571740669492865, 1.3702088468729015, 1.386887560306968, 2.2351285235307885, 1.3425201323409615, 1.277282912487759, 1.4192122765956137 }, 1e-3 };
            yield return new object[] { "S5 order 1,2", 5UL, new[] { 0.03, 0.02, 0.15, -0.08, 0.96 }, Array.Empty<(int, double)>(), 1, 2, -2460.967896797595, indexes,
                new[] { 1.4341237740149162, 1.344347941549339, 1.1375482201696778, 1.821606118319169, 1.15021709692536, 1.4497213559001185, 1.3776896901855908, 1.315682811238371, 1.178473490981305, 1.3038459704652483 }, 1e-3 };
        }

        [Theory]
        [MemberData(nameof(ArchScenarios))]
        public void Fit_MatchesArch_AcrossScenarios(string name, ulong seed, double[] generator, (int Index, double Shock)[] jumps, int p, int q,
            double archLogLikelihood, int[] indexes, double[] archSigma, double tolerance)
        {
            double[] r = Series(1500, seed, generator[0], generator[1], generator[2], generator[3], generator[4], jumps);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(p, q)];
            var fit = EgarchMath.Fit(r, p, q, theta);

            Assert.True(fit.Success, name);
            Assert.True(fit.LogLikelihood >= archLogLikelihood - 1e-3, $"{name}: log-likelihood {fit.LogLikelihood} vs arch {archLogLikelihood}");

            var sigma = new double[r.Length + 1];
            EgarchMath.FilterOneStepAhead(r, theta, p, q, fit.Initialization, sigma);
            var errors = new double[indexes.Length];
            for (int i = 0; i < indexes.Length; i++)
            {
                errors[i] = Math.Abs(sigma[indexes[i]] - archSigma[i]) / archSigma[i];
            }
            _output.WriteLine($"{name}: converged={fit.Converged} loglik(native)={fit.LogLikelihood:F6} loglik(arch)={archLogLikelihood:F6} " +
                              $"sigma rel.err mean={errors.Average():P4} max={errors.Max():P4}");
            Assert.True(errors.Max() <= tolerance, $"{name}: max relative σ error {errors.Max():P4} exceeds {tolerance:P2}");
        }

        [Fact]
        public void Fit_WeaklyIdentifiedSample_IsNotWorseThanArch()
        {
            double[] r = Series(250, 777, 0.04, 0.02, 0.15, -0.08, 0.96);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(1, 1)];
            var fit = EgarchMath.Fit(r, 1, 1, theta);

            Assert.True(fit.Success);
            Assert.True(fit.LogLikelihood >= -360.66953267675933 - 1e-3, $"log-likelihood {fit.LogLikelihood}");

            // The likelihood of this 250-bar sample is nearly flat in (alpha, gamma, beta): arch's SLSQP stops at ftol 1e-6 on a different point of the ridge,
            // so only the objective value (not the volatility path) is comparable; the path must still be finite and positive.
            var sigma = new double[r.Length + 1];
            EgarchMath.FilterOneStepAhead(r, theta, 1, 1, fit.Initialization, sigma);
            Assert.All(sigma, v => Assert.True(double.IsFinite(v) && v > 0.0));
        }

        [Fact]
        public void FilterOneStepAhead_UsesOnlyEarlierReturns()
        {
            double[] r = Series(300, 5, 0.0, 0.02, 0.15, -0.08, 0.96);
            Span<double> theta = stackalloc double[] { 0.0, 0.02, 0.15, -0.08, 0.96 };
            Assert.True(EgarchMath.TryCreateInitialization(r, out var init));

            var full = new double[r.Length + 1];
            EgarchMath.FilterOneStepAhead(r, theta, 1, 1, init, full);
            var truncated = new double[201];
            EgarchMath.FilterOneStepAhead(r.AsSpan(0, 200), theta, 1, 1, init, truncated);

            for (int t = 0; t <= 200; t++)
            {
                Assert.Equal(full[t], truncated[t]);
            }
        }

        [Fact]
        public void FilterOneStepAhead_HandComputedThreeBars()
        {
            // theta = mu 0, omega 0, alpha 0.1, gamma -0.05, beta 0.5; backcast variance 1 (ln = 0).
            var init = new EgarchInitialization(LogBackcast: 0.0, LogMeanSquare: 0.0, MinVariance: 1e-12, MaxVariance: 1e12);
            double[] r = { 2.0, -1.0 };
            var sigma = new double[3];
            EgarchMath.FilterOneStepAhead(r, new[] { 0.0, 0.0, 0.1, -0.05, 0.5 }, 1, 1, init, sigma);

            double c = Math.Sqrt(2.0 / Math.PI);
            double ln0 = 0.5 * 0.0;                       // omega + beta * ln(backcast)
            double z0 = 2.0 / Math.Exp(0.5 * ln0);        // = 2
            double ln1 = 0.1 * (Math.Abs(z0) - c) - 0.05 * z0 + 0.5 * ln0;
            double z1 = -1.0 / Math.Exp(0.5 * ln1);
            double ln2 = 0.1 * (Math.Abs(z1) - c) - 0.05 * z1 + 0.5 * ln1;

            Assert.Equal(Math.Exp(0.5 * ln0), sigma[0], 12);
            Assert.Equal(Math.Exp(0.5 * ln1), sigma[1], 12);
            Assert.Equal(Math.Exp(0.5 * ln2), sigma[2], 12);
        }

        [Fact]
        public void Fit_ConstantSeries_Fails()
        {
            var r = new double[100];
            Array.Fill(r, 0.5);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(1, 1)];

            Assert.False(EgarchMath.Fit(r, 1, 1, theta).Success);
        }

        [Fact]
        public void Fit_NonFiniteReturn_Fails()
        {
            double[] r = Series(100, 3, 0, 0.02, 0.15, -0.08, 0.96);
            r[50] = double.NaN;
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(1, 1)];

            Assert.False(EgarchMath.Fit(r, 1, 1, theta).Success);
        }

        [Fact]
        public void Fit_InvalidOrder_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                Span<double> theta = stackalloc double[EgarchMath.ParameterCount(0, 1)];
                EgarchMath.Fit(new double[100], 0, 1, theta);
            });
        }

        [Fact]
        public void PercentLogReturn_IsPercentAndRejectsNonPositivePrices()
        {
            Assert.Equal(100.0 * Math.Log(1.1), EgarchMath.PercentLogReturn(100.0, 110.0), 12);
            Assert.True(double.IsNaN(EgarchMath.PercentLogReturn(0.0, 1.0)));
            Assert.True(double.IsNaN(EgarchMath.PercentLogReturn(1.0, -1.0)));
        }

        [Fact]
        public void Fit_HigherOrders_ProducesUsableFit()
        {
            double[] r = Series(600, 99, 0.03, 0.02, 0.15, -0.08, 0.96);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(2, 2)];
            var fit = EgarchMath.Fit(r, 2, 2, theta);

            Assert.True(fit.Success);
            Assert.True(double.IsFinite(fit.LogLikelihood));
        }

        [Fact]
        public void FilterOneStepAhead_HealthySeries_ReportsNoGuardTouch()
        {
            double[] r = Series(400, 5, 0.03, 0.02, 0.15, -0.08, 0.95);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(1, 1)];
            var fit = EgarchMath.Fit(r, 1, 1, theta);
            Assert.True(fit.Success);

            var sigma = new double[r.Length + 1];
            EgarchMath.FilterOneStepAhead(r, theta, 1, 1, fit.Initialization, sigma, 0, out int firstGuardedIndex);

            Assert.Equal(-1, firstGuardedIndex);
        }

        [Fact]
        public void FilterOneStepAhead_CollapseThenShock_ReportsTheFirstGuardedStep_AndItIsCausal()
        {
            // alpha < 0 < gamma: every negative shock lowers ln sigma^2 by 1.5 |z|, so a run of them collapses sigma to the lower guard;
            // the next positive shock then has z in the thousands and the variance runs into the upper guard.
            double[] window = Series(250, 8, 0.0, 0.02, 0.15, -0.08, 0.95);
            Assert.True(EgarchMath.TryCreateInitialization(window, out var init));

            var returns = new double[window.Length + 31];
            window.CopyTo(returns, 0);
            for (int i = window.Length; i < returns.Length - 1; i++)
            {
                returns[i] = -2.0;
            }
            returns[^1] = 6.0;

            double[] theta = { 0.0, 0.0, -0.5, 1.0, 0.9 };
            var sigma = new double[returns.Length + 1];
            EgarchMath.FilterOneStepAhead(returns, theta, 1, 1, init, sigma, window.Length, out int firstGuardedIndex);

            Assert.True(firstGuardedIndex >= window.Length && firstGuardedIndex < returns.Length, $"firstGuardedIndex={firstGuardedIndex}");
            Assert.True(sigma[returns.Length] > Math.Sqrt(init.MaxVariance) * 0.99); // the shock bar's successor runs into the upper guard

            // Causal: the first guarded step depends only on returns before it, so cutting the series right there finds the same step.
            var cutSigma = new double[firstGuardedIndex + 1];
            EgarchMath.FilterOneStepAhead(returns.AsSpan(0, firstGuardedIndex), theta, 1, 1, init, cutSigma, window.Length, out int cutFirst);
            Assert.Equal(firstGuardedIndex, cutFirst);
            Assert.Equal(sigma[firstGuardedIndex], cutSigma[firstGuardedIndex]);

            // A guard before guardCheckFrom is not reported.
            var laterSigma = new double[returns.Length + 1];
            EgarchMath.FilterOneStepAhead(returns, theta, 1, 1, init, laterSigma, returns.Length + 1, out int none);
            Assert.Equal(-1, none);
        }

        [Fact]
        public void FilterOneStepAhead_OldOverload_GivesTheSameSigma()
        {
            double[] r = Series(300, 6, 0.03, 0.02, 0.15, -0.08, 0.95);
            Span<double> theta = stackalloc double[EgarchMath.ParameterCount(1, 1)];
            var fit = EgarchMath.Fit(r, 1, 1, theta);

            var a = new double[r.Length + 1];
            var b = new double[r.Length + 1];
            EgarchMath.FilterOneStepAhead(r, theta, 1, 1, fit.Initialization, a);
            EgarchMath.FilterOneStepAhead(r, theta, 1, 1, fit.Initialization, b, 0, out _);

            Assert.Equal(a, b);
        }

        [Fact]
        public void IsSigmaPlausible_UsesTheWindowRootMeanSquareAsTheScale()
        {
            double[] window = Series(250, 21, 0.0, 0.02, 0.15, -0.08, 0.95);
            Assert.True(EgarchMath.TryCreateInitialization(window, out var init));
            double scale = Math.Exp(0.5 * init.LogMeanSquare);

            Assert.True(EgarchMath.IsSigmaPlausible(scale, init, 0.01, 50.0));
            Assert.True(EgarchMath.IsSigmaPlausible(0.011 * scale, init, 0.01, 50.0));
            Assert.False(EgarchMath.IsSigmaPlausible(0.009 * scale, init, 0.01, 50.0));
            Assert.True(EgarchMath.IsSigmaPlausible(49.0 * scale, init, 0.01, 50.0));
            Assert.False(EgarchMath.IsSigmaPlausible(51.0 * scale, init, 0.01, 50.0));
            Assert.False(EgarchMath.IsSigmaPlausible(double.NaN, init, 0.0, 1e5));
        }

        [Fact]
        public void IsSigmaPlausible_MinimumOfZeroSwitchesTheLowerLimitOff()
        {
            double[] window = Series(250, 22, 0.0, 0.02, 0.15, -0.08, 0.95);
            Assert.True(EgarchMath.TryCreateInitialization(window, out var init));

            Assert.True(EgarchMath.IsSigmaPlausible(Math.Sqrt(init.MinVariance), init, 0.0, 50.0));
            Assert.False(EgarchMath.IsSigmaPlausible(Math.Sqrt(init.MinVariance), init, 0.01, 50.0));
        }
    }
}
