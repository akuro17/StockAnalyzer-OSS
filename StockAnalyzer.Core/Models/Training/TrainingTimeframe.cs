namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Bar-aggregation level a training run consumes. The legacy Python resolver selects
/// <c>Data/Daily</c>, <c>Data/Weekly</c> or <c>Data/Monthly</c>; finalized-source jobs
/// use their run-owned prepared directory instead.
/// </summary>
/// <remarks>
/// <c>window</c> and <c>horizon</c> in <see cref="TrainingJobConfig"/> are counted in bars,
/// so their calendar span changes with this selection (75 daily bars vs 75 weekly bars are
/// not comparable). The wizard surfaces that caveat to the user.
/// Wire strings (see <c>TrainingConfigJson</c>): <c>daily</c> / <c>weekly</c> / <c>monthly</c>.
/// </remarks>
public enum TrainingTimeframe
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
}
