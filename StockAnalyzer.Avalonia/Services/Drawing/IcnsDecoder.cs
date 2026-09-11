using System;
using System.Buffers.Binary;
using System.IO;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Lightweight Apple Icon Image (.icns) parser.
/// Modern macOS ICNS files (OS X 10.7+) store icons as embedded PNG streams within chunk elements
/// (e.g. 'ic07' 128x128, 'ic08' 256x256, 'ic09' 512x512, 'ic10' 1024x1024, 'icp4' 16x16, etc.).
/// This decoder extracts the highest-resolution embedded PNG payload without external dependencies.
/// </summary>
public static class IcnsDecoder
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[]? ExtractLargestPng(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return ExtractLargestPng(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? ExtractLargestPng(byte[] icnsBytes)
    {
        if (icnsBytes == null || icnsBytes.Length < 8) return null;

        // Verify magic header "icns"
        if (icnsBytes[0] != 0x69 || icnsBytes[1] != 0x63 || icnsBytes[2] != 0x6E || icnsBytes[3] != 0x73)
            return null;

        int offset = 8;
        byte[]? largestPng = null;
        int maxLen = 0;

        while (offset + 8 <= icnsBytes.Length)
        {
            uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(icnsBytes.AsSpan(offset + 4, 4));
            if (chunkLen < 8 || offset + chunkLen > icnsBytes.Length)
                break;

            int dataOffset = offset + 8;
            int dataLen = (int)chunkLen - 8;

            if (dataLen >= 8 && MatchesPngHeader(icnsBytes, dataOffset))
            {
                if (dataLen > maxLen)
                {
                    maxLen = dataLen;
                    largestPng = new byte[dataLen];
                    Buffer.BlockCopy(icnsBytes, dataOffset, largestPng, 0, dataLen);
                }
            }

            offset += (int)chunkLen;
        }

        return largestPng;
    }

    private static bool MatchesPngHeader(byte[] data, int offset)
    {
        for (int i = 0; i < PngHeader.Length; i++)
        {
            if (data[offset + i] != PngHeader[i]) return false;
        }
        return true;
    }
}
