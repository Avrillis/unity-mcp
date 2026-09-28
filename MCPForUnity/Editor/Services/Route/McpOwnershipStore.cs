using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>What the project-local ownership file currently holds.</summary>
    public enum McpOwnershipStateKind
    {
        /// <summary>No ownership file exists.</summary>
        Absent = 0,

        /// <summary>A complete, parseable ownership record exists.</summary>
        Present,

        /// <summary>
        /// A file exists but cannot be read or parsed. This is UNKNOWN, never "absent" and never
        /// "stale": nothing may overwrite, adopt or delete it.
        /// </summary>
        UnreadableOrMalformed,
    }

    /// <summary>
    /// One read of the project-local ownership file, plus the identity of what was read.
    /// A snapshot is the unit of comparison for every conditional mutation.
    /// </summary>
    public sealed class McpOwnershipSnapshot
    {
        public McpOwnershipStateKind Kind { get; internal set; }

        /// <summary>The parsed record; non-null only when <see cref="Kind"/> is Present.</summary>
        public McpRunStateRecord Record { get; internal set; }

        /// <summary>Stable fingerprint of <see cref="Record"/>, or null when there is none.</summary>
        public string Fingerprint { get; internal set; }

        /// <summary>Human-readable detail (never contains credentials).</summary>
        public string Detail { get; internal set; }

        public bool IsPresent => Kind == McpOwnershipStateKind.Present;

        public bool IsUnknown => Kind == McpOwnershipStateKind.UnreadableOrMalformed;

        /// <summary>True when this snapshot still names exactly the same publication.</summary>
        public bool IsSamePublicationAs(McpOwnershipSnapshot other)
        {
            if (other == null || !IsPresent || !other.IsPresent)
            {
                return false;
            }

            return McpOwnershipIdentity.IsSamePublication(Record, other.Record);
        }

        internal static McpOwnershipSnapshot Absent(string detail = null)
            => new McpOwnershipSnapshot { Kind = McpOwnershipStateKind.Absent, Detail = detail };

        internal static McpOwnershipSnapshot Unknown(string detail)
            => new McpOwnershipSnapshot { Kind = McpOwnershipStateKind.UnreadableOrMalformed, Detail = detail };

        internal static McpOwnershipSnapshot Of(McpRunStateRecord record, string detail = null)
            => new McpOwnershipSnapshot
            {
                Kind = McpOwnershipStateKind.Present,
                Record = record,
                Fingerprint = McpOwnershipIdentity.Fingerprint(record),
                Detail = detail,
            };
    }

    /// <summary>
    /// Identity of one ownership publication: a per-lifecycle GUID plus a canonical fingerprint of
    /// the whole record. Two records are "the same publication" only when both agree, so a decision
    /// made about record X can never mutate a record Y that replaced it.
    /// </summary>
    public static class McpOwnershipIdentity
    {
        /// <summary>Canonical, order-stable fingerprint of a record.</summary>
        public static string Fingerprint(McpRunStateRecord record)
        {
            if (record == null)
            {
                return null;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(record.ToJson() ?? string.Empty);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>True when both records are the same publication.</summary>
        public static bool IsSamePublication(McpRunStateRecord left, McpRunStateRecord right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (!string.Equals(left.RecordId, right.RecordId, StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(Fingerprint(left), Fingerprint(right), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An exclusive, cross-process ownership-mutation session.
    ///
    /// The session holds the interprocess lock until it is disposed, and every mutation it performs
    /// re-checks the current file against the snapshot the decision was made from.
    /// </summary>
    public interface IMcpOwnershipTransaction : IDisposable
    {
        /// <summary>Reads the ownership file as it is right now, while the lock is held.</summary>
        McpOwnershipSnapshot Read();

        /// <summary>
        /// Publishes <paramref name="replacement"/> only when the file still holds exactly the
        /// publication described by <paramref name="expected"/>. Never deletes before writing.
        /// </summary>
        bool PublishIfCurrent(
            McpOwnershipSnapshot expected, McpRunStateRecord replacement, out string error);

        /// <summary>
        /// Removes the ownership file only when it still holds exactly the publication described
        /// by <paramref name="expected"/>. A successor's record is never deleted.
        /// </summary>
        bool DeleteIfCurrent(McpOwnershipSnapshot expected, out string error);
    }

    /// <summary>Provides the cross-process critical section every ownership mutation runs inside.</summary>
    public interface IMcpOwnershipLockProvider
    {
        /// <summary>
        /// Begins an exclusive ownership-mutation session, or fails closed when the lock cannot be
        /// obtained. Implementations must never steal or age out another writer's lock.
        /// </summary>
        bool TryBegin(out IMcpOwnershipTransaction transaction, out string error);
    }

    /// <summary>
    /// What to do with the ownership file once the CURRENT contents have been read inside the
    /// critical section. The decision is made from fresh state, never from a stale evaluation.
    /// </summary>
    public sealed class McpMutationDecision
    {
        public bool Proceed { get; private set; }
        public bool Delete { get; private set; }
        public McpRunStateRecord Replacement { get; private set; }
        public string Reason { get; private set; }

        public static McpMutationDecision Abort(string reason)
            => new McpMutationDecision { Proceed = false, Reason = reason };

        public static McpMutationDecision Write(McpRunStateRecord replacement)
            => new McpMutationDecision { Proceed = true, Replacement = replacement };

        public static McpMutationDecision Remove()
            => new McpMutationDecision { Proceed = true, Delete = true };
    }

    /// <summary>
    /// Cross-process conditional ownership mutation and termination.
    ///
    /// Atomic file publication alone is not enough: a decision taken from record X may only mutate
    /// or delete the file while it is still X. Every operation therefore runs inside the
    /// interprocess critical section and re-reads the file before acting.
    /// </summary>
    public static class McpOwnershipMutation
    {
        /// <summary>
        /// Runs <paramref name="decide"/> against the CURRENT ownership state, inside the
        /// cross-process lock, and applies the resulting write or delete.
        /// </summary>
        public static bool TryMutate(
            IMcpOwnershipLockProvider locks,
            Func<McpOwnershipSnapshot, McpMutationDecision> decide,
            out string error)
        {
            error = null;

            if (locks == null)
            {
                error = "no ownership lock provider is available; refusing to mutate ownership state.";
                return false;
            }

            if (decide == null)
            {
                error = "no ownership decision was supplied.";
                return false;
            }

            if (!locks.TryBegin(out IMcpOwnershipTransaction transaction, out string lockError))
            {
                error = lockError;
                return false;
            }

            using (transaction)
            {
                McpOwnershipSnapshot current = transaction.Read();
                McpMutationDecision decision = decide(current);
                if (decision == null || !decision.Proceed)
                {
                    error = decision?.Reason ?? "the ownership mutation was refused.";
                    return false;
                }

                return decision.Delete
                    ? transaction.DeleteIfCurrent(current, out error)
                    : transaction.PublishIfCurrent(current, decision.Replacement, out error);
            }
        }

        /// <summary>
        /// Final termination guard. Everything below happens inside the cross-process critical
        /// section, from a fresh read:
        ///
        ///   1. the ownership file must still hold exactly the evaluated publication;
        ///   2. the endpoint must still be owned by exactly the validated server PID;
        ///   3. the retained process handle must still be the validated lifetime;
        ///   4. only then is the process terminated through that retained handle;
        ///   5. the ownership file is deleted only while it is still that same publication.
        ///
        /// Any change - a replaced record, a changed or vanished listener, a different retained
        /// lifetime - refuses the kill and leaves the process untouched.
        /// </summary>
        public static McpTerminationOutcome TerminateIfStillOwned(
            IMcpOwnershipLockProvider locks,
            Func<System.Collections.Generic.IReadOnlyList<int>> listenerProbe,
            McpRunStateRecord evaluated,
            DateTime validatedServerStartUtc,
            IRetainedProcessHandle handle,
            Func<IRetainedProcessHandle, DateTime, string> terminate = null)
        {
            if (evaluated == null)
            {
                return McpTerminationOutcome.Refused("no validated ownership record was supplied.");
            }

            if (handle == null)
            {
                return McpTerminationOutcome.Refused(
                    "no retained process identity is available; refusing to kill by PID alone.");
            }

            if (locks == null)
            {
                return McpTerminationOutcome.Refused(
                    "the cross-process ownership lock could not be obtained; refusing to terminate.");
            }

            if (!locks.TryBegin(out IMcpOwnershipTransaction transaction, out string lockError))
            {
                return McpTerminationOutcome.Refused(lockError);
            }

            using (transaction)
            {
                McpOwnershipSnapshot current = transaction.Read();
                if (!current.IsPresent)
                {
                    return McpTerminationOutcome.Refused(
                        "the ownership record changed before termination; refusing to terminate.");
                }

                if (!McpOwnershipIdentity.IsSamePublication(evaluated, current.Record))
                {
                    return McpTerminationOutcome.Refused(
                        "a different ownership record is now published; refusing to terminate.");
                }

                System.Collections.Generic.IReadOnlyList<int> listeners =
                    listenerProbe?.Invoke() ?? Array.Empty<int>();
                if (listeners.Count != 1 || listeners[0] != evaluated.ServerPid)
                {
                    return McpTerminationOutcome.Refused(
                        "the endpoint's listener changed before termination; refusing to terminate.");
                }

                if (!handle.TryGetStartTimeUtc(out DateTime retainedStart)
                    || !McpOwnershipEvaluator.SameProcessInstant(retainedStart, validatedServerStartUtc))
                {
                    return McpTerminationOutcome.Refused(
                        "the retained process is not the validated server lifetime; refusing to terminate.");
                }

                // The kill always goes through the retained handle for the validated lifetime;
                // the optional delegate is the production terminator's own instant re-check.
                string killError = terminate != null
                    ? terminate(handle, validatedServerStartUtc)
                    : (McpTerminationIdentity.TryTerminate(validatedServerStartUtc, handle, out string defaultError)
                        ? null
                        : defaultError);
                if (killError != null)
                {
                    return McpTerminationOutcome.Refused(killError);
                }

                // Conditional: a successor lifecycle that published its own record meanwhile keeps it.
                bool removed = transaction.DeleteIfCurrent(current, out string deleteError);
                return McpTerminationOutcome.Stopped(removed, deleteError);
            }
        }
    }

    /// <summary>Outcome of the guarded termination sequence.</summary>
    public sealed class McpTerminationOutcome
    {
        public bool Terminated { get; private set; }

        /// <summary>True when this editor's own record was removed after the kill.</summary>
        public bool RecordRemoved { get; private set; }

        public string Reason { get; private set; }

        internal static McpTerminationOutcome Refused(string reason)
            => new McpTerminationOutcome { Terminated = false, Reason = reason };

        internal static McpTerminationOutcome Stopped(bool recordRemoved, string detail)
            => new McpTerminationOutcome
            {
                Terminated = true,
                RecordRemoved = recordRemoved,
                Reason = detail,
            };
    }

    /// <summary>
    /// File-backed ownership store and cross-process lock for one project's RunState directory.
    ///
    /// The lock is a same-directory file opened with <see cref="FileShare.None"/>, which the OS
    /// enforces across processes. It is deliberately never deleted and never aged out: a lock that
    /// cannot be acquired fails the mutation closed rather than being stolen from a live writer.
    /// </summary>
    public sealed class McpOwnershipStore : IMcpOwnershipLockProvider, IMcpOwnershipTransaction
    {
        /// <summary>Name of the interprocess ownership lock file (never deleted).</summary>
        public const string LockFileName = "handshake.lock";

        private readonly string _handshakePath;
        private readonly string _lockPath;
        private FileStream _lockStream;

        public McpOwnershipStore(string handshakePath, string lockPath = null)
        {
            _handshakePath = handshakePath;
            _lockPath = string.IsNullOrEmpty(lockPath)
                ? (string.IsNullOrEmpty(handshakePath)
                    ? null
                    : Path.Combine(Path.GetDirectoryName(handshakePath) ?? string.Empty, LockFileName))
                : lockPath;
        }

        /// <summary>Retries for roughly one second before failing closed.</summary>
        public int LockAttempts { get; set; } = 50;

        /// <summary>Delay between lock attempts, in milliseconds.</summary>
        public int LockRetryDelayMs { get; set; } = 20;

        /// <inheritdoc/>
        public bool TryBegin(out IMcpOwnershipTransaction transaction, out string error)
        {
            transaction = null;
            error = null;

            if (string.IsNullOrEmpty(_lockPath))
            {
                error = "the project-local RunState lock path could not be resolved.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lockPath) ?? string.Empty);
            }
            catch (Exception ex)
            {
                error = $"the RunState directory could not be created: {ex.Message}";
                return false;
            }

            for (int attempt = 0; attempt < Math.Max(1, LockAttempts); attempt++)
            {
                try
                {
                    _lockStream = new FileStream(
                        _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    transaction = this;
                    return true;
                }
                catch (IOException)
                {
                    // Another process holds it; wait and retry, never steal.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same treatment: unknown state fails closed rather than being forced.
                }

                if (attempt + 1 < Math.Max(1, LockAttempts) && LockRetryDelayMs > 0)
                {
                    Thread.Sleep(LockRetryDelayMs);
                }
            }

            error = "another process is mutating this project's ownership record; refusing to act "
                    + "on stale ownership state.";
            return false;
        }

        /// <inheritdoc/>
        public McpOwnershipSnapshot Read()
        {
            if (string.IsNullOrEmpty(_handshakePath))
            {
                return McpOwnershipSnapshot.Unknown(
                    "the project-local RunState directory could not be resolved.");
            }

            string json;
            try
            {
                if (!File.Exists(_handshakePath))
                {
                    return McpOwnershipSnapshot.Absent("no ownership record is present.");
                }

                json = File.ReadAllText(_handshakePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return McpOwnershipSnapshot.Unknown(
                    $"the ownership record could not be read: {ex.Message}");
            }

            if (!McpRunStateRecord.TryParse(json, out McpRunStateRecord record, out string parseError))
            {
                return McpOwnershipSnapshot.Unknown(
                    $"the ownership record is unusable: {parseError}");
            }

            return McpOwnershipSnapshot.Of(record);
        }

        /// <inheritdoc/>
        public bool PublishIfCurrent(
            McpOwnershipSnapshot expected, McpRunStateRecord replacement, out string error)
        {
            error = null;

            if (replacement == null)
            {
                error = "no replacement record was supplied.";
                return false;
            }

            McpOwnershipSnapshot current = Read();

            // Two writers may both have observed "absent"; the first to publish wins and the second
            // fails closed here rather than clobbering it.
            if (expected != null && expected.IsPresent)
            {
                if (!current.IsPresent || !current.IsSamePublicationAs(expected))
                {
                    error = "the ownership record changed before it could be replaced; "
                            + "refusing to overwrite it.";
                    return false;
                }
            }
            else if (current.IsPresent || current.IsUnknown)
            {
                error = current.IsUnknown
                    ? "the ownership record is unreadable; refusing to overwrite it."
                    : "another ownership record was published concurrently; refusing to overwrite it.";
                return false;
            }

            return McpRunStateFile.TryWriteAtomic(_handshakePath, replacement.ToJson(), out error);
        }

        /// <inheritdoc/>
        public bool DeleteIfCurrent(McpOwnershipSnapshot expected, out string error)
        {
            error = null;

            McpOwnershipSnapshot current = Read();
            if (!current.IsPresent)
            {
                error = current.IsUnknown
                    ? "the ownership record is unreadable; refusing to delete it."
                    : "the ownership record is no longer present.";
                return false;
            }

            if (expected != null && !current.IsSamePublicationAs(expected))
            {
                error = "a different ownership record is now published; refusing to delete it.";
                return false;
            }

            try
            {
                if (File.Exists(_handshakePath))
                {
                    File.Delete(_handshakePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"the ownership record could not be removed: {ex.Message}";
                return false;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            FileStream stream = _lockStream;
            _lockStream = null;
            if (stream == null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
                // Best effort: releasing the OS handle is enough to release the lock.
            }
        }
    }
}
