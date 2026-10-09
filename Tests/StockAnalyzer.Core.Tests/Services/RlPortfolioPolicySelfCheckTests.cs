#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Opt-in (same gate as the portfolio smoke run; managed Python venv with the pinned stack): runs <c>Assets/rl_portfolio_policy_selfcheck.py</c>, which builds
/// the policy through the trainer's own constructors on a tiny seeded environment and checks the wiring (optimizer coverage, log_std, orthogonal
/// initialisation, one PPO update, slot-order equivariance, masked-slot isolation, exported actor = policy mean) and of the fine-tune start (exact
/// weight loading, rejection of a changed file, a wrong key set or shape and an unknown freeze, a frozen encoder that stays bit-for-bit and one that
/// is detected when changed). It does not compare numbers with Stable-Baselines3 internals and says nothing about training quality.
/// </summary>
[Collection("ChildProcess")]
public sealed class RlPortfolioPolicySelfCheckTests
{
    private const string SelfCheckScript = "rl_portfolio_policy_selfcheck.py";
    private static readonly string[] ExpectedChecks =
    {
        "optimizer_covers_policy", "log_std", "orthogonal_init", "learning_signal", "slot_order_equivariance", "masked_slot_isolation",
        "exported_actor_is_policy_mean",
        "transfer_loads_exactly", "transfer_rejects_a_changed_file", "transfer_rejects_a_wrong_key_set_or_shape", "transfer_rejects_an_unknown_freeze",
        "frozen_encoder_stays_and_heads_train", "frozen_encoder_change_is_detected", "unfrozen_encoder_trains",
    };

    [RlPortfolioTrainingSmokeTests.SmokeFact]
    public async Task ThePolicyWiringChecksAllPass()
    {
        string work = Path.Combine(Path.GetTempPath(), "sa_rl_policy_selfcheck_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string config = Path.Combine(work, "config.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["defaults"] = RlContract.ToWireDefaults(), ["ppo_defaults"] = RlContinuousContract.ToWireDefaults(),
                ["portfolio_defaults"] = RlPortfolioContract.ToWireDefaults(), ["transfer_defaults"] = RlPortfolioTransferContract.ToWireDefaults(),
            }, RlFiles.Json), new UTF8Encoding(false));

            var (exitCode, output, errors) = await RlPortfolioPythonScript.RunInDirectoryAsync(work, SelfCheckScript,
                "--training-dir", RlPortfolioPythonScript.TrainingDirectory, "--config", config);

            var results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith('{')).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
            string report = string.Join(Environment.NewLine, results.Select(r => r.GetRawText())) + Environment.NewLine + errors;
            Assert.True(ExpectedChecks.SequenceEqual(results.Select(r => r.GetProperty("check").GetString()!)), "Unexpected or missing checks:" + Environment.NewLine + report);
            Assert.True(results.All(r => r.GetProperty("ok").GetBoolean()) && exitCode == 0, "A policy wiring check failed:" + Environment.NewLine + report);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }
}
