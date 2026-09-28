namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Unity-facing implementation of the pre-connect authorizer: it binds the dependency-free
    /// <see cref="McpManagedPreConnectGate"/> to this editor process's resolved route and its full
    /// ownership evaluation.
    /// </summary>
    public sealed class McpRouteConnectionAuthorizer : IMcpManagedConnectionAuthorizer
    {
        /// <inheritdoc/>
        public bool IsManagedRoute => McpRouteProvider.Configuration.IsManagedRoute;

        /// <inheritdoc/>
        public bool TryGetLaunchToken(out string instanceToken, out string reason)
        {
            if (McpRouteProvider.TryGetActiveLaunchToken(
                    out instanceToken,
                    out McpOwnershipDenyReason denyReason,
                    out string detail))
            {
                reason = null;
                return true;
            }

            instanceToken = null;
            reason = $"{denyReason}: {detail}";
            return false;
        }
    }
}
