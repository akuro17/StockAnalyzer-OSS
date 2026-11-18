using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Realized variation of a window of log returns and its split into a continuous and a jump part. Unit of the volatilities: log-return standard deviation PER BAR
/// (a ratio, not a percentage, not annualised); <see cref="JumpRatio"/> is dimensionless in [0, 1].
/// </summary>
/// <param name="BipowerVolatility">sqrt(BPV): the continuous volatility.</param>
/// <param name="TotalVolatility">sqrt(RV): the total volatility.</param>
/// <param name="JumpVolatility">sqrt(J), J = max(0, RV - BPV): the jump component.</param>
/// <param name="JumpRatio">J / RV; NaN when RV is not above <see cref="ReturnStatistics.VarianceEpsilon"/>.</param>
public readonly record struct JumpVariation(double BipowerVolatility, double TotalVolatility, double JumpVolatility, double JumpRatio);

/// <summary>
/// Realized bipower variation (Barndorff-Nielsen and Shephard): <c>RV = (1 / W) sum r_i^2</c>, <c>BPV = (pi / 2) (1 / (W - 1)) sum_{i=2..W} |r_i| |r_(i-1)|</c>,
/// <c>J = max(0, RV - BPV)</c>. <c>double</c> function of dimensionless returns; a window that cannot be evaluated gives <c>false</c>, and the caller narrows with
/// <c>IndicatorCalculationHelper.ToDecimalOrNull</c>. A flat window of zero returns is a defined result (all volatilities 0, jump ratio undefined), not an error.
/// Specification: docs/indicators_formulas.md, record indicators_formulas_0601 (bipower variation).
/// </summary>
public static class JumpVariationMath
{
    /// <summary>Fewest log returns of one evaluation.</summary>
    public const int MinCount = 3;

    /// <summary>The scaling constant of the bipower variation, 1 / mu_1^2 with mu_1 = E|Z| = sqrt(2 / pi), that is pi / 2.</summary>
    public const double BipowerScale = Math.PI / 2.0;

    /// <summary>False for fewer than <see cref="MinCount"/> values or a non-finite value or result.</summary>
    public static bool TryCompute(ReadOnlySpan<double> logReturns, out JumpVariation result)
    {
        result = default;
        int n = logReturns.Length;
        if (n < MinCount)
        {
            return false;
        }

        double squares = 0.0;
        double products = 0.0;
        for (int i = 0; i < n; i++)
        {
            double r = logReturns[i];
            if (!double.IsFinite(r))
            {
                return false;
            }

            squares += r * r;
            if (i > 0)
            {
                products += Math.Abs(r) * Math.Abs(logReturns[i - 1]);
            }
        }

        double realizedVariance = squares / n;
        double bipowerVariance = BipowerScale * products / (n - 1);
        double jump = Math.Max(0.0, realizedVariance - bipowerVariance);
        if (!double.IsFinite(realizedVariance) || !double.IsFinite(bipowerVariance))
        {
            return false;
        }

        double ratio = realizedVariance > ReturnStatistics.VarianceEpsilon ? jump / realizedVariance : double.NaN;
        result = new JumpVariation(Math.Sqrt(bipowerVariance), Math.Sqrt(realizedVariance), Math.Sqrt(jump), ratio);
        return true;
    }
}
