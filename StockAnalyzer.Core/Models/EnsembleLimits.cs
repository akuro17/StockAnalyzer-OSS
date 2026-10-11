using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models;

/// <summary>Safety bounds of the ensemble feature (code contract, never configuration).</summary>
public static class EnsembleLimits
{
    /// <summary>
    /// Entries of the process-wide compatibility cache (<see cref="EnsembleCompatibility"/> per generation, a few hundred bytes each); the least recently
    /// used one is evicted beyond it. The group list reads every listed generation, so the bound must exceed the number of listed generations: otherwise
    /// every list rebuild would evict what it just read and open the ONNX files again. It only bounds memory and is not derived from a measurement.
    /// </summary>
    public const int MaxCachedCompatibilities = 1024;
}
