namespace StockAnalyzer.McpServer;

/// <summary>Diagnostic output. The only destination is the writer it is given (stderr in production); never stdout.</summary>
internal sealed class LogSink
{
    private readonly TextWriter _writer;
    private readonly bool _quiet;

    /// <param name="quiet">Drop the information lines (errors are still written); for commands that must print nothing else, such as the help.</param>
    public LogSink(TextWriter writer, bool quiet = false)
    {
        _writer = writer;
        _quiet = quiet;
    }

    public void Info(string message)
    {
        if (!_quiet)
        {
            Write("INFO", message);
        }
    }

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (_writer)
        {
            _writer.WriteLine($"[{typeof(LogSink).Assembly.GetName().Name}] {level} {message}");
        }
    }
}
