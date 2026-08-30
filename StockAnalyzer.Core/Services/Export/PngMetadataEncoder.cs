using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StockAnalyzer.Core.Models.Export;

namespace StockAnalyzer.Core.Services.Export;

/// <summary>
/// Injects standard PNG metadata chunks (iTXt / tEXt) into PNG image byte streams.
/// Enables embedding UTF-8 metadata (Symbol, Company, Timeframe, Indicators, Timestamp) without external dependencies.
/// </summary>
public static class PngMetadataEncoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = InitializeCrcTable();

    private static uint[] InitializeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
            {
                if ((c & 1) != 0)
                {
                    c = 0xEDB88320 ^ (c >> 1);
                }
                else
                {
                    c >>= 1;
                }
            }
            table[i] = c;
        }
        return table;
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> typeAndData)
    {
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < typeAndData.Length; i++)
        {
            c = CrcTable[(c ^ typeAndData[i]) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Injects metadata into the provided PNG byte array.
    /// If the input is not a valid PNG, returns the original bytes unchanged.
    /// </summary>
    public static byte[] InjectMetadata(ReadOnlySpan<byte> pngBytes, ChartImageMetadata metadata)
    {
        if (pngBytes.Length < 8 || !pngBytes[..8].SequenceEqual(PngSignature))
        {
            return pngBytes.ToArray();
        }

        var keyValues = new List<(string Key, string Value)>();

        if (!string.IsNullOrWhiteSpace(metadata.Symbol))
        {
            keyValues.Add(("Symbol", metadata.Symbol));
            keyValues.Add(("Title", string.IsNullOrWhiteSpace(metadata.CompanyName) 
                ? $"StockAnalyzer Chart - {metadata.Symbol}" 
                : $"StockAnalyzer Chart - {metadata.Symbol} ({metadata.CompanyName})"));
        }

        if (!string.IsNullOrWhiteSpace(metadata.CompanyName))
        {
            keyValues.Add(("Company", metadata.CompanyName));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Timeframe))
        {
            keyValues.Add(("Timeframe", metadata.Timeframe));
        }

        if (metadata.StartDate.HasValue && metadata.EndDate.HasValue)
        {
            keyValues.Add(("DateRange", $"{metadata.StartDate:yyyy-MM-dd} ~ {metadata.EndDate:yyyy-MM-dd}"));
        }

        if (!string.IsNullOrWhiteSpace(metadata.IndicatorsSummary))
        {
            keyValues.Add(("Indicators", metadata.IndicatorsSummary));
        }

        keyValues.Add(("Creation Time", metadata.GeneratedAt.ToString("o")));
        keyValues.Add(("Software", $"{metadata.ApplicationName} v{metadata.Version}"));

        if (!string.IsNullOrWhiteSpace(metadata.DetailedJson))
        {
            keyValues.Add(("Comment", metadata.DetailedJson));
        }

        // Find IEND chunk position
        int iendOffset = -1;
        int offset = 8;
        while (offset + 12 <= pngBytes.Length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(offset, 4));
            var chunkType = Encoding.ASCII.GetString(pngBytes.Slice(offset + 4, 4));

            if (chunkType == "IEND")
            {
                iendOffset = offset;
                break;
            }

            offset += (int)(12 + chunkLength);
        }

        if (iendOffset < 0)
        {
            return pngBytes.ToArray();
        }

        using var ms = new MemoryStream(pngBytes.Length + 1024);
        // Write up to IEND
        ms.Write(pngBytes[..iendOffset]);

        // Write iTXt chunks for each metadata item
        foreach (var (key, value) in keyValues)
        {
            WriteItxtChunk(ms, key, value);
        }

        // Write remaining bytes (IEND chunk)
        ms.Write(pngBytes[iendOffset..]);

        return ms.ToArray();
    }

    private static void WriteItxtChunk(Stream stream, string keyword, string text)
    {
        // iTXt Chunk layout:
        // Keyword (1-79 bytes Latin-1) + 0x00
        // Compression Flag (0 = uncompressed)
        // Compression Method (0)
        // Language Tag ("") + 0x00
        // Translated Keyword ("") + 0x00
        // Text (UTF-8)

        var keyBytes = Encoding.ASCII.GetBytes(keyword);
        var textBytes = Encoding.UTF8.GetBytes(text);

        int dataLength = keyBytes.Length + 1 + 1 + 1 + 1 + 1 + textBytes.Length;
        var typeAndData = new byte[4 + dataLength];

        // Type "iTXt"
        typeAndData[0] = 0x69; // 'i'
        typeAndData[1] = 0x54; // 'T'
        typeAndData[2] = 0x58; // 'X'
        typeAndData[3] = 0x74; // 't'

        int pos = 4;
        Buffer.BlockCopy(keyBytes, 0, typeAndData, pos, keyBytes.Length);
        pos += keyBytes.Length;
        typeAndData[pos++] = 0x00; // Null separator for keyword

        typeAndData[pos++] = 0x00; // Uncompressed flag
        typeAndData[pos++] = 0x00; // Compression method

        typeAndData[pos++] = 0x00; // Null separator for empty language tag
        typeAndData[pos++] = 0x00; // Null separator for empty translated keyword

        Buffer.BlockCopy(textBytes, 0, typeAndData, pos, textBytes.Length);

        // Write Length (4 bytes big-endian)
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)dataLength);
        stream.Write(lengthBytes);

        // Write Type + Data
        stream.Write(typeAndData);

        // Write CRC (4 bytes big-endian)
        uint crc = CalculateCrc(typeAndData);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}
