using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

public static class PivotPipeline
{
    /// <summary>
    /// Generates sequential trendline candidates by connecting consecutive pivots of the same type.
    /// Avoids LINQ to adhere to ZeroAllocation constraints in the analysis loop.
    /// </summary>
    /// <param name="pivots">List of previously extracted pivots.</param>
    /// <returns>A list of trendline candidates.</returns>
    public static void GenerateSequentialCandidates(
        IReadOnlyList<FractalPivot> pivots,
        List<TrendlineCandidate> outputBuffer)
    {
        outputBuffer.Clear();
        if (pivots == null || pivots.Count < 2)
        {
            return;
        }

        FractalPivot? lastHigh = null;
        FractalPivot? lastLow = null;

        for (int i = 0; i < pivots.Count; i++)
        {
            var current = pivots[i];
            
            if (current.Type == FractalPivotType.High)
            {
                if (lastHigh.HasValue)
                {
                    outputBuffer.Add(new TrendlineCandidate
                    {
                        StartPoint = lastHigh.Value,
                        EndPoint = current,
                        Type = FractalPivotType.High
                    });
                }
                lastHigh = current;
            }
            else if (current.Type == FractalPivotType.Low)
            {
                if (lastLow.HasValue)
                {
                    outputBuffer.Add(new TrendlineCandidate
                    {
                        StartPoint = lastLow.Value,
                        EndPoint = current,
                        Type = FractalPivotType.Low
                    });
                }
                lastLow = current;
            }
        }
    }
}
