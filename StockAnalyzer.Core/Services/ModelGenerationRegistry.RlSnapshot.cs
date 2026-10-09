using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Services;

public sealed partial class ModelGenerationRegistry
{
    public RlRegistryCapabilities Capabilities => new(_artifactSettings is not null, true, _artifactSettings is not null);

    public RlRegistrySnapshot CaptureRlSnapshot(CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var transaction = EnterStore(ct);
            EnsureRlLoaded();
            var generations = _rlKnown.Values.Where(g => VerifyRlBytes(g)).OrderByDescending(g => g.CreatedUtc)
                .ThenBy(g => g.ModelId, System.StringComparer.Ordinal).Select(CloneRl).ToImmutableArray();
            return new(generations, _rlPointerError is null ? _rlPointer.ActiveId : null,
                _rlContinuousPointerError is null ? _rlContinuousPointer.ActiveId : null,
                _rlPointerError is not null, _rlContinuousPointerError is not null) { Capabilities = Capabilities };
        }
    }
}
