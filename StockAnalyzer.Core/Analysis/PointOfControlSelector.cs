using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Decides which bin of a profile is painted as the Point of Control. One rule for every render site (Volume Profile / Time at Price indicators and drawing tools),
/// so no site repeats the "weight greater than 0" guard. A separate function by design: <see cref="VolumeAnalysis.FindPointOfControlIndex(IReadOnlyList{VolumeBin}?)"/> is unchanged.
/// </summary>
public static class PointOfControlSelector
{
    /// <summary>
    /// Index of the bin to paint as the Point of Control, or -1 when none is to be painted.
    /// The bin is <see cref="VolumeAnalysis.FindPointOfControlIndex(IReadOnlyList{VolumeBin}?)"/> (greatest weight, the lowest index on a tie) provided its weight is greater than 0.
    /// When every bin weighs 0 (or the list is null or empty) the result is -1: that function returns index 0 for an all-zero profile, which must not be coloured as the Point of Control.
    /// One pass, no allocation.
    /// </summary>
    public static int FindDrawableIndex(IReadOnlyList<VolumeBin>? bins)
    {
        int index = VolumeAnalysis.FindPointOfControlIndex(bins);
        return index >= 0 && bins![index].TotalVolume > 0 ? index : -1;
    }
}
