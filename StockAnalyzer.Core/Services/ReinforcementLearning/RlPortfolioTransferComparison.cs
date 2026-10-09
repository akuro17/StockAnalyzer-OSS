using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Whether a sign-test value may be read at all. <see cref="ReferenceOnly"/> still means "a reference value, not evidence": the blocks of a cell share the
/// market path and are not independent. <see cref="Unavailable"/> means too few effective pairs for any p-value to reach the reference level.
/// </summary>
public enum RlPortfolioComparisonInference { Unavailable = 0, ReferenceOnly = 1 }

/// <summary>
/// Paired difference of two evaluation reports of one cell: <c>PairedDelta</c> is the distribution of <c>A.PolicyReturn - B.PolicyReturn</c> over the
/// blocks both reports evaluated; it is null (and <see cref="Comparable"/> false) when the cell was evaluable in only one report or in neither.
/// <para><c>Wins</c> / <c>Ties</c> / <c>Losses</c> count blocks where A is above / equal to / below B (exact decimal comparison); <c>NEff</c> = Wins + Losses
/// (a tie is no pair). <c>OneSidedP</c> is the exact sign-test probability of at least that many wins if A and B were equally likely to win a block (null with no
/// effective pair); <c>MinAttainableP</c> is the smallest two-sided p-value <c>NEff</c> pairs can give; <c>Inference</c> is <see cref="RlPortfolioComparisonInference.Unavailable"/>
/// below <see cref="RlPortfolioSignTest.MinimumEffectivePairs"/>. <c>SelectionContaminated</c> marks the validation cell of the training symbols, which chose
/// the checkpoint of a trained run, so a difference there is optimistic for that run.</para>
/// </summary>
public sealed record RlPortfolioCellComparison(string Partition, RlPanelSymbolGroup Group, bool Comparable, int BlockCount, RlReturnDistribution? PairedDelta,
    int Wins = 0, int Ties = 0, int Losses = 0, int NEff = 0, double? OneSidedP = null, double? MinAttainableP = null,
    RlPortfolioComparisonInference Inference = RlPortfolioComparisonInference.Unavailable, bool SelectionContaminated = false);

/// <summary>The cells of a comparison in report order, with the mandatory limitation statement.</summary>
/// <param name="AcrossModelContracts">True when the two reports come from models of different observation layouts (an ablation of the observation); the difference then
/// reflects the layout and everything else that differs, not a single cause.</param>
public sealed record RlPortfolioTransferComparisonResult(ImmutableArray<RlPortfolioCellComparison> Cells, string Statement, bool AcrossModelContracts = false);

/// <summary>
/// Pure comparison of two portfolio evaluation reports on the same panel (for example a fine-tuned run against a from-scratch run, or a zero-shot
/// evaluation against either). It needs only the two reports: both must come from the same universe, calendar, costs (compared as numbers, so 0.1 and 0.10
/// agree), weight cap, numerical contract, evaluation mode and specification, list the same cells and, in a cell both evaluated, the same blocks in the same
/// order. Any mismatch is an <see cref="InvalidDataException"/> that names the field; nothing is paired across different blocks. Decimal arithmetic only.
/// A cell that only one report evaluated is stated as not comparable, never dropped. The sign test is a reference value only (see <see cref="RlPortfolioSignTest"/>).
/// </summary>
public static class RlPortfolioTransferComparison
{
    /// <param name="acrossModelContracts">Allows two reports whose numerical contract hashes differ, but only when both name a known observation layout; the default keeps the strict rule.</param>
    public static RlPortfolioTransferComparisonResult Compare(RlPortfolioEvaluationReport a, RlPortfolioEvaluationReport b, bool acrossModelContracts = false)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static InvalidDataException Mismatch(string field) => new($"{RlPortfolioTransferContract.ErrorPrefix} the reports differ in {field}.");
        if (a.SpecificationId != b.SpecificationId) throw Mismatch("the specification id");
        bool differentContracts = a.PortfolioContractHash != b.PortfolioContractHash;
        if (differentContracts && !(acrossModelContracts && RlPortfolioObservationLayout.TryFromContractHash(a.PortfolioContractHash, out _)
            && RlPortfolioObservationLayout.TryFromContractHash(b.PortfolioContractHash, out _)))
            throw Mismatch("the numerical contract hash");
        if (a.EvaluationSpecificationId != b.EvaluationSpecificationId) throw Mismatch("the evaluation mode");
        if (a.UniverseSha256 != b.UniverseSha256) throw Mismatch("the universe hash");
        if (a.CalendarSha256 != b.CalendarSha256) throw Mismatch("the calendar hash");
        if (a.Costs is null || b.Costs is null || !SameNumbers(JsonSerializer.Serialize(a.Costs, RlFiles.Json), JsonSerializer.Serialize(b.Costs, RlFiles.Json))) throw Mismatch("the costs");
        if (a.MaxWeightPercent != b.MaxWeightPercent) throw Mismatch("the weight cap");
        if (a.Cells.IsDefault || b.Cells.IsDefault) throw Mismatch("the cell list");
        if (!a.Cells.Select(c => (c.Partition, c.Group)).SequenceEqual(b.Cells.Select(c => (c.Partition, c.Group)))) throw Mismatch("the cell list");

