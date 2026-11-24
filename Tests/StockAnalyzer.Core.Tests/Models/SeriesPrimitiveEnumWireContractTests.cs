using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Rendering;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Pins the integer wire values of <see cref="CoreLineStyle"/> and all rendering primitive enums
/// per SA_ARCHITECTURE_RULES.md §3 (Persisted Enums Carry Explicit Integer Values).
/// </summary>
public sealed class SeriesPrimitiveEnumWireContractTests
{
    private static Dictionary<string, int> Table<T>() where T : struct, Enum =>
        Enum.GetValues<T>().ToDictionary(value => value.ToString(), value => Convert.ToInt32(value));

    [Fact]
    public void CoreLineStyle_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int>
        {
            ["Solid"] = 0,
            ["Dash"] = 1,
            ["Dot"] = 2,
            ["DashDot"] = 3,
            ["Step"] = 4,
        },
        Table<CoreLineStyle>());

    [Fact]
    public void SeriesPrimitiveType_WireValuesArePinned() => Assert.Equal(
        new Dictionary<string, int>
        {
            ["Line"] = 0,
            ["DashedLine"] = 1,
            ["Dot"] = 2,
            ["Histogram"] = 3,
            ["StepLine"] = 4,
            ["HorizontalSegment"] = 5,
            ["Spline"] = 6,
            ["Area"] = 7,
            ["BandCloud"] = 8,
            ["FloatingBar"] = 9,
            ["PositiveNegativeArea"] = 10,
            ["StackedArea"] = 11,
            ["Bubble"] = 12,
            ["MarkerSymbol"] = 13,
            ["ColorStrip"] = 14,
            ["DynamicStyling"] = 15,
            ["Vector"] = 16,
            ["Waterfall"] = 17,
            ["Lollipop"] = 18,
        },
        Table<SeriesPrimitiveType>());

    [Fact]
    public void PrimitiveOptionEnums_WireValuesArePinned()
    {
        Assert.Equal(new Dictionary<string, int> { ["After"] = 0, ["Before"] = 1, ["Mid"] = 2 }, Table<StepAlignmentMode>());
        Assert.Equal(new Dictionary<string, int> { ["BreakPath"] = 0, ["ConnectThrough"] = 1 }, Table<NullGapPolicy>());
        Assert.Equal(new Dictionary<string, int> { ["PanelBottom"] = 0, ["ScalarValue"] = 1, ["ReferenceSeries"] = 2 }, Table<AreaBaselineMode>());
        Assert.Equal(new Dictionary<string, int> { ["ZeroBaselineSplit"] = 0, ["NonNegativeClamp"] = 1, ["RawCumulative"] = 2 }, Table<StackingSignMode>());
        Assert.Equal(new Dictionary<string, int> { ["TreatAsZero"] = 0, ["BreakAllLayers"] = 1 }, Table<StackingNullMode>());
        Assert.Equal(new Dictionary<string, int>
        {
            ["Circle"] = 0,
            ["TriangleUp"] = 1,
            ["TriangleDown"] = 2,
            ["Square"] = 3,
            ["Cross"] = 4,
            ["Plus"] = 5,
            ["Diamond"] = 6,
        }, Table<MarkerSymbolShape>());
        Assert.Equal(new Dictionary<string, int> { ["Sequential"] = 0, ["Diverging"] = 1, ["DiscreteState"] = 2 }, Table<ColorStripScaleMode>());
        Assert.Equal(new Dictionary<string, int> { ["Bottom"] = 0, ["Top"] = 1, ["FullPanel"] = 2 }, Table<ColorStripAnchor>());
        Assert.Equal(new Dictionary<string, int> { ["Value"] = 0, ["Slope"] = 1, ["Delta"] = 2, ["AuxiliarySeries"] = 3 }, Table<DynamicStyleMetric>());
        Assert.Equal(new Dictionary<string, int> { ["PriceSpaceEndpoints"] = 0, ["BarAnchoredPolar"] = 1 }, Table<VectorInputMode>());
        Assert.Equal(new Dictionary<string, int> { ["Delta"] = 0, ["Subtotal"] = 1, ["Total"] = 2 }, Table<WaterfallBarKind>());
    }
}
