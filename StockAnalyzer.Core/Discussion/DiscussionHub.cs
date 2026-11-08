using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The discussion board: sessions, participants, append-only messages, analysis snapshots, rate and capacity limits, exact byte accounting and the expiry state
/// machine (spec 5.1-5.11, 5.15). Everything lives in memory and is gone when the object is disposed; nothing is written to disk. Thread-safe.
/// <para>
/// Locking (spec 5.15): one lock for the hub (the session table, the byte counter) and one per session; the order is always hub lock, then session lock. No
/// <c>await</c>, no I/O and no callback runs inside a lock; waiting readers are completed after the locks are left.
/// </para>
/// </summary>
public sealed class DiscussionHub : IDisposable
{
    private const string EventJoined = "joined";
    private const string EventRejoined = "rejoined";
    private const string EventLeft = "left";
    private const string EventPassed = "passed";
    private const string EventClosedIdle = "closed: idle";
    private const string EventClosedMaxAge = "closed: maxAge";

    private readonly object _sync = new();
    private readonly DiscussionSettings _settings;
    private readonly TimeProvider _time;
    private readonly DiscussionIds _ids;
    private readonly DiscussionAudit _audit;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ITimer? _sweepTimer;
    private long _totalBytes;
    private bool _disposed;

    /// <param name="settings">The validated settings; they must not change while the hub lives.</param>
    /// <param name="time">The clock (tests pass a manual one). Null uses the system clock.</param>
    /// <param name="ids">The identifier source. Null uses the cryptographic one.</param>
    /// <param name="startSweeper">Start the background expiry timer (every <see cref="DiscussionLimits.SweepIntervalSeconds"/> seconds). Every operation also expires the session it touches.</param>
    /// <exception cref="DiscussionLimitsException">The settings are outside their bands.</exception>
    public DiscussionHub(DiscussionSettings settings, TimeProvider? time = null, DiscussionIds? ids = null, bool startSweeper = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiscussionSettingsValidator.Validate(settings);
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _ids = ids ?? new DiscussionIds();
        _audit = new DiscussionAudit(settings.AuditCapacity, _time);
        Epoch = _ids.NewEpoch();
        _audit.Add(DiscussionAuditEvents.HubStarted, detail: Epoch);
        if (startSweeper)
        {
            TimeSpan interval = TimeSpan.FromSeconds(DiscussionLimits.SweepIntervalSeconds);
            _sweepTimer = _time.CreateTimer(_ => Sweep(), null, interval, interval);
        }
    }

    /// <summary>The epoch tag of this hub; every participant token carries it (a token of another epoch means the board was restarted).</summary>
    public string Epoch { get; }

    // ---- Internal records -------------------------------------------------------------------------------------------------------------

    private sealed class Participant
    {
        public Participant(string handle, string displayName, byte[] tokenHash, DateTimeOffset now)
        {
            Handle = handle;
            DisplayName = displayName;
            TokenHash = tokenHash;
            LastSeenAt = now;
        }

        public string Handle { get; }

        public string DisplayName { get; }

        public byte[] TokenHash { get; set; }

        public bool Left { get; set; }

        public DateTimeOffset LastSeenAt { get; set; }

        public long LastReadSequence { get; set; }

        public List<DateTimeOffset> AcceptedPostTimes { get; } = new();

        public List<RecentPost> RecentRequests { get; } = new();
    }

    private sealed record RecentPost(string Id, string Content, bool Pass, long? BasedOnSequence, DiscussionPostResult Result);

    private sealed record WaiterEntry(string ParticipantHandle, TaskCompletionSource<bool> Signal);

    private sealed record MessageRecord(
        long Sequence,
        string? Handle,
        DiscussionMessageKind Kind,
        string Content,
        long? ReplyToSequence,
        long[] ContextIds,
        DateTimeOffset CreatedAt,
        DiscussionInterpretation? Interpretation = null,
        string? InterpretationJson = null);

    private sealed record ContextRecord(
        long Id,
        string Tool,
        string ArgumentsJson,
        string ResultJson,
        string Sha256,
        string AttachedBy,
        string? Note,
        DateTimeOffset CreatedAt,
        long Sequence,
        int ResultBytes,
        string? AnalysisIntent = null,
        string? ProvenanceJson = null);

    private sealed class Session
    {
        public Session(string id, string title, string? topic, DiscussionTurnMode turnMode, DateTimeOffset now)
        {
            Id = id;
            Title = title;
            Topic = topic;
            TurnMode = turnMode;
            CreatedAt = now;
            LastActivityAt = now;
        }

        public object Sync { get; } = new();

        public string Id { get; }

        public string Title { get; }

        public string? Topic { get; }

        public DiscussionTurnMode TurnMode { get; }

        public bool Closed { get; set; }

        public bool Purged { get; set; }

        public DiscussionCloseReason? CloseReason { get; set; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset LastActivityAt { get; set; }

        public DateTimeOffset ClosedAt { get; set; }

        public int JoinCount { get; set; }

        /// <summary>Round-robin only: who may speak now (null in a free discussion, or while nobody is in).</summary>
        public Participant? FloorHolder { get; set; }

        public DateTimeOffset FloorSince { get; set; }

        /// <summary>Payload bytes of this session (messages, snapshot results, arguments and notes); subtracted from the hub total on purge.</summary>
        public long Bytes { get; set; }

        public List<Participant> Participants { get; } = new();

        public List<MessageRecord> Messages { get; } = new();

        public List<ContextRecord> Contexts { get; } = new();

        public List<WaiterEntry> Waiters { get; } = new();

        public long LastSequence => Messages.Count;
    }

    // ---- Errors -----------------------------------------------------------------------------------------------------------------------

    private static DiscussionException NotFound()
        => new(DiscussionErrorCodes.DiscussionNotFound, "The discussion does not exist or has been removed.", "sessionId");

    private static DiscussionException Closed()
        => new(DiscussionErrorCodes.DiscussionClosed, "The discussion is closed.", "sessionId", "Read it to see the transcript; create a new discussion to continue.");

    private static DiscussionException NotJoined(string? hint = null)
        => new(DiscussionErrorCodes.ParticipantNotJoined, "The participant token is not valid for this discussion.", "participantToken", hint ?? "Join the discussion first.");

