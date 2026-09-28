using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MCPForUnity.Editor.Services.Route;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// Project-local record storage, adoption gating, domain-reload reuse, pending-launch
    /// reconciliation and Windows path handling.
    /// </summary>
    [TestFixture]
    public class McpOwnershipRecordTests
    {
        // ------------------------------------------------------------------ J
        [Test]
        public void J_TwoProjects_ReceiveDistinctProjectLocalHandshakePaths()
        {
            string main = McpRunStatePaths.GetHandshakePath(OwnershipFixture.MainRoot);
            string workerOne = McpRunStatePaths.GetHandshakePath(OwnershipFixture.WorkerOneRoot);
            string workerTwo = McpRunStatePaths.GetHandshakePath(OwnershipFixture.WorkerTwoRoot);

            Assert.That(main, Is.Not.EqualTo(workerOne));
            Assert.That(workerOne, Is.Not.EqualTo(workerTwo));
            Assert.That(main, Is.Not.EqualTo(workerTwo));

            string expectedTail = Path.Combine("Library", "MCPForUnity", "RunState", "handshake.json");
            Assert.That(main, Does.EndWith(expectedTail));
            Assert.That(workerOne, Does.EndWith(expectedTail));
            Assert.That(workerTwo, Does.EndWith(expectedTail));
        }

        [Test]
        public void J_EveryRecordAndPidPathStaysInsideItsOwnRunStateDirectory()
        {
            foreach (string root in new[]
                     {
                         OwnershipFixture.MainRoot,
                         OwnershipFixture.WorkerOneRoot,
                         OwnershipFixture.WorkerTwoRoot,
                     })
            {
                string runState = McpRunStatePaths.GetRunStateDirectory(root);
                Assert.That(
                    McpRunStatePaths.IsPathInside(McpRunStatePaths.GetHandshakePath(root), runState),
                    Is.True,
                    root);
                Assert.That(
                    McpRunStatePaths.IsPathInside(McpRunStatePaths.GetPidFilePath(root, 8101), runState),
                    Is.True,
                    root);
            }
        }

        [Test]
        public void J_PidEvidenceStaysSeparateFromTheHandshakeRecord()
        {
            string handshake = McpRunStatePaths.GetHandshakePath(OwnershipFixture.MainRoot);
            string pidFile = McpRunStatePaths.GetPidFilePath(OwnershipFixture.MainRoot, 8101);

            Assert.That(pidFile, Is.Not.EqualTo(handshake));
            Assert.That(Path.GetFileName(pidFile), Is.EqualTo("mcp_http_8101.pid"));
        }

        [Test]
        public void J_PathHelpersRejectEscapes()
        {
            string runState = McpRunStatePaths.GetRunStateDirectory(OwnershipFixture.MainRoot);
            string outside = Path.Combine(
                McpRunStatePaths.GetRunStateDirectory(OwnershipFixture.MainRoot),
                "..", "..", "..", "handshake.json");

            Assert.That(McpRunStatePaths.IsPathInside(outside, runState), Is.False);
            Assert.That(McpRunStatePaths.IsPathInside(null, runState), Is.False);
            Assert.That(McpRunStatePaths.IsPathInside("/tmp/x", null), Is.False);
        }

        [Test]
        public void J_HandshakeRecordRoundTripsThroughItsVersionedSchema()
        {
            McpRunStateRecord original = new OwnershipFixture().BuildRecord();
            string json = original.ToJson();

            Assert.That(json, Does.Contain("schema_version"));
            Assert.That(json, Does.Contain("canonical_project_root"));
            Assert.That(json, Does.Contain("editor_start_utc"));
            Assert.That(json, Does.Contain("server_start_utc"));
            Assert.That(json, Does.Contain("instance_token"));
            Assert.That(json, Does.Contain("pidfile_path"));
            Assert.That(json, Does.Contain("lifecycle_state"));

            Assert.That(McpRunStateRecord.TryParse(json, out McpRunStateRecord parsed, out string error), Is.True, error);
            Assert.That(parsed.SchemaVersion, Is.EqualTo(McpRunStateRecord.CurrentSchemaVersion));
            Assert.That(parsed.ServerPid, Is.EqualTo(original.ServerPid));
            Assert.That(parsed.InstanceToken, Is.EqualTo(original.InstanceToken));
            Assert.That(parsed.CanonicalProjectRoot, Is.EqualTo(original.CanonicalProjectRoot));
        }

        // ------------------------------------------------------------------ L
        [Test]
        public void L_ForeignProjectRecord_IsNotAdopted()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord foreign = fixture.BuildRecord();
            foreign.CanonicalProjectRoot = McpRunStatePaths.Canonicalize(OwnershipFixture.WorkerOneRoot);

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                foreign, fixture.BuildObservation(), out string detail);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
            Assert.That(detail, Does.Contain("refusing to adopt"));
        }

        [Test]
        public void L_NoRecordMeansAFreshLaunchMayProceed()
        {
            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                null, new OwnershipFixture().BuildObservation(), out _);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.NoRecord));
        }

        [Test]
        public void L_UnusableRecordIsUnknownAndNeverReplaceable()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord broken = fixture.BuildRecord();
            broken.SchemaVersion = McpRunStateRecord.CurrentSchemaVersion + 1;

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                broken, fixture.BuildObservation(), out _);

            // A record that cannot be structurally trusted is UNKNOWN, not stale: overwriting it
            // could destroy another editor's live ownership evidence.
            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.Unknown));
        }

        // ------------------------------------------------------------------ X
        [Test]
        public void X_WindowsPathWithSpaces_ResolvesProjectLocalEvidence()
        {
            const string spacedRoot = @"C:\Users\A B\My Unity Project";
            string runState = McpRunStatePaths.GetRunStateDirectory(spacedRoot);
            string handshake = McpRunStatePaths.GetHandshakePath(spacedRoot);
            string pidFile = McpRunStatePaths.GetPidFilePath(spacedRoot, 8101);

            Assert.That(runState, Does.Contain("My Unity Project"));
            Assert.That(handshake, Does.Contain("My Unity Project"));
            Assert.That(McpRunStatePaths.IsPathInside(handshake, runState), Is.True);
            Assert.That(McpRunStatePaths.IsPathInside(pidFile, runState), Is.True);

            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.That(
                    McpRunStatePaths.PathsEqual(
                        @"c:\users\a b\my unity project",
                        @"C:\Users\A B\My Unity Project"),
                    Is.True);
                Assert.That(
                    McpRunStatePaths.PathsEqual(
                        @"C:\Users\A B\My Unity Project\.",
                        @"C:\Users\A B\My Unity Project"),
                    Is.True);
            }
        }

        [Test]
        public void X_SpacedProjectPathSurvivesACoherentStopDecision()
        {
            var fixture = new OwnershipFixture { ProjectRoot = @"C:\Users\A B\My Unity Project" };
            McpOwnershipDecision decision = fixture.EvaluateStop();

            Assert.That(decision.Allowed, Is.True, decision.Detail);
        }

        // ------------------------------------------------------------------ Y
        [Test]
        public void Y_DomainReloadWithinTheSameLifetime_ReusesTheValidatedRecord()
        {
            var fixture = new OwnershipFixture();

            Assert.That(fixture.EvaluateStop().Allowed, Is.True);
            Assert.That(
                McpOwnershipEvaluator.EvaluateAdoption(
                    fixture.BuildRecord(), fixture.BuildObservation(), out _),
                Is.EqualTo(McpAdoptionOutcome.OwnedByCurrentLifetime));
        }

        [Test]
        public void Y_InterruptedLaunchCanBeReconciledIntoARunningRecord()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pending = fixture.BuildRecord();
            McpOwnershipObservation observation = fixture.BuildObservation();

            bool identified = McpOwnershipEvaluator.TryIdentifyPendingServer(
                pending, observation, out McpRunStateRecord promoted, out string detail);
            Assert.That(identified, Is.True, detail);
            Assert.That(promoted.LifecycleState, Is.EqualTo(McpRunStateRecord.LifecycleRunning));
            Assert.That(promoted.ServerPid, Is.EqualTo(OwnershipFixture.DefaultServerPid));
            Assert.That(promoted.ServerStartUtc, Is.EqualTo(McpRunStateRecord.FormatUtc(fixture.ServerStart)));

            Assert.That(McpOwnershipEvaluator.EvaluateStop(promoted, observation).Allowed, Is.True);
        }

        [Test]
        public void Y_PendingRecordIsNeverTerminatedBeforeItIsReconciled()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };

            McpOwnershipDecision decision = fixture.EvaluateStop();

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.IncompleteRecord));
        }

        [Test]
        public void Y_ReconciliationRefusesAProcessThatPredatesTheLaunchRecord()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pending = fixture.BuildRecord();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerProcessStartUtc = fixture.WrittenAt.AddMinutes(-10);

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, observation, out _, out _),
                Is.False);
        }

        [Test]
        public void Y_ReconciliationRefusesAProcessWithoutTheNonce()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pending = fixture.BuildRecord();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerCommandLine = "uvx mcp-for-unity --transport http";

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, observation, out _, out _),
                Is.False);
        }

        [Test]
        public void Y_ReconciliationRefusesAnEndpointWithTwoListeners()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pending = fixture.BuildRecord();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ListeningProcessIds = new List<int>
            {
                OwnershipFixture.DefaultServerPid,
                OwnershipFixture.DefaultServerPid + 1,
            };

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, observation, out _, out _),
                Is.False);
        }

        [Test]
        public void Y_ReconciliationRefusesAForeignEditorLifetime()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pending = fixture.BuildRecord();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1;

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, observation, out _, out _),
                Is.False);
        }

        // ------------------------------------------------------------------ Z
        [Test]
        public void Z_NewEditorLifetimeCannotAdoptALiveOldHandshake()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord previousLifetime = fixture.BuildRecord();

            McpOwnershipObservation newLifetime = fixture.BuildObservation();
            newLifetime.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1000;
            newLifetime.CurrentEditorStartUtc = fixture.EditorStart.AddDays(1);

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                previousLifetime, newLifetime, out string detail);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
            Assert.That(detail, Does.Contain("live server"));
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(previousLifetime, newLifetime).Reason,
                Is.EqualTo(McpOwnershipDenyReason.ForeignEditorLifetime));
        }

        [Test]
        public void Z_NewEditorLifetimeMayReplaceARecordWhoseServerIsGone()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord previousLifetime = fixture.BuildRecord();

            McpOwnershipObservation newLifetime = fixture.BuildObservation();
            newLifetime.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1000;
            newLifetime.CurrentEditorStartUtc = fixture.EditorStart.AddDays(1);
            newLifetime.ServerProcessExists = false;
            // Staleness has to be positively established on BOTH sides: the recorded server is
            // gone AND the editor lifetime that wrote the record is gone too.
            newLifetime.RecordedEditorProcessExists = false;
            newLifetime.ListeningProcessIds = new List<int>();

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                previousLifetime, newLifetime, out _);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.Stale));
        }

        [Test]
        public void Z_NewEditorLifetimeDoesNotAdoptAnUnreadableLiveRecord()
        {
            var fixture = new OwnershipFixture();

            // No observation at all: the existing record is treated as live and left alone.
            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                fixture.BuildRecord(), null, out string detail);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
            Assert.That(detail, Does.Contain("live"));
        }
    }
}
