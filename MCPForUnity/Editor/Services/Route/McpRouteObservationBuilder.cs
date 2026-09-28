using System;
using System.Collections.Generic;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>Inputs for one live ownership observation.</summary>
    public sealed class McpObservationRequest
    {
        public string CanonicalProjectRoot;
        public string Endpoint;
        public int CurrentEditorPid;

        /// <summary>Server PID named by the record, or 0 when the record is still pending.</summary>
        public int RecordedServerPid;

        /// <summary>Editor PID named by the record, or 0 when no record exists.</summary>
        public int RecordedEditorPid;
    }

    /// <summary>
    /// Gathers the live OS observations one ownership decision is made from.
    ///
    /// Every value that could not be observed is left marked unavailable rather than guessed at,
    /// so the evaluator refuses instead of acting on a hole. This is the single production
    /// implementation of that gathering step: the launch path, the stop path and the connection
    /// gate all observe through here.
    /// </summary>
    public static class McpRouteObservationBuilder
    {
        public static McpOwnershipObservation Build(
            IMcpRouteStateStore store,
            IMcpProcessInspector inspector,
            McpObservationRequest request)
        {
            if (request == null)
            {
                return new McpOwnershipObservation();
            }

            var observation = new McpOwnershipObservation
            {
                CanonicalProjectRoot = request.CanonicalProjectRoot,
                Endpoint = request.Endpoint,
                CurrentEditorPid = request.CurrentEditorPid,
            };

            if (store != null)
            {
                observation.RunStateDirectory = store.GetRunStateDirectory();
                observation.PidFilePath = store.GetPidFilePath(PortFromEndpoint(request.Endpoint));
            }

            if (inspector != null)
            {
                if (inspector.TryGetCurrentProcessStartTimeUtc(out DateTime editorStart))
                {
                    observation.CurrentEditorStartUtc = editorStart;
                }

                if (!string.IsNullOrEmpty(observation.PidFilePath) && store != null)
                {
                    observation.PidFileReadable = store.TryReadPidFile(
                        observation.PidFilePath, out int pidFromFile, out bool exists);
                    observation.PidFileExists = exists;
                    observation.PidFilePid = observation.PidFileReadable ? pidFromFile : 0;
                }

                int serverPid = request.RecordedServerPid > 0
                    ? request.RecordedServerPid
                    : observation.PidFilePid;
                observation.ServerPid = serverPid;

                if (serverPid > 0)
                {
                    bool known = inspector.TryObserveProcessExistence(serverPid, out bool exists);
                    observation.ServerProcessExistenceKnown = known;
                    observation.ServerProcessExists = known ? exists : inspector.ProcessExists(serverPid);

                    if (inspector.TryGetProcessStartTimeUtc(serverPid, out DateTime serverStart))
                    {
                        observation.ServerProcessLifetimeAvailable = true;
                        observation.ServerProcessStartUtc = serverStart;
                    }

                    observation.ServerCommandLineAvailable =
                        inspector.TryGetCommandLine(serverPid, out string commandLine);
                    observation.ServerCommandLine = observation.ServerCommandLineAvailable ? commandLine : null;
                }

                if (request.RecordedEditorPid > 0
                    && inspector.TryObserveProcessExistence(
                        request.RecordedEditorPid, out bool editorExists))
                {
                    observation.RecordedEditorProcessExists = editorExists;
                }

                int port = PortFromEndpoint(request.Endpoint);
                observation.ListeningProcessIds = port > 0
                    ? inspector.GetListeningProcessIds(port)
                    : new List<int>();
            }

            return observation;
        }

        /// <summary>TCP port of an absolute endpoint URL, or 0 when there is none.</summary>
        public static int PortFromEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return 0;
            }

            return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) && uri.Port > 0
                ? uri.Port
                : 0;
        }
    }
}
