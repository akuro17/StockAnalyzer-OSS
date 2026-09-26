using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;

/// <summary>
/// Owns the complete strict run input. Validation deliberately does not turn array order into market
/// order; sequence-dependent decisions are made by the engine only from provider sequence evidence.
/// </summary>
public sealed class StrictBacktestInput
{
    public ImmutableArray<StrictMarketEvent> Events { get; }
    public StrictEvidenceProviderContract ProviderContract { get; }
    public DateTime RunStartUtc { get; }
    public DateTime TradingStartUtc { get; }
    public bool RequiresCorporateActions { get; }
    public bool RequiresExternalCashTransfers { get; }
    public bool RequiresBorrowing { get; }
    public ImmutableArray<StrictRunIssue> ValidationIssues { get; }

    public StrictBacktestInput(
        IEnumerable<StrictMarketEvent> events,
        StrictEvidenceProviderContract providerContract,
        DateTime runStartUtc,
        DateTime tradingStartUtc,
        bool requiresCorporateActions = false,
        bool requiresExternalCashTransfers = false,
        bool requiresBorrowing = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        ProviderContract = providerContract ?? throw new ArgumentNullException(nameof(providerContract));
        var issues = ImmutableArray.CreateBuilder<StrictRunIssue>();
        if (runStartUtc.Kind != DateTimeKind.Utc) AddIssue(issues, "RunStartUtc must have DateTimeKind.Utc.");
        if (tradingStartUtc.Kind != DateTimeKind.Utc) AddIssue(issues, "TradingStartUtc must have DateTimeKind.Utc.");
        if (tradingStartUtc < runStartUtc) AddIssue(issues, "TradingStartUtc must be >= RunStartUtc.");

        int initialCapacity = providerContract.MaxEventCount >= 4095
            ? 4096
            : providerContract.MaxEventCount + 1;
        var buffer = new List<StrictMarketEvent>(initialCapacity);
        using IEnumerator<StrictMarketEvent> enumerator = events.GetEnumerator();
        while (buffer.Count <= providerContract.MaxEventCount && enumerator.MoveNext())
        {
            buffer.Add(enumerator.Current);
        }
        if (buffer.Count > providerContract.MaxEventCount)
        {
            AddIssue(issues, $"Event count exceeds provider maximum {providerContract.MaxEventCount}.");
        }

        ImmutableArray<StrictMarketEvent> owned = ImmutableArray.CreateRange(buffer);
        ValidateEvents(owned, providerContract, issues);
        Events = owned;
        RunStartUtc = runStartUtc;
        TradingStartUtc = tradingStartUtc;
        RequiresCorporateActions = requiresCorporateActions;
        RequiresExternalCashTransfers = requiresExternalCashTransfers;
        RequiresBorrowing = requiresBorrowing;
        ValidationIssues = issues.ToImmutable();
    }

