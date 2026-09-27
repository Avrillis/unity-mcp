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
    /// Resolution happens exactly once per editor process (the cached value lives in a
    /// static, and statics are preserved across domain reloads in the same process). That is
    /// what makes the route stable: a later domain reload or an EditorPrefs/UI change cannot
    /// redirect an editor whose route was supplied through the process environment.
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
            instanceToken = null;

            try
            {
                if (!GetOrCreateDefaultStore().TryRead(out McpRunStateRecord record, out _)
                    || record == null
                    || string.IsNullOrWhiteSpace(record.InstanceToken))
                {
                    return false;
                }

                if (!McpRunStatePaths.PathsEqual(record.CanonicalProjectRoot, GetCanonicalProjectRoot()))
                {
                    return false;
                }

                if (record.EditorPid > 0 && record.EditorPid != new McpProcessInspector().GetCurrentProcessId())
                {
                    // A record left behind by a previous editor process is not ours to echo.
                    return false;
                }

                instanceToken = record.InstanceToken;
                return true;
            }
            catch
            {
                instanceToken = null;
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
