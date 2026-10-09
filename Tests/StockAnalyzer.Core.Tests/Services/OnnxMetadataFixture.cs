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
