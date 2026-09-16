using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.ZeroAllocation
{
    /// <summary>
    /// Chart data type enumeration for zero-allocation chart architecture.
    /// </summary>
    public enum ZeroAllocChartDataType
    {
        TimeSeries, Renko, Kagi, PointAndFigure, Range, Tick
    }

    /// <summary>
    /// Zero-allocation candle data structure using readonly record struct.
    /// Uses different name to avoid collision with CandleData.
    /// </summary>
    public readonly record struct ZeroAllocCandleData(
        DateTime Timestamp,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long Volume);

    /// <summary>
    /// Represents a range of indices in the original data source.
    /// </summary>
    public readonly record struct OriginalIndexRange(int Start, int Length)
    {
        public bool IsEmpty => Length == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<int> GetIndices(Span<int> destination)
        {
            if (Length > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(destination));
            
            for (int i = 0; i < Length; i++)
                destination[i] = Start + i;
            
            return destination.Slice(0, Length);
        }
    }

    /// <summary>
    /// Interface for zero-allocation chart data adapters.
    /// Provides memory-based access to OHLCV data.
    /// </summary>
    public interface IZeroAllocChartDataAdapter
    {
        ZeroAllocChartDataType DataType { get; }
        int Count { get; }

        ReadOnlyMemory<DateTime> Timestamps { get; }
        ReadOnlyMemory<decimal> Opens { get; }
        ReadOnlyMemory<decimal> Highs { get; }
        ReadOnlyMemory<decimal> Lows { get; }
        ReadOnlyMemory<decimal> Closes { get; }
        ReadOnlyMemory<long> Volumes { get; }
        ReadOnlyMemory<OriginalIndexRange> OriginalRanges { get; }

        (decimal max, decimal min) GetPriceRange(int startIndex = 0, int? endIndex = null);
    }

    /// <summary>
    /// Time series adapter for zero-allocation chart data.
    /// Converts candle data array to memory-based format.
    /// </summary>
    public sealed class TimeSeriesAdapter : IZeroAllocChartDataAdapter
    {
        private readonly DateTime[] _timestamps;
        private readonly decimal[] _opens;
        private readonly decimal[] _highs;
        private readonly decimal[] _lows;
        private readonly decimal[] _closes;
        private readonly long[] _volumes;
        private readonly OriginalIndexRange[] _ranges;

        public ZeroAllocChartDataType DataType => ZeroAllocChartDataType.TimeSeries;
        public int Count => _closes.Length;

        public ReadOnlyMemory<DateTime> Timestamps => _timestamps;
        public ReadOnlyMemory<decimal> Opens => _opens;
        public ReadOnlyMemory<decimal> Highs => _highs;
        public ReadOnlyMemory<decimal> Lows => _lows;
        public ReadOnlyMemory<decimal> Closes => _closes;
        public ReadOnlyMemory<long> Volumes => _volumes;
        public ReadOnlyMemory<OriginalIndexRange> OriginalRanges => _ranges;

        public TimeSeriesAdapter(ReadOnlyMemory<ZeroAllocCandleData> candles)
        {
            var span = candles.Span;
            int n = span.Length;

            _timestamps = new DateTime[n];
            _opens = new decimal[n];
            _highs = new decimal[n];
            _lows = new decimal[n];
            _closes = new decimal[n];
            _volumes = new long[n];
            _ranges = new OriginalIndexRange[n];

            for (int i = 0; i < n; i++)
            {
                var c = span[i];
                _timestamps[i] = c.Timestamp;
                _opens[i] = c.Open;
                _highs[i] = c.High;
                _lows[i] = c.Low;
                _closes[i] = c.Close;
                _volumes[i] = c.Volume;
                _ranges[i] = new OriginalIndexRange(i, 1);
            }
        }

        public (decimal max, decimal min) GetPriceRange(int startIndex = 0, int? endIndex = null)
        {
            int end = endIndex ?? Count;
            if (startIndex < 0 || end > Count || startIndex >= end)
                throw new ArgumentOutOfRangeException();

            var highs = _highs.AsSpan(startIndex, end - startIndex);
            var lows = _lows.AsSpan(startIndex, end - startIndex);

            decimal max = decimal.MinValue;
            decimal min = decimal.MaxValue;

            for (int i = 0; i < highs.Length; i++)
            {
                if (highs[i] > max) max = highs[i];
                if (lows[i] < min) min = lows[i];
            }

            return (max, min);
        }
    }

    /// <summary>
    /// Chart segment adapter for non-time-series data (Renko, Kagi, etc.).
    /// </summary>
    public sealed class ChartSegmentAdapter : IZeroAllocChartDataAdapter
    {
        private readonly ZeroAllocChartDataType _dataType;
        private readonly DateTime[] _timestamps;
        private readonly decimal[] _opens;
        private readonly decimal[] _highs;
        private readonly decimal[] _lows;
        private readonly decimal[] _closes;
        private readonly long[] _volumes;
        private readonly OriginalIndexRange[] _ranges;

        public ZeroAllocChartDataType DataType => _dataType;
        public int Count => _closes.Length;

        public ReadOnlyMemory<DateTime> Timestamps => _timestamps;
        public ReadOnlyMemory<decimal> Opens => _opens;
        public ReadOnlyMemory<decimal> Highs => _highs;
        public ReadOnlyMemory<decimal> Lows => _lows;
        public ReadOnlyMemory<decimal> Closes => _closes;
        public ReadOnlyMemory<long> Volumes => _volumes;
        public ReadOnlyMemory<OriginalIndexRange> OriginalRanges => _ranges;

        public ChartSegmentAdapter(
            ZeroAllocChartDataType dataType,
            DateTime[] timestamps,
            decimal[] opens,
            decimal[] highs,
            decimal[] lows,
            decimal[] closes,
            long[] volumes,
            OriginalIndexRange[] ranges)
        {
            if (timestamps == null) throw new ArgumentNullException(nameof(timestamps));
            if (opens == null) throw new ArgumentNullException(nameof(opens));
            if (highs == null) throw new ArgumentNullException(nameof(highs));
            if (lows == null) throw new ArgumentNullException(nameof(lows));
            if (closes == null) throw new ArgumentNullException(nameof(closes));
            if (volumes == null) throw new ArgumentNullException(nameof(volumes));
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));

            int n = closes.Length;
            if (timestamps.Length != n || opens.Length != n || highs.Length != n ||
                lows.Length != n || volumes.Length != n || ranges.Length != n)
                throw new ArgumentException("All arrays must have the same length");

            _dataType = dataType;
            _timestamps = (DateTime[])timestamps.Clone();
            _opens = (decimal[])opens.Clone();
            _highs = (decimal[])highs.Clone();
            _lows = (decimal[])lows.Clone();
            _closes = (decimal[])closes.Clone();
            _volumes = (long[])volumes.Clone();
            _ranges = (OriginalIndexRange[])ranges.Clone();
        }

        internal ChartSegmentAdapter(
            ZeroAllocChartDataType dataType,
            DateTime[] timestamps,
            decimal[] opens,
            decimal[] highs,
            decimal[] lows,
            decimal[] closes,
            long[] volumes,
            OriginalIndexRange[] ranges,
            bool bypassClone)
        {
            _dataType = dataType;
            if (bypassClone)
            {
                _timestamps = timestamps;
                _opens = opens;
                _highs = highs;
                _lows = lows;
                _closes = closes;
                _volumes = volumes;
                _ranges = ranges;
            }
            else
            {
                _timestamps = (DateTime[])timestamps.Clone();
                _opens = (decimal[])opens.Clone();
                _highs = (decimal[])highs.Clone();
                _lows = (decimal[])lows.Clone();
                _closes = (decimal[])closes.Clone();
                _volumes = (long[])volumes.Clone();
                _ranges = (OriginalIndexRange[])ranges.Clone();
            }
        }

        public (decimal max, decimal min) GetPriceRange(int startIndex = 0, int? endIndex = null)
        {
            if (Count == 0) return (1m, 0m);
            int end = endIndex ?? Count;
            if (startIndex < 0) startIndex = 0;
            if (end > Count) end = Count;
            if (startIndex >= end) return (1m, 0m);

            var highs = _highs.AsSpan(startIndex, end - startIndex);
            var lows = _lows.AsSpan(startIndex, end - startIndex);

            decimal max = decimal.MinValue;
            decimal min = decimal.MaxValue;

            for (int i = 0; i < highs.Length; i++)
            {
                if (highs[i] > max) max = highs[i];
                if (lows[i] < min) min = lows[i];
            }

            return (max, min);
        }
    }

    /// <summary>
    /// Delegate for creating chart data adapters.
    /// </summary>
    public delegate IZeroAllocChartDataAdapter ChartAdapterCreator(ReadOnlyMemory<ZeroAllocCandleData> candles, object? parameters);

    /// <summary>
    /// Renko chart parameters.
    /// </summary>
    public sealed record RenkoParameters(decimal BlockSize = 10m, int ReversalBricks = 2, ChartRoundingMode RoundingMode = ChartRoundingMode.None);

    /// <summary>
    /// Kagi chart parameters.
    /// </summary>
    public sealed record KagiParameters(decimal ReversalAmount = 5m, ChartRoundingMode RoundingMode = ChartRoundingMode.None);

    /// <summary>
    /// PointAndFigure chart parameters.
    /// </summary>
    public sealed record PointAndFigureParameters(decimal BoxSize = 10m, int ReversalAmount = 3, ChartRoundingMode RoundingMode = ChartRoundingMode.None);

    /// <summary>
    /// Factory for creating chart data adapters.
    /// Supports registration of custom chart types.
    /// </summary>
    public static class ZeroAllocChartDataFactory
    {
        private static readonly ConcurrentDictionary<ZeroAllocChartDataType, ChartAdapterCreator> _providers = new();
        private static bool _initialized;
        private static readonly object _initLock = new();

        static ZeroAllocChartDataFactory()
        {
            Register(ZeroAllocChartDataType.TimeSeries, (candles, _) => new TimeSeriesAdapter(candles));
            
            Register(ZeroAllocChartDataType.Renko, (candles, parameters) =>
            {
                var renkoParams = parameters as RenkoParameters ?? new RenkoParameters();
                return RenkoCalculator.Calculate(candles, renkoParams);
            });
            
            Register(ZeroAllocChartDataType.Kagi, (candles, parameters) =>
            {
                var kagiParams = parameters as KagiParameters ?? new KagiParameters();
                return KagiCalculator.Calculate(candles, kagiParams);
            });

            Register(ZeroAllocChartDataType.PointAndFigure, (candles, parameters) =>
            {
                var pnfParams = parameters as PointAndFigureParameters ?? new PointAndFigureParameters();
                return PointAndFigureCalculator.Calculate(candles, pnfParams);
            });
        }

        public static void Initialize(Action? registrations)
        {
            lock (_initLock)
            {
                if (_initialized)
                    throw new InvalidOperationException("Factory already initialized");
                
                registrations?.Invoke();
                _initialized = true;
            }
        }

        public static void Register(ZeroAllocChartDataType type, ChartAdapterCreator creator)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            
            _providers.AddOrUpdate(type, creator, (_, __) => creator);
        }

        public static IZeroAllocChartDataAdapter Create(
            ReadOnlyMemory<ZeroAllocCandleData> candles,
            ZeroAllocChartDataType chartType,
            object? parameters = null)
        {
            if (_providers.TryGetValue(chartType, out var creator))
                return creator.Invoke(candles, parameters);

            throw new NotSupportedException(
                $"No adapter creator registered for chart type: {chartType}. " +
                $"Available types: {string.Join(", ", _providers.Keys)}");
        }

        public static bool IsRegistered(ZeroAllocChartDataType type) => _providers.ContainsKey(type);

        public static IEnumerable<ZeroAllocChartDataType> GetRegisteredTypes() => _providers.Keys.ToArray();
    }

    /// <summary>
    /// Interface for pooled candle buffer.
    /// </summary>
    public interface IPooledCandleBuffer : IDisposable
    {
        ZeroAllocCandleData[] Buffer { get; }
        int Length { get; }
    }

    /// <summary>
    /// Pooled candle buffer using ArrayPool for zero-allocation.
    /// </summary>
    public sealed class PooledCandleBuffer : IPooledCandleBuffer
    {
        private ZeroAllocCandleData[]? _buffer;
        private readonly int _length;
        private bool _disposed;

        public ZeroAllocCandleData[] Buffer
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PooledCandleBuffer));
                return _buffer!;
            }
        }

        public int Length => _length;

        public PooledCandleBuffer(int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            _length = length;
            _buffer = ArrayPool<ZeroAllocCandleData>.Shared.Rent(length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            var buffer = _buffer;
            if (buffer != null)
            {
                _buffer = null;
                ArrayPool<ZeroAllocCandleData>.Shared.Return(buffer, clearArray: true);
            }
            
            _disposed = true;
        }
    }

    /// <summary>
    /// Bridge for converting between legacy and zero-allocation data.
    /// </summary>
    [Obsolete("IndicatorBridge should use pooled buffers. Use IndicatorBridgePooled for zero-allocation compatibility.", false)]
    public static class IndicatorBridge
    {
        public static List<ZeroAllocCandleData> ToCandleList(IZeroAllocChartDataAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            
            int n = adapter.Count;
            var list = new List<ZeroAllocCandleData>(n);

            var ts = adapter.Timestamps.Span;
            var opens = adapter.Opens.Span;
            var highs = adapter.Highs.Span;
            var lows = adapter.Lows.Span;
            var closes = adapter.Closes.Span;
            var volumes = adapter.Volumes.Span;

            for (int i = 0; i < n; i++)
            {
                list.Add(new ZeroAllocCandleData(ts[i], opens[i], highs[i], lows[i], closes[i], volumes[i]));
            }

            return list;
        }

        public static void Calculate(ZeroAllocLegacyIndicatorBase indicator, IZeroAllocChartDataAdapter adapter)
        {
            if (indicator == null) throw new ArgumentNullException(nameof(indicator));
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));

            var list = ToCandleList(adapter);
            indicator.Calculate(list);
        }
    }

    /// <summary>
    /// Pooled bridge for zero-allocation indicator calculation.
    /// </summary>
    public static class IndicatorBridgePooled
    {
        public static IPooledCandleBuffer ToPooledBuffer(IZeroAllocChartDataAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            
            int n = adapter.Count;
            var pooled = new PooledCandleBuffer(n);
            var buffer = pooled.Buffer;

            var ts = adapter.Timestamps.Span;
            var opens = adapter.Opens.Span;
            var highs = adapter.Highs.Span;
            var lows = adapter.Lows.Span;
            var closes = adapter.Closes.Span;
            var volumes = adapter.Volumes.Span;

            for (int i = 0; i < n; i++)
            {
                buffer[i] = new ZeroAllocCandleData(ts[i], opens[i], highs[i], lows[i], closes[i], volumes[i]);
            }

            return pooled;
        }

        public static void Calculate(ZeroAllocArrayIndicatorBase indicator, IZeroAllocChartDataAdapter adapter)
        {
            if (indicator == null) throw new ArgumentNullException(nameof(indicator));
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));

            using var pooled = ToPooledBuffer(adapter);
            indicator.Calculate(pooled.Buffer, pooled.Length);
        }

        /// <summary>
        /// Extracts close prices from the adapter into a pooled array.
        /// </summary>
        public static IPooledArrayOwner<decimal> ToClosePriceBuffer(IZeroAllocChartDataAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            
            int n = adapter.Count;
            var owner = new PooledArrayOwner<decimal>(n);
            adapter.Closes.Span.CopyTo(owner.Span);
            return owner;
        }
    }

    /// <summary>
    /// Specialized adapter for mapping Renko/Index-based data to indicator input.
    /// Manages pooled buffers for indicator calculation.
    /// </summary>
    public sealed class RenkoIndicatorDataAdapter : IDisposable
    {
        private readonly IZeroAllocChartDataAdapter _adapter;
        private IPooledCandleBuffer? _pooledBuffer;
        private bool _disposed;

        public RenkoIndicatorDataAdapter(IZeroAllocChartDataAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        /// <summary>
        /// Gets or creates a pooled buffer of ZeroAllocCandleData from the adapter.
        /// Useful for indicators requiring full OHLCV (e.g. ATR, Bollinger Bands).
        /// </summary>
        public ReadOnlyMemory<ZeroAllocCandleData> GetCandleData()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RenkoIndicatorDataAdapter));
            
            if (_pooledBuffer == null)
            {
                _pooledBuffer = IndicatorBridgePooled.ToPooledBuffer(_adapter);
            }
            
            return _pooledBuffer.Buffer.AsMemory(0, _pooledBuffer.Length);
        }

        /// <summary>
        /// Extracts only the Close prices into a pooled array.
        /// Useful for simple indicators like SMA/EMA/RSI.
        /// </summary>
        public IPooledArrayOwner<decimal> GetClosePrices()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RenkoIndicatorDataAdapter));

            return IndicatorBridgePooled.ToClosePriceBuffer(_adapter);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _pooledBuffer?.Dispose();
            _pooledBuffer = null;
            _disposed = true;
        }
    }

    /// <summary>
    /// Unified indicator base for zero-allocation architecture.
    /// </summary>
    public abstract class ZeroAllocUnifiedIndicatorBase
    {
        protected IZeroAllocChartDataAdapter? _dataAdapter;

        public void Calculate(IZeroAllocChartDataAdapter dataAdapter)
        {
            _dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter));
            CalculateCore();
        }

        protected abstract void CalculateCore();
    }

    /// <summary>
    /// Legacy indicator base for list-based calculation.
    /// </summary>
    public abstract class ZeroAllocLegacyIndicatorBase
    {
        public abstract void Calculate(List<ZeroAllocCandleData> candles);
    }

    /// <summary>
    /// Array-based indicator base for zero-allocation calculation.
    /// </summary>
    public abstract class ZeroAllocArrayIndicatorBase
    {
        public abstract void Calculate(ZeroAllocCandleData[] candles, int length);
    }

    /// <summary>
    /// Interface for pooled array owner.
    /// </summary>
    public interface IPooledArrayOwner<T> : IDisposable where T : struct
    {
        Memory<T> Memory { get; }
        Span<T> Span { get; }
        int Length { get; }
    }

    /// <summary>
    /// Pooled array owner for zero-allocation patterns.
    /// </summary>
    public sealed class PooledArrayOwner<T> : IPooledArrayOwner<T> where T : struct
    {
        private T[]? _array;
        private readonly int _length;
        private bool _disposed;

        public Memory<T> Memory
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PooledArrayOwner<T>));
                return _array!.AsMemory(0, _length);
            }
        }

        public Span<T> Span
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PooledArrayOwner<T>));
                return _array!.AsSpan(0, _length);
            }
        }

        public int Length => _length;

        public PooledArrayOwner(int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            _length = length;
            _array = ArrayPool<T>.Shared.Rent(length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            var array = _array;
            if (array != null)
            {
                _array = null;
                ArrayPool<T>.Shared.Return(array, clearArray: true);
            }
            
            _disposed = true;
        }
    }
    /// <summary>
    /// Renko calculator implementation.
    /// </summary>
    public static class RenkoCalculator
    {
        public static ChartSegmentAdapter Calculate(ReadOnlyMemory<ZeroAllocCandleData> source, RenkoParameters parameters)
        {
            if (source.Length == 0)
                throw new ArgumentException("Source cannot be empty", nameof(source));

            var span = source.Span;
            var blockSize = parameters.BlockSize;
            var reversalBricks = parameters.ReversalBricks;
            var roundingMode = parameters.RoundingMode;

            if (blockSize <= 0) blockSize = 1; // Prevent infinite loop
            if (reversalBricks <= 0) reversalBricks = 1;

            // Phase 2 Zero Allocation Fix: Use ArrayPool instead of List<T> for intermediate calculations
            // Estimate initial capacity (e.g. half of source length, min 64)
            int capacity = Math.Max(64, span.Length / 2);
            
            var timestamps = System.Buffers.ArrayPool<DateTime>.Shared.Rent(capacity);
            var opens = System.Buffers.ArrayPool<decimal>.Shared.Rent(capacity);
            var highs = System.Buffers.ArrayPool<decimal>.Shared.Rent(capacity);
            var lows = System.Buffers.ArrayPool<decimal>.Shared.Rent(capacity);
            var closes = System.Buffers.ArrayPool<decimal>.Shared.Rent(capacity);
            var volumes = System.Buffers.ArrayPool<long>.Shared.Rent(capacity);
            var ranges = System.Buffers.ArrayPool<OriginalIndexRange>.Shared.Rent(capacity);
            
            int count = 0;

            try
            {
                // Local helper to add a brick and manage array resizing
                void AddBrick(DateTime time, decimal open, decimal high, decimal low, decimal close, ref long vol, int startIdx, int endIdx)
                {
                    if (count >= capacity)
                    {
                        int newCapacity = capacity * 2;
                        
                        var newTimestamps = System.Buffers.ArrayPool<DateTime>.Shared.Rent(newCapacity);
                        var newOpens = System.Buffers.ArrayPool<decimal>.Shared.Rent(newCapacity);
                        var newHighs = System.Buffers.ArrayPool<decimal>.Shared.Rent(newCapacity);
                        var newLows = System.Buffers.ArrayPool<decimal>.Shared.Rent(newCapacity);
                        var newCloses = System.Buffers.ArrayPool<decimal>.Shared.Rent(newCapacity);
                        var newVolumes = System.Buffers.ArrayPool<long>.Shared.Rent(newCapacity);
                        var newRanges = System.Buffers.ArrayPool<OriginalIndexRange>.Shared.Rent(newCapacity);

                        Array.Copy(timestamps, newTimestamps, count);
                        Array.Copy(opens, newOpens, count);
                        Array.Copy(highs, newHighs, count);
                        Array.Copy(lows, newLows, count);
                        Array.Copy(closes, newCloses, count);
                        Array.Copy(volumes, newVolumes, count);
                        Array.Copy(ranges, newRanges, count);

                        System.Buffers.ArrayPool<DateTime>.Shared.Return(timestamps);
                        System.Buffers.ArrayPool<decimal>.Shared.Return(opens);
                        System.Buffers.ArrayPool<decimal>.Shared.Return(highs);
                        System.Buffers.ArrayPool<decimal>.Shared.Return(lows);
                        System.Buffers.ArrayPool<decimal>.Shared.Return(closes);
                        System.Buffers.ArrayPool<long>.Shared.Return(volumes);
                        System.Buffers.ArrayPool<OriginalIndexRange>.Shared.Return(ranges);

                        timestamps = newTimestamps;
                        opens = newOpens;
                        highs = newHighs;
                        lows = newLows;
                        closes = newCloses;
                        volumes = newVolumes;
                        ranges = newRanges;
                        capacity = newCapacity;
                    }

                    timestamps[count] = time;
                    opens[count] = open;
                    highs[count] = high;
                    lows[count] = low;
                    closes[count] = close;
                    volumes[count] = vol;
                    ranges[count] = new OriginalIndexRange(startIdx, endIdx);
                    
                    count++;
                    vol = 0; // reset volume
                }

                var first = span[0];
                // Apply rounding mode for grid alignment
                decimal currentLow = (roundingMode == ChartRoundingMode.None)
                    ? Math.Floor(first.Close / blockSize) * blockSize
                    : ChartMath.Quantize(first.Close, blockSize, roundingMode);
                decimal currentHigh = currentLow + blockSize;
                
                // 0 = Unknown, 1 = Up, -1 = Down
                int direction = 0; 
                int startIndex = 0;
                long currentVolume = 0;

                for (int i = 0; i < span.Length; i++)
                {
                    var c = span[i];
                    decimal closePrice = c.Close;
                    currentVolume += c.Volume;

                    while (true)
                    {
                        if (direction == 0)
                        {
                            if (closePrice >= currentHigh + blockSize)
                            {
                                direction = 1;
                                decimal brickStart = currentHigh;
                                decimal brickEnd = currentHigh + blockSize;
                                
                                AddBrick(c.Timestamp, brickStart, brickEnd, brickStart, brickEnd, ref currentVolume, startIndex, i);
                                    
                                currentHigh = brickEnd;
                                currentLow = brickStart;
                                startIndex = i;
                                continue;
                            }
                            else if (closePrice <= currentLow - blockSize)
                            {
                                direction = -1;
                                decimal brickStart = currentLow;
                                decimal brickEnd = currentLow - blockSize;
                                
                                AddBrick(c.Timestamp, brickStart, brickStart, brickEnd, brickEnd, ref currentVolume, startIndex, i);
                                    
                                currentHigh = brickStart;
                                currentLow = brickEnd;
                                startIndex = i;
                                continue;
                            }
                        }
                        else if (direction == 1)
                        {
                            if (closePrice >= currentHigh + blockSize)
                            {
                                decimal brickStart = currentHigh;
                                decimal brickEnd = currentHigh + blockSize;
                                
                                AddBrick(c.Timestamp, brickStart, brickEnd, brickStart, brickEnd, ref currentVolume, startIndex, i);
                                    
                                currentHigh = brickEnd;
                                currentLow = brickStart;
                                startIndex = i;
                                continue;
                            }
                            else if (closePrice <= currentLow - (blockSize * reversalBricks))
                            {
                                direction = -1;
                                // Reversal skips the vertical area of the previous brick
                                decimal brickStart = currentLow;
                                decimal brickEnd = currentLow - blockSize;
                                
                                AddBrick(c.Timestamp, brickStart, brickStart, brickEnd, brickEnd, ref currentVolume, startIndex, i);
                                    
                                currentHigh = brickStart;
                                currentLow = brickEnd;
                                startIndex = i;
                                continue;
                            }
                        }
                        else if (direction == -1)
                        {
                            if (closePrice <= currentLow - blockSize)
                            {
                                decimal brickStart = currentLow;
                                decimal brickEnd = currentLow - blockSize;
                                
                                AddBrick(c.Timestamp, brickStart, brickStart, brickEnd, brickEnd, ref currentVolume, startIndex, i);
                                    
                                currentHigh = brickStart;
                                currentLow = brickEnd;
                                startIndex = i;
                                continue;
                            }
                            else if (closePrice >= currentHigh + (blockSize * reversalBricks))
                            {
                                direction = 1;
                                // Reversal skips the vertical area of the previous brick
                                decimal brickStart = currentHigh;
                                decimal brickEnd = currentHigh + blockSize;
                                
                                AddBrick(c.Timestamp, brickStart, brickEnd, brickStart, brickEnd, ref currentVolume, startIndex, i);
                                    
                                currentHigh = brickEnd;
                                currentLow = brickStart;
                                startIndex = i;
                                continue;
                            }
                        }
                        break;
                    }
                }

                // ChartSegmentAdapter clones the arrays natively, so we must slice them to the actual 'count'
                // so it clones only the populated portion.
                return new ChartSegmentAdapter(
                    ZeroAllocChartDataType.Renko,
                    timestamps.AsSpan(0, count).ToArray(),
                    opens.AsSpan(0, count).ToArray(),
                    highs.AsSpan(0, count).ToArray(),
                    lows.AsSpan(0, count).ToArray(),
                    closes.AsSpan(0, count).ToArray(),
                    volumes.AsSpan(0, count).ToArray(),
                    ranges.AsSpan(0, count).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<DateTime>.Shared.Return(timestamps);
                System.Buffers.ArrayPool<decimal>.Shared.Return(opens);
                System.Buffers.ArrayPool<decimal>.Shared.Return(highs);
                System.Buffers.ArrayPool<decimal>.Shared.Return(lows);
                System.Buffers.ArrayPool<decimal>.Shared.Return(closes);
                System.Buffers.ArrayPool<long>.Shared.Return(volumes);
                System.Buffers.ArrayPool<OriginalIndexRange>.Shared.Return(ranges);
            }
        }

        private static void AddBrick(
            List<DateTime> timestamps, List<decimal> opens, List<decimal> highs,
            List<decimal> lows, List<decimal> closes, List<long> volumes,
            List<OriginalIndexRange> ranges,
            DateTime timestamp, decimal open, decimal close,
            ref long currentVolume, int startIndex, int currentIndex)
        {
            timestamps.Add(timestamp);
            opens.Add(open);
            closes.Add(close);
            highs.Add(Math.Max(open, close));
            lows.Add(Math.Min(open, close));
            volumes.Add(currentVolume);
            ranges.Add(new OriginalIndexRange(startIndex, currentIndex - startIndex + 1));
            
            // Note: If we add multiple bricks for a single candle, the volume belongs to the first brick.
            // Clear out accumulated volume to avoid double routing it to subsequent bricks in the same tick.
            currentVolume = 0;
        }
    }

    /// <summary>
    /// PointAndFigure calculator implementation.
    /// </summary>
    public static class PointAndFigureCalculator
    {
        public static ChartSegmentAdapter Calculate(ReadOnlyMemory<ZeroAllocCandleData> source, PointAndFigureParameters parameters)
        {
            if (source.Length == 0)
                throw new ArgumentException("Source cannot be empty", nameof(source));

            var span = source.Span;
            var boxSize = parameters.BoxSize;
            var reversalAmount = parameters.ReversalAmount;

            if (boxSize <= 0) boxSize = 1;

            var timestamps = new List<DateTime>();
            var opens = new List<decimal>();
            var highs = new List<decimal>();
            var lows = new List<decimal>();
            var closes = new List<decimal>();
            var volumes = new List<long>();
            var ranges = new List<OriginalIndexRange>();

            // P&F logic:
            // 1. Determine direction (Up/Down).
            // 2. If same direction, add boxes if price moves enough (1 box).
            // 3. If opposite direction, only reverse if price moves ReversalAmount * BoxSize.
            
            // Initial state
            var first = span[0];
            decimal currentLevel = first.Close;
            
            // Align to BoxSize grid using specified rounding mode
            currentLevel = (parameters.RoundingMode == ChartRoundingMode.None)
                ? Math.Floor(currentLevel / boxSize) * boxSize
                : ChartMath.Quantize(currentLevel, boxSize, parameters.RoundingMode);

            int direction = 0; // 1: Up (X), -1: Down (O)
            int startIndex = 0;
            long currentVolume = 0;

            // Current column data
            decimal columnHigh = currentLevel;
            decimal columnLow = currentLevel;

            for (int i = 0; i < span.Length; i++)
            {
                var c = span[i];
                decimal high = c.High;
                decimal low = c.Low;
                decimal close = c.Close; 
                // Traditional P&F uses High/Low, but sometimes just Close.
                // Assuming High/Low for standard P&F.
                
                currentVolume += c.Volume;

                if (direction == 0)
                {
                    // Initial direction determination
                    decimal quantizedHigh = (parameters.RoundingMode == ChartRoundingMode.None)
                        ? Math.Floor(high / boxSize) * boxSize
                        : ChartMath.Quantize(high, boxSize, parameters.RoundingMode);
                    
                    decimal quantizedLow = (parameters.RoundingMode == ChartRoundingMode.None)
                        ? Math.Ceiling(low / boxSize) * boxSize
                        : ChartMath.Quantize(low, boxSize, parameters.RoundingMode);

                    decimal highDist = quantizedHigh - currentLevel;
                    decimal lowDist = currentLevel - quantizedLow;

                    if (highDist >= boxSize)
                    {
                        direction = 1; // Up
                        columnHigh = quantizedHigh;
                        columnLow = currentLevel;
                    }
                    else if (lowDist >= boxSize)
                    {
                        direction = -1; // Down
                        columnLow = quantizedLow;
                        columnHigh = currentLevel;
                    }
                }
                else if (direction == 1) // Up (X)
                {
                    // Check for continuation
                    decimal newHigh = (parameters.RoundingMode == ChartRoundingMode.None)
                        ? Math.Floor(high / boxSize) * boxSize
                        : ChartMath.Quantize(high, boxSize, parameters.RoundingMode);
                    if (newHigh > columnHigh)
                    {
                        // Add X's
                        columnHigh = newHigh;
                    }
                    
                    // Check for reversal
                    decimal reversalLevel = columnHigh - (reversalAmount * boxSize);
                    decimal quantizedLow = (parameters.RoundingMode == ChartRoundingMode.None)
                        ? Math.Ceiling(low / boxSize) * boxSize
                        : ChartMath.Quantize(low, boxSize, parameters.RoundingMode);
                    
                    // Standard: if Low <= reversalLevel
                    if (low <= reversalLevel) // Strict price check
                    {
                        // Commit current UP column
                        AddColumn(timestamps, opens, highs, lows, closes, volumes, ranges,
                            span[startIndex].Timestamp, columnLow, columnHigh, 1, currentVolume, startIndex, i - startIndex);
                        
                        // Start new DOWN column
                        direction = -1;
                        startIndex = i;
                        currentVolume = c.Volume; 
                        
                        // The new column starts one box below the previous high?
                        // Standard P&F: New O column starts one box below the high of the previous X column.
                        columnHigh = columnHigh - boxSize; 
                        
                        // And goes down to at least the low?
                        // The low executed this period triggered the reversal.
                        columnLow = quantizedLow;
                    }
                }
                else if (direction == -1) // Down (O)
                {
                    // Check for continuation
                    decimal quantizedLow = (parameters.RoundingMode == ChartRoundingMode.None)
                        ? Math.Ceiling(low / boxSize) * boxSize
                        : ChartMath.Quantize(low, boxSize, parameters.RoundingMode);
                    
                    if (quantizedLow < columnLow)
                    {
                        columnLow = quantizedLow;
                    }

                    // Check for reversal
                    decimal reversalLevel = columnLow + (reversalAmount * boxSize);
                    if (high >= reversalLevel)
                    {
                        // Commit current DOWN column
                        AddColumn(timestamps, opens, highs, lows, closes, volumes, ranges,
                            span[startIndex].Timestamp, columnHigh, columnLow, -1, currentVolume, startIndex, i - startIndex);

                        // Start new UP column
                        direction = 1;
                        startIndex = i;
                        currentVolume = c.Volume;

                        // New X column starts one box above the low of the previous O column
                        columnLow = columnLow + boxSize;
                        columnHigh = (parameters.RoundingMode == ChartRoundingMode.None)
                            ? Math.Floor(high / boxSize) * boxSize
                            : ChartMath.Quantize(high, boxSize, parameters.RoundingMode);
                    }
                }
            }
            
            // Commit final column
             AddColumn(timestamps, opens, highs, lows, closes, volumes, ranges,
                   span[startIndex].Timestamp, 
                   (direction == 1) ? columnLow : columnHigh, // Open (Base)
                   (direction == 1) ? columnHigh : columnLow, // Close (Tip)
                   direction, currentVolume, startIndex, span.Length - startIndex);


            return new ChartSegmentAdapter(
                ZeroAllocChartDataType.PointAndFigure,
                timestamps.ToArray(),
                opens.ToArray(),
                highs.ToArray(),
                lows.ToArray(),
                closes.ToArray(),
                volumes.ToArray(),
                ranges.ToArray());
        }

        private static void AddColumn(
             List<DateTime> ts, List<decimal> op, List<decimal> hi, List<decimal> lo, List<decimal> cl,
             List<long> vol, List<OriginalIndexRange> rg,
             DateTime t, decimal startLevel, decimal endLevel, int direction, long v, int start, int len)
        {
            ts.Add(t);
            // Adapter standard:
            // Up: Open < Close. Open = Low, Close = High
            // Down: Open > Close. Open = High, Close = Low
            
            // P&F X Column (Up): Starts at 'startLevel' (bottom), goes to 'endLevel' (top)
            // P&F O Column (Down): Starts at 'startLevel' (top), goes to 'endLevel' (bottom)
            
            // Wait, my logic above for O column uses columnHigh/columnLow which are absolute.
            // Let's normalize.
            
            if (direction == 1) // Up
            {
                // startLevel should be Low, endLevel should be High
                op.Add(startLevel);
                cl.Add(endLevel);
                hi.Add(endLevel);
                lo.Add(startLevel);
            }
            else // Down
            {
                // startLevel should be High, endLevel should be Low
                op.Add(startLevel);
                cl.Add(endLevel);
                hi.Add(startLevel); 
                lo.Add(endLevel); 
            }
            
            vol.Add(v);
            rg.Add(new OriginalIndexRange(start, len));
        }
    }

    /// <summary>
    /// Kagi calculator implementation.
    /// </summary>
    public static class KagiCalculator
    {
        private struct KagiState
        {
            public decimal CurrentPrice { get; set; }
            public int Direction { get; set; } // 1 = Up, -1 = Down
            public bool IsYang { get; set; }
            public decimal LastHigh { get; set; }
            public decimal LastLow { get; set; }
            public DateTime CurrentTime { get; set; }
        }

        public static ChartSegmentAdapter Calculate(ReadOnlyMemory<ZeroAllocCandleData> source, KagiParameters parameters)
        {
            if (source.Length == 0)
                throw new ArgumentException("Source cannot be empty", nameof(source));

            var span = source.Span;
            var reversalAmount = parameters.ReversalAmount;

            // Rent arrays that are guaranteed to be large enough (source.Length * 2 is extremely safe for splits)
            int capacity = span.Length * 2 + 10;
            var tsPool = ArrayPool<DateTime>.Shared.Rent(capacity);
            var opPool = ArrayPool<decimal>.Shared.Rent(capacity);
            var hiPool = ArrayPool<decimal>.Shared.Rent(capacity);
            var loPool = ArrayPool<decimal>.Shared.Rent(capacity);
            var clPool = ArrayPool<decimal>.Shared.Rent(capacity);
            var volPool = ArrayPool<long>.Shared.Rent(capacity);
            var rgPool = ArrayPool<OriginalIndexRange>.Shared.Rent(capacity);

            int segmentCount = 0;

            try
            {
                var startPrice = span[0].Close;
                var state = new KagiState
                {
                    CurrentPrice = startPrice,
                    Direction = 0,
                    IsYang = false,
                    LastHigh = decimal.MinValue,
                    LastLow = decimal.MaxValue,
                    CurrentTime = span[0].Timestamp
                };

                int startIndex = 0;

                // Standard: Always insert the starting close price at index 0 as a horizontal anchor block
                AddSegment(tsPool, opPool, hiPool, loPool, clPool, volPool, rgPool, ref segmentCount,
                    span[0].Timestamp, startPrice, startPrice, false, 0, 1);

                for (int i = 1; i < span.Length; i++)
                {
                    var c = span[i];
                    var price = c.Close;

                    if (state.Direction == 0)
                    {
                        if (Math.Abs(price - state.CurrentPrice) >= reversalAmount)
                        {
                            state.Direction = price > state.CurrentPrice ? 1 : -1;
                            state.IsYang = state.Direction == 1;

                            // Add the first trend segment at index 1, originating from startPrice and ending at price
                            AddSegment(tsPool, opPool, hiPool, loPool, clPool, volPool, rgPool, ref segmentCount,
                                c.Timestamp, startPrice, price, state.IsYang, 0, i + 1);

                            state.CurrentPrice = price;
                            state.CurrentTime = c.Timestamp;
                            startIndex = i;
                        }
                    }
                    else
                    {
                        if ((state.Direction == 1 && price > state.CurrentPrice) ||
                            (state.Direction == -1 && price < state.CurrentPrice))
                        {
                            HandleContinuationWithPotentialSplit(tsPool, opPool, hiPool, loPool, clPool, volPool, rgPool, ref segmentCount,
                                ref state, price, c.Timestamp, startIndex, i - startIndex + 1);
                            startIndex = i;
                        }
                        else if ((state.Direction == 1 && price <= state.CurrentPrice - reversalAmount) ||
                                 (state.Direction == -1 && price >= state.CurrentPrice + reversalAmount))
                        {
                            // Reversal confirmed!
                            // 1. First record exact extreme high/low (CurrentPrice)
                            var extremePrice = state.CurrentPrice;
                            if (state.Direction == 1)
                                state.LastHigh = extremePrice;
                            else if (state.Direction == -1)
                                state.LastLow = extremePrice;

                            // 2. Start the new segment EXACTLY from the confirmed extremePrice (the shoulder/waist vertex) to the new trigger price
                            state.Direction = -state.Direction;
                            state.CurrentPrice = extremePrice;

                            // Route through the unified HandleContinuationWithPotentialSplit logic to handle the initial reversal segment.
                            // To ensure it ALWAYS starts a new segment rather than extending the prior trend, we bypass extension.
                            bool forceNewSegment = true;
                            HandleContinuationWithPotentialSplit(tsPool, opPool, hiPool, loPool, clPool, volPool, rgPool, ref segmentCount,
                                ref state, price, c.Timestamp, startIndex, i - startIndex + 1, forceNewSegment);
                            startIndex = i;
                        }
                    }
                }

                // Copy to exactly sized arrays for ChartSegmentAdapter
                var finalTimestamps = new DateTime[segmentCount];
                var finalOpens = new decimal[segmentCount];
                var finalHighs = new decimal[segmentCount];
                var finalLows = new decimal[segmentCount];
                var finalCloses = new decimal[segmentCount];
                var finalVolumes = new long[segmentCount];
                var finalRanges = new OriginalIndexRange[segmentCount];

                Array.Copy(tsPool, finalTimestamps, segmentCount);
                Array.Copy(opPool, finalOpens, segmentCount);
                Array.Copy(hiPool, finalHighs, segmentCount);
                Array.Copy(loPool, finalLows, segmentCount);
                Array.Copy(clPool, finalCloses, segmentCount);
                Array.Copy(volPool, finalVolumes, segmentCount);
                Array.Copy(rgPool, finalRanges, segmentCount);

                return new ChartSegmentAdapter(
                    ZeroAllocChartDataType.Kagi,
                    finalTimestamps,
                    finalOpens,
                    finalHighs,
                    finalLows,
                    finalCloses,
                    finalVolumes,
                    finalRanges,
                    bypassClone: true);
            }
            finally
            {
                ArrayPool<DateTime>.Shared.Return(tsPool);
                ArrayPool<decimal>.Shared.Return(opPool);
                ArrayPool<decimal>.Shared.Return(hiPool);
                ArrayPool<decimal>.Shared.Return(loPool);
                ArrayPool<decimal>.Shared.Return(clPool);
                ArrayPool<long>.Shared.Return(volPool);
                ArrayPool<OriginalIndexRange>.Shared.Return(rgPool);
            }
        }

        private static void HandleContinuationWithPotentialSplit(
            DateTime[] ts, decimal[] op, decimal[] hi, decimal[] lo, decimal[] cl,
            long[] vol, OriginalIndexRange[] rg, ref int count,
            ref KagiState state, decimal newPrice, DateTime time, int startIdx, int len, bool forceNewSegment = false)
        {
            decimal start = state.CurrentPrice;
            decimal end = newPrice;

            bool willTurnYin = state.IsYang && state.LastLow != decimal.MaxValue && end < state.LastLow;
            bool willTurnYang = !state.IsYang && state.LastHigh != decimal.MinValue && end > state.LastHigh;

            if (willTurnYin)
            {
                decimal boundary = state.LastLow;
                
                // 1. Process first half: start -> boundary (with original IsYang = true)
                AddSegmentInternal(ts, op, hi, lo, cl, vol, rg, ref count, time, start, boundary, true, startIdx, len, forceNewSegment);
                
                // 2. Process second half: boundary -> end (with new IsYang = false)
                state.IsYang = false;
                // Force a new segment for the second half to guarantee split structure
                AddSegmentInternal(ts, op, hi, lo, cl, vol, rg, ref count, time, boundary, end, false, startIdx, 0, true);
            }
            else if (willTurnYang)
            {
                decimal boundary = state.LastHigh;
                
                // 1. Process first half: start -> boundary (with original IsYang = false)
                AddSegmentInternal(ts, op, hi, lo, cl, vol, rg, ref count, time, start, boundary, false, startIdx, len, forceNewSegment);
                
                // 2. Process second half: boundary -> end (with new IsYang = true)
                state.IsYang = true;
                // Force a new segment for the second half to guarantee split structure
                AddSegmentInternal(ts, op, hi, lo, cl, vol, rg, ref count, time, boundary, end, true, startIdx, 0, true);
            }
            else
            {
                AddSegmentInternal(ts, op, hi, lo, cl, vol, rg, ref count, time, start, end, state.IsYang, startIdx, len, forceNewSegment);
            }

            state.CurrentPrice = end;
        }

        private static void AddSegmentInternal(
            DateTime[] ts, decimal[] op, decimal[] hi, decimal[] lo, decimal[] cl,
            long[] vol, OriginalIndexRange[] rg, ref int count,
            DateTime time, decimal start, decimal end, bool stateIsYang, int startIdx, int len, bool forceNewSegment)
        {
            bool isExtension = false;
            if (!forceNewSegment && count > 0)
            {
                int lastIdx = count - 1;
                bool lastDirUp = cl[lastIdx] >= op[lastIdx];
                bool currDirUp = end >= start;
                bool lastIsYang = vol[lastIdx] == 1L;
                
                if (cl[lastIdx] == start && lastIsYang == stateIsYang && lastDirUp == currDirUp)
                {
                    cl[lastIdx] = end;
                    hi[lastIdx] = Math.Max(op[lastIdx], end);
                    lo[lastIdx] = Math.Min(op[lastIdx], end);
                    ts[lastIdx] = time;
                    
                    var oldRange = rg[lastIdx];
                    rg[lastIdx] = new OriginalIndexRange(oldRange.Start, oldRange.Length + len);
                    isExtension = true;
                }
            }

            if (!isExtension)
            {
                AddSegment(ts, op, hi, lo, cl, vol, rg, ref count, time, start, end, stateIsYang, startIdx, len);
            }
        }

        private static void AddSegment(
            DateTime[] ts, decimal[] op, decimal[] hi, decimal[] lo, decimal[] cl,
            long[] vol, OriginalIndexRange[] rg, ref int count,
            DateTime t, decimal o, decimal c, bool isYang, int start, int len)
        {
            int idx = count++;
            ts[idx] = t;
            op[idx] = o;
            hi[idx] = Math.Max(o, c);
            lo[idx] = Math.Min(o, c);
            cl[idx] = c;
            vol[idx] = isYang ? 1L : 0L;
            rg[idx] = new OriginalIndexRange(start, len);
        }
    }
}
