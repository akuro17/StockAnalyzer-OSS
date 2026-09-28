using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public interface ITrainingSourceSnapshotProvider
{
    Task<PreparedTrainingInput> PrepareAsync(TrainingJobConfig config, string runDirectory,
        DateTimeOffset jobStartedUtc, IProgress<string>? stage = null, CancellationToken ct = default);
}
