using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Reads LF-terminated UTF-8 lines from a byte stream while capping the size of one line, so a runaway or
    /// corrupted peer cannot exhaust memory. Bytes read past a line stay buffered for the next call.
    /// </summary>
    internal sealed class BoundedLineReader
    {
        private const int ReadChunkSize = 8192;

        private readonly Stream _stream;
        private readonly int _maxLineBytes;
        private readonly byte[] _chunk = new byte[ReadChunkSize];
        private readonly ArrayBufferWriter<byte> _line = new();
        private int _start;
        private int _end;

        public BoundedLineReader(Stream stream, int maxLineBytes)
        {
            _stream = stream;
            _maxLineBytes = maxLineBytes;
        }

        /// <summary>
        /// Returns the next line without its LF (and a preceding CR), or <c>null</c> at end of stream with nothing pending.
        /// </summary>
        /// <exception cref="PythonResponseTooLargeException">The line exceeds the configured maximum.</exception>
        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_start == _end)
                {
                    _end = await _stream.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _start = 0;
                    if (_end == 0)
                    {
                        return _line.WrittenCount == 0 ? null : TakeLine();
                    }
                }

                int newline = Array.IndexOf(_chunk, (byte)'\n', _start, _end - _start);
                int count = newline < 0 ? _end - _start : newline - _start;
                if (_line.WrittenCount + count > _maxLineBytes)
                {
                    throw new PythonResponseTooLargeException(_maxLineBytes);
                }

                _line.Write(_chunk.AsSpan(_start, count));
                if (newline >= 0)
                {
                    _start = newline + 1;
                    return TakeLine();
                }

                _start = _end;
            }
        }

        private string TakeLine()
        {
            var span = _line.WrittenSpan;
            if (span.Length > 0 && span[^1] == (byte)'\r')
            {
                span = span[..^1];
            }

            string text = Encoding.UTF8.GetString(span);
            _line.ResetWrittenCount();
            return text;
        }
    }
}
