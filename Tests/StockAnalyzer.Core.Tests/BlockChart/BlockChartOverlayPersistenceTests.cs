using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// TC-16 (persistence): parameters, colours and ON/OFF of the four overlays survive a save and a restart, field by field. The discriminator of a parameter in the JSON is its CLASS NAME
/// (WorkspacePolymorphicResolver), so these tests also pin the four names: renaming a class after release would orphan every saved overlay.
/// </summary>
public class BlockChartOverlayPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_block_overlay_" + Guid.NewGuid().ToString("N"));

    public BlockChartOverlayPersistenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static readonly (IndicatorType Type, string ClassName)[] PersistedNames =
    {
        (IndicatorType.Renko, "CoreRenkoChartParameter"),
        (IndicatorType.PointAndFigure, "CorePointAndFigureChartParameter"),
        (IndicatorType.Kagi, "CoreKagiChartParameter"),
        (IndicatorType.ThreeLineBreak, "CoreThreeLineBreakChartParameter"),
    };

    /// <summary>The default setting of the type with EVERY field changed away from its default, so a field that is not persisted cannot go unnoticed.</summary>
    private static CoreIndicatorSettings Customized(IndicatorType type)
    {
        var s = DefaultCoreIndicatorSettings.GetDefault().Single(d => d.TypeEnum == type);
        s.IsEnabled = true;
        s.Thickness = 3.5;
        s.DotSize = 9.5;
        s.Color = new IndicatorColor(200, 10, 20, 30);
        s.UpColor = new IndicatorColor(201, 11, 21, 31);
        s.DownColor = new IndicatorColor(202, 12, 22, 32);
        s.UseUpDownColors = false;
        s.Style = CoreLineStyle.Dash;
        s.Offset = 2;

        foreach (var (config, index) in s.SeriesColors.Select((c, i) => (c, i)))
        {
            config.Color = new IndicatorColor((byte)(210 + index), (byte)(40 + index), (byte)(50 + index), (byte)(60 + index));
        }

        switch (s.ParameterObject)
        {
            case CoreRenkoChartParameter r:
                ApplySizing(r);
                r.ReversalBricks = 4;
                break;
            case CorePointAndFigureChartParameter p:
                ApplySizing(p);
                p.ReversalBoxes = 5;
                break;
            case CoreKagiChartParameter k:
                ApplySizing(k);
                break;
            case CoreThreeLineBreakChartParameter t:
                t.NumberOfLines = 7;
                t.MinimumMove = 0.75m;
                break;
        }

        return s;

        static void ApplySizing(CoreBlockChartSizingParameterBase b)
        {
            b.SizingMode = ChartSizingMode.Percentage;
            b.ManualSize = 2.5m;
            b.Percentage = 3.25m;
            b.AtrPeriod = 21;
            b.AtrMultiplier = 1.75m;
            b.RoundingMode = ChartRoundingMode.Floor;
            b.FallbackMode = AutoFallbackMode.Fixed;
        }
    }

    private static void AssertSame(CoreIndicatorSettings expected, CoreIndicatorSettings actual)
    {
        Assert.Equal(expected.TypeEnum, actual.TypeEnum);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.Thickness, actual.Thickness);
        Assert.Equal(expected.DotSize, actual.DotSize);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.UpColor, actual.UpColor);
        Assert.Equal(expected.DownColor, actual.DownColor);
        Assert.Equal(expected.UseUpDownColors, actual.UseUpDownColors);
        Assert.Equal(expected.Style, actual.Style);
        Assert.Equal(expected.Offset, actual.Offset);
        Assert.Equal(expected.IsOverlay, actual.IsOverlay);

        Assert.Equal(expected.SeriesColors.Count, actual.SeriesColors.Count);
        for (int i = 0; i < expected.SeriesColors.Count; i++)
        {
            Assert.Equal(expected.SeriesColors[i].Name, actual.SeriesColors[i].Name);
            Assert.Equal(expected.SeriesColors[i].DisplayName, actual.SeriesColors[i].DisplayName);
            Assert.Equal(expected.SeriesColors[i].Color, actual.SeriesColors[i].Color);
            Assert.Equal(expected.SeriesColors[i].TargetSeries, actual.SeriesColors[i].TargetSeries);
        }

        Assert.NotNull(actual.ParameterObject);
        Assert.Equal(expected.ParameterObject!.GetType(), actual.ParameterObject!.GetType());
        foreach (var property in expected.ParameterObject.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            Assert.Equal(property.GetValue(expected.ParameterObject), property.GetValue(actual.ParameterObject));
        }
    }

    [Fact]
    public async Task Workspace_SaveAndRestart_RestoresEveryField_OfAllFourOverlays()
    {
        var originals = PersistedNames.Select(n => Customized(n.Type)).ToList();
        var workspace = new WorkspaceSettings { Indicators = originals };
        string path = Path.Combine(_directory, "workspace.json");

        var service = new WorkspaceSerializationService();
        await service.SaveAsync(workspace, path);

        // "Restart": a new service instance reads the file.
        var restored = new WorkspaceSerializationService().Deserialize(File.ReadAllBytes(path));

        Assert.NotNull(restored);
        Assert.Equal(originals.Count, restored!.Indicators.Count);
        for (int i = 0; i < originals.Count; i++) AssertSame(originals[i], restored.Indicators[i]);
    }

    [Fact]
    public async Task Workspace_JsonCarriesTheFourClassNamesAsTheDiscriminator_SoARenameWouldBeCaught()
    {
        var workspace = new WorkspaceSettings { Indicators = PersistedNames.Select(n => Customized(n.Type)).ToList() };
        string path = Path.Combine(_directory, "names.json");

        await new WorkspaceSerializationService().SaveAsync(workspace, path);
        string json = Encoding.UTF8.GetString(File.ReadAllBytes(path));

        foreach (var (_, className) in PersistedNames)
        {
            Assert.Contains($"\"{className}\"", json);
        }

        // The class names themselves (the persisted API): the types must still exist under exactly these names.
        foreach (var (_, className) in PersistedNames)
        {
            Assert.NotNull(typeof(CoreIndicatorParameterBase).Assembly.GetType($"StockAnalyzer.Core.Models.Parameters.{className}"));
        }
    }

    [Fact]
    public async Task IndicatorListFile_SaveAndRestart_RestoresEveryField()
    {
        string path = Path.Combine(_directory, "indicators.json");
        var originals = PersistedNames.Select(n => Customized(n.Type)).ToList();

        using (var store = new IndicatorListStore(path, "block chart overlays", NullLogger.Instance))
        {
            foreach (var original in originals) await store.SaveAsync(original);
        }

        using var restarted = new IndicatorListStore(path, "block chart overlays", NullLogger.Instance);
        var loaded = restarted.GetAll();

        Assert.Equal(originals.Count, loaded.Count);
        foreach (var original in originals)
        {
            AssertSame(original, loaded.Single(l => l.TypeEnum == original.TypeEnum));
        }
    }

    [Fact]
    public void ParameterClone_IsAnIndependentCopyOfEveryField()
    {
        foreach (var (type, _) in PersistedNames)
        {
            var original = Customized(type).ParameterObject!;
            var clone = original.Clone();

            Assert.NotSame(original, clone);
            foreach (var property in original.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(clone));
            }
        }
    }

    [Fact]
    public async Task AFileSavedBeforeTheDotSizeSettingExisted_KeepsTheDefaultDotSize_AndTheOtherFields()
    {
        var workspace = new WorkspaceSettings { Indicators = PersistedNames.Select(n => Customized(n.Type)).ToList() };
        string path = Path.Combine(_directory, "before_dot_size.json");
        await new WorkspaceSerializationService().SaveAsync(workspace, path);

        string json = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.Matches(@"(?i)""dotSize""\s*:\s*9\.5", json);   // the setting IS written
        string withoutDotSize = System.Text.RegularExpressions.Regex.Replace(json, @"(?i)""dotSize""\s*:\s*[-0-9.eE+]+\s*,?", string.Empty);
        Assert.DoesNotContain("otSize", withoutDotSize);

        var restored = new WorkspaceSerializationService().Deserialize(Encoding.UTF8.GetBytes(withoutDotSize));

        Assert.NotNull(restored);
        Assert.Equal(PersistedNames.Length, restored!.Indicators.Count);
        foreach (var setting in restored.Indicators)
        {
            Assert.Equal(IndicatorDefaultConstants.BlockChartOverlayDefaultDotSize, setting.DotSize);
            Assert.Equal(3.5, setting.Thickness);   // everything else still restored
        }
    }
}
