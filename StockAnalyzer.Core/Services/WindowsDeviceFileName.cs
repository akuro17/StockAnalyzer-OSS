using System;

namespace StockAnalyzer.Core.Services;

/// <summary>One policy for Windows device names used by file-producing services on every OS.</summary>
internal static class WindowsDeviceFileName
{
    public static bool IsReserved(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Length == 4 && value[3] is >= '1' and <= '9' &&
            (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }
}
