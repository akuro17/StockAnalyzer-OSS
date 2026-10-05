using System;

namespace StockAnalyzer.Core.Services;

/// <summary>One policy for Windows device names used by file-producing services on every OS.</summary>
internal static class WindowsDeviceFileName
{
    private const char SuperscriptOne = '\u00B9', SuperscriptTwo = '\u00B2', SuperscriptThree = '\u00B3';

    public static bool IsReserved(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        // COM1-COM9 and LPT1-LPT9, and the same names with the ISO 8859-1 superscript digits one to three, which Windows also treats
        // as device names (https://learn.microsoft.com/windows/win32/fileio/naming-a-file).
        return value.Length == 4 && value[3] is (>= '1' and <= '9') or SuperscriptOne or SuperscriptTwo or SuperscriptThree &&
            (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }
}
