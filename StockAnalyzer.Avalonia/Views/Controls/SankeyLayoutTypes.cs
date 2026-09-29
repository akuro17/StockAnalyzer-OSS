namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Status outcome of a Sankey geometry layout calculation.
/// </summary>
public enum SankeyLayoutStatus
{
    Ready,
    NoPositiveFlow,
    Suspended,
    Invalid
}

/// <summary>
/// Error diagnostic code for layout validation or numerical calculation failures.
/// </summary>
public enum SankeyLayoutError
{
    None,
    InvalidSnapshot,
    InvalidViewport,
    InsufficientViewport,
    BufferTooSmall,
    WorkspaceBusy,
    WorkspaceDisposed,
    GeometryPrecision
}

/// <summary>
/// Display surface bounds in framework-neutral DIP coordinates.
/// </summary>
public readonly record struct SankeyViewport(double WidthDip, double HeightDip);

/// <summary>
/// Screen coordinate point in DIP.
/// </summary>
public readonly record struct SankeyScreenPoint(double X, double Y);

/// <summary>
/// Calculated rectangular boundary for a node in DIP coordinates.
/// </summary>
public readonly record struct SankeyNodeGeometry(
    int NodeId,
    double X0,
    double Y0,
    double X1,
    double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
}

/// <summary>
/// Cubic Bézier curve defined by four control points.
/// </summary>
public readonly record struct SankeyCubic(
    SankeyScreenPoint P0,
    SankeyScreenPoint P1,
    SankeyScreenPoint P2,
    SankeyScreenPoint P3);

/// <summary>
/// Closed ribbon band boundary linking two nodes.
/// Formed by Upper cubic curve, vertical end line, LowerReverse cubic curve, and vertical start line.
/// </summary>
public readonly record struct SankeyBandGeometry(
    int EdgeId,
    double WidthDip,
    SankeyCubic Upper,
    SankeyCubic LowerReverse);

/// <summary>
/// Overall result of a Sankey geometry layout run.
/// </summary>
public readonly record struct SankeyLayoutResult(
    SankeyLayoutStatus Status,
    int NodeCount,
    int BandCount,
    SankeyLayoutError Error);
