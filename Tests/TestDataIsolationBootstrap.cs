using System;
using System.IO;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.TestSupport;

/// <summary>
/// Points the signal-bundle files of <see cref="UserStrategyMetadataRepository"/> at a temporary folder for the whole test run. Tests
/// that save signal bundles through the singleton (the repository test, the ticker dialog, the watchlist item) used to write
/// <c>{ticker}.signals.json</c> into the user's real Data/Metadata, replacing real files such as <c>MSFT.signals.json</c>.
/// This one file is compiled into every test assembly (linked from the test projects), and it is the single place that names the
/// test scratch folders below the OS temp folder.
/// </summary>
internal static class TestDataIsolationBootstrap
{
    internal const string SignalBundlesFolderName = "TestSignalBundles";
    internal const string MetadataFolderName = "TestMetadata";

    /// <summary>A new, unique, not yet created folder <c>%TEMP%/StockAnalyzer/&lt;purpose&gt;/&lt;guid&gt;</c>; never inside the Data folder.</summary>
    internal static string NewTempDirectory(string purpose)
        => Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName, purpose, Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void IsolateSignalBundles()
    {
        string directory = NewTempDirectory(SignalBundlesFolderName);
        Directory.CreateDirectory(directory);
        UserStrategyMetadataRepository.SignalBundlesDirectory = directory;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort: the OS temp cleaner removes the rest */ }
        };
    }
}
