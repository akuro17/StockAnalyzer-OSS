using System;
using System.IO;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils
{
    public class DtwMathTests
    {
        private const int Precision = 10;

        [Fact]
        public void Calculate_IdenticalSeries_ReturnsZero()
        {
            double[] p = { 1, 2, 3, 4, 5 };
            Assert.Equal(0.0, DtwMath.Calculate(p, p), Precision);
        }

        [Fact]
        public void Calculate_SingleElementSeries_ReturnsAbsoluteDifference()
        {
            Assert.Equal(2.0, DtwMath.Calculate(new[] { 3.0 }, new[] { 5.0 }), Precision);
        }

        [Fact]
        public void Calculate_KnownSeries_MatchesHandDerivedAccumulatedCost()
        {
            // Accumulated squared cost D(4,4) = 2 (path 1-1, 3-2, 3-3, 4-3, 8-8), so DTW = sqrt(2).
            double[] p = { 1, 3, 4, 8 };
            double[] q = { 1, 2, 3, 8 };
            Assert.Equal(Math.Sqrt(2.0), DtwMath.Calculate(p, q), Precision);
        }

        [Fact]
        public void Calculate_IsSymmetric()
        {
            double[] p = { 1, 3, 4, 8, 2 };
            double[] q = { 1, 2, 3, 8 };
            Assert.Equal(DtwMath.Calculate(p, q), DtwMath.Calculate(q, p), Precision);
        }

        [Fact]
        public void Calculate_TimeShiftedSeries_IsSmallerThanEuclidean()
        {
            double[] p = { 0, 1, 2, 1, 0 };
            double[] q = { 1, 2, 1, 0, 0 };
            double euclidean = 0;
            for (int i = 0; i < p.Length; i++) euclidean += (p[i] - q[i]) * (p[i] - q[i]);

            Assert.True(DtwMath.Calculate(p, q) < Math.Sqrt(euclidean));
        }

        [Fact]
        public void Calculate_StretchedSeries_ReturnsZero()
        {
            double[] p = { 1, 2, 3 };
            double[] q = { 1, 1, 2, 2, 3, 3 };
            Assert.Equal(0.0, DtwMath.Calculate(p, q), Precision);
        }

        [Fact]
        public void Calculate_SingleElementAgainstSequence_SumsSquaredDifferences()
        {
            Assert.Equal(Math.Sqrt(2.0), DtwMath.Calculate(new[] { 2.0 }, new[] { 1.0, 3.0 }), Precision);
        }

        [Fact]
        public void Calculate_SakoeChibaBand_IsNotSmallerThanUnconstrained()
        {
            double[] p = { 0, 0, 0, 5, 0, 0, 0, 0 };
            double[] q = { 0, 5, 0, 0, 0, 0, 0, 0 };

            double free = DtwMath.Calculate(p, q);
            double banded = DtwMath.Calculate(p, q, sakoeChibaRadius: 1);

            Assert.True(banded >= free);
            Assert.False(double.IsInfinity(banded));
        }

        [Fact]
        public void Calculate_LargeRadius_EqualsUnconstrained()
        {
            double[] p = { 1, 3, 4, 8, 2, 7 };
            double[] q = { 1, 2, 3, 8, 6 };
            Assert.Equal(DtwMath.Calculate(p, q), DtwMath.Calculate(p, q, sakoeChibaRadius: 100), Precision);
        }

        [Fact]
        public void Calculate_LengthMismatchWithBand_ReturnsFiniteValue()
        {
            double[] p = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            double[] q = { 1, 3, 5, 7, 10 };
            Assert.False(double.IsInfinity(DtwMath.Calculate(p, q, sakoeChibaRadius: 1)));
        }

        [Fact]
        public void Calculate_UnequalLengthsWithRadiusOne_MatchesHandDerivedBandedCost()
        {
            // Rows follow the longer series (6), columns the shorter (3); band centre = floor(i * 2 / 5) = 0,0,0,1,1,2.
            // Best in-band path 0-0, 1-0, 2-1, 3-1, 4-2, 5-2 costs 0 + 1 + 0 + 1 + 0 + 1 = 3.
            double[] p = { 0, 1, 2, 3, 4, 5 };
            double[] q = { 0, 2, 4 };

            Assert.Equal(Math.Sqrt(3.0), DtwMath.Calculate(p, q, sakoeChibaRadius: 1), Precision);
            Assert.Equal(Math.Sqrt(3.0), DtwMath.Calculate(q, p, sakoeChibaRadius: 1), Precision);
        }

        [Fact]
        public void Calculate_ExtremeLengthRatioWithRadiusOne_IsFiniteAndSymmetric()
        {
            double[] longSeries = new double[200];
            for (int i = 0; i < longSeries.Length; i++) longSeries[i] = i;
            double[] shortSeries = { 0, 100, 199 };

            double forward = DtwMath.Calculate(longSeries, shortSeries, sakoeChibaRadius: 1);
            double backward = DtwMath.Calculate(shortSeries, longSeries, sakoeChibaRadius: 1);

            Assert.False(double.IsInfinity(forward));
            Assert.False(double.IsNaN(forward));
            Assert.Equal(forward, backward, Precision);
        }

        [Fact]
        public void Calculate_ExplicitUnconstrainedRadius_EqualsDefault()
        {
            double[] p = { 1, 3, 4, 8, 2, 7 };
            double[] q = { 1, 2, 3, 8, 6 };
            Assert.Equal(DtwMath.Calculate(p, q), DtwMath.Calculate(p, q, DtwMath.UnconstrainedRadius), Precision);
        }

        [Fact]
        public void Calculate_RadiusZeroWithEqualLengths_EqualsEuclideanDistance()
        {
            double[] p = { 0, 0, 0, 5, 0, 0 };
            double[] q = { 0, 5, 0, 0, 0, 0 };
            double euclidean = 0;
            for (int i = 0; i < p.Length; i++) euclidean += (p[i] - q[i]) * (p[i] - q[i]);

            Assert.Equal(Math.Sqrt(euclidean), DtwMath.Calculate(p, q, sakoeChibaRadius: 0), Precision);
            Assert.True(DtwMath.Calculate(p, q) < Math.Sqrt(euclidean));
        }

        [Fact]
        public void Calculate_RadiusZeroWithUnequalLengths_IsFiniteAndSymmetric()
        {
            // Only the centre cells (columns 0,0,0,1,1,2) are allowed: 0-0, 1-0, 2-0, 3-1, 4-1, 5-2 costs 0 + 1 + 4 + 1 + 4 + 1 = 11.
            double[] p = { 0, 1, 2, 3, 4, 5 };
            double[] q = { 0, 2, 4 };

            Assert.Equal(Math.Sqrt(11.0), DtwMath.Calculate(p, q, sakoeChibaRadius: 0), Precision);
            Assert.Equal(Math.Sqrt(11.0), DtwMath.Calculate(q, p, sakoeChibaRadius: 0), Precision);
        }

        [Fact]
        public void Calculate_LongSeriesBeyondStackThreshold_MatchesIdentity()
        {
            double[] p = new double[600];
            for (int i = 0; i < p.Length; i++) p[i] = Math.Sin(i * 0.1);
            Assert.Equal(0.0, DtwMath.Calculate(p, p), Precision);
        }

        [Fact]
        public void Calculate_NaNInput_ReturnsNaN()
        {
            Assert.True(double.IsNaN(DtwMath.Calculate(new[] { 1.0, double.NaN, 3.0 }, new[] { 1.0, 2.0, 3.0 })));
        }

        [Fact]
        public void Calculate_EmptyInput_Throws()
        {
            Assert.Throws<ArgumentException>(() => DtwMath.Calculate(ReadOnlySpan<double>.Empty, new[] { 1.0 }));
        }

        [Fact]
        public void Calculate_NegativeRadius_Throws()
        {
            Assert.Throws<ArgumentException>(() => DtwMath.Calculate(new[] { 1.0 }, new[] { 1.0 }, DtwMath.UnconstrainedRadius - 1));
        }

        [Fact]
        public void PythonMirror_UnconstrainedRadiusMatchesCSharpConstant()
        {
            string script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "server.py"));
            Match match = Regex.Match(script, @"^DTW_UNCONSTRAINED_RADIUS\s*=\s*(-?\d+)", RegexOptions.Multiline);

            Assert.True(match.Success);
            Assert.Equal(DtwMath.UnconstrainedRadius, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void Calculate_FlatSeries_ReturnsZero()
        {
            double[] p = { 5, 5, 5 };
            Assert.Equal(0.0, DtwMath.Calculate(p, p), Precision);
        }
    }
}
