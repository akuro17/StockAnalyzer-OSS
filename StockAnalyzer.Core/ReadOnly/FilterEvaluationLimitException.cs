namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Thrown when the number of parent symbols to evaluate in a filter exceeds MaxFilterEvaluationSymbols.
/// </summary>
public sealed class FilterEvaluationLimitException : Exception
{
    public int Count { get; }
    public int Limit { get; }

    public FilterEvaluationLimitException(int count, int limit)
        : base($"Filter evaluation requires {count} symbols, which exceeds the limit of {limit}.")
    {
        Count = count;
        Limit = limit;
    }
}
