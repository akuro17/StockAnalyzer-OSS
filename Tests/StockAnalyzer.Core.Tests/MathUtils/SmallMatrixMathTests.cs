using System;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>Pins <see cref="SmallMatrixMath"/> to a hand-solved least-squares problem and to the rank rule of plan section 3.6.</summary>
public class SmallMatrixMathTests
{
    // X = [1 x] with x = 0..3, y = {1, 3, 2, 5}: X'X = [[4, 6], [6, 14]] (det 20), X'y = [11, 22], b = [1.1, 1.1], residuals {-0.1, 0.8, -1.3, 0.6}, SSR 2.7,
    // (X'X)^-1 = [[0.7, -0.3], [-0.3, 0.2]].
    private static double[] Design() => new[] { 1.0, 0.0, 1.0, 1.0, 1.0, 2.0, 1.0, 3.0 };

    [Fact]
    public void HandSolvedProblem_GivesCoefficientsResidualAndInverseDiagonal()
    {
        double[] x = Design();
        double[] y = { 1.0, 3.0, 2.0, 5.0 };
        Assert.True(SmallMatrixMath.TryHouseholderQr(x, 4, 2, y, out double ssr));
        Assert.Equal(2.7, ssr, 12);

        double[] b = new double[2];
        SmallMatrixMath.SolveUpperTriangular(x, 2, y, b);
        Assert.Equal(1.1, b[0], 12);
        Assert.Equal(1.1, b[1], 12);

        double[] scratch = new double[2];
        Assert.Equal(0.7, SmallMatrixMath.InverseNormalDiagonal(x, 2, 0, scratch), 12);
        Assert.Equal(0.2, SmallMatrixMath.InverseNormalDiagonal(x, 2, 1, scratch), 12);
    }

    [Fact]
    public void ExactFit_HasANegligibleResidual()
    {
        double[] x = Design();
        double[] y = { 2.0, 5.0, 8.0, 11.0 }; // 2 + 3 x
        Assert.True(SmallMatrixMath.TryHouseholderQr(x, 4, 2, y, out double ssr));
        Assert.True(ssr < 1e-28);
        double[] b = new double[2];
        SmallMatrixMath.SolveUpperTriangular(x, 2, y, b);
        Assert.Equal(2.0, b[0], 12);
        Assert.Equal(3.0, b[1], 12);
    }

    [Fact]
    public void RankDeficientMatrix_IsRejected()
    {
        // The second column equals the first (a constant): no unique solution.
        double[] x = { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
        double[] y = { 1.0, 2.0, 3.0 };
        Assert.False(SmallMatrixMath.TryHouseholderQr(x, 3, 2, y, out double ssr));
        Assert.True(double.IsNaN(ssr));
        Assert.Equal(1e-12, SmallMatrixMath.RankTolerance);
    }

    [Fact]
    public void BadArguments_AreRejected()
    {
        Assert.False(SmallMatrixMath.TryHouseholderQr(new double[2], 1, 2, new double[1], out _)); // fewer rows than columns
        Assert.False(SmallMatrixMath.TryHouseholderQr(new double[3], 2, 2, new double[2], out _)); // matrix too short
        double[] x = Design();
        x[3] = double.NaN;
        Assert.False(SmallMatrixMath.TryHouseholderQr(x, 4, 2, new[] { 1.0, 2.0, 3.0, 4.0 }, out _));
        Assert.False(SmallMatrixMath.TryHouseholderQr(Design(), 4, 2, new[] { 1.0, double.PositiveInfinity, 3.0, 4.0 }, out _));
    }

    [Fact]
    public void Kernel_AllocatesNothing()
    {
        double[] x = new double[8];
        double[] y = new double[4];
        double[] b = new double[2];
        double[] scratch = new double[2];
        double[] design = Design();
        void Run()
        {
            design.CopyTo(x, 0);
            y[0] = 1; y[1] = 3; y[2] = 2; y[3] = 5;
            SmallMatrixMath.TryHouseholderQr(x, 4, 2, y, out _);
            SmallMatrixMath.SolveUpperTriangular(x, 2, y, b);
            SmallMatrixMath.InverseNormalDiagonal(x, 2, 1, scratch);
        }

        Run();
        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                Run();
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
