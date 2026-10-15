#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// A registered RL model whose work folder no longer exists is labelled in the model lists of the RL window and of the RL Playback page,
/// and the label comes with a button that removes that model after a confirmation.
/// </summary>
public sealed partial class RlUiTests
{
    private const string NoWorkFolderLabel = "Rl.Ui.NoWorkFolder";

    private static EmptyRegistry RegistryWith(params string[] withoutWorkFolder) => new()
    {
        Generations = new[] { PlaybackGeneration("a"), PlaybackGeneration("b", ageDays: 1) },
        ModelsWithoutWorkFolder = new HashSet<string>(withoutWorkFolder),
    };

    private static Mock<IDialogService> Confirming(bool answer)
    {
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(answer);
        return dialogs;
    }

    private async Task<RlTrainingDialogViewModel> RlWindow(EmptyRegistry registry, IDialogService? dialogs)
    {
        var chart = Chart(new ImmediateData());
        var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart,
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance, data: new PlaybackData(registry), dialogService: dialogs);
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task RlWindow_LabelsOnlyTheModelWithoutWorkFolder_AndOffersTheButtonForIt()
    {
        using var vm = await RlWindow(RegistryWith("a"), Confirming(true).Object);

        Assert.Contains(NoWorkFolderLabel, vm.ModelOptions.Single(o => o.Generation.ModelId == "a").Name);
        Assert.DoesNotContain(NoWorkFolderLabel, vm.ModelOptions.Single(o => o.Generation.ModelId == "b").Name);
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == "a");
        Assert.True(vm.SelectedModelHasNoWorkFolder); Assert.True(vm.CanRemoveSelectedModel);
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == "b");
        Assert.False(vm.SelectedModelHasNoWorkFolder); Assert.False(vm.CanRemoveSelectedModel);
    }

    [Fact]
    public async Task RlWindow_RemoveAsksFirst_ThenRemovesTheModelAndRefreshesTheList()
    {
        var registry = RegistryWith("a"); var dialogs = Confirming(true);
        using var vm = await RlWindow(registry, dialogs.Object);
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == "a");

        await vm.RemoveSelectedModelCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.Is<string>(text => text.Contains("Rl.Ui.RemoveModel.Confirm"))), Times.Once);
        Assert.Equal(new[] { "a" }, registry.RemoveCalls);
        Assert.Equal(new[] { "b" }, vm.Generations.Select(g => g.ModelId));
        Assert.Contains("Rl.Ui.RemoveModel.Done", vm.Message);
    }

    [Fact]
    public async Task RlWindow_DecliningTheConfirmationRemovesNothing()
    {
        var registry = RegistryWith("a");
        using var vm = await RlWindow(registry, Confirming(false).Object);
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == "a");

        await vm.RemoveSelectedModelCommand.ExecuteAsync(null);

        Assert.Empty(registry.RemoveCalls); Assert.Equal(2, vm.Generations.Count);
    }

    [Fact]
    public async Task RlWindow_ARefusedRemovalKeepsTheModelAndStatesTheReason()
    {
        var registry = RegistryWith("a");
        registry.RemoveResult = _ => new(false, ImmutableArray.Create("Active"));
        using var vm = await RlWindow(registry, Confirming(true).Object);
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == "a");

        await vm.RemoveSelectedModelCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Generations.Count);
        Assert.Contains("Rl.Ui.RemoveModel.Refused", vm.Message);
    }

    [Fact]
    public async Task RlWindow_WithoutADialogServiceTheButtonIsNeverOffered()
    {
        using var vm = await RlWindow(RegistryWith("a"), dialogs: null);
        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == "a");

        Assert.True(vm.SelectedModelHasNoWorkFolder); Assert.False(vm.CanRemoveSelectedModel);
    }

    private static async Task<RlPlaybackSettingsViewModel> PlaybackPage(EmptyRegistry registry, IDialogService? dialogs)
    {
        var data = new PlaybackData(registry);
        var vm = new RlPlaybackSettingsViewModel(registry, data, new RecordingReplayTarget(), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance,
            new SynchronousDispatcherService(), dialogService: dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        await PlaybackUntil(() => vm.ModelsState != RlDialogLoadState.Loading && vm.BlocksState != RlDialogLoadState.Loading);
        return vm;
    }

    [Fact]
    public async Task Playback_LabelsOnlyTheModelWithoutWorkFolder_AndRemovesItAfterConfirmation()
    {
        var registry = RegistryWith("a"); var dialogs = Confirming(true);
        using var vm = await PlaybackPage(registry, dialogs.Object);

        Assert.Contains(NoWorkFolderLabel, vm.ModelOptions.Single(o => o.Generation.ModelId == "a").Name);
        Assert.DoesNotContain(NoWorkFolderLabel, vm.ModelOptions.Single(o => o.Generation.ModelId == "b").Name);
        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "a");
        Assert.True(vm.SelectedModelHasNoWorkFolder); Assert.True(vm.CanRemoveSelectedModel);

        await vm.RemoveSelectedModelCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.Equal(new[] { "a" }, registry.RemoveCalls);
        Assert.Equal(new[] { "b" }, vm.ModelOptions.Select(o => o.Generation.ModelId));
        Assert.Contains("Rl.Ui.RemoveModel.Done", vm.StatusMessage);
    }

    [Fact]
    public async Task Playback_NoButtonForAModelWithItsWorkFolder_AndNothingIsRemovedWhenDeclined()
    {
        var registry = RegistryWith("a");
        using var vm = await PlaybackPage(registry, Confirming(false).Object);
        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "b");
        Assert.False(vm.SelectedModelHasNoWorkFolder); Assert.False(vm.CanRemoveSelectedModel);

        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "a");
        await vm.RemoveSelectedModelCommand.ExecuteAsync(null);

        Assert.Empty(registry.RemoveCalls); Assert.Equal(2, vm.ModelOptions.Count);
    }
}
