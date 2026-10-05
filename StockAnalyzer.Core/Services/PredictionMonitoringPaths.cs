using System.IO;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services;

/// <summary>The one derivation of the prediction monitoring runtime folders (Data/PredictionMonitoring and its attestation working copies).</summary>
internal static class PredictionMonitoringPaths
{
    private const string AttestedCopyFolderName = "attested";

    public static string DefaultRoot() => Path.Combine(PathDiscovery.ResolveDataPath(null, "Data"), PredictionMonitoringSettings.DataFolderName);

    public static string AttestedCopyRoot(string monitoringRoot) => Path.Combine(monitoringRoot, AttestedCopyFolderName);
}
