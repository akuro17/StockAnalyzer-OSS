using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class TrainingResourceSettingsViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_training_page_" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_directory, TrainingResourceSettings.FileName);

    public TrainingResourceSettingsViewModelTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ValidPreview_InvalidEntries_ResetAndSave()
    {
        var service = new TrainingResourceSettings(PathName);
        using var page = new TrainingResourceSettingsViewModel(service);
        page.ChannelsEnabled = true;
        Assert.Equal(TrainingResourceOverrides.MaximumChannels, service.Snapshot.MaxChannels);
        page.ChannelsValue = 7;
        Assert.Equal(7, service.Snapshot.MaxChannels);

        page.ChannelsValue = null;
        Assert.NotNull(page.ValidationMessage);
        Assert.Equal(7, service.Snapshot.MaxChannels);
        Assert.False(await page.TrySaveChangesAsync());
        page.ChannelsValue = 1.5m;
        Assert.False(await page.TrySaveChangesAsync());
        Assert.Equal(7, service.Snapshot.MaxChannels);
        page.ChannelsValue = 1;
        page.TensorEnabled = true;
        page.TensorValue = TrainingResourceOverrides.MaximumTensorSizeMiB;
        page.SamplesEnabled = true;
        page.SamplesValue = TrainingResourceOverrides.MaximumSamples;
        Assert.True(await page.TrySaveChangesAsync());
        Assert.False(page.IsModified);
        Assert.Equal(new TrainingResourceOverrides(1, 256, 10_000), service.SavedSnapshot);

        page.ResetToDefault();
        Assert.Equal(new TrainingResourceOverrides(), service.Snapshot);
        Assert.True(page.IsModified);
        page.RevertChanges();
        Assert.Equal(service.SavedSnapshot, service.Snapshot);
    }

    [Fact]
    public void SecondPage_IsReadOnlyUntilTheFirstPageReleasesPreview()
    {
        var service = new TrainingResourceSettings(PathName);
        var first = new TrainingResourceSettingsViewModel(service);
        using var second = new TrainingResourceSettingsViewModel(service);
        Assert.True(second.HasOwnershipWarning);
        Assert.False(second.IsEditable);
        second.ChannelsEnabled = true;
        Assert.Equal(new TrainingResourceOverrides(), service.Snapshot);
        first.Dispose();
        second.Activate();
        Assert.True(second.IsEditable);
        second.ChannelsEnabled = true;
        Assert.Equal(TrainingResourceOverrides.MaximumChannels, service.Snapshot.MaxChannels);
    }

    [Fact]
    public async Task Shell_SaveThenEditThenClose_RestoresLastSavedPreview()
    {
        var service = new TrainingResourceSettings(PathName);
        using var shell = CreateShell(service);
        var page = Navigate(shell);
        page.ChannelsEnabled = true;
        page.ChannelsValue = 9;
        await shell.SaveAllChangesCommand.ExecuteAsync(null);
        Assert.Equal(9, service.SavedSnapshot.MaxChannels);
        page.ChannelsValue = 10;
        Assert.Equal(10, service.Snapshot.MaxChannels);
        Assert.True(shell.OnClosing());
        Assert.Equal(9, service.Snapshot.MaxChannels);
    }

    [Fact]
    public async Task Shell_CorruptFileSaveFailure_LeavesDialogOpenAndPreservesFile()
    {
        const string invalid = "{invalid";
        File.WriteAllText(PathName, invalid);
        var service = new TrainingResourceSettings(PathName);
        using var shell = CreateShell(service);
        var page = Navigate(shell);
        page.ChannelsEnabled = true;
        var closed = false;
        shell.RequestClose += () => closed = true;
        await shell.SaveAndCloseCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.True(page.IsModified);
        Assert.NotNull(page.ValidationMessage);
        Assert.Equal(invalid, File.ReadAllText(PathName));
        Assert.True(shell.OnClosing());
        Assert.Equal(new TrainingResourceOverrides(), service.Snapshot);
    }

    [Fact]
    public async Task Shell_RejectsEditAndCloseWhileSaveIsInProgress()
    {
        var service = new PausedSaveSettings(new TrainingResourceSettings(PathName));
        using var shell = CreateShell(service);
        var page = Navigate(shell);
        page.ChannelsEnabled = true;
        page.ChannelsValue = 9;

        var saving = shell.SaveAllChangesCommand.ExecuteAsync(null);
        Assert.True(page.IsSaveInProgress);
        Assert.False(shell.OnClosing());
        page.ChannelsValue = 10;
        Assert.Equal(9, service.Snapshot.MaxChannels);
        service.ReleaseSave();
        await saving;
        Assert.Equal(9, service.SavedSnapshot.MaxChannels);
    }

    private sealed class PausedSaveSettings : ITrainingResourceSettings
    {
        private readonly ITrainingResourceSettings _inner;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PausedSaveSettings(ITrainingResourceSettings inner) => _inner = inner;
        public TrainingResourceOverrides Snapshot => _inner.Snapshot;
        public TrainingResourceOverrides SavedSnapshot => _inner.SavedSnapshot;
        public string? LoadError => _inner.LoadError;
        public bool IsSaving => _inner.IsSaving;
        public bool TryAcquirePreview(object owner) => _inner.TryAcquirePreview(owner);
        public void SetPreview(object owner, TrainingResourceOverrides value) => _inner.SetPreview(owner, value);
        public void ReleasePreview(object owner) => _inner.ReleasePreview(owner);
        public async Task SaveAsync(object owner)
        {
            await _release.Task;
            await _inner.SaveAsync(owner);
        }
        public void ReleaseSave() => _release.SetResult();
    }

    private static SettingsViewModel CreateShell(ITrainingResourceSettings service)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IThemeManager>(new ThemeManager());
        services.AddTransient<ThemeSettingsViewModel>();
        services.AddSingleton(service);
        services.AddTransient<TrainingResourceSettingsViewModel>();
        return new SettingsViewModel(services.BuildServiceProvider());
    }

    private static TrainingResourceSettingsViewModel Navigate(SettingsViewModel shell)
    {
        var category = SettingsConstants.Categories.Single(c => c.Key == SettingsConstants.Keys.TrainingWizard);
        var notesIndex = SettingsConstants.Categories.ToList().FindIndex(c => c.Key == SettingsConstants.Keys.Notes);
        Assert.Equal(notesIndex - 1, SettingsConstants.Categories.ToList().IndexOf(category));
        shell.SelectedCategory = category;
        return Assert.IsType<TrainingResourceSettingsViewModel>(shell.CurrentPage);
    }
}
