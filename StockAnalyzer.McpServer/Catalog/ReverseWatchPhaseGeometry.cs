using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Where in the Reverse Watch window a point of a given phase lies: the volume average (horizontal axis) and the price average (vertical axis)
/// relative to the centre of the last dataCount points. It is the inverse of the sector-to-phase assignment in
/// <c>ReverseWatchSectors</c> (East = volume high, price middle; counter-clockwise in 45-degree steps) and states geometry only:
/// the application's descriptions of what a phase means contradict each other, so no meaning is attached. A test pins this table to the service.
/// </summary>
internal static class ReverseWatchPhaseGeometry
{
    internal const string High = "High";
    internal const string Middle = "Middle";
    internal const string Low = "Low";

    private static readonly Dictionary<ReverseWatchPhase, (string Price, string Volume)> Positions = new()
    {
        [ReverseWatchPhase.Phase1] = (Low, High),       // south-east
        [ReverseWatchPhase.Phase2] = (Middle, High),    // east
        [ReverseWatchPhase.Phase3] = (High, High),      // north-east
        [ReverseWatchPhase.Phase4] = (High, Middle),    // north
        [ReverseWatchPhase.Phase5] = (High, Low),       // north-west
        [ReverseWatchPhase.Phase6] = (Middle, Low),     // west
        [ReverseWatchPhase.Phase7] = (Low, Low),        // south-west
        [ReverseWatchPhase.Phase8] = (Low, Middle),     // south
    };

    /// <summary>False for <see cref="ReverseWatchPhase.None"/> (a point outside the phase window).</summary>
    internal static bool TryGet(ReverseWatchPhase phase, out string price, out string volume)
    {
        if (Positions.TryGetValue(phase, out (string Price, string Volume) position))
        {
            price = position.Price;
            volume = position.Volume;
            return true;
        }

        price = volume = string.Empty;
        return false;
    }
}
