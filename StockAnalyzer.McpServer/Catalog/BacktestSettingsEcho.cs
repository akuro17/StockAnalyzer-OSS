using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// The echo of the account keys that only some calls name. The execution-realism keys (<see cref="McpBacktestSettingsExposure.ExecutionRealismKeys"/>) are written only when the call
/// named them, so a call that names none gives the bytes it gave before the keys existed. One writer for every tool that echoes the account settings.
/// </summary>
internal static class BacktestSettingsEcho
{
    /// <summary>
    /// Writes, into the account object that is currently open, the effective value of each execution-realism key that <paramref name="accountArguments"/> (the call's <c>account</c> object)
    /// named, in the order of <see cref="McpBacktestSettingsExposure.ExecutionRealismKeys"/>. Nothing is written when the call named none.
    /// </summary>
    public static void WriteExecutionRealism(Utf8JsonWriter writer, JsonElement? accountArguments, BacktestConfiguration configuration)
    {
        if (accountArguments is not { ValueKind: JsonValueKind.Object } account)
        {
            return;
        }

        foreach (string key in McpBacktestSettingsExposure.ExecutionRealismKeys)
        {
            if (!account.TryGetProperty(key, out _))
            {
                continue;
            }

            switch (key)
            {
                case McpBacktestSettingsExposure.MaxVolumeParticipationRatioKey:
                    if (configuration.MaxVolumeParticipationRatio is { } ratio)
                    {
                        writer.WriteNumber(key, ratio);
                    }

                    break;
                case McpBacktestSettingsExposure.VolumeConstraintModeKey:
                    writer.WriteString(key, configuration.VolumeConstraintMode.ToString());
                    break;
                case McpBacktestSettingsExposure.ShortBorrowAnnualRateKey:
                    writer.WriteNumber(key, configuration.ShortBorrowAnnualRate);
                    break;
                case McpBacktestSettingsExposure.BorrowFeeBasisModeKey:
                    writer.WriteString(key, configuration.BorrowFeeBasisMode.ToString());
                    break;
                case McpBacktestSettingsExposure.PriceLimitFillModeKey:
                    writer.WriteString(key, configuration.PriceLimitFillMode.ToString());
                    break;
                default:
                    // A key added to ExecutionRealismKeys without a case here would be bound but never echoed: a programming error, never silent.
                    throw new InvalidOperationException($"The execution-realism key '{key}' has no echo.");
            }
        }
    }
}
