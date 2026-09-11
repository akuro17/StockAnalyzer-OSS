using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Semantic;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Registers and resolves semantic signal conflicts based on priority, exclusivity, and proximity rules.
/// Operates with [ZeroAllocation] by requiring an output buffer for continuous evaluation loops.
/// </summary>
public class SignalConflictRegistry<T> where T : ISemanticSignal
{
    /// <summary>
    /// Rule definition: Returns true if the candidate signal should be excluded based on the existing accepted signal.
    /// </summary>
    private readonly List<Func<T, T, bool>> _exclusionRules = new();

    /// <summary>
    /// Adds a custom rule defining when a candidate signal is excluded by a previously accepted signal.
    /// delegate(existing, candidate) => bool
    /// </summary>
    public void AddExclusionRule(Func<T, T, bool> rule)
    {
        _exclusionRules.Add(rule);
    }

    /// <summary>
    /// Evaluates the input signals and populates the valid, non-conflicting signals into the output buffer.
    /// </summary>
    /// <param name="signals">The chronological list of signals to resolve.</param>
    /// <param name="outputBuffer">A pre-allocated buffer the accepted signals will be written into, ensuring Zero Allocation.</param>
    public void ResolveConflicts(IReadOnlyList<T> signals, List<T> outputBuffer)
    {
        outputBuffer.Clear();
        if (signals.Count == 0) return;

        for (int i = 0; i < signals.Count; i++)
        {
            T candidate = signals[i];
            bool rejected = false;

            // Phase 1: Check basic same-index priority conflicts
            for (int j = 0; j < outputBuffer.Count; j++)
            {
                T existing = outputBuffer[j];

                if (existing.Index == candidate.Index)
                {
                    if (candidate.Priority > existing.Priority)
                    {
                        // Candidate wins, replace existing
                        outputBuffer[j] = candidate;
                        rejected = true; // Handled by replacement, no need to add again
                        break;
                    }
                    else
                    {
                        // Existing wins or ties, candidate rejected
                        rejected = true;
                        break;
                    }
                }
            }

            if (rejected)
                continue;

            // Phase 2: Evaluate custom exclusion rules
            for (int j = 0; j < outputBuffer.Count; j++)
            {
                T existing = outputBuffer[j];
                
                for (int r = 0; r < _exclusionRules.Count; r++)
                {
                    // If existing signal causes candidate to be excluded
                    if (_exclusionRules[r](existing, candidate))
                    {
                        rejected = true;
                        break;
                    }
                }
                if (rejected) break;
            }

            if (!rejected)
            {
                outputBuffer.Add(candidate);
            }
        }
    }
}
