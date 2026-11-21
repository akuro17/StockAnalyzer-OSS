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
    /// Machine epsilon of <see cref="double"/> (2^-52): the gap between 1.0 and the next larger double, the relative resolution of one value.
    /// NOT <see cref="double.Epsilon"/> (the smallest subnormal, 4.9e-324). The single definition of the repository.
    /// </summary>
    public const double MachineEpsilon = 2.220446049250313e-16;

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

    /// <summary>
    /// Quarter turn in radians, <c>pi / 2</c>. Equals <c>0.5 * Math.PI</c>.
    /// </summary>
    public const double HalfPi = 0.5 * Math.PI;

    /// <summary>
    /// High-precision <see cref="decimal"/> constant for <c>pi</c> (29 significant digits).
    /// </summary>
    public const decimal PiDecimal = 3.1415926535897932384626433833m;

    /// <summary>
    /// High-precision <see cref="decimal"/> constant for Euler's number <c>e</c> (29 significant digits).
    /// </summary>
    public const decimal EDecimal = 2.7182818284590452353602874714m;

    /// <summary>
    /// High-precision <see cref="decimal"/> constant for the golden ratio <c>phi = (1 + sqrt(5)) / 2</c> (29 significant digits).
    /// </summary>
    public const decimal PhiDecimal = 1.6180339887498948482045868344m;

    /// <summary>
    /// High-precision <see cref="decimal"/> constant for the Euler-Mascheroni constant <c>gamma</c> (28 fractional digits).
    /// </summary>
    public const decimal EulerGammaDecimal = 0.5772156649015328606065120901m;

    /// <summary>
    /// Double-precision Euler-Mascheroni constant <c>gamma</c>.
    /// </summary>
    public const double EulerGamma = 0.57721566490153286;
}