    private static DiscussionException Invalid(string parameter, string message, string? hint = null)
        => new(DiscussionErrorCodes.InvalidArgument, message, parameter, hint);

    private static DiscussionException Limit(string parameter, string message, string? hint = null)
        => new(DiscussionErrorCodes.LimitExceeded, message, parameter, hint);

    // ---- Plumbing ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The state of one operation while both locks are held.</summary>
    private sealed class Op
    {
        public Op(Session session, DateTimeOffset now, List<TaskCompletionSource<bool>> wake)
        {
            Session = session;
            Now = now;
            Wake = wake;
        }

        public Session Session { get; }

        public DateTimeOffset Now { get; }

        public List<TaskCompletionSource<bool>> Wake { get; }
    }

    private static void Complete(List<TaskCompletionSource<bool>> wake)
    {
        foreach (TaskCompletionSource<bool> waiter in wake)
        {
            waiter.TrySetResult(true);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiscussionHub));
        }
    }

    /// <summary>Runs <paramref name="body"/> with the hub lock and the session lock held, after the session was expired. A purged or unknown session is "not found".</summary>
    private T WithSession<T>(string? sessionId, Func<Op, T> body)
    {
        var wake = new List<TaskCompletionSource<bool>>();
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (sessionId is null || !_sessions.TryGetValue(sessionId, out Session? session))
                {
                    throw NotFound();
                }

                DateTimeOffset now = _time.GetUtcNow();
                lock (session.Sync)
                {
                    ExpireLocked(session, now, wake);
                    if (session.Purged)
                    {
                        throw NotFound();
                    }

                    return body(new Op(session, now, wake));
                }
            }
        }
        finally
        {
            Complete(wake);
        }
    }

    /// <summary>The epoch part of a token (spec 5.6 step 1): a token of another epoch is "board restarted", anything that is not a token is "not joined".</summary>
    private void CheckToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > DiscussionLimits.TokenMaxChars || !DiscussionIds.TryGetEpoch(token, out string epoch))
        {
            throw NotJoined();
        }

        if (!string.Equals(epoch, Epoch, StringComparison.Ordinal))
        {
            throw new DiscussionException(
                DiscussionErrorCodes.HubReset,
                "The discussion board was restarted; its sessions and your token are gone.",
                "participantToken",
                "Create a new discussion and join it again.");
        }
    }

    private static Participant Resolve(Session session, string token, bool allowLeft)
    {
        byte[] hash = DiscussionIds.HashToken(token);
        Participant? found = null;
        foreach (Participant participant in session.Participants)
        {
            // No early exit: every participant is compared, so the time does not reveal which one matched.
            if (DiscussionIds.HashesEqual(participant.TokenHash, hash))
            {
                found = participant;
            }
        }

        if (found is null)
        {
            throw NotJoined();
        }

        if (found.Left && !allowLeft)
        {
            throw NotJoined("left");
        }

        return found;
    }

    private DiscussionParticipantState StateOf(Participant participant, DateTimeOffset now)
    {
        if (participant.Left)
        {
            return DiscussionParticipantState.Left;
        }

        return now - participant.LastSeenAt >= TimeSpan.FromSeconds(_settings.AwaySeconds) ? DiscussionParticipantState.Away : DiscussionParticipantState.Active;
    }

    private DiscussionSessionHeader Header(Session session, DateTimeOffset now)
        => new(
            session.Id,
            session.Title,
            session.Topic,
            session.Closed ? DiscussionSessionState.Closed : DiscussionSessionState.Active,
            session.CloseReason,
            session.TurnMode,
            FloorOf(session, now),
            session.Messages.Count,
            Math.Max(0, _settings.MaxMessagesPerSession - session.Messages.Count),
            session.Participants.Select(p => new DiscussionParticipantInfo(p.Handle, p.DisplayName, StateOf(p, now), p.LastReadSequence)).ToList(),
            session.Contexts.Count,
            Epoch);

    // ---- Turn control (spec 5.5) --------------------------------------------------------------------------------------------------------

    private TimeSpan FloorTimeout => TimeSpan.FromSeconds(_settings.FloorTimeoutSeconds);

    /// <summary>The next participant after <paramref name="current"/> in join order (wrapping round) who has not left; null when there is nobody else.</summary>
    private static Participant? NextAfter(Session session, Participant current)
    {
        int count = session.Participants.Count;
        int start = session.Participants.IndexOf(current);
        for (int step = 1; step <= count; step++)
        {
            Participant candidate = session.Participants[(start + step) % count];
            if (!candidate.Left && !ReferenceEquals(candidate, current))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The floor as a reader sees it: null in a free discussion or while nobody holds it; the seconds left never go below zero.</summary>
    private DiscussionFloorInfo? FloorOf(Session session, DateTimeOffset now)
    {
        if (session.TurnMode != DiscussionTurnMode.RoundRobin || session.FloorHolder is not { } holder)
        {
            return null;
        }

        int left = (int)Math.Ceiling(FloorTimeout.TotalSeconds - (now - session.FloorSince).TotalSeconds);
        return new DiscussionFloorInfo(holder.Handle, Math.Max(0, left));
    }

    /// <summary>
    /// The floor moves on after <paramref name="from"/> spoke, passed or left: to the next participant in order, or stays with the speaker when nobody else
    /// is in (and is empty when the leaver was the last one). The clock of the floor restarts.
    /// </summary>
    private static void PassFloor(Session session, Participant from, DateTimeOffset now)
    {
        session.FloorHolder = NextAfter(session, from) ?? (from.Left ? null : from);
        session.FloorSince = now;
    }

    /// <summary>
    /// Advances the floor by ONE step when its holder has been silent for the floor timeout, and records it. Runs at the start of post, read and status, never
    /// in a timer, so the result depends only on the order of calls and on the clock. A lone holder keeps the floor.
    /// </summary>
    private void EvaluateFloorTimeout(Session session, DateTimeOffset now, List<TaskCompletionSource<bool>> wake)
    {
        if (session.TurnMode != DiscussionTurnMode.RoundRobin || session.Closed || session.FloorHolder is not { } holder)
        {
            return;
        }

        if (now - session.FloorSince < FloorTimeout)
        {
            return;
        }

        Participant? next = NextAfter(session, holder);
        if (next is null)
        {
            return;
        }

        string content = $"floor passed from {holder.Handle} to {next.Handle} (timeout)";
        if (HasRoom(session, DiscussionText.Utf8Bytes(content)))
        {
            Append(session, now, null, DiscussionMessageKind.Event, content, null, Array.Empty<long>(), wake);
        }

        session.FloorHolder = next;
        session.FloorSince = now;
    }

    private static void Touch(Session session, Participant participant, DateTimeOffset now)
    {
        participant.LastSeenAt = now;
        if (!participant.Left)
        {
            session.LastActivityAt = now;
        }
    }

    private bool HasRoom(Session session, int bytes)
        => session.Messages.Count < _settings.MaxMessagesPerSession && _totalBytes + bytes <= _settings.MaxTotalBytes;

    private void AddBytes(Session session, long bytes)
    {
        session.Bytes += bytes;
        _totalBytes += bytes;
    }

    /// <summary>Appends one message (the caller has checked the limits it must obey) and moves the waiting readers to <paramref name="wake"/>.</summary>
    private MessageRecord Append(
        Session session,
        DateTimeOffset now,
        string? handle,
        DiscussionMessageKind kind,
        string content,
        long? replyTo,
        long[] contextIds,
        List<TaskCompletionSource<bool>> wake,
        DiscussionInterpretation? interpretation = null,
        string? interpretationJson = null)
    {
        var message = new MessageRecord(session.Messages.Count + 1, handle, kind, content, replyTo, contextIds, now, interpretation, interpretationJson);
        session.Messages.Add(message);
        int totalMsgBytes = DiscussionText.Utf8Bytes(content) + (interpretationJson is null ? 0 : DiscussionText.Utf8Bytes(interpretationJson));
        AddBytes(session, totalMsgBytes);
        WakeReaders(session, wake);
        return message;
    }

    private static void WakeReaders(Session session, List<TaskCompletionSource<bool>> wake)
    {
        if (session.Waiters.Count > 0)
        {
            foreach (WaiterEntry entry in session.Waiters)
            {
                wake.Add(entry.Signal);
            }

            session.Waiters.Clear();
        }
    }

    // ---- Expiry (spec 5.3): one function, used by every operation and by the sweeper --------------------------------------------------

    private void ExpireLocked(Session session, DateTimeOffset now, List<TaskCompletionSource<bool>> wake)
    {
        if (session.Purged)
        {
            return;
        }

        if (session.Closed)
        {
            if (now - session.ClosedAt >= TimeSpan.FromSeconds(_settings.ClosedRetentionSeconds))
            {
                PurgeLocked(session, wake);
            }

            return;
        }

        DiscussionCloseReason? reason = null;
        if (now - session.LastActivityAt >= TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds))
        {
            reason = DiscussionCloseReason.Idle;
        }
        else if (now - session.CreatedAt >= TimeSpan.FromSeconds(_settings.MaxSessionAgeSeconds))
        {
            reason = DiscussionCloseReason.MaxAge;
        }

        if (reason is { } expired)
        {
            CloseLocked(session, now, expired, closerHandle: null, wake);
        }
    }

    /// <summary>Closes the session and appends the closing event, which may exceed the message cap and the budget by that one message.</summary>
    private void CloseLocked(Session session, DateTimeOffset now, DiscussionCloseReason reason, string? closerHandle, List<TaskCompletionSource<bool>> wake)
    {
        session.Closed = true;
        session.CloseReason = reason;
        session.ClosedAt = now;
        string content = reason switch
        {
            DiscussionCloseReason.Idle => EventClosedIdle,
            DiscussionCloseReason.MaxAge => EventClosedMaxAge,
            _ => $"closed by {closerHandle}",
        };
        Append(session, now, null, DiscussionMessageKind.Event, content, null, Array.Empty<long>(), wake);
        _audit.Add(DiscussionAuditEvents.SessionClosed, session.Id, closerHandle, detail: DiscussionNames.ToWire(reason));
        EnforceClosedSessionCapLocked(wake);
    }

    private void EnforceClosedSessionCapLocked(List<TaskCompletionSource<bool>> wake)
    {
        int maxClosed = Math.Max(4, _settings.MaxActiveSessions * DiscussionLimits.ClosedSessionRetentionMultiplier);
        var closedSessions = _sessions.Values
            .Where(s => s.Closed && !s.Purged)
            .OrderBy(s => s.ClosedAt)
            .ToList();

        int excess = closedSessions.Count - maxClosed;
        for (int i = 0; i < excess; i++)
        {
            Session oldest = closedSessions[i];
            lock (oldest.Sync)
            {
                PurgeLocked(oldest, wake);
            }
        }
    }

    private void PurgeLocked(Session session, List<TaskCompletionSource<bool>> wake)
    {
        _sessions.Remove(session.Id);
        _totalBytes -= session.Bytes;
        session.Bytes = 0;
        session.Purged = true;
        WakeReaders(session, wake);
        _audit.Add(DiscussionAuditEvents.SessionPurged, session.Id);
    }

    /// <summary>Evaluates the expiry of every session now. Called by the background timer; safe to call at any time.</summary>
    public void Sweep()
    {
        var wake = new List<TaskCompletionSource<bool>>();
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                DateTimeOffset now = _time.GetUtcNow();
                foreach (Session session in _sessions.Values.ToArray())
                {
                    lock (session.Sync)
                    {
                        ExpireLocked(session, now, wake);
                    }
                }
            }
        }
        finally
        {
            Complete(wake);
        }
    }

    // ---- create / join / leave / close ------------------------------------------------------------------------------------------------

    /// <summary>Opens a new discussion. Needs no token: the creator joins like everybody else.</summary>
    public DiscussionCreateResult Create(string? title, string? topic, DiscussionTurnMode turnMode)
    {
        if (title is null)
        {
            throw Invalid("title", "title is required.");
        }

        if (title.Length > DiscussionLimits.TitleMaxChars)
        {
            throw Limit("title", $"title is longer than {DiscussionLimits.TitleMaxChars} characters.");
        }

        if (!DiscussionText.HasVisibleCharacter(title) || DiscussionText.IsInvalid(title, allowTabLineBreaks: false))
        {
            throw Invalid("title", "title must contain a visible character and no control character.");
        }

        if (topic is not null)
        {
            if (topic.Length > DiscussionLimits.TopicMaxChars)
            {
                throw Limit("topic", $"topic is longer than {DiscussionLimits.TopicMaxChars} characters.");
            }

            if (DiscussionText.IsInvalid(topic, allowTabLineBreaks: true))
            {
                throw Invalid("topic", "topic contains a control character or an unpaired surrogate.");
            }
        }

        var wake = new List<TaskCompletionSource<bool>>();
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                DateTimeOffset now = _time.GetUtcNow();
                foreach (Session existing in _sessions.Values.ToArray())
                {
                    lock (existing.Sync)
                    {
                        ExpireLocked(existing, now, wake);
                    }
                }

                EnforceClosedSessionCapLocked(wake);

                if (_sessions.Values.Count(candidate => !candidate.Closed) >= _settings.MaxActiveSessions)
                {
                    throw Limit("sessions", $"There are already {_settings.MaxActiveSessions} active discussions.", "Close one that is no longer needed.");
                }

                string id;
                try
                {
                    id = _ids.NewSessionId(_sessions.ContainsKey);
                }
                catch (InvalidOperationException)
                {
                    throw new DiscussionException(DiscussionErrorCodes.InternalError, "No free discussion id was found.");
                }

                var session = new Session(id, title, topic, turnMode, now);
                _sessions.Add(id, session);
                _audit.Add(DiscussionAuditEvents.SessionCreated, id);
                lock (session.Sync)
                {
                    return new DiscussionCreateResult(Header(session, now), now);
                }
            }
        }
        finally
        {
            Complete(wake);
        }
    }

    public DiscussionJoinResult Join(string? sessionId, string? displayName)
    {
        if (displayName is null)
        {
            throw Invalid("displayName", "displayName is required.");
        }

        string name = displayName.Trim();
        if (name.Length == 0 || DiscussionText.IsInvalid(name, allowTabLineBreaks: false))
        {
            throw Invalid("displayName", "displayName must contain a visible character and no control character.");
        }

        if (name.Length > DiscussionLimits.DisplayNameMaxChars)
        {
            throw Limit("displayName", $"displayName is longer than {DiscussionLimits.DisplayNameMaxChars} characters.");
        }

        return WithSession(sessionId, op =>
        {
            Session session = op.Session;
            if (session.Closed)
            {
                throw Closed();
            }

            Participant? same = session.Participants.FirstOrDefault(p => string.Equals(p.DisplayName, name, StringComparison.Ordinal));
            if (same is not null && StateOf(same, op.Now) == DiscussionParticipantState.Active)
            {
                throw Invalid("displayName", "The name is in use.", "name in use by an active participant");
            }

            bool rejoin = same is not null;
            if (!rejoin)
            {
                if (session.Participants.Count(p => !p.Left) >= _settings.MaxParticipantsPerSession)
                {
                    throw Limit("participants", $"The discussion already has {_settings.MaxParticipantsPerSession} participants.");
                }

                if (session.JoinCount >= DiscussionLimits.MaxJoinsPerSession(_settings.MaxParticipantsPerSession))
                {
                    throw Limit("joins", "The discussion accepted its maximum number of joins.");
                }
            }

            string handle = same?.Handle ?? $"p{session.Participants.Count + 1}";
            string eventContent = $"{handle} ({name}) {(rejoin ? EventRejoined : EventJoined)}";
            if (!HasRoom(session, DiscussionText.Utf8Bytes(eventContent)))
            {
                throw Limit("messages", "The discussion has no room left for another entry.");
            }

            string token = _ids.NewToken(Epoch);
            byte[] hash = DiscussionIds.HashToken(token);
            Participant participant;
            if (same is not null)
            {
                participant = same;
                participant.TokenHash = hash;
                participant.Left = false;
                participant.LastSeenAt = op.Now;
            }
            else
            {
                participant = new Participant(handle, name, hash, op.Now);
                session.Participants.Add(participant);
            }

            session.JoinCount++;
            session.LastActivityAt = op.Now;
            if (session.TurnMode == DiscussionTurnMode.RoundRobin && session.FloorHolder is null)
            {
                // The first participant to be in takes the floor.
                session.FloorHolder = participant;
                session.FloorSince = op.Now;
            }

            Append(session, op.Now, null, DiscussionMessageKind.Event, eventContent, null, Array.Empty<long>(), op.Wake);
            _audit.Add(rejoin ? DiscussionAuditEvents.ParticipantRejoined : DiscussionAuditEvents.ParticipantJoined, session.Id, handle);
            return new DiscussionJoinResult(token, handle, rejoin, Header(session, op.Now), session.LastSequence, DiscussionProtocolText.Text);
        });
    }

    public DiscussionLeaveResult Leave(string? sessionId, string? participantToken)
    {
        CheckToken(participantToken);
        return WithSession(sessionId, op =>
        {
            Session session = op.Session;
            Participant participant = Resolve(session, participantToken!, allowLeft: true);
            if (participant.Left)
            {
                return new DiscussionLeaveResult(true, Header(session, op.Now));
            }

            if (session.Closed)
            {
                // A closed discussion changes no more: leaving it is accepted and has no effect.
                return new DiscussionLeaveResult(false, Header(session, op.Now));
            }

            participant.Left = true;
            participant.LastSeenAt = op.Now;
            session.LastActivityAt = op.Now;
            if (ReferenceEquals(session.FloorHolder, participant))
            {
                PassFloor(session, participant, op.Now);
            }

            string eventContent = $"{participant.Handle} ({participant.DisplayName}) {EventLeft}";
            if (HasRoom(session, DiscussionText.Utf8Bytes(eventContent)))
            {
                Append(session, op.Now, null, DiscussionMessageKind.Event, eventContent, null, Array.Empty<long>(), op.Wake);
            }

            _audit.Add(DiscussionAuditEvents.ParticipantLeft, session.Id, participant.Handle);
            return new DiscussionLeaveResult(false, Header(session, op.Now));
        });
    }

    public DiscussionCloseResult Close(string? sessionId, string? participantToken)
    {
        CheckToken(participantToken);
        return WithSession(sessionId, op =>
        {
            Session session = op.Session;
            Participant participant = Resolve(session, participantToken!, allowLeft: false);
            bool already = session.Closed;
            if (!already)
            {
                Touch(session, participant, op.Now);
                CloseLocked(session, op.Now, DiscussionCloseReason.Participant, participant.Handle, op.Wake);
            }

            return new DiscussionCloseResult(
                already,
                DiscussionSessionState.Closed,
                session.CloseReason,
                session.Messages.Count,
                Header(session, op.Now));
        });
    }

    // ---- post -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Appends a message. The checks run in the exact order of spec 5.6 and the first failing one wins: token, idempotency, closed, content, references, turn, rate, capacity.
    /// </summary>
    public DiscussionPostResult Post(string? sessionId, string? participantToken, DiscussionPostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckToken(participantToken);
        return WithSession(sessionId, op =>
        {
            Session session = op.Session;
            Participant participant = Resolve(session, participantToken!, allowLeft: false);

            // Step 2: a repeated request id returns the stored answer and changes nothing.
            string? requestId = request.ClientRequestId;
            if (requestId is not null)
            {
                if (requestId.Length == 0 || !requestId.All(DiscussionText.IsRequestIdCharacter))
                {
                    throw Invalid("clientRequestId", "clientRequestId may only contain letters, digits, underscore and hyphen.");
                }

                if (requestId.Length > DiscussionLimits.ClientRequestIdMaxChars)
                {
                    throw Limit("clientRequestId", $"clientRequestId is longer than {DiscussionLimits.ClientRequestIdMaxChars} characters.");
                }

                foreach (RecentPost stored in participant.RecentRequests)
                {
                    if (string.Equals(stored.Id, requestId, StringComparison.Ordinal))
                    {
                        EvaluateFloorTimeout(session, op.Now, op.Wake);
                        DiscussionFloorInfo? currentFloor = FloorOf(session, op.Now);
                        return stored.Result with { Duplicate = true, Floor = currentFloor };
                    }
                }
            }

            try
            {
                return PostLocked(op, participant, request, requestId);
            }
            catch (DiscussionException error) when (error.Code != DiscussionErrorCodes.InternalError)
            {
                _audit.Add(DiscussionAuditEvents.PostRejected, session.Id, participant.Handle, code: error.Code);
                if (error.Code == DiscussionErrorCodes.RateLimited)
                {
                    _audit.Add(DiscussionAuditEvents.RateLimited, session.Id, participant.Handle);
                }

                throw;
            }
        });
    }

    private DiscussionPostResult PostLocked(Op op, Participant participant, DiscussionPostRequest request, string? requestId)
    {
        Session session = op.Session;
        DateTimeOffset now = op.Now;

        // Step 3.
        if (session.Closed)
        {
            throw Closed();
        }

        // Step 4: content (a pass carries none: the server writes the "passed" event itself).
        bool pass = request.Pass;
        string content;
        int contentBytes;
        DiscussionInterpretation? interpretation = null;
        string? interpretationJson = null;
        int interpretationBytes = 0;
        if (request.Interpretation is not null)
        {
            if (pass)
            {
                throw Invalid("pass", "A pass carries no interpretation.");
            }

            if (!DiscussionEvidenceValidator.TryValidateInterpretation(request.Interpretation, out interpretation, out string? errorReason))
            {
                throw Invalid("interpretation", errorReason ?? "interpretation is invalid.");
            }

            interpretationJson = DiscussionEvidenceValidator.SerializeInterpretationCanonical(interpretation!);
            interpretationBytes = DiscussionText.Utf8Bytes(interpretationJson);
        }

        if (pass)
        {
            if (session.TurnMode != DiscussionTurnMode.RoundRobin)
            {
                throw Invalid("pass", "pass is only meaningful in a round-robin discussion.", "Post a message instead.");
            }

            if (request.Content is not null || request.ReplyToSequence is not null || request.ContextIds is not null)
            {
                throw Invalid("pass", "A pass carries no content, reply or contexts.");
            }

            content = $"{participant.Handle} ({participant.DisplayName}) {EventPassed}";
            contentBytes = DiscussionText.Utf8Bytes(content);
        }
        else
        {
            string? posted = request.Content;
            if (posted is null || posted.Length == 0 || !DiscussionText.HasVisibleCharacter(posted))
            {
                throw Invalid("content", "content is required and must contain a visible character.");
            }

            contentBytes = DiscussionText.Utf8Bytes(posted);
            if (contentBytes > _settings.MaxMessageBytes)
            {
                throw Limit("content", $"content is larger than {_settings.MaxMessageBytes} bytes.", "split the message or attach the data with sa_discussion_attach_analysis");
            }

            if (DiscussionText.IsInvalid(posted, allowTabLineBreaks: true))
            {
                throw Invalid("content", "content contains a control character or an unpaired surrogate.");
            }

            content = posted;
        }

        // Step 5: references.
        if (request.ReplyToSequence is { } replyTo && (replyTo < 1 || replyTo > session.LastSequence))
        {
            throw Invalid("replyToSequence", "replyToSequence does not name an existing message.");
        }

        long[] contextIds = Array.Empty<long>();
        if (request.ContextIds is { } requestedContexts)
        {
            if (requestedContexts.Count > DiscussionLimits.MaxContextIdsPerPost
                || requestedContexts.Distinct().Count() != requestedContexts.Count
                || requestedContexts.Any(id => id < 1 || id > session.Contexts.Count))
            {
                throw Invalid("contextIds", $"contextIds must name at most {DiscussionLimits.MaxContextIdsPerPost} different existing contexts.");
            }

            contextIds = requestedContexts.ToArray();
        }

        if (request.BasedOnSequence is { } basedOn && (basedOn < 0 || basedOn > session.LastSequence))
        {
            throw Invalid("basedOnSequence", "basedOnSequence is outside the transcript.");
        }

        // Step 6: turn control (free discussions have none).
        if (session.TurnMode == DiscussionTurnMode.RoundRobin)
        {
            EvaluateFloorTimeout(session, now, op.Wake);
            session.FloorHolder ??= participant; // nobody held the floor (everybody had left): the first to speak takes it
            if (!ReferenceEquals(session.FloorHolder, participant))
            {
                Participant holder = session.FloorHolder;
                int seconds = Math.Max(1, (int)Math.Ceiling(FloorTimeout.TotalSeconds - (now - session.FloorSince).TotalSeconds));
                throw new DiscussionException(
                    DiscussionErrorCodes.NotYourTurn,
                    "It is another participant's turn to speak.",
                    "floor",
                    $"{holder.Handle} ({holder.DisplayName}) speaks next; {seconds} s until the floor passes");
            }
        }

        // Step 7: rate (a pass does not count toward it).
        TimeSpan window = TimeSpan.FromSeconds(DiscussionLimits.PostRateWindowSeconds);
        participant.AcceptedPostTimes.RemoveAll(time => now - time >= window);
        if (!pass && participant.AcceptedPostTimes.Count >= _settings.MaxPostsPerMinute)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling((participant.AcceptedPostTimes[0] + window - now).TotalSeconds));
            throw new DiscussionException(DiscussionErrorCodes.RateLimited, "Too many posts in a short time.", "content", $"retry after {seconds} s");
        }

        // Step 8: capacity.
        if (session.Messages.Count >= _settings.MaxMessagesPerSession)
        {
            throw Limit("messages", $"The discussion holds its maximum of {_settings.MaxMessagesPerSession} messages.", "Close it and start a new one.");
        }

        if (_totalBytes + contentBytes + interpretationBytes > _settings.MaxTotalBytes)
        {
            throw Limit("memoryBudget", "The memory budget of the discussion board is used up.", "Close a discussion that is no longer needed.");
        }

        // Step 9: append.
        long newer = request.BasedOnSequence is { } based ? session.LastSequence - based : 0;
        MessageRecord message = pass
            ? Append(session, now, null, DiscussionMessageKind.Event, content, null, Array.Empty<long>(), op.Wake)
            : Append(session, now, participant.Handle, DiscussionMessageKind.Message, content, request.ReplyToSequence, contextIds, op.Wake, interpretation, interpretationJson);
        if (!pass)
        {
            participant.AcceptedPostTimes.Add(now);
        }

        Touch(session, participant, now);
        if (session.TurnMode == DiscussionTurnMode.RoundRobin)
        {
            PassFloor(session, participant, now);
        }

        bool hasInterpretation = interpretation is not null;
        bool hasStructured = hasInterpretation || contextIds.Length > 0;
        IReadOnlyList<string>? providedFields = interpretation?.ProvidedFields;
        var result = new DiscussionPostResult(message.Sequence, now, newer, false, FloorOf(session, now), hasStructured, hasInterpretation, providedFields);
        if (requestId is not null)
        {
            participant.RecentRequests.Add(new RecentPost(requestId, request.Content ?? string.Empty, pass, request.BasedOnSequence, result));
            if (participant.RecentRequests.Count > DiscussionLimits.IdempotencyRemembered)
            {
                participant.RecentRequests.RemoveAt(0);
            }
        }

        _audit.Add(DiscussionAuditEvents.MessagePosted, session.Id, participant.Handle, message.Sequence, detail: pass ? "pass" : null);
        return result;
    }

    // ---- read -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the messages after <paramref name="afterSequence"/>. When there are none and <paramref name="waitSeconds"/> is above zero, waits (no lock held, no busy loop)
    /// until a message is appended, the discussion closes or is removed, the wait elapses or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<DiscussionReadResult> ReadAsync(
        string? sessionId,
        string? participantToken,
        long afterSequence = 0,
        int? limit = null,
        int waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        CheckToken(participantToken);
        if (afterSequence < 0)
        {
            throw Invalid("afterSequence", "afterSequence must not be negative.");
        }

        int effectiveLimit = limit ?? _settings.DefaultReadLimit;
        if (effectiveLimit < 1 || effectiveLimit > _settings.MaxReadLimit)
        {
            throw Limit("limit", $"limit must be 1 to {_settings.MaxReadLimit}.");
        }

        if (waitSeconds < 0 || waitSeconds > _settings.MaxWaitSeconds)
        {
            throw Limit("waitSeconds", $"waitSeconds must be 0 to {_settings.MaxWaitSeconds}.");
        }

        TaskCompletionSource<bool>? waiter = null;
        Session? waitedSession = null;
        DiscussionReadResult? immediate = WithSession<DiscussionReadResult?>(sessionId, op =>
        {
            Participant participant = Resolve(op.Session, participantToken!, allowLeft: true);
            EvaluateFloorTimeout(op.Session, op.Now, op.Wake);
            Touch(op.Session, participant, op.Now);
            DiscussionReadResult result = Collect(op, participant, afterSequence, effectiveLimit, timedOut: false);
            if (result.Messages.Count == 0 && waitSeconds > 0 && !op.Session.Closed)
            {
                for (int i = op.Session.Waiters.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(op.Session.Waiters[i].ParticipantHandle, participant.Handle, StringComparison.Ordinal))
                    {
                        op.Session.Waiters[i].Signal.TrySetResult(false);
                        op.Session.Waiters.RemoveAt(i);
                    }
                }

                if (op.Session.Waiters.Count >= DiscussionLimits.MaxWaitersPerSession)
                {
                    throw Limit("waiters", $"There are already {DiscussionLimits.MaxWaitersPerSession} concurrent waiting readers in this discussion.", "Retry with waitSeconds=0 or wait for active readers to complete.");
                }

                int totalWaiters = 0;
                foreach (Session s in _sessions.Values)
                {
                    totalWaiters += s.Waiters.Count;
                }

                if (totalWaiters >= DiscussionLimits.MaxTotalWaiters)
                {
                    throw Limit("waiters", $"The hub reached its limit of {DiscussionLimits.MaxTotalWaiters} concurrent waiting readers across all discussions.", "Retry with waitSeconds=0 or wait for active readers to complete.");
                }

                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                op.Session.Waiters.Add(new WaiterEntry(participant.Handle, waiter));
                waitedSession = op.Session;
                return null;
            }

            return result;
        });
        if (immediate is not null)
        {
            return immediate;
        }

        bool elapsed = false;
        try
        {
            await waiter!.Task.WaitAsync(TimeSpan.FromSeconds(waitSeconds), _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            elapsed = true;
        }
        finally
        {
            lock (waitedSession!.Sync)
            {
                for (int i = waitedSession.Waiters.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(waitedSession.Waiters[i].Signal, waiter))
                    {
                        waitedSession.Waiters.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        // One more evaluation: the session may have changed, closed or been removed meanwhile.
        return WithSession(sessionId, op =>
        {
            Participant participant = Resolve(op.Session, participantToken!, allowLeft: true);
            EvaluateFloorTimeout(op.Session, op.Now, op.Wake);
            Touch(op.Session, participant, op.Now);
            DiscussionReadResult result = Collect(op, participant, afterSequence, effectiveLimit, timedOut: false);
            return elapsed && result.Messages.Count == 0 ? result with { TimedOut = true } : result;
        });
    }

    private DiscussionReadResult Collect(Op op, Participant participant, long afterSequence, int limit, bool timedOut)
    {
        Session session = op.Session;
        long last = session.LastSequence;
        var messages = new List<DiscussionReadMessage>();
        if (afterSequence < last)
        {
            int start = (int)afterSequence;
            int count = (int)Math.Min(limit, last - afterSequence);
            for (int index = start; index < start + count; index++)
            {
                MessageRecord record = session.Messages[index];
                string? displayName = record.Handle is null ? null : session.Participants.First(p => p.Handle == record.Handle).DisplayName;
                bool hasInterpretation = record.Interpretation is not null;
                bool hasStructured = hasInterpretation || record.ContextIds.Length > 0 || record.Kind == DiscussionMessageKind.Analysis;
                messages.Add(new DiscussionReadMessage(
                    record.Sequence,
                    record.Handle,
                    displayName,
                    record.Kind,
                    record.Content,
                    record.ReplyToSequence,
                    record.ContextIds,
                    record.CreatedAt,
                    hasStructured,
                    hasInterpretation,
                    record.Interpretation));
            }
        }

        long next = messages.Count > 0 ? messages[^1].Sequence : afterSequence;
        participant.LastReadSequence = Math.Max(participant.LastReadSequence, Math.Min(next, last));
        return new DiscussionReadResult(messages, next, last, next < last, timedOut, Header(session, op.Now));
    }

    // ---- analysis snapshots (hub side of spec 5.10) -----------------------------------------------------------------------------------

    /// <summary>
    /// Step P2: checks that an attach could succeed (token, active session, room for another context) before the caller starts the expensive tool. Reserves nothing.
    /// </summary>
    public void AttachPrecheck(string? sessionId, string? participantToken)
    {
        CheckToken(participantToken);
        WithSession(sessionId, op =>
        {
            Resolve(op.Session, participantToken!, allowLeft: false);
            if (op.Session.Closed)
            {
                throw Closed();
            }

            if (op.Session.Contexts.Count >= _settings.MaxContextsPerSession)
            {
                throw Limit("contexts", $"The discussion already holds {_settings.MaxContextsPerSession} analysis snapshots.");
            }

            return 0;
        });
    }

    /// <summary>
    /// Step P4: stores the snapshot. The result text is kept verbatim (never parsed into numbers); its SHA-256 and size are computed here before any lock is taken. The state is
    /// checked again because the session may have changed while the tool ran.
    /// </summary>
    /// <param name="tool">The registered tool that produced the result (the caller has checked the whitelist).</param>
    /// <param name="canonicalArgumentsJson">The canonical form of the arguments (see <c>DiscussionCanonicalJson</c>).</param>
    /// <param name="resultJson">The tool's result text.</param>
    public DiscussionAttachResult AttachCommit(
        string? sessionId,
        string? participantToken,
        string? tool,
        string? canonicalArgumentsJson,
        string? resultJson,
        string? note,
        string? analysisIntent = null,
        string? provenanceJson = null)
    {
        CheckToken(participantToken);
        string hash = string.Empty;
        int resultBytes = 0;
        if (resultJson is not null)
        {
            if (DiscussionText.ContainsUnpairedSurrogate(resultJson))
            {
                throw Invalid("result", "result contains an unpaired UTF-16 surrogate.");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(resultJson);
            resultBytes = bytes.Length;
            hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        string? normalizedIntent = null;
        if (analysisIntent is not null)
        {
            if (!DiscussionEvidenceValidator.TryNormalizeAnalysisIntent(analysisIntent, out normalizedIntent))
            {
                throw Invalid("analysisIntent", $"analysisIntent must be at most {DiscussionEvidenceLimits.MaxAnalysisIntentChars} characters without control characters.");
            }
        }

        return WithSession(sessionId, op =>
        {
            Session session = op.Session;
            Participant participant = Resolve(session, participantToken!, allowLeft: false);
            if (session.Closed)
            {
                throw Closed();
            }

            if (string.IsNullOrEmpty(tool) || tool.Length > DiscussionLimits.ToolNameMaxChars || DiscussionText.IsInvalid(tool, allowTabLineBreaks: false))
            {
                throw Invalid("tool", "tool must be the name of a registered tool.");
            }

            if (canonicalArgumentsJson is null)
            {
                throw Invalid("arguments", "arguments are required.");
            }

            if (DiscussionText.Utf8Bytes(canonicalArgumentsJson) > DiscussionLimits.AnalysisArgumentsMaxBytes)
            {
                throw Limit("arguments", $"The arguments are larger than {DiscussionLimits.AnalysisArgumentsMaxBytes} bytes.");
            }

            if (note is not null)
            {
                if (note.Length > DiscussionLimits.NoteMaxChars)
                {
                    throw Limit("note", $"note is longer than {DiscussionLimits.NoteMaxChars} characters.");
                }

                if (DiscussionText.IsInvalid(note, allowTabLineBreaks: true))
                {
                    throw Invalid("note", "note contains a control character or an unpaired surrogate.");
                }
            }

            if (resultJson is null)
            {
                throw Invalid("result", "A result is required.");
            }

            if (resultBytes > _settings.MaxContextBytes)
            {
                throw Limit("result", $"The result is larger than {_settings.MaxContextBytes} bytes.", "request a narrower window or fewer items");
            }

            // Reuse (D-21): the same tool, the same canonical arguments, the same result and the same analysisIntent.
            ContextRecord? same = session.Contexts.FirstOrDefault(c =>
                string.Equals(c.Tool, tool, StringComparison.Ordinal)
                && string.Equals(c.ArgumentsJson, canonicalArgumentsJson, StringComparison.Ordinal)
                && string.Equals(c.Sha256, hash, StringComparison.Ordinal)
                && string.Equals(c.AnalysisIntent, normalizedIntent, StringComparison.Ordinal));
            if (same is not null)
            {
                Touch(session, participant, op.Now);
                return new DiscussionAttachResult(same.Id, true, same.Sha256, same.ResultBytes, same.Sequence, same.AnalysisIntent is not null, same.AnalysisIntent, same.ProvenanceJson);
            }

            if (session.Contexts.Count >= _settings.MaxContextsPerSession)
            {
                throw Limit("contexts", $"The discussion already holds {_settings.MaxContextsPerSession} analysis snapshots.");
            }

            string computedProvenance = provenanceJson ?? DiscussionProvenanceExtractor.ExtractCanonicalJson(
                tool, canonicalArgumentsJson, resultJson, normalizedIntent, participant.Handle, op.Now, hash, resultBytes);

            long id = session.Contexts.Count + 1;
            string intentSuffix = normalizedIntent is null ? string.Empty : $" [intent={normalizedIntent}]";
            string content = $"{participant.Handle} attached context {id} ({tool}{intentSuffix})" + (note is null ? string.Empty : $": {note}");
            long intentBytes = normalizedIntent is null ? 0 : DiscussionText.Utf8Bytes(normalizedIntent);
            long payload = resultBytes + DiscussionText.Utf8Bytes(canonicalArgumentsJson) + (note is null ? 0 : DiscussionText.Utf8Bytes(note)) + intentBytes;
            if (session.Messages.Count >= _settings.MaxMessagesPerSession)
            {
                throw Limit("messages", $"The discussion holds its maximum of {_settings.MaxMessagesPerSession} messages.");
            }

            if (_totalBytes + payload + DiscussionText.Utf8Bytes(content) > _settings.MaxTotalBytes)
            {
                throw Limit("memoryBudget", "The memory budget of the discussion board is used up.", "Close a discussion that is no longer needed.");
            }

            MessageRecord message = Append(session, op.Now, participant.Handle, DiscussionMessageKind.Analysis, content, null, new[] { id }, op.Wake);
            session.Contexts.Add(new ContextRecord(id, tool, canonicalArgumentsJson, resultJson, hash, participant.Handle, note, op.Now, message.Sequence, resultBytes, normalizedIntent, computedProvenance));
            AddBytes(session, payload);
            Touch(session, participant, op.Now);
            _audit.Add(DiscussionAuditEvents.ContextAttached, session.Id, participant.Handle, message.Sequence, detail: id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new DiscussionAttachResult(id, false, hash, resultBytes, message.Sequence, normalizedIntent is not null, normalizedIntent, computedProvenance);
        });
    }

    public DiscussionAnalysisResult GetAnalysis(string? sessionId, string? participantToken, long contextId)
    {
        CheckToken(participantToken);
        return WithSession(sessionId, op =>
        {
            Session session = op.Session;
            Participant participant = Resolve(session, participantToken!, allowLeft: true);
            if (contextId < 1 || contextId > session.Contexts.Count)
            {
                throw Invalid("contextId", "contextId does not name an attached context.");
            }

            ContextRecord context = session.Contexts[(int)contextId - 1];
            Touch(session, participant, op.Now);
            return new DiscussionAnalysisResult(
                context.Id,
                context.Tool,
                context.ArgumentsJson,
                context.Sha256,
                context.AttachedBy,
                context.Note,
                context.CreatedAt,
                context.ResultBytes,
                context.ResultJson,
                context.AnalysisIntent is not null,
                context.AnalysisIntent,
                context.ProvenanceJson);
        });
    }

    // ---- status -----------------------------------------------------------------------------------------------------------------------

    /// <summary>The discussions (or one), the newest audit entries and the clock. Needs no token and does not count as activity.</summary>
    public DiscussionStatusResult Status(string? sessionId = null, int auditTail = 0)
    {
        if (auditTail < 0 || auditTail > DiscussionLimits.StatusTailMax)
        {
            throw Limit("auditTail", $"auditTail must be 0 to {DiscussionLimits.StatusTailMax}.");
        }

        var wake = new List<TaskCompletionSource<bool>>();
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                DateTimeOffset now = _time.GetUtcNow();
                var headers = new List<DiscussionSessionHeader>();
                if (sessionId is not null)
                {
                    if (!_sessions.TryGetValue(sessionId, out Session? one))
                    {
                        throw NotFound();
                    }

                    lock (one.Sync)
                    {
                        ExpireLocked(one, now, wake);
                        if (one.Purged)
                        {
                            throw NotFound();
                        }

                        EvaluateFloorTimeout(one, now, wake);

                        headers.Add(Header(one, now));
                    }
                }
                else
                {
                    foreach (Session session in _sessions.Values.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray())
                    {
                        lock (session.Sync)
                        {
                            ExpireLocked(session, now, wake);
                            if (!session.Purged)
                            {
                                EvaluateFloorTimeout(session, now, wake);
                                headers.Add(Header(session, now));
                            }
                        }
                    }
                }

                return new DiscussionStatusResult(Epoch, headers, _audit.Tail(auditTail), now);
            }
        }
        finally
        {
            Complete(wake);
        }
    }

    // ---- diagnostics and shutdown -----------------------------------------------------------------------------------------------------

    /// <summary>The byte counter, the same sum recomputed from the stored payload, and the numbers of sessions and waiting readers.</summary>
    internal DiscussionDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            long recomputed = 0;
            int waiters = 0;
            foreach (Session session in _sessions.Values)
            {
                lock (session.Sync)
                {
                    recomputed += session.Messages.Sum(m => (long)DiscussionText.Utf8Bytes(m.Content) + (m.InterpretationJson is null ? 0 : DiscussionText.Utf8Bytes(m.InterpretationJson)))
                        + session.Contexts.Sum(c => (long)c.ResultBytes + DiscussionText.Utf8Bytes(c.ArgumentsJson) + (c.Note is null ? 0 : DiscussionText.Utf8Bytes(c.Note))
                            + (c.AnalysisIntent is null ? 0 : DiscussionText.Utf8Bytes(c.AnalysisIntent)));
                    waiters += session.Waiters.Count;
                }
            }

            return new DiscussionDiagnostics(_totalBytes, recomputed, _sessions.Count, waiters);
        }
    }

    /// <summary>Stops the timer, removes every session (the byte counter returns to zero) and releases every waiting reader. The board is gone.</summary>
    public void Dispose()
    {
        var wake = new List<TaskCompletionSource<bool>>();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sweepTimer?.Dispose();
            foreach (Session session in _sessions.Values.ToArray())
            {
                lock (session.Sync)
                {
                    PurgeLocked(session, wake);
                }
            }

            _totalBytes = 0;
        }

        Complete(wake);
    }
}
