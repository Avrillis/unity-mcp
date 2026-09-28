using System;
using System.Threading;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>Outcome of the managed pre-connect authorization.</summary>
    public sealed class McpPreConnectDecision
    {
        /// <summary>
        /// True only when a socket MAY be opened. For a managed route that means the whole
        /// ownership tuple was proven; for an unmanaged route it means the route is not managed.
        /// </summary>
        public bool CanOpenConnection { get; internal set; }

        /// <summary>True when this editor's route is process-scoped (managed).</summary>
        public bool IsManagedRoute { get; internal set; }

        /// <summary>The launch nonce to echo, when one may be released.</summary>
        public string InstanceToken { get; internal set; }

        /// <summary>Human-readable refusal reason (never contains credentials).</summary>
        public string Reason { get; internal set; }
    }

    /// <summary>Result of one gated connection attempt.</summary>
    public sealed class McpConnectAttempt
    {
        /// <summary>False when the gate refused: the opener was never invoked.</summary>
        public bool Authorized { get; internal set; }

        /// <summary>The opener's own result; meaningless unless <see cref="Authorized"/> is true.</summary>
        public bool Opened { get; internal set; }

        public string Reason { get; internal set; }
    }

    /// <summary>
    /// The single authority every managed WebSocket connection attempt consults before any socket
    /// is created or opened.
    ///
    /// A managed editor must never connect to another editor's MCP endpoint, and a reachable
    /// endpoint is not proof of ownership: it could be a sibling worker's server, a server left
    /// over by a previous editor process, or a reused port. The implementation is therefore the
    /// same full ownership evaluation the stop path uses.
    /// </summary>
    public interface IMcpManagedConnectionAuthorizer
    {
        /// <summary>True when this editor's route came from its process-scoped configuration.</summary>
        bool IsManagedRoute { get; }

        /// <summary>
        /// Full ownership-tuple check for the CURRENT editor lifetime, yielding the launch nonce
        /// only when the project-local record and live OS observations agree.
        /// </summary>
        bool TryGetLaunchToken(out string instanceToken, out string reason);
    }

    /// <summary>
    /// Centralized managed pre-connect gate.
    ///
    /// Every managed connection path - normal startup, auto-start, manual Connect, reconnect and
    /// reload resume - must run through <see cref="ConnectWithGateAsync"/> so the authorization is
    /// ordered strictly before the socket open. A refusal returns without ever invoking the opener.
    /// </summary>
    public static class McpManagedPreConnectGate
    {
        /// <summary>Decides whether a connection may be attempted, without opening anything.</summary>
        public static McpPreConnectDecision Authorize(IMcpManagedConnectionAuthorizer authorizer)
        {
            var decision = new McpPreConnectDecision();

            // An absent authorizer can only occur in a non-Unity host; treat it as unmanaged so the
            // legacy behaviour is preserved rather than silently disabling MCP.
            if (authorizer == null || !authorizer.IsManagedRoute)
            {
                decision.CanOpenConnection = true;
                decision.IsManagedRoute = false;
                return decision;
            }

            decision.IsManagedRoute = true;

            if (!authorizer.TryGetLaunchToken(out string token, out string reason))
            {
                decision.CanOpenConnection = false;
                decision.Reason =
                    "refusing to connect to a managed MCP endpoint without proven ownership: "
                    + (reason ?? "the ownership evidence did not agree.");
                return decision;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                decision.CanOpenConnection = false;
                decision.Reason =
                    "refusing to connect to a managed MCP endpoint: no launch nonce was available.";
                return decision;
            }

            decision.CanOpenConnection = true;
            decision.InstanceToken = token;
            return decision;
        }

        /// <summary>
        /// Runs the gate and, only when it allows the attempt, invokes
        /// <paramref name="openConnectionAsync"/>. This is the production orchestration every
        /// managed connection path uses, and it is what makes "no network attempt without proven
        /// ownership" true by construction rather than by convention.
        /// </summary>
        public static async Task<McpConnectAttempt> ConnectWithGateAsync(
            IMcpManagedConnectionAuthorizer authorizer,
            Func<CancellationToken, Task<bool>> openConnectionAsync,
            Action<string> log,
            CancellationToken token)
        {
            var attempt = new McpConnectAttempt();

            McpPreConnectDecision decision = Authorize(authorizer);
            if (!decision.CanOpenConnection)
            {
                attempt.Authorized = false;
                attempt.Opened = false;
                attempt.Reason = decision.Reason;
                log?.Invoke(decision.Reason);
                return attempt;
            }

            if (openConnectionAsync == null)
            {
                attempt.Authorized = true;
                attempt.Opened = false;
                attempt.Reason = "no connection opener was supplied.";
                return attempt;
            }

            attempt.Authorized = true;
            attempt.Opened = await openConnectionAsync(token).ConfigureAwait(false);
            return attempt;
        }
    }
}
