using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// The one definition of the run identifier format, <c>yyyyMMdd-HHmmssfff-{8 lower-case hex}</c>. The orchestrator creates the identifier of a normal run
/// with it, and the sandboxed execution gateway creates the job id of an ephemeral run with it (the job id is the run id, see
/// <see cref="TrainingJobConfig.RunId"/>).
/// </summary>
public static class TrainingRunId
{
    private const string TimestampFormat = "yyyyMMdd-HHmmssfff";
    private const int SuffixLength = 8;

    private static readonly Regex Pattern = new(@"\A\d{8}-\d{9}-[0-9a-f]{8}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A new identifier: the UTC time of <paramref name="utc"/> to the millisecond, then the first eight hex digits of a new GUID, so two runs in the same millisecond still differ.</summary>
    public static string New(DateTimeOffset utc)
        => $"{utc.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..SuffixLength]}";

    /// <summary>True when <paramref name="value"/> has exactly the format <see cref="New"/> produces.</summary>
    public static bool IsValid(string? value) => value is not null && Pattern.IsMatch(value);
}
