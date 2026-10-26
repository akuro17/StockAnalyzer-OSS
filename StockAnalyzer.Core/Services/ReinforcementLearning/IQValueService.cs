using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public interface IQValueService
{
    Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replay on the stored bars of any ticker and date. Optional: an implementation without it keeps compiling and reports it as unsupported.</summary>
    Task<RlExternalReplayResult> ReplayExternalAsync(RlExternalReplayRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This Q service does not replay on another ticker or date.");

    /// <summary>
    /// Replay on the stored bars using an ephemeral uncommitted ONNX policy model located in the OS temporary sandbox.
    /// Optional: an implementation without it keeps compiling and reports it as unsupported.
    /// </summary>
    Task<RlExternalReplayResult> ReplayEphemeralAsync(
        string onnxModelPath,
        string symbol,
        DateOnly asOfDate,
        int decisionPosition,
        string? policyId = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This Q service does not support ephemeral model replay.");
}
