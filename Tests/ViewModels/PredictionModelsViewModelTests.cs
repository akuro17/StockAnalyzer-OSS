using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class PredictionModelsViewModelTests
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    public PredictionModelsViewModelTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    private sealed class BlockingGenerationRegistry : IModelGenerationRegistry, IDisposable
    {
        private readonly IModelGenerationRegistry _inner;
        private readonly ManualResetEventSlim _release = new(true);
        private TaskCompletionSource<bool> _started = NewSignal();
        private int _armed;

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingGenerationRegistry(IModelGenerationRegistry inner) => _inner = inner;
        public Task Started => _started.Task;
        public void Arm()
        {
            _release.Reset();
            _started = NewSignal();
            Volatile.Write(ref _armed, 1);
        }
        public void Release() => _release.Set();
        public string? ActiveId => _inner.ActiveId;
        public string? PreviousId => _inner.PreviousId;
        public IReadOnlyList<ModelGenerationSummary> List() => _inner.List();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            CancellationToken ct = default) => _inner.RegisterAsync(onnxPath, metricsPath, ct);
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            CancellationToken ct = default) => _inner.ActivateAsync(modelId, manual, confirmLowerScore, ct);
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            CancellationToken ct = default) => _inner.ConfirmActivationAsync(modelId, expectedActiveId, ct);
        public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default) =>
            _inner.RollbackAsync(confirmLowerScore, ct);
        public ModelGenerationLease? AcquireActive() => _inner.AcquireActive();
        public ModelGenerationLease? Acquire(string id) => _inner.Acquire(id);
        public ModelGenerationManifest? GetManifest(string id)
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _started.TrySetResult(true);
                if (!_release.Wait(BarrierTimeout)) throw new TimeoutException("Preset barrier was not released.");
            }
            return _inner.GetManifest(id);
        }
        public void Dispose() => _release.Dispose();
    }

    private sealed class ControlledEnsembleSettingsManager : IEnsembleSettingsManager
    {
        private TaskCompletionSource<bool>? _loadRelease;
        private TaskCompletionSource<bool>? _saveRelease;
        public EnsembleSpec? Current { get; private set; }
        public TaskCompletionSource<bool> LoadStarted { get; private set; } = NewSignal();
        public TaskCompletionSource<bool> SaveStarted { get; private set; } = NewSignal();
        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BlockLoad() => _loadRelease = NewSignal();
        public TaskCompletionSource<bool> BlockNextSave()
        {
            SaveStarted = NewSignal();
            return _saveRelease = NewSignal();
        }
        public async Task LoadAsync()
        {
            LoadStarted.TrySetResult(true);
            if (_loadRelease is { } pending) await pending.Task;
        }
        public async Task SaveAsync(EnsembleSpec? spec)
        {
            var pending = _saveRelease;
            _saveRelease = null;
            SaveStarted.TrySetResult(true);
            if (pending is not null) await pending.Task;
            Current = spec;
        }
    }
    internal class FakePredictionSettingsManager : IPredictionSettingsManager
    {
        public int WindowSize { get; private set; } = PredictionSettingsManager.DefaultWindowSize;
        public void SetWindowSize(int value) => WindowSize = value;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    [Fact]
    public async Task ModelGenerationActivation_OfALowerScore_ActivatesAtOnceWithoutAConfirmation()
    {
        var registry = new FakeGenerationRegistry();
        var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ActivateGenerationCommand.ExecuteAsync(null);

        Assert.Equal("older", registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
    }

    [Fact]
    public async Task ActivationButton_BecomesDeactivate_ForTheActiveGeneration_AndClearsTheActiveGeneration()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        var activate = LocalizationManager.Instance["Settings_ModelGenerations_Activate"];
        var deactivate = LocalizationManager.Instance["Settings_ModelGenerations_Deactivate"];
        Assert.NotEqual(activate, deactivate);
        Assert.Equal(activate, vm.GenerationActivationText); // nothing selected

        vm.SelectedGeneration = registry.List()[1];
        Assert.False(vm.IsSelectedGenerationActive);
        Assert.Equal(activate, vm.GenerationActivationText);

        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedGeneration = registry.List()[0]; // the active generation (marked)
        Assert.True(vm.IsSelectedGenerationActive);
        Assert.Equal(deactivate, vm.GenerationActivationText);
        Assert.Contains(nameof(PredictionModelsViewModel.GenerationActivationText), raised);

        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null);

        Assert.Null(registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Deactivated"], vm.GenerationStatusMessage);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_LegacyUnverified"], vm.LegacyModelStatus);
        Assert.All(vm.Generations, item => Assert.False(item.IsActive));
        Assert.Equal(activate, vm.GenerationActivationText); // the list was refreshed: nothing selected, nothing active
    }

    [Fact]
    public async Task RowCheckBox_IsTheActivationButtonForThatRow_AndTheRowsShowTheRegistryStateAfterwards()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);

        // An inactive row of a lower score: the box activates it exactly like Activate, without a question; it is ticked because the registry says so.
        var lower = vm.GenerationRows.Single(r => r.Summary == registry.List()[1]);
        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(lower);
        Assert.Equal("older", registry.ActiveId);
        Assert.NotSame(lower, vm.GenerationRows.Single(r => r.Summary?.ModelId == "older"));   // rebuilt: no stale local tick
        Assert.True(vm.GenerationRows.Single(r => r.Summary?.ModelId == "older").IsActive);

        // The ticked row deactivates.
        var active = vm.GenerationRows.Single(r => r.IsActive);
        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(active);
        Assert.Null(registry.ActiveId);
        Assert.DoesNotContain(vm.GenerationRows, r => r.IsActive);
        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(null);   // a header or nothing does nothing
    }

    [Fact]
    public void RowTip_ListsEveryFactOnItsOwnLine()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        var summary = registry.List()[0];
        var tip = vm.GenerationRows.Single(r => r.Summary == summary).Tooltip!;
        var lines = tip.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Contains(summary.ModelFile, lines[0]);
        Assert.Contains(summary.Timeframe, lines[2]);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Tip_Active"], lines[5]);
    }

    [Fact]
    public async Task ActivationButton_OnAnInactiveGeneration_StillActivates_AndDeactivatingTwiceReportsNothingActive()
    {
        var registry = new FakeGenerationRegistry { ActiveId = null };
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // a lower score activates at once
        Assert.Equal("older", registry.ActiveId);

        await vm.DeactivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Deactivated"], vm.GenerationStatusMessage);
        await vm.DeactivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_AlreadyInactive"], vm.GenerationStatusMessage);
        Assert.Null(registry.ActiveId);
    }

    internal static (CommunityToolkit.Mvvm.Messaging.IMessenger Messenger, Func<int> Count) CountingMessenger()
    {
        var messenger = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();
        var count = 0;
        var recipient = new object();
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Register<StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage>(
            messenger, recipient, (_, _) => count++);
        GC.KeepAlive(recipient);
        return (messenger, () => count);
    }

    [Fact]
    public async Task ModelChangesThatSucceed_TellTheChartsOnce_AndRefusedOrUnchangedOnesNeverDo()
    {
        var (messenger, count) = CountingMessenger();
        var registry = new FakeGenerationRegistry();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, messenger: messenger);

        vm.SelectedGeneration = registry.List()[0];
        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // deactivate
        Assert.Equal(1, count());

        await vm.DeactivateGenerationCommand.ExecuteAsync(null); // nothing active: no change
        Assert.Equal(1, count());

        vm.SelectedGeneration = registry.List()[1];
        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // lower score: activated at once, the charts hear about it once
        Assert.Equal(2, count());

        registry.ActivationFailure = new PredictionWindowMismatchException(25, 50);
        vm.SelectedGeneration = registry.List()[2];
        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // refused
        Assert.Equal(2, count());

    }


    [Fact]
    public async Task ActivationRefusedForAWindowMismatch_NamesBothWindows()
    {
        var registry = new FakeGenerationRegistry { ActivationFailure = new PredictionWindowMismatchException(25, 50) };
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance["Settings_ModelGenerations_Error_WindowMismatch"], 25, 50), vm.GenerationStatusMessage);
        Assert.Contains("25", vm.GenerationStatusMessage);
        Assert.Contains("50", vm.GenerationStatusMessage);
    }

    [Fact]
    public void WindowMismatchAndDeactivationTexts_ExistInBothLanguages()
    {
        foreach (var language in new[] { "en", "ja" })
        {
            LocalizationManager.Instance.Initialize(language);
            foreach (var key in new[] { "Settings_ModelGenerations_Deactivate", "Settings_ModelGenerations_Deactivated",
                         "Settings_ModelGenerations_AlreadyInactive", "Settings_ModelGenerations_Error_WindowMismatch", "Prediction_Notice_WindowMismatch" })
                Assert.False(string.IsNullOrWhiteSpace(LocalizationManager.Instance[key]) || LocalizationManager.Instance[key] == $"[{key}]", $"{language}:{key}");
        }
        LocalizationManager.Instance.Initialize("en");
    }

    [Fact]
    public async Task Activation_IsForTheModelTheOwnerAskedFor_EvenWhenTheSelectionOrTheListChangesDuringTheAwait()
    {
        var registry = new FakeGenerationRegistry();
        var announcer = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, messenger: announcer);

        vm.SelectedGeneration = registry.List()[1];
        var releaseSelection = registry.BlockNextActivation();
        var activation = vm.ActivateGenerationCommand.ExecuteAsync(null);
        await registry.ActivationStarted.WaitAsync(BarrierTimeout);
        vm.SelectedGeneration = registry.List()[2];
        releaseSelection.TrySetResult(true);
        await activation.WaitAsync(BarrierTimeout);
        Assert.Equal("older", registry.ActiveId);

        vm.SelectedGeneration = registry.List()[2];
        var releaseRefresh = registry.BlockNextActivation();
        activation = vm.ActivateGenerationCommand.ExecuteAsync(null);
        await registry.ActivationStarted.WaitAsync(BarrierTimeout);
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(announcer, new StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage()); // the list is read from the registry again
        releaseRefresh.TrySetResult(true);
        await activation.WaitAsync(BarrierTimeout);
        Assert.Equal("other", registry.ActiveId);
    }

    [Fact]
    public async Task Activation_ThatFindsTheActiveModelChangedUnderneath_IsRefusedAndChangesNothing()
    {
        var registry = new ReplacingGenerationRegistry { ReplaceFailure = new ModelActivationContextChangedException() };
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ActivateGenerationCommand.ExecuteAsync(null);

        Assert.Equal("current", registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_ContextChanged"], vm.GenerationStatusMessage);
    }

    [Fact]
    public async Task EnsemblePercentages_AreConvertedAtSaveBoundary()
    {
        var manager = new FakeEnsembleSettingsManager();
        var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), ensembleSettings: manager);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("a", 25m));
        vm.EnsembleMembers.Add(new EnsembleWeightRow("b", 75m));
        await vm.SaveEnsembleCommand.ExecuteAsync(null);
        Assert.Equal(.25, manager.Current!.Members[0].Weight);
        Assert.Equal(.75, manager.Current.Members[1].Weight);
        await vm.ClearEnsembleCommand.ExecuteAsync(null);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task ManagementFailures_ShowLocalizedReasonsInsteadOfExceptionText()
    {
        const string internalDetail = "sensitive internal path";
        var settings = new FakeEnsembleSettingsManager
        {
            SaveFailure = new IOException(internalDetail),
        };
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), ensembleSettings: settings);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("a", 100m));

        await vm.SaveEnsembleCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_Ensemble_Error_Storage"], vm.EnsembleStatus);
        Assert.DoesNotContain(internalDetail, vm.EnsembleStatus);

        settings.SaveFailure = new KeyNotFoundException(internalDetail);
        await vm.SaveEnsembleCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_Ensemble_Error_MissingGeneration"], vm.EnsembleStatus);
        Assert.DoesNotContain(internalDetail, vm.EnsembleStatus);

        var registry = new FakeGenerationRegistry { ActivationFailure = new KeyNotFoundException(internalDetail) };
        using var generationVm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        generationVm.SelectedGeneration = registry.List()[1];
        await generationVm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_Missing"],
            generationVm.GenerationStatusMessage);
        Assert.DoesNotContain(internalDetail, generationVm.GenerationStatusMessage);

        registry.ActivationFailure = new InvalidOperationException(internalDetail);
        await generationVm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_Incompatible"],
            generationVm.GenerationStatusMessage);

        registry.ActivationFailure = new IOException(internalDetail);
        await generationVm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_Storage"],
            generationVm.GenerationStatusMessage);
    }

    [Fact]
    public async Task AccuracyPreset_DiscardsResultsAfterMemberWeightClearOrSaveEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_vm_ensemble_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var registry = new ModelGenerationRegistry(Path.Combine(root, "generations"));
            var a = await RegisterEnsembleFixtureAsync(registry, root, "a", .4);
            var b = await RegisterEnsembleFixtureAsync(registry, root, "b", .6);
            var c = await RegisterEnsembleFixtureAsync(registry, root, "c", .5);
            using var blocked = new BlockingGenerationRegistry(registry);
            var settings = new FakeEnsembleSettingsManager();
            using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(),
                modelRegistry: blocked, ensembleSettings: settings);

            async Task CheckStale(Func<Task> edit)
            {
                vm.EnsembleMembers.Clear();
                vm.EnsembleMembers.Add(new EnsembleWeightRow(a.ModelId, 25m));
                vm.EnsembleMembers.Add(new EnsembleWeightRow(b.ModelId, 75m));
                blocked.Arm();
                var preset = vm.AccuracyEnsembleWeightsCommand.ExecuteAsync(null);
                await blocked.Started.WaitAsync(BarrierTimeout);
                try
                {
                    await edit();
                    var expected = vm.EnsembleMembers.Select(row => (row.ModelId, row.Percent)).ToArray();
                    var status = vm.EnsembleStatus;
                    blocked.Release();
                    await preset.WaitAsync(BarrierTimeout);
                    Assert.Equal(expected, vm.EnsembleMembers.Select(row => (row.ModelId, row.Percent)));
                    Assert.Equal(status, vm.EnsembleStatus);
                }
                finally { blocked.Release(); }
            }

            await CheckStale(() =>
            {
                vm.SelectedEnsembleMember = vm.EnsembleMembers[0];
                vm.RemoveEnsembleMemberCommand.Execute(null);
                vm.SelectedGeneration = blocked.List().Single(item => item.ModelId == c.ModelId);
                vm.AddEnsembleMemberCommand.Execute(null);
                return Task.CompletedTask;
            });
            await CheckStale(() =>
            {
                vm.EnsembleMembers.Move(0, 1);
                return Task.CompletedTask;
            });
            await CheckStale(() =>
            {
                vm.EnsembleMembers[0].Percent = 40m;
                return Task.CompletedTask;
            });
            await CheckStale(() => vm.ClearEnsembleCommand.ExecuteAsync(null));
            await CheckStale(() => vm.SaveEnsembleCommand.ExecuteAsync(null));
            Assert.Equal(.25, settings.Current!.Members[0].Weight);
            Assert.Equal(.75, settings.Current.Members[1].Weight);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PendingLoadSaveAndClear_DoNotOverwriteNewerRows()
    {
        var manager = new ControlledEnsembleSettingsManager();
        var loadRelease = manager.BlockLoad();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), ensembleSettings: manager);
        await manager.LoadStarted.Task.WaitAsync(BarrierTimeout);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("a", 25m));
        vm.EnsembleMembers.Add(new EnsembleWeightRow("b", 75m));
        loadRelease.TrySetResult(true);
        await Task.Yield();
        Assert.Equal(new[] { "a", "b" }, vm.EnsembleMembers.Select(row => row.ModelId));

        var saveRelease = manager.BlockNextSave();
        var saving = vm.SaveEnsembleCommand.ExecuteAsync(null);
        await manager.SaveStarted.Task.WaitAsync(BarrierTimeout);
        vm.EnsembleMembers[0].Percent = 40m;
        saveRelease.TrySetResult(true);
        await saving.WaitAsync(BarrierTimeout);
        Assert.Equal(.25, manager.Current!.Members[0].Weight);
        Assert.Equal(40m, vm.EnsembleMembers[0].Percent);
        Assert.Null(vm.EnsembleStatus);

        var clearRelease = manager.BlockNextSave();
        var clearing = vm.ClearEnsembleCommand.ExecuteAsync(null);
        await manager.SaveStarted.Task.WaitAsync(BarrierTimeout);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("c", 0m));
        clearRelease.TrySetResult(true);
        await clearing.WaitAsync(BarrierTimeout);
        Assert.Equal(new[] { "a", "b", "c" }, vm.EnsembleMembers.Select(row => row.ModelId));
        Assert.Null(vm.EnsembleStatus);
    }


    /// <summary>An <see cref="IDialogService"/> that only answers the open-file dialog (a real fake of the whole interface is not needed here).</summary>
    public class OpenFileDialogProxy : DispatchProxy
    {
        public string? PickedPath { get; set; }
        public string[]? LastFilters { get; private set; }
        public int Calls { get; private set; }
        /// <summary>How often a confirmation was asked (the model list asks nothing: activating and removing a model are the owner's decision).</summary>
        public int ConfirmCalls { get; private set; }
        public string? LastStartDirectory { get; private set; }

        public static (IDialogService Service, OpenFileDialogProxy Probe) Create(string? pickedPath)
        {
            var service = DispatchProxy.Create<IDialogService, OpenFileDialogProxy>();
            var probe = (OpenFileDialogProxy)(object)service;
            probe.PickedPath = pickedPath;
            return (service, probe);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowConfirmationAsync))
            {
                ConfirmCalls++;
                return Task.FromResult(true);
            }

            if (targetMethod?.Name != nameof(IDialogService.ShowOpenFileDialogAsync)) throw new NotSupportedException(targetMethod?.Name);
            Calls++;
            LastFilters = (string[]?)args![1];
            LastStartDirectory = args.Length > 2 ? (string?)args[2] : null;
            return Task.FromResult(PickedPath);
        }
    }

    /// <summary>A registry-ready copy of ensemble fixture <paramref name="index"/> (a model with its independent-evaluation file beside it).</summary>
    private static string CopyRegistrableFixture(string root, int index, bool withMetrics = true, string? evaluationRevision = null)
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets"));
        var folder = Path.Combine(root, "picked" + index + (withMetrics ? "" : "_nometrics"));
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, $"ensemble_fixture_m0{index}.onnx");
        File.Copy(Path.Combine(assets, $"ensemble_fixture_m0{index}.onnx"), model);
        if (withMetrics) File.Copy(Path.Combine(assets, $"ensemble_fixture_m0{index}.onnx.metrics.json"), model + ".metrics.json");
        if (withMetrics && evaluationRevision is not null)
        {
            // a model of another training run: the same metrics with another evaluation revision (the fixtures share one revision)
            var fixtureRevision = new string('d', 64);
            File.WriteAllText(model + ".metrics.json", File.ReadAllText(model + ".metrics.json").Replace(fixtureRevision, evaluationRevision));
        }
        return model;
    }

    private static async Task WithAddFromFileRig(Func<string, ModelGenerationRegistry, Func<string?, (PredictionModelsViewModel Vm, OpenFileDialogProxy Dialog)>, Func<int>, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_vm_add_model_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (messenger, count) = CountingMessenger();
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "generations"));
        try
        {
            (PredictionModelsViewModel, OpenFileDialogProxy) Create(string? picked)
            {
                var (dialogs, probe) = OpenFileDialogProxy.Create(picked);
                return (new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, messenger: messenger, dialogService: dialogs), probe);
            }
            await body(root, registry, Create, count);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { /* temp folder, best effort */ } }
    }

    [Fact]
    public Task AddModelFromFile_RegistersActivatesAndAnnouncesThePickedOnnx() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Generations);
        Assert.True(row.IsActive);
        Assert.Null(vm.SelectedGeneration); // the list is reloaded after an activation, exactly as after the Activate button
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
        Assert.Equal(row.ModelId, registry.ActiveId);
        Assert.Equal(1, count());
        Assert.Equal(1, dialog.Calls);
        Assert.Equal(new[] { ".onnx" }, dialog.LastFilters);
    });

    [Fact]
    public Task AddModelFromFile_WithoutTheEvaluationFile_ChangesNothingAndSaysWhatIsMissing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, _) = create(CopyRegistrableFixture(root, 1, withMetrics: false));
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Empty(vm.Generations);
        Assert.Null(registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_FileMissing"], vm.GenerationStatusMessage);
        Assert.Contains(".metrics.json", vm.GenerationStatusMessage);
        Assert.Equal(0, count());
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task AddModelFromFile_CancellingTheDialogDoesNothing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(null);
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        Assert.Empty(vm.Generations);
        Assert.Null(vm.GenerationStatusMessage);
        Assert.Equal(0, count());
    });

    [Fact]
    public Task AddModelFromFile_AModelThatDoesNotImproveTheScoreIsActivatedWithoutAsking() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var first = registry.ActiveId;
        dialog.PickedPath = CopyRegistrableFixture(root, 2);

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // the same score: still the owner's choice

        Assert.Equal(2, vm.Generations.Count);
        Assert.NotEqual(first, registry.ActiveId);
        Assert.Equal(registry.ActiveId, vm.Generations.Single(g => g.IsActive).ModelId);
        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Equal(2, count());
    });

    private static readonly string OtherEvaluationRevision = new('e', 64);

    /// <summary>The attention fixture (horizon 3, while the ensemble fixtures have horizon 5) with the evidence the registry demands: another ensemble group.</summary>
    private static string CopyOtherGroupFixture(string root)
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets"));
        var folder = Path.Combine(root, "picked_other_group");
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, "other_group.onnx");
        File.Copy(Path.Combine(assets, "attention_fixture.onnx"), model);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var revision = new string('d', 64);
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = revision, scored_model_sha256 = hash, target_type = "classification",
            timeframe = "daily", horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey], fold_macro_f1 = 0.4, fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0,
            n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.4 },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return model;
    }

    [Fact]
    public Task ModelList_GroupsTheModelsThatCanFormOneEnsemble_UnderHeaders_AndOnlyAddsWithinAGroup() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = CopyRegistrableFixture(root, 2);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // same group, same score: waits for the lower-score confirmation
        dialog.PickedPath = CopyOtherGroupFixture(root);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // another horizon: a different group (and a replacement confirmation)
        await vm.GenerationGroupingTask;

        var headers = vm.GenerationRows.Where(r => r.IsHeader).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.All(headers, h => Assert.False(h.IsSelectable));
        // groups are ordered by time frame, then horizon: the 3-bar group first
        Assert.Contains("3", headers[0].HeaderText);
        Assert.EndsWith("(1)", headers[0].HeaderText);
        Assert.Contains("5", headers[1].HeaderText);
        Assert.EndsWith("(2)", headers[1].HeaderText);
        Assert.Equal(new[] { true, false, true, false, false }, vm.GenerationRows.Select(r => r.IsHeader).ToArray());
        var fiveBarRows = vm.GenerationRows.SkipWhile(r => !ReferenceEquals(r, headers[1])).Skip(1).ToArray();
        Assert.Equal(2, fiveBarRows.Length);
        Assert.Single(fiveBarRows.Select(r => r.GroupKey).Distinct());

        // ensemble: after one 5-bar model, the other 5-bar model may be added, the 3-bar one may not
        var first = fiveBarRows[0];
        var second = fiveBarRows[1];
        var otherGroup = vm.GenerationRows.First(r => r.IsItem);
        vm.SelectedGenerationRow = first;
        Assert.True(vm.AddEnsembleMemberCommand.CanExecute(null));
        vm.AddEnsembleMemberCommand.Execute(null);
        Assert.Single(vm.EnsembleMembers);

        vm.SelectedGenerationRow = otherGroup;
        Assert.False(vm.AddEnsembleMemberCommand.CanExecute(null));
        Assert.Equal(LocalizationManager.Instance["Settings_Ensemble_Hint_OtherGroup"], vm.AddEnsembleMemberHint);

        vm.SelectedGenerationRow = second;
        Assert.True(vm.AddEnsembleMemberCommand.CanExecute(null));
        Assert.Null(vm.AddEnsembleMemberHint);
        vm.AddEnsembleMemberCommand.Execute(null);
        Assert.Equal(2, vm.EnsembleMembers.Count);
        Assert.False(vm.AddEnsembleMemberCommand.CanExecute(null), "already a member");
    });

    [Fact]
    public Task ModelList_SelectionFollowsTheRowsAndSurvivesTheGroupingRefresh() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, _) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        await vm.GenerationGroupingTask;
        var item = vm.GenerationRows.Single(r => r.IsItem);

        vm.SelectedGenerationRow = item;
        Assert.Equal(item.Summary, vm.SelectedGeneration);

        vm.SelectedGeneration = null;
        Assert.Null(vm.SelectedGenerationRow);

        vm.SelectedGeneration = item.Summary;
        Assert.Same(item, vm.SelectedGenerationRow);
    });

    [Fact]
    public Task RemoveFromList_DeletesTheRegisteredCopy_KeepsTheSourceFile_AndTheFileCanBeAddedAgain() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var firstFile = CopyRegistrableFixture(root, 1);
        var (vm, dialog) = create(firstFile);
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var active = registry.ActiveId;
        var secondFile = CopyRegistrableFixture(root, 2, evaluationRevision: OtherEvaluationRevision);
        dialog.PickedPath = secondFile;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // registered and, being the owner's choice, active (it replaces the first)
        Assert.Equal(2, vm.Generations.Count);
        var second = registry.ActiveId;
        Assert.NotEqual(active, second);

        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == active);
        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Equal(second, Assert.Single(vm.Generations).ModelId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Removed"], vm.GenerationStatusMessage);
        Assert.True(File.Exists(firstFile), "the file it was added from is never touched");
        Assert.False(Directory.Exists(Path.Combine(root, "generations", active!)));

        dialog.PickedPath = firstFile;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // the same file again
        Assert.Equal(2, vm.Generations.Count);
    });

    [Fact]
    public Task RemoveFromList_TheActiveModel_IsDeactivatedFirst_WithoutAsking() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var changesBefore = count();
        vm.SelectedGeneration = vm.Generations.Single();

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Empty(vm.Generations);
        Assert.Null(registry.ActiveId);
        Assert.Equal(changesBefore + 1, count());   // the charts hear about the deactivation
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Removed"], vm.GenerationStatusMessage);
    });

    [Fact]
    public Task RemoveFromList_AnEnsembleMember_IsTakenOutOfTheSavedEnsembleFirst() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (first, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = first;
        await first.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = CopyRegistrableFixture(root, 2);
        await first.AddGenerationFromFileCommand.ExecuteAsync(null);
        var active = first.Generations.Single(g => g.IsActive);
        var member = first.Generations.Single(g => !g.IsActive);
        var ensemble = new FakeEnsembleSettingsManager();
        await ensemble.SaveAsync(new EnsembleSpec(Array.AsReadOnly(new[] { new EnsembleMember(active.ModelId, .25), new EnsembleMember(member.ModelId, .75) })));
        var (dialogs, probe) = OpenFileDialogProxy.Create(null);
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, ensembleSettings: ensemble, dialogService: dialogs,
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == member.ModelId);

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(0, probe.ConfirmCalls);
        var kept = Assert.Single(ensemble.Current!.Members);
        Assert.Equal(active.ModelId, kept.ModelId);
        Assert.Equal(1.0, kept.Weight, 6);   // the remaining weights add up to one again
        Assert.Equal(active.ModelId, Assert.Single(vm.Generations).ModelId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Removed"], vm.GenerationStatusMessage);

        // the last member of the ensemble: the ensemble is cleared
        var lastOne = vm.Generations.Single();
        await ensemble.SaveAsync(EnsembleSpec.Equal(new[] { lastOne.ModelId }));
        vm.SelectedGeneration = lastOne;
        await vm.RemoveGenerationCommand.ExecuteAsync(null);
        Assert.Null(ensemble.Current);
        Assert.Empty(vm.Generations);
    });

    [Fact]
    public Task RemoveFromList_WithoutASelection_SaysWhatToDo_AndAsksNothing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        vm.SelectedGeneration = null;

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveNoSelection"], vm.GenerationStatusMessage);
        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Single(vm.Generations);
    });

    [Fact]
    public Task RemoveFromList_AModelTheChartsPredictorStillHolds_IsRemovedAfterTheChartPredictedOnce() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm0, dialog0) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm0;
        await vm0.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog0.PickedPath = CopyRegistrableFixture(root, 2);
        await vm0.AddGenerationFromFileCommand.ExecuteAsync(null);
        var heldId = vm0.Generations.Single(g => !g.IsActive).ModelId;
        var lease = registry.Acquire(heldId)!;
        var runs = 0;
        var target = new FakeRunTarget { Next = () => { runs++; lease.Dispose(); return Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "SPY", TimeframeType.Daily, null, null)); } };
        var (dialogs, _) = OpenFileDialogProxy.Create(null);
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, dialogService: dialogs, predictionRunTarget: target,
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == heldId);

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(1, runs);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Removed"], vm.GenerationStatusMessage);
        Assert.Single(vm.Generations);
    });

    [Fact]
    public Task RemoveFromList_AModelThatStaysInUse_IsReportedAsInUse_AndKept() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm0, dialog0) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm0;
        await vm0.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog0.PickedPath = CopyRegistrableFixture(root, 2);
        await vm0.AddGenerationFromFileCommand.ExecuteAsync(null);
        var heldId = vm0.Generations.Single(g => !g.IsActive).ModelId;
        using var lease = registry.Acquire(heldId)!;
        var (dialogs, _) = OpenFileDialogProxy.Create(null);
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, dialogService: dialogs, predictionRunTarget: new FakeRunTarget(),
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == heldId);

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveInUse"], vm.GenerationStatusMessage);
        Assert.Equal(2, vm.Generations.Count);
    });

    [Fact]
    public Task Checkbox_OnAModelThatDoesNotImproveTheScore_ActivatesWithoutAsking_AndTheFooterSaysWhatTheChartShows() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (seed, seedDialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = seed;
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);
        seedDialog.PickedPath = CopyRegistrableFixture(root, 2);
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);   // same score: it becomes the active one, the first is not active any more
        var first = registry.ActiveId;
        var (dialogs, probe) = OpenFileDialogProxy.Create(null);
        var target = new FakeRunTarget { Next = () => Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "AAPL", TimeframeType.Daily, "Up 61%", null, 200)) };
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, dialogService: dialogs, predictionRunTarget: target,
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        await vm.GenerationGroupingTask;
        var row = vm.GenerationRows.Single(r => r.IsItem && !r.IsActive);

        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(row);

        Assert.Equal(0, probe.ConfirmCalls);
        Assert.NotEqual(first, registry.ActiveId);
        Assert.Equal(row.Summary!.ModelId, registry.ActiveId);
        Assert.Equal(1, target.Calls);
        Assert.Contains("Up 61%", vm.RunPredictionStatus);
        Assert.Contains(row.ModelFile, vm.RunPredictionStatus);   // the footer names the model the chart now predicts with
    });

    [Fact]
    public Task AddModelFromFile_ARunEvaluatedDifferently_ReplacesTheActiveModelWithoutAsking() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var first = registry.ActiveId;
        dialog.PickedPath = CopyRegistrableFixture(root, 2, evaluationRevision: OtherEvaluationRevision);

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // another evaluation revision: it cannot be compared, the owner's choice replaces the active model

        Assert.Equal(2, vm.Generations.Count);
        Assert.NotEqual(first, registry.ActiveId);
        Assert.Equal(first, registry.PreviousId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Equal(2, count());
    });

    [Fact]
    public Task AddModelFromFile_APickedModelThatIsAlreadyActiveIsNotRegisteredTwice() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, _) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Single(vm.Generations);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_AlreadyActive"], vm.GenerationStatusMessage);
        Assert.Equal(1, count());
    });

    private static async Task<ModelGenerationManifest> RegisterEnsembleFixtureAsync(
        ModelGenerationRegistry registry, string root, string name, double score)
    {
        var source = Path.Combine(root, name);
        Directory.CreateDirectory(source);
        var model = Path.Combine(source, "member.onnx");
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "StockAnalyzer.Core.Tests", "Assets", "trend_predictor_goodmeta.onnx"));
        File.Copy(fixture, model);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var revision = new string('a', 64);
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = revision,
            scored_model_sha256 = hash, target_type = "classification", timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey],
            fold_macro_f1 = score, fold_accuracy = score, fold_is_holdout = 1.0,
            fold = 2.0, fold_n = 20.0, n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash,
                    fold_macro_f1 = .2, fold_accuracy = .2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash,
                    fold_macro_f1 = .3, fold_accuracy = .3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash,
                    fold_macro_f1 = score, fold_accuracy = score },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return await registry.RegisterAsync(model);
    }

    private sealed class FakeEnsembleSettingsManager : IEnsembleSettingsManager
    {
        public EnsembleSpec? Current { get; private set; }
        public Exception? LoadFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public Task LoadAsync() => LoadFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        public Task SaveAsync(EnsembleSpec? spec)
        {
            if (SaveFailure is { } failure) return Task.FromException(failure);
            Current = spec;
            return Task.CompletedTask;
        }
    }

    /// <summary>A registry that cannot compare the candidate with the active generation and replaces it in one call, like <see cref="ModelGenerationRegistry"/>.</summary>
    private sealed class ReplacingGenerationRegistry : IModelGenerationRegistry
    {
        private readonly ModelGenerationSummary[] _models =
        {
            new("current", "current.onnx", "classification", "daily", 5, 0.8, "revision", true),
            new("other", "other.onnx", "classification", "daily", 5, 0.6, "other revision", false),
        };
        public string? ActiveId { get; private set; } = "current";
        public string? PreviousId { get; private set; }
        public Exception? ReplaceFailure { get; set; }
        public int ActivateCalls { get; private set; }
        public int DeactivateCalls { get; private set; }
        public System.Collections.Generic.List<(string ModelId, string? ExpectedActiveId)> ReplaceCalls { get; } = new();
        public System.Collections.Generic.IReadOnlyList<ModelGenerationSummary> List() =>
            _models.Select(model => model with { IsActive = model.ModelId == ActiveId }).ToArray();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            System.Threading.CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default)
        {
            ActivateCalls++;
            throw new ModelActivationIncompatibleException();
        }
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            System.Threading.CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<bool> RollbackAsync(bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeactivateAsync(System.Threading.CancellationToken ct = default)
        {
            DeactivateCalls++;
            return Task.FromResult(false);
        }
        public Task<bool> ReplaceActiveAsync(string modelId, string? expectedActiveId, System.Threading.CancellationToken ct = default)
        {
            ReplaceCalls.Add((modelId, expectedActiveId));
            if (ReplaceFailure is { } failure) return Task.FromException<bool>(failure);
            PreviousId = ActiveId;
            ActiveId = modelId;
            return Task.FromResult(true);
        }
        public ModelGenerationLease? AcquireActive() => null;
    }

    [Fact]
    public async Task ConfirmedReplacement_IsOneReplaceActiveCall_NotADeactivateAndAnActivate()
    {
        var registry = new ReplacingGenerationRegistry();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ActivateGenerationCommand.ExecuteAsync(null);   // incomparable: the replacement follows without a question

        Assert.Equal(("other", (string?)"current"), Assert.Single(registry.ReplaceCalls));
        Assert.Equal(1, registry.ActivateCalls);    // only the first, refused attempt
        Assert.Equal(0, registry.DeactivateCalls);
        Assert.Equal("other", registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
    }

    [Fact]
    public async Task ConfirmedReplacement_RefusedByAWindowMismatch_ShowsBothWindows_AndChangesNothing()
    {
        var registry = new ReplacingGenerationRegistry { ReplaceFailure = new PredictionWindowMismatchException(25, 50) };
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ActivateGenerationCommand.ExecuteAsync(null);

        Assert.Single(registry.ReplaceCalls);
        Assert.Equal("current", registry.ActiveId);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance["Settings_ModelGenerations_Error_WindowMismatch"], 25, 50), vm.GenerationStatusMessage);
    }

    private sealed class FakeGenerationRegistry : IModelGenerationRegistry
    {
        private readonly ModelGenerationSummary[] _models =
        {
            new("current", "current.onnx", "classification", "daily", 5, 0.8, "revision", true),
            new("older", "older.onnx", "classification", "daily", 5, 0.4, "revision", false),
            new("other", "other.onnx", "classification", "daily", 5, 0.6, "revision", false),
        };
        private TaskCompletionSource<bool>? _activationRelease;
        public Task ActivationStarted { get; private set; } = Task.CompletedTask;
        public TaskCompletionSource<bool> BlockNextActivation()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ActivationStarted = started.Task;
            _activationStarted = started;
            return _activationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private TaskCompletionSource<bool>? _activationStarted;
        public string? ActiveId { get; set; } = "current";
        public string? PreviousId { get; set; } = "older";
        public Exception? ActivationFailure { get; set; }
        /// <summary>Generations that are no longer registered (another window removed them).</summary>
        public System.Collections.Generic.HashSet<string> Removed { get; } = new();
        public System.Collections.Generic.IReadOnlyList<ModelGenerationSummary> List() =>
            _models.Where(model => !Removed.Contains(model.ModelId)).Select(model => model with { IsActive = model.ModelId == ActiveId }).ToArray();
        public Task<bool> DeactivateAsync(System.Threading.CancellationToken ct = default)
        {
            if (ActiveId is null) return Task.FromResult(false);
            PreviousId = ActiveId;
            ActiveId = null;
            return Task.FromResult(true);
        }
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            System.Threading.CancellationToken ct = default) => throw new System.NotSupportedException();
        public async Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default)
        {
            if (ActivationFailure is { } failure) throw failure;
            if (_activationRelease is { } release)
            {
                _activationRelease = null;
                _activationStarted?.TrySetResult(true);
                await release.Task.WaitAsync(ct);
            }
            if (!confirmLowerScore) throw new ModelActivationConfirmationRequiredException(modelId, ActiveId);
            ActiveId = modelId;
            return true;
        }
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            System.Threading.CancellationToken ct = default)
        {
            if (ActiveId != expectedActiveId) throw new ModelActivationContextChangedException();
            return ActivateAsync(modelId, manual: true, confirmLowerScore: true, ct);
        }
        public Task<bool> RollbackAsync(bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default) => PreviousId is { } previous
                ? ActivateAsync(previous, true, confirmLowerScore, ct) : Task.FromResult(false);
        public ModelGenerationLease? AcquireActive() => null;
    }

    [Fact]
    public void ConfiguredWindowSize_FollowsTheSettingsManager_AndNamesTheValueInItsText()
    {
        var manager = new PredictionSettingsManager();
        using var vm = new PredictionModelsViewModel(manager);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        manager.SetWindowSize(33);

        Assert.Equal(33, vm.ConfiguredWindowSize);
        Assert.Contains("33", vm.ConfiguredWindowText);
        Assert.Contains(nameof(PredictionModelsViewModel.ConfiguredWindowSize), changed);
        Assert.Contains(nameof(PredictionModelsViewModel.ConfiguredWindowText), changed);
    }

    [Fact]
    public void ConfiguredWindowSize_StopsFollowingAfterDispose()
    {
        var manager = new PredictionSettingsManager();
        var vm = new PredictionModelsViewModel(manager);
        var changed = 0;
        vm.PropertyChanged += (_, _) => changed++;
        vm.Dispose();

        manager.SetWindowSize(44);

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task OpenSettings_OpensTheAiPredictionsCategory()
    {
        var (dialogs, probe) = AIPredictionsSettingsViewModelTests.RecordingDialogProxy.Create();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), dialogService: dialogs);

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        var call = Assert.Single(probe.Calls);
        Assert.Equal(nameof(IDialogService.ShowSettingsDialogAsync), call.Method);
        Assert.Equal(StockAnalyzer.Avalonia.Models.SettingsConstants.Keys.AIPredictions, call.First);
    }

    private sealed class FakeRunTarget : IPredictionRunTarget
    {
        public int Calls { get; private set; }
        public Func<Task<PredictionRunReport>> Next { get; set; } = () => Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "SPY", TimeframeType.Daily, "Up 61%", null));
        public Task<PredictionRunReport> RunAsync(CancellationToken cancellationToken) { Calls++; return Next(); }
    }

    private static PredictionModelsViewModel RunVm(IPredictionRunTarget? target) =>
        new(new FakePredictionSettingsManager(), predictionRunTarget: target);

    [Fact]
    public async Task RunPrediction_IsUnavailableWithoutATarget_AndRunsOnceWithItsSummary()
    {
        using var none = RunVm(null);
        Assert.False(none.RunPredictionCommand.CanExecute(null));

        var target = new FakeRunTarget();
        using var vm = RunVm(target);
        Assert.True(vm.RunPredictionCommand.CanExecute(null));
        await vm.RunPredictionCommand.ExecuteAsync(null);

        Assert.Equal(1, target.Calls);
        Assert.Contains("SPY", vm.RunPredictionStatus);
        Assert.Contains("Up 61%", vm.RunPredictionStatus);
        Assert.False(vm.IsRunningPrediction);
        Assert.True(vm.RunPredictionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(PredictionRunOutcome.NoData, "PredictionModels_Run_NoData")]
    [InlineData(PredictionRunOutcome.ReplayShown, "PredictionModels_Run_Replay")]
    [InlineData(PredictionRunOutcome.Unavailable, "PredictionModels_Run_Unavailable")]
    public async Task RunPrediction_ExplainsWhyNothingWasPredicted(PredictionRunOutcome outcome, string key)
    {
        var target = new FakeRunTarget { Next = () => Task.FromResult(new PredictionRunReport(outcome, "SPY", TimeframeType.Daily, null, null)) };
        using var vm = RunVm(target);
        await vm.RunPredictionCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance[key], vm.RunPredictionStatus);
    }

    [Fact]
    public async Task RunPrediction_WithoutADisplayShowsTheNoPredictionTextAndTheNotice()
    {
        var target = new FakeRunTarget { Next = () => Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "SPY", TimeframeType.Daily, null, "window 25 vs 50")) };
        using var vm = RunVm(target);
        await vm.RunPredictionCommand.ExecuteAsync(null);
        Assert.Contains("SPY", vm.RunPredictionStatus);
        Assert.EndsWith("window 25 vs 50", vm.RunPredictionStatus);
        Assert.DoesNotContain("Up 61%", vm.RunPredictionStatus);
    }

    [Fact]
    public async Task RunPrediction_BlocksReentryWhileRunning_AndAFailureIsReportedNotThrown()
    {
        var gate = new TaskCompletionSource<PredictionRunReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new FakeRunTarget { Next = () => gate.Task };
        using var vm = RunVm(target);
        var running = vm.RunPredictionCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunningPrediction);
        Assert.False(vm.RunPredictionCommand.CanExecute(null));
        Assert.Equal(LocalizationManager.Instance["PredictionModels_Run_Running"], vm.RunPredictionStatus);

        gate.SetException(new InvalidOperationException("boom"));
        await running;

        Assert.Equal(1, target.Calls);
        Assert.Equal(LocalizationManager.Instance["PredictionModels_Run_Failed"], vm.RunPredictionStatus);
        Assert.False(vm.IsRunningPrediction);
    }

    [Fact]
    public async Task RunPrediction_ALateResultAfterTheWindowWasClosedIsDropped()
    {
        var gate = new TaskCompletionSource<PredictionRunReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = RunVm(new FakeRunTarget { Next = () => gate.Task });
        var running = vm.RunPredictionCommand.ExecuteAsync(null);
        vm.Dispose();
        gate.SetResult(new PredictionRunReport(PredictionRunOutcome.Completed, "SPY", TimeframeType.Daily, "Up 61%", null));
        await running;
        Assert.DoesNotContain("Up 61%", vm.RunPredictionStatus ?? "");
    }

    [Fact]
    public void RunPrediction_WithoutAnyPrediction_NamesTheCauseTheOwnerCanActOn()
    {
        var registry = new FakeGenerationRegistry { ActiveId = null };
        PredictionRunReport Report(int bars, TimeframeType timeframe = TimeframeType.Daily) => new(PredictionRunOutcome.Completed, "SPY", timeframe, null, null, bars);
        string Status(PredictionRunReport report)
        {
            using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry,
                predictionRunTarget: new FakeRunTarget { Next = () => Task.FromResult(report) });
            vm.RunPredictionCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            return vm.RunPredictionStatus!;
        }
        string Text(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationManager.Instance[key], args);

        // no model active
        Assert.Equal(Text("PredictionModels_Run_NoActive", "SPY", TimeframeType.Daily), Status(Report(300)));

        // an active model, but the chart holds fewer bars than the window
        registry.ActiveId = "current";
        var window = PredictionSettingsManager.DefaultWindowSize;
        Assert.Equal(Text("PredictionModels_Run_TooFewBars", "SPY", TimeframeType.Daily, window - 1, window), Status(Report(window - 1)));

        // an active (daily) model, the chart on another time frame
        Assert.Equal(Text("PredictionModels_Run_TimeframeMismatch", "SPY", TimeframeType.Weekly, "current.onnx", "daily"), Status(Report(300, TimeframeType.Weekly)));

        // a prediction names the model that made it
        var done = Status(new PredictionRunReport(PredictionRunOutcome.Completed, "SPY", TimeframeType.Daily, "Up 61%", null, 300));
        Assert.Contains("current.onnx", done);
        Assert.Contains("Up 61%", done);
    }

    [Fact]
    public void ModelList_ShowsTheRegistryAgain_WhenAnotherWindowAnnouncesAChange()
    {
        var registry = new FakeGenerationRegistry();
        var messenger = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, messenger: messenger);
        Assert.Equal(3, vm.Generations.Count);

        registry.Removed.Add("older");   // e.g. the Training Wizard changed the registry
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(messenger, new StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage());

        Assert.Equal(2, vm.Generations.Count);
    }

    private static string WithLogLoss(string model, double logLoss)
    {
        var metrics = model + ".metrics.json";
        var json = File.ReadAllText(metrics);
        File.WriteAllText(metrics, json.TrimEnd().TrimEnd('}') + ",\"fold_multi_logloss\":" + logLoss.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
        return model;
    }

    [Fact]
    public Task ModelList_MarksAModelWhoseEvaluationIsNotBetterThanGuessing_AndOnlyThat() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(WithLogLoss(CopyRegistrableFixture(root, 1), 5.0));          // far above ln 3
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = WithLogLoss(CopyRegistrableFixture(root, 2), 0.5);                  // clearly better than uniform
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        await vm.GenerationGroupingTask;

        var rows = vm.GenerationRows.Where(r => r.IsItem).ToArray();
        var weak = Assert.Single(rows, r => r.NotBetterThanGuessing);
        var good = Assert.Single(rows, r => !r.NotBetterThanGuessing);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Quality_NotBetter"], weak.QualityHint);
        Assert.Equal(string.Empty, good.QualityHint);
        Assert.Equal(7, weak.Tooltip!.Split('\n').Length);   // the six facts and the explanation
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Tip_Quality"], weak.Tooltip.Split('\n')[6]);
        Assert.Equal(6, good.Tooltip!.Split('\n').Length);
    });

    /// <summary>An <see cref="IStockAnalyzerSettings"/> that only knows the confidence threshold (the one setting the reach text needs).</summary>
    public class ThresholdSettingsProxy : DispatchProxy
    {
        public float Threshold { get; set; } = 0.5f;

        public static IStockAnalyzerSettings Create(float threshold)
        {
            var service = DispatchProxy.Create<IStockAnalyzerSettings, ThresholdSettingsProxy>();
            ((ThresholdSettingsProxy)(object)service).Threshold = threshold;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_" + nameof(IStockAnalyzerSettings.PredictionConfidenceThreshold) ? Threshold : throw new NotSupportedException(targetMethod?.Name);
    }

    /// <summary>Writes the held-out confidence histogram into the metrics file of a model that is about to be registered: <paramref name="counts"/> as (bin, count) pairs over 100 bins.</summary>
    internal static string WithReach(string model, params (int Bin, int Count)[] counts)
    {
        var bins = new int[100];
        foreach (var (bin, count) in counts) bins[bin] = count;
        var metrics = model + ".metrics.json";
        var json = File.ReadAllText(metrics);
        File.WriteAllText(metrics, json.TrimEnd().TrimEnd('}') + ",\"confidence_reach\":{\"bins\":100,\"n\":" + bins.Sum() + ",\"max_probability_histogram\":[" + string.Join(",", bins) + "]}}");
        return model;
    }

    [Fact]
    public Task ModelList_SaysHowOftenAModelReachesTheConfidenceThreshold_AndWarnsOnlyForZero() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (seed, dialog) = create(WithReach(CopyRegistrableFixture(root, 1), (36, 100)));                  // never reaches 50 %
        using var _ = seed;
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = WithReach(CopyRegistrableFixture(root, 2), (36, 90), (55, 10));                  // 10 % reach 50 %
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);
        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, settings: ThresholdSettingsProxy.Create(0.5f),
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        await vm.GenerationGroupingTask;

        var rows = vm.GenerationRows.Where(r => r.IsItem).ToArray();
        var never = Assert.Single(rows, r => r.ReachIsZero);
        var some = Assert.Single(rows, r => !r.ReachIsZero);
        Assert.Equal(0.0, never.ReachShare);
        Assert.Equal(0.10, some.ReachShare!.Value, 12);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationManager.Instance["Settings_ModelGenerations_Reach"],
            "50%", (0.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "%"), never.ReachText);
        Assert.Contains("10.0%", some.ReachText);
        Assert.True(some.HasReachNeutral);
        Assert.False(never.HasReachNeutral);
        Assert.Equal(7, never.Tooltip!.Split('\n').Length);                                                  // the six facts and the reach line
        Assert.Equal(never.ReachText, never.Tooltip.Split('\n')[6]);
    });

    [Fact]
    public Task ModelList_WarnsOnlyWhenNoWindowCanReachTheThreshold_EvenIfTheThresholdFallsBetweenTwoBins() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        // every window is in bin 50 = [0.50, 0.51): with the threshold 0.505 the lower bound of the share is 0 but the windows may reach it
        var (seed, _) = create(WithReach(CopyRegistrableFixture(root, 1), (50, 100)));
        using var _ = seed;
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);
        using var between = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, settings: ThresholdSettingsProxy.Create(0.505f),
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        await between.GenerationGroupingTask;
        var row = Assert.Single(between.GenerationRows, r => r.IsItem);
        Assert.Equal(0.0, row.ReachShare);
        Assert.False(row.ReachIsZero);
        Assert.True(row.HasReachNeutral);

        using var above = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, settings: ThresholdSettingsProxy.Create(0.51f),
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        await above.GenerationGroupingTask;
        Assert.True(Assert.Single(above.GenerationRows, r => r.IsItem).ReachIsZero);                         // no window can reach 0.51
    });

    [Fact]
    public Task ModelList_ShowsNoReachWithoutTheThreshold_OrForAModelWithoutTheHistogram() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (seed, dialog) = create(WithReach(CopyRegistrableFixture(root, 1), (36, 100)));
        using var _ = seed;
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = CopyRegistrableFixture(root, 2);                                                 // an older model: no histogram in its metrics
        await seed.AddGenerationFromFileCommand.ExecuteAsync(null);
        await seed.GenerationGroupingTask;
        Assert.All(seed.GenerationRows.Where(r => r.IsItem), r => Assert.False(r.HasReach));                  // no settings: the threshold is unknown

        using var vm = new PredictionModelsViewModel(new FakePredictionSettingsManager(), modelRegistry: registry, settings: ThresholdSettingsProxy.Create(0.5f),
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        await vm.GenerationGroupingTask;
        var rows = vm.GenerationRows.Where(r => r.IsItem).ToArray();
        Assert.Equal(1, rows.Count(r => r.HasReach));
        var plain = Assert.Single(rows, r => !r.HasReach);
        Assert.Equal(string.Empty, plain.ReachText);
        Assert.Equal(6, plain.Tooltip!.Split('\n').Length);
    });

    [Fact]
    public Task AddModelFromFile_OpensTheFileDialogInTheTrainingWizardFolder() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(null);
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Equal(StockAnalyzer.Core.Common.PathDiscovery.ResolveTrainingWizardArtifactsDirectory(), dialog.LastStartDirectory);
        Assert.EndsWith(System.IO.Path.Combine("TrainingArtifacts", "TW"), dialog.LastStartDirectory);
    });

    [Fact]
    public Task AddModelFromFile_AModelThatWouldNeedAConfirmation_IsTickedWithoutAsking() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var first = registry.ActiveId;
        dialog.PickedPath = CopyRegistrableFixture(root, 2);   // the same score

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.NotEqual(first, registry.ActiveId);
        Assert.Equal(registry.ActiveId, vm.Generations.Single(g => g.IsActive).ModelId);   // the check box is ticked
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
    });
}
