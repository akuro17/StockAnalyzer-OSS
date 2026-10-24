using System.Text;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Limits;

// The arguments are read before stdout is redirected: --version and a usage error never start a session.
McpCommandLineResult commandLine = McpCommandLine.Parse(args);
if (commandLine.Command is McpCommand.Invalid invalid)
{
    Console.Error.WriteLine(invalid.Message);
    return McpCommandLine.ExitInvalidUsage;
}

// stdout carries protocol messages only: keep the real stream for the transport and send every other
// Console.Out write (including stray ones from library code) to stderr.
Stream protocolOutput = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

if (commandLine.Command is McpCommand.Version)
{
    WriteText(protocolOutput, McpHelpText.Version());
    return McpCommandLine.ExitOk;
}

// The help builds the tool registry like a session does, but stays silent (its stdout is the help text, its stderr empty).
var log = new LogSink(Console.Error, quiet: commandLine.Command is McpCommand.Help);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

McpServerRuntime runtime;
try
{
    runtime = McpServerComposition.Create(McpServerComposition.LoadConfiguration(AppContext.BaseDirectory), log, enableExecution: commandLine.EnableExecution);
}
catch (Exception error) when (error is McpLimitsException or ExecutionLimitsException)
{
    log.Error($"Invalid configuration: {error.Message}");
    return McpCommandLine.ExitInvalidConfiguration;
}

using (runtime)
{
    if (commandLine.Command is McpCommand.Help help)
    {
        if (help.ToolName is null)
        {
            WriteText(protocolOutput, McpHelpText.Overview(runtime.Tools, commandLine.EnableExecution));
            return McpCommandLine.ExitOk;
        }

        if (!McpHelpText.TryTool(runtime.Tools, commandLine.EnableExecution, help.ToolName, out string detail, out string toolError))
        {
            Console.Error.WriteLine(toolError);
            return McpCommandLine.ExitInvalidUsage;
        }

        WriteText(protocolOutput, detail);
        return McpCommandLine.ExitOk;
    }

    // The execution gateway (and its start-up clean-up) is built before the first request, never for the help.
    try
    {
        runtime.PrepareExecution();
    }
    catch (Exception error)
    {
        // The server keeps serving the read-only tools; each execution call then ends as an unexpected error.
        log.Error($"The execution gateway could not start: {error.GetType().Name}");
    }

    return await McpServerHost.RunAsync(Console.OpenStandardInput(), protocolOutput, runtime.Dispatcher, runtime.Limits.MaxRequestBytes, log, cancellation.Token);
}

static void WriteText(Stream destination, string text)
{
    byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    destination.Write(bytes, 0, bytes.Length);
    destination.Flush();
}
