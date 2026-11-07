using System.Text.RegularExpressions;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Removes absolute paths from text sent to the client: every known root (base directory, data root, home, temp, working
/// directory) and anything shaped like a Windows drive path, a UNC path, a file:// URI or an absolute path under a well-known Unix root
/// (/home, /Users, /tmp, /var, /opt, /srv, /etc, /mnt, /media, /usr, /root, /proc, /run) becomes <c>&lt;path&gt;</c>. A Unix root counts
/// only at the start of a word (not after a letter, digit, underscore or dot), so text such as <c>P/E</c>, <c>EV/EBITDA</c>, <c>7203.T/9984.T</c>
/// or a date <c>2024/01/05</c> is never touched.
/// </summary>
internal sealed class ErrorMessageSanitizer
{
    public const string Placeholder = "<path>";

    private static readonly Regex PathShapes = new(
        @"file://[^\s""']+|[A-Za-z]:[\\/][^\s""']+|\\\\[^\s""']+|(?<![A-Za-z0-9_.])/(?:home|Users|tmp|var|opt|srv|etc|mnt|media|usr|root|proc|run)/[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string[] _roots;

    public ErrorMessageSanitizer(IEnumerable<string?> knownRoots)
    {
        _roots = knownRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .SelectMany(r => new[] { r!.TrimEnd('\\', '/'), r.TrimEnd('\\', '/').Replace('\\', '/') })
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(r => r.Length)
            .ToArray();
    }

    public string Sanitize(string text)
    {
        foreach (string root in _roots)
        {
            text = text.Replace(root, Placeholder, StringComparison.OrdinalIgnoreCase);
        }

        return PathShapes.Replace(text, Placeholder);
    }
}
