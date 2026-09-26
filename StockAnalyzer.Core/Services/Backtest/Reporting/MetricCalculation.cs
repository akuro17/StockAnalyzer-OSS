using System;
using System.Threading;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

internal static class MetricCalculation
{
    internal const int CancellationCheckInterval = 1024;

    public static void CheckCancellation(CancellationToken cancellationToken, int index)
    {
        if ((index & (CancellationCheckInterval - 1)) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public static MetricValue FromDouble(double value, MetricUnit unit)
    {
        if (!double.IsFinite(value))
        {
            return Failure(unit, MetricReason.NonFiniteResult);
        }

        try
        {
            return MetricValue.Valid((decimal)value, unit);
        }
        catch (OverflowException)
        {
            return Failure(unit, MetricReason.ArithmeticOverflow);
        }
    }

    public static MetricValue Failure(MetricUnit unit, MetricReason reason) =>
        MetricValue.NonValid(MetricStatus.NumericFailure, unit, reason);

    public static MetricValue Propagate(MetricValue source, MetricUnit outputUnit) =>
        MetricValue.NonValid(source.Status, outputUnit, source.Reason);

    public static MetricValue Run(MetricUnit unit, Func<MetricValue> calculation)
    {
        try
        {
            return calculation();
        }
        catch (OverflowException)
        {
            return Failure(unit, MetricReason.ArithmeticOverflow);
        }
        catch (DivideByZeroException)
        {
            return Failure(unit, MetricReason.UnexpectedZeroDivisor);
        }
    }
}
