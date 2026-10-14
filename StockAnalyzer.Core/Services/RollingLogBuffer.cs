using System;
using System.Text;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// A thread-safe, bounded circular log buffer that retains the most recent log lines
/// up to a fixed capacity to prevent unbounded memory growth and LOH allocation.
/// </summary>
public sealed class RollingLogBuffer
{
    private readonly string[] _buffer;
    private readonly int _capacity;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    public RollingLogBuffer(int capacity = 500)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
        _capacity = capacity;
        _buffer = new string[capacity];
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void AppendLine(string? line)
    {
        if (line == null) return;
        lock (_lock)
        {
            _buffer[_head] = line;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            if (_count == 0) return string.Empty;

            var sb = new StringBuilder();
            int start = (_count < _capacity) ? 0 : _head;
            for (int i = 0; i < _count; i++)
            {
                int index = (start + i) % _capacity;
                sb.AppendLine(_buffer[index]);
            }
            return sb.ToString();
        }
    }
}
