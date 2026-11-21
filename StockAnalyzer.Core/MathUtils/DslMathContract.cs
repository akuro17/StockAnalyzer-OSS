using System;
using System.Buffers;
using System.Numerics;
using System.Threading;

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

    /// <summary>
    /// The user-supplied function (the body of a higher-order DSL function) failed with an error of the caller's own error domain;
    /// the details are in <see cref="DslExecutionBudget.LastFunctorFailure"/> and are valid only when <see cref="DslFunctorFailure.IsSet"/> is true.
    /// </summary>
    FunctorFailure,
}

/// <summary>
/// Opaque failure detail of a user-supplied function, carried through the numerical kernels beside <see cref="MathErrorCode.FunctorFailure"/>.
/// <see cref="Code"/> is meaningful only to the layer that stored it (the calculator stores its error code); the math layer never interprets it.
/// The default value is "unset": <see cref="IsSet"/> is false, so a numeric code of 0 is never read as a stored failure.
/// </summary>
internal readonly record struct DslFunctorFailure(bool IsSet, int Code, int Position, string Message, string? Token)
{
    public static DslFunctorFailure Unset => default;

    public static DslFunctorFailure Of(int code, int position, string message, string? token) => new(true, code, position, message, token);
}

/// <summary>
/// Zero-allocation bit flags returned by correlation and covariance matrix validation kernels.
/// </summary>
[Flags]
public enum MatrixValidationFlags : ushort
{
    Valid = 0,
    NotSquare = 1 << 0,
    DimensionOver = 1 << 1,
    NonFinite = 1 << 2,
    Asymmetric = 1 << 3,
    InvalidDiag = 1 << 4,
    OutOfRange = 1 << 5,
    NotPsd = 1 << 6,
    NotPd = 1 << 7,
    IllConditioned = 1 << 8,
}

/// <summary>
/// Handle identifying a contiguous slice within a <see cref="DslArrayArena"/>.
/// </summary>
public readonly record struct DslArrayHandle(int Offset, int Length);

/// <summary>
/// Zero-allocation bump-allocated arena backed by a lazily rented <see cref="ArrayPool{T}"/> buffer
/// for 1D <see cref="decimal"/> arrays evaluated inside a single DSL expression or batch session.
/// </summary>
public ref struct DslArrayArena
{
    public const int MaxArrayLength = 512;
    public const int MaxArenaElements = 4_096;

    private decimal[]? _rented;
    private Span<decimal> _buffer;
    private int _cursor;

    /// <summary>Current bump allocation offset in elements.</summary>
    public readonly int Cursor => _cursor;

    /// <summary>
    /// Allocates <paramref name="length"/> elements in the arena. Returns <c>false</c> when
    /// <paramref name="length"/> is negative, exceeds <see cref="MaxArrayLength"/>, or exceeds remaining arena capacity.
    /// </summary>
    public bool TryAllocate(int length, out DslArrayHandle handle, out Span<decimal> slice)
    {
        if (length < 0 || length > MaxArrayLength)
        {
            handle = default;
            slice = default;
            return false;
        }

        if (length == 0)
        {
            handle = new DslArrayHandle(_cursor, 0);
            slice = Span<decimal>.Empty;
            return true;
        }

        EnsureInitialized();
        if (_cursor + length > _buffer.Length)
        {
            handle = default;
            slice = default;
            return false;
        }

        handle = new DslArrayHandle(_cursor, length);
        slice = _buffer.Slice(_cursor, length);
        _cursor += length;
        return true;
    }

    /// <summary>Returns a read-only view of the slice referenced by <paramref name="handle"/>.</summary>
    public readonly ReadOnlySpan<decimal> GetSlice(DslArrayHandle handle)
    {
        if (handle.Length == 0)
        {
            return ReadOnlySpan<decimal>.Empty;
        }

        return _buffer.Slice(handle.Offset, handle.Length);
    }

    /// <summary>Returns a mutable view of the slice referenced by <paramref name="handle"/>.</summary>
    internal Span<decimal> GetMutableSlice(DslArrayHandle handle)
    {
        if (handle.Length == 0)
        {
            return Span<decimal>.Empty;
        }

        return _buffer.Slice(handle.Offset, handle.Length);
    }

    /// <summary>Restores the bump cursor to an earlier checkpoint to discard transient allocations on error.</summary>
    public void RestoreCursor(int cursor)
    {
        if ((uint)cursor <= (uint)_cursor)
        {
            _cursor = cursor;
        }
    }

    /// <summary>
    /// Compacts <paramref name="resultHandle"/> down to <paramref name="targetOffset"/> and resets the cursor
    /// right after it, reclaiming transient intermediate allocations from a completed expression.
    /// When <paramref name="resultHandle"/> points into an earlier binding (<c>Offset &lt; targetOffset</c>),
    /// returns the existing handle directly and restores the cursor to <paramref name="targetOffset"/> without copying.
    /// </summary>
    public DslArrayHandle CompactResult(int targetOffset, DslArrayHandle resultHandle)
    {
        if (resultHandle.Offset < targetOffset)
        {
            RestoreCursor(targetOffset);
            return resultHandle;
        }

        if (resultHandle.Length == 0)
        {
            _cursor = targetOffset;
            return new DslArrayHandle(targetOffset, 0);
        }

        if (resultHandle.Offset != targetOffset)
        {
            _buffer.Slice(resultHandle.Offset, resultHandle.Length)
                .CopyTo(_buffer.Slice(targetOffset, resultHandle.Length));
        }

        _cursor = targetOffset + resultHandle.Length;
        return new DslArrayHandle(targetOffset, resultHandle.Length);
    }

    private void EnsureInitialized()
    {
        if (_rented is null)
        {
            _rented = ArrayPool<decimal>.Shared.Rent(MaxArenaElements);
            _buffer = _rented.AsSpan(0, MaxArenaElements);
        }
    }

    /// <summary>Returns any rented backing array to <see cref="ArrayPool{T}.Shared"/>, clearing sensitive values.</summary>
    public void Dispose()
    {
        decimal[]? toReturn = _rented;
        _rented = null;
        _buffer = default;
        _cursor = 0;
        if (toReturn is not null)
        {
            ArrayPool<decimal>.Shared.Return(toReturn, clearArray: true);
        }
    }
}

