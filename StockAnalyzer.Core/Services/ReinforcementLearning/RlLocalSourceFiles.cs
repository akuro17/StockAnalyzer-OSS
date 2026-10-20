using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Copies existing approved local bytes only. It never downloads, updates or repairs data.</summary>
internal static class RlLocalSourceFiles
{
    internal sealed record Snapshot(string SourceIdentity, string MappedSymbol, string SourceSha256,
        string MetadataIdentity, string MetadataSha256);

    public static async Task<Snapshot> CaptureAsync(string symbol, string destination,
        TrainingResourceLimits limits, CancellationToken ct)
    {
        string daily = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultDailyDataPath));
        string metadata = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath));
        string source = ParquetMarketDataProvider.ResolveFilePath(daily, symbol)
            ?? throw new FileNotFoundException("Unavailable: no existing Data/Daily Parquet matches the requested symbol.");
        string mapped = Path.GetFileNameWithoutExtension(source);
        // Metadata is reference information, never an additional time-series input.
        string metadataFile = Path.Combine(metadata, mapped + RlLocalParquetReader.MetadataFileSuffix);
        if (!File.Exists(metadataFile)) throw new FileNotFoundException("Unavailable: existing local symbol metadata is required.", metadataFile);
        Directory.CreateDirectory(destination);
        long bound = Math.Min(RlContract.BufferByteLimit, checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB));
        string sourceHash = await CopyAsync(daily, source, Path.Combine(destination, RlFiles.RawParquet), bound, ct).ConfigureAwait(false);
        string metadataHash = await CopyAsync(metadata, metadataFile, Path.Combine(destination, RlFiles.MetadataParquet), bound, ct).ConfigureAwait(false);
        return new(Path.GetRelativePath(Directory.GetParent(daily)!.FullName, Path.GetFullPath(source)).Replace('\\', '/'),
            mapped, sourceHash, Path.GetRelativePath(Directory.GetParent(metadata)!.FullName, metadataFile).Replace('\\', '/'), metadataHash);
    }

    internal static void ValidatePath(string approvedFolder, string path)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedFolder));
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Local source escapes its approved Data folder.");
        // Include Data itself: redirecting an allowed child folder cannot authorize an external source.
        string boundary = Directory.GetParent(folder)!.FullName;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked local source paths are prohibited.");
            if (string.Equals(current, boundary, comparison)) return;
        }
        throw new InvalidDataException("Unable to validate the local Data path boundary.");
    }

    private static async Task<string> CopyAsync(string folder, string source, string destination, long bound, CancellationToken ct)
    {
        ValidatePath(folder, source);
        await using var input = new FileStream(source, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read,
            Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });
        ValidatePath(folder, source);
        if (input.Length <= 0 || input.Length > bound) throw new InvalidDataException("Local Parquet exceeds the captured snapshot byte bound.");
        string before = ContentHash.OfFile(source);
        await using (var output = new FileStream(destination, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.SequentialScan }))
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        string hash = ContentHash.OfFile(destination);
        if (hash != before || hash != ContentHash.OfFile(source)) throw new InvalidDataException("Local source changed while its immutable snapshot was captured.");
        return hash;
    }
}
