#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6Domain2And3Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static CalculatorOutcome Eval(string expr)
        => ExpressionCalculator.Evaluate(expr, EmptyScalars, CalculatorLimits.Default);

    private static ExpressionCalculator.CalculatorBatchResult EvalBatchOne(string expr)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        return results[0];
    }

    // ---- Domain 2: Number Theory, Bitwise, Banker's Rounding & Complex Trig ----

    [Fact]
    public void NumberTheory_BasicAndLargePrimes_AreExact()
    {
        Assert.Equal(6m, Eval("gcd(-54, 24)").Value);
        Assert.Equal(0m, Eval("gcd(0, 0)").Value);
        Assert.Equal(216m, Eval("lcm(54, 24)").Value);
        Assert.Equal(0m, Eval("lcm(0, 24)").Value);

        Assert.Equal(0m, Eval("is_prime(1)").Value);
        Assert.Equal(1m, Eval("is_prime(2)").Value);
        Assert.Equal(1m, Eval("is_prime(1999993)").Value);
        Assert.Equal(0m, Eval("is_prime(1999999)").Value);
        Assert.Equal(1m, Eval("is_prime(2000003)").Value);
        Assert.Equal(1m, Eval("is_prime(9007199254740881)").Value); // largest prime < 2^53

        Assert.Equal(2m, Eval("next_prime(1)").Value);
        Assert.Equal(11m, Eval("next_prime(7)").Value);
        Assert.Equal(2000003m, Eval("next_prime(1999993)").Value);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("next_prime(9007199254740881)").Error!.Code);

        Assert.Equal(445m, Eval("mod_pow(4, 13, 497)").Value);
        Assert.Equal(0m, Eval("mod_pow(10, 5, 1)").Value);
        Assert.Equal(4m, Eval("mod_inv(3, 11)").Value);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("mod_inv(2, 4)").Error!.Code);

        Assert.Equal(-3m, Eval("floor_div(-7, 3)").Value);
        Assert.Equal(-23333333333333333333333333334m, Eval("floor_div(-70000000000000000000000000000, 3)").Value);
        Assert.Equal(23333333333333333333333333333m, Eval("floor_div(70000000000000000000000000000, 3)").Value);
        Assert.Equal(23333333333333333333333333333m, Eval("floor_div(-70000000000000000000000000001, -3)").Value);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, Eval("floor_div(5, 0)").Error!.Code);
    }

    [Fact]
    public void BitwiseOperations_53BitContract_IsExact()
    {
        Assert.Equal(8m, Eval("bit_and(12, 10)").Value);
        Assert.Equal(14m, Eval("bit_or(12, 10)").Value);
        Assert.Equal(6m, Eval("bit_xor(12, 10)").Value);
        Assert.Equal(9007199254740991m, Eval("bit_not(0)").Value);
        Assert.Equal(0m, Eval("bit_not(9007199254740991)").Value);
        Assert.Equal(16m, Eval("bit_shl(1, 4)").Value);
        Assert.Equal(0m, Eval("bit_shl(1, 52) * 2 * 0 + bit_shl(9007199254740991, 52) - 4503599627370496").Value);
        Assert.Equal(4m, Eval("bit_shr(16, 2)").Value);
        Assert.Equal(53m, Eval("popcount(9007199254740991)").Value);

        Assert.Equal(CalculatorErrorCode.DomainError, Eval("bit_and(-1, 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("bit_shl(1, 53)").Error!.Code);
    }

    [Fact]
    public void RoundEven_PreservesExistingRoundAwayFromZero()
    {
        Assert.Equal(3m, Eval("round(2.5, 0)").Value);
        Assert.Equal(2m, Eval("round_even(2.5, 0)").Value);
        Assert.Equal(4m, Eval("round_even(3.5, 0)").Value);
        Assert.Equal(20m, Eval("round_even(25, -1)").Value);
        Assert.Equal(40m, Eval("round_even(35, -1)").Value);
        Assert.Equal(10m, Eval("round_even(5.0000000000000000000000000001, -1)").Value);
        Assert.Equal(-10m, Eval("round_even(-5.0000000000000000000000000001, -1)").Value);
        Assert.Equal(1000000000000000m, Eval("round_even(500000000000000.00000000000001, -15)").Value);
        Assert.Equal(75000000000002000000000000000m, Eval("round_even(75000000000001500000000000000, -15)").Value);
        Assert.Equal(75000000000000000000000000000m, Eval("round_even(75000000000000500000000000000, -15)").Value);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("round_even(1.23, 16)").Error!.Code);
    }

    [Fact]
    public void ComplexElementary_TrigonometricAndHyperbolic_RespectLimitsAndPoles()
    {
        Assert.Equal(3m, Eval("c_conj_re(3, -4)").Value);
        Assert.Equal(4m, Eval("c_conj_im(3, -4)").Value);

        // sin(0 + i*0) = 0, cos(0 + i*0) = 1
        Assert.Equal(0m, Eval("c_sin_re(0, 0)").Value);
        Assert.Equal(1m, Eval("c_cos_re(0, 0)").Value);
        // sinh(0 + i*0) = 0, cosh(0 + i*0) = 1
        Assert.Equal(0m, Eval("c_sinh_re(0, 0)").Value);
        Assert.Equal(1m, Eval("c_cosh_re(0, 0)").Value);

        // Asymptotic limits (>35) and smooth continuity across the legacy b = 20 threshold
        Assert.Equal(0m, Eval("c_tan_re(1, 40)").Value);
        Assert.Equal(1m, Eval("c_tan_im(1, 40)").Value);
        Assert.InRange(Eval("c_tan_re(1, 30)").Value!.Value, 0m, 1e-24m);
        Assert.InRange(Math.Abs(Eval("c_tan_re(1, 20.1)").Value!.Value - Eval("c_tan_re(1, 19.9)").Value!.Value), 0m, 1e-15m);
        Assert.InRange(Math.Abs(Eval("c_tan_im(1, 20.1)").Value!.Value - Eval("c_tan_im(1, 19.9)").Value!.Value), 0m, 1e-15m);
        Assert.Equal(-1m, Eval("c_tanh_re(-40, 1)").Value);
        Assert.Equal(0m, Eval("c_tanh_im(-40, 1)").Value);

        // Radian range limit enforced BEFORE asymptotic shortcut (1e9 + 1)
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("c_tan_im(1000000001, 30)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("c_tan_im(2000000000, 30)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("c_tanh_re(30, 1000000001)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("c_tanh_re(30, 2000000000)").Error!.Code);

        // Pole at pi/2 + 0i matches real tan(x) -> DomainError
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("c_tan_re(pi / 2, 0)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, Eval("c_tanh_im(0, pi / 2)").Error!.Code);

        // Hyperbolic overflow
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, Eval("c_sin_re(1, 710)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, Eval("c_cosh_re(710, 0)").Error!.Code);
    }

    // ---- Domain 3a: Pure Decimal Array Generators & Utilities ----

    [Fact]
    public void ArrayGeneratorsAndUtilities_ProduceExactDecimalArrays()
    {
        Assert.Equal(new[] { 0m, 2.5m, 5m, 7.5m, 10m }, EvalBatchOne("linspace(0, 10, 5)").ArrayValues);
        Assert.Equal(Array.Empty<decimal>(), EvalBatchOne("linspace(0, 10, 0)").ArrayValues);
        Assert.Equal(new[] { 1m, 3m, 5m }, EvalBatchOne("arange(1, 6, 2)").ArrayValues);
        Assert.Equal(Array.Empty<decimal>(), EvalBatchOne("arange(5, 1, 1)").ArrayValues);
        Assert.Equal(new[] { 0m, 0m, 0m }, EvalBatchOne("arr_zeros(3)").ArrayValues);
        Assert.Equal(new[] { 1m, 1m }, EvalBatchOne("arr_ones(2)").ArrayValues);
        Assert.Equal(new[] { 7m, 7m, 7m }, EvalBatchOne("arr_fill(3, 7)").ArrayValues);
        Assert.Equal(new[] { 1m, 0m, 0m, 1m }, EvalBatchOne("mat_eye(2)").ArrayValues);
        Assert.Equal(new[] { 5m, 0m, 0m, 9m }, EvalBatchOne("mat_diag([5, 9])").ArrayValues);
        Assert.Equal(new[] { 1m, 2m, 3m, 4m }, EvalBatchOne("arr_concat([1, 2], [3, 4])").ArrayValues);
        Assert.Equal(new[] { 3m, 2m, 1m }, EvalBatchOne("arr_reverse([1, 2, 3])").ArrayValues);
        Assert.Equal(new[] { -2m, 0m, 3m }, EvalBatchOne("arr_unique([3, -0, -2, 3, 0])").ArrayValues);
        Assert.Equal(new[] { 1m, 2m, 5m, 5m }, EvalBatchOne("arr_clip([0, 2, 5, 9], 1, 5)").ArrayValues);
        Assert.Equal(new[] { 11m, 12m, 13m }, EvalBatchOne("arr_add_scalar([1, 2, 3], 10)").ArrayValues);

        // Array lowering integration check
        Assert.Equal(15m, EvalBatchOne("sum(linspace(1, 5, 5))").ScalarValue);
    }

    // ---- Domain 3b: Signal Processing & Spectral Functions ----

    [Fact]
    public void SignalProcessing_FftRoundTripConvolveWindowsAndEma_WorkCleanly()
    {
        ExpressionCalculator.CalculatorBatchResult roundTrip = EvalBatchOne("ifft_re(fft_re([1, 2, 3, 4, 5]), fft_im([1, 2, 3, 4, 5]))");
        Assert.True(roundTrip.IsOk, roundTrip.Error?.Message);
        Assert.Equal(5, roundTrip.ArrayValues!.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.InRange(roundTrip.ArrayValues[i], i + 0.999999m, i + 1.000001m);
        }

        ExpressionCalculator.CalculatorBatchResult phase = EvalBatchOne("fft_phase([1, 0, 0, 0])");
        Assert.True(phase.IsOk, phase.Error?.Message);
        Assert.Equal(new[] { 0m, 0m, 0m, 0m }, phase.ArrayValues);

        Assert.Equal(new[] { 1m, 4m, 10m, 12m, 9m }, EvalBatchOne("convolve([1, 2, 3], [1, 2, 3], 0)").ArrayValues);
        Assert.Equal(new[] { 4m, 10m, 12m }, EvalBatchOne("convolve([1, 2, 3], [1, 2, 3], 1)").ArrayValues);
        Assert.Equal(new[] { 10m }, EvalBatchOne("convolve([1, 2, 3], [1, 2, 3], 2)").ArrayValues);

        ExpressionCalculator.CalculatorBatchResult hann = EvalBatchOne("window_hann(5)");
        Assert.True(hann.IsOk);
        Assert.Equal(0m, hann.ArrayValues![0]);
        Assert.Equal(1m, hann.ArrayValues![2]);
        Assert.Equal(0m, hann.ArrayValues![4]);

        ExpressionCalculator.CalculatorBatchResult hamming = EvalBatchOne("window_hamming(1)");
        Assert.Equal(new[] { 1m }, hamming.ArrayValues);

        Assert.Equal(new[] { 10m, 15m, 17.5m }, EvalBatchOne("ema_series([10, 20, 20], 0.5)").ArrayValues);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("ema_series([10, 20], 0)").Error!.Code);
    }
}
