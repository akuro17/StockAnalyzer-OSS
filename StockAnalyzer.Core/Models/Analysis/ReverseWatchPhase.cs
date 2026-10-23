namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// The sector of the Reverse Watch plane a curve point lies in (<c>ReverseWatchSectors</c>). Each comment gives the compass sector and
/// where the point lies on the price and volume axes; numbering and geometry only, the phases carry no meaning label.
/// </summary>
public enum ReverseWatchPhase
{
    None,
    Phase1, // South-East: price low, volume high
    Phase2, // East: price middle, volume high
    Phase3, // North-East: price high, volume high
    Phase4, // North: price high, volume middle
    Phase5, // North-West: price high, volume low
    Phase6, // West: price middle, volume low
    Phase7, // South-West: price low, volume low
    Phase8  // South: price low, volume middle
}
