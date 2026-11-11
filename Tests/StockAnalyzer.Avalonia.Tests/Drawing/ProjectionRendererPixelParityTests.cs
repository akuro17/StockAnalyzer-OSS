using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Pixel-for-pixel parity of the renderers whose per-frame paints, paths, dash effects and arrays became cached/stack-allocated
/// (ReverseWatch, Pearson, Frechet, and the FFT harmonics label). The SHA-256 of the rendered bitmap was taken from the
/// implementation before the change and is pinned here.
/// </summary>
public class ProjectionRendererPixelParityTests
{
    private const int Width = 1000;
    private const int Height = 500;
    private static readonly DateTime Start = new(2026, 1, 1);
    private readonly ITestOutputHelper _output;

    public ProjectionRendererPixelParityTests(ITestOutputHelper output) => _output = output;

    private static GenericCoordinateTransform Transform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(Start, Start.AddDays(500), 80m, 125m);
        return transform;
    }

    private static string Hash(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        draw(canvas);
        return Convert.ToHexString(SHA256.HashData(bitmap.Bytes));
    }

    private static List<StockAnalyzer.Core.Models.Point> Path(int count, double offset) =>
        Enumerable.Range(0, count)
            .Select(i => new StockAnalyzer.Core.Models.Point(Start.AddDays(260 + i * 2).Ticks, 100.0 + offset + 6.0 * Math.Sin(i * 0.4)))
            .ToList();

    private static void Fill(PearsonProjectionObject o)
    {
        o.Points.Add(new ChartPoint(Start.AddDays(150), 100m));
        o.Points.Add(new ChartPoint(Start.AddDays(260), 108m));
        o.ProjectedPath = Path(40, 0); o.UpperBandPath = Path(40, 4); o.LowerBandPath = Path(40, -4);
        o.MatchedStartTime = Start.AddDays(40); o.MatchedEndTime = Start.AddDays(120);
    }

    private static void Fill(FrechetProjectionObject o)
    {
        o.Points.Add(new ChartPoint(Start.AddDays(150), 100m));
        o.Points.Add(new ChartPoint(Start.AddDays(260), 108m));
        o.ProjectedPath = Path(40, 0); o.UpperBandPath = Path(40, 4); o.LowerBandPath = Path(40, -4);
        o.MatchedStartTime = Start.AddDays(40); o.MatchedEndTime = Start.AddDays(120);
    }

    private static void Fill(FftProjectionObject o)
    {
        o.Points.Add(new ChartPoint(Start.AddDays(150), 100m));
        o.Points.Add(new ChartPoint(Start.AddDays(260), 108m));
        o.ProjectedPath = Path(40, 0); o.UpperBandPath = Path(40, 4); o.LowerBandPath = Path(40, -4);
        o.DominantHarmonics = new[]
        {
            new FftHarmonicComponent(3, 0.05, 20.04, 5.0, 25.0, 0.1),
            new FftHarmonicComponent(7, 0.11, 9.087, 3.0, 9.0, 0.2),
            new FftHarmonicComponent(11, 0.17, 5.882, 2.0, 4.0, 0.3),
            new FftHarmonicComponent(13, 0.2, 5.0, 1.0, 1.0, 0.4),
        };
    }

    private sealed class Cfg : IReverseWatchRenderConfig
    {
        public Core.Theme.IThemeManager ThemeManager => null!;
        public ChartType ChartType => default;
        public double CurrentPrice => 100;
        public IndicatorColor BullishColor => new(255, 0, 255, 0);
        public IndicatorColor BearishColor => new(255, 255, 0, 0);
        public IndicatorColor NeutralColor => new(255, 128, 128, 128);
        public NamedColor ReversalLabelColor => new("c", SKColors.White);
        public NamedColor PriceLabelColor => new("c", SKColors.White);
        public int VisibleStartIndex => 0;
        public int VisibleCandleCount => 100;
        public ICoordinateTransform? Transform { get; set; }
        public StockAnalyzer.Core.Models.Point MousePosition => default;
        public bool ShowMultiWavePatterns => false;
        public bool ShowGhostProjections => false;
        public int MultiWavePatternMaxLines => 0;
        public IndicatorColor? MultiWaveBullishColor => null;
        public IndicatorColor? MultiWaveBearishColor => null;
        public float GhostProjectionFontSize => 10;
        public bool ShowGhostLabelsOnHoverOnly => false;
        public double RenderScaling => 1;
        public bool IsSubWindowVisible => false;
        public bool InvertOscillator => false;
        public double DefaultDrawingThickness => 1;
        public bool CrosshairLabelVisible => false;
        public ReverseWatchCurveData? ReverseWatchData { get; set; }
        public IndicatorColor GetPhaseColor(ReverseWatchPhase phase) => new(255, (byte)(20 + 25 * (int)phase), (byte)(220 - 20 * (int)phase), 120);
        public bool ShowReverseWatchGrid => false;
        public float ReverseWatchLineThickness => 2;
        public bool ReverseWatchIsMaBased => false;
        public bool ReverseWatchIsLogScaleVolume => false;
        public int ReverseWatchDataCount { get; set; }
    }

    // A spiral around the data centre: its segments cross the 8 phase boundaries (0..8 crossings per segment).
    private static ReverseWatchCurveData Spiral(int count)
    {
        var points = Enumerable.Range(0, count).Select(i =>
        {
            double angle = i * 0.37;
            double radius = 40 + i * 2.2;
            return new ReverseWatchCurvePoint
            {
                Date = Start.AddDays(i),
                VolumeAverage = 1000m + (decimal)(radius * Math.Cos(angle)),
                PriceAverage = 100m + (decimal)(radius * 0.12 * Math.Sin(angle)),
                Open = 1, High = 1, Low = 1, Close = 1, Volume = 1, Index = i
            };
        }).ToList();
        return new ReverseWatchCurveData
        {
            Points = points,
            Bounds = new ReverseWatchCurveBounds { MinPrice = 80m, MaxPrice = 120m, MinVolume = 700m, MaxVolume = 1300m },
            Period = 5,
            StockCode = "T"
        };
    }

    private static string ReverseWatchHash(bool grid, int dataCount)
    {
        var transform = Transform();
        var data = Spiral(120);
        var cfg = new Cfg { ReverseWatchData = data, Transform = transform, ReverseWatchDataCount = dataCount };
        var renderer = new ReverseWatchRenderer();
        return Hash(c => renderer.Render(c, new global::Avalonia.Rect(0, 0, Width, Height), data, grid, default, transform, 2f, cfg));
    }

    private static string ObjectHash(IChartObject obj, bool selected)
    {
        var transform = Transform();
        obj.IsSelected = selected;
        return Hash(c => obj.Render(c, transform));
    }

    // SHA-256 of the bitmap, taken from the code before the change.
    private static readonly Dictionary<string, string> Pinned = new()
    {
        ["rw-nogrid"] = "EDD783C42B934344298A204343951417C4BAD6B4B31D6EDBA95A883736687005",
        ["rw-grid"] = "FFFDA0E3FAD18FDAB9F535FF0656653C5455F021295257D6DB6614E31C6962B2",
        ["rw-window60"] = "F84DCE02780264C76C95B6A9AF0614463CC6CE86330A602B49A8D2DEF81C2956",
        ["pearson-unselected"] = "EA3679E205160BFC8C0041866C4305EC1332010632C9466DBC86131D4E080565",
        ["pearson-selected"] = "270F197B5F0A47C495B8A4B9CB6B58FCB53A1B2BCE0F960EF061D79738D1BD21",
        ["frechet-unselected"] = "2C49599E60487B15C3294D74E32D3E359AB7E3D9DC58508888D4F4B08F7FA3E2",
        ["frechet-selected"] = "E0A8A109B94920FDEA67E7BA6E8133BB856AF71A882BC208D3285E898E59A72A",
        ["fft-unselected"] = "85EB98FB040BE46C9ABBAE21C21ED1296B900BDEA928336DC1C5ED6A1A153FD2",
        ["fft-selected"] = "BDF3FE714D7838016B12B9D77335835E74855974F8BCE6A476A9E9006AA6D2A7",
    };

    [Fact]
    public void RenderedPixelsAreUnchanged()
    {
        var pearson = new PearsonProjectionObject(); Fill(pearson);
        var frechet = new FrechetProjectionObject(); Fill(frechet);
        var fft = new FftProjectionObject(); Fill(fft);
        var actual = new Dictionary<string, string>
        {
            ["rw-nogrid"] = ReverseWatchHash(false, 0),
            ["rw-grid"] = ReverseWatchHash(true, 0),
            ["rw-window60"] = ReverseWatchHash(false, 60),
            ["pearson-unselected"] = ObjectHash(pearson, false),
            ["pearson-selected"] = ObjectHash(pearson, true),
            ["frechet-unselected"] = ObjectHash(frechet, false),
            ["frechet-selected"] = ObjectHash(frechet, true),
            ["fft-unselected"] = ObjectHash(fft, false),
            ["fft-selected"] = ObjectHash(fft, true),
        };
        foreach (var kv in actual) _output.WriteLine($"PIXEL {kv.Key}={kv.Value}");

        Assert.Equal(Pinned.Count, actual.Count);
        foreach (var kv in Pinned) Assert.Equal(kv.Value, actual[kv.Key]);
    }

    [Fact]
    public void RenderingTwiceGivesTheSamePixels()
    {
        var pearson = new PearsonProjectionObject(); Fill(pearson);
        Assert.Equal(ObjectHash(pearson, true), ObjectHash(pearson, true));
        Assert.Equal(ReverseWatchHash(true, 0), ReverseWatchHash(true, 0));
    }
}
