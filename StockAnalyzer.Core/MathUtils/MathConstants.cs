using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Single source of truth for angle-unit conversion factors and the full-turn constant.
///
/// Every value here is a compile-time <c>const</c> whose initializer is written in the exact
/// expression form that was previously duplicated across the solution (indicator engines, the
/// Reverse Watch renderer/analysis service, and several drawing tools). Because the initializer is
/// that same expression, C# constant folding yields the same IEEE-754 <see cref="double"/> /
/// <see cref="float"/> value the inline literal produced, so migrating a call site that used the
/// identical expression form is a value-preserving substitution;
/// <c>MathConstantsTests</c> asserts exact equality of each member against its literal expression.
/// (A call site written in a different form -- e.g. the divide-last <c>x * Math.PI / 180.0</c> --
/// changes rounding order by up to one ULP when switched to the pre-divided factor; that is a
/// numerical change, not a pure substitution.)
///
/// Multiplication is the intended usage: <c>radians = degrees * DegToRad</c>,
/// <c>degrees = radians * RadToDeg</c>. Prefer multiplying by the pre-divided factor over the
/// <c>x * Math.PI / 180.0</c> divide-last form so that every caller shares one rounding.
/// </summary>
public static class MathConstants
{
    /// <summary>
    /// Radians per degree. Multiply a value in degrees by this to obtain radians.
    /// Equals <c>Math.PI / 180.0</c>.
    /// </summary>
    public const double DegToRad = Math.PI / 180.0;

    /// <summary>
    /// Degrees per radian. Multiply a value in radians by this to obtain degrees.
    /// Equals <c>180.0 / Math.PI</c>.
    /// </summary>
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// Single-precision <see cref="RadToDeg"/> for screen-space (pixel) geometry that works in
    /// <see cref="float"/> -- SkiaSharp path angles and tangent directions, where <see cref="float"/>
    /// precision is sufficient and matches the render pipeline's own type, so keeping the factor
    /// <see cref="float"/> avoids a per-call <c>(float)</c> narrowing cast. Equals
    /// <c>(float)(180.0 / Math.PI)</c>.
    /// </summary>
    public const float RadToDegF = (float)(180.0 / Math.PI);

    /// <summary>
    /// One full turn in radians, <c>2 * pi</c>. Equals <c>2.0 * Math.PI</c>.
    /// </summary>
    public const double TwoPi = 2.0 * Math.PI;
}
