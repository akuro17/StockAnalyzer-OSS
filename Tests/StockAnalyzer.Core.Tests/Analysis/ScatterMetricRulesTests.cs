#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ScatterMetricRulesTests
{
    private const int MaxOffset = 500;
    private static readonly IIndicatorFactory Factory = IndicatorFactory.Default;

    private static ScatterMetricInvalidReason Validate(ScatterMetricDefinition? metric, int maxOffset = MaxOffset, IIndicatorFactory? factory = null)
        => ScatterMetricRules.Validate(metric, maxOffset, factory ?? Factory);

    [Fact]
    public void Validate_AcceptsAnIndicatorAnOffsetAtTheLimitAndAPriceSource()
    {
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(ScatterMetricTestKit.Indicator(IndicatorType.RSI)));
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: MaxOffset)));
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(ScatterMetricTestKit.Indicator(IndicatorType.Price, output: "Close")));
    }

    [Fact]
    public void Validate_AcceptsANumericColumn()
    {
        var column = ScatterMetricTestKit.Column(ScatterMetricTestKit.FirstNumericColumn());

        Assert.Equal(ScatterMetricInvalidReason.None, Validate(column));
    }

    [Fact]
    public void Validate_RefusesNullAndAnEmptyId()
    {
        Assert.Equal(ScatterMetricInvalidReason.Missing, Validate(null));

        var noId = ScatterMetricTestKit.Indicator(IndicatorType.RSI);
        noId.Id = Guid.Empty;
        Assert.Equal(ScatterMetricInvalidReason.Missing, Validate(noId));
    }

    [Fact]
    public void Validate_RefusesACriteriaSource()
    {
        var criteria = ScatterMetricTestKit.Indicator(IndicatorType.RSI);
        criteria.Source.CategoryType = ScreenerItemCategoryType.Criteria;

        Assert.Equal(ScatterMetricInvalidReason.UnsupportedCategory, Validate(criteria));
    }

    // The enum member keeps its legacy name (it meant "not daily" when Day was the only time frame); it now means "not Day, Week or Month".
    [Theory]
    [InlineData(TimeFrame.M1)]
    [InlineData(TimeFrame.M5)]
    [InlineData(TimeFrame.M15)]
    [InlineData(TimeFrame.M30)]
    [InlineData(TimeFrame.H1)]
    [InlineData(TimeFrame.H4)]
    public void Validate_RefusesATimeFrameThatIsNotDayWeekOrMonth(TimeFrame frame)
    {
        var indicator = ScatterMetricTestKit.Indicator(IndicatorType.RSI);
        indicator.Source.TimeFrame = frame;
        var column = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("Close"));
        column.Source.TimeFrame = frame;

        Assert.Equal(ScatterMetricInvalidReason.TimeFrameNotDaily, Validate(indicator));
        Assert.Equal(ScatterMetricInvalidReason.TimeFrameNotDaily, Validate(column));
    }

    [Theory]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public void Validate_AcceptsAnIndicatorAPriceSourceAndABarValueColumn_OnWeekAndMonth(TimeFrame frame)
    {
        var indicator = ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: MaxOffset);
        var price = ScatterMetricTestKit.Indicator(IndicatorType.Price, output: "Close");
        var close = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("Close"));
        var changePercent = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("ChangePercent"));

        foreach (ScatterMetricDefinition metric in new[] { indicator, price, close, changePercent })
        {
            metric.Source.TimeFrame = frame;
            Assert.Equal(ScatterMetricInvalidReason.None, Validate(metric));
        }
    }

    [Fact]
    public void Validate_RefusesAFundamentalColumnOnWeekAndMonth_ButAcceptsItOnDay()
    {
        var marketCap = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("MarketCap"));
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(marketCap));

        foreach (TimeFrame frame in new[] { TimeFrame.W1, TimeFrame.MN1 })
        {
            marketCap.Source.TimeFrame = frame;
            Assert.Equal(ScatterMetricInvalidReason.TimeFrameNotApplicable, Validate(marketCap));
        }
    }

    [Fact]
    public void Validate_KeepsItsOrder_WhenSeveralThingsAreWrongOnWeek()
    {
        // The time frame is judged before the Offset, and a text Column or one without a value is named before the time frame does not apply.
        var offsetAndFrame = ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: MaxOffset + 1);
        offsetAndFrame.Source.TimeFrame = TimeFrame.H1;
        Assert.Equal(ScatterMetricInvalidReason.TimeFrameNotDaily, Validate(offsetAndFrame));

        var offsetOnly = ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: MaxOffset + 1);
        offsetOnly.Source.TimeFrame = TimeFrame.W1;
        Assert.Equal(ScatterMetricInvalidReason.OffsetOutOfRange, Validate(offsetOnly));

        var text = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("Close"));
        text.Source.OutputName = "Sector";
        text.Source.CustomDisplayName = "Sector";
        text.Source.TimeFrame = TimeFrame.W1;
        Assert.Equal(ScatterMetricInvalidReason.TextColumn, Validate(text));

        var withoutValue = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("Close"));
        withoutValue.Source.OutputName = ScatterMetricEligibility.ColumnsWithoutAValue[0];
        withoutValue.Source.CustomDisplayName = ScatterMetricEligibility.ColumnsWithoutAValue[0];
        withoutValue.Source.TimeFrame = TimeFrame.W1;
        Assert.Equal(ScatterMetricInvalidReason.ColumnWithoutValue, Validate(withoutValue));
    }

    [Fact]
    public void Validate_StillRefusesACriteriaOnWeek()
    {
        var criteria = ScatterMetricTestKit.Indicator(IndicatorType.RSI);
        criteria.Source.CategoryType = ScreenerItemCategoryType.Criteria;
        criteria.Source.TimeFrame = TimeFrame.W1;

        Assert.Equal(ScatterMetricInvalidReason.UnsupportedCategory, Validate(criteria));
    }

    [Theory]
    [InlineData(-1, 500)]
    [InlineData(501, 500)]
    [InlineData(1, 0)]
    public void Validate_RefusesAnOffsetOutsideZeroToTheLimit(int offset, int maxOffset)
    {
        var metric = ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: offset);

        Assert.Equal(ScatterMetricInvalidReason.OffsetOutOfRange, Validate(metric, maxOffset));
    }

    [Theory]
    [InlineData("Sector")]
    [InlineData("Industry")]
    [InlineData("Region")]
    [InlineData("Name")]
    [InlineData("Tag")]
    public void Validate_RefusesATextColumn(string field)
    {
        var column = ScatterMetricTestKit.Column(ScatterMetricTestKit.FirstNumericColumn());
        column.Source.OutputName = field;
        column.Source.CustomDisplayName = field;

        Assert.Equal(ScatterMetricInvalidReason.TextColumn, Validate(column));
    }

    [Fact]
    public void Validate_RefusesAColumnThatNamesNothing()
    {
        var column = ScatterMetricTestKit.Column(ScatterMetricTestKit.FirstNumericColumn());
        column.Source.OutputName = string.Empty;
        column.Source.CustomDisplayName = string.Empty;

        Assert.Equal(ScatterMetricInvalidReason.ColumnNameMissing, Validate(column));
    }

    [Fact]
    public void Validate_RefusesAnIndicatorTheFactoryDoesNotKnow()
    {
        Assert.Equal(ScatterMetricInvalidReason.UnknownIndicator,
            Validate(ScatterMetricTestKit.Indicator(IndicatorType.RSI), MaxOffset, new NothingRegisteredFactory()));
    }

    [Fact]
    public void Validate_RefusesALookAheadIndicatorAndALookAheadSeries()
    {
        IndicatorType wholeIndicator = BacktestIndicatorEligibility.NonCausalIndicatorTypes.First();
        Assert.Equal(ScatterMetricInvalidReason.NotAllowedIndicator,
            Validate(new ScatterMetricDefinition { Source = new ScreenerIndicatorSideConfig { IndicatorType = wholeIndicator, OutputName = "Main" } }));

        (IndicatorType type, string series) = BacktestIndicatorEligibility.NonCausalSeries.First();
        Assert.Equal(ScatterMetricInvalidReason.NotAllowedIndicator,
            Validate(new ScatterMetricDefinition { Source = new ScreenerIndicatorSideConfig { IndicatorType = type, OutputName = series } }));
    }

    // ----- S-1: a metric that compares two symbols reads the comparison symbol's DAILY bars, so it is Day only -------------------------

    private static ScatterMetricDefinition Compared(IndicatorType type, TimeFrame frame, string? comparisonSymbol, int offset = 0, string? output = null)
    {
        ScatterMetricDefinition metric = ScatterMetricTestKit.Indicator(type, offset: offset, output: output);
        metric.Source.TimeFrame = frame;
        if (comparisonSymbol != null)
        {
            metric.Source.Parameters["ComparisonSymbol"] = comparisonSymbol;
        }

        return metric;
    }

    [Fact]
    public void Validate_TwoTickerNeedsDay_IsTheLastMember_AndNoEarlierMemberChangedItsNumber()
    {
        ScatterMetricInvalidReason[] all = Enum.GetValues<ScatterMetricInvalidReason>();

        Assert.Equal(ScatterMetricInvalidReason.TwoTickerNeedsDay, all.Max());
        Assert.Equal(11, (int)ScatterMetricInvalidReason.TwoTickerNeedsDay);
        Assert.Equal(10, (int)ScatterMetricInvalidReason.NotAllowedIndicator);
        Assert.Equal(9, (int)ScatterMetricInvalidReason.UnknownIndicator);
        Assert.Equal(8, (int)ScatterMetricInvalidReason.TimeFrameNotApplicable);
        Assert.Equal(0, (int)ScatterMetricInvalidReason.None);
    }

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    public void Validate_RefusesATwoTickerMetricWithAComparisonSymbolOnWeekAndMonth(IndicatorType type)
    {
        Assert.Equal(ScatterMetricInvalidReason.TwoTickerNeedsDay, Validate(Compared(type, TimeFrame.W1, "SPY")));
        Assert.Equal(ScatterMetricInvalidReason.TwoTickerNeedsDay, Validate(Compared(type, TimeFrame.MN1, "SPY")));
    }

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    public void Validate_AcceptsATwoTickerMetricWithAComparisonSymbolOnDay(IndicatorType type)
    {
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(Compared(type, TimeFrame.D1, "SPY")));
    }

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    public void Validate_StillAcceptsABlankSymbolTwoTickerMetricOnWeekAndMonth_PriceVersusVolumeIsComputedInItsOwnBars(IndicatorType type)
    {
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(Compared(type, TimeFrame.W1, null)));
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(Compared(type, TimeFrame.MN1, "  ")));
    }

    [Fact]
    public void Validate_TwoTickerNeedsDay_IsJudgedAfterTheOffsetAndAfterTheEligibility()
    {
        // The Offset is judged first.
        Assert.Equal(ScatterMetricInvalidReason.OffsetOutOfRange, Validate(Compared(IndicatorType.Correlation, TimeFrame.W1, "SPY", offset: MaxOffset + 1)));

        // A type the Screener cannot compute is refused as such, not as a time frame problem (it never reaches the Day check).
        ScatterMetricDefinition blocked = new() { Source = Compared(IndicatorType.RollingBeta, TimeFrame.W1, "SPY", output: IndicatorResult.MainSeriesName).Source };
        Assert.Equal(ScatterMetricInvalidReason.NotAllowedIndicator, Validate(blocked));

        // An unknown type is refused first.
        Assert.Equal(ScatterMetricInvalidReason.UnknownIndicator,
            Validate(Compared(IndicatorType.Correlation, TimeFrame.W1, "SPY"), MaxOffset, new NothingRegisteredFactory()));
    }

    [Fact]
    public void Validate_DoesNotApplyTheDayRuleToAMetricThatDoesNotCompareTwoSymbols()
    {
        // A stray ComparisonSymbol parameter on an ordinary indicator or on the Price source changes nothing.
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(Compared(IndicatorType.RSI, TimeFrame.W1, "SPY")));
        ScatterMetricDefinition price = Compared(IndicatorType.Price, TimeFrame.MN1, "SPY");
        price.Source.OutputName = "Close";
        Assert.Equal(ScatterMetricInvalidReason.None, Validate(price));
    }

    [Fact]
    public void DiffersInIdOrContent_DetectsCountIdAndContentChangesOnly()
    {
        var a = ScatterMetricTestKit.Indicator(IndicatorType.RSI);
        var b = ScatterMetricTestKit.Indicator(IndicatorType.SMA);

        Assert.False(ScatterMetricRules.DiffersInIdOrContent(new[] { a, b }, new[] { a.Clone(), b.Clone() }));
        Assert.True(ScatterMetricRules.DiffersInIdOrContent(new[] { a, b }, new[] { b, a }), "a reorder differs");
        Assert.True(ScatterMetricRules.DiffersInIdOrContent(new[] { a }, new[] { a, b }), "another count differs");

        var edited = a.Clone();
        edited.Source.Offset = 4;
        Assert.True(ScatterMetricRules.DiffersInIdOrContent(new[] { a }, new[] { edited }), "another content differs");

        var sameContentOtherId = a.Clone();
        sameContentOtherId.Id = Guid.NewGuid();
        Assert.True(ScatterMetricRules.DiffersInIdOrContent(new[] { a }, new[] { sameContentOtherId }), "another Id differs");
    }

    private sealed class NothingRegisteredFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => null;
        public bool IsRegistered(IndicatorType type) => false;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => Array.Empty<IndicatorType>();
    }
}
