using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Wall-clock milliseconds of the phases of one external replay and the sizes they worked on, logged ONCE at Debug level. It measures only: no setting,
/// no telemetry stack, and a failing logger never changes the replay. The numbers are the evidence for a later decision on caching a session or an
/// episode, and for a benchmark on very long histories. No ticker text is recorded.
/// </summary>
internal sealed class RlReplayTimings
{
    internal const string Message = "RL external replay timings: rows={Rows} rawBytes={RawBytes} captureMs={CaptureMs:F1} calendarMs={CalendarMs:F1} "
        + "episodeMs={EpisodeMs:F1} modelLoadMs={ModelLoadMs:F1} steppingMs={SteppingMs:F1} steps={Steps} inferenceMs={InferenceMs:F1} "
        + "totalMs={TotalMs:F1} scope={Scope} findings={Findings}";

    public long Rows { get; set; }
    public long RawBytes { get; set; }
    public double CaptureMs { get; set; }
    public double CalendarMs { get; set; }
    public double EpisodeMs { get; set; }
    public double ModelLoadMs { get; set; }
    public double SteppingMs { get; set; }
    public int Steps { get; set; }
    public double InferenceMs { get; set; }

    /// <summary>The timestamp to pass to <see cref="Since"/> when the phase ends.</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    public static double Since(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    /// <summary>Logs the phases once. A logger that throws is ignored: measuring must never change or fail a replay.</summary>
    public void Log(ILogger logger, long replayStart, string scope, int findings)
    {
        try
        {
            if (!logger.IsEnabled(LogLevel.Debug)) return;
            logger.LogDebug(Message, Rows, RawBytes, CaptureMs, CalendarMs, EpisodeMs, ModelLoadMs, SteppingMs, Steps, InferenceMs,
                Since(replayStart), scope, findings);
        }
        catch (Exception)
        {
            // Deliberate: a diagnostic must not turn a successful replay into a failure.
        }
    }
}
