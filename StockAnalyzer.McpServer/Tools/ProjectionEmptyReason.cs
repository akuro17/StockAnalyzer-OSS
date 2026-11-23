using System.Collections.Generic;
using System.Text.Json;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Maps empty <c>sa_project_price_path</c> results to machine-readable <c>reasonCode</c> and <c>reason</c> values.
/// Thresholds reference the Core projection engines directly without duplicating numeric literals.
/// </summary>
internal static class ProjectionEmptyReason
{
    public const string InsufficientBarsCode = "INSUFFICIENT_BARS";
    public const string ProjectionUnavailableCode = "PROJECTION_UNAVAILABLE";

    private static readonly IReadOnlyDictionary<string, string> ReasonMessages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [InsufficientBarsCode] = "Loaded bar count is below the minimum sample count required by the selected projection method.",
        [ProjectionUnavailableCode] = "The projection engine could not estimate a trajectory from the loaded bars.",
    };

    public static string Classify(string canonicalMethod, int loadedBarCount, int arimaD)
    {
        bool insufficientBars = canonicalMethod switch
        {
            "Arima" => loadedBarCount <= arimaD,
            "Fft" => loadedBarCount < FftProjectionAnalysis.MinSampleCount,
            "Ssa" => loadedBarCount < SsaProjectionAnalysis.MinSampleCount,
            "Hmm" => loadedBarCount < HmmProjectionAnalysis.MinSampleCount,
            _ => false,
        };

        return insufficientBars ? InsufficientBarsCode : ProjectionUnavailableCode;
    }

    public static string GetReason(string reasonCode) => ReasonMessages[reasonCode];

    public static void WriteIfEmpty(
        Utf8JsonWriter writer,
        bool isEmpty,
        string canonicalMethod,
        int loadedBarCount,
        int arimaD)
    {
        if (!isEmpty)
        {
            return;
        }

        string code = Classify(canonicalMethod, loadedBarCount, arimaD);
        writer.WriteString("reasonCode", code);
        writer.WriteString("reason", GetReason(code));
    }
}
