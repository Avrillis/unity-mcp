namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Decides whether a managed Unity Editor may connect/register to an MCP endpoint and echo
    /// that launch's nonce.
    ///
    /// A reachable endpoint proves nothing: it could be another worker's server, a server left
    /// over by a previous editor process, or a port that has been reused. The nonce is only ever
    /// released when the project-local record and live OS observations agree on the whole
    /// ownership tuple (schema, root, endpoint, editor PID AND creation instant, lifecycle,
    /// nonce, RunState path and the live server identity).
    ///
    /// Anything less fails closed: no token is returned and the connection must not be made.
    /// </summary>
    public static class McpManagedConnectionGate
    {
        /// <summary>
        /// Returns the launch nonce for <paramref name="record"/> only when the current editor
        /// lifetime provably owns the live server described by it.
        /// </summary>
        public static bool TryGetLaunchToken(
            McpRunStateRecord record,
            McpOwnershipObservation observation,
            out string instanceToken,
            out McpOwnershipDenyReason reason,
            out string detail)
        {
            instanceToken = null;
            reason = McpOwnershipDenyReason.None;
            detail = null;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(record, observation);
            if (!decision.Allowed)
            {
                reason = decision.Reason;
                detail = decision.Detail;
                return false;
            }

            if (string.IsNullOrWhiteSpace(record.InstanceToken))
            {
                reason = McpOwnershipDenyReason.IncompleteRecord;
                detail = "the ownership record has no usable launch nonce.";
                return false;
            }

            instanceToken = record.InstanceToken;
            return true;
        }
    }
}
