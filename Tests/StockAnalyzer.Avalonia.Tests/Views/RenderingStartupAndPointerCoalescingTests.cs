using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

public sealed class RenderingStartupAndPointerCoalescingTests
{
    [Fact]
    public void ResolveMaxGpuResourceSizeBytes_DesignMode_ReturnsDefault128MiB()
    {
        using var dir = TempFileFixture();
        File.WriteAllText(dir.ConfigPath, "{\"Rendering\":{\"MaxGpuResourceSizeBytes\":268435456}}");

        long resolved = Program.ResolveMaxGpuResourceSizeBytes(dir.ConfigPath, isDesignModeOverride: true);

        Assert.Equal(RenderingLimits.DefaultMaxGpuResourceSizeBytes, resolved);
    }

    [Fact]
    public void ResolveMaxGpuResourceSizeBytes_ValidConfiguredValue_IsReturned()
    {
        using var dir = TempFileFixture();
        File.WriteAllText(dir.ConfigPath, "{\"Rendering\":{\"MaxGpuResourceSizeBytes\":268435456}}");

        long resolved = Program.ResolveMaxGpuResourceSizeBytes(dir.ConfigPath, isDesignModeOverride: false);

        Assert.Equal(268_435_456L, resolved);
    }

    [Theory]
    [InlineData("{ broken json !!")]
    [InlineData("{\"Rendering\":{\"MaxGpuResourceSizeBytes\":\"not-a-number\"}}")]
    public void ResolveMaxGpuResourceSizeBytes_MalformedJsonOrWrongType_FallsBackToDefault(string json)
    {
        using var dir = TempFileFixture();
        File.WriteAllText(dir.ConfigPath, json);

        long resolved = Program.ResolveMaxGpuResourceSizeBytes(dir.ConfigPath, isDesignModeOverride: false);

        Assert.Equal(RenderingLimits.DefaultMaxGpuResourceSizeBytes, resolved);
    }

    [Theory]
    [InlineData(1024L)]
    [InlineData(9_999_999_999L)]
    public void ResolveMaxGpuResourceSizeBytes_ExplicitOutOfHardBand_ThrowsNamingKey(long invalidBytes)
    {
        using var dir = TempFileFixture();
        File.WriteAllText(dir.ConfigPath, $"{{\"Rendering\":{{\"MaxGpuResourceSizeBytes\":{invalidBytes}}}}}");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Program.ResolveMaxGpuResourceSizeBytes(dir.ConfigPath, isDesignModeOverride: false));
        Assert.Contains(RenderingSettingsValidator.MaxGpuResourceSizeBytesKey, ex.Message);
    }

    [Fact]
    public void ResolveMaxGpuResourceSizeBytes_MissingFile_FallsBackToDefault()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json");

        long resolved = Program.ResolveMaxGpuResourceSizeBytes(missingPath, isDesignModeOverride: false);

        Assert.Equal(RenderingLimits.DefaultMaxGpuResourceSizeBytes, resolved);
    }

    [Fact]
    public void BuildAvaloniaApp_DoesNotThrow()
    {
        var builder = Program.BuildAvaloniaApp();
        Assert.NotNull(builder);
    }

    [Fact]
    public void AvaloniaDiContainer_ResolvesPoolAndConfiguredTimeFrameManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketDataSettings:TimeFrameCacheMaxEntries"] = "300",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCommonServices(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsAssignableFrom<IReadOnlyParquetConnectionPool>(provider.GetRequiredService<IReadOnlyParquetConnectionPool>());
        var tfm = Assert.IsType<TimeFrameManager>(provider.GetRequiredService<ITimeFrameManager>());
        var maxField = typeof(TimeFrameManager).GetField("_maxCacheEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(300, maxField!.GetValue(tfm));

        var invalidConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketDataSettings:TimeFrameCacheMaxEntries"] = "0",
            })
            .Build();
        using var invalidReadOnly = new ServiceCollection()
            .AddStockAnalyzerReadOnlyCore(invalidConfig)
            .BuildServiceProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidReadOnly.GetRequiredService<IStockAnalyzerSettings>());
    }

    [AvaloniaFact]
    public void ChartBaseControl_CoalescedPointerMove_FlushesAndResetsOnExitAndDetach()
    {
        var vm = new ChartViewModel
        {
            Symbol = "TEST",
            Candles = SwingingCandles.Create(),
        };

        var control = new ChartBaseControl
        {
            Width = 800,
            Height = 500,
            ViewModel = vm,
        };
        var window = new Window
        {
            Width = 800,
            Height = 500,
            Content = control,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

            window.MouseMove(new Point(300, 200));
            window.MouseMove(new Point(320, 210));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Assert.False(control.IsPointerInvalidateQueued);

            // Re-queue then verify margin/pointer exit resets pending flag.
            window.MouseMove(new Point(340, 210));
            window.MouseMove(new Point(2, 200));
            Assert.False(control.IsPointerInvalidateQueued);

            window.MouseMove(new Point(350, 210));
            window.Content = null;
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Assert.False(control.IsPointerInvalidateQueued);
        }
        finally
        {
            window.Close();
        }
    }

    private static ConfigTempDir TempFileFixture() => new();

    private sealed class ConfigTempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"sa_render_cfg_{Guid.NewGuid():N}");
        public string ConfigPath => Path.Combine(Root, "appsettings.json");

        public ConfigTempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
