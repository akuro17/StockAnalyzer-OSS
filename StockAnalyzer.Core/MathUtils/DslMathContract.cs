using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Unified zero-allocation error codes shared across DSL mathematical kernels.
/// </summary>
public enum MathErrorCode : byte
{
    None = 0,
    DomainError,
    DivisionByZero,
    ArithmeticOverflow,
    NonFiniteResult,
    ConvergenceFailure,
    SingularMatrix,
    DimensionMismatch,
    BudgetExceeded,
}

/// <summary>
/// Zero-allocation value-type outcome carrying either a result value or a <see cref="MathErrorCode"/>.
/// </summary>
public readonly record struct MathResult<T>(T Value, MathErrorCode Error)
{
    public bool IsOk => Error == MathErrorCode.None;

    public static MathResult<T> Ok(T value) => new(value, MathErrorCode.None);

    public static MathResult<T> Fail(MathErrorCode error) => new(default!, error);
}

/// <summary>
/// Two-component <see cref="decimal"/> complex number representation.
/// </summary>
public readonly record struct DslComplex(decimal Re, decimal Im);

/// <summary>
/// UI-independent 2D coordinate pair in <see cref="decimal"/> data coordinates.
/// </summary>
public readonly record struct DslPoint2D(decimal X, decimal Y);

/// <summary>
/// Stack-only execution budget enforcing per-expression operation and recursion depth bounds.
/// </summary>
public ref struct DslExecutionBudget
{
    public const int DefaultMaxOps = 10_000;
    public const int DefaultMaxDepth = 16;

    public int RemainingOps;
    public int RemainingDepth;
    internal MathErrorCode LastError;

    public static DslExecutionBudget CreateDefault()
        => Create(DefaultMaxOps, DefaultMaxDepth);

    public static DslExecutionBudget Create(int maxOps, int maxDepth = DefaultMaxDepth)
        => new()
        {
            RemainingOps = maxOps,
            RemainingDepth = maxDepth,
            LastError = MathErrorCode.None,
        };

    /// <summary>
    /// Consumes at least one operation unit; returns <c>false</c> and records
    /// <see cref="MathErrorCode.BudgetExceeded"/> when the budget drops below zero.
    /// </summary>
    public bool TryConsume(int cost = 1)
    {
        int effectiveCost = cost < 1 ? 1 : cost;
        RemainingOps -= effectiveCost;
        if (RemainingOps < 0)
        {
            LastError = MathErrorCode.BudgetExceeded;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enters one nested evaluation or recursion frame; returns <c>false</c> and records
    /// <see cref="MathErrorCode.BudgetExceeded"/> when <see cref="RemainingDepth"/> is exhausted.
    /// </summary>
    public bool TryEnterDepth()
    {
        if (RemainingDepth <= 0)
        {
            LastError = MathErrorCode.BudgetExceeded;
            return false;
        }

        RemainingDepth--;
        return true;
    }

    /// <summary>
    /// Restores one nested evaluation or recursion frame exited after <see cref="TryEnterDepth"/>.
    /// </summary>
    public void ExitDepth()
    {
        RemainingDepth++;
    }
}

/// <summary>
/// Zero-allocation scalar functor interface for higher-order numerical algorithms.
/// Implement as a <c>struct</c> constrained by <c>where TFunc : struct, IScalarFunction</c>.
/// </summary>
public interface IScalarFunction
{
    bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y);
}

/// <summary>
/// Zero-allocation ODE right-hand-side functor interface for systems of dimension 1..8.
/// </summary>
public interface IOdeSystem
{
    int Dimension { get; }

    bool TryEvaluate(double t, scoped ReadOnlySpan<double> y, scoped Span<double> dydt, ref DslExecutionBudget budget);
}
