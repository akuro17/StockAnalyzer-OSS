using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Training;

public sealed record PreparedTrainingInput(string RunDirectory, string DatasetDirectory,
    string ProviderId, string DatasetRevision, DateTimeOffset AsOfUtc,
    IReadOnlyDictionary<string, PreparedTrainingSymbol> Symbols);

public sealed record PreparedTrainingSymbol(string SourceSha256, string SnapshotPath,
    string PreparedPath, IReadOnlyList<CandleData> Candles);

public sealed record TrainingSourceProvenance(string ProviderId, string DatasetRevision,
    DateTimeOffset AsOfUtc, IReadOnlyDictionary<string, string> Sha256BySymbol);
