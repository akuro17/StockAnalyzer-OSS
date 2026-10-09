using System;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Pure resource arithmetic of a portfolio run. The rollout buffer uses the same rule as the single-symbol continuous target
/// (<see cref="RlTrainingValidation.ContinuousReplayBytes"/>); only the shapes differ. Nothing here allocates a tensor.
/// </summary>
public static class RlPortfolioResources
{
    /// <summary>
    /// Observation length L = symbols * (window * market channels + asset auxiliary channels) + global values. The optional
    /// <paramref name="layout"/> is the observation variant (default v1); every size below follows it.
    /// </summary>
    public static long ObservationLength(int symbols, RlPortfolioObservationLayout? layout = null)
        => (layout ?? RlPortfolioObservationLayout.V1).ObservationLength(symbols);

    /// <summary>Action length: one cash slot plus one slot per symbol.</summary>
    public static long ActionLength(int symbols) => checked(symbols + 1L);

    /// <summary>Bytes of the PPO rollout and sampled-batch buffers for a universe of <paramref name="symbols"/>.</summary>
    public static long RolloutBytes(int symbols, RlPortfolioObservationLayout? layout = null)
        => RlTrainingValidation.ContinuousReplayBytes(ObservationLength(symbols, layout), ActionLength(symbols));

    /// <summary>Worst-case characters of one float32 in a JSON frame: sign, nine significant digits, point, exponent such as E-38, and the separator.</summary>
    private const int JsonCharactersPerFloat = 16;

    /// <summary>Worst-case bytes of the JSON frame that carries one observation (the largest single IPC payload of a step).</summary>
    public static long ObservationFrameBytes(int symbols, RlPortfolioObservationLayout? layout = null)
        => checked(ObservationLength(symbols, layout) * JsonCharactersPerFloat);

    /// <summary>True when the rollout buffer fits both the RL buffer limit and the captured tensor limit and one observation fits one IPC frame.</summary>
    public static bool Fits(int symbols, TrainingResourceLimits limits, RlPortfolioObservationLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        long bound = Math.Min(RlContract.BufferByteLimit, checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB));
        return RolloutBytes(symbols, layout) <= bound && ObservationFrameBytes(symbols, layout) <= RlContract.IpcFrameByteLimit;
    }

    /// <summary>Largest universe that <see cref="Fits"/>; the rule is monotone in the symbol count, so the first failure ends the search.</summary>
    public static int MaximumSymbols(TrainingResourceLimits limits, RlPortfolioObservationLayout? layout = null)
    {
        int symbols = 0;
        while (Fits(symbols + 1, limits, layout)) symbols++;
        return symbols;
    }
}
