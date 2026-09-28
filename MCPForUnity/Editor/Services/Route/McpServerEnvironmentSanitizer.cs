using System.Collections.Generic;
using System.Collections.Specialized;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Removes inherited server-side routing environment variables before a managed server is
    /// launched.
    ///
    /// The Python server reads <c>UNITY_MCP_HTTP_URL/HOST/PORT</c> from its environment and lets
    /// them override its command line. A supervisor-managed launch passes the approved endpoint
    /// on the command line, so an inherited value must never be able to redirect the server away
    /// from the endpoint this editor owns.
    /// </summary>
    public static class McpServerEnvironmentSanitizer
    {
        /// <summary>Server-side variables that can redirect a launched managed server.</summary>
        public static readonly string[] ManagedServerRoutingVariables =
        {
            "UNITY_MCP_HTTP_URL",
            "UNITY_MCP_HTTP_HOST",
            "UNITY_MCP_HTTP_PORT",
            "UNITY_MCP_HTTP_REMOTE_HOSTED",
        };

        /// <summary>Removes the routing variables and returns the names that were present.</summary>
        public static IReadOnlyList<string> RemoveRoutingVariables(IDictionary<string, string> environment)
        {
            var removed = new List<string>();
            if (environment == null)
            {
                return removed;
            }

            foreach (string name in ManagedServerRoutingVariables)
            {
                if (environment.ContainsKey(name))
                {
                    environment.Remove(name);
                    removed.Add(name);
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes the routing variables from a process environment block
        /// (<see cref="System.Diagnostics.ProcessStartInfo.EnvironmentVariables"/>).
        /// </summary>
        public static IReadOnlyList<string> RemoveRoutingVariables(StringDictionary environment)
        {
            var removed = new List<string>();
            if (environment == null)
            {
                return removed;
            }

            foreach (string name in ManagedServerRoutingVariables)
            {
                if (environment.ContainsKey(name))
                {
                    environment.Remove(name);
                    removed.Add(name);
                }
            }

            return removed;
        }
    }
}
