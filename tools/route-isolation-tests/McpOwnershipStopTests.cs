using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MCPForUnity.Editor.Services.Route;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// The terminate decision. Every refusal case is asserted explicitly: leaving a process
    /// untouched is the intended outcome whenever the evidence is anything less than exact.
    /// </summary>
    [TestFixture]
    public class McpOwnershipStopTests
    {
        [Test]
        public void Baseline_CoherentEvidence_AllowsTheStop()
        {
            McpOwnershipDecision decision = new OwnershipFixture().EvaluateStop();

            Assert.That(decision.Allowed, Is.True, decision.Detail);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.None));
        }

        // ------------------------------------------------------------------ K
        [Test]
        public void K_CopiedHandshakeFromAnotherProject_IsRejected()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord copied = fixture.BuildRecord();
            copied.CanonicalProjectRoot = McpRunStatePaths.Canonicalize(OwnershipFixture.WorkerOneRoot);

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                copied, fixture.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignProject));
        }

        // ------------------------------------------------------------------ M
        [Test]
        public void M_WrongLaunchNonce_IsRejected()
        {
            // The record expects one nonce; the live process carries a different one.
            var fixture = new OwnershipFixture();
            McpRunStateRecord record = fixture.BuildRecord();
            record.InstanceToken = "a-different-nonce";

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                record, fixture.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.NonceMismatch));
        }

        [Test]
        public void M_UnavailableCommandLineCannotProveTheNonce_AndIsRejected()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerCommandLineAvailable = false;
            observation.ServerCommandLine = null;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.NonceMismatch));
        }

        // ------------------------------------------------------------------ N
        [Test]
        public void N_RecordForADifferentEndpoint_IsRejected()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord record = fixture.BuildRecord();
            record.Endpoint = "http://127.0.0.1:8102";

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                record, fixture.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEndpoint));
        }

        // ------------------------------------------------------------------ O
        [Test]
        public void O_EditorCannotStopAnotherEditorsServer()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEditorLifetime));
        }

        // ------------------------------------------------------------------ P
        [Test]
        public void P_MainCannotStopAWorkersServer()
        {
            var main = new OwnershipFixture();
            var workerOne = new OwnershipFixture { ProjectRoot = OwnershipFixture.WorkerOneRoot };

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                main.BuildRecord(), workerOne.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignProject));
        }

        // ------------------------------------------------------------------ Q
        [Test]
        public void Q_WorkerOneCannotStopWorkerTwosServer()
        {
            var workerOne = new OwnershipFixture { ProjectRoot = OwnershipFixture.WorkerOneRoot };
            var workerTwo = new OwnershipFixture { ProjectRoot = OwnershipFixture.WorkerTwoRoot };

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                workerTwo.BuildRecord(), workerOne.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignProject));
        }

        // ------------------------------------------------------------------ R
        [Test]
        public void R_StalePidEvidence_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerProcessExists = false;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ServerNotRunning));
        }

        [Test]
        public void R_StalePidFile_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.PidFilePid = OwnershipFixture.DefaultServerPid + 7;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.PidFileMismatch));
        }

        [Test]
        public void R_RecordNamingTheEditorProcessItself_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord record = fixture.BuildRecord();
            record.ServerPid = OwnershipFixture.DefaultEditorPid;

            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerPid = OwnershipFixture.DefaultEditorPid;
            observation.PidFilePid = OwnershipFixture.DefaultEditorPid;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(record, observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ServerNotRunning));
        }

        // ------------------------------------------------------------------ S
        [Test]
        public void S_ReusedPid_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            // Same PID, but the live process was created hours later: the PID was reused.
            observation.ServerProcessStartUtc = fixture.ServerStart.AddHours(3);

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.PidReuse));
        }

        // ------------------------------------------------------------------ T
        [Test]
        public void T_MismatchedEditorProcessStartTime_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddMinutes(30);

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEditorLifetime));
        }

        [Test]
        public void T_ProcessStartComparisonsAreExact_NoToleranceIsApplied()
        {
            var fixture = new OwnershipFixture();

            // A single millisecond is a different process lifetime. Both sides are read from the
            // same OS identity source and round-trip through the invariant "O" format, so there is
            // no rounding error to absorb and no tolerance is justified.
            McpOwnershipObservation serverShifted = fixture.BuildObservation();
            serverShifted.ServerProcessStartUtc = fixture.ServerStart.AddMilliseconds(1);
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(fixture.BuildRecord(), serverShifted).Allowed,
                Is.False);

            McpOwnershipObservation editorShifted = fixture.BuildObservation();
            editorShifted.CurrentEditorStartUtc = fixture.EditorStart.AddMilliseconds(-1);
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(fixture.BuildRecord(), editorShifted).Allowed,
                Is.False);

            // The unchanged, coherent pair is still accepted.
            Assert.That(fixture.EvaluateStop().Allowed, Is.True);
        }

        // ------------------------------------------------------------------ U
        [Test]
        public void U_MissingOwnershipEvidence_DoesNotTerminate()
        {
            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                null, new OwnershipFixture().BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.NoRecord));
        }

        [Test]
        public void U_MissingObservation_DoesNotTerminate()
        {
            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                new OwnershipFixture().BuildRecord(), null);

            Assert.That(decision.Allowed, Is.False);
        }

        // ------------------------------------------------------------------ V
        [Test]
        public void V_UnparseableRecord_DoesNotTerminate()
        {
            Assert.That(McpRunStateRecord.TryParse("{ not json", out _, out _), Is.False);
            Assert.That(McpRunStateRecord.TryParse("", out _, out _), Is.False);
            Assert.That(McpRunStateRecord.TryParse(null, out _, out _), Is.False);
        }

        [Test]
        public void V_UnsupportedSchemaVersion_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord future = fixture.BuildRecord();
            future.SchemaVersion = McpRunStateRecord.CurrentSchemaVersion + 1;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                future, fixture.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.MalformedRecord));
        }

        [Test]
        public void V_UnknownLifecycleState_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord wedged = fixture.BuildRecord();
            wedged.LifecycleState = "wedged";

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                wedged, fixture.BuildObservation());

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.MalformedRecord));
        }

        [Test]
        public void V_UnobservableProcessLifetime_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerProcessLifetimeAvailable = false;
            observation.ServerProcessStartUtc = null;

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.MissingProcessLifetime));
        }

        [Test]
        public void V_UnreadablePidEvidence_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.PidFileReadable = false;
            observation.PidFilePid = 0;

            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(fixture.BuildRecord(), observation).Allowed,
                Is.False);
        }

        [Test]
        public void V_MissingPidEvidence_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.PidFileExists = false;
            observation.PidFileReadable = false;
            observation.PidFilePid = 0;

            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(fixture.BuildRecord(), observation).Allowed,
                Is.False);
        }

        [Test]
        public void V_PidEvidencePathOutsideRunState_IsRejected()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.PidFilePath = Path.Combine(Path.GetTempPath(), "mcp_http_8101.pid");

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.PidFileMismatch));
        }

        [Test]
        public void V_RecordPointingAtAForeignRunStateDirectory_IsRejected()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.RunStateDirectory =
                McpRunStatePaths.GetRunStateDirectory(OwnershipFixture.WorkerOneRoot);

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.PathEscape));
        }

        // ------------------------------------------------------------------ W
        [Test]
        public void W_EndPointOccupiedByAForeignProcess_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ListeningProcessIds =
                new List<int> { OwnershipFixture.DefaultServerPid + 99 };

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ServerNotListener));
        }

        [Test]
        public void W_AmbiguousListener_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ListeningProcessIds = new List<int>
            {
                OwnershipFixture.DefaultServerPid,
                OwnershipFixture.DefaultServerPid + 1,
            };

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(
                fixture.BuildRecord(), observation);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.AmbiguousListener));
        }

        [Test]
        public void W_NoListenerAtAll_DoesNotTerminate()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ListeningProcessIds = new List<int>();

            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(fixture.BuildRecord(), observation).Allowed,
                Is.False);
        }
    }
}
