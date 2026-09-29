namespace StockAnalyzer.Core.Analysis.Sankey;

/// <summary>
/// Status outcome of a Sankey decomposition run.
/// </summary>
public enum SankeyStatus
{
    Success,
    Empty,
    Invalid
}

/// <summary>
/// Diagnostic error code for Sankey decomposition validation or execution failures.
/// </summary>
public enum SankeyError
{
    None,
    WorkspaceDisposed,
    WorkspaceBusy,
    LimitExceeded,
    InvalidMeasure,
    InvalidNodeId,
    DuplicateNodeId,
    InvalidEdgeId,
    DuplicateEdgeId,
    MissingSource,
    MissingTarget,
    SelfLoop,
    InvalidWeight,
    WeightPrecision,
    BufferTooSmall,
    Cycle,
    NonAdjacentLayer,
    NumericOverflow
}

/// <summary>
/// Semantic domain category of the flow measure.
/// </summary>
public enum SankeyMeasureKind
{
    Count,
    Volume,
    Amount,
    Ratio,
    Other
}

/// <summary>
/// Origin provenance of the measure data.
/// </summary>
public enum SankeyProvenance
{
    Observed,
    Modelled
}

/// <summary>
/// Input node descriptor with a unique non-negative identifier.
/// </summary>
public readonly record struct SankeyNode(int Id);

/// <summary>
/// Input edge descriptor connecting SourceId to TargetId with non-negative weight.
/// </summary>
public readonly record struct SankeyEdge(int Id, int SourceId, int TargetId, decimal Weight);

/// <summary>
/// Calculated flow metrics and topological placement for an individual node.
/// </summary>
public readonly record struct SankeyNodeValue(
    int Id,
    decimal Incoming,
    decimal Outgoing,
    decimal Capacity,
    decimal Balance,
    int Layer,
    int Order);

/// <summary>
/// Validated edge output descriptor with normalized weight.
/// </summary>
public readonly record struct SankeyEdgeValue(
    int Id,
    int SourceId,
    int TargetId,
    decimal Weight);

/// <summary>
/// Semantic unit and metadata contract accompanying the flow graph.
/// </summary>
public readonly record struct SankeyMeasure(
    SankeyMeasureKind Kind,
    string UnitCode,
    SankeyProvenance Provenance,
    string DefinitionId);

/// <summary>
/// Overall outcome of the Sankey decomposition analysis.
/// </summary>
public readonly record struct SankeyRunResult(
    SankeyStatus Status,
    int NodeCount,
    int EdgeCount,
    int LayerCount,
    SankeyError Error,
    int ErrorId);
