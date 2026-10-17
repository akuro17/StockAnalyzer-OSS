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

    private const string ClassTemplate = "{0} bars ahead: {1}";
    private const string ClassWithProbabilityTemplate = "{0} bars ahead: {1} ({2}%)";

    private static readonly PredictionOutputContract WeeklyClass = new(
        PredictionOutputSemantic.ClassProbabilities, PredictionOutputUnit.Probability, 5,
        TimeframeType.Weekly, ConfidenceType.ClassProbability,
        PredictionTargetFormula.ThresholdedSimpleReturn);

    private static PredictionResult ClassResult(string label = "Up", float? probability = 0.6234f,
        ConfidenceType type = ConfidenceType.ClassProbability) => new(
        label, probability ?? float.NaN, Array.Empty<ClassScore>(), IsFallback: false)
    {
        OutputContract = WeeklyClass,
        ConfidenceType = type,
        ConfidenceValue = probability,
    };

    private static string? FormatClass(PredictionResult result, TimeframeType timeframe = TimeframeType.Weekly) =>
        PredictionDisplayFormatter.Format(result, timeframe, CultureInfo.InvariantCulture, key => key switch
        {
            PredictionDisplayFormatter.ClassLabelKey => ClassTemplate,
            PredictionDisplayFormatter.ClassLabelWithProbabilityKey => ClassWithProbabilityTemplate,
            _ => null,
        });

    [Fact]
    public void Format_Class_ShowsLabelWithTheRealProbabilityAtOneDecimal()
    {
        Assert.Equal("5 bars ahead: Up (62.3%)", FormatClass(ClassResult()));
        Assert.Equal("5 bars ahead: Up (62.3%)",
            FormatClass(ClassResult(type: ConfidenceType.CalibratedProbability)));
    }

    [Fact]
    public void Format_Class_UsesTheCultureDecimalSeparator()
    {
        var value = PredictionDisplayFormatter.Format(ClassResult(), TimeframeType.Weekly,
            new CultureInfo("de-DE"), key => key == PredictionDisplayFormatter.ClassLabelWithProbabilityKey
                ? ClassWithProbabilityTemplate : ClassTemplate);

        Assert.Equal("5 bars ahead: Up (62,3%)", value);
    }

    [Fact]
    public void Format_Class_WithoutARealProbability_KeepsTheLabelOnlyText()
    {
        Assert.Equal("5 bars ahead: Up", FormatClass(ClassResult(probability: null)));
        Assert.Equal("5 bars ahead: Up", FormatClass(ClassResult(probability: float.NaN)));
        Assert.Equal("5 bars ahead: Up", FormatClass(ClassResult(probability: 1.2f)));
        Assert.Equal("5 bars ahead: Up", FormatClass(ClassResult(probability: -0.1f)));
        // A confidence of another meaning (e.g. a standard deviation) is never shown as a percentage.
        Assert.Equal("5 bars ahead: Up",
            FormatClass(ClassResult(type: ConfidenceType.PredictionStdDev)));
        Assert.Equal("5 bars ahead: Up", FormatClass(ClassResult(type: ConfidenceType.None)));
    }

    [Fact]
    public void Format_Class_ProbabilityBoundsAreShownExactly()
    {
        Assert.Equal("5 bars ahead: Up (0.0%)", FormatClass(ClassResult(probability: 0f)));
        Assert.Equal("5 bars ahead: Up (100.0%)", FormatClass(ClassResult(probability: 1f)));
    }

    [Fact]
    public void Format_Class_UnavailableResults_AreNull()
    {
        Assert.Null(FormatClass(ClassResult(), TimeframeType.Daily)); // another chart timeframe
        Assert.Null(FormatClass(ClassResult(label: " "))); // no label
        Assert.Null(FormatClass(PredictionResult.Empty)); // fallback
        Assert.Null(FormatClass(ClassResult() with { IsRegression = true })); // class semantic but a regression flag
        Assert.Null(FormatClass(ClassResult() with { OutputContract = null }));
    }

    [Fact]
    public void Format_Class_MissingTemplate_IsNull()
    {
        Assert.Null(PredictionDisplayFormatter.Format(ClassResult(), TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => null));
        Assert.Null(PredictionDisplayFormatter.Format(ClassResult(probability: null), TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => " "));
    }

    [Fact]
    public void Format_LogReturn_RejectsAWrongUnitOrAClassificationFlag()
    {
        var wrongUnit = Result(0.04) with
        {
            OutputContract = WeeklyLogReturn with { Unit = PredictionOutputUnit.Probability },
        };
        Assert.Null(PredictionDisplayFormatter.Format(wrongUnit, TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => "{0} bars ahead: {1}%"));
        Assert.Null(PredictionDisplayFormatter.Format(Result(0.04) with { IsRegression = false },
            TimeframeType.Weekly, CultureInfo.InvariantCulture, _ => "{0} bars ahead: {1}%"));
        Assert.Null(PredictionDisplayFormatter.Format(Result(0.04), TimeframeType.Weekly,
            CultureInfo.InvariantCulture, _ => ""));
    }

    private const string NoDecisionTemplate = "{0} bars ahead: no decision (top class {1}%, {2}% needed)";

    [Fact]
    public void Format_UnknownClass_WithAThreshold_SaysItIsUnderTheThreshold()
    {
        var result = ClassResult(PredictionResult.UnknownLabel, 0.3358f);

        var value = PredictionDisplayFormatter.Format(result, TimeframeType.Weekly, CultureInfo.InvariantCulture,
            key => key == PredictionDisplayFormatter.NoDecisionKey ? NoDecisionTemplate : ClassWithProbabilityTemplate, decisionThreshold: 0.5f);

        Assert.Equal("5 bars ahead: no decision (top class 33.6%, 50.0% needed)", value);
    }

    [Fact]
    public void Format_UnknownClass_WithoutAThreshold_OrOtherLabels_KeepsTheTextAsBefore()
    {
        string? Localize(string key) => key == PredictionDisplayFormatter.NoDecisionKey ? NoDecisionTemplate : ClassWithProbabilityTemplate;

        Assert.Equal("5 bars ahead: Unknown (33.6%)", PredictionDisplayFormatter.Format(ClassResult(PredictionResult.UnknownLabel, 0.3358f),
            TimeframeType.Weekly, CultureInfo.InvariantCulture, Localize));
        Assert.Equal("5 bars ahead: Up (62.3%)", PredictionDisplayFormatter.Format(ClassResult("Up", 0.6234f),
            TimeframeType.Weekly, CultureInfo.InvariantCulture, Localize, decisionThreshold: 0.5f));
        // a template that is missing falls back to the old text
        Assert.Equal("5 bars ahead: Unknown (33.6%)", PredictionDisplayFormatter.Format(ClassResult(PredictionResult.UnknownLabel, 0.3358f),
            TimeframeType.Weekly, CultureInfo.InvariantCulture, key => key == PredictionDisplayFormatter.NoDecisionKey ? null : ClassWithProbabilityTemplate, decisionThreshold: 0.5f));
    }
}
