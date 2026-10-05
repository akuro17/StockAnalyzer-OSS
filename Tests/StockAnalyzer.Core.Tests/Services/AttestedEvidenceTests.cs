#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FP06: producer-attested evidence in the SQLite monitoring store. Every fixture is generated from the contract (real SQLite
/// store, real ONNX registry, deterministic bars), because no real provider emits revision epochs, sequences or revocations.
/// </summary>
public sealed class AttestedEvidenceTests : IDisposable
{
    private const string Provider = "prov", Instrument = "INST", Symbol = "SYM";
    private readonly SqliteMonitoringTestKit _kit = new();
    private static readonly AttestedSourceEnrollment Enrollment = new()
    { Symbol = Symbol, Timeframe = TimeframeType.Daily, ProviderId = Provider, InstrumentId = Instrument };

    public void Dispose() => _kit.Dispose();

    /// <summary>The target window of the recorded forecast is bars 194..199 (anchor 194, horizon 5); bar 197 lies inside it.</summary>
    private const int InsideWindow = 197, OutsideWindow = 100;

    private readonly CapturingLogger _logger = new();

    private async Task<(SqlitePredictionLogService Log, string Id)> ArrangeAsync(params AttestedSourceEnrollment[] sources)
    {
        await _kit.EnsureRegistryAsync();
        var log = await _kit.CreateServiceAsync(attestedSources: sources.Length == 0 ? [Enrollment] : sources, logger: _logger);
        var id = await log.RecordAsync(_kit.Result!, Symbol, TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        return (log, id!);
    }

    private AttestedMonitoringSnapshot Snapshot(ulong sequence, decimal future = 106m, long epoch = 0, Func<int, BarFinality>? finality = null,
        ImmutableArray<BarTombstone>? tombstones = null, int keepLast = 200, DateTimeOffset? observed = null, string provider = Provider,
        string instrument = Instrument, TimeframeType timeframe = TimeframeType.Daily)
    {
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28, future);
        int skipped = bars.Length - keepLast;
        bars = bars.Skip(skipped).ToImmutableArray();
        var evidence = bars.Select((bar, i) =>
        {
            var start = PredictionLogService.Utc(bar.Timestamp);
            return new BarEvidence(start, start, start.AddDays(1), start.AddDays(1), finality?.Invoke(i + skipped) ?? BarFinality.Final);
        }).ToImmutableArray();
        return new AttestedMonitoringSnapshot(provider, instrument, timeframe, epoch, sequence, PredictionLogService.SourceRevision(bars),
            observed ?? _kit.Clock.Now, bars, evidence, tombstones ?? ImmutableArray<BarTombstone>.Empty);
    }

    private static BarFinality RevokedInside(int index) => index == InsideWindow ? BarFinality.ExplicitlyRevoked : BarFinality.Final;
    private static BarFinality AllUnknown(int _) => BarFinality.Unknown;

    private static async Task<PredictionOutcomeRevision> LatestAsync(IPredictionLogService log, string id) => (await log.ReadAuditAsync(id))!.Outcomes[^1];
    private static async Task<int> RevisionsAsync(IPredictionLogService log, string id) => (await log.ReadAuditAsync(id))!.Outcomes.Length;

