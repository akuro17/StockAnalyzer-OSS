using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Discussion;

/// <summary>The event names of the hub audit ring (spec 5.11).</summary>
public static class DiscussionAuditEvents
{
    public const string HubStarted = "HubStarted";
    public const string SessionCreated = "SessionCreated";
    public const string ParticipantJoined = "ParticipantJoined";
    public const string ParticipantRejoined = "ParticipantRejoined";
    public const string ParticipantLeft = "ParticipantLeft";
    public const string MessagePosted = "MessagePosted";
    public const string PostRejected = "PostRejected";
    public const string RateLimited = "RateLimited";
    public const string ContextAttached = "ContextAttached";
    public const string SessionClosed = "SessionClosed";
    public const string SessionPurged = "SessionPurged";
}

/// <summary>
/// A bounded in-memory ring of what happened on the board: who (handle) did what to which session, never the content, a token, a display name or a result.
/// The oldest entry is dropped when the ring is full. Thread-safe; its lock is a leaf (nothing is called while it is held).
/// </summary>
internal sealed class DiscussionAudit
{
    private readonly object _sync = new();
    private readonly DiscussionAuditEntry?[] _ring;
    private readonly TimeProvider _time;
    private long _next = 1;

    public DiscussionAudit(int capacity, TimeProvider time)
    {
        _ring = new DiscussionAuditEntry?[capacity];
        _time = time;
    }

    public void Add(string eventName, string? sessionId = null, string? handle = null, long? sequence = null, string? code = null, string? detail = null)
    {
        lock (_sync)
        {
            long n = _next++;
            _ring[(int)((n - 1) % _ring.Length)] = new DiscussionAuditEntry(n, _time.GetUtcNow(), eventName, sessionId, handle, sequence, code, detail);
        }
    }

    /// <summary>The newest <paramref name="count"/> entries (fewer when fewer exist), oldest first.</summary>
    public IReadOnlyList<DiscussionAuditEntry> Tail(int count)
    {
        lock (_sync)
        {
            long last = _next - 1;
            long available = Math.Min(last, _ring.Length);
            long take = Math.Min(count, available);
            var result = new List<DiscussionAuditEntry>((int)take);
            for (long n = last - take + 1; n <= last; n++)
            {
                result.Add(_ring[(int)((n - 1) % _ring.Length)]!);
            }

            return result;
        }
    }
}
