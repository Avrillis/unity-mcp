using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Services.Route;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// Builds a fully coherent "this editor owns this server" pair (record + live
    /// observation) that individual tests then perturb one field at a time.
    /// </summary>
    internal sealed class OwnershipFixture
    {
        public const string MainRoot = @"C:\Projects\The Last Noob";
        public const string WorkerOneRoot = @"C:\Projects\Workers\WORKER-01";
        public const string WorkerTwoRoot = @"C:\Projects\Workers\WORKER-02";

        public const string DefaultEndpoint = "http://127.0.0.1:8101";
        public const string DefaultNonce = "0f0a1b2c3d4e5f60718293a4b5c6d7e8";
        public const int DefaultEditorPid = 4242;
        public const int DefaultServerPid = 5150;

        public static readonly DateTime DefaultEditorStart =
            new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

        public static readonly DateTime DefaultServerStart =
            new DateTime(2026, 9, 28, 9, 0, 5, DateTimeKind.Utc);

        public static readonly DateTime DefaultWrittenAt =
            new DateTime(2026, 9, 28, 9, 0, 4, DateTimeKind.Utc);

        public string ProjectRoot = MainRoot;
        public string Endpoint = DefaultEndpoint;
        public int EditorPid = DefaultEditorPid;
        public DateTime EditorStart = DefaultEditorStart;
        public int ServerPid = DefaultServerPid;
        public DateTime ServerStart = DefaultServerStart;
        public string Nonce = DefaultNonce;
        public string LifecycleState = McpRunStateRecord.LifecycleRunning;
        public DateTime WrittenAt = DefaultWrittenAt;
        public string RecordId = "9f1c4a2b7d3e4568a0b1c2d3e4f50617";

        public static OwnershipFixture ForProject(string projectRoot, string endpoint, string nonce)
            => new OwnershipFixture
            {
                ProjectRoot = projectRoot,
                Endpoint = endpoint,
                Nonce = nonce,
            };

        public McpRunStateRecord BuildRecord()
        {
            bool pending = LifecycleState == McpRunStateRecord.LifecycleStarting;
            string canonicalRoot = McpRunStatePaths.Canonicalize(ProjectRoot);
            int port = Port;

            return new McpRunStateRecord
            {
                SchemaVersion = McpRunStateRecord.CurrentSchemaVersion,
                RecordId = RecordId,
                CanonicalProjectRoot = canonicalRoot,
                Endpoint = Endpoint,
                EditorPid = EditorPid,
                EditorStartUtc = McpRunStateRecord.FormatUtc(EditorStart),
                ServerPid = pending ? 0 : ServerPid,
                ServerStartUtc = pending ? null : McpRunStateRecord.FormatUtc(ServerStart),
                InstanceToken = Nonce,
                PidFilePath = McpRunStatePaths.GetPidFilePath(canonicalRoot, port),
                LifecycleState = LifecycleState,
                WrittenUtc = McpRunStateRecord.FormatUtc(WrittenAt),
            };
        }

        public McpOwnershipObservation BuildObservation()
        {
            string canonicalRoot = McpRunStatePaths.Canonicalize(ProjectRoot);
            string pidFilePath = McpRunStatePaths.GetPidFilePath(canonicalRoot, Port);

            return new McpOwnershipObservation
            {
                CanonicalProjectRoot = canonicalRoot,
                RunStateDirectory = McpRunStatePaths.GetRunStateDirectory(canonicalRoot),
                Endpoint = Endpoint,
                CurrentEditorPid = EditorPid,
                CurrentEditorStartUtc = EditorStart,
                ServerPid = ServerPid,
                ServerProcessExists = true,
                ServerProcessExistenceKnown = true,
                ServerProcessLifetimeAvailable = true,
                ServerProcessStartUtc = ServerStart,
                ServerCommandLineAvailable = true,
                ServerCommandLine =
                    $"uvx mcp-for-unity --transport http --http-url {Endpoint} "
                    + $"--unity-project-root \"{canonicalRoot}\" --pidfile \"{pidFilePath}\" "
                    + $"--unity-instance-token {Nonce}",
                PidFilePath = pidFilePath,
                PidFileExists = true,
                PidFileReadable = true,
                PidFilePid = ServerPid,
                RecordedEditorProcessExists = true,
                ListeningProcessIds = new List<int> { ServerPid },
            };
        }

        public McpOwnershipDecision EvaluateStop()
            => McpOwnershipEvaluator.EvaluateStop(BuildRecord(), BuildObservation());

        private int Port
        {
            get
            {
                return Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri uri) && uri.Port > 0
                    ? uri.Port
                    : 8101;
            }
        }
    }
}
