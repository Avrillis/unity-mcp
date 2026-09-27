using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Best-effort cleanup when the Unity Editor is quitting.
    /// - Stops active transports so clients don't see a "hung" session longer than necessary.
    /// - Stops the local HTTP server this Unity instance launched, so a headless server doesn't
    ///   become an invisible orphan. This runs on quit only, never on domain reload.
    ///
    /// Termination requires exact project-local ownership evidence (see
    /// ServerManagementService): this editor lifetime, this endpoint, the live server PID and
    /// creation time, the launch nonce and the PID evidence file must all agree. Missing,
    /// stale, ambiguous or foreign evidence means the process is left untouched, so one
    /// editor can never stop another editor's or another project's server.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpEditorShutdownCleanup
    {
        static McpEditorShutdownCleanup()
        {
            // Guard against duplicate subscriptions across domain reloads.
            try { EditorApplication.quitting -= OnEditorQuitting; } catch { }
            EditorApplication.quitting += OnEditorQuitting;
        }

        // A -batchmode/CI instance resolves the interactive editor's server via the global
        // pidfile+port handshake, so cleanup there would stop another user's server. Mirror the
        // sibling guards (HttpAutoStartHandler, StdioBridgeHost): skip in batch unless opted in.
        internal static bool ShouldRunCleanup() =>
            ShouldRunCleanup(Application.isBatchMode, Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH"));

        internal static bool ShouldRunCleanup(bool isBatchMode, string allowBatchEnv) =>
            !isBatchMode || !string.IsNullOrWhiteSpace(allowBatchEnv);

        private static void OnEditorQuitting()
        {
            if (!ShouldRunCleanup()) return;

            // 1) Stop transports (best-effort, bounded wait).
            try
            {
                var transport = MCPServiceLocator.TransportManager;

                Task stopHttp = transport.StopAsync(TransportMode.Http);
                Task stopStdio = transport.StopAsync(TransportMode.Stdio);

                try { Task.WaitAll(new[] { stopHttp, stopStdio }, 750); } catch { }
            }
            catch (Exception ex)
            {
                // Avoid hard failures on quit.
                McpLog.Warn($"Shutdown cleanup: failed to stop transports: {ex.Message}");
            }

            // 2) Stop the local HTTP server this Unity instance launched (best-effort).
            // Headless servers have no terminal window, so an unstopped one is an invisible orphan.
            // StopManagedLocalHttpServer only stops the server whose project-local ownership record
            // matches this editor lifetime exactly, so it never touches servers launched by other
            // Unity instances. This runs on quit only;
            // domain reloads must NOT stop the server (and don't — this handler is gated on EditorApplication.quitting).
            try
            {
                MCPServiceLocator.Server.StopManagedLocalHttpServer();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Shutdown cleanup: failed to stop local HTTP server: {ex.Message}");
            }
        }
    }
}

