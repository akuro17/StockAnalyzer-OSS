using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class IndicatorRegistrationViewModelTests
{
    // Pairs of Bullish/Bearish candlestick pattern ShortNames that sort adjacent under 'B'
    public static readonly TheoryData<string, string> BullishBearishPatternPairs = new()
    {
        { "Bearish Abandoned Baby", "Bullish Abandoned Baby" },
        { "Bearish Breakaway", "Bullish Breakaway" },
        { "Bearish Engulfing", "Bullish Engulfing" },
        { "Bearish Gap Three Methods", "Bullish Gap Three Methods" },
        { "Bearish Harami", "Bullish Harami" },
        { "Bearish Kicking", "Bullish Kicking" },
        { "Bearish Marubozu", "Bullish Marubozu" },
        { "Bearish Side-by-Side White Lines", "Bullish Side-by-Side White Lines" },
        { "Bearish Tasuki Gap", "Bullish Tasuki Gap" },
        { "Bearish Three-Line Strike", "Bullish Three-Line Strike" },
    };

    [Theory]
    [MemberData(nameof(BullishBearishPatternPairs))]
    public void CandlestickPatternsGroup_BullishBearishPair_SortsAdjacentlyUnderB(string bearishName, string bullishName)
    {
        var vm = new IndicatorRegistrationViewModel();
        var candlestickGroup = vm.NavGroups.First(g =>
            !g.IsHeader && string.Equals(g.Group!.Name, ScreenerGroupNames.CandlestickPatterns, System.StringComparison.OrdinalIgnoreCase));

        vm.SelectedGroupItem = candlestickGroup;

        var names = vm.FilteredIndicators.Select(i => i.ShortName).ToList();
        int bearishIndex = names.IndexOf(bearishName);
        int bullishIndex = names.IndexOf(bullishName);

        Assert.True(bearishIndex >= 0, $"'{bearishName}' not found in Candlestick Patterns catalog.");
        Assert.True(bullishIndex >= 0, $"'{bullishName}' not found in Candlestick Patterns catalog.");
        Assert.Equal(bearishIndex + 1, bullishIndex);
    }

    [Fact]
    public void CandlestickPatternsGroup_AllItems_AreInStrictLeadingCharacterAlphabeticalOrder()
    {
        var vm = new IndicatorRegistrationViewModel();
        var candlestickGroup = vm.NavGroups.First(g =>
            !g.IsHeader && string.Equals(g.Group!.Name, ScreenerGroupNames.CandlestickPatterns, System.StringComparison.OrdinalIgnoreCase));

        vm.SelectedGroupItem = candlestickGroup;

        var names = vm.FilteredIndicators.Select(i => i.ShortName).ToList();

        // Verify strict leading character milestone ordering (A -> B -> C -> D -> E -> F -> G -> H -> I -> L -> M -> P -> R -> S -> T)
        int idxAdvance = names.IndexOf("Advance Block");                         // A
        int idxBearishAbandoned = names.IndexOf("Bearish Abandoned Baby");       // B
        int idxBullishThreeLine = names.IndexOf("Bullish Three-Line Strike");     // B (end of B group)
        int idxSwallow = names.IndexOf("Concealing Baby Swallow");               // C
        int idxDarkCloud = names.IndexOf("Dark Cloud Cover");                    // D
        int idxDeliberation = names.IndexOf("Deliberation");                     // D
        int idxDoji = names.IndexOf("Doji / Cross Doji");                        // D
        int idxEveningStar = names.IndexOf("Evening Star");                      // E
        int idxFallingThree = names.IndexOf("Falling Three Methods");            // F
        int idxGravestone = names.IndexOf("Gravestone Doji");                    // G
        int idxHammer = names.IndexOf("Hammer / Bullish Umbrella");              // H
        int idxHoming = names.IndexOf("Homing Pigeon");                          // H
        int idxIdenticalCrows = names.IndexOf("Identical Three Crows");          // I
        int idxInvertedHammer = names.IndexOf("Inverted Hammer");                // I
        int idxLadder = names.IndexOf("Ladder Bottom");                          // L
        int idxMatHold = names.IndexOf("Mat Hold");                              // M
        int idxMorningStar = names.IndexOf("Morning Star");                      // M
        int idxPiercing = names.IndexOf("Piercing Line");                        // P
        int idxRisingThree = names.IndexOf("Rising Three Methods");              // R
        int idxShootingStar = names.IndexOf("Shooting Star");                    // S
        int idxStickSandwich = names.IndexOf("Stick Sandwich");                  // S
        int idxThreeBlackCrows = names.IndexOf("Three Black Crows");              // T
        int idxThreeWhiteSoldiers = names.IndexOf("Three White Soldiers");        // T

        Assert.True(idxAdvance < idxBearishAbandoned, "A (Advance Block) should precede B (Bearish Abandoned Baby)");
        Assert.True(idxBearishAbandoned < idxBullishThreeLine, "Bearish Abandoned Baby should precede Bullish Three-Line Strike in B");
        Assert.True(idxBullishThreeLine < idxSwallow, "B (Bullish Three-Line Strike) should precede C (Concealing Baby Swallow)");
        Assert.True(idxSwallow < idxDarkCloud, "C (Concealing Baby Swallow) should precede D (Dark Cloud Cover)");
        Assert.True(idxDarkCloud < idxDeliberation, "D (Dark Cloud Cover) should precede D (Deliberation)");
        Assert.True(idxDeliberation < idxDoji, "D (Deliberation) should precede D (Doji)");
        Assert.True(idxDoji < idxEveningStar, "D (Doji) should precede E (Evening Star)");
        Assert.True(idxEveningStar < idxFallingThree, "E (Evening Star) should precede F (Falling Three Methods)");
        Assert.True(idxFallingThree < idxGravestone, "F (Falling Three Methods) should precede G (Gravestone Doji)");
        Assert.True(idxGravestone < idxHammer, "G (Gravestone Doji) should precede H (Hammer)");
        Assert.True(idxHammer < idxHoming, "H (Hammer) should precede H (Homing Pigeon)");
        Assert.True(idxHoming < idxIdenticalCrows, "H (Homing Pigeon) should precede I (Identical Three Crows)");
        Assert.True(idxIdenticalCrows < idxInvertedHammer, "I (Identical Three Crows) should precede I (Inverted Hammer)");
        Assert.True(idxInvertedHammer < idxLadder, "I (Inverted Hammer) should precede L (Ladder Bottom)");
        Assert.True(idxLadder < idxMatHold, "L (Ladder Bottom) should precede M (Mat Hold)");
        Assert.True(idxMatHold < idxMorningStar, "M (Mat Hold) should precede M (Morning Star)");
        Assert.True(idxMorningStar < idxPiercing, "M (Morning Star) should precede P (Piercing Line)");
        Assert.True(idxPiercing < idxRisingThree, "P (Piercing Line) should precede R (Rising Three Methods)");
        Assert.True(idxRisingThree < idxShootingStar, "R (Rising Three Methods) should precede S (Shooting Star)");
        Assert.True(idxShootingStar < idxStickSandwich, "S (Shooting Star) should precede S (Stick Sandwich)");
        Assert.True(idxStickSandwich < idxThreeBlackCrows, "S (Stick Sandwich) should precede T (Three Black Crows)");
        Assert.True(idxThreeBlackCrows < idxThreeWhiteSoldiers, "T (Three Black Crows) should precede T (Three White Soldiers)");
    }

    [Fact]
    public void NavGroups_ContainsPriceDirectlyAboveTrend()
    {
        var vm = new IndicatorRegistrationViewModel();

        int priceIdx = vm.NavGroups.ToList().FindIndex(g => !g.IsHeader && g.Group?.Name == "Price");
        int trendIdx = vm.NavGroups.ToList().FindIndex(g => !g.IsHeader && g.Group?.Name == "Trend");

        Assert.True(priceIdx >= 0, "Price group should exist in NavGroups");
        Assert.True(trendIdx >= 0, "Trend group should exist in NavGroups");
        Assert.Equal(trendIdx - 1, priceIdx);
    }

    [Fact]
    public void SelectedGroupItem_Price_Displays15ItemsInPriceTypeOptionsOrder()
    {
        var vm = new IndicatorRegistrationViewModel();
        var priceGroup = vm.NavGroups.First(g => !g.IsHeader && g.Group?.Name == "Price");

        vm.SelectedGroupItem = priceGroup;

        Assert.Equal(15, vm.FilteredIndicators.Count);

        for (int i = 0; i < StockAnalyzer.Core.Models.Indicators.PriceDataHelper.PriceTypeOptions.Count; i++)
        {
            var expectedType = StockAnalyzer.Core.Models.Indicators.PriceDataHelper.PriceTypeOptions[i];
            Assert.Equal(expectedType.ToString(), vm.FilteredIndicators[i].ShortName);
            Assert.Equal(StockAnalyzer.Core.Models.Indicators.PriceDataHelper.FormatPriceTypeLabel(expectedType), vm.FilteredIndicators[i].DisplayName);
        }
    }

    [Fact]
    public void BindToSide_PriceItem_ConfiguresLeftIndicatorSettingsAndRegistersEntry()
    {
        var vm = new IndicatorRegistrationViewModel();
        var priceGroup = vm.NavGroups.First(g => !g.IsHeader && g.Group?.Name == "Price");
        vm.SelectedGroupItem = priceGroup;

        // Select Median (H+L)/2
        var medianItem = vm.FilteredIndicators.First(i => i.ShortName == "Median");
        vm.SelectedIndicator = medianItem;

        Assert.NotNull(vm.LeftSelectedIndicator);
        Assert.Equal("Median", vm.LeftSelectedIndicator.ShortName);
        Assert.Equal(StockAnalyzer.Core.Models.IndicatorType.Price, vm.LeftIndicatorSettings?.TypeEnum);
        Assert.Equal(StockAnalyzer.Core.Models.PriceType.Median, vm.LeftIndicatorSettings?.PriceSource);

        vm.RightNumericValue = 150.0m;
        vm.RegisterIndicatorCommand.Execute(null);

        Assert.Single(vm.RegisteredEntries);
        var entry = vm.RegisteredEntries[0];
        Assert.Equal(StockAnalyzer.Core.Models.IndicatorType.Price, entry.LeftHand.IndicatorType);
        Assert.Equal("Median (H+L)/2", entry.LeftHand.DisplayName);
    }

    [Fact]
    public void RegisterIndicator_InNumericMode_ResetsActiveTargetSideToLeft_AndAllowsSubsequentCatalogSelectionsToUpdateLeftTarget()
    {
        var vm = new IndicatorRegistrationViewModel();
        var priceGroup = vm.NavGroups.First(g => !g.IsHeader && g.Group?.Name == "Price");
        vm.SelectedGroupItem = priceGroup;

        // 1. Select Close and register
        var closeItem = vm.FilteredIndicators.First(i => i.ShortName == "Close");
        vm.SelectedIndicator = closeItem;
        Assert.Equal("Close", vm.LeftSelectedIndicator?.ShortName);

        vm.RightNumericValue = 100m;
        vm.RegisterIndicatorCommand.Execute(null);

        Assert.Single(vm.RegisteredEntries);
        Assert.Equal(StockAnalyzer.Avalonia.ViewModels.TargetSide.Left, vm.ActiveTargetSide);

        // 2. Select another indicator (High) after registration
        var highItem = vm.FilteredIndicators.First(i => i.ShortName == "High");
        vm.SelectedIndicator = highItem;

        // LeftSelectedIndicator MUST update to High, not remain stuck on Close!
        Assert.Equal("High", vm.LeftSelectedIndicator?.ShortName);
        Assert.Equal(StockAnalyzer.Core.Models.PriceType.High, vm.LeftIndicatorSettings?.PriceSource);
    }

    [Fact]
    public void SelectRightTarget_InNumericMode_DoesNotSwitchActiveTargetSideToRight()
    {
        var vm = new IndicatorRegistrationViewModel();
        Assert.Equal(StockAnalyzer.Core.Models.Screener.RightHandTargetMode.NumericValue, vm.RightTargetMode);
        Assert.Equal(StockAnalyzer.Avalonia.ViewModels.TargetSide.Left, vm.ActiveTargetSide);

        // Attempting to select right target while in numeric mode must be ignored
        vm.SelectRightTargetCommand.Execute(null);
        Assert.Equal(StockAnalyzer.Avalonia.ViewModels.TargetSide.Left, vm.ActiveTargetSide);
        Assert.True(vm.IsLeftActive);
        Assert.False(vm.IsRightActive);
    }
}
