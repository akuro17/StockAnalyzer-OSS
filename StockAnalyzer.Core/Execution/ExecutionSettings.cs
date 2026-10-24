using System;

namespace StockAnalyzer.Core.Execution;

/// <summary>A configured execution setting is outside its allowed band (checked at start-up).</summary>
public sealed class ExecutionLimitsException(string message) : Exception(message);

/// <summary>
/// The tunable settings of the sandboxed execution gateway, bound from the <c>AiIntegration:Execution</c> configuration section. The
/// initialisers are the standard values; the bands they must stay inside are in <see cref="ExecutionLimits"/> and are checked at start-up.
/// </summary>
public sealed class SandboxedExecutionSettings
{
    public const string SectionName = "AiIntegration:Execution";

    /// <summary>How many successful jobs keep their artifacts (the newest ones).</summary>
    public int KeepLatestRuns { get; set; } = 1;

    /// <summary>How old (hours since the last write) the folder of another session must be before the start-up clean-up may delete it.</summary>
    public int OrphanSessionMaxAgeHours { get; set; } = 24;

    /// <summary>How many jobs run at the same time; a further start is refused as busy.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>How many of the latest messages a job status carries.</summary>
    public int StatusTailMessages { get; set; } = 20;

    /// <summary>How many consecutive bars one ONNX prediction call may cover; the upper bound is the host's candle limit (the host checks it).</summary>
    public int MaxPredictionBars { get; set; } = 64;
}

/// <summary>The safety contract of the execution settings: hard bands for the tunable values and the fixed values of the gateway.</summary>
public static class ExecutionLimits
{
    public const int MinKeepLatestRuns = 1;
    public const int MaxKeepLatestRuns = 8;
    public const int MinOrphanSessionMaxAgeHours = 1;
    public const int MaxOrphanSessionMaxAgeHours = 168;
    public const int MinConcurrentJobs = 1;
    public const int MaxConcurrentJobsAllowed = 2;
    public const int MinStatusTailMessages = 0;
    public const int MaxStatusTailMessagesAllowed = 100;
    public const int MinPredictionBars = 1;

    /// <summary>Seconds to wait for a cancelled training process to stop before the job is reported as cancelled anyway.</summary>
    public const int CancelGraceSeconds = 10;

    /// <summary>Largest number of jobs the in-memory job table keeps; the oldest finished job is dropped first.</summary>
    public const int MaxTrackedJobs = 64;

    /// <summary>
    /// Seconds the check of the reinforcement-learning environment (one import of its packages) may take before the environment counts as unusable. Measured
    /// 3.1-3.2 s on this machine; the margin is for a cold disk, and it stays below the 30-second default tool timeout. The tool timeout is configurable
    /// (<c>McpLimits.ToolTimeoutSeconds</c>, 1 to 300) and is not checked against this value: a call that is cut off by a shorter tool timeout stops waiting only,
    /// the probe goes on and its answer is shared by the next start (see <c>RlEnvironmentProbe</c>).
    /// </summary>
    public const int RlEnvironmentProbeSeconds = 20;

    /// <summary>Longest part of the probe's error text that is kept, in characters; the rest is read and dropped.</summary>
    public const int RlProbeStandardErrorChars = 8 * 1024;

    /// <summary>Longest module name the probe reports, in characters; a longer one is not reported.</summary>
    public const int RlProbeModuleNameChars = 64;

    /// <summary>How many session ids are tried before the sandbox gives up finding a free session folder.</summary>
    public const int SessionIdAttempts = 8;

    /// <summary>How many job ids are tried before a start gives up finding a free job folder.</summary>
    public const int JobIdAttempts = 5;

    /// <summary>Longest status message, in characters.</summary>
    public const int MaxStatusMessageChars = 200;

    /// <summary>Longest stage name, in characters.</summary>
    public const int MaxStageChars = 64;

    /// <summary>
    /// A path of this many characters or more cannot be opened by the trainer's Python on Windows without the long-path setting, which the gateway never
    /// relies on. Measured 2026-10-24 at the boundary: a supervised job under a 139-character job folder succeeded, under 140 it failed with WinError 3.
    /// </summary>
    public const int PathCharsLimit = 260;

    /// <summary>
    /// Length of the longest path a supervised job creates below its job folder (without the separator that joins them): the checkpoint blob
    /// <c>checkpoints\&lt;run id, 27&gt;\fold_N\&lt;sha-256, 64&gt;.lgb.txt</c> (a PyTorch blob ends in the shorter <c>.pt</c>). N has one digit because a job has at most
    /// <see cref="Models.Training.TrainingResourceOverrides.MaximumEvaluationFolds"/> folds. The symbol is not part of any name. The opt-in real run asserts this value.
    /// </summary>
    public const int SupervisedLongestRelativePathChars = 119;

    /// <summary>
    /// Length of the longest path a reinforcement-learning smoke job creates below its job folder: <c>TrainingArtifacts\RL\&lt;run, 32&gt;\RlSnapshots\metadata.parquet</c>
    /// and the temporary <c>policy.onnx.metrics.json.tmp</c> beside the run files. Measured 2026-10-24 with a file-system watcher, so temporary names count;
    /// a job under a 176-character job folder succeeded, under 177 it failed. The opt-in real run asserts this value.
    /// </summary>
    public const int RlLongestRelativePathChars = 82;

    /// <exception cref="ExecutionLimitsException">A value is outside its band.</exception>
    public static void Validate(SandboxedExecutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Band(nameof(SandboxedExecutionSettings.KeepLatestRuns), settings.KeepLatestRuns, MinKeepLatestRuns, MaxKeepLatestRuns);
        Band(nameof(SandboxedExecutionSettings.OrphanSessionMaxAgeHours), settings.OrphanSessionMaxAgeHours, MinOrphanSessionMaxAgeHours, MaxOrphanSessionMaxAgeHours);
        Band(nameof(SandboxedExecutionSettings.MaxConcurrentJobs), settings.MaxConcurrentJobs, MinConcurrentJobs, MaxConcurrentJobsAllowed);
        Band(nameof(SandboxedExecutionSettings.StatusTailMessages), settings.StatusTailMessages, MinStatusTailMessages, MaxStatusTailMessagesAllowed);
        if (settings.MaxPredictionBars < MinPredictionBars)
        {
            throw new ExecutionLimitsException($"{SandboxedExecutionSettings.SectionName}:{nameof(SandboxedExecutionSettings.MaxPredictionBars)} is {settings.MaxPredictionBars}; it must be at least {MinPredictionBars}.");
        }

        // Jobs that run at once each leave a model; fewer kept models than running jobs would delete the first one to finish before anything could use it.
        if (settings.KeepLatestRuns < settings.MaxConcurrentJobs)
        {
            throw new ExecutionLimitsException(
                $"{SandboxedExecutionSettings.SectionName}:{nameof(SandboxedExecutionSettings.KeepLatestRuns)} is {settings.KeepLatestRuns}; it must be at least {nameof(SandboxedExecutionSettings.MaxConcurrentJobs)} ({settings.MaxConcurrentJobs}).");
        }
    }

    private static void Band(string name, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new ExecutionLimitsException($"{SandboxedExecutionSettings.SectionName}:{name} is {value}; it must be {minimum} to {maximum}.");
        }
    }
}
