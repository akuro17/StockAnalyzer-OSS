using System;
using System.Globalization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class PredictionDisplayFormatterTests
{
    private static readonly PredictionOutputContract WeeklyLogReturn = new(
        PredictionOutputSemantic.LogReturn, PredictionOutputUnit.Dimensionless, 5,
        TimeframeType.Weekly, ConfidenceType.None,
        PredictionTargetFormula.LogFutureCloseOverAnchorClose);

    private static PredictionResult Result(double logReturn) => new(
        string.Empty, 0f, Array.Empty<ClassScore>(), IsFallback: false)
    {
        IsRegression = true,
        PredictedLogReturn = logReturn,
        OutputContract = WeeklyLogReturn,
    };

    [Fact]
    public void Format_LogReturn_UsesExactExponentialConversionAndBarUnit()
    {
        var value = PredictionDisplayFormatter.Format(Result(0.04), TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => "{0} bars ahead: {1}%");

        Assert.Equal("5 bars ahead: 4.08%", value);
    }

    [Fact]
    public void Format_Midpoint_UsesNearestEvenAtTwoDecimals()
    {
        var value = PredictionDisplayFormatter.Format(Result(Math.Log(1.03125)), TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => "{0} bars ahead: {1}%");

        Assert.Equal("5 bars ahead: 3.12%", value);
    }

    [Fact]
    public void Format_MismatchedTimeframeOrNonfiniteResult_IsUnavailable()
    {
        Assert.Null(PredictionDisplayFormatter.Format(Result(0.04), TimeframeType.Daily,
            CultureInfo.InvariantCulture, _ => "{0} bars ahead: {1}%"));
        Assert.Null(PredictionDisplayFormatter.Format(Result(double.PositiveInfinity), TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => "{0} bars ahead: {1}%"));
    }
}
