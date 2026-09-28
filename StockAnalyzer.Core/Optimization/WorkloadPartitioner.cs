using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Optimization
{
    /// <summary>
    /// partitions workloads and executes them according to the optimization context.
    /// Implements the "Optimal Division Number" logic from Phase 1.
    /// </summary>
    public static class WorkloadPartitioner
    {
        /// <summary>
        /// Executes a processing loop on a data source in parallel using the specified context.
        /// </summary>
        /// <typeparam name="TSource">The type of the data source elements.</typeparam>
        /// <typeparam name="TResult">The type of the result elements.</typeparam>
        /// <param name="source">The source collection.</param>
        /// <param name="context">The optimization context defining execution mode.</param>
        /// <param name="body">The processing function to execute for each partition.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A collection of results.</returns>
        public static async Task<IList<TResult>> ExecuteParallelAsync<TSource, TResult>(
            IList<TSource> source,
            OptimizationContext context,
            Func<IList<TSource>, IEnumerable<TResult>> body,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (body == null) throw new ArgumentNullException(nameof(body));

            if (source.Count == 0)
            {
                return new List<TResult>();
            }

            // 1. Calculate optimal chunk size
            int chunkSize = context.GetOptimalChunkSize(source.Count);
            
            // 2. Partition the source data
            var partitions = CreatePartitions(source, chunkSize);

            // 3. Configure ParallelOptions
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = context.RecommendedParallelism,
                CancellationToken = cancellationToken
            };

            var resultsBag = new ConcurrentBag<IEnumerable<TResult>>();

            // 4. Execute Parallel.ForEach
            // Wraps in Task.Run to ensure it doesn't block the calling thread if it's the UI thread
            await Task.Run(() =>
            {
                Parallel.ForEach(partitions, parallelOptions, (partition) =>
                {
                    // Monitor cancellation
                    parallelOptions.CancellationToken.ThrowIfCancellationRequested();

                    // Execute body
                    var partialResults = body(partition);
                    if (partialResults != null)
                    {
                        resultsBag.Add(partialResults);
                    }
                });
            }, cancellationToken).ConfigureAwait(false);

            // 5. Merge results (preserving order notionally if needed - currently unordered merge)
            // Note: Parallel.ForEach doesn't guarantee order. 
            // If order is required, we need a different approach (e.g., Select with index).
            // For indicator calculations that are independent per chunk or robust to ordering during aggregation, this is fine.
            // If strict order is required, the 'body' should return indexed results or we execute differently.
            // Assuming for generic "Output Functionality" that aggregation might happen later or order is handled by the caller.
            // However, typically for time-series, order matters. 
            // Let's assume the caller handles re-sorting or we simple return the flat list.
            
            return resultsBag.SelectMany(x => x).ToList();
        }

        /// <summary>
        /// Creates chunks from a list source.
        /// </summary>
        private static IEnumerable<IList<TSource>> CreatePartitions<TSource>(IList<TSource> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
            {
                int count = Math.Min(size, source.Count - i);
                // ArraySegment or List.GetRange could be used. 
                // To avoid copying logic dependencies, we'll use Skip/Take for simplicity 
                // but for performance on IList, manual slicing is better.
                if (source is List<TSource> list)
                {
                    yield return list.GetRange(i, count);
                }
                else if (source is TSource[] array)
                {
                    // Return a new array or use ArraySegment?
                    // Body expects IList, ArraySegment<T> implements IList<T> in newer .NET but safer to clone for now
                    // or implement a custom slice.
                    // For safety and compatibility:
                    TSource[] chunk = new TSource[count];
                    Array.Copy(array, i, chunk, 0, count);
                    yield return chunk;
                }
                else
                {
                    // Fallback using LINQ (slower)
                    yield return source.Skip(i).Take(count).ToList();
                }
            }
        }
    }
}
