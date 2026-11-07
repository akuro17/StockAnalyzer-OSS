using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using Serilog;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>Tests that swap the process-wide application base directory run alone, never beside another test.</summary>
[CollectionDefinition("ReadOnlyDataRoot", DisableParallelization = true)]
public sealed class ReadOnlyDataRootCollection { }

/// <summary>
/// D-2' Run 1 / P0 (observation only, NO production change): what breaks when the application's <c>Data</c> folder cannot be
/// written. Every measurement runs once in a writable install folder (the control) and once in a read-only one; only the
/// differences matter. The tests print the table that the step log records (Run 3 and Run 4 take their scope from it) and pin only
/// the preconditions of the measurement itself. The Run 1 table was recorded BEFORE Run 2 removed the redirect to the temp folder:
/// from Run 2 on, the resolver rows print the uncreated path below <c>Data</c> instead of a temp path, and the settings saves fail.
/// The start-up rows (A) remain valid until Run 4 fixes the three constructors.
/// Run with <c>--logger "console;verbosity=detailed"</c> to see the output.
/// </summary>
[Collection("ReadOnlyDataRoot")]
public class ReadOnlyStorageP0MeasurementTests
{
    private readonly ITestOutputHelper _output;

    public ReadOnlyStorageP0MeasurementTests(ITestOutputHelper output) => _output = output;

    private const string Ok = "ok";

    private static string Describe(Exception ex)
    {
        var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
        var message = inner.Message.ReplaceLineEndings(" ");
        return $"{inner.GetType().Name}: {(message.Length > 140 ? message[..140] + "..." : message)}";
    }

