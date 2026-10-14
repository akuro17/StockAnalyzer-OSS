using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Single owner of the 3:1:1 ratio cuts of a series of bars (train | validation | final evaluation).</summary>
internal static class RlPartitionCuts
{
    /// <summary>
    /// The first index of the validation part (<c>floor(3N/5)</c>) and of the final-evaluation part (<c>floor(4N/5)</c>) of <paramref name="length"/> bars.
    /// Train is [0, Cut1), validation is [Cut1, Cut2) and the final evaluation is [Cut2, length); the episode blocks of validation and final
    /// evaluation start <see cref="RlContract.WindowLength"/> bars after their cut so that their first window lies inside the part.
    /// </summary>
    public static (int Cut1, int Cut2) Ratio(int length)
        => (checked((int)((long)RlContract.TrainPartitionNumerator * length / RlContract.PartitionDenominator)),
            checked((int)((long)RlContract.ValidationPartitionNumerator * length / RlContract.PartitionDenominator)));
}
