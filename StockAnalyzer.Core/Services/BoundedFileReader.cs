using System;
using System.IO;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Reads a file that comes from outside the process (a model sidecar next to an imported ONNX model) with a hard byte ceiling. The ceiling is applied to
/// the bytes actually read, not to the length the file system reported first, so a file that grows while it is read still stops at the ceiling.
/// </summary>
internal static class BoundedFileReader
{
    private const int ChunkSize = 81920;

    /// <summary>All bytes of <paramref name="path"/>; <see cref="InvalidDataException"/> when the file holds more than <paramref name="maxBytes"/> bytes.</summary>
    public static byte[] ReadAllBytes(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadAllBytes(stream, maxBytes);
    }

    /// <summary>All bytes of <paramref name="stream"/> from its current position; reads at most <c>maxBytes + 1</c> bytes before it gives up.</summary>
    public static byte[] ReadAllBytes(Stream stream, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "The byte ceiling cannot be negative.");

        using var collected = new MemoryStream();
        var chunk = new byte[ChunkSize];
        long total = 0;
        int read;
        // "remaining + 1" would overflow for a ceiling of long.MaxValue, so the one extra probe byte is added only below a full chunk.
        while ((read = stream.Read(chunk, 0, NextReadSize(maxBytes - total))) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"The file holds more than the permitted {maxBytes} bytes.");
            collected.Write(chunk, 0, read);
        }

        return collected.ToArray();
    }

    /// <summary>A full chunk while more than a chunk may still be read; otherwise the remaining allowance plus one byte, which detects a file over the ceiling.</summary>
    private static int NextReadSize(long remaining) => remaining >= ChunkSize ? ChunkSize : (int)remaining + 1;
}
