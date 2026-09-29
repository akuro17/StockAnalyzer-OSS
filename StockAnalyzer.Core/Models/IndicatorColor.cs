using System;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models;

[JsonConverter(typeof(IndicatorColorJsonConverter))]
public struct IndicatorColor : IEquatable<IndicatorColor>
{
    public byte A { get; set; }
    public byte Alpha => A;
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public IndicatorColor(byte a, byte r, byte g, byte b)
    {
        A = a;
        R = r;
        G = g;
        B = b;
    }

    public static IndicatorColor FromRgb(byte r, byte g, byte b) => new IndicatorColor(255, r, g, b);
    public static IndicatorColor FromArgb(byte a, byte r, byte g, byte b) => new IndicatorColor(a, r, g, b);
    public static IndicatorColor FromUInt(uint color)
    {
        return new IndicatorColor(
            (byte)((color >> 24) & 0xFF),
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF)
        );
    }

    public IndicatorColor WithAlpha(byte alpha)
    {
        return new IndicatorColor(alpha, R, G, B);
    }

    public override bool Equals(object? obj) => obj is IndicatorColor other && Equals(other);
    public bool Equals(IndicatorColor other) => A == other.A && R == other.R && G == other.G && B == other.B;
    public override int GetHashCode() => HashCode.Combine(A, R, G, B);

    public static bool operator ==(IndicatorColor left, IndicatorColor right) => left.Equals(right);
    public static bool operator !=(IndicatorColor left, IndicatorColor right) => !left.Equals(right);

    public static IndicatorColor Gray => new IndicatorColor(255, 128, 128, 128);
    public static IndicatorColor Bullish => new IndicatorColor(255, 0, 255, 0);
    public static IndicatorColor Bearish => new IndicatorColor(255, 255, 0, 0);
    public static IndicatorColor Transparent => new IndicatorColor(0, 0, 0, 0);

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
