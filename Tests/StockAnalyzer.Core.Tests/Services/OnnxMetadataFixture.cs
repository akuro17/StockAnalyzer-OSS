using System.IO;
using System.Collections.Generic;
using System.Text;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Appends <c>metadata_props</c> (ModelProto field 14, repeated StringStringEntryProto) to a serialized ONNX model without touching its graph:
/// a repeated field can be extended by concatenation, so the original bytes stay valid and verbatim.
/// </summary>
internal static class OnnxMetadataFixture
{
    public static void Write(string sourceModel, string destinationModel, IReadOnlyDictionary<string, string> metadata)
    {
        using var output = File.Create(destinationModel);
        output.Write(File.ReadAllBytes(sourceModel));
        foreach (var pair in metadata)
        {
            using var entry = new MemoryStream();
            WriteString(entry, 1, pair.Key);
            WriteString(entry, 2, pair.Value);
            WriteVarint(output, (14 << 3) | 2);
            WriteVarint(output, (int)entry.Length);
            entry.Position = 0;
            entry.CopyTo(output);
        }
    }

    /// <summary>
    /// Copies <paramref name="sourceModel"/> with the value of the existing <c>metadata_props</c> entry <paramref name="key"/> replaced: every top-level
    /// field is copied verbatim except that entry, and the new entry is appended (<see cref="Write"/> would add a second entry with the same key).
    /// </summary>
    public static void ReplaceValue(string sourceModel, string destinationModel, string key, string value)
    {
        const int metadataPropsField = 14;
        byte[] bytes = File.ReadAllBytes(sourceModel);
        using var output = File.Create(destinationModel);
        bool replaced = false;
        int position = 0;
        while (position < bytes.Length)
        {
            int start = position;
            int tag = ReadVarint(bytes, ref position);
            switch (tag & 7)
            {
                case 0: ReadVarint(bytes, ref position); break;
                case 1: position += 8; break;
                case 5: position += 4; break;
                case 2:
                    int length = ReadVarint(bytes, ref position);
                    int payload = position;
                    position += length;
                    if (tag >> 3 == metadataPropsField && EntryKey(bytes, payload, length) == key)
                    {
                        replaced = true;
                        continue; // dropped here, appended below with the new value
                    }
                    break;
                default: throw new InvalidDataException("Unsupported protobuf wire type.");
            }
            output.Write(bytes, start, position - start);
        }

        if (!replaced) throw new InvalidDataException($"The model has no metadata entry '{key}'.");
        using var entry = new MemoryStream();
        WriteString(entry, 1, key);
        WriteString(entry, 2, value);
        WriteVarint(output, (metadataPropsField << 3) | 2);
        WriteVarint(output, (int)entry.Length);
        entry.Position = 0;
        entry.CopyTo(output);
    }

    /// <summary>The key (field 1) of a StringStringEntryProto, or an empty string when the entry has none.</summary>
    private static string EntryKey(byte[] bytes, int start, int length)
    {
        int position = start;
        int end = start + length;
        while (position < end)
        {
            int tag = ReadVarint(bytes, ref position);
            if ((tag & 7) != 2) return string.Empty;
            int size = ReadVarint(bytes, ref position);
            if (tag >> 3 == 1) return Encoding.UTF8.GetString(bytes, position, size);
            position += size;
        }

        return string.Empty;
    }

    private static int ReadVarint(byte[] bytes, ref int position)
    {
        int result = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = bytes[position++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
    }

    private static void WriteString(Stream stream, int field, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (field << 3) | 2);
        WriteVarint(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarint(Stream stream, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80) { stream.WriteByte((byte)(v | 0x80)); v >>= 7; }
        stream.WriteByte((byte)v);
    }
}
