using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class PythonSettingsViewModelTests
{
    // Unlike PythonSettingsViewModel's built-in DesignPythonSettingsManager (a static XAML-designer
    // stub whose getter always returns true regardless of Set calls), this fake actually tracks the
    // last value passed to SetShowUpdateConfirmationDialog, so Save/Revert round-trips can be verified
    // without touching the real Data/Config/user_python_settings.json file.
    private class FakePythonSettingsManager : IPythonSettingsManager
    {
        public bool ShowUpdateConfirmationDialog { get; private set; } = true;
        public void SetShowUpdateConfirmationDialog(bool value) => ShowUpdateConfirmationDialog = value;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void PythonSettingsViewModel_DefaultsToShowUpdateDialogEnabled()
    {
        var vm = new PythonSettingsViewModel();

        Assert.Equal("Settings_Python", vm.TitleKey);
        Assert.Equal("SettingsPythonIcon", vm.IconKey);
        Assert.True(vm.SelectedShowUpdateConfirmationDialog);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void PythonSettingsViewModel_TogglingSetsIsModified()
    {
        var vm = new PythonSettingsViewModel(new FakePythonSettingsManager());

        vm.SelectedShowUpdateConfirmationDialog = false;

        Assert.True(vm.IsModified);
    }

    [Fact]
    public async Task PythonSettingsViewModel_SaveChangesAsync_ClearsIsModified()
    {
        var vm = new PythonSettingsViewModel(new FakePythonSettingsManager());
        vm.SelectedShowUpdateConfirmationDialog = false;
        Assert.True(vm.IsModified);

        await vm.SaveChangesAsync();

        Assert.False(vm.IsModified);
    }

    [Fact]
    public void PythonSettingsViewModel_RevertChanges_RestoresSnapshot()
    {
        var vm = new PythonSettingsViewModel(new FakePythonSettingsManager());
        vm.SelectedShowUpdateConfirmationDialog = false;

        vm.RevertChanges();

        Assert.True(vm.SelectedShowUpdateConfirmationDialog);
        Assert.False(vm.IsModified);
    }
}
