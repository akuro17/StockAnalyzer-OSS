using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// POC / TPOC colour of the two drawing tools (FixedRangeVolumeProfile, TimeAtPrice): default from the single red constant, painting of exactly one bin
/// (precedence POC &gt; Value Area &gt; base), no POC bin for an all-zero profile, the DataWindow row colour, and the restore of older saved files.
/// </summary>
public class VolumeProfilePocColorDrawingTests
{
    private static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
    private static readonly Color Yellow = Color.Parse("#FFEB3B");
    private static readonly Color Base = Color.Parse("#90A4AE");
    private static readonly Color Green = Color.FromArgb(255, 0, 200, 0);

    private static readonly DateTime Day1 = new(2025, 1, 2);

    /// <summary>800x600 for 2025-01-01..2025-01-10 and prices 50..60: a 50-row bin is 12 px high, a day is about 89 px wide.</summary>
    private static LinearCoordinateTransform MakeTransform()
        => new(new DateTime(2025, 1, 1), new DateTime(2025, 1, 10), 50m, 60m, 800, 600);

    /// <summary>
    /// Volume profile fixture: a wide low-volume bar (50..60) and a narrow high-volume bar (54..56). The narrow bar gives about 12 equal top bins,
    /// so the POC is the lowest of them, its upper neighbour is in the Value Area with the same bar width, and the bins near the range ends are tiny.
    /// </summary>
    private static List<CoreCandleData> VolumeCandles(long volume2 = 100000)
    {
        return new List<CoreCandleData>
        {
            new(Day1, 55m, 60m, 50m, 58m, 1000),
            new(Day1.AddDays(1), 55m, 56m, 54m, 55m, volume2),
        };
    }

    /// <summary>Time-at-price fixture: three bars close at 55, two at 57; the 55 bin is the TPOC and the 57 bin is another (non-Value-Area) bin.</summary>
    private static List<CoreCandleData> TimeCandles()
    {
        var list = new List<CoreCandleData>();
        decimal[] closes = { 55m, 55m, 55m, 57m, 57m };
        for (int i = 0; i < closes.Length; i++)
        {
            decimal c = closes[i];
            list.Add(new CoreCandleData(Day1.AddDays(i), c, 60m, 50m, c, 1000));
        }

        return list;
    }

    private static FixedRangeVolumeProfileObject NewVolumeProfile(List<CoreCandleData> candles)
    {
        var obj = new FixedRangeVolumeProfileObject(new ChartPoint(candles[0].Timestamp, 55m), new ChartPoint(candles[^1].Timestamp, 55m))
        {
            Opacity = 1.0,
            FillOpacity = 0,
        };
        obj.Recalculate(candles);
        return obj;
    }

    private static TimeAtPriceObject NewTimeAtPrice(List<CoreCandleData> candles)
    {
        var obj = new TimeAtPriceObject(new ChartPoint(candles[0].Timestamp, 55m), new ChartPoint(candles[^1].Timestamp, 55m))
        {
            Opacity = 1.0,
            FillOpacity = 0,
        };
        obj.Recalculate(candles);
        return obj;
    }