/// <summary>
/// Deterministic value-type Xoshiro256** PRNG initialized via SplitMix64.
/// Prohibits reliance on non-deterministic <see cref="Random.Shared"/>.
/// </summary>
public struct DeterministicRng
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public DeterministicRng(ulong seed)
    {
        ulong state = seed;
        _s0 = SplitMix64(ref state);
        _s1 = SplitMix64(ref state);
        _s2 = SplitMix64(ref state);
        _s3 = SplitMix64(ref state);

        if ((_s0 | _s1 | _s2 | _s3) == 0UL)
        {
            _s0 = 0x9E3779B97F4A7C15UL;
            _s1 = 0xBF58476D1CE4E5B9UL;
            _s2 = 0x94D049BB133111EBUL;
            _s3 = 0x369DEA0F31A53F85UL;
        }
    }

    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5UL, 7) * 9UL;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    public double NextUnitDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be positive.");
        }

        if (maxExclusive == 1)
        {
            return 0;
        }

        ulong range = (ulong)maxExclusive;
        ulong threshold = (0UL - range) % range;
        while (true)
        {
            ulong r = NextUInt64();
            if (r >= threshold)
            {
                return (int)(r % range);
            }
        }
    }

    private static ulong SplitMix64(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// Shared strict conversion helpers for <see cref="decimal"/>-encoded integer seeds and indices.
/// </summary>
public static class DslMathValidation
{
    public static bool TryReadUInt64Seed(decimal rawSeed, out ulong seed)
    {
        if (rawSeed != decimal.Truncate(rawSeed) || rawSeed < 0m || rawSeed > ulong.MaxValue)
        {
            seed = 0UL;
            return false;
        }

        seed = (ulong)rawSeed;
        return true;
    }

    public static bool TryReadInt32InRange(decimal raw, int minInclusive, int maxInclusive, out int value)
    {
        if (raw != decimal.Truncate(raw) || raw < minInclusive || raw > maxInclusive)
        {
            value = 0;
            return false;
        }

        value = (int)raw;
        return true;
    }
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

    /// <summary>Detail of the last failure of a user-supplied function; valid only while <see cref="DslFunctorFailure.IsSet"/> is true and <see cref="LastError"/> is <see cref="MathErrorCode.FunctorFailure"/>.</summary>
    internal DslFunctorFailure LastFunctorFailure;

    private CancellationToken _cancellationToken;

    public static DslExecutionBudget CreateDefault()
        => Create(DefaultMaxOps, DefaultMaxDepth);

    public static DslExecutionBudget Create(int maxOps, int maxDepth = DefaultMaxDepth)
        => new()
        {
            RemainingOps = maxOps,
            RemainingDepth = maxDepth,
            LastError = MathErrorCode.None,
        };

    public static DslExecutionBudget Create(int maxOps, int maxDepth, CancellationToken cancellationToken)
        => new()
        {
            RemainingOps = maxOps,
            RemainingDepth = maxDepth,
            LastError = MathErrorCode.None,
            _cancellationToken = cancellationToken,
        };

    /// <summary>
    /// Consumes at least one operation unit; returns <c>false</c> and records
    /// <see cref="MathErrorCode.BudgetExceeded"/> when the budget drops below zero.
    /// </summary>
    public bool TryConsume(int cost = 1)
    {
        _cancellationToken.ThrowIfCancellationRequested();
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

/// <summary>
/// Zero-allocation multidimensional scalar functor interface for Monte Carlo quadrature of dimension 1..8.
/// </summary>
public interface INdScalarFunction
{
    int Dimension { get; }

    bool TryEvaluate(scoped ReadOnlySpan<double> x, ref DslExecutionBudget budget, out double y);
}
