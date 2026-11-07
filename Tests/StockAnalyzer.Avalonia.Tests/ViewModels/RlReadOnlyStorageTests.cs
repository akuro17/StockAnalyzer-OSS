#nullable enable
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>D-2' Run 3b / S4: the RL training window refuses to train (and to change the model store) when the data folder cannot be written; with no state or a writable one nothing changes.</summary>
public sealed partial class RlUiTests
{
    private async Task<RlTrainingDialogViewModel> ReadyDialogAsync(ChartViewModel chart, IDataStorageState? state)
    {
        var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart,
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance, dataStorageState: state);
        await vm.InitializeAsync();
        vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01";
        return vm;
    }

    [Fact]
    public async Task ReadOnlyStorage_TrainIsRefused_WithEveryInputValid_AndTheFooterNamesTheReason()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = await ReadyDialogAsync(chart, StockAnalyzer.TestSupport.TestDataStorageState.StateOf(writable: false));

        Assert.False(vm.IsStorageWritable);
        Assert.False(vm.CanTrain);
        Assert.False(vm.TrainCommand.CanExecute(null));
        Assert.True(vm.HasStartBlocker);
        Assert.Equal(LocalizationManager.Instance[StorageFeatureGate.DisabledToolTipKey], vm.StartBlockerDisplay);
        Assert.Equal(vm.StorageDisabledToolTip, vm.StartBlockerDisplay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoStateOrWritableStorage_TrainsExactlyAsBefore(bool withWritableState)
    {
        using var chart = Chart(new ImmediateData());
        using var vm = await ReadyDialogAsync(chart, withWritableState ? StockAnalyzer.TestSupport.TestDataStorageState.StateOf(writable: true) : null);

        Assert.True(vm.IsStorageWritable);
        Assert.True(vm.CanTrain);
        Assert.True(vm.TrainCommand.CanExecute(null));
        Assert.False(vm.HasStartBlocker);
        Assert.Null(vm.StorageDisabledToolTip);
    }
}
