using System.Collections.Generic;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Optional capability interface implemented by indicator results (and preserved by result wrappers)
/// that declare explicit rendering primitives. Consumers check
/// <c>if (result is IPrimitiveAwareIndicatorResult { PrimitiveDescriptors.Count: &gt; 0 } aware)</c>
/// and fall back to legacy rendering otherwise.
/// </summary>
public interface IPrimitiveAwareIndicatorResult
{
    IReadOnlyList<SeriesPrimitiveDescriptor> PrimitiveDescriptors { get; }
}
