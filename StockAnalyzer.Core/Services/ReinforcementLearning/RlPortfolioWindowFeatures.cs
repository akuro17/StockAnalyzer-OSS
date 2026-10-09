using System;
using System.Collections.Generic;
using System.IO;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The raw (unscaled) market windows of a set of symbols for one decision, including the cross-sectional channels of the v2-a variant. It is the single
/// owner of the feature definition for the observation and for the scaler fit. Every value reads bars at calendar indices up to the decision index only:
/// the one-bar return of the first window bar reads the bar just before the window, which lies at or below the decision index as well.
/// </summary>
internal static class RlPortfolioWindowFeatures
{
    /// <summary>Position of each extra channel after the v1 channels; the order is that of <see cref="RlPortfolioV2Contract.ExtraChannelNames"/>.</summary>
    internal const int RelativeReturnOffset = 0, RankOffset = 1;

    /// <summary>The rank of a symbol is (average rank - this) / count, so that it lies strictly between 0 and 1; the same number is part of the hashed <see cref="RlPortfolioV2Contract.FeatureConventions"/>.</summary>
    internal const double RankCentering = 0.5d;

    /// <summary>The calendar index of the first bar of the block that the decision at <paramref name="anchor"/> belongs to (the first decision is at start + window - 1).</summary>
    public static int BlockStart(int anchor, int completedDecisions) => anchor - completedDecisions - (RlContract.WindowLength - 1);

    /// <summary>
    /// Fills <paramref name="raw"/>[k] (length window x channels of the layout, oldest bar first) for each of <paramref name="symbols"/> whose window is complete and
    /// returns which ones are. The v1 channels come from <see cref="RlPortfolioObservationBuilder.TryFillRawWindow"/>. For the extra channels the set U is exactly
    /// the symbols with a complete window (the same set the observation marks available): per window bar the relative return is the one-bar log return minus the mean
    /// of that return over U (0 when |U| = 1), and the rank is (average rank - 0.5) / |U| of the log close over the window-end close, rank 1 = lowest, ties share the
    /// average rank. A bar whose previous bar lies before <paramref name="blockStart"/> has return 0; a missing bar at or after the block start is a broken block.
    /// </summary>
    public static bool[] Fill(IRlPanelBars bars, RlPortfolioObservationLayout layout, IReadOnlyList<int> symbols, int anchor, int blockStart, double[][] raw)
    {
        int channels = layout.MarketChannelCount, baseChannels = RlContract.MarketChannelCount, window = RlContract.WindowLength;
        var available = new bool[symbols.Count];
        int count = 0;
        for (int k = 0; k < symbols.Count; k++)
            if (available[k] = RlPortfolioObservationBuilder.TryFillRawWindow(bars, symbols[k], anchor, raw[k], channels)) count++;
        if (channels == baseChannels || count == 0) return available;
        if (channels != baseChannels + RlPortfolioV2Contract.ExtraChannelNames.Length)
            throw new InvalidDataException("The observation layout has a channel set that no feature definition covers.");

        int first = anchor - window + 1;
        var returns = new double[symbols.Count];
        var order = new int[count];
        for (int row = 0; row < window; row++)
        {
            int index = first + row, member = 0;
            double sum = 0d;
            for (int k = 0; k < symbols.Count; k++)
            {
                if (!available[k]) continue;
                order[member++] = k;
                returns[k] = index - 1 < blockStart ? 0d : OneBarReturn(bars, symbols[k], index);
                sum += returns[k];
            }
            double mean = sum / count;
            foreach (int k in order)
                raw[k][row * channels + baseChannels + RelativeReturnOffset] = count == 1 ? 0d : returns[k] - mean;
            // Rank of the log close over the window-end close (already computed), lowest first, ties share the average rank.
            int close = row * channels + RlPortfolioObservationBuilder.CloseChannel;
            Array.Sort(order, (x, y) => raw[x][close].CompareTo(raw[y][close]));
            for (int i = 0; i < count;)
            {
                int j = i;
                double value = raw[order[i]][close];
                while (j + 1 < count && raw[order[j + 1]][close] == value) j++;
                double averageRank = (i + j) / 2d + 1d;
                double rank = (averageRank - RankCentering) / count;
                for (int m = i; m <= j; m++) raw[order[m]][row * channels + baseChannels + RankOffset] = rank;
                i = j + 1;
            }
        }
        return available;
    }

    private static double OneBarReturn(IRlPanelBars bars, int symbol, int index)
    {
        if (!bars.TryGetBar(symbol, index, out var bar) || !bars.TryGetBar(symbol, index - 1, out var previous))
            throw new InvalidDataException("A block lacks a bar next to an observation window.");
        if (previous.Close <= 0m) throw new InvalidDataException("A close must be positive.");
        return Math.Log((double)(bar.Close / previous.Close));
    }
}
