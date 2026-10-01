using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>sa_implement (Tickers settings category, Y:\Temp\sa_implementation_plan_TickersSettingsCategory.md
/// Phases 1 and 3): configuration defaults/validation and the TickersSettingsManager contract, using a
/// per-test temp file through the manager's optional file path.</summary>
public class TickersSettingsManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tickers_settings_tests_" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "user_tickers_settings.json");

    public TickersSettingsManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private TickersSettingsManager Create(TickersSettings? config = null) =>
        new(Options.Create(config ?? new TickersSettings()), null, FilePath);

    [Fact]
    public void Configuration_Defaults_AreFiveSecondsAndStopToggles()
    {
        var config = new TickersSettings();

        Assert.Equal(5, config.AutoPlayDefaultIntervalSeconds);
        Assert.Equal(3600, config.AutoPlayMaxIntervalSeconds);
        Assert.True(config.AutoPlayDefaultStopAtListEnd);
        Assert.True(config.AutoPlayDefaultStopOnListChange);
        Assert.Equal(1, TickersSettings.MinAutoPlayIntervalSeconds);
        config.Validate();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 0)]
    [InlineData(10, 11)]
    public void Configuration_Validate_RejectsInvalidValues(int max, int defaultInterval)
    {
        var config = new TickersSettings { AutoPlayMaxIntervalSeconds = max, AutoPlayDefaultIntervalSeconds = defaultInterval };

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void Constructor_WithInvalidConfiguration_Throws()
    {
        var config = new TickersSettings { AutoPlayMaxIntervalSeconds = 0 };

        Assert.Throws<InvalidOperationException>(() => Create(config));
    }

    [Fact]
    public void Defaults_FollowConfiguration()
    {
        var manager = Create(new TickersSettings
        {
            AutoPlayDefaultIntervalSeconds = 7,
            AutoPlayMaxIntervalSeconds = 20,
            AutoPlayDefaultStopAtListEnd = false,
            AutoPlayDefaultStopOnListChange = false
        });

        Assert.Equal(7, manager.AutoPlayIntervalSeconds);
        Assert.Equal(20, manager.AutoPlayMaxIntervalSeconds);
        Assert.Equal(1, manager.AutoPlayMinIntervalSeconds);
        Assert.False(manager.AutoPlayStopAtListEnd);
        Assert.False(manager.AutoPlayStopOnListChange);
        Assert.Equal(TickerColumnSelectionScope.PerList, manager.ColumnSelectionScope);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public void SetAutoPlayIntervalSeconds_Boundaries_AreAccepted(int value)
    {
        var manager = Create();

        Assert.True(manager.SetAutoPlayIntervalSeconds(value));
        Assert.Equal(value, manager.AutoPlayIntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void SetAutoPlayIntervalSeconds_OutOfRange_IsRejectedWithoutSideEffect(int value)
    {
        var manager = Create();
        var raised = new List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(manager.SetAutoPlayIntervalSeconds(value));

        Assert.Equal(5, manager.AutoPlayIntervalSeconds);
        Assert.Empty(raised);
    }

    [Fact]
    public void SetColumnSelectionScope_UndefinedValue_IsRejected()
    {
        var manager = Create();

        Assert.False(manager.SetColumnSelectionScope((TickerColumnSelectionScope)99));

        Assert.Equal(TickerColumnSelectionScope.PerList, manager.ColumnSelectionScope);
    }

    [Fact]
    public void Setters_RaisePropertyChangedOncePerRealChange_AndNotForEqualValues()
    {
        var manager = Create();
        var raised = new List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        manager.SetAutoPlayStopAtListEnd(false);
        manager.SetAutoPlayStopAtListEnd(false);
        manager.SetAutoPlayStopOnListChange(false);
        manager.SetAutoPlayIntervalSeconds(9);
        manager.SetAutoPlayIntervalSeconds(9);
        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);
        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);

        Assert.Equal(new List<string?>
        {
            nameof(manager.AutoPlayStopAtListEnd),
            nameof(manager.AutoPlayStopOnListChange),
            nameof(manager.AutoPlayIntervalSeconds),
            nameof(manager.ColumnSelectionScope)
        }, raised);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllValues()
    {
        var manager = Create();
        manager.SetAutoPlayStopAtListEnd(false);
        manager.SetAutoPlayStopOnListChange(false);
        manager.SetAutoPlayIntervalSeconds(12);
        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);
        await manager.SaveAsync();

        var reloaded = Create();
        await reloaded.LoadAsync();

        Assert.False(reloaded.AutoPlayStopAtListEnd);
        Assert.False(reloaded.AutoPlayStopOnListChange);
        Assert.Equal(12, reloaded.AutoPlayIntervalSeconds);
        Assert.Equal(TickerColumnSelectionScope.Shared, reloaded.ColumnSelectionScope);
    }

    [Fact]
    public async Task OverlappingSaves_AllComplete_WithoutErrors_AndLeaveALoadableFile()
    {
        // sa_improve (constraint check L6): the saves share one "<file>.tmp" opened with FileShare.None, so unserialized
        // overlapping saves make all but one fail with an IOException (logged, value not written).
        var logger = new ErrorCountingLogger();
        var manager = new TickersSettingsManager(Options.Create(new TickersSettings()), logger, FilePath);
        manager.SetAutoPlayIntervalSeconds(7);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(manager.SaveAsync)));

        Assert.Equal(0, logger.Errors);
        var reloaded = Create();
        await reloaded.LoadAsync();
        Assert.Equal(7, reloaded.AutoPlayIntervalSeconds);
    }

    private sealed class ErrorCountingLogger : Microsoft.Extensions.Logging.ILogger<TickersSettingsManager>
    {
        private int _errors;
        public int Errors => Volatile.Read(ref _errors);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error) Interlocked.Increment(ref _errors);
        }
    }

    [Fact]
    public async Task Load_MissingFile_KeepsDefaultsAndWritesNothing()
    {
        var manager = Create();

        await manager.LoadAsync();

        Assert.Equal(5, manager.AutoPlayIntervalSeconds);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Load_AbsentKeys_KeepDefaults()
    {
        await File.WriteAllTextAsync(FilePath, "{ \"AutoPlayIntervalSeconds\": 30 }");
        var manager = Create();

        await manager.LoadAsync();

        Assert.Equal(30, manager.AutoPlayIntervalSeconds);
        Assert.True(manager.AutoPlayStopAtListEnd);
        Assert.True(manager.AutoPlayStopOnListChange);
        Assert.Equal(TickerColumnSelectionScope.PerList, manager.ColumnSelectionScope);
    }

    [Theory]
    [InlineData("{ \"AutoPlayIntervalSeconds\": 0, \"AutoPlayStopAtListEnd\": false }")]
    [InlineData("{ \"AutoPlayIntervalSeconds\": 3601, \"AutoPlayStopAtListEnd\": false }")]
    [InlineData("{ \"ColumnSelectionScope\": 99, \"AutoPlayStopAtListEnd\": false }")]
    [InlineData("{ this is not json")]
    public async Task Load_InvalidFile_FailsAsAWhole_KeepsDefaults_AndLeavesFileUntouched(string json)
    {
        await File.WriteAllTextAsync(FilePath, json);
        var manager = Create();
        var raised = new List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await manager.LoadAsync();

        Assert.Equal(5, manager.AutoPlayIntervalSeconds);
        Assert.True(manager.AutoPlayStopAtListEnd);
        Assert.Equal(TickerColumnSelectionScope.PerList, manager.ColumnSelectionScope);
        Assert.Empty(raised);
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
    }
}
