namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Sample data series extracted from candles within a <c>DistributionRange</c>.
/// </summary>
public enum DistributionDataMode
{
    LogReturn = 0,
    SimpleReturn = 1,
    Price = 2
}

/// <summary>
/// Visual presentation layout for a <c>DistributionRange</c> chart object.
/// </summary>
public enum DistributionDisplayStyle
{
    Histogram = 0,
    HistogramWithCurve = 1,
    HorizontalPriceProfile = 2
}

/// <summary>
/// Histogram bin-count selection rule used by <see cref="DistributionRangeCalculator"/>.
/// </summary>
public enum DistributionBinningRule
{
    FreedmanDiaconis = 0,
    Sturges = 1,
    Scott = 2,
    FixedCount = 3
}

/// <summary>
/// Reference continuous probability distribution fitted onto the empirical sample.
/// Only univariate continuous PDFs implemented in <c>DslProbabilityDistributionsMath</c> are exposed.
/// </summary>
public enum ReferenceDistributionType
{
    None = 0,
    Normal = 1,
    StudentT = 2,
    Laplace = 3,
    Logistic = 4,
    Cauchy = 5,
    Gev = 6,
    Uniform = 7,
    LogNormal = 8,
    Gamma = 9,
    Weibull = 10,
    Exponential = 11,
    Pareto = 12
}

/// <summary>
/// Outcome status of fitting <see cref="ReferenceDistributionType"/> in <see cref="DistributionRangeResult"/>.
/// </summary>
public enum ReferenceFitStatus
{
    Fitted = 0,
    None = 1,
    DomainViolation = 2,
    InsufficientData = 3,
    FlatSeries = 4
}