        var cells = ImmutableArray.CreateBuilder<RlPortfolioCellComparison>(a.Cells.Length);
        for (int i = 0; i < a.Cells.Length; i++)
        {
            var left = a.Cells[i];
            var right = b.Cells[i];
            if (!left.Evaluable || !right.Evaluable || left.Blocks.IsDefault || right.Blocks.IsDefault)
            {
                cells.Add(new RlPortfolioCellComparison(left.Partition, left.Group, false, 0, null));
                continue;
            }
            if (!left.Blocks.Select(x => x.BlockId).SequenceEqual(right.Blocks.Select(x => x.BlockId), StringComparer.Ordinal))
                throw Mismatch($"the blocks of cell {left.Partition}/{left.Group}");
            var deltas = left.Blocks.Zip(right.Blocks, (x, y) => x.PolicyReturn - y.PolicyReturn).ToArray();
            int wins = deltas.Count(d => d > 0m), losses = deltas.Count(d => d < 0m), effective = wins + losses;
            cells.Add(new RlPortfolioCellComparison(left.Partition, left.Group, true, deltas.Length, RlAllBlockEvaluation.Distribution(deltas),
                wins, deltas.Length - effective, losses, effective,
                effective > 0 ? RlPortfolioSignTest.OneSidedP(wins, effective) : null,
                effective > 0 ? RlPortfolioSignTest.MinimumAttainableTwoSidedP(effective) : null,
                effective >= RlPortfolioSignTest.MinimumEffectivePairs ? RlPortfolioComparisonInference.ReferenceOnly : RlPortfolioComparisonInference.Unavailable,
                RlPortfolioSelectionCell.Is(left.Partition, left.Group)));
        }
        return new RlPortfolioTransferComparisonResult(cells.MoveToImmutable(), RlPortfolioTransferContract.Statement, differentContracts);
    }

    /// <summary>Two JSON texts that agree in structure, strings and booleans, and whose numbers are equal as decimals (a double fallback for a number a decimal cannot hold).</summary>
    private static bool SameNumbers(string left, string right)
    {
        using var x = JsonDocument.Parse(left);
        using var y = JsonDocument.Parse(right);
        return Same(x.RootElement, y.RootElement);

        static bool Same(JsonElement p, JsonElement q)
        {
            if (p.ValueKind != q.ValueKind) return false;
            switch (p.ValueKind)
            {
                case JsonValueKind.Object:
                    var first = p.EnumerateObject().ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);
                    var second = q.EnumerateObject().ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);
                    return first.Count == second.Count && first.All(o => second.TryGetValue(o.Key, out var other) && Same(o.Value, other));
                case JsonValueKind.Array:
                    return p.GetArrayLength() == q.GetArrayLength() && p.EnumerateArray().Zip(q.EnumerateArray(), Same).All(equal => equal);
                case JsonValueKind.Number:
                    return p.TryGetDecimal(out decimal dp) && q.TryGetDecimal(out decimal dq) ? dp == dq : p.GetDouble() == q.GetDouble();
                case JsonValueKind.String:
                    return string.Equals(p.GetString(), q.GetString(), StringComparison.Ordinal);
                default:
                    return true;                                                // two nulls, two trues, two falses
            }
        }
    }
}
