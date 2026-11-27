using System;
using System.Diagnostics;
using System.Globalization;

namespace StockAnalyzer.Core.Services;

/// <summary>The environment variables every Python child process of the application is started with (names and values in one place).</summary>
internal static class PythonChildEnvironment
{
    public const string OmpNumThreadsVariable = "OMP_NUM_THREADS";
    public const string OpenBlasNumThreadsVariable = "OPENBLAS_NUM_THREADS";
    public const string MklNumThreadsVariable = "MKL_NUM_THREADS";
    public const string PolarsMaxThreadsVariable = "POLARS_MAX_THREADS";
    public const string SingleThreadValue = "1";
    public const int MinPolarsThreads = 1;
    public const int MaxInteractivePolarsThreads = 4;
    public const int MinTrainingThreads = 1;
    public const int MaxTrainingThreads = 8;

    /// <summary>Makes the child write its standard streams as UTF-8, which is what the readers of the application decode.</summary>
    public const string IoEncodingVariable = "PYTHONIOENCODING";

    public const string IoEncodingValue = "utf-8";

    /// <summary>Stops the interpreter from writing <c>__pycache__</c> folders next to the scripts it imports.</summary>
    public const string NoBytecodeVariable = "PYTHONDONTWRITEBYTECODE";

    public const string NoBytecodeValue = "1";

    /// <summary>
    /// Thread profile for persistent interactive/sync Python workers: caps native BLAS/OpenMP at 1 and Polars at [1, 4]
    /// so background sync does not contend with UI/Skia rendering. Applies only variables not already set.
    /// </summary>
    public static void ApplyInteractiveWorkerThreadCaps(ProcessStartInfo startInfo, int? processorCountOverride = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        int cores = Math.Max(MinPolarsThreads, processorCountOverride ?? Environment.ProcessorCount);
        string polarsThreads = Math.Clamp(cores / 2, MinPolarsThreads, MaxInteractivePolarsThreads).ToString(CultureInfo.InvariantCulture);

        SetIfAbsent(startInfo, OmpNumThreadsVariable, SingleThreadValue);
        SetIfAbsent(startInfo, OpenBlasNumThreadsVariable, SingleThreadValue);
        SetIfAbsent(startInfo, MklNumThreadsVariable, SingleThreadValue);
        SetIfAbsent(startInfo, PolarsMaxThreadsVariable, polarsThreads);
    }

    /// <summary>
    /// Thread profile for background training workers (LightGBM / PyTorch): reserves 2 cores for UI rendering and caps native
    /// BLAS/OpenMP/Polars threads at <c>clamp(cores - 2, 1, 8)</c> to avoid single-thread training regression.
    /// Does not modify <c>RlWorkerProcess</c> (<c>RlContract.CpuThreads = 1</c>).
    /// </summary>
    public static void ApplyTrainingWorkerThreadCaps(ProcessStartInfo startInfo, int? processorCountOverride = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        int cores = Math.Max(MinTrainingThreads, processorCountOverride ?? Environment.ProcessorCount);
        string threads = Math.Clamp(
                cores - Optimization.HardwareConcurrencyPolicy.DefaultReservedCores,
                MinTrainingThreads,
                MaxTrainingThreads)
            .ToString(CultureInfo.InvariantCulture);

        SetIfAbsent(startInfo, OmpNumThreadsVariable, threads);
        SetIfAbsent(startInfo, OpenBlasNumThreadsVariable, threads);
        SetIfAbsent(startInfo, MklNumThreadsVariable, threads);
        SetIfAbsent(startInfo, PolarsMaxThreadsVariable, threads);
    }

    private static void SetIfAbsent(ProcessStartInfo startInfo, string name, string value)
    {
        if (!startInfo.Environment.ContainsKey(name))
        {
            startInfo.Environment[name] = value;
        }
    }
}
