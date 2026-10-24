using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Services;
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

    private static (PythonSettingsViewModel Vm, AIPredictionsSettingsViewModelTests.FakePythonService Python, AIPredictionsSettingsViewModelTests.FakeClipboardService Clipboard, AIPredictionsSettingsViewModelTests.FakeToastNotificationService Toast) CreateWithServices()
    {
        LocalizationManager.Instance.Initialize("en");
        var python = new AIPredictionsSettingsViewModelTests.FakePythonService();
        var clipboard = new AIPredictionsSettingsViewModelTests.FakeClipboardService();
        var toast = new AIPredictionsSettingsViewModelTests.FakeToastNotificationService();
        return (new PythonSettingsViewModel(new FakePythonSettingsManager(), python, clipboard, toast), python, clipboard, toast);
    }

    [Fact]
    public async Task ManualUpdateLibraries_CopiesExactlyTheStartupDialogUpdateCommand()
    {
        var (vm, _, clipboard, toast) = CreateWithServices();

        await vm.ManualUpdateLibrariesAsync();

        Assert.Equal(ManualPipCommands.Update(ManualPipCommands.ClipboardShell), clipboard.CopiedText);
        Assert.Equal(LocalizationManager.Instance["Settings_Python_Update_Copied"], vm.StatusMessage);
        Assert.Equal(vm.StatusMessage, toast.NotificationMessage);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task AutoUpdateLibraries_UpgradesTheSamePackageSetAsTheManualCommand()
    {
        var (vm, python, _, toast) = CreateWithServices();

        await vm.AutoUpdateLibrariesAsync();

        Assert.True(python.LastForceUpgrade);
        Assert.Equal(PythonPackageSets.Update, python.LastPackages);
        var manual = ManualPipCommands.Update(ManualPipCommands.ClipboardShell);
        Assert.All(PythonPackageSets.Update, package => Assert.Contains(package, manual));
        Assert.Contains("--upgrade", manual);
        Assert.Equal(LocalizationManager.Instance["Settings_Python_Update_Success"], vm.StatusMessage);
        Assert.Equal(vm.StatusMessage, toast.NotificationMessage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task AutoUpdateLibraries_IsBusyWhileRunning_AndASecondCallIsIgnored()
    {
        var (vm, python, _, _) = CreateWithServices();
        var release = new TaskCompletionSource();
        var calls = 0;
        python.InstallBehavior = () => { calls++; return release.Task; };

        var first = vm.AutoUpdateLibrariesAsync();
        Assert.True(vm.IsBusy);
        await vm.AutoUpdateLibrariesAsync();
        Assert.Equal(1, calls);

        release.SetResult();
        await first;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AutoUpdateLibraries_Failure_ShowsTheErrorAndClearsBusy()
    {
        var (vm, python, _, toast) = CreateWithServices();
        python.InstallBehavior = () => throw new InvalidOperationException("boom");

        await vm.AutoUpdateLibrariesAsync();

        Assert.Equal(string.Format(LocalizationManager.Instance["Settings_Python_Update_Error"], "boom"), vm.StatusMessage);
        Assert.Equal(vm.StatusMessage, toast.NotificationMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UpdateCommands_WithoutServices_DoNothing()
    {
        var vm = new PythonSettingsViewModel(new FakePythonSettingsManager());

        await vm.ManualUpdateLibrariesAsync();
        await vm.AutoUpdateLibrariesAsync();

        Assert.Null(vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }
}