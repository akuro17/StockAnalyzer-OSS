namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Centralized DuckDB SQL expression to extract the ticker symbol from the parquet filename.
/// Strips directory path and .parquet extension.
/// </summary>
internal static class DuckDbSymbolSql
{
    internal const string Expression = "replace(regexp_replace(filename, '.*[\\\\/]', ''), '.parquet', '')";
}
