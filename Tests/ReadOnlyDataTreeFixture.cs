using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.TestSupport;

/// <summary>
/// A complete, throw-away data tree (daily files, empty weekly/monthly/metadata folders, ticker list) under the OS temp folder,
/// addressed through absolute <see cref="MarketDataSettings"/> paths, so read-only tests never touch the user's Data folder.
/// This one file is compiled into every test assembly that needs it (linked from the test projects).
/// </summary>
internal sealed class ReadOnlyDataTreeFixture : IDisposable
{
    internal const string FixtureFolderName = "ReadOnlyDataTree";

    private ReadOnlyDataTreeFixture(string root, MarketDataSettings settings)
    {
        Root = root;
        Settings = settings;
    }

    internal string Root { get; }

    internal MarketDataSettings Settings { get; }

    internal string DailyDirectory => Settings.DailyDataPath!;

    internal string MetadataDirectory => Settings.MetadataPath!;

    internal string TickerListPath => Settings.TickerListPath!;

    /// <summary>Creates the tree; every name in <paramref name="tickerListEntries"/> is written to the ticker list as given and gets a daily file named with its canonical form.</summary>
    internal static ReadOnlyDataTreeFixture Create(int days = 20, params string[] tickerListEntries)
    {
        string root = TestDataIsolationBootstrap.NewTempDirectory(FixtureFolderName);
        var settings = new MarketDataSettings
        {
            DailyDataPath = Path.Combine(root, "Daily"),
            WeeklyDataPath = Path.Combine(root, "Weekly"),
            MonthlyDataPath = Path.Combine(root, "Monthly"),
            MetadataPath = Path.Combine(root, "Metadata"),
            TickerListPath = Path.Combine(root, "tickers.json"),
        };

        foreach (string directory in new[] { settings.DailyDataPath, settings.WeeklyDataPath, settings.MonthlyDataPath, settings.MetadataPath })
        {
            Directory.CreateDirectory(directory);
        }

        foreach (string entry in tickerListEntries)
        {
            ParquetFixtureBuilder.SeedDailyOhlcv(settings.DailyDataPath, TickerSymbol.Canonicalize(entry), days);
        }

        File.WriteAllText(settings.TickerListPath, JsonSerializer.Serialize(tickerListEntries));
        return new ReadOnlyDataTreeFixture(root, settings);
    }

    /// <summary>Writes stored metadata the way the application does (setup only; the code under test never calls this).</summary>
    internal async Task SeedMetadataAsync(string canonicalSymbol, TickerMetadata metadata)
    {
        using var db = new DuckDBConnectionManager();
        var provider = new ParquetMarketDataProvider(db, UnavailablePythonService.Instance, Options.Create(Settings));
        await provider.SaveMetadataAsync(canonicalSymbol, metadata);
    }

    /// <summary>Every directory and file below <see cref="Root"/> as {relative path, size, last write time, SHA-256}, sorted.</summary>
    internal IReadOnlyList<string> Snapshot() => SnapshotOf(Root);

    internal static IReadOnlyList<string> SnapshotOf(string root)
    {
        if (!Directory.Exists(root))
        {
            return new[] { "<missing>" };
        }

        var entries = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path);
            if (Directory.Exists(path))
            {
                entries.Add($"D|{relative}");
                continue;
            }

            var info = new FileInfo(path);
            using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            entries.Add($"F|{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{hash}");
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort: the OS temp cleaner removes the rest */ }
    }
}
