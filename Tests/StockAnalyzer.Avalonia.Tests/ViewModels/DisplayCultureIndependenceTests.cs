using System;
using System.Globalization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// SA_ARCHITECTURE_RULES, Culture Invariance: the display text of numbers does not depend on the culture of the machine or of the test host. The texts of the
/// Training Wizard warnings and of the reach lines are formatted with <c>DisplayFormat.Culture</c>.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class DisplayCultureIndependenceTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");   // '.' groups thousands and ',' is the decimal separator

    public DisplayCultureIndependenceTests() => LocalizationManager.Instance.Initialize("en");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        LocalizationManager.Instance.Initialize(_originalLanguage);
    }

    private static SufficiencySnapshot Snapshot()
    {
        var infos = new[] { new SymbolBarInfo("AAA", 600, 600, null, null), new SymbolBarInfo("BBB", 12_000, 12_000, new DateOnly(2017, 5, 1), 12_000) };
        return new SufficiencySnapshot(TrainingDataSufficiency.Evaluate(infos, 2374, 60, 5, 0, 5, false, 1_900), 2374, 60, 5, 5, 64, 1_900, new DateOnly(2026, 10, 8));
    }

    [Fact]
    public void TheWarningsOfTheWizardAreTheSameInEveryCulture()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string requirement = TrainingSufficiencyDisplay.RequirementWarning(Snapshot()), limit = TrainingSufficiencyDisplay.SampleLimitWarning(Snapshot());
        Assert.Contains("2,374", requirement);
        Assert.NotEmpty(limit);

        CultureInfo.CurrentCulture = German;
        Assert.Equal(requirement, TrainingSufficiencyDisplay.RequirementWarning(Snapshot()));
        Assert.Equal(limit, TrainingSufficiencyDisplay.SampleLimitWarning(Snapshot()));
    }

    [Fact]
    public void TheReachTextsAndTheStopReasonAreTheSameInEveryCulture()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string list = ModelReachDisplay.ListText(0.1234, 0.505f), status = ModelReachDisplay.StatusLine(0.1234, cannotReach: false, 0.505f);
        string reasons = ModelReachDisplay.StopReasons(new ModelAutoActivationDecision(ReachIsZero: true, NotBetterThanUniform: true), 0.505f);
        Assert.Contains("50.5%", list);
        Assert.Contains("12.3%", status);
        Assert.Contains("50.5%", reasons);

        CultureInfo.CurrentCulture = German;
        Assert.Equal(list, ModelReachDisplay.ListText(0.1234, 0.505f));
        Assert.Equal(status, ModelReachDisplay.StatusLine(0.1234, cannotReach: false, 0.505f));
        Assert.Equal(reasons, ModelReachDisplay.StopReasons(new ModelAutoActivationDecision(true, true), 0.505f));
    }

    [Fact]
    public void NoStopReasonIsGivenForAGateThatDidNotStop()
        => Assert.Equal(string.Empty, ModelReachDisplay.StopReasons(new ModelAutoActivationDecision(false, false), 0.5f));
}
