using System;
using System.IO;
using System.Text;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Byte-level canonical encoding used by the run fingerprint (owner decision G5): fixed field order chosen by the caller, little-endian integers
/// (<see cref="BinaryWriter"/> is little-endian on every platform), explicit null markers and collection counts, UTF-8 strings with a length prefix,
/// UTC ticks for timestamps, and decimals via <see cref="decimal.GetBits(decimal)"/> (sign and scale included - 1.0 and 1.00 are different encodings;
/// no semantic normalization is implied). Nothing here is culture-sensitive.
/// </summary>
internal sealed class CanonicalWriter : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public CanonicalWriter() => _writer = new BinaryWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);

    public void Int32(int value) => _writer.Write(value);

    public void Int64(long value) => _writer.Write(value);

    public void Bool(bool value) => _writer.Write(value);

    /// <summary>Collection element count (always written before the elements).</summary>
    public void Count(int count) => _writer.Write(count);

    public void Enum<T>(T value) where T : struct, Enum => _writer.Write(Convert.ToInt32(value));

    public void NullableEnum<T>(T? value) where T : struct, Enum
    {
        _writer.Write(value.HasValue);
        if (value.HasValue) Enum(value.Value);
    }

    public void Decimal(decimal value)
    {
        foreach (int part in decimal.GetBits(value)) _writer.Write(part);
    }

    public void NullableDecimal(decimal? value)
    {
        _writer.Write(value.HasValue);
        if (value.HasValue) Decimal(value.Value);
    }

    public void NullableInt32(int? value)
    {
        _writer.Write(value.HasValue);
        if (value.HasValue) _writer.Write(value.Value);
    }

    public void NullableInt64(long? value)
    {
        _writer.Write(value.HasValue);
        if (value.HasValue) _writer.Write(value.Value);
    }

    /// <summary>Null marker, then the UTF-8 byte count, then the bytes.</summary>
    public void String(string? value)
    {
        _writer.Write(value is not null);
        if (value is null) return;

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        _writer.Write(bytes.Length);
        _writer.Write(bytes);
    }

    /// <summary>UTC ticks. A non-UTC timestamp has no machine-independent meaning, so it is rejected instead of being converted.</summary>
    public void Timestamp(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("A fingerprinted timestamp must be DateTimeKind.Utc.", nameof(value));
        }
        _writer.Write(value.Ticks);
    }

    public void Bytes(byte[] value) => _writer.Write(value);

    public byte[] ToArray()
    {
        _writer.Flush();
        return _stream.ToArray();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
