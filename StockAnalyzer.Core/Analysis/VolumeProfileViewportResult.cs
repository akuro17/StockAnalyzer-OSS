using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

public enum VolumeProfileResultStatus
{
    Success,
    Empty,
    Cancelled,
    Failed
}

public sealed class VolumeProfileSegment
{
    public int StartIndex { get; init; }
    public int Count { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public bool IsPartial { get; init; }
    public IReadOnlyList<VolumeBin> Bins { get; init; } = Array.Empty<VolumeBin>();
    public decimal? POC { get; init; }
    public decimal? VAH { get; init; }
    public decimal? VAL { get; init; }
}

public sealed class VolumeProfileViewportResult
{
    public string RequestKey { get; init; } = string.Empty;
    public VolumeProfileResultStatus Status { get; init; }
    public IReadOnlyList<VolumeProfileSegment> Segments { get; init; } = Array.Empty<VolumeProfileSegment>();
}
