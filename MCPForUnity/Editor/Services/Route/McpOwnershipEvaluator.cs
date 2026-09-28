using System;
using System.Collections.Generic;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>Why a managed-route stop was refused.</summary>
    public enum McpOwnershipDenyReason
    {
        None = 0,

        /// <summary>No ownership record exists.</summary>
        NoRecord,

        /// <summary>The record exists but could not be read or parsed.</summary>
        MalformedRecord,

        /// <summary>The record describes a different project.</summary>
        ForeignProject,

        /// <summary>The record describes a different endpoint.</summary>
        ForeignEndpoint,

        /// <summary>The record belongs to a different editor process lifetime.</summary>
        ForeignEditorLifetime,

        /// <summary>The record has not confirmed a live server yet.</summary>
        IncompleteRecord,

        /// <summary>The recorded server process is not running.</summary>
        ServerNotRunning,

        /// <summary>The recorded server process lifetime could not be observed.</summary>
        MissingProcessLifetime,

        /// <summary>The recorded start time disagrees with the live process (PID reuse).</summary>
        PidReuse,

        /// <summary>The live process does not echo the expected launch nonce.</summary>
        NonceMismatch,

        /// <summary>The server PID is no longer the listener on the endpoint port.</summary>
        ServerNotListener,

        /// <summary>More than one process listens on the endpoint port.</summary>
        AmbiguousListener,

        /// <summary>A path involved in the decision escapes the project's RunState directory.</summary>
        PathEscape,

        /// <summary>The server PID evidence file disagrees with the record.</summary>
        PidFileMismatch,
    }

    /// <summary>Outcome of an ownership evaluation.</summary>
    public sealed class McpOwnershipDecision
    {
        private McpOwnershipDecision(bool allowed, McpOwnershipDenyReason reason, string detail)
        {
            Allowed = allowed;
            Reason = reason;
            Detail = detail;
        }

        /// <summary>True only when the current editor lifetime provably owns the live server.</summary>
        public bool Allowed { get; }

        public McpOwnershipDenyReason Reason { get; }

        public string Detail { get; }

        internal static McpOwnershipDecision Allow(string detail)
            => new McpOwnershipDecision(true, McpOwnershipDenyReason.None, detail);

        internal static McpOwnershipDecision Deny(McpOwnershipDenyReason reason, string detail)
            => new McpOwnershipDecision(false, reason, detail);
    }

    /// <summary>Outcome of checking whether an existing record may be reused or replaced.</summary>
    public enum McpAdoptionOutcome
    {
        /// <summary>No record on disk; a normal fresh launch may proceed.</summary>
        NoRecord = 0,

        /// <summary>The record belongs to the current editor lifetime; reuse it.</summary>
        OwnedByCurrentLifetime,

        /// <summary>The record is stale (its server is provably gone); safe to replace.</summary>
        Stale,

        /// <summary>A live server owned by another editor lifetime. Never adopt, never overwrite.</summary>
        LiveForeign,
    }

    /// <summary>
    /// Live OS observations gathered for one ownership decision. Values that could not be
    /// observed are left unavailable rather than guessed: the evaluator treats absent
    /// evidence as a refusal.
    /// </summary>
    public sealed class McpOwnershipObservation
    {
        public string CanonicalProjectRoot;
        public string RunStateDirectory;
        public string Endpoint;

        public int CurrentEditorPid;
        public DateTime? CurrentEditorStartUtc;

        public int ServerPid;
        public bool ServerProcessExists;
        /// <summary>
        /// True only when the server process existence check completed and its answer is
        /// trustworthy. A failed query leaves this false so the evaluator refuses instead of
        /// treating "could not look" as "not running".
        /// </summary>
        public bool ServerProcessExistenceKnown;
        public bool ServerProcessLifetimeAvailable;
        public DateTime? ServerProcessStartUtc;

        public bool ServerCommandLineAvailable;
        public string ServerCommandLine;

        /// <summary>
        /// Whether the editor process named by the record is still running. Used only to decide
        /// whether another editor lifetime's record is an orphan that may be replaced.
        /// </summary>
        public bool? RecordedEditorProcessExists;

        public string PidFilePath;
        public bool PidFileExists;
        public bool PidFileReadable;
        public int PidFilePid;

        public IReadOnlyList<int> ListeningProcessIds = Array.Empty<int>();
    }

    /// <summary>
    /// Decides whether the current editor lifetime may terminate a managed dedicated server.
    ///
    /// The rule is deliberately blunt: stop only when every piece of evidence agrees, and
    /// refuse on anything missing, stale, ambiguous or unexpected. Refusals leave the
    /// process untouched, which is always the safe outcome.
    /// </summary>
    public static class McpOwnershipEvaluator
    {
        /// <summary>
        /// Evaluates whether <paramref name="observation"/> is the exact server described by
        /// <paramref name="record"/>.
        /// </summary>
        public static McpOwnershipDecision EvaluateStop(
            McpRunStateRecord record,
            McpOwnershipObservation observation)
        {
            if (observation == null)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.MalformedRecord, "no live observation was gathered.");
            }

            if (record == null)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.NoRecord, "no ownership record is available.");
            }

            if (!record.IsStructurallyValid(out string structuralError))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.MalformedRecord, structuralError);
            }

            // ---- project identity -------------------------------------------------
            if (!McpRunStatePaths.PathsEqual(record.CanonicalProjectRoot, observation.CanonicalProjectRoot))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ForeignProject,
                    $"record project '{record.CanonicalProjectRoot}' does not match this project "
                    + $"'{observation.CanonicalProjectRoot}'.");
            }

            string expectedRunState = McpRunStatePaths.GetRunStateDirectory(record.CanonicalProjectRoot);
            if (!McpRunStatePaths.PathsEqual(expectedRunState, observation.RunStateDirectory))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PathEscape,
                    $"run state directory '{observation.RunStateDirectory}' is not the project-local "
                    + $"'{expectedRunState}'.");
            }

            // ---- endpoint ---------------------------------------------------------
            if (!string.Equals(
                    NormalizeEndpoint(record.Endpoint),
                    NormalizeEndpoint(observation.Endpoint),
                    StringComparison.OrdinalIgnoreCase))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ForeignEndpoint,
                    $"record endpoint '{record.Endpoint}' does not match '{observation.Endpoint}'.");
            }

            // ---- editor lifetime --------------------------------------------------
            if (record.EditorPid != observation.CurrentEditorPid)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ForeignEditorLifetime,
                    $"record editor PID {record.EditorPid} is not this editor ({observation.CurrentEditorPid}).");
            }

            if (!observation.CurrentEditorStartUtc.HasValue
                || !McpRunStateRecord.TryParseUtc(record.EditorStartUtc, out DateTime recordedEditorStart))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ForeignEditorLifetime,
                    "the editor process start time is unavailable on one side of the comparison.");
            }

            if (!SameProcessInstant(recordedEditorStart, observation.CurrentEditorStartUtc.Value))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ForeignEditorLifetime,
                    "the recorded editor start time belongs to a different editor lifetime.");
            }

            // ---- lifecycle --------------------------------------------------------
            if (!string.Equals(record.LifecycleState, McpRunStateRecord.LifecycleRunning, StringComparison.Ordinal))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.IncompleteRecord,
                    $"lifecycle_state is '{record.LifecycleState}', not '{McpRunStateRecord.LifecycleRunning}'.");
            }

            // ---- server process ---------------------------------------------------
            if (record.ServerPid <= 0)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.IncompleteRecord, "the record has no server PID.");
            }

            if (observation.ServerPid > 0 && observation.ServerPid != record.ServerPid)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PidFileMismatch,
                    $"observed server PID {observation.ServerPid} is not the recorded {record.ServerPid}.");
            }

            if (!observation.ServerProcessExistenceKnown)
            {
                // The existence query itself failed. Absence of evidence is not evidence of
                // absence: refuse rather than act on an unobservable process.
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.MissingProcessLifetime,
                    "the server process existence check did not complete.");
            }

            if (!observation.ServerProcessExists)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ServerNotRunning,
                    $"server PID {record.ServerPid} is not running.");
            }

            if (record.ServerPid == observation.CurrentEditorPid)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ServerNotRunning,
                    "the recorded server PID is this Unity Editor process.");
            }

            if (!observation.ServerProcessLifetimeAvailable
                || !observation.ServerProcessStartUtc.HasValue
                || !McpRunStateRecord.TryParseUtc(record.ServerStartUtc, out DateTime recordedServerStart))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.MissingProcessLifetime,
                    "the server process start time could not be corroborated.");
            }

            if (!SameProcessInstant(recordedServerStart, observation.ServerProcessStartUtc.Value))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PidReuse,
                    $"server PID {record.ServerPid} was created at "
                    + $"{observation.ServerProcessStartUtc.Value:O}, not {recordedServerStart:O} (PID reuse).");
            }

            // ---- nonce ------------------------------------------------------------
            if (!observation.ServerCommandLineAvailable
                || string.IsNullOrEmpty(observation.ServerCommandLine))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.NonceMismatch,
                    "the server command line is unavailable, so the launch nonce cannot be echoed.");
            }

            if (observation.ServerCommandLine.IndexOf(record.InstanceToken, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.NonceMismatch,
                    "the live process does not carry the expected launch nonce.");
            }

            // ---- PID evidence -----------------------------------------------------
            if (string.IsNullOrWhiteSpace(observation.PidFilePath)
                || !McpRunStatePaths.PathsEqual(observation.PidFilePath, record.PidFilePath))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PidFileMismatch,
                    "the PID file path does not match the recorded PID file path.");
            }

            if (!McpRunStatePaths.IsPathInside(record.PidFilePath, expectedRunState)
                || !McpRunStatePaths.IsPathInside(observation.PidFilePath, expectedRunState))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PathEscape,
                    $"the PID file path escapes '{expectedRunState}'.");
            }

            if (!observation.PidFileExists || !observation.PidFileReadable)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PidFileMismatch,
                    "the server PID file is missing or unreadable.");
            }

            if (observation.PidFilePid != record.ServerPid)
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.PidFileMismatch,
                    $"the PID file names {observation.PidFilePid}, not the recorded {record.ServerPid}.");
            }

            // ---- listener ---------------------------------------------------------
            IReadOnlyList<int> listeners = observation.ListeningProcessIds ?? Array.Empty<int>();
            if (!Contains(listeners, record.ServerPid))
            {
                return McpOwnershipDecision.Deny(
                    McpOwnershipDenyReason.ServerNotListener,
                    $"server PID {record.ServerPid} is not listening on the endpoint port.");
            }

            foreach (int pid in listeners)
            {
                if (pid > 0 && pid != record.ServerPid)
                {
                    return McpOwnershipDecision.Deny(
                        McpOwnershipDenyReason.AmbiguousListener,
                        $"PID {pid} also listens on the endpoint port.");
                }
            }

            return McpOwnershipDecision.Allow(
                $"server PID {record.ServerPid} is the confirmed owner of {record.Endpoint}.");
        }

        /// <summary>
        /// Identifies the server of a pending (<see cref="McpRunStateRecord.LifecycleStarting"/>)
        /// record and returns the completed record.
        ///
        /// This exists so a launch interrupted by a domain reload can still be cleaned up: the
        /// record carries the per-launch nonce and was written immediately before the spawn, so
        /// a live process that started after that write, echoes the nonce, owns the PID file and
        /// is the sole listener on the endpoint is provably the process this editor launched.
        ///
        /// Returns false for anything less; the caller must then leave the record pending and
        /// never terminate.
        /// </summary>
        public static bool TryIdentifyPendingServer(
            McpRunStateRecord record,
            McpOwnershipObservation observation,
            out McpRunStateRecord promoted,
            out string detail)
        {
            promoted = null;
            detail = null;

            if (record == null || observation == null)
            {
                detail = "no pending record or observation was supplied.";
                return false;
            }

            if (!string.Equals(record.LifecycleState, McpRunStateRecord.LifecycleStarting, StringComparison.Ordinal))
            {
                detail = $"the record lifecycle is '{record.LifecycleState}', not pending.";
                return false;
            }

            if (!record.IsStructurallyValid(out string structuralError))
            {
                detail = structuralError;
                return false;
            }

            if (!McpRunStatePaths.PathsEqual(record.CanonicalProjectRoot, observation.CanonicalProjectRoot))
            {
                detail = "the pending record belongs to a different project.";
                return false;
            }

            if (!McpRunStatePaths.PathsEqual(
                    McpRunStatePaths.GetRunStateDirectory(record.CanonicalProjectRoot),
                    observation.RunStateDirectory))
            {
                detail = "the pending record is not in this project's RunState directory.";
                return false;
            }

            if (!string.Equals(
                    NormalizeEndpoint(record.Endpoint),
                    NormalizeEndpoint(observation.Endpoint),
                    StringComparison.OrdinalIgnoreCase))
            {
                detail = "the pending record is for a different endpoint.";
                return false;
            }

            if (record.EditorPid != observation.CurrentEditorPid)
            {
                detail = "the pending record belongs to a different editor process.";
                return false;
            }

            if (!observation.CurrentEditorStartUtc.HasValue
                || !McpRunStateRecord.TryParseUtc(record.EditorStartUtc, out DateTime recordedEditorStart)
                || !SameProcessInstant(recordedEditorStart, observation.CurrentEditorStartUtc.Value))
            {
                detail = "the pending record belongs to a different editor lifetime.";
                return false;
            }

            string runStateDirectory = McpRunStatePaths.GetRunStateDirectory(record.CanonicalProjectRoot);
            if (string.IsNullOrWhiteSpace(observation.PidFilePath)
                || !McpRunStatePaths.PathsEqual(observation.PidFilePath, record.PidFilePath)
                || !McpRunStatePaths.IsPathInside(observation.PidFilePath, runStateDirectory))
            {
                detail = "the PID file path does not match the pending record's RunState path.";
                return false;
            }

            if (!observation.PidFileExists || !observation.PidFileReadable || observation.PidFilePid <= 0)
            {
                detail = "the launched server has not written its PID file yet.";
                return false;
            }

            int serverPid = observation.PidFilePid;
            if (observation.ServerPid > 0 && observation.ServerPid != serverPid)
            {
                detail = "the observed server PID disagrees with the PID file.";
                return false;
            }

            if (!observation.ServerProcessExists)
            {
                detail = $"server PID {serverPid} is not running.";
                return false;
            }

            if (serverPid == observation.CurrentEditorPid)
            {
                detail = "the PID file names this Unity Editor process.";
                return false;
            }

            if (!observation.ServerProcessLifetimeAvailable
                || !observation.ServerProcessStartUtc.HasValue)
            {
                detail = "the server process start time is unavailable.";
                return false;
            }

            // Anti-PID-reuse anchor: the process must have been created at or after the record
            // was written, since the record is written immediately before the spawn.
            if (!McpRunStateRecord.TryParseUtc(record.WrittenUtc, out DateTime writtenUtc))
            {
                detail = "the pending record has no usable write time.";
                return false;
            }

            DateTime serverStart = observation.ServerProcessStartUtc.Value;
            if (serverStart < writtenUtc)
            {
                detail = "the process predates the launch record (PID reuse).";
                return false;
            }

            if (!observation.ServerCommandLineAvailable
                || string.IsNullOrEmpty(observation.ServerCommandLine)
                || observation.ServerCommandLine.IndexOf(record.InstanceToken, StringComparison.OrdinalIgnoreCase) < 0)
            {
                detail = "the live process does not carry the pending record's launch nonce.";
                return false;
            }

            IReadOnlyList<int> listeners = observation.ListeningProcessIds ?? Array.Empty<int>();
            if (listeners.Count != 1 || listeners[0] != serverPid)
            {
                detail = "the endpoint is not exclusively owned by the pending server's PID.";
                return false;
            }

            promoted = new McpRunStateRecord
            {
                SchemaVersion = McpRunStateRecord.CurrentSchemaVersion,
                CanonicalProjectRoot = record.CanonicalProjectRoot,
                Endpoint = record.Endpoint,
                EditorPid = record.EditorPid,
                EditorStartUtc = record.EditorStartUtc,
                ServerPid = serverPid,
                ServerStartUtc = McpRunStateRecord.FormatUtc(serverStart),
                InstanceToken = record.InstanceToken,
                PidFilePath = record.PidFilePath,
                LifecycleState = McpRunStateRecord.LifecycleRunning,
                WrittenUtc = McpRunStateRecord.FormatUtc(DateTime.UtcNow),
            };

            detail = $"identified the pending server as PID {serverPid}.";
            return true;
        }

        /// <summary>
        /// Decides what may happen to an existing record before a fresh server launch.
        /// A live server owned by another editor lifetime is never adopted or overwritten.
        /// </summary>
        public static McpAdoptionOutcome EvaluateAdoption(
            McpRunStateRecord record,
            McpOwnershipObservation observation,
            out string detail)
        {
            detail = null;

            if (record == null)
            {
                detail = "no ownership record is present.";
                return McpAdoptionOutcome.NoRecord;
            }

            if (!record.IsStructurallyValid(out string structuralError))
            {
                detail = $"the existing record is unusable ({structuralError}); it may be replaced.";
                return McpAdoptionOutcome.Stale;
            }

            if (observation == null)
            {
                detail = "no live observation was gathered; an existing record is treated as live and untouched.";
                return McpAdoptionOutcome.LiveForeign;
            }

            McpOwnershipDecision ownership = EvaluateStop(record, observation);
            if (ownership.Allowed)
            {
                detail = "the record belongs to this editor lifetime.";
                return McpAdoptionOutcome.OwnedByCurrentLifetime;
            }

            // The record may still be this editor's own incomplete launch (for example a launch
            // interrupted by a domain reload). That record is ours to complete or replace; only a
            // record from a *different* editor lifetime is ever treated as foreign.
            if (IsCurrentEditorLifetime(record, observation))
            {
                detail = "the record was written by this editor lifetime; it may be reused or replaced.";
                return McpAdoptionOutcome.OwnedByCurrentLifetime;
            }

            // The record is not ours. It is only replaceable when its server is provably gone
            // and its editor lifetime is over; a live server is never adopted.
            // A pending record is never replaceable on the strength of a missing listener or a
            // not-yet-written pidfile: "still starting" and "gone" are indistinguishable there.
            if (string.Equals(record.LifecycleState, McpRunStateRecord.LifecycleStarting, StringComparison.Ordinal))
            {
                detail = "the existing record belongs to another editor lifetime and is still pending "
                         + "(or not yet observable); refusing to adopt or overwrite it.";
                return McpAdoptionOutcome.LiveForeign;
            }

            // Only positively established staleness may be replaced: the recorded server must be
            // confirmed gone AND the editor that wrote the record must be confirmed gone too.
            bool serverConfirmedGone = observation.ServerProcessExistenceKnown
                                       && !observation.ServerProcessExists;
            bool editorConfirmedGone = observation.RecordedEditorProcessExists.HasValue
                                       && !observation.RecordedEditorProcessExists.Value;

            if (serverConfirmedGone && editorConfirmedGone && !Contains(observation.ListeningProcessIds, record.ServerPid))
            {
                detail = $"the recorded server and its editor are both gone ({ownership.Reason}); the record may be replaced.";
                return McpAdoptionOutcome.Stale;
            }

            detail = $"a live server owned by another editor lifetime is registered for this project "
                     + $"({ownership.Reason}: {ownership.Detail}); refusing to adopt or overwrite it.";
            return McpAdoptionOutcome.LiveForeign;
        }

        /// <summary>
        /// True when the record names this exact editor process lifetime (PID and creation
        /// instant). An unavailable instant is treated as "not proven ours".
        /// </summary>
        public static bool IsCurrentEditorLifetime(
            McpRunStateRecord record,
            McpOwnershipObservation observation)
        {
            if (record == null || observation == null)
            {
                return false;
            }

            if (record.EditorPid <= 0 || record.EditorPid != observation.CurrentEditorPid)
            {
                return false;
            }

            // A record copied from another project can carry the same editor PID; it is only
            // "ours" when it also names this project AND this endpoint.
            if (!McpRunStatePaths.PathsEqual(
                    record.CanonicalProjectRoot, observation.CanonicalProjectRoot))
            {
                return false;
            }

            if (!string.Equals(
                    NormalizeEndpoint(record.Endpoint),
                    NormalizeEndpoint(observation.Endpoint),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return observation.CurrentEditorStartUtc.HasValue
                   && McpRunStateRecord.TryParseUtc(record.EditorStartUtc, out DateTime recordedEditorStart)
                   && SameProcessInstant(recordedEditorStart, observation.CurrentEditorStartUtc.Value);
        }

        /// <summary>
        /// Exact process-creation comparison. Both sides are read from the same OS process
        /// identity source and round-tripped through the invariant "O" format, so no tolerance
        /// is required (or justified): any difference means a different process lifetime.
        /// </summary>
        public static bool SameProcessInstant(DateTime left, DateTime right)
        {
            return ToUtc(left).Ticks == ToUtc(right).Ticks;
        }

        private static DateTime ToUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        private static bool Contains(IReadOnlyList<int> values, int pid)
        {
            if (values == null)
            {
                return false;
            }

            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == pid)
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            return string.IsNullOrWhiteSpace(endpoint) ? string.Empty : endpoint.Trim().TrimEnd('/');
        }
    }
}
