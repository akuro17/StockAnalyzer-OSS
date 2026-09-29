using System;
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
