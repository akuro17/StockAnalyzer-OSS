namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Facts emitted by the bootstrap calculation that actually ran; no values are reconstructed later.</summary>
public sealed class BootstrapDiagnostics
{
    /// <summary>Single owner of the bootstrap method version reported by every diagnostic branch.</summary>
    public const int CurrentMethodVersion = 1;

    public const string PartialRunReason = "NotComputedPartial";
    public const string LegacyEntryPointReason = "LegacyEntryPoint";

    public int MethodVersion { get; }
    public int? EffectiveBlockLength { get; }
    public int? ValidReplicates { get; }
    public int? ExecutedReplicates { get; }
    public bool DegenerateConstant { get; }
    public string? Reason { get; }

    internal BootstrapDiagnostics(
        int methodVersion,
        int? effectiveBlockLength,
        int? validReplicates,
        int? executedReplicates,
        bool degenerateConstant,
        string? reason)
    {
        MethodVersion = methodVersion;
        EffectiveBlockLength = effectiveBlockLength;
        ValidReplicates = validReplicates;
        ExecutedReplicates = executedReplicates;
        DegenerateConstant = degenerateConstant;
        Reason = reason;
    }

    internal static BootstrapDiagnostics NotComputed(string reason) =>
        new(CurrentMethodVersion, null, null, 0, false, reason);
}
