using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class ProbeOutputTests
    {
        [Fact]
        public async Task ReadBoundedAsync_KeepsOnlyTheFirstCharactersAndReadsToTheEnd()
        {
            var text = new string('a', 100) + new string('b', 5000);
            using var reader = new CountingReader(text);

            var kept = await ProbeOutput.ReadBoundedAsync(reader, 150, CancellationToken.None);

            Assert.Equal(new string('a', 100) + new string('b', 50), kept);
            Assert.Equal(text.Length, reader.CharactersRead);
        }

        [Fact]
        public async Task ReadBoundedAsync_ShortOutput_IsReturnedWhole()
        {
            using var reader = new StringReader("NO_UPDATE\n");

            Assert.Equal("NO_UPDATE\n", await ProbeOutput.ReadBoundedAsync(reader, ProbeOutputLimits.UpdateCheckKeptChars, CancellationToken.None));
        }

        [Fact]
        public async Task ReadBoundedAsync_KeepNothing_DrainsAndReturnsEmpty()
        {
            using var reader = new CountingReader(new string('x', 4000));

            Assert.Equal(string.Empty, await ProbeOutput.ReadBoundedAsync(reader, ProbeOutput.KeepNothing, CancellationToken.None));
            Assert.Equal(4000, reader.CharactersRead);
        }

        [Fact]
        public async Task ReadBoundedAsync_WhenCanceled_Throws()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var reader = new StringReader("text");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeOutput.ReadBoundedAsync(reader, 10, cts.Token));
        }

        /// <summary>A reader that counts what was consumed, to show the unkept rest is still read.</summary>
        private sealed class CountingReader : StringReader
        {
            public CountingReader(string text) : base(text) { }

            public int CharactersRead { get; private set; }

            public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = Read(buffer.Span);
                CharactersRead += read;
                return new ValueTask<int>(read);
            }
        }
    }
}
