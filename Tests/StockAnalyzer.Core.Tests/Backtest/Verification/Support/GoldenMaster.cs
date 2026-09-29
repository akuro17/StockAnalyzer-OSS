using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Golden Master (snapshot) comparison. Golden files live in <c>Assets\BacktestGolden\</c> (copied to the output directory by
/// the test csproj). A missing golden file FAILS - it is never created silently. Regeneration is opt-in only:
/// set environment variable <see cref="UpdateEnvVar"/>=1, run the tests once (they write into the SOURCE tree and still fail
/// with "golden updated"), then REVIEW the JSON against the hand-calculated tables before committing.
/// </summary>
internal static class GoldenMaster
{
    public const string UpdateEnvVar = "STOCKANALYZER_UPDATE_GOLDEN";

    private const string GoldenFolderName = "BacktestGolden";
    private const string AssetsFolderName = "Assets";

    /// <param name="callerFilePath">Auto-filled with the calling test file; used only to locate the source tree in update mode.</param>
    public static void Verify(string scenarioName, BacktestResult result, [CallerFilePath] string callerFilePath = "")
    {
        string actual = BacktestSnapshot.ToCanonicalJson(result);
        string fileName = scenarioName + ".json";

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            // callerFilePath = <Tests>\Backtest\Verification\<File>.cs  ->  <Tests>\Assets\BacktestGolden
            string verificationDir = Path.GetDirectoryName(callerFilePath)!;
            string sourceDir = Path.GetFullPath(Path.Combine(verificationDir, "..", "..", AssetsFolderName, GoldenFolderName));
            Directory.CreateDirectory(sourceDir);
            string target = Path.Combine(sourceDir, fileName);
            File.WriteAllText(target, actual, new UTF8Encoding(false));
            throw new XunitException($"Golden file updated: {target}. Review it against the hand calculation, then rerun WITHOUT {UpdateEnvVar}.");
        }

        string path = Path.Combine(AppContext.BaseDirectory, AssetsFolderName, GoldenFolderName, fileName);
        if (!File.Exists(path))
        {
            throw new XunitException($"Golden file missing: {path}. Generate it once with {UpdateEnvVar}=1, review it, and commit it.");
        }

        string expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (string.Equals(expected.TrimEnd('\n'), actual.TrimEnd('\n'), StringComparison.Ordinal)) return;

        throw new XunitException($"Golden mismatch for '{scenarioName}'.{Environment.NewLine}{DescribeFirstDifference(expected, actual)}");
    }

    /// <summary>Finds the first differing line of two texts (used by golden and determinism failure messages).</summary>
    public static string DescribeFirstDifference(string expected, string actual)
    {
        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        int n = Math.Min(e.Length, a.Length);
        for (int i = 0; i < n; i++)
        {
            if (!string.Equals(e[i], a[i], StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:{Environment.NewLine}  expected: {e[i].Trim()}{Environment.NewLine}  actual:   {a[i].Trim()}";
            }
        }
        return $"Texts share a common prefix but differ in length (expected {e.Length} lines, actual {a.Length} lines).";
    }
}
