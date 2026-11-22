using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// High-performance implementation of ITickerImportService using Span-based parsing.
/// </summary>
public class TickerImportService : ITickerImportService
{
    private readonly ILogger<TickerImportService> _logger;

    private const string InvalidTickerSkippedMessage = "Invalid ticker format skipped: {Token}";

    private static readonly char[] Delimiters = [',', '\t', ' '];

    public TickerImportService(ILogger<TickerImportService>? logger = null)
    {
        _logger = logger ?? NullLogger<TickerImportService>.Instance;
    }

    public async Task<IReadOnlyList<string>> ImportTickersAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        var uniqueTickers = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            ParseLine(line.AsSpan(), uniqueTickers);
        }

        return uniqueTickers.ToList().AsReadOnly();
    }

    private void ParseLine(ReadOnlySpan<char> span, HashSet<string> result)
    {
        ReadOnlySpan<char> remaining = span.Trim();

        while (!remaining.IsEmpty)
        {
            int delimiterIndex = remaining.IndexOfAny(Delimiters);
            ReadOnlySpan<char> token;

            if (delimiterIndex == -1)
            {
                token = remaining;
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                token = remaining.Slice(0, delimiterIndex);
                remaining = remaining.Slice(delimiterIndex + 1).TrimStart(Delimiters);
            }

            if (token.IsEmpty) continue;

            ProcessToken(token, result);
        }
    }

    private void ProcessToken(ReadOnlySpan<char> token, HashSet<string> result)
    {
        if (token.Length > StockAnalyzer.Core.Models.TickerSymbolLimits.ImportMaxLength || token.IsEmpty) return;

        // Use stackalloc for normalization to avoid intermediate string allocations (Rule #11)
        Span<char> normalized = stackalloc char[token.Length];

        // Only ASCII is a symbol character: the canonical form (upper case, '.' to '-') is built by the one definition, which refuses a non-ASCII character before any case mapping
        // (upper-casing first would map U+017F to 'S', and the four-digit code pattern accepts any Unicode digit).
        if (!StockAnalyzer.Core.Models.TickerSymbol.TryCanonicalizeAscii(token, normalized, out _))
        {
            _logger.LogWarning(InvalidTickerSkippedMessage, token.ToString());
            return;
        }

        if (StockAnalyzer.Core.Helpers.TickerHelper.IsFourDigitJapaneseCode(normalized))
        {
            string ticker = string.Concat(normalized, "-T");
            if (result.Add(ticker))
            {
                _logger.LogDebug("Extracted Japanese ticker with -T suffix: {Ticker}", ticker);
            }
        }
        else if (StockAnalyzer.Core.Models.TickerSymbol.IsAcceptedSymbolFormat(normalized, StockAnalyzer.Core.Models.TickerSymbolLimits.ImportMaxLength))
        {
            string ticker = normalized.ToString(); // Single string allocation for HashSet storage
            if (result.Add(ticker))
            {
                _logger.LogDebug("Extracted ticker: {Ticker}", ticker);
            }
        }
        else
        {
            _logger.LogWarning(InvalidTickerSkippedMessage, normalized.ToString());
        }
    }
}
