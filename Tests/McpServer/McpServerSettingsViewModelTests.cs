using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class McpServerSettingsViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testFilePath;

    public McpServerSettingsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer_VmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _testFilePath = Path.Combine(_tempDir, "user_mcpserver_settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task ViewModel_InitialState_ReflectsDefaults()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        Assert.False(vm.EnableDiscussion);
        Assert.False(vm.UseTextOnlyResponse);
        Assert.False(vm.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.Default, vm.MaxResponseTokens);
        Assert.Equal(McpResponseTokenLimits.Minimum, vm.MaxResponseTokensMinimum);
        Assert.Equal(McpResponseTokenLimits.Maximum, vm.MaxResponseTokensMaximum);
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, vm.MaxResponseTokensRecommended);
        Assert.Equal(4, vm.MaxActiveSessions);
        Assert.Equal(6, vm.MaxParticipantsPerSession);
        Assert.Equal(300, vm.MaxMessagesPerSession);
        Assert.Equal(20, vm.MaxWaitSeconds);
        Assert.False(vm.IsModified);
        Assert.True(vm.IsValid);
    }

    [Fact]
    public async Task ViewModel_TokenOptimizationOptions_ValidateLaunchArgsAndResetCorrectly()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        using var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        vm.UseTextOnlyResponse = true;
        vm.CompactDefaults = true;
        Assert.True(vm.IsModified);
        Assert.Equal("--response-format text-only --compact-defaults", vm.LaunchArgumentsPreview);

        vm.ApplyRecommendedMaxResponseTokensCommand.Execute(null);
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, vm.MaxResponseTokens);
        Assert.True(vm.IsValid);
        Assert.True(string.IsNullOrEmpty(vm.MaxResponseTokensError));

        vm.MaxResponseTokens = McpResponseTokenLimits.Minimum - 1;
        Assert.False(vm.IsValid);
        Assert.False(string.IsNullOrEmpty(vm.MaxResponseTokensError));

        vm.MaxResponseTokens = McpResponseTokenLimits.Maximum + 1;
        Assert.False(vm.IsValid);
        Assert.False(string.IsNullOrEmpty(vm.MaxResponseTokensError));

        vm.MaxResponseTokens = McpResponseTokenLimits.RecommendedStrict;
        Assert.True(vm.IsValid);
        Assert.True(string.IsNullOrEmpty(vm.MaxResponseTokensError));

        await vm.SaveChangesAsync();
        Assert.False(vm.IsModified);

        vm.ResetToDefault();
        Assert.False(vm.UseTextOnlyResponse);
        Assert.False(vm.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.Default, vm.MaxResponseTokens);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public async Task ViewModel_ApplyRecommendedTokenPreset_ConfiguresPresetWithoutAlteringDiscussion()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        using var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        vm.EnableDiscussion = true;
        vm.ApplyRecommendedTokenPresetCommand.Execute(null);

        Assert.True(vm.EnableDiscussion);
        Assert.Equal(McpToolsetProfiles.RecommendedPresetToolset, vm.Toolset);
        Assert.False(vm.UseAllToolsets);
        Assert.True(vm.UseCoreToolset);
        Assert.True(vm.UseTechnicalToolset);
        Assert.False(vm.UseTechnicalExtendedToolset);
        Assert.False(vm.UseScreenerToolset);
        Assert.False(vm.UseBacktestToolset);
        Assert.False(vm.UsePortfolioToolset);
        Assert.False(vm.UseMlToolset);
        Assert.True(vm.UseTextOnlyResponse);
        Assert.True(vm.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, vm.MaxResponseTokens);
        Assert.True(vm.IsValid);
        Assert.True(vm.IsModified);
        Assert.Equal("--enable-discussion --toolset core,technical --response-format text-only --compact-defaults", vm.LaunchArgumentsPreview);
    }

    [Fact]
    public async Task ViewModel_ModifyingProperty_SetsIsModifiedTrue()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        Assert.False(vm.IsModified);

        vm.EnableDiscussion = true;
        Assert.True(vm.IsModified);

        vm.EnableDiscussion = false;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task ViewModel_ValueOutsideBand_ShowsValidationError()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        Assert.True(string.IsNullOrEmpty(vm.MaxActiveSessionsError));

        // Band is 1..16
        vm.MaxActiveSessions = 20;

        Assert.False(string.IsNullOrEmpty(vm.MaxActiveSessionsError));
        Assert.False(vm.IsValid);

        vm.MaxActiveSessions = 5;
        Assert.True(string.IsNullOrEmpty(vm.MaxActiveSessionsError));
        Assert.True(vm.IsValid);
    }

    [Fact]
    public async Task ViewModel_CrossValidation_DefaultReadLimitCannotExceedMaxReadLimit()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        vm.MaxReadLimit = 30;
        vm.DefaultReadLimit = 40; // Exceeds MaxReadLimit

        Assert.False(string.IsNullOrEmpty(vm.DefaultReadLimitError));
        Assert.False(vm.IsValid);

        vm.DefaultReadLimit = 25;
        Assert.True(string.IsNullOrEmpty(vm.DefaultReadLimitError));
        Assert.True(vm.IsValid);
    }

    [Fact]
    public async Task ViewModel_SaveAndRevert_WorksCorrectly()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        vm.EnableDiscussion = true;
        vm.MaxActiveSessions = 8;
        Assert.True(vm.IsModified);

        await vm.SaveChangesAsync();

        Assert.False(vm.IsModified);
        var saved = await manager.LoadAsync();
        Assert.True(saved.EnableDiscussion);
        Assert.Equal(8, saved.Discussion.MaxActiveSessions);

        // Edit again, then revert
        vm.MaxActiveSessions = 12;
        Assert.True(vm.IsModified);

        vm.RevertChanges();
        Assert.False(vm.IsModified);
        Assert.Equal(8, vm.MaxActiveSessions);
    }

    [Fact]
    public async Task ViewModel_ResetToDefault_RestoresSystemDefaults()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        vm.EnableDiscussion = true;
        vm.MaxActiveSessions = 10;
        await vm.SaveChangesAsync();

        vm.ResetToDefault();
        Assert.False(vm.EnableDiscussion);
        Assert.Equal(4, vm.MaxActiveSessions);
        Assert.True(vm.IsModified); // Differs from saved snapshot (which had 10)
    }

    [Fact]
    public void SettingsConstants_McpServerCategory_IsPlacedImmediatelyAfterBacktest()
    {
        var categories = StockAnalyzer.Avalonia.Models.SettingsConstants.Categories;
        var backtestIndex = -1;
        var mcpIndex = -1;

        for (int i = 0; i < categories.Count; i++)
        {
            if (categories[i].Key == StockAnalyzer.Avalonia.Models.SettingsConstants.Keys.Backtest)
            {
                backtestIndex = i;
            }
            else if (categories[i].Key == StockAnalyzer.Avalonia.Models.SettingsConstants.Keys.McpServer)
            {
                mcpIndex = i;
            }
        }

        Assert.True(backtestIndex >= 0, "Backtest category should exist");
        Assert.True(mcpIndex >= 0, "McpServer category should exist");
        Assert.Equal(backtestIndex + 1, mcpIndex);
        Assert.Empty(categories[backtestIndex].Children);
    }

    [Fact]
    public async Task ViewModel_ToolsetCheckboxesAndValidation_WorkEndToEnd()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        using var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        Assert.True(vm.UseAllToolsets);
        Assert.Equal(string.Empty, vm.Toolset);
        Assert.True(string.IsNullOrEmpty(vm.ToolsetError));

        vm.UseCoreToolset = true;
        vm.UseTechnicalExtendedToolset = true;
        vm.UseBacktestToolset = true;

        Assert.False(vm.UseAllToolsets);
        Assert.Equal("core,technical-extended,backtest", vm.Toolset);
        Assert.True(vm.IsModified);
        Assert.True(vm.IsValid);
        Assert.Equal("--toolset core,technical-extended,backtest", vm.LaunchArgumentsPreview);

        // Invalid toolset profile fails validation
        vm.Toolset = "core,unknown_profile";
        Assert.False(vm.IsValid);
        Assert.False(string.IsNullOrEmpty(vm.ToolsetError));

        await vm.SaveChangesAsync();
        Assert.False(string.IsNullOrEmpty(vm.ValidationMessage));

        // Clicking All restores empty default and clears domain flags
        vm.UseAllToolsets = true;
        Assert.Equal(string.Empty, vm.Toolset);
        Assert.False(vm.UseCoreToolset);
        Assert.False(vm.UseTechnicalExtendedToolset);
        Assert.False(vm.UseBacktestToolset);
        Assert.True(vm.IsValid);
    }

    [Fact]
    public async Task ViewModel_StopAndRestartCommands_ManageServerLifecycleAndPersistValidChanges()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var processService = new FakeMcpServerProcessService(manager);
        using var vm = new McpServerSettingsViewModel(manager, processService: processService);
        await vm.LoadTask;

        Assert.False(vm.IsServerRunning);
        Assert.False(vm.StopServerCommand.CanExecute(null));
        Assert.True(vm.RestartServerCommand.CanExecute(null));

        vm.EnableDiscussion = true;
        vm.Toolset = "core,screener";

        // Restart saves pending valid edits first and starts with updated arguments
        await vm.RestartServerCommand.ExecuteAsync(null);

        Assert.False(vm.IsServerOperationInProgress);
        Assert.False(string.IsNullOrEmpty(vm.ServerActionMessage));
        Assert.Equal(1, processService.RestartCallCount);
        Assert.True(vm.IsServerRunning);
        Assert.Equal(4242, vm.ServerProcessId);
        Assert.Contains("4242", vm.ServerStatusText, StringComparison.Ordinal);
        Assert.Equal("--enable-discussion --toolset core,screener", processService.LastArguments);
        Assert.Contains("--enable-discussion --toolset core,screener", vm.LaunchArgumentsDisplay, StringComparison.Ordinal);
        Assert.False(vm.IsModified);
        Assert.True(vm.StopServerCommand.CanExecute(null));

        // Stop shuts down managed process
        vm.StopServerCommand.Execute(null);
        Assert.False(vm.IsServerOperationInProgress);
        Assert.Equal(1, processService.StopCallCount);
        Assert.False(vm.IsServerRunning);
        Assert.Null(vm.ServerProcessId);
        Assert.False(vm.StopServerCommand.CanExecute(null));

        // Invalid settings prevent restart
        vm.Toolset = "not_a_profile";
        await vm.RestartServerCommand.ExecuteAsync(null);
        Assert.False(vm.IsServerOperationInProgress);
        Assert.Equal(1, processService.RestartCallCount);
        Assert.False(string.IsNullOrEmpty(vm.ValidationMessage));
    }

    [Fact]
    public async Task ViewModel_ProgressiveDiscovery_UpdatesPreviewAndRemainsUntouchedByRecommendedPreset()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        using var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        Assert.False(vm.ProgressiveDiscovery);

        vm.ApplyRecommendedTokenPresetCommand.Execute(null);
        Assert.False(vm.ProgressiveDiscovery);

        vm.ProgressiveDiscovery = true;
        Assert.True(vm.IsModified);
        Assert.Contains("--progressive-discovery", vm.LaunchArgumentsPreview, StringComparison.Ordinal);

        await vm.SaveChangesAsync();
        Assert.False(vm.IsModified);

        vm.ResetToDefault();
        Assert.False(vm.ProgressiveDiscovery);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public async Task ViewModel_ApplyProgressiveTokenPreset_ConfiguresFourPartPresetWithoutAlteringDiscussion()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        using var vm = new McpServerSettingsViewModel(manager);
        await vm.LoadTask;

        vm.EnableDiscussion = true;
        vm.Toolset = McpToolsetProfiles.RecommendedPresetToolset;
        vm.ApplyProgressiveTokenPresetCommand.Execute(null);

        Assert.True(vm.EnableDiscussion);
        Assert.True(vm.ProgressiveDiscovery);
        Assert.Equal(string.Empty, vm.Toolset);
        Assert.True(vm.UseAllToolsets);
        Assert.False(vm.UseCoreToolset);
        Assert.False(vm.UseTechnicalToolset);
        Assert.True(vm.UseTextOnlyResponse);
        Assert.True(vm.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, vm.MaxResponseTokens);
        Assert.True(vm.IsValid);
        Assert.True(vm.IsModified);
        Assert.Equal("--enable-discussion --response-format text-only --compact-defaults --progressive-discovery", vm.LaunchArgumentsPreview);
    }

    private sealed class FakeMcpServerProcessService : IMcpServerProcessService
    {
        private static readonly string FakeExePath = OperatingSystem.IsWindows() ? "StockAnalyzer.McpServer.exe" : "StockAnalyzer.McpServer";
        private readonly IMcpServerSettingsManager _settingsManager;

        public FakeMcpServerProcessService(IMcpServerSettingsManager settingsManager)
        {
            _settingsManager = settingsManager;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsRunning { get; private set; }
        public int? ProcessId => IsRunning ? 4242 : null;
        public int StopCallCount { get; private set; }
        public int RestartCallCount { get; private set; }
        public string LastArguments { get; private set; } = string.Empty;

        public string? ResolveExecutablePath() => FakeExePath;

        public async Task<McpServerLaunchResult> StartServerAsync(CancellationToken cancellationToken = default)
        {
            var settings = await _settingsManager.LoadAsync(cancellationToken).ConfigureAwait(false);
            LastArguments = settings.BuildLaunchArguments();
            IsRunning = true;
            NotifyState();
            return new McpServerLaunchResult(true, ProcessId, "Started.", FakeExePath, LastArguments);
        }

        public async Task<McpServerLaunchResult> RestartServerAsync(CancellationToken cancellationToken = default)
        {
            RestartCallCount++;
            IsRunning = false;
            return await StartServerAsync(cancellationToken).ConfigureAwait(false);
        }

        public void StopServer()
        {
            StopCallCount++;
            IsRunning = false;
            NotifyState();
        }

        public void Dispose()
        {
        }

        private void NotifyState([CallerMemberName] string? _ = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessId)));
        }
    }
}
