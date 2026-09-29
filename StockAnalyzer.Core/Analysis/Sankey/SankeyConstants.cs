namespace StockAnalyzer.Core.Analysis.Sankey;

/// <summary>
/// Single Source of Truth (SSoT) constants for the Sankey flow decomposition engine.
/// </summary>
public static class SankeyConstants
{
    /// <summary>
    /// Maximum allowed number of nodes in a single Sankey graph.
    /// </summary>
    public const int MaxNodes = 512;

    /// <summary>
    /// Maximum allowed number of edges in a single Sankey graph.
    /// </summary>
    public const int MaxEdges = 4096;

    /// <summary>
    /// Maximum number of decomposed paths in a single residual flow decomposition run (at most MaxEdges).
    /// </summary>
    public const int MaxPathCount = MaxEdges;

    /// <summary>
    /// Conservative upper bound for the total edge references across all decomposed paths in a single run.
    /// </summary>
    public const int MaxPathEdgeReferences = MaxEdges * (MaxNodes - 1);

    /// <summary>
    /// Maximum weight value supported for an individual edge flow (1 trillion).
    /// </summary>
    public const decimal MaxWeight = 1_000_000_000_000m;

    /// <summary>
    /// Quantum (step size) for weights (1e-6m). Weights must be integer multiples of this quantum.
    /// </summary>
    public const decimal WeightQuantum = 0.000001m;

    /// <summary>
    /// Maximum length for string metadata (UnitCode, DefinitionId).
    /// </summary>
    public const int MaxMetadataLength = 128;

    /// <summary>
    /// Numerical tolerance threshold for geometric DIP boundary alignment (0.0001 DIP).
    /// </summary>
    public const double GeometryToleranceDip = 0.0001;

    /// <summary>
    /// Maximum allowed viewport dimension in DIP.
    /// </summary>
    public const double MaxViewportDip = 1_000_000.0;

    /// <summary>
    /// Default viewport outer margin in DIP.
    /// </summary>
    public const double MarginDip = 16.0;

    /// <summary>
    /// Fixed width for node rectangles in DIP.
    /// </summary>
    public const double NodeWidthDip = 20.0;

    /// <summary>
    /// Vertical spacing between consecutive nodes in the same layer in DIP.
    /// </summary>
    public const double LayerNodeSpacingDip = 8.0;

    /// <summary>
    /// Minimum horizontal spacing between consecutive layer columns in DIP.
    /// </summary>
    public const double MinColumnSpacingDip = 24.0;

    /// <summary>
    /// Default alpha opacity for ribbon flow bands.
    /// </summary>
    public const double DefaultLinkOpacity = 0.6;

    /// <summary>
    /// Default alpha opacity for node rectangles.
    /// </summary>
    public const double DefaultNodeOpacity = 1.0;

    /// <summary>
    /// Border thickness for focused node rectangles in DIP.
    /// </summary>
    public const double FocusBorderWidthDip = 2.0;

    /// <summary>
    /// Default font size for node labels in DIP.
    /// </summary>
    public const double LabelFontSizeDip = 12.0;

    /// <summary>
    /// Horizontal padding inside node rectangle for label text in DIP.
    /// </summary>
    public const double LabelPaddingXDip = 4.0;

    /// <summary>
    /// Vertical padding inside node rectangle for label text in DIP.
    /// </summary>
    public const double LabelPaddingYDip = 2.0;

    /// <summary>
    /// Minimum node width required to render a text label in DIP.
    /// </summary>
    public const double MinLabelNodeWidthDip = 8.0;

    /// <summary>
    /// Minimum extra node height required above font size to render a text label in DIP.
    /// </summary>
    public const double MinLabelNodeHeightPaddingDip = 8.0;

    /// <summary>
    /// Default minimum visual rendering thickness for ribbon flow bands in DIP (Extension E07).
    /// Prevents micro-flows from vanishing into sub-pixel nothingness on display canvases.
    /// </summary>
    public const double MinVisibleBandWidthDip = 1.0;

    /// <summary>
    /// Default tolerance radius in DIP for pointer hit-testing on thin ribbon flow bands (Extension E07).
    /// Enables effortless mouse hovering and selection of micro-flows without pixel-hunting.
    /// </summary>
    public const double MinHitTestRadiusDip = 3.0;

    /// <summary>
    /// Base ID offset used for generating virtual pass-through dummy nodes in Extension E06.
    /// </summary>
    public const int DummyNodeIdBase = 1_000_000_000;

    /// <summary>
    /// Base ID offset used for generating segmented dummy edges in Extension E06.
    /// </summary>
    public const int DummyEdgeIdBase = 1_000_000_000;

    /// <summary>
    /// Checks whether a given node ID represents a virtual pass-through dummy node.
    /// </summary>
    public static bool IsDummyNode(int nodeId) => nodeId >= DummyNodeIdBase;

    /// <summary>
    /// Checks whether a given edge ID represents a virtual pass-through dummy edge.
    /// </summary>
    public static bool IsDummyEdge(int edgeId) => edgeId >= DummyEdgeIdBase;

    /// <summary>
    /// Decodes a potentially virtual/dummy edge ID back to its originating logical edge ID.
    /// </summary>
    public static int GetOriginalEdgeId(int edgeId)
    {
        if (edgeId >= DummyEdgeIdBase)
        {
            return (edgeId - DummyEdgeIdBase) / MaxNodes;
        }
        return edgeId;
    }
}
