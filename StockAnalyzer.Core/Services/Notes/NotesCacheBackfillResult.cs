namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Outcome of one <see cref="TickerMetadataNotesCacheSynchronizer.RecalculateAllNotesCachesAsync"/> pass.
/// </summary>
/// <param name="Total">Tickers that have at least one active Note (the tickers the pass looked at).</param>
/// <param name="Rewritten">Tickers whose stored Notes cache differed and was rewritten.</param>
/// <param name="Failed">Tickers that could not be processed (unreadable stored data or another error); their stored
/// data was left untouched and a later pass retries them.</param>
public readonly record struct NotesCacheBackfillResult(int Total, int Rewritten, int Failed);
