using System;

namespace StockAnalyzer.Core.Utilities
{
    public static class RoundingHelper
    {
        // "Nice Numbers" for chart scaling: 1, 2, 5, 10, 20, 50, 100, etc.
        public static decimal RoundToNiceNumber(decimal number)
        {
            if (number <= 0) return 1m;

            // Find magnitude (power of 10)
            decimal magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)number)));
            decimal normalized = number / magnitude;

            decimal niceNormalized;
            // Refined steps: 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 10
            // Or simpler: 1, 2, 2.5, 3 (300), 4 (400), 5
            if (normalized < 1.5m) niceNormalized = 1m;
            else if (normalized < 2.3m) niceNormalized = 2m;
            else if (normalized < 2.8m) niceNormalized = 2.5m; // Adds 250, 25
            else if (normalized < 3.5m) niceNormalized = 3m;   // Adds 300, 30
            else if (normalized < 4.5m) niceNormalized = 4m;   // Adds 400, 40
            else if (normalized < 7.5m) niceNormalized = 5m;
            else niceNormalized = 10m;

            return niceNormalized * magnitude;
        }

        // Round to nearest tick size (or arbitrary step)
        public static decimal RoundToStep(decimal number, decimal step)
        {
            if (step <= 0) return number;
            return Math.Round(number / step) * step;
        }

        // Culture-invariant number formatting
        public static string FormatInvariant(this decimal value, string format = "F2")
        {
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string FormatInvariant(this double value, string format = "F2")
        {
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
