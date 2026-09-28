using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Server;
using MCPForUnity.Editor.Services.Route;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Service for managing MCP server lifecycle
    /// </summary>
    public class ServerManagementService : IServerManagementService
    {
        private readonly IProcessDetector _processDetector;
        private readonly IPidFileManager _pidFileManager;
        private readonly IProcessTerminator _processTerminator;
        private readonly IServerCommandBuilder _commandBuilder;
        private readonly ITerminalLauncher _terminalLauncher;
        private readonly IMcpRouteStateStore _routeStateStore;
        private readonly IMcpProcessInspector _processInspector;

        private System.Diagnostics.Process _lastLaunchedProcess;

        // Completes the ownership record for a launch once the server's PID evidence appears.
        // Stopping waits briefly on it so a quit right after a launch can still clean up.
        private Task _launchFinalizationTask;

        /// <summary>
        /// Creates a new ServerManagementService with default dependencies.
        /// </summary>
        public ServerManagementService() : this(null, null, null, null, null) { }

        /// <summary>
        /// Creates a new ServerManagementService with injected dependencies (for testing).
        /// </summary>
        /// <param name="processDetector">Process detector implementation (null for default)</param>
        /// <param name="pidFileManager">PID file manager implementation (null for default)</param>
        /// <param name="processTerminator">Process terminator implementation (null for default)</param>
        /// <param name="commandBuilder">Server command builder implementation (null for default)</param>
        /// <param name="terminalLauncher">Terminal launcher implementation (null for default)</param>
        public ServerManagementService(
            IProcessDetector processDetector,
            IPidFileManager pidFileManager = null,
            IProcessTerminator processTerminator = null,
            IServerCommandBuilder commandBuilder = null,
            ITerminalLauncher terminalLauncher = null,
            IMcpRouteStateStore routeStateStore = null,
            IMcpProcessInspector processInspector = null)
        {
            _processDetector = processDetector ?? new ProcessDetector();
            _pidFileManager = pidFileManager ?? new PidFileManager();
            _processTerminator = processTerminator ?? new ProcessTerminator(_processDetector);
            _commandBuilder = commandBuilder ?? new ServerCommandBuilder();
            _terminalLauncher = terminalLauncher ?? new TerminalLauncher();
            _routeStateStore = routeStateStore ?? new McpRouteStateStore();
            _processInspector = processInspector ?? new McpProcessInspector(_processDetector);
        }

        // ------------------------------------------------------------------
        // Managed-route ownership
        //
        // Every lifecycle decision goes through the project-local ownership record
        // (Library/MCPForUnity/RunState/handshake.json) corroborated against live OS
        // observations. There is no port, process-name, argument-fingerprint or global
        // EditorPrefs fallback: anything not provably owned by this editor lifetime is
        // left untouched.
        // ------------------------------------------------------------------

        private string GetLocalHttpServerPidFilePath(int port)
        {
            return _routeStateStore.GetPidFilePath(port);
        }

        private string GetCanonicalProjectRoot()
        {
            return _routeStateStore.GetCanonicalProjectRoot();
        }

        /// <summary>
        /// Effective local endpoint for this editor process. Empty when the process-scoped
        /// configuration was rejected, which makes every launch/connect/stop path fail closed.
        /// </summary>
        private string GetEffectiveEndpoint()
        {
            McpRouteConfiguration route = McpRouteProvider.Configuration;
            return route.IsValid ? route.LocalHttpBaseUrl : string.Empty;
        }

        private static int GetPortFromEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return 0;
            }

            return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) && uri.Port > 0
                ? uri.Port
                : 0;
        }

        private McpRunStateRecord ReadOwnershipRecord()
        {
            McpOwnershipSnapshot snapshot = ReadOwnershipSnapshot();
            if (!snapshot.IsPresent && !string.IsNullOrEmpty(snapshot.Detail))
            {
                McpLog.Debug($"[MCP Route] {snapshot.Detail}");
            }

            return snapshot.IsPresent ? snapshot.Record : null;
        }

        /// <summary>Fresh ownership state, distinguishing absent from unreadable/malformed.</summary>
        private McpOwnershipSnapshot ReadOwnershipSnapshot() => _routeStateStore.ReadOwnershipState();

        /// <summary>The cross-process critical section every ownership mutation runs inside.</summary>
        private IMcpOwnershipLockProvider OwnershipLocks => _routeStateStore.OwnershipLocks;

        /// <summary>
        /// Gathers the live observations for one ownership decision. Anything that cannot
        /// be observed is left unavailable so the evaluator refuses instead of guessing.
        /// </summary>
        private McpOwnershipObservation BuildOwnershipObservation(
            McpRunStateRecord record,
            string endpoint)
        {
            return McpRouteObservationBuilder.Build(
                _routeStateStore,
                _processInspector,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = GetCanonicalProjectRoot(),
                    Endpoint = endpoint,
                    CurrentEditorPid = GetCurrentProcessIdSafe(),
                    RecordedServerPid = record != null && record.ServerPid > 0 ? record.ServerPid : 0,
                    RecordedEditorPid = record != null && record.EditorPid > 0 ? record.EditorPid : 0,
                });
        }

        /// <summary>
        /// Promotes a pending (<see cref="McpRunStateRecord.LifecycleStarting"/>) record to
        /// <see cref="McpRunStateRecord.LifecycleRunning"/> once the server's own PID
        /// evidence corroborates it. Only a record written by this same editor process for
        /// this same endpoint can be promoted; a foreign record is never touched.
        /// </summary>
        private bool TryPromotePendingRecord(
            McpRunStateRecord record,
            string endpoint,
            out McpRunStateRecord promoted)
        {
            promoted = null;

            if (record == null)
            {
                return false;
            }

            // A pending record does not know the server PID yet, so take it from the PID
            // evidence the launched process writes for itself.
            McpOwnershipObservation observation = BuildOwnershipObservation(record, endpoint);
            observation.ServerPid = observation.PidFilePid;

            if (!McpOwnershipEvaluator.TryIdentifyPendingServer(
                    record, observation, out McpRunStateRecord candidate, out _))
            {
                return false;
            }

            // Promotion is conditional: it may only replace the exact pending publication that was
            // identified. If a successor record appeared in the meantime it is left untouched.
            McpRunStateRecord promotedLocal = candidate;
            if (!McpOwnershipMutation.TryMutate(
                    OwnershipLocks,
                    current =>
                    {
                        if (!current.IsPresent)
                        {
                            return McpMutationDecision.Abort(
                                current.IsUnknown
                                    ? "the ownership record is unreadable; refusing to promote it."
                                    : "the pending ownership record is gone; refusing to promote it.");
                        }

                        if (!McpOwnershipIdentity.IsSamePublication(record, current.Record))
                        {
                            return McpMutationDecision.Abort(
                                "a different ownership record is now published; refusing to promote it.");
                        }

                        return McpMutationDecision.Write(promotedLocal);
                    },
                    out _))
            {
                return false;
            }

            promoted = promotedLocal;
            return true;
        }

        /// <summary>
        /// Terminates the server described by <paramref name="record"/> only when every piece
        /// of live evidence agrees. Returns false (leaving the process untouched) otherwise.
        ///
        /// The final decision is taken inside the cross-process ownership critical section and is
        /// conditioned on the ownership record still being the exact publication that was
        /// evaluated, the listener still being the validated server and the retained process handle
        /// still being that same lifetime. Deletion afterwards is conditional too, so a successor
        /// lifecycle's record is never removed.
        /// </summary>
        private bool TryStopOwnedServer(McpRunStateRecord record, string endpoint, bool quiet)
        {
            McpOwnershipObservation observation = BuildOwnershipObservation(record, endpoint);

            // A launch interrupted by a domain reload can still be reconciled, but only
            // through this same editor lifetime's own record and only with full evidence.
            if (record != null
                && string.Equals(record.LifecycleState, McpRunStateRecord.LifecycleStarting, StringComparison.Ordinal)
                && TryPromotePendingRecord(record, endpoint, out McpRunStateRecord promoted))
            {
                // The promotion itself was conditional; re-read so the evaluation below sees the
                // record that is actually published now.
                McpRunStateRecord published = ReadOwnershipRecord();
                if (published != null)
                {
                    record = published;
                    observation = BuildOwnershipObservation(record, endpoint);
                }
                else if (!quiet)
                {
                    McpLog.Warn(
                        "[MCP Route] Could not complete the ownership record; the launch stays pending.");
                }
            }

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(record, observation);
            if (!decision.Allowed)
            {
                if (!quiet)
                {
                    McpLog.Warn(
                        $"[MCP Route] Refusing to stop a local HTTP server for {endpoint}: "
                        + $"{decision.Reason} - {decision.Detail}. The process was left untouched.");
                }
                return false;
            }

            // The evaluation validated one specific process lifetime. Retain exactly that
            // lifetime and terminate through it: the PID alone is never used again, so a process
            // that exits here and whose PID is reused cannot be killed by mistake.
            if (!McpRunStateRecord.TryParseUtc(record.ServerStartUtc, out DateTime validatedStart))
            {
                if (!quiet)
                {
                    McpLog.Warn(
                        "[MCP Route] Refusing to stop a local HTTP server: the validated record has "
                        + "no usable server creation time.");
                }
                return false;
            }

            if (!_processInspector.TryOpenRetainedProcess(record.ServerPid, out IRetainedProcessHandle handle))
            {
                if (!quiet)
                {
                    McpLog.Warn(
                        $"[MCP Route] Refusing to stop server PID {record.ServerPid}: the validated "
                        + "process lifetime could not be retained, so it cannot be killed safely. "
                        + "The process was left untouched.");
                }
                return false;
            }

            McpTerminationOutcome outcome = McpOwnershipMutation.TerminateIfStillOwned(
                OwnershipLocks,
                // Re-observe the whole live ownership tuple inside the critical section: the
                // record, the PID evidence, the launch nonce and the listener are all refreshed
                // here so no observation that was made before the lock can authorize a kill.
                () => BuildOwnershipObservation(record, endpoint),
                record,
                validatedStart,
                handle,
                (retained, start) => _processTerminator.TerminateValidated(retained, start, out string terminateError)
                    ? null
                    : terminateError);

            if (!outcome.Terminated)
            {
                if (!quiet)
                {
                    McpLog.Warn($"[MCP Route] Refusing to stop server PID {record.ServerPid}: "
                                + $"{outcome.Reason} The process was left untouched.");
                }
                return false;
            }

            if (!outcome.RecordRemoved && !quiet)
            {
                McpLog.Warn(
                    "[MCP Route] The server was stopped but the ownership record was left in place: "
                    + outcome.Reason);
            }

            McpLog.Info($"Stopped local HTTP server on {endpoint} (PID: {record.ServerPid})");
            return true;
        }

        /// <summary>
        /// Clear the local uvx cache for the MCP server package
        /// </summary>
        /// <returns>True if successful, false otherwise</returns>
        public bool ClearUvxCache()
        {
            try
            {
                string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
                string uvCommand = BuildUvPathFromUvx(uvxPath);

                // Get the package name
                string packageName = "mcp-for-unity";

                // Run uvx cache clean command
                string args = $"cache clean {packageName}";

                bool success;
                string stdout;
                string stderr;

                success = ExecuteUvCommand(uvCommand, args, out stdout, out stderr);

                if (success)
                {
                    McpLog.Info($"uv cache cleared successfully: {stdout}");
                    return true;
                }
                string combinedOutput = string.Join(
                    Environment.NewLine,
                    new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

                string lockHint = (!string.IsNullOrEmpty(combinedOutput) &&
                                   combinedOutput.IndexOf("currently in-use", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "Another uv process may be holding the cache lock; wait a moment and try again or clear with '--force' from a terminal."
                    : string.Empty;

                if (string.IsNullOrEmpty(combinedOutput))
                {
                    combinedOutput = "Command failed with no output. Ensure uv is installed, on PATH, or set an override in Advanced Settings.";
                }

                McpLog.Error(
                    $"Failed to clear uv cache using '{uvCommand} {args}'. " +
                    $"Details: {combinedOutput}{(string.IsNullOrEmpty(lockHint) ? string.Empty : " Hint: " + lockHint)}");
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error clearing uv cache: {ex.Message}");
                return false;
            }
        }

        private bool ExecuteUvCommand(string uvCommand, string args, out string stdout, out string stderr)
        {
            stdout = null;
            stderr = null;

            string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
            string uvPath = BuildUvPathFromUvx(uvxPath);

            if (!string.Equals(uvCommand, uvPath, StringComparison.OrdinalIgnoreCase))
            {
                return ExecPath.TryRun(uvCommand, args, Application.dataPath, out stdout, out stderr, 30000);
            }

            string command = $"{uvPath} {args}";
            string extraPathPrepend = GetPlatformSpecificPathPrepend();

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return ExecPath.TryRun("cmd.exe", $"/c {command}", Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
            }

            string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

            if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
            {
                string escaped = command.Replace("\"", "\\\"");
                return ExecPath.TryRun(shell, $"-lc \"{escaped}\"", Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
            }

            return ExecPath.TryRun(uvPath, args, Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
        }

        private string BuildUvPathFromUvx(string uvxPath)
        {
            return _commandBuilder.BuildUvPathFromUvx(uvxPath);
        }

        private string GetPlatformSpecificPathPrepend()
        {
            return _commandBuilder.GetPlatformSpecificPathPrepend();
        }

        /// <summary>
        /// Start the local HTTP server headless (no terminal window), redirecting its
        /// stdout/stderr to Library/MCPForUnity/Logs/server-launch-{port}.log.
        ///
        /// A launch requires exclusive ownership of the endpoint. An existing server is
        /// stopped first only when this editor lifetime provably owns it; a live server owned
        /// by another editor lifetime aborts the launch and is left untouched.
        /// </summary>
        public bool StartLocalHttpServer(bool quiet = false)
        {
            /// Clean stale Python build artifacts when using a local dev server path
            AssetPathUtility.CleanLocalServerBuildArtifacts();

            McpRouteConfiguration route = McpRouteProvider.Configuration;
            if (!route.IsValid)
            {
                ReportStartFailure(
                    quiet,
                    "Cannot Start HTTP Server",
                    "The process-scoped MCP configuration supplied to this editor is invalid, so no "
                    + "server will be started for this editor process:\n\n" + route.ValidationError);
                return false;
            }

            if (!TryGetLocalHttpServerCommandParts(out _, out _, out var displayCommand, out var error))
            {
                ReportStartFailure(
                    quiet,
                    "Cannot Start HTTP Server",
                    error ?? "The server command could not be constructed with the current settings.");
                return false;
            }

            string endpoint = GetEffectiveEndpoint();
            int port = GetPortFromEndpoint(endpoint);
            if (port <= 0)
            {
                ReportStartFailure(
                    quiet,
                    "Cannot Start HTTP Server",
                    "The configured local HTTP URL is not a usable loopback endpoint. "
                    + "Set a URL such as http://127.0.0.1:8080.");
                return false;
            }

            string pidFilePath = GetLocalHttpServerPidFilePath(port);
            if (string.IsNullOrEmpty(pidFilePath))
            {
                ReportStartFailure(
                    quiet,
                    "Cannot Start HTTP Server",
                    "The project-local RunState directory could not be resolved, so server "
                    + "ownership could not be recorded. Refusing to launch an unrecorded server.");
                return false;
            }

            // ---- ownership gate: never adopt or overwrite a foreign live server ----
            McpOwnershipSnapshot existingState = ReadOwnershipSnapshot();
            if (existingState.IsUnknown)
            {
                // An unreadable ownership record is UNKNOWN, not stale: overwriting it could destroy
                // another editor's live ownership evidence.
                McpLog.Error($"[MCP Route] {existingState.Detail}");
                ReportStartFailure(
                    quiet,
                    "Ownership Unknown",
                    existingState.Detail + "\n\nNo server was started. Resolve the existing "
                    + "ownership record before launching a managed server.");
                return false;
            }

            McpRunStateRecord existing = existingState.IsPresent ? existingState.Record : null;
            McpOwnershipObservation existingObservation = BuildOwnershipObservation(existing, endpoint);
            McpAdoptionOutcome adoption = McpOwnershipEvaluator.EvaluateAdoption(
                existing, existingObservation, out string adoptionDetail);

            if (adoption == McpAdoptionOutcome.LiveForeign || adoption == McpAdoptionOutcome.Unknown)
            {
                McpLog.Error($"[MCP Route] {adoptionDetail}");
                ReportStartFailure(
                    quiet,
                    adoption == McpAdoptionOutcome.Unknown ? "Ownership Unknown" : "Server Already Owned",
                    adoptionDetail + "\n\nNo server was started and the existing process was left untouched.");
                return false;
            }

            // ---- the endpoint must be free, or hold a server this lifetime owns ----
            List<int> listeners = GetListeningProcessIdsForPort(port);
            if (listeners.Count > 0)
            {
                bool stoppedOwn = adoption == McpAdoptionOutcome.OwnedByCurrentLifetime
                                  && TryStopOwnedServer(existing, endpoint, quiet: true);
                if (stoppedOwn)
                {
                    listeners = GetListeningProcessIdsForPort(port);
                }

                if (listeners.Count > 0)
                {
                    ReportStartFailure(
                        quiet,
                        "Port In Use",
                        $"Cannot start the local HTTP server because {endpoint} is already in use by "
                        + $"PID(s): {string.Join(", ", listeners)}\n\n"
                        + $"{ProductInfo.ProductName} will not terminate a process it does not own. "
                        + "Stop the owning process manually or change the HTTP URL.");
                    return false;
                }
            }

            // First-time-only confirmation. Subsequent launches (and the quiet auto-start path) skip the dialog.
            if (!quiet && !EditorPrefs.GetBool(EditorPrefKeys.HttpServerLaunchConfirmed, false))
            {
                if (!EditorUtility.DisplayDialog(
                    "Start Local HTTP Server",
                    "Start the local MCP server in the background?\n\n" +
                    "It launches headless (no terminal window) and logs progress to the Unity Console. " +
                    "This confirmation is shown only once.",
                    "Start",
                    "Cancel"))
                {
                    return false;
                }
                try { EditorPrefs.SetBool(EditorPrefKeys.HttpServerLaunchConfirmed, true); } catch { }
            }

            string launchLog = GetLocalHttpServerLaunchLogPath(port);
            string instanceToken = Guid.NewGuid().ToString("N");
            if (!McpManagedServerArguments.TryAppendLaunchIdentity(
                    displayCommand,
                    pidFilePath,
                    instanceToken,
                    out string launchCommand,
                    out string launchCommandError))
            {
                ReportStartFailure(
                    quiet,
                    "Cannot Start HTTP Server",
                    launchCommandError ?? "The managed launch command could not be constructed.");
                return false;
            }

            try
            {
                _lastLaunchedProcess = null;

                // Best-effort: delete stale pidfile if it exists.
                try
                {
                    if (!string.IsNullOrEmpty(pidFilePath) && File.Exists(pidFilePath))
                    {
                        DeletePidFile(pidFilePath);
                    }
                }
                catch { }

                // Record the launch (with its nonce) BEFORE spawning, so the nonce survives a
                // domain reload during startup and an interrupted launch can still be reconciled.
                var pendingRecord = new McpRunStateRecord
                {
                    SchemaVersion = McpRunStateRecord.CurrentSchemaVersion,
                    RecordId = McpRunStateRecord.NewRecordId(),
                    CanonicalProjectRoot = GetCanonicalProjectRoot(),
                    Endpoint = endpoint,
                    EditorPid = GetCurrentProcessIdSafe(),
                    EditorStartUtc = _processInspector.TryGetCurrentProcessStartTimeUtc(out DateTime editorStart)
                        ? McpRunStateRecord.FormatUtc(editorStart)
                        : null,
                    ServerPid = 0,
                    ServerStartUtc = null,
                    InstanceToken = instanceToken,
                    PidFilePath = pidFilePath,
                    LifecycleState = McpRunStateRecord.LifecycleStarting,
                    WrittenUtc = McpRunStateRecord.FormatUtc(DateTime.UtcNow),
                };

                // Publication is a single conditional mutation: the adoption decision is re-taken
                // against the CURRENT record while the cross-process lock is held, so a foreign or
                // unreadable record that appeared since the gate above can never be overwritten.
                if (!McpOwnershipMutation.TryMutate(
                        OwnershipLocks,
                        current =>
                        {
                            if (current.IsUnknown)
                            {
                                return McpMutationDecision.Abort(
                                    "the existing ownership record is unreadable; refusing to "
                                    + "overwrite it.");
                            }

                            McpAdoptionOutcome rechecked = McpOwnershipEvaluator.EvaluateAdoption(
                                current.Record,
                                BuildOwnershipObservation(current.Record, endpoint),
                                out string detail);

                            if (rechecked == McpAdoptionOutcome.LiveForeign
                                || rechecked == McpAdoptionOutcome.Unknown)
                            {
                                return McpMutationDecision.Abort(detail);
                            }

                            return McpMutationDecision.Write(pendingRecord);
                        },
                        out string recordError))
                {
                    McpLog.Error($"[MCP Route] {recordError}");
                    ReportStartFailure(
                        quiet,
                        "Cannot Start HTTP Server",
                        "The project-local ownership record could not be written, so the launched server "
                        + "could never be stopped safely. Refusing to launch.\n\n" + recordError);
                    return false;
                }

                // Truncate the launch log so the tail always reflects the current launch.
                if (!string.IsNullOrEmpty(launchLog))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(launchLog));
                        File.WriteAllText(launchLog, string.Empty);
                    }
                    catch { }
                }

                McpLog.Info("Starting local HTTP server… (first run may take a minute while dependencies install)");

                // Launch the server headless (no terminal window); stdout+stderr go to the launch log.
                string effectiveLog = launchLog ?? Path.Combine(Path.GetTempPath(), "mcp-for-unity-server-launch.log");
                var startInfo = CreateHeadlessProcessStartInfo(launchCommand, effectiveLog);

                // The approved endpoint is supplied on the command line. An inherited
                // server-side routing variable would let the child pick a different endpoint, so
                // it is removed from the environment before the process is created.
                IReadOnlyList<string> strippedRouting =
                    McpServerEnvironmentSanitizer.RemoveRoutingVariables(startInfo.EnvironmentVariables);
                if (strippedRouting.Count > 0)
                {
                    McpLog.Warn(
                        "[MCP Route] Ignored inherited server routing environment variable(s) for "
                        + $"this managed launch: {string.Join(", ", strippedRouting)}.");
                }

                // The headless shell is not a login shell, so it does not inherit the user's
                // profile PATH (on macOS, GUI-launched Unity has a minimal PATH). Prepend the
                // platform uv/uvx locations so a bare `uvx`/`uv` resolves the same way the old
                // terminal (login shell) launch did.
                string extraPathPrepend = GetPlatformSpecificPathPrepend();
                if (!string.IsNullOrEmpty(extraPathPrepend))
                {
                    string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    startInfo.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(currentPath)
                        ? extraPathPrepend
                        : (extraPathPrepend + Path.PathSeparator + currentPath);
                }

                _lastLaunchedProcess = System.Diagnostics.Process.Start(startInfo);
                ScheduleLaunchFinalization(pendingRecord, endpoint);
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to start server: {ex.Message}");
                if (!quiet)
                {
                    EditorUtility.DisplayDialog(
                        "Error",
                        $"Failed to start server: {ex.Message}",
                        "OK");
                }
                return false;
            }
        }

        /// <summary>
        /// Stop the local HTTP server for the configured endpoint.
        ///
        /// Only a server this editor lifetime provably owns is terminated. Missing, stale,
        /// ambiguous or foreign evidence leaves the process untouched.
        /// </summary>
        public bool StopLocalHttpServer()
        {
            return StopLocalHttpServerInternal(quiet: false);
        }

        public bool StopManagedLocalHttpServer()
        {
            // A launch finalization may still be in flight (the server writes its PID file
            // shortly after spawn). Give it a bounded moment so a quit right after a launch
            // can still clean up; we never widen the ownership checks to compensate.
            WaitForPendingLaunchFinalization();

            McpRouteConfiguration route = McpRouteProvider.Configuration;
            McpRunStateRecord record = ReadOwnershipRecord();
            if (record == null)
            {
                return false;
            }

            string endpoint = GetEffectiveEndpoint();
            if (string.IsNullOrEmpty(endpoint) && !route.IsValid)
            {
                // Invalid process-scoped configuration: fail closed, touch nothing.
                McpLog.Warn(
                    "[MCP Route] Refusing to stop a local HTTP server because this editor's "
                    + $"process-scoped MCP configuration was rejected: {route.ValidationError}");
                return false;
            }

            if (string.IsNullOrEmpty(endpoint))
            {
                endpoint = record.Endpoint;
            }

            return TryStopOwnedServer(record, endpoint, quiet: true);
        }

        public bool IsLocalHttpServerRunning()
        {
            try
            {
                McpRouteConfiguration route = McpRouteProvider.Configuration;
                if (!route.IsValid)
                {
                    return false;
                }

                string endpoint = GetEffectiveEndpoint();
                McpRunStateRecord record = ReadOwnershipRecord();
                if (record != null
                    && string.Equals(record.LifecycleState, McpRunStateRecord.LifecycleStarting, StringComparison.Ordinal)
                    && TryPromotePendingRecord(record, endpoint, out _))
                {
                    // The promotion (if any) was applied conditionally; observe what is published now.
                    record = ReadOwnershipRecord() ?? record;
                }

                // "Running" now means one thing only: this editor lifetime provably owns the
                // live listener. Anything else (including a foreign listener on the port) is
                // reported as not-running rather than guessed at.
                return McpOwnershipEvaluator.EvaluateStop(
                    record,
                    BuildOwnershipObservation(record, endpoint)).Allowed;
            }
            catch
            {
                return false;
            }
        }

        public bool IsLocalHttpServerReachable()
        {
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (!IsLocalUrl(httpUrl))
                {
                    return false;
                }

                if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
                {
                    return false;
                }

                // 250ms, not 50ms: on a machine busy with test runs or domain reloads a 50ms
                // connect wait produces false "server gone" readings that tore down healthy
                // sessions via the orphaned-session detector (#1207).
                return TryConnectToLocalPort(uri.Host, uri.Port, timeoutMs: 250);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConnectToLocalPort(string host, int port, int timeoutMs)
        {
            try
            {
                // timeoutMs is an overall budget shared across candidate hosts, so a
                // filtered/dropped first candidate cannot multiply the worst-case wait
                // (this can run on the editor UI tick).
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                foreach (string target in BuildLocalProbeHosts(host))
                {
                    int remainingMs = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        break;
                    }

                    try
                    {
                        using (var client = new TcpClient())
                        {
                            var connectTask = client.ConnectAsync(target, port);
                            if (connectTask.Wait(remainingMs) && client.Connected)
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore per-host failures.
                    }
                }
            }
            catch
            {
                // Ignore probe failures and treat as unreachable.
            }

            return false;
        }

        private static IReadOnlyList<string> BuildLocalProbeHosts(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "127.0.0.1";
            }
            else
            {
                host = host.Trim();
            }

            var hosts = new List<string>();
            AddHostCandidate(hosts, host);

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Probe both loopback families for localhost to avoid false negatives on systems where
                // localhost resolution prefers an address family different from the server bind.
                AddHostCandidate(hosts, "127.0.0.1");
                AddHostCandidate(hosts, "::1");
            }
            else if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                AddHostCandidate(hosts, "127.0.0.1");
            }
            else if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(host, "0:0:0:0:0:0:0:0", StringComparison.OrdinalIgnoreCase))
            {
                AddHostCandidate(hosts, "::1");
            }

            return hosts;
        }

        private static void AddHostCandidate(List<string> hosts, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (hosts.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            hosts.Add(candidate);
        }

        /// <summary>
        /// Stop the local HTTP server for the effective endpoint.
        ///
        /// This is the single termination path in the service and it contains no heuristics:
        /// the project-local ownership record must match this editor lifetime, this endpoint,
        /// the live server PID, its creation time, its launch nonce, its PID evidence file and
        /// the current listener on the port. Anything less leaves the process running.
        /// </summary>
        private bool StopLocalHttpServerInternal(bool quiet, int? portOverride = null, bool allowNonLocalUrl = false)
        {
            try
            {
                McpRouteConfiguration route = McpRouteProvider.Configuration;
                if (!route.IsValid)
                {
                    if (!quiet)
                    {
                        McpLog.Warn(
                            "[MCP Route] Refusing to stop a local HTTP server because this editor's "
                            + $"process-scoped MCP configuration was rejected: {route.ValidationError}");
                    }
                    return false;
                }

                McpRunStateRecord record = ReadOwnershipRecord();
                if (record == null)
                {
                    if (!quiet)
                    {
                        McpLog.Info("No MCP server ownership record for this project; nothing to stop.");
                    }
                    return false;
                }

                // Ownership is bound to the recorded endpoint, never to a bare port: a
                // caller-supplied port cannot be used to reach a neighbour's server.
                string endpoint = GetEffectiveEndpoint();
                if (string.IsNullOrEmpty(endpoint))
                {
                    endpoint = record.Endpoint;
                }

                if (!allowNonLocalUrl && !IsLocalUrl(endpoint))
                {
                    if (!quiet)
                    {
                        McpLog.Warn("Cannot stop server: URL is not local.");
                    }
                    return false;
                }

                WaitForPendingLaunchFinalization();
                return TryStopOwnedServer(record, endpoint, quiet);
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    McpLog.Error($"Failed to stop server: {ex.Message}");
                }
                return false;
            }
        }

        private void DeletePidFile(string pidFilePath)
        {
            _pidFileManager.DeletePidFile(pidFilePath);
        }

        private List<int> GetListeningProcessIdsForPort(int port)
        {
            return _processDetector.GetListeningProcessIdsForPort(port);
        }

        private int GetCurrentProcessIdSafe()
        {
            return _processDetector.GetCurrentProcessId();
        }

        /// <summary>
        /// Reports a refusal to start without assuming a UI is available (batch mode and the
        /// quiet auto-start path must never surface a modal dialog).
        /// </summary>
        private static void ReportStartFailure(bool quiet, string title, string message)
        {
            McpLog.Warn($"[MCP Route] {title}: {message}");
            if (quiet)
            {
                return;
            }

            try
            {
                EditorUtility.DisplayDialog(title, message, "OK");
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[MCP Route] Could not show the '{title}' dialog: {ex.Message}");
            }
        }

        /// <summary>
        /// Watches for the launched server's own PID evidence and completes the ownership
        /// record. Bounded, best effort and never fatal: an unfinished record stays in the
        /// pending state, which simply means nothing will be terminated for it.
        /// </summary>
        private void ScheduleLaunchFinalization(McpRunStateRecord pendingRecord, string endpoint)
        {
            _launchFinalizationTask = Task.Run(async () =>
            {
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (TryPromotePendingRecord(pendingRecord, endpoint, out _))
                        {
                            return;
                        }

                        await Task.Delay(250).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Never surface launch-finalization failures: the record simply stays pending.
                }
            });
        }

        /// <summary>
        /// Gives an in-flight launch finalization a bounded moment to finish so a stop issued
        /// immediately after a launch can still see coherent evidence.
        /// </summary>
        private void WaitForPendingLaunchFinalization()
        {
            Task pending = _launchFinalizationTask;
            if (pending == null)
            {
                return;
            }

            try
            {
                pending.Wait(TimeSpan.FromMilliseconds(1500));
            }
            catch
            {
                // Ignore: the ownership checks decide, not this wait.
            }
        }

        /// <summary>
        /// Attempts to build the command used for starting the local HTTP server
        /// </summary>
        public bool TryGetLocalHttpServerCommand(out string command, out string error)
        {
            command = null;
            error = null;
            if (!TryGetLocalHttpServerCommandParts(out var fileName, out var args, out var displayCommand, out error))
            {
                return false;
            }

            // Maintain existing behavior: return a single command string suitable for display/copy.
            command = displayCommand;
            return true;
        }

        private bool TryGetLocalHttpServerCommandParts(out string fileName, out string arguments, out string displayCommand, out string error)
        {
            return _commandBuilder.TryBuildCommand(out fileName, out arguments, out displayCommand, out error);
        }

        /// <summary>
        /// Check if the configured HTTP URL is a local address
        /// </summary>
        public bool IsLocalUrl()
        {
            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return IsLocalUrl(httpUrl);
        }

        /// <summary>
        /// Check if a URL is local or bind-all (localhost/loopback and 0.0.0.0/::).
        /// This helper is intentionally broader than local-launch policy checks.
        /// </summary>
        private static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            try
            {
                var uri = new Uri(url);
                string host = uri.Host;
                return HttpEndpointUtility.IsLoopbackHost(host) || HttpEndpointUtility.IsBindAllInterfacesHost(host);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if the local HTTP server can be started
        /// </summary>
        public bool CanStartLocalServer()
        {
            bool useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
            if (!useHttpTransport)
            {
                return false;
            }

            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(httpUrl, out _);
        }

        private System.Diagnostics.ProcessStartInfo CreateTerminalProcessStartInfo(string command)
        {
            return _terminalLauncher.CreateTerminalProcessStartInfo(command);
        }

        private System.Diagnostics.ProcessStartInfo CreateHeadlessProcessStartInfo(string command, string logFilePath)
        {
            return _terminalLauncher.CreateHeadlessProcessStartInfo(command, logFilePath);
        }

        public string GetLocalHttpServerLaunchLogPath()
        {
            string baseUrl = HttpEndpointUtility.GetLocalBaseUrl();
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return GetLocalHttpServerLaunchLogPath(uri.Port);
            }
            return null;
        }

        private string GetLocalHttpServerLaunchLogPath(int port)
        {
            string dir = Path.Combine(_terminalLauncher.GetProjectRootPath(), "Library", "MCPForUnity", "Logs");
            return Path.Combine(dir, $"server-launch-{port}.log");
        }

        public bool HasManagedServerLaunchHandle => _lastLaunchedProcess != null;

        public bool IsManagedServerLaunchProcessAlive()
        {
            try
            {
                var proc = _lastLaunchedProcess;
                return proc != null && !proc.HasExited;
            }
            catch
            {
                // If we cannot query the process (e.g. it was disposed), assume it is no longer alive
                // so callers stop waiting on a dead handle.
                return false;
            }
        }

        public void LogLocalHttpServerLaunchFailure()
        {
            string logPath = GetLocalHttpServerLaunchLogPath();
            string tail = TailFile(logPath, 40);

            string copyHint;
            if (TryGetLocalHttpServerCommand(out var command, out _) && !string.IsNullOrEmpty(command))
            {
                copyHint = $"To run it yourself, copy this command into a terminal:\n{command}";
            }
            else
            {
                copyHint = "Use the \"Manual Server Launch\" foldout to copy the command and run it yourself.";
            }

            string logRef = string.IsNullOrEmpty(logPath) ? "(launch log unavailable)" : logPath;
            string body = string.IsNullOrEmpty(tail) ? "(no output captured)" : tail;

            McpLog.Error(
                "Local HTTP server did not become reachable. " +
                $"Launch log: {logRef}\n{body}\n{copyHint}");
        }

        private static string TailFile(string path, int maxLines)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return string.Empty;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length <= maxLines)
                {
                    return string.Join(Environment.NewLine, lines).Trim();
                }

                return string.Join(Environment.NewLine, lines.Skip(lines.Length - maxLines)).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
