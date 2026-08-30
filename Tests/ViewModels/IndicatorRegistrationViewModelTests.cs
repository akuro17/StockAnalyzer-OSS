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
}