    [Fact]
    public async Task Matured_ExplicitRevoke_Pending_Reconfirm_Matured_KeepsEveryRevisionAndOneVote()
    {
        var (log, id) = await ArrangeAsync();
        int changes = 0; log.Changed += (_, _) => changes++;

        await log.RefreshAttestedAsync(Snapshot(1));
        var matured = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Matured, "Up", 1L), (matured.State, matured.ActualLabel, matured.Revision));
        Assert.Equal(1, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);

        await log.RefreshAttestedAsync(Snapshot(2, finality: RevokedInside));
        var pending = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Pending, PendingReasons.ProviderFinalityRevoked, 2L), (pending.State, pending.PendingReason, pending.Revision));
        Assert.Null(pending.ActualLabel); Assert.Null(pending.LabelEndUtc);
        Assert.Equal(0, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);   // latest Pending contributes no vote

        await log.RefreshAttestedAsync(Snapshot(3));
        var again = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Matured, "Up", 3L), (again.State, again.ActualLabel, again.Revision));
        Assert.Equal(1, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);   // one vote per original identity

        var audit = (await log.ReadAuditAsync(id))!;                                           // prior revisions remain readable, id unchanged
        Assert.Equal([PredictionOutcomeState.Pending, PredictionOutcomeState.Matured, PredictionOutcomeState.Pending, PredictionOutcomeState.Matured],
            audit.Outcomes.Select(o => o.State));
        Assert.Equal(id, audit.Prediction.PredictionId);
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task CorrectedBars_AppendOneRevision_RecomputeTheLabelWithTheUnchangedRule_AndSameContentIsAHeadOnlyAdvance()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1, future: 106m));
        await log.RefreshAttestedAsync(Snapshot(2, future: 94m));
        var corrected = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Matured, "Down", 2L), (corrected.State, corrected.ActualLabel, corrected.Revision));
        var health = (await log.GetSnapshotsAsync(_kit.ModelId)).Single();
        Assert.Equal((1, 0), (health.MaturedCount, health.CorrectCount));                        // predicted Up, corrected to Down
        await log.RefreshAttestedAsync(Snapshot(3, future: 94m));                               // same applicable content
        Assert.Equal(3, await RevisionsAsync(log, id));
    }

    [Fact]
    public async Task SameOhlcvWithChangedFinality_WithdrawsOnlyOnAnExplicitRevocation()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1));
        await log.RefreshAttestedAsync(Snapshot(2, finality: AllUnknown));                      // same bars, Unknown tokens
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        await log.RefreshAttestedAsync(Snapshot(3, finality: RevokedInside));                   // same bars, explicit revoke
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);
    }

    [Fact]
    public async Task RevocationOutsideTheUsedWindow_DoesNotWithdraw()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1));
        await log.RefreshAttestedAsync(Snapshot(2, finality: i => i == OutsideWindow ? BarFinality.ExplicitlyRevoked : BarFinality.Final));
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        Assert.Equal(2, await RevisionsAsync(log, id));
    }

    [Fact]
    public async Task ShortEmptyUnknownOrMissingEvidence_NeverWithdrawsAndNeverMatures()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1));
        await log.RefreshAttestedAsync(Snapshot(2, keepLast: 3));                                // anchor not in the short window
        await log.RefreshAttestedAsync(Snapshot(3, keepLast: 0));                                // empty observation is not a revocation
        await log.RefreshAttestedAsync(Snapshot(4, finality: AllUnknown));
        var latest = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Matured, 1L), (latest.State, latest.Revision));
        Assert.Equal(1, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);
    }

    [Fact]
    public async Task UnknownEvidence_NeverMaturesAPendingForecast_AndTheForecastStaysPending()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1, finality: AllUnknown));
        await log.RefreshAttestedAsync(Snapshot(2, keepLast: 3));
        await log.RefreshAttestedAsync(Snapshot(3, keepLast: 0));
        Assert.Equal((PredictionOutcomeState.Pending, 0L), ((await LatestAsync(log, id)).State, (await LatestAsync(log, id)).Revision));
    }

    [Fact]
    public async Task TombstoneInsideTheWindow_Withdraws_AndATombstoneOutsideDoesNot()
    {
        var (log, id) = await ArrangeAsync();
        var anchor = PredictionLogService.Utc(SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28)[InsideWindow].Timestamp);
        await log.RefreshAttestedAsync(Snapshot(1));
        await log.RefreshAttestedAsync(Snapshot(2, tombstones: [new BarTombstone(anchor.AddYears(-1), anchor, "deleted")]));
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        // The deleted bar is no longer among the rows: the snapshot must not repeat its anchor in the evidence.
        var snapshot = Snapshot(3);
        var without = snapshot with
        {
            Candles = snapshot.Candles.RemoveAt(InsideWindow), Evidence = snapshot.Evidence.RemoveAt(InsideWindow),
            Tombstones = [new BarTombstone(anchor, anchor.AddDays(1), "deleted")],
        };
        without = without with { SourceRevision = PredictionLogService.SourceRevision(without.Candles) };
        await log.RefreshAttestedAsync(without);
        var latest = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Pending, PendingReasons.ProviderFinalityRevoked), (latest.State, latest.PendingReason));
    }

    [Fact]
    public async Task SequenceTwelveBeforeEleven_Retains12_AndTheOlderDeliveryIsOnlyAnOrderingDiagnostic()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(12, finality: RevokedInside));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);
        await log.RefreshAttestedAsync(Snapshot(11));                                            // older, would have matured it
        Assert.Equal((PredictionOutcomeState.Pending, 1), ((await LatestAsync(log, id)).State, await RevisionsAsync(log, id)));
        Assert.Equal(12UL, await HeadSequenceAsync());
        Assert.Equal(1L, await _kit.QueryAsync("SELECT COUNT(*) FROM AttestedEvidence WHERE Disposition = 'IgnoredLowerSequence';", r => r.GetInt64(0)));
    }

    [Fact]
    public async Task EqualSequenceConflict_AfterAMaturedDelivery_LeavesZeroVotesAndCannotBeRestoredBySameSequenceInput()
    {
        var (log, id) = await ArrangeAsync();
        var a = Snapshot(5); var b = Snapshot(5, finality: RevokedInside); var c = Snapshot(5, future: 107m);
        await log.RefreshAttestedAsync(a);
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        await log.RefreshAttestedAsync(b);
        var conflict = await LatestAsync(log, id);
        Assert.Equal((PredictionOutcomeState.Pending, PendingReasons.EvidenceConflict), (conflict.State, conflict.PendingReason));
        Assert.Equal(0, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);
        Assert.Single(await log.GetConflictedStreamsAsync());                                    // exposed as a data-integrity fault

        await log.RefreshAttestedAsync(a); await log.RefreshAttestedAsync(c); await log.RefreshAttestedAsync(a);
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);        // no hash is ever chosen
        Assert.Equal(0, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);

        await log.RefreshAttestedAsync(Snapshot(6));                                             // a higher sequence resolves it
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        Assert.Empty(await log.GetConflictedStreamsAsync());
    }

    [Fact]
    public async Task EqualSequenceConflict_InTheOtherArrivalOrder_AlsoLeavesZeroVotes()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(5, finality: RevokedInside));
        await log.RefreshAttestedAsync(Snapshot(5));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);
        Assert.Equal(0, (await log.EvaluateCurrentAsync(_kit.ModelId)).Single().MaturedCount);
        Assert.Single(await log.GetConflictedStreamsAsync());
    }

    [Fact]
    public async Task IdenticalRedeliveryOfTheSameSequence_IsIdempotent_NoRevisionNoDuplicateAudit()
    {
        var (log, id) = await ArrangeAsync();
        var snapshot = Snapshot(1);
        await log.RefreshAttestedAsync(snapshot); await log.RefreshAttestedAsync(snapshot with { ObservedUtc = snapshot.ObservedUtc.AddMinutes(-1) });
        Assert.Equal(2, await RevisionsAsync(log, id));
        Assert.Equal(1L, await _kit.CountAsync("AttestedEvidence"));
    }

    [Fact]
    public async Task ANewerNoOpDelivery_AdvancesTheHead_SoAnOlderRevocationCannotWin()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(8));
        await log.RefreshAttestedAsync(Snapshot(10, finality: AllUnknown));                      // no outcome change, head moves to 10
        Assert.Equal(10UL, await HeadSequenceAsync());
        await log.RefreshAttestedAsync(Snapshot(9, finality: RevokedInside));                    // delayed older revocation
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        Assert.Equal(10UL, await HeadSequenceAsync());
    }

    [Fact]
    public async Task HigherEpoch_MayResetTheSequence_ALowerEpochIsIgnored()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(100, epoch: 0));
        await log.RefreshAttestedAsync(Snapshot(1, epoch: 1, finality: RevokedInside));          // sequence restarts under a higher epoch
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);
        await log.RefreshAttestedAsync(Snapshot(200, epoch: 0));                                 // the old epoch can never win again
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);
        Assert.Equal((1L, 1UL), (await HeadEpochAsync(), await HeadSequenceAsync()));
        Assert.Equal(1L, await _kit.QueryAsync("SELECT COUNT(*) FROM AttestedEvidence WHERE Disposition = 'IgnoredLowerEpoch';", r => r.GetInt64(0)));
    }

    [Fact]
    public async Task CorruptEvidence_CommitsNothing_AndBlankOrNullInputsFailBeforeAnyWrite()
    {
        var (log, id) = await ArrangeAsync();
        var good = Snapshot(1);
        var bad = new Dictionary<string, AttestedMonitoringSnapshot>
        {
            ["source revision"] = good with { SourceRevision = new string('a', 64) },
            ["short evidence"] = good with { Evidence = good.Evidence.RemoveAt(0) },
            ["shifted anchor"] = good with { Evidence = good.Evidence.SetItem(10, good.Evidence[10] with { AnchorUtc = good.Evidence[10].AnchorUtc.AddHours(1) }) },
            ["bad enum"] = good with { Evidence = good.Evidence.SetItem(10, good.Evidence[10] with { Finality = (BarFinality)99 }) },
            ["start after end"] = good with { Evidence = good.Evidence.SetItem(10, good.Evidence[10] with { PeriodStartUtc = good.Evidence[10].PeriodEndUtc }) },
            ["end after available"] = good with { Evidence = good.Evidence.SetItem(10, good.Evidence[10] with { AvailableAtUtc = good.Evidence[10].PeriodEndUtc.AddSeconds(-1) }) },
            ["future availability"] = good with { Evidence = good.Evidence.SetItem(199, good.Evidence[199] with { AvailableAtUtc = good.ObservedUtc.AddSeconds(1) }) },
            ["future observation"] = good with { ObservedUtc = _kit.Clock.Now.AddSeconds(1) },
            ["non-utc observation"] = good with { ObservedUtc = good.ObservedUtc.ToOffset(TimeSpan.FromHours(9)) },
            ["negative epoch"] = good with { RevisionEpoch = -1 },
            ["duplicate tombstone"] = good with { Tombstones = [new BarTombstone(good.ObservedUtc.AddDays(-400), good.ObservedUtc), new BarTombstone(good.ObservedUtc.AddDays(-400), good.ObservedUtc)] },
            ["tombstone repeats evidence"] = good with { Tombstones = [new BarTombstone(good.Evidence[5].AnchorUtc, good.ObservedUtc)] },
            ["tombstone from the future"] = good with { Tombstones = [new BarTombstone(good.ObservedUtc.AddDays(-400), good.ObservedUtc.AddSeconds(1))] },
            ["default arrays"] = good with { Candles = default, Evidence = default, Tombstones = default },
            ["blank provider"] = good with { ProviderId = " " },
        };
        foreach (var (name, snapshot) in bad)
            await Assert.ThrowsAsync<InvalidDataException>(() => log.RefreshAttestedAsync(snapshot));
        await Assert.ThrowsAsync<ArgumentNullException>(() => log.RefreshAttestedAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => log.RefreshAttestedAsync(good with { InstrumentId = " " }));
        Assert.Equal(1, await RevisionsAsync(log, id));
        Assert.Equal(0L, await _kit.QueryAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'AttestedStreams';", r => r.GetInt64(0))); // nothing was created
    }

    [Theory]
    [InlineData("high below low")]
    [InlineData("negative volume")]
    public async Task InvalidOhlcvBar_IsRejectedAtTheStoreBoundary_EvenWhenTheSourceRevisionMatches(string defect)
    {
        var (log, id) = await ArrangeAsync();
        var good = Snapshot(1);
        var bar = good.Candles[10];
        var broken = good.Candles.SetItem(10, defect == "high below low" ? bar with { High = bar.Low - 1m } : bar with { Volume = -1 });
        var snapshot = good with { Candles = broken, SourceRevision = PredictionLogService.SourceRevision(broken) };
        await Assert.ThrowsAsync<InvalidDataException>(() => log.RefreshAttestedAsync(snapshot));
        Assert.Equal(1, await RevisionsAsync(log, id));
        Assert.Equal(0L, await _kit.QueryAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'AttestedStreams';", r => r.GetInt64(0)));
    }

    [Fact]
    public async Task EveryAuditRowOfANonTrivialDelivery_CarriesItsReason_AndACompleteDeliveryHasNone()
    {
        var (log, _) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1));                                              // complete: nothing to explain
        await log.RefreshAttestedAsync(Snapshot(2, finality: AllUnknown));                        // incomplete: an evidence gap
        await log.RefreshAttestedAsync(Snapshot(1, finality: AllUnknown));                        // older than the head (other digest than the first)
        await log.RefreshAttestedAsync(Snapshot(2, future: 107m));                                // same sequence, other content
        await log.RefreshAttestedAsync(Snapshot(1, epoch: 1));                                    // a higher epoch may restart the sequence
        await log.RefreshAttestedAsync(Snapshot(9, epoch: 0));                                    // lower epoch
        Assert.Equal("EvidenceGap:UnknownBars=200", await _kit.QueryAsync("SELECT Reason FROM AttestedEvidence WHERE Disposition = 'Applied' AND Sequence = '2';", r => r.GetString(0)));
        Assert.Equal("SameSequenceDifferentContent", await _kit.QueryAsync("SELECT Reason FROM AttestedEvidence WHERE Disposition = 'Conflict';", r => r.GetString(0)));
        Assert.Equal(1L, await _kit.QueryAsync("SELECT COUNT(*) FROM AttestedEvidence WHERE Disposition = 'Applied' AND Sequence = '1' AND Epoch = 0 AND Reason IS NULL;", r => r.GetInt64(0)));
        Assert.Equal(1L, await _kit.QueryAsync("SELECT COUNT(*) FROM AttestedEvidence WHERE Disposition = 'IgnoredLowerEpoch' AND Reason = 'HeadEpoch=1';", r => r.GetInt64(0)));
        Assert.Equal(1L, await _kit.QueryAsync("SELECT COUNT(*) FROM AttestedEvidence WHERE Disposition = 'IgnoredLowerSequence' AND Reason = 'HeadSequence=2';", r => r.GetInt64(0)));
    }

    [Fact]
    public async Task ConflictsGapsAndIgnoredDeliveries_AreLogged_ACompleteDeliveryIsNot()
    {
        var (log, _) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1));
        Assert.Empty(_logger.Entries);                                                            // nothing to report
        await log.RefreshAttestedAsync(Snapshot(2, finality: AllUnknown));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, Assert.Single(_logger.Entries).Level);
        Assert.Contains("evidence gap", _logger.Entries[^1].Message);
        await log.RefreshAttestedAsync(Snapshot(1, finality: AllUnknown));
        Assert.Contains("below the stream head", _logger.Entries[^1].Message);
        await log.RefreshAttestedAsync(Snapshot(2, future: 107m));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, _logger.Entries[^1].Level);
        Assert.Contains("different content", _logger.Entries[^1].Message);
        await log.RefreshAttestedAsync(Snapshot(9));                                               // complete and higher: resolves, nothing to report
        Assert.Equal(3, _logger.Entries.Count);
    }

    [Fact]
    public async Task AStoredTimeframeThatMonitoringDoesNotSupport_IsACorruptRow_InStreamsAndInBindings()
    {
        var (log, _) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1)); await log.RefreshAttestedAsync(Snapshot(1, future: 107m));   // the stream is in conflict
        Assert.Single(await log.GetConflictedStreamsAsync());
        await ExecAsync("UPDATE AttestedStreams SET Timeframe = 99;");
        await Assert.ThrowsAsync<InvalidDataException>(() => log.GetConflictedStreamsAsync());
        await ExecAsync("UPDATE AttestedStreams SET Timeframe = 0;");                                           // M1 is defined, but monitoring never supported it
        await Assert.ThrowsAsync<InvalidDataException>(() => log.GetConflictedStreamsAsync());
        await ExecAsync("UPDATE AttestedStreams SET Timeframe = 5;");                                           // Daily reads again
        Assert.Single(await log.GetConflictedStreamsAsync());
        await ExecAsync("UPDATE AttestedBindings SET Timeframe = -1;");
        await Assert.ThrowsAsync<InvalidDataException>(() => log.RefreshAttestedAsync(Snapshot(2)));
        await ExecAsync("UPDATE AttestedBindings SET Timeframe = 4;");                                          // H4 is defined, but not supported
        await Assert.ThrowsAsync<InvalidDataException>(() => log.RefreshAttestedAsync(Snapshot(2)));
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = await _kit.Store().OpenAsync(requireInitialized: true, System.Threading.CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ExactlyEqualEndAvailableAndObserved_IsAccepted_FutureAvailabilityIsNot()
    {
        var (log, id) = await ArrangeAsync();
        var good = Snapshot(1);
        var lastEnd = good.Evidence[^1].PeriodEndUtc;
        await log.RefreshAttestedAsync(good with { ObservedUtc = lastEnd });                     // end = available = observed
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, id)).State);
        await Assert.ThrowsAsync<InvalidDataException>(() => log.RefreshAttestedAsync(Snapshot(2) with { ObservedUtc = lastEnd.AddSeconds(-1) }));
    }

    [Fact]
    public async Task LegacyForecast_IsUntouchedUntilBound_AndBoundForecastsAreExcludedFromTheLegacyRefresh()
    {
        var (log, id) = await ArrangeAsync();
        await _kit.ObserveVoteAsync(log, Symbol, SqliteMonitoringTestKit.Sept28, correct: true);   // legacy maturity, before any evidence
        Assert.Equal(1L, (await LatestAsync(log, id)).Revision);
        await log.RefreshAttestedAsync(Snapshot(1, finality: AllUnknown));                       // enrollment event: binds, changes nothing
        Assert.Equal(1L, (await LatestAsync(log, id)).Revision);
        await log.RefreshAttestedAsync(Snapshot(2, finality: RevokedInside));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);
        await _kit.ObserveVoteAsync(log, Symbol, SqliteMonitoringTestKit.Sept28, correct: true); // the legacy path must not re-mature it
        Assert.Equal((PredictionOutcomeState.Pending, 2L), ((await LatestAsync(log, id)).State, (await LatestAsync(log, id)).Revision));
    }

    [Fact]
    public async Task UnenrolledSymbolOrOtherAuthority_IsNeverMerged()
    {
        await _kit.EnsureRegistryAsync();
        var rival = new AttestedSourceEnrollment { Symbol = Symbol, Timeframe = TimeframeType.Daily, ProviderId = "someone-else", InstrumentId = Instrument };
        var log = await _kit.CreateServiceAsync(attestedSources: [Enrollment, rival]);
        var other = await log.RecordAsync(_kit.Result!, "OTHER", TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        var enrolled = await log.RecordAsync(_kit.Result!, Symbol, TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        await log.RefreshAttestedAsync(Snapshot(1));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, other!)).State);   // not enrolled
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, enrolled!)).State);
        // The same instrument from another provider is another authority: the bound forecast is skipped, not reinterpreted.
        await log.RefreshAttestedAsync(Snapshot(1, provider: "someone-else", finality: RevokedInside));
        Assert.Equal(PredictionOutcomeState.Matured, (await LatestAsync(log, enrolled!)).State);
    }

    [Fact]
    public async Task AnotherTimeframeOfTheSameInstrument_IsIndependent()
    {
        var (log, id) = await ArrangeAsync();
        await log.RefreshAttestedAsync(Snapshot(1, timeframe: TimeframeType.Weekly, finality: RevokedInside));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id)).State);        // the daily forecast is not enrolled in the weekly stream
        Assert.Equal(1, await RevisionsAsync(log, id));
    }

    [Fact]
    public async Task StreamWithoutEnrollment_StillKeepsItsHeadAndAudit_AndTouchesNoForecast()
    {
        await _kit.EnsureRegistryAsync();
        var log = await _kit.CreateServiceAsync();                                               // no enrollment
        var id = await log.RecordAsync(_kit.Result!, Symbol, TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        await log.RefreshAttestedAsync(Snapshot(7));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestAsync(log, id!)).State);
        Assert.Equal(7UL, await HeadSequenceAsync());
    }

    [Fact]
    public async Task WithoutAnyAttestedRefresh_NoCompanionTableIsEverCreated()
    {
        await _kit.EnsureRegistryAsync();
        var log = await _kit.CreateServiceAsync(attestedSources: [Enrollment]);
        await log.RecordAsync(_kit.Result!, Symbol, TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        await _kit.ObserveVoteAsync(log, Symbol, SqliteMonitoringTestKit.Sept28, correct: true);
        await log.EvaluateCurrentAsync();
        Assert.Empty(await log.GetConflictedStreamsAsync());
        Assert.Equal(0L, await _kit.QueryAsync("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'Attested%';", r => r.GetInt64(0)));
    }

    [Fact]
    public async Task RevocationOfAnUnavailableForecast_NeverRevivesIt()
    {
        var (log, id) = await ArrangeAsync();
        await log.MarkUnavailableAsync(id, "source permanently removed");
        await log.RefreshAttestedAsync(Snapshot(1));
        Assert.Equal(PredictionOutcomeState.Unavailable, (await LatestAsync(log, id)).State);
    }

    [Fact]
    public async Task BackendFactory_ProvidesTheCapabilityOnlyWithSqlite()
    {
        await _kit.EnsureRegistryAsync();
        var json = new PredictionMonitoringBackendFactory(_kit.RegistryOrMock, Microsoft.Extensions.Options.Options.Create(
            SqliteMonitoringTestKit.Settings(backend: PredictionMonitoringBackendKind.Json)), clock: _kit.Clock, root: Path.Combine(_kit.Root, "json"));
        Assert.Null(json.Attested); Assert.Null(json.Diagnostics);
        var sqlite = new PredictionMonitoringBackendFactory(_kit.RegistryOrMock, Microsoft.Extensions.Options.Options.Create(
            SqliteMonitoringTestKit.Settings(backend: PredictionMonitoringBackendKind.Sqlite)), clock: _kit.Clock, root: Path.Combine(_kit.Root, "sqlite"));
        Assert.NotNull(sqlite.Attested); Assert.NotNull(sqlite.Diagnostics);
    }

    private async Task<ulong> HeadSequenceAsync() => ulong.Parse(await _kit.QueryAsync("SELECT HeadSequence FROM AttestedStreams;", r => r.GetString(0)));
    private Task<long> HeadEpochAsync() => _kit.QueryAsync("SELECT HeadEpoch FROM AttestedStreams;", r => r.GetInt64(0));
}
