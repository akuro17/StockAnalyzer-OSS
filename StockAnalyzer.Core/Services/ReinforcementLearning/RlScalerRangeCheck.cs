using System;
using System.Collections.Immutable;
using System.IO;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Compares the raw values of an observation window with the minimum and maximum the agent's scaler stored from its training data.
/// A value outside that range is not an error: the model still runs, but its output is an extrapolation. No threshold is invented.
/// </summary>
public static class RlScalerRangeCheck
{
    /// <param name="rawRows">Row-major raw values: one value per scaler channel in each row, at least <c>WindowSize</c> rows (one observation window,
    /// see <see cref="RlObservationBuilder.RawWindow"/>, or the union of several windows, see <see cref="RlObservationBuilder.RawRows"/>).</param>
    /// <returns>One finding per channel with <c>min &lt; TrainedMin</c> or <c>max &gt; TrainedMax</c> over all rows, in channel order.</returns>
    public static ImmutableArray<RlScalerRangeFinding> Find(FixedScaler scaler, ReadOnlySpan<double> rawRows)
    {
        ArgumentNullException.ThrowIfNull(scaler);
        int channels = scaler.Statistics.Length;
        if (rawRows.Length % channels != 0 || rawRows.Length < checked(scaler.WindowSize * channels))
            throw new InvalidDataException("Raw rows do not match the scaler.");
        int rows = rawRows.Length / channels;
        var findings = ImmutableArray.CreateBuilder<RlScalerRangeFinding>();
        for (int channel = 0; channel < channels; channel++)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int row = 0; row < rows; row++)
            {
                double value = rawRows[row * channels + channel];
                if (value < min) min = value;
                if (value > max) max = value;
            }
            var statistic = scaler.Statistics[channel];
            if (min < statistic.Min || max > statistic.Max)
                findings.Add(new RlScalerRangeFinding(scaler.ChannelIdentities[channel], statistic.Min, statistic.Max, min, max));
        }
        return findings.ToImmutable();
    }
}
