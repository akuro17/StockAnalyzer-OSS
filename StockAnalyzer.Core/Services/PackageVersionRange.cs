using System.Globalization;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// The version range an automatic update of a pinned package stays in: from the pinned version up to, but excluding, the next major
    /// (owner decision D-01 B, N-03 A: the bound is <c>(major + 1).0.0</c> for every version, including 0.x).
    /// </summary>
    internal static class PackageVersionRange
    {
        /// <summary>The exclusive upper major of the range, or null when <paramref name="pinnedVersion"/> does not start with a number.</summary>
        public static int? UpperMajor(string? pinnedVersion)
        {
            if (string.IsNullOrWhiteSpace(pinnedVersion)) return null;

            var text = pinnedVersion.Trim();
            var end = 0;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;

            return end > 0 && int.TryParse(text.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                ? major + 1
                : null;
        }

        /// <summary>pip requirement <c>package&gt;=pin,&lt;(major+1).0.0</c>, or null when the pin has no leading major.</summary>
        public static string? UpgradeSpecifier(string packageName, string? pinnedVersion)
        {
            return UpperMajor(pinnedVersion) is { } upper
                ? $"{packageName}>={pinnedVersion!.Trim()},<{upper.ToString(CultureInfo.InvariantCulture)}.0.0"
                : null;
        }
    }
}
