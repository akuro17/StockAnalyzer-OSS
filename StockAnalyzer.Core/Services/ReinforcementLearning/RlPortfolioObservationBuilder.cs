using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The only decimal-to-ML boundary of the portfolio observation (S7); never used for financial calculations. Layout of the float32 row of
/// length <see cref="RlPortfolioResources.ObservationLength"/>: one block per asset slot in canonical symbol order, each holding the scaled
/// market window (oldest bar first, five channels per bar) followed by the slot's current weight and availability mask, then the four global
/// values. Only bars at calendar indices up to the decision index are read, and the bars of a slot that is masked or padded are never read.
/// </summary>
public static class RlPortfolioObservationBuilder
{
    /// <summary>Values of one asset slot: the market window and the two auxiliary channels.</summary>
    public static int SlotLength => RlContract.WindowLength * RlContract.MarketChannelCount + RlPortfolioContract.AssetAuxiliaryChannelCount;

    /// <param name="maxSymbols">Number of asset slots of the model (N_max); slots beyond the panel's symbols are padding and stay exactly zero.</param>
    /// <param name="tradable">Panel symbol indices the policy may hold in the current phase. A tradable symbol is available (mask 1) only if it
    /// also has a bar on every index of the window ending at the decision index, so the mask uses past bars only.</param>
    public static float[] Build(IRlPanelBars bars, int maxSymbols, RlPortfolioSnapshot account, ImmutableArray<int> tradable, FixedScaler scaler)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(scaler);
        if (maxSymbols < RlPortfolioContract.MinimumSymbols || bars.SymbolCount > maxSymbols)
            throw new ArgumentException("The model must have at least two slots and one slot per panel symbol.", nameof(maxSymbols));
        if (tradable.IsDefault || tradable.Any(i => i < 0 || i >= bars.SymbolCount))
            throw new ArgumentException("Tradable symbols must be valid panel symbol indices.", nameof(tradable));
        if (account.Weights.Length != bars.SymbolCount)
            throw new ArgumentException("The account snapshot must hold one weight per panel symbol.", nameof(account));
        if (scaler.WindowSize != RlContract.WindowLength || !RlPortfolioObservationLayout.TryFromContractHash(scaler.FeatureContractHash, out var layout)
            || scaler.Statistics.Length != layout.MarketChannelCount)
            throw new InvalidDataException("The portfolio scaler must cover the 64-bar window of a known observation layout.");
        int window = RlContract.WindowLength * layout.MarketChannelCount, slotLength = layout.SlotLength;
        var observation = new float[checked((int)RlPortfolioResources.ObservationLength(maxSymbols, layout))];
        // The v1 layout needs one symbol at a time; the cross-sectional channels of v2-a need the whole set of available symbols of the decision.
        bool crossSectional = layout.MarketChannelCount != RlContract.MarketChannelCount;
        var raw = new double[crossSectional ? 0 : window];
        double[][]? windows = null;
        bool[] available = Array.Empty<bool>();
        if (crossSectional)
        {
            windows = new double[tradable.Length][];
            for (int i = 0; i < windows.Length; i++) windows[i] = new double[window];
            available = RlPortfolioWindowFeatures.Fill(bars, layout, tradable, account.AnchorIndex,
                RlPortfolioWindowFeatures.BlockStart(account.AnchorIndex, account.CompletedDecisions), windows);
        }
        for (int i = 0; i < tradable.Length; i++)
        {
            int symbol = tradable[i];
            double[] source = raw;
            if (windows is null) { if (!TryFillRawWindow(bars, symbol, account.AnchorIndex, raw)) continue; }
            else if (available[i]) source = windows[i];
            else continue;
            int start = symbol * slotLength;
            scaler.Transform(source, observation.AsSpan(start, window));
            observation[start + window] = Finite((float)account.Weights[symbol]);
            observation[start + window + 1] = 1f;
        }
        int global = maxSymbols * slotLength;
        observation[global] = Finite((float)account.CashWeight);
        observation[global + 1] = Finite((float)account.Drawdown);
        observation[global + 2] = Finite((float)((decimal)account.RemainingDecisions / RlContract.EpisodeHorizon));
        observation[global + 3] = Finite((float)account.PreviousTurnover);
        return observation;
    }

    /// <summary>The padded or masked slot flags that match <see cref="Build"/>: true where the slot is a tradable panel symbol.</summary>
    public static bool[] SlotMask(int symbolCount, int maxSymbols, ImmutableArray<int> tradable)
    {
        var mask = new bool[maxSymbols];
        foreach (int symbol in tradable)
            if (symbol >= 0 && symbol < symbolCount && symbol < maxSymbols) mask[symbol] = true;
        return mask;
    }

    /// <summary>The position of <c>ln(close/close_t)</c> among the market channels of a raw window (the index of "Close" in <see cref="RlContract.MarketChannelNames"/>).</summary>
    internal const int CloseChannel = 3;

    /// <summary>
    /// Fills the raw (unscaled) window ending at <paramref name="anchor"/> for one symbol: per bar <c>ln(open/close_t)</c>, <c>ln(high/close_t)</c>,
    /// <c>ln(low/close_t)</c>, <c>ln(close/close_t)</c> and <c>ln(1+volume)</c>. Returns false when any bar of the window is absent. Single owner
    /// of the feature definition for the observation and for the scaler fit.
    /// </summary>
    internal static bool TryFillRawWindow(IRlPanelBars bars, int symbol, int anchor, Span<double> raw, int channels = RlContract.MarketChannelCount)
    {
        int length = RlContract.WindowLength;
        if (anchor - length + 1 < 0 || !bars.TryGetBar(symbol, anchor, out var last)) return false;
        decimal reference = last.Close;
        if (reference <= 0m) throw new InvalidDataException("A window-end close must be positive.");
        for (int row = 0; row < length; row++)
        {
            if (!bars.TryGetBar(symbol, anchor - length + 1 + row, out var bar)) return false;
            int offset = row * channels;
            raw[offset] = Math.Log((double)(bar.Open / reference));
            raw[offset + 1] = Math.Log((double)(bar.High / reference));
            raw[offset + 2] = Math.Log((double)(bar.Low / reference));
            raw[offset + CloseChannel] = Math.Log((double)(bar.Close / reference));
            raw[offset + 4] = Math.Log(1d + bar.Volume);
        }
        return true;
    }

    private static float Finite(float value)
        => float.IsFinite(value) ? value : throw new InvalidDataException("Account ML value is not finite.");
}
