using System.Buffers;
using System.Text;

namespace StockAnalyzer.McpServer;

/// <summary>One input line, or the fact that a line was longer than the limit (its content is discarded).</summary>
internal readonly record struct RequestLine(string? Text, bool TooLong);

/// <summary>
/// Reads LF-terminated UTF-8 lines from a byte stream and caps the size of one line. A line over the cap is skipped up to its
/// line end and reported as <see cref="RequestLine.TooLong"/>, so the next line is read normally and memory use stays bounded.
/// </summary>
internal sealed class RequestLineReader
{
    private const int ReadChunkSize = 8192;

    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _chunk = new byte[ReadChunkSize];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;

    public RequestLineReader(Stream stream, int maxLineBytes)
    {
        _stream = stream;
        _maxLineBytes = maxLineBytes;
    }

    /// <summary>The next line without its LF (and a preceding CR), or null at the end of the stream with nothing pending.</summary>
    public async ValueTask<RequestLine?> ReadAsync(CancellationToken cancellationToken)
    {
        bool tooLong = false;
        while (true)
        {
            if (_start == _end)
            {
                _end = await _stream.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                _start = 0;
                if (_end == 0)
                {
                    return tooLong ? Take(true) : _line.WrittenCount == 0 ? null : Take(false);
                }
            }

            int newline = Array.IndexOf(_chunk, (byte)'\n', _start, _end - _start);
            int count = newline < 0 ? _end - _start : newline - _start;
            if (!tooLong)
            {
                if (_line.WrittenCount + count > _maxLineBytes)
                {
                    tooLong = true;
                    _line.Clear();
                }
                else
                {
                    _line.Write(_chunk.AsSpan(_start, count));
                }
            }

            if (newline >= 0)
            {
                _start = newline + 1;
                return Take(tooLong);
            }

            _start = _end;
        }
    }

    private RequestLine Take(bool tooLong)
    {
        if (tooLong)
        {
            _line.Clear();
            return new RequestLine(null, true);
        }

        ReadOnlySpan<byte> span = _line.WrittenSpan;
        if (span.Length > 0 && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        string text = Encoding.UTF8.GetString(span);
        _line.Clear();
        return new RequestLine(text, false);
    }
}
