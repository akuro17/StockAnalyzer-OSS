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
}
