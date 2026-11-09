namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Method for calculating non-parametric rank correlation between two series.
/// </summary>
public enum RankCorrelationMethod
{
    /// <summary>
    /// Spearman's rank correlation coefficient (rho) with mid-rank tie handling.
    /// </summary>
    Spearman = 0,

    /// <summary>
    /// Kendall's rank correlation coefficient (tau-b) with pairwise tie adjustment.
    /// </summary>
    Kendall = 1
}
