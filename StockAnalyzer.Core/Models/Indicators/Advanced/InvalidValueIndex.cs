namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// The one prefix counter behind the window classes of this folder: after <see cref="Add"/> has been called once per bar in bar order, <see cref="IsClean"/> tells in O(1)
/// whether the bars [first, last] contain no invalid value. before[i] = number of invalid values among the indices 0 .. i-1.
/// What counts as invalid (a null price, a NaN return, a non-finite level) stays with the caller.
/// </summary>
internal sealed class InvalidValueIndex
{
    private readonly int[] _before;
    private int _count;

    public InvalidValueIndex(int capacity)
    {
        _before = new int[capacity + 1];
    }

    /// <summary>Appends the next bar; <paramref name="invalid"/> is true when its value is unusable.</summary>
    public void Add(bool invalid)
    {
        _before[_count + 1] = _before[_count] + (invalid ? 1 : 0);
        _count++;
    }

    /// <summary>True when none of the bars [<paramref name="first"/>, <paramref name="last"/>] is invalid.</summary>
    public bool IsClean(int first, int last) => _before[last + 1] - _before[first] == 0;
}
