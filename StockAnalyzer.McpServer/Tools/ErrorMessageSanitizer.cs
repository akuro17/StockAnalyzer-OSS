using System.Text.RegularExpressions;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Removes absolute paths from text sent to the client: every known root (base directory, data root, home, temp, working
/// directory) and anything shaped like a Windows drive path, a UNC path or a /home, /Users, /tmp or /var path becomes <c>&lt;path&gt;</c>.
/// </summary>
internal sealed class ErrorMessageSanitizer
{
    public const string Placeholder = "<path>";

    private static readonly Regex PathShapes = new(
        @"[A-Za-z]:[\\/][^\s""']+|\\\\[^\s""']+|/(home|Users|tmp|var)/[^\s""']+",
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
