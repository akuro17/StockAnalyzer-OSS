using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Native canonical accountant evidence and paired C# actor order lifecycle on immutable real support.</summary>
internal static partial class RlContinuousParity
{
    public static (int Transitions, int EpisodeStarts) ValidateDirectory(string directory, RlSourceSnapshot source, BacktestConfiguration costs, CancellationToken ct = default)
    {
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        using var actor = new RlQModel(Path.Combine(directory, RlFiles.Model), continuous: true);
        bool versioned = job.RootElement.TryGetProperty("financial_parity_specification_id", out var spec);
        if (versioned != actor.Metadata.ContainsKey("financial_parity_specification_id")) throw new InvalidDataException("Financial parity version downgrade/mismatch.");
        if (!versioned) return ValidateLegacyDirectory(directory, source, costs, ct);
        var work = ValidateFinancialDirectory(directory, source, costs, ct);
        return (work.Transitions, work.EpisodeStarts);
    }

    private static (int Transitions, int EpisodeStarts) ValidateLegacyDirectory(string directory, RlSourceSnapshot source, BacktestConfiguration costs, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Parity)));
        using var actor = new RlQModel(Path.Combine(directory, RlFiles.Model), continuous: true);
        using var direction = new RlQModel(Path.Combine(directory, RlContinuousContract.DirectionModel));
        var episodes = doc.RootElement.GetProperty("paired_episodes");
        if (episodes.GetArrayLength() != source.Catalog.Validation.Length) throw new InvalidDataException("Actor lifecycle parity requires all fixed validation blocks.");
        int index = 0, transitions = 0;
        foreach (var episode in episodes.EnumerateArray())
        {
            var block = source.Catalog.Validation[index++];
            if (episode.GetProperty("block_id").GetString() != block.Id) throw new InvalidDataException("Actor parity block identity/order differs.");
            var engine = new BacktestEngine(IndicatorFactory.Default);
            using var native = engine.CreateRlContinuousSession(source.CreateInput(block), costs);
            using var exported = engine.CreateRlContinuousSession(source.CreateInput(block), costs);
            var expectedState = native.Reset(ct);
            var actualState = exported.Reset(ct);
            int nativeFills = 0, exportedFills = 0;
            decimal? retainedLowerDistance = null, retainedUpperDistance = null;
            foreach (var row in episode.GetProperty("steps").EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (expectedState.Terminated || actualState.Terminated) throw new InvalidDataException("Actor parity includes post-terminal actions.");
                var referenceObservation = row.GetProperty("observation").Deserialize<float[]>() ?? throw new InvalidDataException("Native observation required.");
                var canonicalObservation = RlObservationBuilder.Build(expectedState, direction.Scaler);
                if (!referenceObservation.SequenceEqual(canonicalObservation)) throw new InvalidDataException("Native actor observation differs from canonical Core replay.");
                var raw = row.GetProperty("actions").Deserialize<float[]>() ?? throw new InvalidDataException("Native actor action required.");
                if (raw.Length != RlContinuousContract.ActionCount) throw new InvalidDataException("Native actor shape differs.");
                var actualObservation = RlObservationBuilder.Build(actualState, direction.Scaler);
                var predicted = actor.Infer(actualObservation);
                var expectedDirection = RlContract.SelectAction(direction.Infer(canonicalObservation));
                var actualDirection = RlContract.SelectAction(direction.Infer(actualObservation));
                if (expectedDirection.ToString() != row.GetProperty("direction").GetString() || expectedDirection != actualDirection)
                    throw new InvalidDataException("Frozen direction differs across actor replays.");
                // The same-input ML tolerance remains independent of drift caused by different stop prices.
                var sameInput = actor.Infer(referenceObservation);
                for (int i = 0; i < raw.Length; i++)
                    if (!float.IsFinite(raw[i]) || Math.Abs((double)sameInput[i] - raw[i]) > Tolerance(raw[i]))
                        throw new InvalidDataException("C# account-aware actor output parity failed.");
                var expected = native.Step(expectedDirection, raw[0], raw[1], ct);
                var actual = exported.Step(actualDirection, predicted[0], predicted[1], ct);
                if (expectedState.Quantity == 0m && !expectedState.PendingEntry && expectedDirection == RlAction.Buy && expected.Outcome == RlActionOutcome.Submitted)
                {
                    double tolerance = Tolerance(raw[1]);
                    retainedLowerDistance = RlContinuousContract.Map(raw[0], (float)Math.Clamp(raw[1] - tolerance, RlContinuousContract.ActionMinimum, RlContinuousContract.ActionMaximum)).StopDistance;
                    retainedUpperDistance = RlContinuousContract.Map(raw[0], (float)Math.Clamp(raw[1] + tolerance, RlContinuousContract.ActionMinimum, RlContinuousContract.ActionMaximum)).StopDistance;
                }
                transitions += 2;
                expectedState = expected.Snapshot; actualState = actual.Snapshot;
                var saved = row.GetProperty("account");
                if (Decimal(saved, "cash") != expectedState.EquityPoint.Cash || Decimal(saved, "equity") != expectedState.EquityPoint.Equity
                    || Decimal(saved, "quantity") != expectedState.Quantity || Decimal(saved, "held_margin") != expectedState.EquityPoint.HeldMargin
                    || Decimal(saved, "entry_price") != expectedState.EntryPrice || Decimal(saved, "unrealized_pnl") != expectedState.UnrealizedPnL
                    || row.GetProperty("outcome").GetString() != expected.Outcome.ToString())
                    throw new InvalidDataException("Canonical actor inputs did not reproduce exact decimal account evidence.");
                if (expected.Outcome != actual.Outcome || expected.RejectionReason != actual.RejectionReason || expected.Terminated != actual.Terminated
                    || expectedState.Quantity != actualState.Quantity || expectedState.EntryPrice != actualState.EntryPrice || expectedState.PendingEntry != actualState.PendingEntry
                    || expectedState.PendingExit != actualState.PendingExit || expectedState.DeferredMarketExit != actualState.DeferredMarketExit
                    || expectedState.ActiveStopPrice.HasValue != actualState.ActiveStopPrice.HasValue)
                    throw new InvalidDataException("Actor export changed quantity/admission/stop/deferred/terminal decisions.");
                var referenceFills = native.GetResult().Fills.Skip(nativeFills).ToArray();
                var actualFills = exported.GetResult().Fills.Skip(exportedFills).ToArray();
                var savedFills = row.GetProperty("fills");
                if (referenceFills.Length != actualFills.Length || referenceFills.Length != savedFills.GetArrayLength())
                    throw new InvalidDataException("Actor export changed fill events.");
                for (int fill = 0; fill < referenceFills.Length; fill++)
                {
                    var a = referenceFills[fill]; var b = actualFills[fill]; var s = savedFills[fill];
                    if (a.BarIndex != b.BarIndex || a.Side != b.Side || a.Quantity != b.Quantity
                        || a.BarIndex != s.GetProperty("bar_index").GetInt32() || a.Side.ToString() != s.GetProperty("side").GetString()
                        || a.Price != Decimal(s, "price") || a.Quantity != Decimal(s, "quantity") || a.Commission != Decimal(s, "commission"))
                        throw new InvalidDataException("Actor fill event/canonical finance parity failed.");
                }
                nativeFills += referenceFills.Length; exportedFills += actualFills.Length;
                if (expectedState.ActiveStopPrice.HasValue && actualState.ActiveStopPrice is { } actualStop)
                {
                    if (retainedLowerDistance is null || retainedUpperDistance is null
                        || actualStop < actualState.EntryPrice * (1m - retainedUpperDistance.Value)
                        || actualStop > actualState.EntryPrice * (1m - retainedLowerDistance.Value))
                        throw new InvalidDataException("Actor stop price is outside the mapped ML interval.");
                }
            }
            if (!expectedState.Terminated || !actualState.Terminated) throw new InvalidDataException("Actor parity episode is incomplete.");
        }
        return (transitions, index * 2);
    }
    private static double Tolerance(float reference) => RlContract.AbsoluteTolerance + RlContract.RelativeTolerance * Math.Abs((double)reference);
    private static decimal Decimal(JsonElement row, string name) => decimal.Parse(row.GetProperty(name).GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
