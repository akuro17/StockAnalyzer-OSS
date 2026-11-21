using System;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils
{
    /// <summary>
    /// Golden values were produced by the previous NumPy/SciPy implementation in server.py
    /// (calculate_mesa / calculate_fourier_transform / calculate_fft_cycle) on <see cref="ReferenceSeries"/>.
    /// </summary>
    public class SignalMathTests
    {
        private const int Length = 200;
        private const double RelativeTolerance = 1e-9;

        private static double[] ReferenceSeries()
        {
            var p = new double[Length];
            for (int i = 0; i < Length; i++)
            {
                p[i] = 100.0 + 10.0 * Math.Sin(2 * Math.PI * i / 16.0) + 0.05 * i + 3.0 * Math.Sin(2 * Math.PI * i / 7.0);
            }
            return p;
        }

        private static void AssertClose(double expected, double actual)
            => Assert.True(Math.Abs(expected - actual) <= RelativeTolerance * Math.Max(1.0, Math.Abs(expected)), $"expected {expected}, actual {actual}");

        [Fact]
        public void Mesa_MatchesReferenceImplementation()
        {
            var mama = new double[Length];
            var fama = new double[Length];
            MesaMath.CalculateMesa(ReferenceSeries(), 0.5, 0.05, mama, fama);

            AssertClose(99.02481689370177, mama[31]);
            AssertClose(101.0592699314565, mama[50]);
            AssertClose(105.3233606474809, mama[100]);
            AssertClose(113.53858067892502, mama[199]);
            AssertClose(99.02481689370177, fama[31]);
            AssertClose(100.05417914357918, fama[50]);
            AssertClose(103.00178736986493, fama[100]);
            AssertClose(108.7959841924193, fama[199]);
        }

        [Fact]
        public void Mesa_BarsBeforeFirstFullWindow_CarryThePrice()
        {
            var prices = ReferenceSeries();
            var mama = new double[Length];
            var fama = new double[Length];
            MesaMath.CalculateMesa(prices, 0.5, 0.05, mama, fama);

            for (int i = 0; i < MesaMath.WindowSize - 1; i++)
            {
                Assert.Equal(prices[i], mama[i]);
                Assert.Equal(prices[i], fama[i]);
            }
        }

        [Fact]
        public void Mesa_SeriesShorterThanWindow_ReturnsPrices()
        {
            var prices = new[] { 1.0, 2.0, 3.0 };
            var mama = new double[3];
            var fama = new double[3];
            MesaMath.CalculateMesa(prices, 0.5, 0.05, mama, fama);

            Assert.Equal(prices, mama);
            Assert.Equal(prices, fama);
        }

        [Fact]
        public void Mesa_OutputShorterThanInput_Throws()
        {
            Assert.Throws<ArgumentException>(() => MesaMath.CalculateMesa(new double[40], 0.5, 0.05, new double[39], new double[40]));
        }

        [Fact]
        public void Goertzel_MatchesReferenceImplementation()
        {
            var amplitude = new double[Length];
            GoertzelMath.CalculateRollingAmplitude(ReferenceSeries(), 16, amplitude);

            AssertClose(9.988138704639903, amplitude[15]);
            AssertClose(10.330218472646358, amplitude[50]);
            AssertClose(10.829418064377002, amplitude[100]);
            AssertClose(10.601915161914548, amplitude[199]);
            Assert.True(double.IsNaN(amplitude[14]));
        }

        private static readonly double[] GoertzelSeries = [1.5, -2.0, 0.5, 4.0, -1.0, 2.5, 0.0, 3.25, -0.75, 1.0, 2.0, -3.5];

        /// <summary>Period 2: bin 1 is the Nyquist bin, which a one-sided spectrum does not double (an alternating +-a series has amplitude a, not 2a).</summary>
        [Fact]
        public void Goertzel_PeriodTwo_IsTheNyquistAmplitude()
        {
            var alternating = new double[4];
            GoertzelMath.CalculateRollingAmplitude([1.0, -1.0, 1.0, -1.0], 2, alternating);
            Assert.True(double.IsNaN(alternating[0]));
            Assert.Equal(1.0, alternating[1], 12);
            Assert.Equal(1.0, alternating[3], 12);

            var scaled = new double[2];
            GoertzelMath.CalculateRollingAmplitude([2.0, -2.0], 2, scaled);
            Assert.Equal(2.0, scaled[1], 12);

            // A window [x0, x1] with its mean removed is +-(x0 - x1) / 2.
            var amplitude = new double[GoertzelSeries.Length];
            GoertzelMath.CalculateRollingAmplitude(GoertzelSeries, 2, amplitude);
            for (int t = 1; t < GoertzelSeries.Length; t++)
            {
                Assert.Equal(Math.Abs(GoertzelSeries[t] - GoertzelSeries[t - 1]) / 2.0, amplitude[t], 12);
            }
        }

        /// <summary>The amplitude is the one-sided DFT amplitude c * |X1| / P of the mean-removed window (c = 2, and 1 for the Nyquist bin of period 2).</summary>
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void Goertzel_MatchesTheOneSidedDftAmplitude(int period)
        {
            var amplitude = new double[GoertzelSeries.Length];
            GoertzelMath.CalculateRollingAmplitude(GoertzelSeries, period, amplitude);

            double singleSided = period == 2 ? 1.0 : 2.0;
            for (int t = period - 1; t < GoertzelSeries.Length; t++)
            {
                double[] window = GoertzelSeries[(t - period + 1)..(t + 1)];
                double mean = window.Average();
                double[] centered = window.Select(v => v - mean).ToArray();
                (double[] real, double[] imag) = StockAnalyzer.Core.Tests.Analysis.DiscreteFourierTransformReference.Forward(centered);
                double expected = singleSided * Math.Sqrt((real[1] * real[1]) + (imag[1] * imag[1])) / period;

                Assert.Equal(expected, amplitude[t], 10);
            }
        }

        /// <summary>Periods above 2 are untouched: these values were captured from the kernel before the period-2 fix and must stay bit-identical.</summary>
        [Fact]
        public void Goertzel_PeriodsAboveTwo_AreBitIdenticalToTheirPreviousValues()
        {
            double[] period3 = [2.081665999466132, 3.4801021696368504, 2.962731472438529, 2.962731472438529, 2.081665999466132, 1.964971020425266, 2.4551533104427055, 2.315407331574967, 1.6072751268321595, 3.3829638550307397];
            double[] period4 = [3.0413812651491097, 3.092329219213245, 1.0606601717798212, 0.9013878188659974, 0.6250000000000001, 0.5303300858899106, 1.1858541225631423, 1.7765838004439867, 2.6368778887161235];
            double[] period5 = [1.7186150976731611, 1.4601359756715317, 0.7293812908556515, 1.483857963241476, 1.3225962773793145, 0.6423430534672818, 0.5716368401246185, 0.8344300500868563];

            foreach ((int period, double[] expected) in new[] { (3, period3), (4, period4), (5, period5) })
            {
                var amplitude = new double[GoertzelSeries.Length];
                GoertzelMath.CalculateRollingAmplitude(GoertzelSeries, period, amplitude);
                Assert.Equal(expected, amplitude[(period - 1)..]);
            }
        }

        [Fact]
        public void Goertzel_SeriesShorterThanPeriod_IsAllNaN()
        {
            var amplitude = new double[5];
            GoertzelMath.CalculateRollingAmplitude(new double[5], 20, amplitude);

            Assert.All(amplitude, v => Assert.True(double.IsNaN(v)));
        }

        [Fact]
        public void FftCycle_MatchesReferenceImplementation()
        {
            var cycle = new double[Length];
            var strength = new double[Length];
            var oscillator = new double[Length];
            FftCycleMath.CalculateRollingFftCycle(ReferenceSeries(), 64, cycle, strength, oscillator);

            AssertClose(16.0, cycle[63]);
            AssertClose(16.0, cycle[199]);
            AssertClose(11.666591680493607, strength[63]);
            AssertClose(11.643028504607349, strength[100]);
            AssertClose(11.62773123203451, strength[199]);
            AssertClose(4.549524986977461, oscillator[100]);
            Assert.Equal(0.0011083987576157345, oscillator[63], 9);
            Assert.True(double.IsNaN(cycle[62]));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(3, 2)]
        [InlineData(5, 4)]
        [InlineData(6, 4)]
        [InlineData(24, 16)]
        [InlineData(64, 64)]
        [InlineData(100, 128)]
        public void FftCycle_NearestPowerOfTwo_TiesRoundDown(int requested, int expected)
        {
            Assert.Equal(expected, FftCycleMath.NearestPowerOfTwo(requested));
        }

        [Fact]
        public void FftCycle_WindowBelowMinimum_IsAllNaN()
        {
            var cycle = new double[10];
            var strength = new double[10];
            var oscillator = new double[10];
            FftCycleMath.CalculateRollingFftCycle(new double[10], 2, cycle, strength, oscillator);

            Assert.All(cycle, v => Assert.True(double.IsNaN(v)));
            Assert.All(strength, v => Assert.True(double.IsNaN(v)));
        }

        [Fact]
        public void FftCycle_FlatSeries_HasNoStrength()
        {
            var samples = new double[20];
            Array.Fill(samples, 5.0);
            var cycle = new double[20];
            var strength = new double[20];
            var oscillator = new double[20];
            FftCycleMath.CalculateRollingFftCycle(samples, 8, cycle, strength, oscillator);

            Assert.True(double.IsNaN(strength[19]));
            Assert.Equal(0.0, oscillator[19], 12);
        }
    }
}
