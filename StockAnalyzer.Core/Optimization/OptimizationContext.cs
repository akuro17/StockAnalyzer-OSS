using System;
using System.Diagnostics;
using System.Threading;

namespace StockAnalyzer.Core.Optimization
{
    /// <summary>
    /// Defines the execution strategy based on system resource availability and user preference.
    /// </summary>
    public enum ExecutionMode
    {
        /// <summary>
        /// Utilitizes maximum available resources for fastest processing.
        /// Ideal for batch processing where UI responsiveness is secondary.
        /// </summary>
        HighPerformance,

        /// <summary>
        /// Balances resource usage to maintain UI responsiveness.
        /// Suitable for background tasks during active usage.
        /// </summary>
        Balanced,

        /// <summary>
        /// Minimizes resource impact, running only when significant resources are free.
        /// Ideal for background maintenance tasks.
        /// </summary>
        LowImpact
    }

    /// <summary>
    /// Context for managing optimization parameters and resource monitoring.
    /// Derived from the "Indicator System Extension Mechanism Standardization Plan Phase 1".
    /// </summary>
    public class OptimizationContext
    {
        private readonly ExecutionMode _mode;
        private readonly int _logicalCores;

        /// <summary>
        /// Gets the current execution mode.
        /// </summary>
        public ExecutionMode Mode => _mode;

        /// <summary>
        /// Gets the calculated optimal degree of parallelism.
        /// </summary>
        public int RecommendedParallelism { get; private set; }

        /// <summary>
        /// Gets the recommended chunk size for data partitioning.
        /// Value depends on data size and parallelism.
        /// </summary>
        public int RecommendedChunkSize { get; private set; }

        /// <summary>
        /// Gets the minimum data count required to trigger parallel execution.
        /// </summary>
        public int ParallelizationThreshold { get; private set; }

        public OptimizationContext(ExecutionMode mode = ExecutionMode.Balanced)
        {
            _mode = mode;
            _logicalCores = Environment.ProcessorCount;
            CalculateParameters();
        }

        /// <summary>
        /// Recalculates optimization parameters based on current system state.
        /// </summary>
        public void Refresh()
        {
            CalculateParameters();
        }

        private void CalculateParameters()
        {
            // Baseline based on logical cores
            int baseParallelism;

            switch (_mode)
            {
                case ExecutionMode.HighPerformance:
                    // Use all cores, leaving one for OS/UI if possible, but at least 1
                    baseParallelism = Math.Max(1, _logicalCores);
                    break;
                case ExecutionMode.Balanced:
                    // Use half of available cores, minimum 1
                    baseParallelism = Math.Max(1, _logicalCores / 2);
                    break;
                case ExecutionMode.LowImpact:
                    // Use quarter of cores or just 1
                    baseParallelism = Math.Max(1, _logicalCores / 4);
                    break;
                default:
                    baseParallelism = 1;
                    break;
            }

            // Memory-based adjustment (Simple Heuristic for Phase 1)
            // If memory is tight, reduce parallelism to prevent OOM
            var memoryInfo = GC.GetGCMemoryInfo();
            // If more than 80% of memory is loaded (in container/system assumption), throttle back
            // Note: This is a loose check as specific limits depend on the app's context
            // For Phase 1, we imply a "safe" reduction if heap turn-over is high, 
            // but absent detailed telemetry, we'll stick to core-based logic primarily.
            
            // Apply bounds
            RecommendedParallelism = Math.Max(1, Math.Min(baseParallelism, 128)); // Cap at 128 for sanity

            // Default chunk size (can be overridden by partitioner based on actual data count)
            RecommendedChunkSize = 1000;

            // Determine Parallelization Threshold
            // HighPerformance: Parallelize aggressively (low threshold)
            // Balanced: Standard threshold (2000)
            // LowImpact: Avoid parallelizing unless necessary or very large data
            switch (_mode)
            {
                case ExecutionMode.HighPerformance:
                    ParallelizationThreshold = 1000;
                    break;
                case ExecutionMode.Balanced:
                    ParallelizationThreshold = 2000; 
                    break;
                case ExecutionMode.LowImpact:
                    ParallelizationThreshold = 5000;
                    break;
                default:
                    ParallelizationThreshold = 2000;
                    break;
            }
        }

        /// <summary>
        /// Determines optimal chunk size for a specific data set.
        /// </summary>
        /// <param name="dataCount">Total number of items to process.</param>
        /// <returns>Recommended chunk size.</returns>
        public int GetOptimalChunkSize(int dataCount)
        {
            if (dataCount <= 0) return 0;

            // Strategy: Ensure enough work per task to justify TPL overhead
            // Minimum chunk size to avoid context switching domination
            const int MinChunkSize = 100;

            int split = dataCount / RecommendedParallelism;
            
            // If the split is too small, reduce parallelism implicitly by increasing chunk size
            return Math.Max(MinChunkSize, split);
        }
    }
}
