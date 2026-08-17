namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Parameters;

[StockAnalyzerIndicator(IndicatorType.BSMA)]
public sealed class CoreBSmaIndicator : CoreIndicatorBase
{
    public const int MinPeriod = 2;
    public const int MaxPeriod = 500;

    private int _period = 14;
    public int Period
    {
        get => _period;
        set => _period = Math.Clamp(value, MinPeriod, MaxPeriod);
    }

    private int _degree = 3;
    public int Degree
    {
        get => _degree;
        set => _degree = Math.Clamp(value, 1, 5);
    }

    private double _offset = 0.85;
    public double Offset
    {
        get => _offset;
        set => _offset = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.85;
    }

    private double _sigma = 6.0;
    public double Sigma
    {
        get => _sigma;
        set => _sigma = double.IsFinite(value) ? Math.Clamp(value, 0.5, 20.0) : 6.0;
    }

    public override string Name => $"BSMA ({Period}, {Degree}, {Offset:F2}, {Sigma:F1})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBSmaParameter p)
        {
            p.Validate();
            Period = p.Period;
            Degree = p.Degree;
            Offset = p.Offset;
            Sigma = p.Sigma;
        }
    }

    public override CoreIndicatorSettings GetDefaultSettings()
    {
        return new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.BSMA,
            IsEnabled = true,
            Category = CoreIndicatorCategory.Trend,
            Color = IndicatorDefaultConstants.DodgerBlue,
            Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
            Style = CoreLineStyle.Solid,
            IsOverlay = true,
            ParameterObject = new CoreBSmaParameter
            {
                Period = Period,
                Degree = Degree,
                Offset = Offset,
                Sigma = Sigma
            }
        };
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        int count = candles?.Count ?? 0;

        if (_values.Capacity < count)
        {
            _values.Capacity = count;
        }
        _values.Clear();

        if (count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        if (count < Period)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        // 1. Stackalloc weights (Guaranteed <= 500, Zero Heap Allocation)
        Span<double> weights = stackalloc double[Period];
        ComputeNormalizedWeights(Period, Degree, Offset, Sigma, weights);

        // 2. Pad leading bars with null
        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        // 3. Sliding Window Dot Product (Zero-Allocation Hot Loop)
        for (int i = Period - 1; i < count; i++)
        {
            decimal sum = 0m;
            int startIdx = i - Period + 1;

            for (int j = 0; j < Period; j++)
            {
                sum += candles![startIdx + j].Close * (decimal)weights[j];
            }

            _values.Add(sum);
        }

        return IndicatorResult.Success(_values);
    }

    public static void ComputeNormalizedWeights(int period, int degree, double offset, double sigma, Span<double> destination)
    {
        if (destination.Length < period)
            throw new ArgumentException($"Destination span length ({destination.Length}) is less than period ({period}).");

        double m = offset * (period - 1);
        double s = period / sigma;
        if (s <= 1e-12) s = 1.0;

        double sum = 0.0;
        for (int j = 0; j < period; j++)
        {
            double z = (j - m) / s;
            double w = EvaluateBasisSplineKernel(degree, z);
            destination[j] = w;
            sum += w;
        }

        if (sum > 1e-12)
        {
            double invSum = 1.0 / sum;
            for (int j = 0; j < period; j++)
            {
                destination[j] *= invSum;
            }
        }
        else
        {
            destination.Slice(0, period).Clear();
            if (offset >= 0.5)
            {
                destination[period - 1] = 1.0;
            }
            else
            {
                destination[0] = 1.0;
            }
        }
    }

    public static double EvaluateBasisSplineKernel(int degree, double z)
    {
        if (degree < 1 || degree > 5)
            throw new ArgumentOutOfRangeException(nameof(degree), "Degree must be between 1 and 5.");

        double absZ = Math.Abs(z);

        switch (degree)
        {
            case 1:
                return absZ < 1.0 ? 1.0 - absZ : 0.0;

            case 2:
                if (absZ < 0.5) return 0.75 - absZ * absZ;
                if (absZ < 1.5)
                {
                    double d = 1.5 - absZ;
                    return 0.5 * d * d;
                }
                return 0.0;

            case 3:
                if (absZ < 1.0) return (2.0 / 3.0) - absZ * absZ + 0.5 * absZ * absZ * absZ;
                if (absZ < 2.0)
                {
                    double d = 2.0 - absZ;
                    return (1.0 / 6.0) * d * d * d;
                }
                return 0.0;

            case 4:
                return EvaluateTruncatedPower(4, z);

            case 5:
                return EvaluateTruncatedPower(5, z);

            default:
                return 0.0;
        }
    }

    private static double EvaluateTruncatedPower(int p, double z)
    {
        double halfSupport = (p + 1) * 0.5;
        if (Math.Abs(z) >= halfSupport) return 0.0;

        double sum = 0.0;
        int n = p + 1;
        double factor = 1.0;
        for (int i = 1; i <= p; i++) factor *= i; // p!
        double invFactor = 1.0 / factor;

        for (int k = 0; k <= n; k++)
        {
            double baseVal = z + halfSupport - k;
            if (baseVal > 0.0)
            {
                double term = Math.Pow(baseVal, p);
                double binom = BinomialCoefficient(n, k);
                if ((k & 1) == 1)
                {
                    sum -= binom * term;
                }
                else
                {
                    sum += binom * term;
                }
            }
        }

        return Math.Max(0.0, sum * invFactor);
    }

    private static double BinomialCoefficient(int n, int k)
    {
        if (k < 0 || k > n) return 0.0;
        if (k == 0 || k == n) return 1.0;
        double result = 1.0;
        for (int i = 1; i <= k; i++)
        {
            result = result * (n - (k - i)) / i;
        }
        return result;
    }
}
