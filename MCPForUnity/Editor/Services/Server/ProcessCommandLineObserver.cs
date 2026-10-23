using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>Where one command-line observation stands.</summary>
    public enum CommandLineObservationStatus
    {
        /// <summary>The query has not finished. This is never evidence of anything.</summary>
        Pending = 0,

        /// <summary>The complete query succeeded for exactly one process lifetime.</summary>
        Ready,

        /// <summary>The query could not produce a trustworthy command line. Non-authorizing.</summary>
        Failed,
    }

    /// <summary>Immutable result of asking for one process's command line.</summary>
    public sealed class CommandLineObservation
    {
        private CommandLineObservation(
            CommandLineObservationStatus status,
            string commandLine,
            WindowsCommandLineStatus failure,
            string detail)
        {
            Status = status;
            CommandLine = commandLine;
            Failure = failure;
            Detail = detail;
        }

        public CommandLineObservationStatus Status { get; }

        /// <summary>The exact command line; null unless <see cref="IsReady"/>.</summary>
        public string CommandLine { get; }

        /// <summary>Why a failed observation failed; meaningless otherwise.</summary>
        public WindowsCommandLineStatus Failure { get; }

        public string Detail { get; }

        public bool IsReady => Status == CommandLineObservationStatus.Ready;

        public bool IsPending => Status == CommandLineObservationStatus.Pending;

        internal static readonly CommandLineObservation PendingObservation = new CommandLineObservation(
            CommandLineObservationStatus.Pending,
            null,
            WindowsCommandLineStatus.RunFailed,
            "the command line query has not completed yet.");

        internal static CommandLineObservation ReadyWith(string commandLine)
            => new CommandLineObservation(
                CommandLineObservationStatus.Ready, commandLine, WindowsCommandLineStatus.Ok, "the command line was read.");

        internal static CommandLineObservation FailedWith(WindowsCommandLineStatus failure, string detail)
            => new CommandLineObservation(CommandLineObservationStatus.Failed, null, failure, detail);
    }

    /// <summary>
    /// Supplies process command lines to Editor callbacks WITHOUT ever making them wait.
    ///
    /// Reading a command line through CIM takes hundreds of milliseconds and can take seconds, so
    /// no Editor-facing call may perform it inline. This type moves the blocking query onto a worker
    /// and gives callers two non-blocking views of it:
    ///
    ///   * <see cref="Observe"/> returns immediately: <c>Ready</c> only when a COMPLETE, verified
    ///     query already succeeded for that exact process lifetime; otherwise <c>Pending</c> (and a
    ///     query is started if none is running) or <c>Failed</c>. Callers treat anything but
    ///     <c>Ready</c> as "no evidence", so a pending query can never authorize ownership.
    ///   * <see cref="ObserveAsync"/> is the awaitable form for lifecycle code that is already async.
    ///
    /// What is, and is not, remembered. A command line is a fact about ONE process lifetime, so an
    /// observation is keyed by (PID, creation instant), and the creation instant is read afresh from
    /// the OS on every call. A lookup for a recycled PID has a different key and cannot see the old
    /// entry. Nothing here is an ownership decision: no success is cached as authorization, and every
    /// caller still re-reads the record, listener, PID file, lifetime and nonce when it decides.
    ///
    /// Stale results. The creation instant is read before the query starts and again when it ends;
    /// if they differ (the PID was recycled mid-flight) the result is discarded. <see cref="Reset"/>
    /// cancels everything in flight and forgets every entry, so a query that still finishes afterwards
    /// has nowhere to publish its result.
    ///
    /// Bounds. At most one query child runs at a time, at most <c>maxOutstanding</c> distinct
    /// lifetimes may be queued or running (further requests fail immediately as busy), a failed
    /// lookup is not retried during a short cooldown, and every entry has its own deadline. Calls for
    /// a lifetime that is already being queried share that one query, so repeated polling cannot fan
    /// out into repeated child processes.
    ///
    /// Free of Unity dependencies: it touches no Unity API, so its worker threads are safe.
    /// </summary>
    public sealed class ProcessCommandLineObserver
    {
        public const int DefaultMaxOutstanding = 3;

        // A command line is a fact about one process lifetime and the lookup key already includes that
        // lifetime, so an entry never describes a different process. The window only bounds memory, and
        // is long so a long-lived Editor session can still stop its server at quit without a cold query.
        public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromHours(8);
        public static readonly TimeSpan DefaultFailureCooldown = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan DefaultEntryDeadline = TimeSpan.FromSeconds(20);

        private const int MaxRetainedEntries = 16;

        private static readonly Lazy<ProcessCommandLineObserver> SharedInstance =
            new Lazy<ProcessCommandLineObserver>(CreateShared, LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly Func<int, DateTime?> _lifetimeOf;
        private readonly Func<int, CancellationToken, WindowsCommandLineResult> _query;
        private readonly Func<DateTime> _clock;
        private readonly int _maxOutstanding;
        private readonly TimeSpan _idleTtl;
        private readonly TimeSpan _failureCooldown;
        private readonly TimeSpan _entryDeadline;

        private readonly object _gate = new object();
        private readonly Dictionary<Key, Entry> _entries = new Dictionary<Key, Entry>();
        private readonly SemaphoreSlim _slot = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _resetCts = new CancellationTokenSource();
        private int _outstanding;

        private int _queriesStarted;
        private int _activeQueries;
        private int _peakActiveQueries;

        public ProcessCommandLineObserver(
            Func<int, DateTime?> lifetimeOf,
            Func<int, CancellationToken, WindowsCommandLineResult> query,
            int maxOutstanding = DefaultMaxOutstanding,
            TimeSpan? idleTtl = null,
            TimeSpan? failureCooldown = null,
            TimeSpan? entryDeadline = null,
            Func<DateTime> clock = null)
        {
            _lifetimeOf = lifetimeOf ?? throw new ArgumentNullException(nameof(lifetimeOf));
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _maxOutstanding = maxOutstanding > 0 ? maxOutstanding : DefaultMaxOutstanding;
            _idleTtl = idleTtl ?? DefaultIdleTtl;
            _failureCooldown = failureCooldown ?? DefaultFailureCooldown;
            _entryDeadline = entryDeadline ?? DefaultEntryDeadline;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>The process-wide observer every production caller shares, so queries coalesce.</summary>
        public static ProcessCommandLineObserver Shared => SharedInstance.Value;

        /// <summary>Queries started since creation (each is at most one child process).</summary>
        internal int QueriesStarted => Volatile.Read(ref _queriesStarted);

        /// <summary>Highest number of queries that ever ran at the same moment.</summary>
        internal int PeakActiveQueries => Volatile.Read(ref _peakActiveQueries);

        /// <summary>Distinct lifetimes currently queued or running.</summary>
        internal int Outstanding => Volatile.Read(ref _outstanding);

        /// <summary>
        /// Cancels and clears the shared observer, if one was ever created. Called when the Editor
        /// quits or reloads so no query child outlives the lifetime that asked for it.
        /// </summary>
        public static void ResetSharedIfCreated()
        {
            if (SharedInstance.IsValueCreated)
            {
                SharedInstance.Value.Reset();
            }
        }

        /// <summary>Reads a process's creation instant in UTC, or null when it cannot be read.</summary>
        public static DateTime? ReadProcessCreationUtc(int pid)
        {
            if (pid <= 0)
            {
                return null;
            }

            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return process.StartTime.ToUniversalTime();
                }
            }
            catch
            {
                return null;
            }
        }

        private static ProcessCommandLineObserver CreateShared()
            => new ProcessCommandLineObserver(
                ReadProcessCreationUtc,
                (pid, token) => WindowsProcessCommandLine.Query(
                    pid, null, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));

        /// <summary>
        /// Returns what is known about this process's command line right now, without waiting.
        /// Starts (or joins) at most one background query for the process's current lifetime.
        /// </summary>
        public CommandLineObservation Observe(int pid)
            => ObserveCore(pid, out _);

        /// <summary>
        /// Completes when the observation for the process's current lifetime is final (Ready or
        /// Failed), when <paramref name="cancellationToken"/> fires, or when the observer is reset.
        /// Awaiting never blocks a thread, and cancelling the wait does not disturb other waiters.
        /// </summary>
        public async Task<CommandLineObservation> ObserveAsync(int pid, CancellationToken cancellationToken)
        {
            CommandLineObservation first = ObserveCore(pid, out Entry entry);
            if (!first.IsPending || entry == null)
            {
                return first;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                Task finished = await Task.WhenAny(entry.Completion.Task, cancelled.Task).ConfigureAwait(false);
                if (ReferenceEquals(finished, entry.Completion.Task))
                {
                    return await entry.Completion.Task.ConfigureAwait(false);
                }
            }

            return CommandLineObservation.FailedWith(
                WindowsCommandLineStatus.Cancelled, "waiting for the command line was cancelled.");
        }

        /// <summary>
        /// Cancels every query in flight (killing its child), discards every remembered observation
        /// and invalidates results that finish later. Used when the Editor lifetime ends or reloads.
        /// </summary>
        public void Reset()
        {
            CancellationTokenSource previous;
            List<Entry> abandoned;
            lock (_gate)
            {
                previous = _resetCts;
                _resetCts = new CancellationTokenSource();
                abandoned = new List<Entry>(_entries.Values);
                _entries.Clear();
            }

            try { previous.Cancel(); } catch { /* a callback threw; the queries still end by deadline */ }

            CommandLineObservation cancelled = CommandLineObservation.FailedWith(
                WindowsCommandLineStatus.Cancelled, "the observer was reset.");
            foreach (Entry entry in abandoned)
            {
                entry.Completion.TrySetResult(cancelled);
            }
        }

        private CommandLineObservation ObserveCore(int pid, out Entry entry)
        {
            entry = null;
            if (pid <= 0)
            {
                return CommandLineObservation.FailedWith(
                    WindowsCommandLineStatus.InvalidPid, "the process id must be a positive integer.");
            }

            DateTime? lifetime = ReadLifetime(pid);
            if (!lifetime.HasValue)
            {
                return CommandLineObservation.FailedWith(
                    WindowsCommandLineStatus.ProcessNotFound,
                    "the process lifetime could not be read, so no result can be bound to it.");
            }

            var key = new Key(pid, lifetime.Value.Ticks);
            Entry created = null;
            CancellationToken resetToken;
            lock (_gate)
            {
                DateTime now = _clock();
                PruneLocked(now);

                if (_entries.TryGetValue(key, out Entry existing))
                {
                    CommandLineObservation known = existing.Result;
                    if (known == null)
                    {
                        existing.LastUsedUtc = now;
                        entry = existing;
                        return CommandLineObservation.PendingObservation;
                    }

                    if (known.IsReady || now - existing.CompletedUtc < _failureCooldown)
                    {
                        existing.LastUsedUtc = now;
                        return known;
                    }

                    // A failure whose cooldown ended is retried.
                    _entries.Remove(key);
                }

                if (_outstanding >= _maxOutstanding)
                {
                    return CommandLineObservation.FailedWith(
                        WindowsCommandLineStatus.RunFailed,
                        "too many command line queries are already outstanding.");
                }

                _outstanding++;
                created = new Entry(now);
                _entries[key] = created;
                resetToken = _resetCts.Token;
            }

            entry = created;
            StartQuery(created, pid, lifetime.Value, resetToken);
            return CommandLineObservation.PendingObservation;
        }

        private void StartQuery(Entry entry, int pid, DateTime lifetimeBefore, CancellationToken resetToken)
        {
            // Everything below runs on pool threads; the caller has already returned.
            _ = Task.Run(async () =>
            {
                CommandLineObservation outcome;
                var linked = CancellationTokenSource.CreateLinkedTokenSource(resetToken);
                bool slotHeld = false;
                try
                {
                    linked.CancelAfter(_entryDeadline);
                    await _slot.WaitAsync(linked.Token).ConfigureAwait(false);
                    slotHeld = true;

                    int active = Interlocked.Increment(ref _activeQueries);
                    UpdatePeak(active);
                    Interlocked.Increment(ref _queriesStarted);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        WindowsCommandLineResult result = await Task.Run(
                            () => _query(pid, linked.Token), CancellationToken.None).ConfigureAwait(false);
                        outcome = Interpret(result);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeQueries);
                    }
                }
                catch (OperationCanceledException)
                {
                    outcome = CommandLineObservation.FailedWith(
                        WindowsCommandLineStatus.Cancelled, "the command line query was cancelled or ran out of time.");
                }
                catch (Exception ex)
                {
                    outcome = CommandLineObservation.FailedWith(
                        WindowsCommandLineStatus.RunFailed, "the command line query failed: " + ex.GetType().Name);
                }
                finally
                {
                    if (slotHeld)
                    {
                        _slot.Release();
                    }

                    linked.Dispose();
                }

                Seal(entry, pid, lifetimeBefore, outcome);
            });
        }

        private void Seal(Entry entry, int pid, DateTime lifetimeBefore, CommandLineObservation outcome)
        {
            // A result belongs to the lifetime it was started for. If the PID now names a different
            // process, the line that was read may not have been this one's, so it is discarded.
            if (outcome.IsReady)
            {
                DateTime? after = ReadLifetime(pid);
                if (!after.HasValue || after.Value.Ticks != lifetimeBefore.Ticks)
                {
                    outcome = CommandLineObservation.FailedWith(
                        WindowsCommandLineStatus.RunFailed,
                        "the process lifetime changed while the query ran, so the result was discarded.");
                }
            }

            lock (_gate)
            {
                _outstanding--;

                DateTime now = _clock();
                entry.Result = outcome;
                entry.CompletedUtc = now;
                entry.LastUsedUtc = now;
            }

            entry.Completion.TrySetResult(outcome);
        }

        private static CommandLineObservation Interpret(WindowsCommandLineResult result)
        {
            if (result == null)
            {
                return CommandLineObservation.FailedWith(
                    WindowsCommandLineStatus.RunFailed, "the command line query returned nothing.");
            }

            return result.Succeeded
                ? CommandLineObservation.ReadyWith(result.CommandLine)
                : CommandLineObservation.FailedWith(result.Status, result.Detail);
        }

        private DateTime? ReadLifetime(int pid)
        {
            try
            {
                return _lifetimeOf(pid);
            }
            catch
            {
                return null;
            }
        }

        private void UpdatePeak(int active)
        {
            int seen;
            while (active > (seen = Volatile.Read(ref _peakActiveQueries)))
            {
                if (Interlocked.CompareExchange(ref _peakActiveQueries, active, seen) == seen)
                {
                    break;
                }
            }
        }

        private void PruneLocked(DateTime now)
        {
            List<Key> remove = null;
            foreach (KeyValuePair<Key, Entry> pair in _entries)
            {
                Entry e = pair.Value;
                if (e.Result != null && now - e.LastUsedUtc > _idleTtl)
                {
                    (remove ?? (remove = new List<Key>())).Add(pair.Key);
                }
            }

            if (remove != null)
            {
                foreach (Key key in remove)
                {
                    _entries.Remove(key);
                }
            }

            while (_entries.Count > MaxRetainedEntries)
            {
                Key oldest = default;
                DateTime oldestUsed = DateTime.MaxValue;
                bool found = false;
                foreach (KeyValuePair<Key, Entry> pair in _entries)
                {
                    if (pair.Value.Result != null && pair.Value.LastUsedUtc < oldestUsed)
                    {
                        oldest = pair.Key;
                        oldestUsed = pair.Value.LastUsedUtc;
                        found = true;
                    }
                }

                if (!found)
                {
                    break;
                }

                _entries.Remove(oldest);
            }
        }

        private readonly struct Key : IEquatable<Key>
        {
            public Key(int pid, long lifetimeTicks)
            {
                Pid = pid;
                LifetimeTicks = lifetimeTicks;
            }

            public int Pid { get; }
            public long LifetimeTicks { get; }

            public bool Equals(Key other) => Pid == other.Pid && LifetimeTicks == other.LifetimeTicks;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => unchecked((Pid * 397) ^ LifetimeTicks.GetHashCode());
        }

        private sealed class Entry
        {
            public Entry(DateTime now)
            {
                LastUsedUtc = now;
                Completion = new TaskCompletionSource<CommandLineObservation>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public TaskCompletionSource<CommandLineObservation> Completion { get; }
            public CommandLineObservation Result { get; set; }
            public DateTime LastUsedUtc { get; set; }
            public DateTime CompletedUtc { get; set; }
        }
    }
}
