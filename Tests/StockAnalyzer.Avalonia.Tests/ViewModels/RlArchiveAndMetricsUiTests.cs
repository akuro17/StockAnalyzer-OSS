#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    [Fact]
    public async Task CheckpointExport_CommandExecutionUpdatesUiState()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RlUiArchiveTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var chart = Chart(new ImmediateData());
            var review = new RlCheckpointReview(
                Guid.NewGuid(),
                tempDir,
                "checkpoint_001",
                42,
                1000,
                "train",
                "hash123",
                true,
                null);

            var registry = new EmptyRegistry
            {
                Capabilities = new(Storage: false, Recovery: false, Checkpoints: true),
                Checkpoints = new[] { review }
            };

            using var vm = new RlTrainingDialogViewModel(
                new DelayedTraining(),
                registry,
                new UnavailableQ(),
                new FakeBacktestDefaultSettingsManager(),
                chart,
                StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);

            await vm.InitializeAsync();
            Assert.False(vm.CanExportCheckpoint);

            await vm.InspectCheckpointsCommand.ExecuteAsync(null);
            Assert.NotEmpty(vm.CheckpointRows);

            vm.SelectedCheckpoint = vm.CheckpointRows[0];
            Assert.True(vm.CanExportCheckpoint);

            // Execute export (headless fallback writes to Path.GetTempPath()/StockAnalyzer_Exports)
            await vm.ExportCheckpointCommand.ExecuteAsync(null);
            Assert.Contains("Rl.Archive.ExportSucceeded", vm.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GenerationExport_CommandExecutionUpdatesUiState()
    {
        using var chart = Chart(new ImmediateData());
        var gen = new RlGeneration(
            new string('b', 64),
            "SPY",
            "rev1",
            RlContract.ContractHash,
            "eval",
            0.15m,
            0.08m,
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        var registry = new EmptyRegistry
        {
            Generations = new[] { gen }
        };

        using var vm = new RlTrainingDialogViewModel(
            new DelayedTraining(),
            registry,
            new UnavailableQ(),
            new FakeBacktestDefaultSettingsManager(),
            chart,
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);

        await vm.InitializeAsync();
        Assert.False(vm.CanExportGeneration);

        vm.SelectedGeneration = gen;
        Assert.True(vm.CanExportGeneration);

        // The empty registry hands out no lease, so there are no model files to archive: the export reports it instead of a success.
        await vm.ExportGenerationCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Rl.Archive.ExportSucceeded", vm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generation is unavailable", vm.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
    }
}
