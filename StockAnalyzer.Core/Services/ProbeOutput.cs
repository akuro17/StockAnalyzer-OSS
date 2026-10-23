using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// System contract of <see cref="ProbeOutput"/>: how much of a check process's output is kept. These are safety bounds of the code, not
    /// tunables, so no configuration file can change them.
    /// </summary>
    internal static class ProbeOutputLimits
    {
        /// <summary>Longest text kept from the output of the update check (a marker word or one error line).</summary>
        public const int UpdateCheckKeptChars = 4096;

        /// <summary>Longest text kept from the pip version read (a version number such as <c>26.2.2</c>).</summary>
        public const int PipVersionKeptChars = 64;

        /// <summary>Size of one read of a process stream.</summary>
        public const int ReadChunkChars = 1024;
    }

    /// <summary>
    /// Reads the output of a check process with a memory bound. The probes only need a marker line (or nothing), so the text kept is capped;
    /// the rest is still read and dropped, because a child whose pipe is not drained blocks and then runs into the time limit.
    /// </summary>
    internal static class ProbeOutput
    {
        /// <summary>Keep nothing: the output is drained only (the import check reads its exit code).</summary>
        public const int KeepNothing = 0;

        /// <summary>
        /// Reads <paramref name="reader"/> to its end and returns at most the first <paramref name="keptChars"/> characters. Cancellation
        /// through <paramref name="cancellationToken"/> is rethrown.
        /// </summary>
        public static async Task<string> ReadBoundedAsync(TextReader reader, int keptChars, CancellationToken cancellationToken)
        {
            var kept = new StringBuilder();
            var chunk = new char[ProbeOutputLimits.ReadChunkChars];
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var room = keptChars - kept.Length;
                if (room > 0)
                {
                    kept.Append(chunk, 0, Math.Min(read, room));
                }
            }

            return kept.ToString();
        }
    }
}
