using System;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Resolves the process-scoped MCP configuration once per domain lifetime and reuses it for
    /// the rest of that lifetime.
    ///
    /// The resolved value is deliberately not invalidatable: within a domain, a later
    /// EditorPrefs/UI change must not be able to redirect an editor whose route was supplied
    /// through its process environment.
    ///
    /// This cache does NOT survive a domain reload (statics don't). After a reload the same
    /// unchanged process environment re-resolves to the same route deterministically, which is
    /// the actual guarantee - not static persistence.
    /// </summary>
    public sealed class McpRouteConfigurationCache
    {
        private readonly Func<McpRouteInputs> _inputsFactory;
        private readonly object _gate = new object();
        private McpRouteConfiguration _resolved;

        public McpRouteConfigurationCache(Func<McpRouteInputs> inputsFactory)
        {
            _inputsFactory = inputsFactory ?? throw new ArgumentNullException(nameof(inputsFactory));
        }

        /// <summary>The resolved configuration, resolving it on first use.</summary>
        public McpRouteConfiguration Get()
        {
            McpRouteConfiguration resolved = _resolved;
            if (resolved != null)
            {
                return resolved;
            }

            lock (_gate)
            {
                _resolved ??= McpRouteConfiguration.Resolve(_inputsFactory());
                return _resolved;
            }
        }

        /// <summary>Test seam: installs a pre-built configuration.</summary>
        public void SetForTests(McpRouteConfiguration configuration)
        {
            lock (_gate)
            {
                _resolved = configuration;
            }
        }

        /// <summary>Test seam: drops the cached configuration.</summary>
        public void ResetForTests()
        {
            lock (_gate)
            {
                _resolved = null;
            }
        }
    }
}
