namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// A stored data file exists but could not be read (for example while another process is rewriting it).
/// Not retried automatically; the caller reports it as transient.
/// </summary>
public sealed class DataUnavailableException : Exception
{
    public DataUnavailableException(string message) : base(message)
    {
    }
}
