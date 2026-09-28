using System;
using System.IO;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Resolves and holds the process-scoped MCP route for this Unity Editor.
    ///
    /// Resolution happens at most once per domain lifetime. Static fields do NOT survive a Unity
    /// domain reload, so the stable part is the process ENVIRONMENT itself: the same unchanged
    /// <c>UNITY_MCP_*</c> variables deterministically re-resolve to the same managed route after
    /// a reload, independent of any later EditorPrefs edit. Where state must outlive a reload
    /// (the launch nonce), the project-local ownership record is the carrier - and it is only
    /// honoured while it still names this exact editor process lifetime and route.
    /// </summary>
    public static class McpRouteProvider
    {
        private static readonly McpRouteConfigurationCache Cache =
            new McpRouteConfigurationCache(BuildInputs);
        private static McpRouteStateStore _defaultStateStore;
        private static McpRouteConfiguration _loggedResolution;

        /// <summary>The resolved configuration for this editor process.</summary>
        public static McpRouteConfiguration Configuration
        {
            get
            {
                McpRouteConfiguration configuration = Cache.Get();
                if (!ReferenceEquals(_loggedResolution, configuration))
                {
                    LogResolved(configuration);
                    _loggedResolution = configuration;
                }

                return configuration;
            }
        }

        /// <summary>
        /// Reads the process environment together with the current EditorPrefs values and
        /// resolves the single effective configuration.
        /// </summary>
        public static McpRouteConfiguration Resolve()
        {
            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(BuildInputs());
            LogResolved(configuration);
            return configuration;
        }

        /// <summary>
        /// Snapshots the raw inputs for one resolution. Called at most once per editor
        /// process by <see cref="McpRouteConfigurationCache"/>.
        /// </summary>
        private static McpRouteInputs BuildInputs()
        {
            return new McpRouteInputs
            {
                TransportEnvironmentValue = Environment.GetEnvironmentVariable(
                    McpRouteConfiguration.TransportEnvironmentVariable),
                HttpUrlEnvironmentValue = Environment.GetEnvironmentVariable(
                    McpRouteConfiguration.HttpUrlEnvironmentVariable),
                AutoStartEnvironmentValue = Environment.GetEnvironmentVariable(
                    McpRouteConfiguration.AutoStartEnvironmentVariable),

                // Read EditorPrefs directly (not via EditorConfigurationCache / HttpEndpointUtility
                // accessors) because those accessors consult this resolver.
                StoredUseHttpTransport = EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true),
                StoredLocalHttpBaseUrl = HttpEndpointUtility.GetStoredLocalBaseUrl(),
                StoredAutoStartOnLoad = EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, false),
                StoredAllowLanBind = EditorPrefs.GetBool(EditorPrefKeys.AllowLanHttpBind, false),
            };
        }

        private static void LogResolved(McpRouteConfiguration configuration)
        {
            if (!configuration.IsValid)
            {
                McpLog.Error(
                    "[MCP Route] Rejecting the process-scoped MCP configuration; MCP will not "
                    + $"start, connect or stop anything for this editor: {configuration.ValidationError}");
            }
            else if (configuration.HasAnyOverride)
            {
                McpLog.Info(
                    $"[MCP Route] Process-scoped route: transport={configuration.Transport}, "
                    + $"endpoint='{configuration.LocalHttpBaseUrl}', autoStart={configuration.AutoStart}, "
                    + $"managed={configuration.IsManagedRoute}");
            }
        }

        /// <summary>Canonical project root of the running editor.</summary>
        public static string GetCanonicalProjectRoot()
            => McpRouteStateStore.ResolveProjectRoot();

        private static McpRouteStateStore GetOrCreateDefaultStore()
            => _defaultStateStore ??= new McpRouteStateStore();

        private static McpProcessInspector _processInspector;

        private static McpProcessInspector GetOrCreateProcessInspector()
            => _processInspector ??= new McpProcessInspector();

        /// <summary>
        /// The endpoint this editor uses, or an empty string when the configuration is
        /// invalid (so launch, connect and stop paths all fail closed).
        /// </summary>
        public static string GetEndpoint()
        {
            McpRouteConfiguration configuration = Configuration;
            return configuration.IsValid ? configuration.LocalHttpBaseUrl : string.Empty;
        }

        /// <summary>Test seam: replaces the cached configuration.</summary>
        internal static void SetConfigurationForTests(McpRouteConfiguration configuration)
        {
            Cache.SetForTests(configuration);
        }

        /// <summary>Test seam: drops the cached configuration.</summary>
        internal static void ResetForTests()
        {
            Cache.ResetForTests();
        }

        /// <summary>
        /// Returns the launch nonce recorded for this editor process's route.
        ///
        /// The nonce is echoed to the server on registration so a guarded dedicated server
        /// can prove that the Unity instance registering with it is the instance the launch
        /// was made for. It is correlation data, never a credential.
        /// </summary>
        public static bool TryGetActiveLaunchToken(out string instanceToken)
        {
            return TryGetActiveLaunchToken(out instanceToken, out _, out _);
        }

        /// <summary>
        /// Returns the launch nonce only when the whole ownership tuple is coherent for the
        /// CURRENT editor lifetime and the live server.
        ///
        /// A reachable endpoint is not proof of ownership, so the nonce is never released on the
        /// strength of a copied, stale or foreign record: schema, project root, endpoint, editor
        /// PID and creation instant, lifecycle, nonce shape, RunState path and the live server
        /// identity must all agree. Anything else fails closed.
        /// </summary>
        public static bool TryGetActiveLaunchToken(
            out string instanceToken,
            out McpOwnershipDenyReason reason,
            out string detail)
        {
            instanceToken = null;
            reason = McpOwnershipDenyReason.None;
            detail = null;

            try
            {
                McpRouteConfiguration route = Configuration;
                if (!route.IsValid)
                {
                    reason = McpOwnershipDenyReason.MalformedRecord;
                    detail = "the process-scoped MCP configuration for this editor was rejected.";
                    McpLog.Debug($"[MCP Route] Not echoing a launch nonce: {detail}");
                    return false;
                }

                McpRouteStateStore store = GetOrCreateDefaultStore();
                if (!store.TryRead(out McpRunStateRecord record, out string readError) || record == null)
                {
                    reason = McpOwnershipDenyReason.NoRecord;
                    detail = readError;
                    McpLog.Debug($"[MCP Route] Not echoing a launch nonce: {detail}");
                    return false;
                }

                McpProcessInspector inspector = GetOrCreateProcessInspector();
                McpOwnershipObservation observation = McpRouteObservationBuilder.Build(
                    store,
                    inspector,
                    new McpObservationRequest
                    {
                        CanonicalProjectRoot = GetCanonicalProjectRoot(),
                        Endpoint = route.LocalHttpBaseUrl,
                        CurrentEditorPid = inspector.GetCurrentProcessId(),
                        RecordedServerPid = record.ServerPid,
                        RecordedEditorPid = record.EditorPid,
                    });

                if (!McpManagedConnectionGate.TryGetLaunchToken(
                        record,
                        observation,
                        out instanceToken,
                        out reason,
                        out detail))
                {
                    McpLog.Debug(
                        $"[MCP Route] Not echoing a launch nonce ({reason}): {detail}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                instanceToken = null;
                reason = McpOwnershipDenyReason.MalformedRecord;
                detail = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// True when the given URL is the endpoint of this editor process. Used by the log
        /// and UI layers to avoid acting on a stale value.
        /// </summary>
        public static bool MatchesProcessEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            string endpoint = GetEndpoint();
            return endpoint.Length > 0
                   && string.Equals(
                       endpoint.TrimEnd('/'),
                       url.Trim().TrimEnd('/'),
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the editor process is running with an invalid process-scoped
        /// configuration. Surfaced so the UI can explain why MCP is inert.
        /// </summary>
        public static bool HasInvalidProcessConfiguration =>
            !Configuration.IsValid && Configuration.HasAnyOverride;

        /// <summary>Renders the resolved configuration for diagnostics.</summary>
        public static string Describe()
        {
            McpRouteConfiguration configuration = Configuration;
            if (!configuration.IsValid)
            {
                return $"invalid ({configuration.ValidationError})";
            }

            return $"transport={configuration.Transport} endpoint='{configuration.LocalHttpBaseUrl}' "
                   + $"autoStart={configuration.AutoStart} managed={configuration.IsManagedRoute}";
        }

        internal static bool TryGetApplicationDataPath(out string dataPath)
        {
            dataPath = null;
            try
            {
                dataPath = Application.dataPath;
                return !string.IsNullOrEmpty(dataPath);
            }
            catch
            {
                return false;
            }
        }

        internal static string CombineProjectRoot(string dataPath)
            => McpRunStatePaths.Canonicalize(Path.Combine(dataPath, ".."));
    }
}