    private static void ValidateEvents(
        ImmutableArray<StrictMarketEvent> events,
        StrictEvidenceProviderContract contract,
        ImmutableArray<StrictRunIssue>.Builder issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<(string SourceId, DateTime EventTimeUtc, long Sequence)>();
        var liquidityRecords = new Dictionary<string, LiquidityRecordIdentity>(StringComparer.Ordinal);

        for (int i = 0; i < events.Length; i++)
        {
            StrictMarketEvent? item = events[i];
            if (item is null)
            {
                throw new ArgumentException($"Events[{i}] must not be null.", nameof(events));
            }
            if (!string.Equals(item.SourceId, contract.ProviderId, StringComparison.Ordinal)) AddIssue(issues, $"Events[{i}].SourceId does not match ProviderId.", item.EventId);
            if (string.IsNullOrWhiteSpace(item.DataVersion)) AddIssue(issues, $"Events[{i}].DataVersion must not be blank.", item.EventId);
            if (string.IsNullOrWhiteSpace(item.EventId)) AddIssue(issues, $"Events[{i}].EventId must not be blank.");
            else if (!ids.Add(item.EventId)) AddIssue(issues, $"Duplicate EventId '{item.EventId}'.", item.EventId);
            if (string.IsNullOrWhiteSpace(item.CoverageId)) AddIssue(issues, $"Events[{i}].CoverageId must not be blank.", item.EventId);
            if (item.EventTimeUtc.Kind != DateTimeKind.Utc || item.AvailableTimeUtc.Kind != DateTimeKind.Utc)
            {
                AddIssue(issues, $"Events[{i}] clocks must have DateTimeKind.Utc.", item.EventId);
            }
            if (item.AvailableTimeUtc < item.EventTimeUtc) AddIssue(issues, $"Events[{i}].AvailableTimeUtc must be >= EventTimeUtc.", item.EventId);
            if (item.KnowledgeTimeUtc is { } knowledgeTime && knowledgeTime.Kind != DateTimeKind.Utc)
            {
                AddIssue(issues, $"Events[{i}].KnowledgeTimeUtc must have DateTimeKind.Utc.", item.EventId);
            }
            if (item.Price <= 0m) AddIssue(issues, $"Events[{i}].Price must be > 0.", item.EventId);
            if (item.Quantity < 0L) AddIssue(issues, $"Events[{i}].Quantity must be >= 0.", item.EventId);
            if (!Enum.IsDefined(item.Kind)) AddIssue(issues, $"Events[{i}].Kind is undefined.", item.EventId);
            if (item.ExecutableOrderSide is { } side && !Enum.IsDefined(side)) AddIssue(issues, $"Events[{i}].ExecutableOrderSide is undefined.", item.EventId);
            if (item.SourceSequence is { } sequence && !sequences.Add((item.SourceId, item.EventTimeUtc, sequence)))
            {
                AddIssue(issues, $"Duplicate SourceSequence {sequence} at {item.EventTimeUtc:O}.", item.EventId);
            }
            if (string.Equals(item.RevisionOf, item.EventId, StringComparison.Ordinal)) AddIssue(issues, $"Events[{i}] cannot revise itself.", item.EventId);
            if (item.RevisionOf is not null && item.KnowledgeTimeUtc is null) AddIssue(issues, $"Events[{i}] revisions require KnowledgeTimeUtc.", item.EventId);

            if (!string.IsNullOrWhiteSpace(item.LiquidityEvidenceId))
            {
                var identity = new LiquidityRecordIdentity(
                    item.SourceId, item.DataVersion, item.EventTimeUtc, item.AvailableTimeUtc,
                    item.KnowledgeTimeUtc, item.SourceSequence, item.Quantity, item.CoverageId,
                    item.Kind, item.ExecutableOrderSide, item.HasCompleteCoverage);
                if (liquidityRecords.TryGetValue(item.LiquidityEvidenceId, out LiquidityRecordIdentity existing) && existing != identity)
                {
                    AddIssue(issues,
                        $"LiquidityEvidenceId '{item.LiquidityEvidenceId}' is reused with contradictory allocation metadata.",
                        item.EventId, item.LiquidityEvidenceId);
                }
                else
                {
                    liquidityRecords[item.LiquidityEvidenceId] = identity;
                }
            }
        }

        foreach (StrictMarketEvent item in events)
        {
            if (item is not null && item.RevisionOf is not null && !ids.Contains(item.RevisionOf))
            {
                AddIssue(issues, $"RevisionOf '{item.RevisionOf}' does not resolve to an event in this input.", item.EventId, item.RevisionOf);
            }
        }
    }

    private static void AddIssue(
        ImmutableArray<StrictRunIssue>.Builder issues,
        string message,
        params string[] relatedIds)
        => issues.Add(new StrictRunIssue(
            StrictRunIssueCode.InvalidInput,
            message,
            relatedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToImmutableArray()));

    private readonly record struct LiquidityRecordIdentity(
        string SourceId,
        string DataVersion,
        DateTime EventTimeUtc,
        DateTime AvailableTimeUtc,
        DateTime? KnowledgeTimeUtc,
        long? SourceSequence,
        long Quantity,
        string CoverageId,
        StrictMarketEventKind Kind,
        OrderSide? ExecutableOrderSide,
        bool HasCompleteCoverage);
}
