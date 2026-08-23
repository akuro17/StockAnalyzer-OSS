using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Translates screening conditions into SQL WHERE clauses for DuckDB.
/// </summary>
public static class SqlConditionTranslator
{
    public static string BuildBatchQuery(string parquetPattern, ScreeningCriteria criteria)
    {
        var indicatorCols = new List<string>();
        var filterConditions = new List<string>();

        for (int i = 0; i < criteria.Conditions.Count; i++)
        {
            var cond = criteria.Conditions[i];
            var colName = $"indicator_{i}";
            indicatorCols.Add($"{TranslateToColumn(cond)} as {colName}");
            filterConditions.Add($"{colName} = true");
        }

        var selectList = indicatorCols.Count > 0 ? ", " + string.Join(", ", indicatorCols) : "";
        var whereClause = filterConditions.Count > 0 ? string.Join(" AND ", filterConditions) : "1=1";

        // Single pass query:
        // 1. Load data and calculate diff for RSI
        // 2. Calculate all indicators in parallel
        // 3. Filter for latest row and meet condition
        var safePattern = parquetPattern.Replace("\\", "/").Replace("'", "''");
        return $@"
            WITH raw_data AS (
                SELECT *, 
                       Close - LAG(Close) OVER (PARTITION BY filename ORDER BY date) as _diff
                FROM read_parquet('{safePattern}', filename=true)
            ),
            calculated AS (
                SELECT 
                    filename,
                    date,
                    {string.Join(", ", indicatorCols)}
                FROM raw_data
            )
            SELECT DISTINCT ticker
            FROM (
                SELECT 
                    replace(regexp_replace(filename, '.*[\\\\/]', ''), '.parquet', '') as ticker,
                    {string.Join(", ", filterConditions.Select(c => c.Split(' ')[0]))}
                FROM calculated
                QUALIFY row_number() OVER (PARTITION BY filename ORDER BY date DESC) = 1
            )
            WHERE {whereClause}";
    }

    private static string TranslateToColumn(IScreeningCondition condition)
    {
        return condition switch
        {
            RsiOversoldCondition rsi => TranslateRsiToColumn(rsi),
            _ => throw new NotSupportedException($"Condition type {condition.GetType().Name} is not yet supported for batch column translation.")
        };
    }

    private static string TranslateRsiToColumn(RsiOversoldCondition condition)
    {
        var periodField = typeof(RsiOversoldCondition).GetField("_period", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var thresholdField = typeof(RsiOversoldCondition).GetField("_threshold", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        int period = (int)(periodField?.GetValue(condition) ?? 14);
        decimal threshold = (decimal)(thresholdField?.GetValue(condition) ?? 30m);

        // Calculate RSI using window functions on the _diff column from raw_data
        return $@"
            (
                100 - (100 / (1 + 
                    (AVG(CASE WHEN _diff > 0 THEN _diff ELSE 0 END) OVER (PARTITION BY filename ORDER BY date ROWS BETWEEN {period - 1} PRECEDING AND CURRENT ROW)) / 
                    NULLIF(AVG(CASE WHEN _diff < 0 THEN -_diff ELSE 0 END) OVER (PARTITION BY filename ORDER BY date ROWS BETWEEN {period - 1} PRECEDING AND CURRENT ROW), 0)
                ))
            ) < {threshold}";
    }

    public static string Translate(ScreeningCriteria criteria)
    {
        if (criteria.Conditions == null || criteria.Conditions.Count == 0)
        {
            return "1=1"; // Match all
        }

        var sb = new StringBuilder();
        for (int i = 0; i < criteria.Conditions.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append(TranslateCondition(criteria.Conditions[i]));
        }

        return sb.ToString();
    }

    private static string TranslateCondition(IScreeningCondition condition)
    {
        return condition switch
        {
            RsiOversoldCondition rsi => TranslateRsi(rsi),
            _ => throw new NotSupportedException($"Condition type {condition.GetType().Name} is not yet supported for SQL translation.")
        };
    }

    private static string TranslateRsi(RsiOversoldCondition condition)
    {
        var periodField = typeof(RsiOversoldCondition).GetField("_period", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var thresholdField = typeof(RsiOversoldCondition).GetField("_threshold", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        int period = (int)(periodField?.GetValue(condition) ?? 14);
        decimal threshold = (decimal)(thresholdField?.GetValue(condition) ?? 30m);

        // Single file mode (original implementation)
        return $@"
        (
            WITH rsi_data AS (
                SELECT 
                    Close,
                    row_number() OVER() as rn,
                    Close - LAG(Close) OVER() as diff
                FROM current_file
            ),
            g_l AS (
                SELECT 
                    rn,
                    CASE WHEN diff > 0 THEN diff ELSE 0 END as gain,
                    CASE WHEN diff < 0 THEN -diff ELSE 0 END as loss
                FROM rsi_data
            ),
            a_gl AS (
                SELECT 
                    AVG(gain) OVER (ORDER BY rn ROWS BETWEEN {period - 1} PRECEDING AND CURRENT ROW) as ag,
                    AVG(loss) OVER (ORDER BY rn ROWS BETWEEN {period - 1} PRECEDING AND CURRENT ROW) as al
                FROM g_l
            )
            SELECT (100 - (100 / (1 + ag / NULLIF(al, 0)))) 
            FROM a_gl 
            ORDER BY 1 DESC 
            LIMIT 1
        ) < {threshold}";
    }
}
