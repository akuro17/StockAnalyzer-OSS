using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Least squares of a small dense system by Householder QR, in place on caller-provided scratch memory (no allocation).
/// The matrix is row-major <c>rows x columns</c> with <c>rows &gt;= columns</c>. Specification: docs/indicators_formulas.md, record indicators_formulas_0594, subject SmallMatrixMath.
/// </summary>
public static class SmallMatrixMath
{
    /// <summary>Relative pivot tolerance: a pivot <c>|R_kk| &lt; RankTolerance * ||X||_F</c> declares the matrix rank deficient.</summary>
    public const double RankTolerance = 1e-12;

    /// <summary>
    /// Factors <paramref name="matrix"/> in place into <c>Q R</c> (<c>R</c> in the upper triangle, the reflectors below it) and applies <c>Q^T</c> to <paramref name="rightHandSide"/>
    /// (length <paramref name="rows"/>). On success the first <paramref name="columns"/> entries of <paramref name="rightHandSide"/> are <c>(Q^T y)</c> restricted to the range of the
    /// matrix and <paramref name="residualSumOfSquares"/> is the sum of squares of the remaining entries, the least-squares residual <c>|y - X b|^2</c>.
    /// False (outputs NaN) for a non-finite entry, <c>rows &lt; columns</c>, or a pivot below the rank tolerance.
    /// </summary>
    public static bool TryHouseholderQr(Span<double> matrix, int rows, int columns, Span<double> rightHandSide, out double residualSumOfSquares)
    {
        residualSumOfSquares = double.NaN;
        if (columns < 1 || rows < columns || matrix.Length < rows * columns || rightHandSide.Length < rows)
        {
            return false;
        }

        double frobenius = 0.0;
        for (int i = 0; i < rows * columns; i++)
        {
            if (!double.IsFinite(matrix[i]))
            {
                return false;
            }

            frobenius += matrix[i] * matrix[i];
        }

        for (int i = 0; i < rows; i++)
        {
            if (!double.IsFinite(rightHandSide[i]))
            {
                return false;
            }
        }

        frobenius = Math.Sqrt(frobenius);
        double pivotLimit = RankTolerance * frobenius;
        for (int k = 0; k < columns; k++)
        {
            double norm = 0.0;
            for (int i = k; i < rows; i++)
            {
                double value = matrix[i * columns + k];
                norm += value * value;
            }

            norm = Math.Sqrt(norm);
            if (!(norm >= pivotLimit) || !(norm > 0.0))
            {
                return false;
            }

            // Reflector v = x + sign(x_k) ||x|| e_k, stored in place of the column (normalised so that v_k = 1 is not required: applied with beta = 2 / v^T v).
            double alpha = matrix[k * columns + k] >= 0.0 ? -norm : norm;
            double head = matrix[k * columns + k] - alpha;
            double vNormSquared = head * head;
            for (int i = k + 1; i < rows; i++)
            {
                double value = matrix[i * columns + k];
                vNormSquared += value * value;
            }

            if (vNormSquared > 0.0)
            {
                // Apply H = I - 2 v v^T / (v^T v) to the remaining columns and to the right-hand side.
                for (int j = k + 1; j < columns; j++)
                {
                    double dot = head * matrix[k * columns + j];
                    for (int i = k + 1; i < rows; i++)
                    {
                        dot += matrix[i * columns + k] * matrix[i * columns + j];
                    }

                    double factor = 2.0 * dot / vNormSquared;
                    matrix[k * columns + j] -= factor * head;
                    for (int i = k + 1; i < rows; i++)
                    {
                        matrix[i * columns + j] -= factor * matrix[i * columns + k];
                    }
                }

                double rhsDot = head * rightHandSide[k];
                for (int i = k + 1; i < rows; i++)
                {
                    rhsDot += matrix[i * columns + k] * rightHandSide[i];
                }

                double rhsFactor = 2.0 * rhsDot / vNormSquared;
                rightHandSide[k] -= rhsFactor * head;
                for (int i = k + 1; i < rows; i++)
                {
                    rightHandSide[i] -= rhsFactor * matrix[i * columns + k];
                }
            }

            matrix[k * columns + k] = alpha;
            for (int i = k + 1; i < rows; i++)
            {
                matrix[i * columns + k] = 0.0;
            }
        }

        double ssr = 0.0;
        for (int i = columns; i < rows; i++)
        {
            ssr += rightHandSide[i] * rightHandSide[i];
        }

        residualSumOfSquares = ssr;
        return true;
    }

    /// <summary>
    /// Solves <c>R b = c</c> by back-substitution, where <c>R</c> is the upper triangle of the factored <paramref name="matrix"/> (row stride <paramref name="columns"/>)
    /// and <c>c</c> the first <paramref name="columns"/> entries of <paramref name="transformedRightHandSide"/>. The solution goes to <paramref name="solution"/>.
    /// </summary>
    public static void SolveUpperTriangular(ReadOnlySpan<double> matrix, int columns, ReadOnlySpan<double> transformedRightHandSide, Span<double> solution)
    {
        for (int i = columns - 1; i >= 0; i--)
        {
            double sum = transformedRightHandSide[i];
            for (int j = i + 1; j < columns; j++)
            {
                sum -= matrix[i * columns + j] * solution[j];
            }

            solution[i] = sum / matrix[i * columns + i];
        }
    }

    /// <summary>
    /// The diagonal entry <c>[(R^T R)^-1]_jj = sum_k (R^-1)_jk^2</c> of the inverse normal matrix <c>(X^T X)^-1</c>, from the triangular factor alone.
    /// Row <c>j</c> of <c>R^-1</c> is the solution <c>z</c> of <c>R^T z = e_j</c> by forward substitution; <paramref name="scratch"/> needs <paramref name="columns"/> entries.
    /// </summary>
    public static double InverseNormalDiagonal(ReadOnlySpan<double> matrix, int columns, int index, Span<double> scratch)
    {
        double total = 0.0;
        for (int k = index; k < columns; k++)
        {
            double sum = k == index ? 1.0 : 0.0;
            for (int i = index; i < k; i++)
            {
                sum -= matrix[i * columns + k] * scratch[i];
            }

            double z = sum / matrix[k * columns + k];
            scratch[k] = z;
            total += z * z;
        }

        return total;
    }
}
