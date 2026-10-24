using System.Text;

namespace StockAnalyzer.McpServer;

/// <summary>The only writer of protocol output: one UTF-8 (no BOM) line per message, never interleaved, flushed at once.</summary>
internal sealed class ResponseWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _output;
    private readonly SemaphoreSlim _turn = new(1, 1);

    public ResponseWriter(Stream output)
    {
        _output = output;
    }

    public async Task WriteLineAsync(string message)
    {
        byte[] bytes = Utf8NoBom.GetBytes(message + "\n");
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }
}
