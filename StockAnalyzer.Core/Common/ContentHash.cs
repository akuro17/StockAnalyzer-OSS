using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace StockAnalyzer.Core.Common;

/// <summary>The single definition of the SHA-256 identity strings used by the generation registry and every sidecar contract.</summary>
public static class ContentHash
{
    /// <summary>Length of a SHA-256 digest in hexadecimal characters.</summary>
    public const int HexLength = 64;

    /// <summary>SHA-256 of canonical UTF-8 contract text as lower-case hexadecimal.</summary>
    public static string OfText(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>True for exactly <see cref="HexLength"/> lower-case hexadecimal characters.</summary>
    public static bool IsLowerHex64(string? value) =>
        value is { Length: HexLength } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>SHA-256 of a file's bytes as lower-case hexadecimal.</summary>
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