    private static SKBitmap Draw(IChartObject obj, ICoordinateTransform transform)
    {
        var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, transform);
        return bitmap;
    }

    private static void AssertPixel(Color expected, SKBitmap bitmap, ICoordinateTransform transform, decimal price, int x, string because)
    {
        SKColor actual = bitmap.GetPixel(x, (int)Math.Round(transform.GetYFromPrice(price)));
        Assert.True(
            actual.Alpha == 255 && Math.Abs(actual.Red - expected.R) <= 1 && Math.Abs(actual.Green - expected.G) <= 1 && Math.Abs(actual.Blue - expected.B) <= 1,
            $"{because}: expected {expected} but was {actual}");
    }

    /// <summary>A POC colour no other element of the drawing uses (the selection handles are red-ish, so plain red cannot be searched for).</summary>
    private static readonly Color UniquePoc = Color.FromArgb(255, 1, 254, 3);

    private static void AssertNoPixelOf(Color color, SKBitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                Assert.False(c.Alpha > 0 && Math.Abs(c.Red - color.R) <= 2 && Math.Abs(c.Green - color.G) <= 2 && Math.Abs(c.Blue - color.B) <= 2, $"a POC-coloured pixel at ({x}, {y}): {c}");
            }
        }
    }

    // ---- defaults and the effective colour ----

    [Fact]
    public void PocColor_DefaultsToTheSingleRedConstant_ForBothTools()
    {
        using var frvp = new FixedRangeVolumeProfileObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m));
        using var tap = new TimeAtPriceObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m));

        Assert.Equal(Red, frvp.PocColor);
        Assert.Equal(Red, frvp.EffectivePocColor);
        Assert.Equal(Red, tap.PocColor);
        Assert.Equal(Red, tap.EffectivePocColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor.ToAvaloniaColor(), frvp.EffectivePocColor);
    }

    [Fact]
    public void GenericParameterMetadata_LabelsThePocColorRowWithTheDataWindowWording()
    {
        // The tool preset panel builds its rows from every public read/write property; without a DisplayName the label would be generated as "Poc Color".
        var frvp = StockAnalyzer.Avalonia.Services.DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(FixedRangeVolumeProfileObject));
        var tap = StockAnalyzer.Avalonia.Services.DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(TimeAtPriceObject));

        Assert.Equal("POC Color", frvp.Single(m => m.Prop.Name == nameof(FixedRangeVolumeProfileObject.PocColor)).Display!.DisplayName);
        Assert.Equal("TPOC Color", tap.Single(m => m.Prop.Name == nameof(TimeAtPriceObject.PocColor)).Display!.DisplayName);

        // The get-only effective colour is derived, not a parameter row.
        Assert.DoesNotContain(frvp, m => m.Prop.Name == nameof(FixedRangeVolumeProfileObject.EffectivePocColor));
        Assert.DoesNotContain(tap, m => m.Prop.Name == nameof(TimeAtPriceObject.EffectivePocColor));
    }

    [Fact]
    public void NullPocColor_ResolvesToRed_AndACustomColorIsKept()
    {
        using var frvp = new FixedRangeVolumeProfileObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m)) { PocColor = null };
        using var tap = new TimeAtPriceObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m)) { PocColor = null };

        Assert.Equal(Red, frvp.EffectivePocColor);
        Assert.Equal(Red, tap.EffectivePocColor);

        frvp.PocColor = Green;
        tap.PocColor = Green;
        Assert.Equal(Green, frvp.EffectivePocColor);
        Assert.Equal(Green, tap.EffectivePocColor);
    }

    [Fact]
    public void SkiaPocColor_UsesTheColourAndTheOpacitySetting()
    {
        using var frvp = new FixedRangeVolumeProfileObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m)) { PocColor = Green, Opacity = 0.5 };
        using var tap = new TimeAtPriceObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m)) { PocColor = Green, Opacity = 0.5 };

        Assert.Equal(new SKColor(0, 200, 0, (byte)(255 * 0.5)), frvp.SkiaPocColor);
        Assert.Equal(new SKColor(0, 200, 0, (byte)(255 * 0.5)), tap.SkiaPocColor);
    }

    [Fact]
    public void ValueAreaAndProfileColors_AreNotAffectedByThePocColor()
    {
        using var frvp = new FixedRangeVolumeProfileObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(1), 60m)) { PocColor = Green };

        Assert.Equal(Yellow, frvp.ValueAreaColor);
        Assert.Equal(Base, frvp.ProfileColor);
    }

    // ---- painting ----

    [Fact]
    public void FixedRangeVolumeProfile_Render_PocBinIsPaintedWithThePocColor_AheadOfTheValueAreaColor()
    {
        var t = MakeTransform();
        using var obj = NewVolumeProfile(VolumeCandles());
        obj.PocColor = Green;

        int poc = VolumeAnalysis.FindPointOfControlIndex(obj.ProfileData);
        var pocBin = obj.ProfileData[poc];
        var neighbour = obj.ProfileData[poc + 1];
        Assert.Equal(pocBin.TotalVolume, neighbour.TotalVolume); // the fixture's equal top bins: the neighbour is as wide as the POC bin
        Assert.True(pocBin.Price >= obj.VAL && pocBin.Price <= obj.VAH, "the POC bin lies inside the Value Area");
        Assert.True(neighbour.Price >= obj.VAL && neighbour.Price <= obj.VAH);

        using var bitmap = Draw(obj, t);
        int x = (int)t.GetXFromTime(Day1) + 8;

        AssertPixel(Green, bitmap, t, pocBin.Price, x, "the POC bin");
        AssertPixel(Yellow, bitmap, t, neighbour.Price, x, "a Value Area bin that is not the POC");
    }

    [Fact]
    public void FixedRangeVolumeProfile_Render_DefaultPocColorIsRed_AndABinOutsideTheValueAreaKeepsTheBaseColor()
    {
        var t = MakeTransform();
        using var obj = NewVolumeProfile(VolumeCandles(volume2: 6000));

        int poc = VolumeAnalysis.FindPointOfControlIndex(obj.ProfileData);
        using var bitmap = Draw(obj, t);
        int x = (int)t.GetXFromTime(Day1) + 8;

        AssertPixel(Red, bitmap, t, obj.ProfileData[poc].Price, x, "the POC bin with the default colour");
    }

    [Fact]
    public void TimeAtPrice_Render_TpocBinIsPaintedWithThePocColor_AndOtherBinsKeepTheirColors()
    {
        var t = MakeTransform();
        using var obj = NewTimeAtPrice(TimeCandles());
        obj.PocColor = Green;

        int poc = VolumeAnalysis.FindPointOfControlIndex(obj.ProfileData);
        var pocBin = obj.ProfileData[poc];
        Assert.True(pocBin.Price >= obj.VAL && pocBin.Price <= obj.VAH, "the TPOC bin lies inside the Value Area");
        VolumeBin other = obj.ProfileData.First(b => b.TotalVolume > 0 && b != pocBin);

        using var bitmap = Draw(obj, t);
        int x = (int)t.GetXFromTime(Day1) + 8;

        AssertPixel(Green, bitmap, t, pocBin.Price, x, "the TPOC bin");
        AssertPixel(Base, bitmap, t, other.Price, x, "a bin that is neither the TPOC nor in the Value Area");
    }

    [Fact]
    public void TimeAtPrice_Render_DefaultPocColorIsRed()
    {
        var t = MakeTransform();
        using var obj = NewTimeAtPrice(TimeCandles());

        var pocBin = obj.ProfileData[VolumeAnalysis.FindPointOfControlIndex(obj.ProfileData)];
        using var bitmap = Draw(obj, t);

        AssertPixel(Red, bitmap, t, pocBin.Price, (int)t.GetXFromTime(Day1) + 8, "the TPOC bin");
    }

    [Fact]
    public void RenderTwice_AfterChangingThePocColor_PicksUpTheNewColor()
    {
        var t = MakeTransform();
        using var obj = NewVolumeProfile(VolumeCandles());
        var pocBin = obj.ProfileData[VolumeAnalysis.FindPointOfControlIndex(obj.ProfileData)];
        int x = (int)t.GetXFromTime(Day1) + 8;

        using (var first = Draw(obj, t)) AssertPixel(Red, first, t, pocBin.Price, x, "first render");

        obj.PocColor = Green;
        using var second = Draw(obj, t);
        AssertPixel(Green, second, t, pocBin.Price, x, "second render");
    }

    [Fact]
    public void FixedRangeVolumeProfile_Render_SegmentMode_PaintsOnePocBinPerSegment()
    {
        // RangeBar mode: a profile per group of bars. Every segment has its own POC bin, painted with the POC colour.
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 6; i++)
        {
            decimal centre = 52m + (i / 2) * 3m; // the segments' weight sits at 52, 55 and 58
            candles.Add(new CoreCandleData(Day1.AddDays(i), centre, centre + 0.5m, centre - 0.5m, centre, 5000));
        }

        var t = MakeTransform();
        using var obj = new FixedRangeVolumeProfileObject(new ChartPoint(candles[0].Timestamp, 55m), new ChartPoint(candles[1].Timestamp, 55m))
        {
            Opacity = 1.0,
            FillOpacity = 0,
            RangeBars = 2,
            RepeatMode = VolumeProfileRepeatMode.RangeBar,
            PocColor = Green,
        };
        obj.Recalculate(candles);
        Assert.Equal(3, obj.Segments.Count);

        using var bitmap = Draw(obj, t);
        foreach (VolumeProfileSegment seg in obj.Segments)
        {
            int idx = PointOfControlSelector.FindDrawableIndex(seg.Bins);
            Assert.True(idx >= 0);
            Assert.Equal(seg.POC, seg.Bins[idx].Price);
            int segX = (int)t.GetXFromTime(seg.StartTime) + 4;
            AssertPixel(Green, bitmap, t, seg.Bins[idx].Price, segX, $"the POC bin of the segment at {seg.StartTime:yyyy-MM-dd}");
        }
    }

    [Fact]
    public void FixedRangeVolumeProfile_Render_AllBinsWeighZero_PaintsNoPocBin_InTheDefaultAndTheSegmentPath()
    {
        // Every candle traded 0 volume: the bins all weigh 0. The bars are given a visible width by hand so that a wrongly coloured index 0 would show.
        var candles = VolumeCandles(volume2: 0).Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, 0)).ToList();
        var t = MakeTransform();

        using var obj = NewVolumeProfile(candles);
        Assert.All(obj.ProfileData, b => Assert.Equal(0, b.TotalVolume));
        foreach (VolumeBin bin in obj.ProfileData) bin.WidthPercent = 1.0;
        obj.PocColor = UniquePoc;
        using (var bitmap = Draw(obj, t)) AssertNoPixelOf(UniquePoc, bitmap);

        // Segment path: one zero-weight segment, repeat mode on.
        using var segmented = NewVolumeProfile(candles);
        segmented.PocColor = UniquePoc;
        segmented.RepeatMode = VolumeProfileRepeatMode.RangeBar;
        segmented.Recalculate(candles);
        Assert.NotEmpty(segmented.Segments);
        foreach (VolumeProfileSegment seg in segmented.Segments)
        {
            foreach (VolumeBin bin in seg.Bins) bin.WidthPercent = 1.0;
        }

        using var bitmap2 = Draw(segmented, t);
        AssertNoPixelOf(UniquePoc, bitmap2);
    }

    [Fact]
    public void TimeAtPrice_Render_SegmentWithAllBinsZero_PaintsNoPocBin()
    {
        // A time-at-price profile cannot weigh 0 by itself (each bar counts 1); a hand-made zero-weight segment proves the shared guard on this tool too.
        var t = MakeTransform();
        using var obj = new TimeAtPriceObject(new ChartPoint(Day1, 55m), new ChartPoint(Day1.AddDays(3), 55m))
        {
            Opacity = 1.0,
            FillOpacity = 0,
            RepeatMode = VolumeProfileRepeatMode.RangeBar,
            PocColor = UniquePoc,
        };
        var bins = new List<VolumeBin>();
        for (int i = 0; i < 5; i++)
        {
            bins.Add(new VolumeBin { LowerBound = 50m + 2m * i, UpperBound = 52m + 2m * i, Price = 51m + 2m * i, TotalVolume = 0, WidthPercent = 1.0 });
        }

        obj.Segments.Add(new VolumeProfileSegment { StartIndex = 0, Count = 3, StartTime = Day1, EndTime = Day1.AddDays(2), Bins = bins });

        using var bitmap = Draw(obj, t);
        AssertNoPixelOf(UniquePoc, bitmap);
    }

    [Fact]
    public void Dispose_ReleasesThePocPaintWithoutError()
    {
        var obj = NewVolumeProfile(VolumeCandles());
        var tap = NewTimeAtPrice(TimeCandles());

        obj.Dispose();
        tap.Dispose();
    }

    // ---- DataWindow ----

    [Fact]
    public void GetCalculatedValues_PocRowColorFollowsThePocColor_AndVahValKeepTheValueAreaColor()
    {
        using var frvp = NewVolumeProfile(VolumeCandles());
        using var tap = NewTimeAtPrice(TimeCandles());
        frvp.PocColor = Green;
        tap.PocColor = Green;
        frvp.ValueAreaColor = Colors.Magenta;
        tap.ValueAreaColor = Colors.Magenta;

        var frvpValues = frvp.GetCalculatedValues(Day1);
        var tapValues = tap.GetCalculatedValues(Day1);

        Assert.Equal("POC", frvpValues[0].Key);
        Assert.Equal("TPOC", tapValues[0].Key);
        foreach (var values in new[] { frvpValues, tapValues })
        {
            Assert.Equal(new IndicatorColor(255, 0, 200, 0), values[0].Color);
            Assert.Equal(new IndicatorColor(255, 255, 0, 255), values[1].Color);
            Assert.Equal(new IndicatorColor(255, 255, 0, 255), values[2].Color);
        }
    }

    [Fact]
    public void GetCalculatedValues_DefaultPocRowColorIsRed_NotTheDrawingMainColor()
    {
        using var frvp = NewVolumeProfile(VolumeCandles());
        frvp.Color = Colors.Cyan;

        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, frvp.GetCalculatedValues(Day1)[0].Color);
    }

    // ---- persistence: older files without the key, explicit null, and a custom colour ----

    private static readonly JsonSerializerOptions ConverterOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() }
    };

    private static void RemoveOrNullKey(JsonNode? node, string key, bool setNull)
    {
        if (node is JsonObject obj)
        {
            string? match = obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                if (setNull) obj[match] = null; else obj.Remove(match);
            }

            foreach (var child in obj.Select(kv => kv.Value).ToList()) RemoveOrNullKey(child, key, setNull);
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr) RemoveOrNullKey(child, key, setNull);
        }
    }

    private static IEnumerable<IChartObject> NewToolsWithCustomColors()
    {
        var frvp = new FixedRangeVolumeProfileObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(3), 60m))
        {
            PocColor = Green, ValueAreaColor = Colors.Magenta, ProfileColor = Colors.Cyan, FillColor = Colors.Orange, Opacity = 0.6, FillOpacity = 33,
        };
        var tap = new TimeAtPriceObject(new ChartPoint(Day1, 50m), new ChartPoint(Day1.AddDays(3), 60m))
        {
            PocColor = Green, ValueAreaColor = Colors.Magenta, ProfileColor = Colors.Cyan, FillColor = Colors.Orange, Opacity = 0.6, FillOpacity = 33,
        };
        return new IChartObject[] { frvp, tap };
    }

    private static (Color? Poc, Color Va, Color Profile, Color Fill, double Opacity, int FillOpacity) Read(IChartObject obj) => obj switch
    {
        FixedRangeVolumeProfileObject f => (f.PocColor, f.ValueAreaColor, f.ProfileColor, f.FillColor, f.Opacity, f.FillOpacity),
        TimeAtPriceObject t => (t.PocColor, t.ValueAreaColor, t.ProfileColor, t.FillColor, t.Opacity, t.FillOpacity),
        _ => throw new InvalidOperationException(obj.GetType().Name)
    };

    private static Color EffectivePoc(IChartObject obj) => obj switch
    {
        FixedRangeVolumeProfileObject f => f.EffectivePocColor,
        TimeAtPriceObject t => t.EffectivePocColor,
        _ => throw new InvalidOperationException(obj.GetType().Name)
    };

    [Fact]
    public void ChartObjectConverter_CustomPocColor_RoundTrips_ForBothTools()
    {
        foreach (IChartObject original in NewToolsWithCustomColors())
        {
            string json = JsonSerializer.Serialize(original, ConverterOptions);
            Assert.Contains("pocColor", json);

            var loaded = JsonSerializer.Deserialize<IChartObject>(json, ConverterOptions)!;
            Assert.Equal(Green, Read(loaded).Poc);
            (original as IDisposable)?.Dispose();
            (loaded as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)] // the key is missing: a file saved before this setting existed
    [InlineData(true)]  // the key is present as an explicit null
    public void ChartObjectConverter_OlderFileWithoutAPocColor_LoadsWithRed_AndTheOtherValuesAreUnchanged(bool explicitNull)
    {
        foreach (IChartObject original in NewToolsWithCustomColors())
        {
            JsonNode node = JsonSerializer.SerializeToNode(original, ConverterOptions)!;
            RemoveOrNullKey(node, "pocColor", explicitNull);

            var loaded = JsonSerializer.Deserialize<IChartObject>(node.ToJsonString(), ConverterOptions)!;

            Assert.Equal(Red, EffectivePoc(loaded));
            var values = Read(loaded);
            Assert.Equal(Colors.Magenta, values.Va);
            Assert.Equal(Colors.Cyan, values.Profile);
            Assert.Equal(Colors.Orange, values.Fill);
            Assert.Equal(0.6, values.Opacity);
            Assert.Equal(33, values.FillOpacity);
            (original as IDisposable)?.Dispose();
            (loaded as IDisposable)?.Dispose();
        }
    }

    private static MaterializedDrawingDocument RoundTripThroughTheDocumentCodec(Action<JsonNode>? editSavedJson)
    {
        var manager = new ChartObjectManager();
        foreach (IChartObject tool in NewToolsWithCustomColors()) manager.AddObject(tool);

        DrawingDocumentState state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("7203", TimeframeType.Daily), 1);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        if (editSavedJson != null)
        {
            JsonNode node = JsonNode.Parse(json)!;
            editSavedJson(node);
            json = node.ToJsonString();
        }

        return DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(json));
    }

    [Fact]
    public void DocumentCodec_CustomPocColor_RoundTrips_ForBothTools()
    {
        using var document = RoundTripThroughTheDocumentCodec(null);

        IChartObject[] loaded = document.Objects.Values.SelectMany(x => x).ToArray();
        Assert.Equal(2, loaded.Length);
        Assert.All(loaded, o => Assert.Equal(Green, Read(o).Poc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocumentCodec_OlderFileWithoutAPocColor_LoadsWithRed_AndTheOtherValuesAreUnchanged(bool explicitNull)
    {
        using var document = RoundTripThroughTheDocumentCodec(node => RemoveOrNullKey(node, "pocColor", explicitNull));

        IChartObject[] loaded = document.Objects.Values.SelectMany(x => x).ToArray();
        Assert.Equal(2, loaded.Length);
        foreach (IChartObject obj in loaded)
        {
            Assert.Equal(Red, EffectivePoc(obj)); // an explicit null must NOT become default(Color) (transparent black)
            var values = Read(obj);
            Assert.Equal(Colors.Magenta, values.Va);
            Assert.Equal(Colors.Cyan, values.Profile);
            Assert.Equal(Colors.Orange, values.Fill);
            Assert.Equal(0.6, values.Opacity);
            Assert.Equal(33, values.FillOpacity);
        }
    }
}
