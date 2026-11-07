namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// A fixed 64-bit linear congruential generator for test series: the same seed gives the same noise on every machine and runtime,
/// which pinned values (golden hashes) and hand-computed expectations depend on. Not a statistical random source.
/// </summary>
public sealed class DeterministicNoise
{
    private const ulong Multiplier = 6364136223846793005UL;
    private const ulong Increment = 1442695040888963407UL;
    private const int HighBitsShift = 33;
    private const double HighBitsRange = 1UL << 31;
    private const double Centre = 0.5;

    private ulong _state;

    public DeterministicNoise(ulong seed) => _state = seed;

    /// <summary>The next value, uniformly spread over [-0.5, 0.5).</summary>
    public double NextCentered()
    {
        _state = unchecked(_state * Multiplier + Increment);
        return ((_state >> HighBitsShift) / HighBitsRange) - Centre;
    }
}
