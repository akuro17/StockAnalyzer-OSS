using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Analysis;

public class RegressionService
{
    public struct RegressionResult
    {
        public decimal Slope;
        public decimal Intercept;
        public decimal StdDev;
        public int Count;
        public bool IsValid;

        public decimal GetValueAt(int x) => Slope * x + Intercept;
    }

    /// <summary>
    /// Calculates Linear Regression (Ordinary Least Squares) for the given candles.
    /// X axis is assumed to be 0-based index.
    /// Y axis is Price (Close).
    /// </summary>
    public RegressionResult Calculate(IEnumerable<CoreCandleData> candles)
    {
        var list = candles.ToList();
        int n = list.Count;

        if (n < 2)
        {
            return new RegressionResult { IsValid = false };
        }

        decimal sumX = 0;
        decimal sumY = 0;
        decimal sumXY = 0;
        decimal sumX2 = 0;

        for (int i = 0; i < n; i++)
        {
            decimal x = i;
            decimal y = list[i].Close;

            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        // Slope (m) = (N * ΣXY - ΣX * ΣY) / (N * ΣX^2 - (ΣX)^2)
        decimal denominator = (n * sumX2 - sumX * sumX);
        
        if (denominator == 0)
        {
             return new RegressionResult { IsValid = false };
        }

        decimal slope = (n * sumXY - sumX * sumY) / denominator;
        
        // Intercept (b) = (ΣY - m * ΣX) / N
        decimal intercept = (sumY - slope * sumX) / n;

        // Standard Deviation
        // StdDev = Sqrt( Σ(Y - (mx+b))^2 / N )
        double sumSqErrors = 0;
        for (int i = 0; i < n; i++)
        {
            decimal x = i;
            decimal y = list[i].Close;
            decimal predicted = slope * x + intercept;
            decimal error = y - predicted;
            sumSqErrors += (double)(error * error);
        }

        decimal stdDev = (decimal)Math.Sqrt(sumSqErrors / n);

        return new RegressionResult
        {
            Slope = slope,
            Intercept = intercept,
            StdDev = stdDev,
            Count = n,
            IsValid = true
        };
    }
}
