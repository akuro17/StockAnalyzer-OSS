using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SsaDecompositionEngineTests
{
    [Fact]
    public void Detrend_LeastSquares_LinearTrend_ExtractsExactSlopeAndIntercept()
    {
        int n = 20;
        double[] input = new double[n];
        double trueSlope = 2.5;
        double trueIntercept = 10.0;
        for (int i = 0; i < n; i++)
        {
            input[i] = trueIntercept + trueSlope * i;
        }

        double[] dest = new double[n];
        SsaDecompositionEngine.Detrend(input, dest, SsaDetrendMode.LeastSquaresLinear, out double slope, out double intercept);

        Assert.Equal(trueSlope, slope, 6);
        Assert.Equal(trueIntercept, intercept, 6);
        // All detrended residuals should be zero (within tolerance)
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0.0, dest[i], 6);
        }
    }

    [Fact]
    public void Detrend_DegenerateSeries_ZerosOutResiduals()
    {
        int n = 25;
        double[] input = Enumerable.Repeat(50.0, n).ToArray();
        // Perturb by sub-epsilon noise
        input[5] += 1e-12;

        double[] dest = new double[n];
        SsaDecompositionEngine.Detrend(input, dest, SsaDetrendMode.LeastSquaresLinear, out double slope, out double intercept);

        Assert.Equal(0.0, slope, 6);
        Assert.Equal(50.0, intercept, 6);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0.0, dest[i]);
        }
    }

    [Fact]
    public void ComputeJacobiEigensystem_DiagonalMatrix_ReturnsExactEigenvalues()
    {
        double[,] diag = new double[,]
        {
            { 5.0, 0.0, 0.0 },
            { 0.0, 3.0, 0.0 },
            { 0.0, 0.0, 1.0 }
        };

        double[] d = new double[3];
        double[,] v = new double[3, 3];
        SsaDecompositionEngine.ComputeJacobiEigensystem(diag, 3, d, v);

        Assert.Equal(5.0, d[0], 6);
        Assert.Equal(3.0, d[1], 6);
        Assert.Equal(1.0, d[2], 6);
    }

    [Fact]
    public void ReconstructGroup_PureSineWave_ReconstructsSignalAccurately()
    {
        int n = 48;
        double period = 12.0;
        double amplitude = 10.0;
        double[] sine = new double[n];
        for (int i = 0; i < n; i++)
        {
            sine[i] = amplitude * Math.Sin(2.0 * Math.PI * i / period);
        }

        int l = 12;
        int k = n - l + 1;
        double[,] sMatrix = new double[l, l];
        SsaDecompositionEngine.BuildLagCovarianceMatrix(sine, l, k, sMatrix);

        double[] eigenvalues = new double[l];
        double[,] eigenvectors = new double[l, l];
        SsaDecompositionEngine.ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

        int[] sortedIndices = Enumerable.Range(0, l)
            .OrderByDescending(idx => eigenvalues[idx])
            .ToArray();

        double[] recon = new double[n];
        // Select leading pair of components (r = 2) for pure sine
        SsaDecompositionEngine.ReconstructGroup(sine, l, k, sortedIndices.AsSpan(0, 2), eigenvectors, recon);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(sine[i], recon[i], 1); // Close to true signal
        }
    }

    [Fact]
    public void ComputeCausalEndpoint_PureSineWave_MatchesSignalEndpoint()
    {
        int n = 48;
        double period = 12.0;
        double amplitude = 10.0;
        double mean = 100.0;
        double[] signal = new double[n];
        for (int i = 0; i < n; i++)
        {
            signal[i] = mean + amplitude * Math.Sin(2.0 * Math.PI * i / period);
        }

        double endpoint = SsaDecompositionEngine.ComputeCausalEndpoint(
            signal,
            embeddingDimension: 12,
            numComponents: 2,
            detrendMode: SsaDetrendMode.None);

        Assert.False(double.IsNaN(endpoint));
        Assert.Equal(signal[^1], endpoint, 1);
    }

    [Fact]
    public void ComputeCausalEndpoint_FlatSeries_ReturnsConstantValue()
    {
        int n = 30;
        double[] flat = Enumerable.Repeat(150.0, n).ToArray();

        double endpoint = SsaDecompositionEngine.ComputeCausalEndpoint(
            flat,
            embeddingDimension: 10,
            numComponents: 2,
            detrendMode: SsaDetrendMode.LeastSquaresLinear);

        Assert.Equal(150.0, endpoint, 4);
    }

    [Fact]
    public void Decompose_PureSineWave_ReturnsValidComponentEnergiesAndEffectiveRank()
    {
        int n = 48;
        double period = 12.0;
        double amplitude = 10.0;
        double[] sine = new double[n];
        for (int i = 0; i < n; i++)
        {
            sine[i] = amplitude * Math.Sin(2.0 * Math.PI * i / period);
        }

        var result = SsaDecompositionEngine.Decompose(sine, embeddingDimension: 12, SsaDetrendMode.LeastSquaresLinear);

        Assert.False(result.IsDegenerate);
        Assert.True(result.EffectiveRank >= 2);
        Assert.Equal(12, result.Eigenvalues.Length);
        Assert.Equal(12, result.SingularValues.Length);
        Assert.Equal(12, result.ComponentEnergies.Length);

        // Sum of component energies should equal 1.0 (within float tolerance)
        double totalEnergy = result.ComponentEnergies.Sum();
        Assert.Equal(1.0, totalEnergy, 4);

        // Leading 2 components for pure sine should carry majority of variance (> 80%)
        double leadingVariance = result.ComponentEnergies[0] + result.ComponentEnergies[1];
        Assert.True(leadingVariance > 0.80);
    }

    [Fact]
    public void ComputeJacobiEigensystem_DeterministicSignNormalization_EnforcesNonNegativeFirstRow()
    {
        double[,] matrix = new double[,]
        {
            { 4.0, 1.0, 2.0 },
            { 1.0, 5.0, 3.0 },
            { 2.0, 3.0, 6.0 }
        };

        double[] d = new double[3];
        double[,] v = new double[3, 3];
        SsaDecompositionEngine.ComputeJacobiEigensystem(matrix, 3, d, v);

        for (int j = 0; j < 3; j++)
        {
            Assert.True(v[0, j] >= 0.0, $"Eigenvector column {j} should have non-negative first element, got {v[0, j]}");
            Assert.True(d[j] >= 0.0, $"Eigenvalue {j} should be non-negative, got {d[j]}");
        }
    }
}
