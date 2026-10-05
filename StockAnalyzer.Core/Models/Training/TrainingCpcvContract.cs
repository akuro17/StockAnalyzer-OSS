using System;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Shape rules of an evaluation-only combinatorial purged cross-validation request (T14), mirrored by <c>dataset.py</c>
/// (<c>cpcv_combination_count</c> / <c>cpcv_combination</c>). Verified against Python by <c>Tests/CpcvParity.json</c>.
/// </summary>
/// <remarks>
/// Only the combination count <c>C(groups, testGroups)</c> is defined: no backtest path is constructed and no path formula
/// exists (decision 13-17=B), so the count must never be presented as a number of paths.
/// </remarks>
public static class TrainingCpcvContract
{
    /// <summary>Default calendar group count (<c>C(6,2) = 15</c> combinations).</summary>
    public const int DefaultGroups = 6;

    /// <summary>Default test groups held out per combination.</summary>
    public const int DefaultTestGroups = 2;

    /// <summary>Safety bound on the group count (code Limit, not configuration).</summary>
    public const int MaxGroups = 20;

    /// <summary>Safety bound on the combination count: every combination is a full training run.</summary>
    public const int MaxCombinations = 45;

    /// <summary>
    /// <c>C(groups, testGroups)</c> for <c>1 &lt;= testGroups &lt; groups &lt;= <see cref="MaxGroups"/></c>. The running value is compared with
    /// <see cref="MaxCombinations"/> after every step, so it never exceeds the cap before the next multiplication and no
    /// product can overflow; a request over the cap fails explicitly instead of being shrunk.
    /// </summary>
    public static bool TryCountCombinations(int groups, int testGroups, out int count, out string error)
    {
        count = 0;
        error = string.Empty;
        if (testGroups < 1 || testGroups >= groups)
        {
            error = "CPCV requires 1 <= test groups < groups";
            return false;
        }

        if (groups > MaxGroups)
        {
            error = $"CPCV groups must not exceed {MaxGroups}";
            return false;
        }

        var k = Math.Min(testGroups, groups - testGroups);
        long value = 1;
        for (var i = 1; i <= k; i++)
        {
            value = value * (groups - k + i) / i;
            if (value > MaxCombinations)
            {
                error = $"CPCV would need more than {MaxCombinations} combinations; reduce the groups or test groups";
                return false;
            }
        }

        count = (int)value;
        return true;
    }

    /// <summary>The combination count, or <see cref="InvalidOperationException"/> when the request is malformed or over the cap.</summary>
    public static int CombinationCount(int groups, int testGroups) =>
        TryCountCombinations(groups, testGroups, out var count, out var error)
            ? count : throw new InvalidOperationException($"TrainingJobConfig: {error}.");

    /// <summary>The 0-based <paramref name="index"/>-th test-group set in lexicographic order (the order the trainer enumerates).</summary>
    public static int[] Combination(int groups, int testGroups, int index)
    {
        var total = CombinationCount(groups, testGroups);
        if (index < 0 || index >= total)
            throw new ArgumentOutOfRangeException(nameof(index), "CPCV combination index is out of range.");
        var chosen = new int[testGroups];
        long remaining = index;
        var start = 0;
        for (var position = 0; position < testGroups; position++)
        {
            for (var candidate = start; candidate < groups; candidate++)
            {
                var block = Binomial(groups - candidate - 1, testGroups - position - 1);
                if (remaining < block)
                {
                    chosen[position] = candidate;
                    start = candidate + 1;
                    break;
                }

                remaining -= block;
            }
        }

        return chosen;
    }

    private static long Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);
        long value = 1;
        for (var i = 1; i <= k; i++) value = value * (n - k + i) / i; // n <= MaxGroups: no overflow
        return value;
    }
}
