using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>The one-time <c>.v1.bak</c> copy made when the first condition-tree (version 2) save replaces a version 1 file (MP-2 addendum A5).</summary>
public sealed class BacktestConfigurationManagerBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"backtest_backup_{Guid.NewGuid():N}");
    private readonly string _path;

    public BacktestConfigurationManagerBackupTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "backtest_configuration.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string BackupPath => _path + ".v1.bak";

    private BacktestConfigurationManager CreateManager() => new(new MockStockAnalyzerSettings(), _path);

    private static BacktestConfigurationDto Config(int version, string symbol = "AAPL") => new()
    {
        SchemaVersion = version,
        Symbol = symbol,
        Frame = TimeFrame.D1,
        EvaluationStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EvaluationEndUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        InitialCapital = 1_000_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 0.30m,
        MaintenanceMarginRatio = 0.20m,
        LiquidationPenaltyRatio = 0.005m,
        ConditionTree = version == BacktestConfigurationDto.ConditionTreeSchemaVersion ? new BacktestConditionTreeDto() : null,
    };

    [Fact]
    public async Task FirstVersion2Save_OverAVersion1File_CreatesAByteIdenticalBackup()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion));
        byte[] original = await File.ReadAllBytesAsync(_path);

        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));

        Assert.Equal(original, await File.ReadAllBytesAsync(BackupPath));
        Assert.NotEqual(original, await File.ReadAllBytesAsync(_path));
    }

    [Fact]
    public async Task Backup_IsMadeThroughATemporaryFile_ThatIsGoneAfterwards_AndAStaleOneDoesNotBlockIt()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion));
        byte[] original = await File.ReadAllBytesAsync(_path);
        // Left behind by an earlier crash in the middle of a copy.
        await File.WriteAllTextAsync(BackupPath + ".tmp", "torn");

        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));

        Assert.Equal(original, await File.ReadAllBytesAsync(BackupPath));
        Assert.False(File.Exists(BackupPath + ".tmp"));
    }

    [Fact]
    public async Task SecondVersion2Save_LeavesTheBackupUnchanged()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion));
        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));
        byte[] backup = await File.ReadAllBytesAsync(BackupPath);

        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion, "MSFT"));

        Assert.Equal(backup, await File.ReadAllBytesAsync(BackupPath));
    }

    [Fact]
    public async Task ExistingBackup_IsNeverOverwritten_AndDoesNotBlockTheSave()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion));
        await File.WriteAllTextAsync(BackupPath, "older backup");

        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));

        Assert.Equal("older backup", await File.ReadAllTextAsync(BackupPath));
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, (await manager.LoadAsync()).Status);
    }

    [Fact]
    public async Task BackupFailure_AbortsTheSave_AndLeavesTheOriginalFile()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion));
        byte[] original = await File.ReadAllBytesAsync(_path);
        Directory.CreateDirectory(BackupPath);

        // A directory at the backup path is not an existing backup file: the copy fails.
        IOException ex = await Assert.ThrowsAsync<IOException>(() => manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion)));

        Assert.Contains(BackupPath, ex.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(_path));
    }

    [Fact]
    public async Task Version2OverVersion2_CreatesNoBackup()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));

        await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion, "MSFT"));

        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task FreshInstall_WritesNoBackup()
    {
        await CreateManager().SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion));

        Assert.False(File.Exists(BackupPath));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    public async Task ExistingFileWhoseVersionCannotBeDetermined_AbortsTheSave_AndLeavesTheFileAndNoBackup(string content)
    {
        await File.WriteAllTextAsync(_path, content);

        IOException ex = await Assert.ThrowsAsync<IOException>(() => CreateManager().SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion)));

        Assert.Contains(_path, ex.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(_path));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task Version1Save_NeverCreatesABackup()
    {
        BacktestConfigurationManager manager = CreateManager();
        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion));

        await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion, "MSFT"));

        Assert.False(File.Exists(BackupPath));
    }
}
