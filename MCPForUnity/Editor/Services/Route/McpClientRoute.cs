namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// The single place every generated client configuration takes its transport and endpoint
    /// from.
    ///
    /// Client writers used to read EditorPrefs directly (transport flag, URL, scope). For a
    /// process-scoped managed route that is wrong: a later global EditorPrefs change would
    /// rewrite a client to point at a different worker's endpoint. Routing every reader through
    /// this helper keeps the resolved process route authoritative.
    /// </summary>
    public static class McpClientRoute
    {
        /// <summary>
        /// Transport a generated client config should use. A process-scoped transport override
        /// wins; an invalid managed configuration fails closed to stdio rather than guessing.
        /// </summary>
        public static bool UseHttp(McpRouteConfiguration route, bool storedUseHttp)
        {
            if (route == null || !route.IsValid)
            {
                return false;
            }

            return route.HasTransportOverride
                ? route.Transport == McpTransportSelection.Http
                : storedUseHttp;
        }

        /// <summary>
        /// Base URL a generated client config should use. A process-scoped URL (or a forced
        /// local scope) pins the resolved local endpoint; otherwise the stored scope is honoured.
        /// </summary>
        public static string ResolveBaseUrl(
            McpRouteConfiguration route,
            string storedLocalBaseUrl,
            string storedRemoteBaseUrl,
            bool storedRemoteScope)
        {
            if (route == null || !route.IsValid)
            {
                // Fail closed: an invalid managed configuration must not produce a URL at all.
                return string.Empty;
            }

            if (route.HasHttpUrlOverride || route.ForcesLocalScope)
            {
                return route.LocalHttpBaseUrl ?? string.Empty;
            }

            return storedRemoteScope
                ? (storedRemoteBaseUrl ?? string.Empty)
                : (storedLocalBaseUrl ?? string.Empty);
        }
    }
}
