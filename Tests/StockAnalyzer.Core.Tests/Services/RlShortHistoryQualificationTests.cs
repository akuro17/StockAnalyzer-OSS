using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Opt-in real local Data/Daily run of the short-history T15 evaluation; no symbol, period or shortened run is substituted.</summary>
public sealed class RlShortHistoryQualificationTests
{
    public const string EnabledVariable = "SA_RL_SHE_QUALIFICATION";
    public const string SymbolVariable = "SA_RL_SHE_SYMBOL";
    public const string StartVariable = "SA_RL_SHE_START";
    public const string EndVariable = "SA_RL_SHE_END_EXCLUSIVE";
    public sealed class ShortHistoryQualificationAttribute : FactAttribute
    {
        public ShortHistoryQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
                Skip = $"Opt-in existing local Data/Daily CPU DQN short-history run; set {EnabledVariable}=1 with {SymbolVariable}, {StartVariable} and {EndVariable} (yyyy-MM-dd).";
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required.");

    [ShortHistoryQualification]
    public async Task SmokeShortHistoryRun() => Assert.True((await RunAsync(smoke: true)).SmokeOnly);

    [ShortHistoryQualification]
    public async Task FullShortHistoryRun()
    {
        var result = await RunAsync(smoke: false);
        Assert.NotNull(result.ModelId);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(result.EvaluationPath));
        Assert.Equal(RlShortHistoryEvaluationContract.SpecificationId, report.RootElement.GetProperty(RlShortHistoryEvaluationContract.MetadataKey).GetString());
        Assert.True(report.RootElement.GetProperty(RlShortHistoryEvaluationContract.ReportKey).GetProperty("block_count").GetProperty("validation").GetInt32() >= 1);
    }

    private static async Task<RlTrainingResult> RunAsync(bool smoke)
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_rl_short_history_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var job = RlTrainingValidation.Freeze(new RlTrainingJob(Required(SymbolVariable),
            DateOnly.ParseExact(Required(StartVariable), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(Required(EndVariable), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new BacktestConfiguration { InitialCapital = BacktestDefaults.InitialCapital, SizingModel = BacktestDefaults.SizingModel,
                SizingParameter = BacktestDefaults.SizingParameter, LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio }, SmokeOnly: smoke)
        { ShortHistorySpecificationId = RlShortHistoryEvaluationContract.SpecificationId });
        var result = await service.RunAsync(job);
        Assert.Equal(smoke ? 0 : 1, registry.ListRl().Count);
        return result;
    }
}
