using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Shared RFC4180-style CSV field escaping and row writing, used by every CSV export feature in
/// the app (Ticker Code export and Column Data export share this instead of duplicating escaping
/// logic per feature). Line endings and BOM-free UTF-8 follow this project's own cross-platform
/// text-file standard (SA_ARCHITECTURE_RULES.md "Standardized Line Endings & BOM-Free UTF-8"), not
/// RFC4180's CRLF default - this export's own stated purpose includes re-import into external/Python
/// tooling, where a stray BOM or CRLF is a real interop hazard. Both write paths write to a
/// temporary sibling file first, then atomically replace the target - a partially-written file is
/// never left at the user-chosen path.
/// </summary>
public static class CsvExportWriter
{
    private const string RecordSeparator = "\n";
    private static readonly System.Text.UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Quotes a field if it contains a comma, quote, or newline; doubles any embedded quotes.
    /// Returns an empty string for null.
    /// </summary>
    public static string EscapeField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        bool needsQuoting = value.IndexOfAny(_specialChars) >= 0;
        if (!needsQuoting) return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static readonly char[] _specialChars = { ',', '"', '\r', '\n' };
    private static readonly char[] _formulaTriggerChars = { '=', '+', '-', '@', '\t', '\r' };

    /// <summary>
    /// Formats an arbitrary raw column value (decimal/double/long/DateTime/bool/string/null) using
    /// invariant culture, then escapes it. Never uses current-UI-culture formatting so exported
    /// numeric precision and decimal separators stay stable regardless of the app's locale.
    /// <see cref="DateTime"/>/<see cref="DateTimeOffset"/> use the round-trip ("O"/ISO 8601) format
    /// instead of the generic <see cref="IFormattable"/> fallback, which under InvariantCulture is
    /// still a culture-shaped general date string, not ISO 8601. A non-finite <see cref="double"/>/
    /// <see cref="float"/> (NaN/Infinity) is treated the same as null (empty cell) - most CSV
    /// consumers, including this app's own re-import path, cannot parse the literal words
    /// "NaN"/"Infinity". A <see cref="string"/> source value that starts with a character a
    /// spreadsheet application would interpret as a formula trigger (=, +, -, @, tab, CR) gets a
    /// leading apostrophe, the standard Excel/OWASP-recommended CSV-injection neutralization; this
    /// only applies to genuinely string-typed columns (e.g. Name/Sector/Tag) - a numeric value that
    /// happens to be negative is never affected, since it never reaches this branch.
    /// </summary>
    public static string FormatAndEscapeValue(object? value)
    {
        if (value is null) return string.Empty;

        string raw = value switch
        {
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            double d when !double.IsFinite(d) => string.Empty,
            float f when !float.IsFinite(f) => string.Empty,
            string s => NeutralizeFormulaPrefix(s),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return EscapeField(raw);
    }

    private static string NeutralizeFormulaPrefix(string value)
    {
        if (value.Length == 0) return value;
        return Array.IndexOf(_formulaTriggerChars, value[0]) >= 0 ? "'" + value : value;
    }

    /// <summary>
    /// Writes comma-joined, LF-terminated rows to <paramref name="filePath"/> atomically (see
    /// class remarks).
    /// </summary>
    public static async Task WriteRowsAsync(string filePath, IEnumerable<string[]> rows, CancellationToken ct = default)
    {
        await WriteAtomicallyAsync(filePath, async writer =>
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) await writer.WriteAsync(',');
                    await writer.WriteAsync(row[i]);
                }
                await writer.WriteAsync(RecordSeparator);
            }
        });
    }

    /// <summary>
    /// Writes one line per entry (LF-terminated, no delimiter/escaping) to <paramref name="filePath"/>
    /// atomically (see class remarks) - used by the headerless Ticker Codes export.
    /// </summary>
    public static async Task WriteLinesAsync(string filePath, IEnumerable<string> lines, CancellationToken ct = default)
    {
        await WriteAtomicallyAsync(filePath, async writer =>
        {
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(line);
                await writer.WriteAsync(RecordSeparator);
            }
        });
    }

    private static async Task WriteAtomicallyAsync(string filePath, Func<StreamWriter, Task> writeBody)
    {
        var tempPath = filePath + ".tmp";
        try
        {
            await using (var writer = new StreamWriter(tempPath, append: false, _utf8NoBom))
            {
                await writeBody(writer);
            }

            // Same branching as StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync (this project's
            // established atomic-write pattern): File.Replace requires the destination to already
            // exist and offers stronger atomicity guarantees than File.Move, so it is the primary
            // path for overwriting an existing export; File.Move only handles the brand-new-file
            // case, where File.Replace would otherwise throw FileNotFoundException.
            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup only */ }
            }
            throw;
        }
    }
}