    private static async Task<string> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Ok;
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    /// <summary>Where a path ended up, relative to the install folder under test.</summary>
    private static string Locate(string path, ReadOnlyDataRootFixture fixture)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(fixture.Root, StringComparison.OrdinalIgnoreCase))
            return "INSTALL FOLDER (" + Path.GetRelativePath(fixture.Root, full) + ")";
        string tempFallback = Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName);
        if (full.StartsWith(tempFallback, StringComparison.OrdinalIgnoreCase))
            return "%TEMP%\\" + PathDiscovery.TempFolderName + " (" + Path.GetRelativePath(tempFallback, full) + ")";
        return "ELSEWHERE (" + ExceptionSanitizer.Sanitize(full) + ")";
    }

    // ---- (a) the dependency-injection container ----------------------------------------------------------------

    private static async Task<Dictionary<string, string>> ResolveEverySingletonAsync(IDataStorageState? state = null)
    {
        var services = new ServiceCollection();
        if (state is not null) services.AddSingleton(state); // the application registers the probed state before the container is built (App.axaml.cs)
        services.AddCommonServices(new ConfigurationBuilder().Build());
        services.AddViewModels();
        var failures = new Dictionary<string, string>();
        await using var provider = services.BuildServiceProvider();
        foreach (var descriptor in services.Where(d => d.Lifetime == ServiceLifetime.Singleton && !d.ServiceType.ContainsGenericParameters).DistinctBy(d => d.ServiceType))
        {
            // The writable store is only built on the writable path; in read-only mode the interfaces resolve to the zero-mutation registry (asserted by the caller).
            if (state is { Current.IsWritable: false } && descriptor.ServiceType == typeof(ModelGenerationRegistry)) continue;
            try
            {
                provider.GetService(descriptor.ServiceType);
            }
            catch (Exception ex)
            {
                failures[descriptor.ServiceType.Name] = Describe(ex);
            }
        }

        return failures;
    }

    [Fact]
    public async Task A_DependencyInjection_ResolveEverySingleton_ControlVersusReadOnly()
    {
        Dictionary<string, string> control, readOnly;
        using (var writable = ReadOnlyDataRootFixture.Create(readOnly: false))
        using (writable.UseAsBaseDirectory())
        {
            control = await ResolveEverySingletonAsync();
        }

        using (var denied = ReadOnlyDataRootFixture.Create())
        using (denied.UseAsBaseDirectory())
        {
            readOnly = await ResolveEverySingletonAsync();
            Assert.False(Directory.Exists(denied.DataRoot)); // nothing could be created beside the application
        }

        _output.WriteLine($"[A] singletons that fail to resolve: control={control.Count}, read-only={readOnly.Count}");
        foreach (var (type, failure) in control) _output.WriteLine($"[A] control failure (unrelated to storage): {type} -> {failure}");
        foreach (var (type, failure) in readOnly.Where(p => !control.ContainsKey(p.Key))) _output.WriteLine($"[A] READ-ONLY ONLY: {type} -> {failure}");
        foreach (var type in control.Keys.Where(k => !readOnly.ContainsKey(k))) _output.WriteLine($"[A] fails only in the control: {type}");
    }

    /// <summary>
    /// D-2' Run 4 / S5: the Run 1 measurement said 46 singletons (incl. the main window view model) cannot be built in a read-only install, from
    /// three constructors. With the probed state registered (as App.axaml.cs does) every singleton now resolves, nothing is created in the
    /// install folder, and the notes database, the market data provider and the model registry come up in their read-only form.
    /// </summary>
    [Fact]
    public async Task A2_DependencyInjection_ReadOnlyWithTheProbedState_ResolvesEverySingleton_AndCreatesNothing()
    {
        using var denied = ReadOnlyDataRootFixture.Create();
        using var scope = denied.UseAsBaseDirectory();
        var state = new DataStorageState(DataStorageProbe.Evaluate(PathDiscovery.ResolveDataRootWithoutCreating()));
        Assert.False(state.Current.IsWritable);

        var failures = await ResolveEverySingletonAsync(state);
        foreach (var (type, failure) in failures) _output.WriteLine($"[A2] still failing: {type} -> {failure}");

        Assert.Empty(failures);
        Assert.Empty(Directory.GetFileSystemEntries(denied.Root)); // no Data folder, no notes folder, no model folder

        var services = new ServiceCollection();
        services.AddSingleton<IDataStorageState>(state);
        services.AddCommonServices(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<StockAnalyzer.Core.Services.ReinforcementLearning.ReadOnlyModelGenerationRegistry>(provider.GetRequiredService<IModelGenerationRegistry>());
        Assert.Same(provider.GetRequiredService<IModelGenerationRegistry>(),
            provider.GetRequiredService<StockAnalyzer.Core.Services.ReinforcementLearning.IRlModelGenerationRegistry>());
        Assert.False(provider.GetRequiredService<NoteDatabaseConnectionManager>().IsAvailable);
        await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => provider.GetRequiredService<NoteDatabaseConnectionManager>().OpenConnectionAsync());
        Assert.Empty(await provider.GetRequiredService<IMarketDataProvider>().GetAvailableTickersAsync());
    }

    [Fact]
    public async Task A3_DependencyInjection_WritableWithTheProbedState_KeepsTheWritableStore()
    {
        using var writable = ReadOnlyDataRootFixture.Create(readOnly: false);
        using var scope = writable.UseAsBaseDirectory();
        var state = new DataStorageState(DataStorageProbe.Evaluate(PathDiscovery.ResolveDataRootWithoutCreating()));
        Assert.True(state.Current.IsWritable);

        var services = new ServiceCollection();
        services.AddSingleton<IDataStorageState>(state);
        services.AddCommonServices(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IMarketDataProvider>(); // singletons are built on first use: ask for the one that creates the data folders
        Assert.IsType<ModelGenerationRegistry>(provider.GetRequiredService<IModelGenerationRegistry>());
        Assert.True(provider.GetRequiredService<NoteDatabaseConnectionManager>().IsAvailable);
        Assert.True(Directory.Exists(Path.Combine(writable.DataRoot, "Notes")));
        Assert.True(Directory.Exists(Path.Combine(writable.DataRoot, "Daily")));
    }

    // ---- (b) workspace -----------------------------------------------------------------------------------------

    [Fact]
    public async Task B_Workspace_PathInitialisationAndSave_ReadOnly()
    {
        using var denied = ReadOnlyDataRootFixture.Create();
        using var scope = denied.UseAsBaseDirectory();

        var options = new WorkspaceCoordinatorOptions();
        _output.WriteLine($"[B] default workspace path resolves to: {Locate(options.WorkspacePath, denied)}");

        var store = new LayoutStateStore();
        var target = new Mock<IWorkspaceLayoutTarget>();
        target.SetupAllProperties();
        target.Setup(t => t.LeftPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.RightPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.TopPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.BottomPanelTabs).Returns(new System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem>());
        using var serialization = new WorkspaceSerializationService();
        var coordinator = new WorkspaceCoordinator(
            store, serialization, new Mock<IWatchlistManager>().Object, Options.Create(options),
            new SynchronousDispatcherService(), NullLogger<WorkspaceCoordinator>.Instance);
        coordinator.Bind(target.Object);

        var init = await RunAsync(() => coordinator.InitializeWorkspaceAsync());
        _output.WriteLine($"[B] InitializeWorkspaceAsync: {init}; lifecycle={store.LifecycleState}; locked={coordinator.IsAutoSaveLocked}");
        var save = await RunAsync(() => coordinator.SaveActiveWorkspaceAsync());
        _output.WriteLine($"[B] SaveActiveWorkspaceAsync: {save}; file exists at the resolved path={File.Exists(options.WorkspacePath)}");
        var shutdown = await RunAsync(() => { coordinator.ForceSaveSync(new WorkspaceSettings()); return Task.CompletedTask; });
        _output.WriteLine($"[B] ForceSaveSync: {shutdown}");

        Assert.False(Directory.Exists(denied.DataRoot));
    }

    // ---- (c) notes database ------------------------------------------------------------------------------------

    [Fact]
    public async Task C_NotesDatabase_Construction_ReadOnly()
    {
        using var denied = ReadOnlyDataRootFixture.Create();
        using var scope = denied.UseAsBaseDirectory();
        NoteDatabaseConnectionManager? manager = null;

        var construct = await RunAsync(() => { manager = new NoteDatabaseConnectionManager(); return Task.CompletedTask; });
        _output.WriteLine($"[C] new NoteDatabaseConnectionManager(): {construct}");
        if (manager is not null)
        {
            _output.WriteLine($"[C] database path: {Locate(manager.DatabasePath, denied)}");
            var open = await RunAsync(async () => { using var connection = await manager.OpenConnectionAsync(); });
            _output.WriteLine($"[C] OpenConnectionAsync: {open}");
        }
    }

    // ---- (d) settings save and every folder resolver (E1/E3) ---------------------------------------------------

    [Fact]
    public async Task D_SettingsSave_And_EveryResolver_ReadOnly()
    {
        using var denied = ReadOnlyDataRootFixture.Create();
        using var scope = denied.UseAsBaseDirectory();

        var resolvers = new (string Name, Func<string> Resolve)[]
        {
            ("ResolveConfigPath", () => PathDiscovery.ResolveConfigPath("probe.json")),
            ("ResolvePortfolioPath", () => PathDiscovery.ResolvePortfolioPath("probe.parquet")),
            ("ResolveBacktestReportSettingsPath", () => PathDiscovery.ResolveBacktestReportSettingsPath("probe.json")),
            ("ResolveBacktestDefaultSettingsPath", () => PathDiscovery.ResolveBacktestDefaultSettingsPath("probe.json")),
            ("ResolveBacktestReportExportPath", () => PathDiscovery.ResolveBacktestReportExportPath("probe.json")),
            ("ResolveIndicatorDefaultsPath", () => PathDiscovery.ResolveIndicatorDefaultsPath("probe.json")),
            ("ResolveSourceIndicatorsPath", () => PathDiscovery.ResolveSourceIndicatorsPath("probe.json")),
            ("ResolveDynamicPeriodDriversPath", () => PathDiscovery.ResolveDynamicPeriodDriversPath("probe.json")),
            ("ResolveTemplatesDirectory", () => PathDiscovery.ResolveTemplatesDirectory()),
            ("ResolveExperimentsDirectory", () => PathDiscovery.ResolveExperimentsDirectory("run-probe")),
            ("ResolveModelGenerationsDirectory", () => PathDiscovery.ResolveModelGenerationsDirectory()),
            ("ResolveDataPath(Data/Notes)", () => PathDiscovery.ResolveDataPath(null, "Data/Notes")),
        };

        foreach (var (name, resolve) in resolvers)
        {
            try
            {
                string path = resolve();
                _output.WriteLine($"[D] {name} -> {Locate(path, denied)}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[D] {name} THROWS {Describe(ex)}");
            }
        }

        // A settings-style save through the shared atomic writer, to the place the resolver names.
        string configPath = PathDiscovery.ResolveConfigPath("p0_settings_probe.json");
        var save = await RunAsync(() => AtomicJsonFile.SaveAsync(configPath, new { Probe = 1 }));
        _output.WriteLine($"[D] AtomicJsonFile.SaveAsync to {Locate(configPath, denied)}: {save}; file exists={File.Exists(configPath)}");
        try { if (File.Exists(configPath)) File.Delete(configPath); } catch (IOException) { }

        var tickers = new TickersSettingsManager(Options.Create(new TickersSettings()));
        var tickersSave = await RunAsync(() => tickers.SaveAsync());
        _output.WriteLine($"[D] TickersSettingsManager.SaveAsync: {tickersSave}");

        Assert.False(Directory.Exists(denied.DataRoot));
    }

    // ---- (f) logging start-up ----------------------------------------------------------------------------------

    [Fact]
    public async Task F_Logging_SerilogConfigurationOfProgramMain_ReadOnly()
    {
        using var denied = ReadOnlyDataRootFixture.Create();
        string logDirectory = Path.Combine(denied.Root, "logs");
        var selfLog = new StringBuilder();
        Serilog.Debugging.SelfLog.Enable(new StringWriter(selfLog));
        string outcome;
        try
        {
            // The same sinks as Program.Main (Async wrappers around two rolling files), minus the console sink.
            outcome = await RunAsync(() =>
            {
                using var logger = new LoggerConfiguration()
                    .WriteTo.Async(a => a.File(new Serilog.Formatting.Compact.CompactJsonFormatter(), Path.Combine(logDirectory, "stockanalyzer-json-.log"), rollingInterval: Serilog.RollingInterval.Day, retainedFileCountLimit: 30))
                    .WriteTo.Async(a => a.File(Path.Combine(logDirectory, "stockanalyzer-.log"), rollingInterval: Serilog.RollingInterval.Day, retainedFileCountLimit: 30))
                    .CreateLogger();
                logger.Information("P0 probe event");
                return Task.CompletedTask;
            });
        }
        finally
        {
            Serilog.Debugging.SelfLog.Disable();
        }

        _output.WriteLine($"[F] configure + write + dispose with an uncreatable log folder: {outcome}");
        _output.WriteLine($"[F] Serilog self-log: {(selfLog.Length == 0 ? "(empty)" : selfLog.ToString().ReplaceLineEndings(" | "))}");
        _output.WriteLine($"[F] log folder created: {Directory.Exists(logDirectory)}");
    }
}
