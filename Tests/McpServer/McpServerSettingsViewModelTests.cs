using System;
using System.IO;
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
        Assert.Equal(4, vm.MaxActiveSessions);
        Assert.Equal(6, vm.MaxParticipantsPerSession);
        Assert.Equal(300, vm.MaxMessagesPerSession);
        Assert.Equal(20, vm.MaxWaitSeconds);
        Assert.False(vm.IsModified);
        Assert.True(vm.IsValid);
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
}
