using System.Text.Json;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>What the execution tools share: the mapping of the gateway's exceptions to tool error codes, and the names of the job arguments.</summary>
internal static class ExecutionToolSupport
{
    public const string JobIdArgument = "jobId";
    public const string ArtifactIdArgument = "artifactId";

    /// <summary>The only hint of PYTHON_ENV_MISSING: a manual command would carry a path.</summary>
    public const string PythonEnvironmentHint = "Start StockAnalyzer once to install the embedded Python.";

    /// <summary>The only hint of PYTHON_ENV_MISSING for reinforcement learning: it uses the application's managed training environment, not the embedded Python, and a command would carry a path.</summary>
    public const string RlEnvironmentHint = "Set up the application's training Python environment (the one the RL window uses), then retry.";

    /// <summary>The only hint of SANDBOX_PATH_TOO_LONG: the cause is the temporary folder of the server process, which only the client's launch settings change (no path is named).</summary>
    public const string PathTooLongHint = "Set the TEMP and TMP environment variables of the server process to a short folder, then restart the server.";

    /// <summary>The argument of a call that names no member of the request (the second kind of rule failure names none).</summary>
    private const string WholeRequestParameter = "request";

    /// <summary>The tool error for an exception the gateway documents; false for any other exception (it stays an unexpected error).</summary>
    public static bool TryMap(Exception error, out ToolException mapped)
    {
        mapped = error switch
        {
            ExecutionRequestInvalidException invalid => new ToolException(
                ToolErrorCodes.InvalidArgument, invalid.Message, invalid.ParameterName is null ? WholeRequestParameter : CamelCase(invalid.ParameterName)),
            ExecutionBusyException => new ToolException(
                ToolErrorCodes.ExecutionBusy, "The allowed number of training jobs is already running.", hint: "Poll sa_get_training_status until a job ends, or stop one with sa_cancel_training."),
            JobNotFoundException => new ToolException(
                ToolErrorCodes.JobNotFound, "No training job has this id.", JobIdArgument, "The jobs of a previous session are gone; start a new job with sa_start_training."),
            ArtifactNotFoundException => new ToolException(
                ToolErrorCodes.ArtifactNotFound, "No such model: the job has not succeeded, or its files were removed.", ArtifactIdArgument,
                "Use the artifactId of a succeeded job from sa_get_training_status, or 'latest'."),
            PythonEnvironmentMissingException => new ToolException(
                ToolErrorCodes.PythonEnvMissing, "The embedded Python interpreter is not installed.", hint: PythonEnvironmentHint),
            RlEnvironmentMissingException rl => new ToolException(
                ToolErrorCodes.PythonEnvMissing,
                rl.MissingModule is null
                    ? "The managed Python environment for reinforcement learning is not installed."
                    : $"The managed Python environment for reinforcement learning cannot import the Python module '{rl.MissingModule}'.",
                hint: RlEnvironmentHint),
            ExecutionPathTooLongException tooLong => new ToolException(
                ToolErrorCodes.SandboxPathTooLong,
                $"The temporary folder of the server is too deep: the files of a job would need paths of {tooLong.RequiredChars} characters, and the trainer cannot open paths of {ExecutionLimits.PathCharsLimit} characters or more.",
                hint: PathTooLongHint),
            InsufficientStoredDataException insufficient => new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                insufficient.AvailableBars == 0
                    ? "The symbol has no usable stored daily bars (a stored equity with its metadata is required)."
                    : $"The stored daily data of the symbol is not enough: {insufficient.RequiredBars} bars are required, {insufficient.AvailableBars} are stored.",
                "symbol",
                "Update the stored data of the symbol, or choose another symbol."),
            _ => null!,
        };
        return mapped is not null;
    }

    /// <summary>Runs <paramref name="action"/>; a documented gateway exception becomes the matching <see cref="ToolException"/>.</summary>
    public static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception error) when (TryMap(error, out ToolException mapped))
        {
            throw mapped;
        }
    }

    /// <summary>A job id must have the shape the gateway makes (<see cref="TrainingRunId"/>); anything else is a malformed argument, not an unknown job.</summary>
    public static string JobId(JsonElement arguments)
    {
        string jobId = ToolArgs.String(arguments, JobIdArgument)!;
        if (!TrainingRunId.IsValid(jobId))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{JobIdArgument}' is not a job id.", JobIdArgument, "Use the jobId returned by sa_start_training.");
        }

        return jobId;
    }

    public static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
