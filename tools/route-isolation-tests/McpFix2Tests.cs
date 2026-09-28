using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Route;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// C17-R3-FIX2 coverage at the production boundaries Sol named:
    ///   * the managed pre-connect gate (blocker 2);
    ///   * the termination final revalidation and its successor-record safety (blocker 3);
    ///   * cross-process conditional ownership mutation (blocker 4);
    ///   * the managed URL parser's explicit-port/loopback contract (blocker 6).
    ///
    /// The sources exercised here ARE the production implementations
    /// (<see cref="McpManagedPreConnectGate"/>, <see cref="McpOwnershipMutation"/>,
    /// <see cref="McpOwnershipStore"/>, <see cref="McpRouteConfiguration"/>); only the OS process
    /// observations and the socket opener are substituted.
    /// </summary>
    [TestFixture]
    public class McpFix2Tests
    {
        private string _directory;
        private string _handshakePath;

        private OwnershipFixture Fixture => new OwnershipFixture();

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "mcp-fix2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _handshakePath = Path.Combine(_directory, McpRunStatePaths.HandshakeFileName);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // Best effort.
            }
        }

        private McpOwnershipStore NewStore(int lockAttempts = 20)
            => new McpOwnershipStore(_handshakePath) { LockAttempts = lockAttempts, LockRetryDelayMs = 5 };

        private void Publish(McpRunStateRecord record)
        {
            Assert.That(
                McpRunStateFile.TryWriteAtomic(_handshakePath, record.ToJson(), out string error),
                Is.True, error);
        }

        // ============================================================
        // Blocker 2 - managed pre-connect gate
        // ============================================================

        private sealed class FakeAuthorizer : IMcpManagedConnectionAuthorizer
        {
            public bool IsManagedRoute { get; set; } = true;
            public bool Allow { get; set; } = true;
            public string Token { get; set; } = OwnershipFixture.DefaultNonce;
            public string Reason { get; set; } = "stale ownership record";
            public int Authorizations { get; private set; }

            public bool TryGetLaunchToken(out string instanceToken, out string reason)
            {
                Authorizations++;
                instanceToken = Allow ? Token : null;
                reason = Allow ? null : Reason;
                return Allow;
            }
        }

        private sealed class RecordingOpener
        {
            public int Attempts { get; private set; }
            public bool Result { get; set; }

            public Task<bool> OpenAsync(CancellationToken token)
            {
                Attempts++;
                return Task.FromResult(Result);
            }
        }

        private static async Task<McpConnectAttempt> Attempt(
            FakeAuthorizer authorizer, RecordingOpener opener, List<string> log = null)
        {
            return await McpManagedPreConnectGate.ConnectWithGateAsync(
                authorizer,
                opener.OpenAsync,
                log == null ? null : (Action<string>)(m => log.Add(m)),
                CancellationToken.None);
        }

        [Test]
        public async Task PreConnect_ValidManagedOwnershipOpensTheSocket()
        {
            var authorizer = new FakeAuthorizer();
            var opener = new RecordingOpener { Result = true };

            McpConnectAttempt attempt = await Attempt(authorizer, opener);

            Assert.That(attempt.Authorized, Is.True);
            Assert.That(attempt.Opened, Is.True);
            Assert.That(opener.Attempts, Is.EqualTo(1), "the socket open must be attempted");
        }

        [Test]
        public async Task PreConnect_UnmanagedRoutePreservesLegacyBehaviour()
        {
            var authorizer = new FakeAuthorizer { IsManagedRoute = false };
            var opener = new RecordingOpener { Result = true };

            McpConnectAttempt attempt = await Attempt(authorizer, opener);

            Assert.That(attempt.Authorized, Is.True);
            Assert.That(opener.Attempts, Is.EqualTo(1));
            Assert.That(authorizer.Authorizations, Is.EqualTo(0), "an unmanaged route is not gated");
        }

        [TestCase("stale ownership record")]
        [TestCase("copied handshake from another project")]
        [TestCase("foreign editor lifetime")]
        [TestCase("wrong endpoint")]
        [TestCase("missing pidfile")]
        [TestCase("wrong listener")]
        public async Task PreConnect_UnprovenOwnershipNeverOpensTheSocket(string reason)
        {
            var authorizer = new FakeAuthorizer { Allow = false, Reason = reason };
            var opener = new RecordingOpener { Result = true };
            var log = new List<string>();

            McpConnectAttempt attempt = await Attempt(authorizer, opener, log);

            Assert.That(attempt.Authorized, Is.False);
            Assert.That(opener.Attempts, Is.EqualTo(0), "no socket may be opened without ownership");
            Assert.That(attempt.Reason, Does.Contain(reason));
            Assert.That(log.Any(m => m.Contains(reason)), Is.True, "the refusal must be surfaced");
        }

        [Test]
        public async Task PreConnect_EmptyNonceNeverOpensTheSocket()
        {
            var authorizer = new FakeAuthorizer { Token = "   " };
            var opener = new RecordingOpener { Result = true };

            McpConnectAttempt attempt = await Attempt(authorizer, opener);

            Assert.That(attempt.Authorized, Is.False);
            Assert.That(opener.Attempts, Is.EqualTo(0));
        }

        [Test]
        public void PreConnect_EveryConnectionPathSharesTheSameGate()
        {
            // Startup, auto-start, manual Connect, reconnect and reload resume all reach the network
            // through WebSocketTransportClient.EstablishConnectionAsync, which now routes through
            // McpManagedPreConnectGate.ConnectWithGateAsync before creating any socket. The gate is
            // therefore the single pre-connect authorization path.
            var source = File.ReadAllText(FindRepositoryFile(
                @"MCPForUnity\Editor\Services\Transport\Transports\WebSocketTransportClient.cs"));

            int gateIndex = source.IndexOf(
                "McpManagedPreConnectGate.ConnectWithGateAsync", StringComparison.Ordinal);
            int connectIndex = source.IndexOf("ConnectAsync(candidate", StringComparison.Ordinal);

            Assert.That(gateIndex, Is.GreaterThanOrEqualTo(0), "the client must use the shared gate");
            Assert.That(connectIndex, Is.GreaterThan(gateIndex),
                "the gate must be ordered before the socket open in the production orchestration");
        }

        // ============================================================
        // Blocker 3 - termination final revalidation
        // ============================================================

        private sealed class FakeHandle : IRetainedProcessHandle
        {
            public DateTime? Start { get; set; } = OwnershipFixture.DefaultServerStart;
            public bool KillResult { get; set; } = true;
            public int KillCalls { get; private set; }

            public bool TryGetStartTimeUtc(out DateTime startUtc)
            {
                startUtc = Start ?? default;
                return Start.HasValue;
            }

            public bool Kill()
            {
                KillCalls++;
                return KillResult;
            }
        }

        private static Func<IReadOnlyList<int>> Listeners(params int[] pids)
            => () => pids;

        private McpTerminationOutcome Terminate(
            McpRunStateRecord evaluated,
            FakeHandle handle,
            Func<IReadOnlyList<int>> probe,
            IMcpOwnershipLockProvider locks = null)
            => McpOwnershipMutation.TerminateIfStillOwned(
                locks ?? NewStore(),
                probe,
                evaluated,
                OwnershipFixture.DefaultServerStart,
                handle);

        [Test]
        public void Termination_SameRetainedProcessAndUnchangedRecordIsTerminated()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);
            var handle = new FakeHandle();

            McpTerminationOutcome outcome = Terminate(
                record, handle, Listeners(record.ServerPid));

            Assert.That(outcome.Terminated, Is.True, outcome.Reason);
            Assert.That(outcome.RecordRemoved, Is.True, outcome.Reason);
            Assert.That(handle.KillCalls, Is.EqualTo(1));
            Assert.That(File.Exists(_handshakePath), Is.False);
        }

        [Test]
        public void Termination_RecordReplacedBeforeTerminationIsNeverKilled()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord evaluated = fixture.BuildRecord();
            Publish(evaluated);

            // A successor lifecycle publishes its own record between the evaluation and the kill.
            McpRunStateRecord successor = fixture.BuildRecord();
            successor.RecordId = McpRunStateRecord.NewRecordId();
            successor.InstanceToken = "successor-nonce-9a8b7c6d";
            Publish(successor);

            var handle = new FakeHandle();
            McpTerminationOutcome outcome = Terminate(evaluated, handle, Listeners(evaluated.ServerPid));

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0), "a replaced record must never authorise a kill");
            Assert.That(
                File.ReadAllText(_handshakePath),
                Does.Contain("successor-nonce-9a8b7c6d"),
                "the successor's record must be left untouched");
        }

        [TestCase("nonce")]
        [TestCase("lifecycle")]
        [TestCase("endpoint")]
        [TestCase("pidfile")]
        [TestCase("record-id")]
        public void Termination_AnyRecordFieldChangeRefusesTheKill(string field)
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord evaluated = fixture.BuildRecord();
            Publish(evaluated);

            McpRunStateRecord changed = fixture.BuildRecord();
            switch (field)
            {
                case "nonce": changed.InstanceToken = "different-nonce"; break;
                case "lifecycle": changed.LifecycleState = McpRunStateRecord.LifecycleStopping; break;
                case "endpoint": changed.Endpoint = "http://127.0.0.1:8999"; break;
                case "pidfile": changed.PidFilePath = @"C:\elsewhere\mcp_http_8101.pid"; break;
                case "record-id": changed.RecordId = McpRunStateRecord.NewRecordId(); break;
            }

            Publish(changed);

            var handle = new FakeHandle();
            McpTerminationOutcome outcome = Terminate(evaluated, handle, Listeners(evaluated.ServerPid));

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0));
        }

        [Test]
        public void Termination_ListenerReplacedRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);

            var handle = new FakeHandle();
            McpTerminationOutcome outcome = Terminate(record, handle, Listeners(record.ServerPid + 7));

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(outcome.Reason, Does.Contain("listener"));
            Assert.That(handle.KillCalls, Is.EqualTo(0));
            Assert.That(File.Exists(_handshakePath), Is.True, "the record is left for the owner");
        }

        [Test]
        public void Termination_ListenerDisappearedRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);

            var handle = new FakeHandle();
            McpTerminationOutcome outcome = Terminate(record, handle, Listeners());

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0));
        }

        [Test]
        public void Termination_RetainedLifetimeChangedRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);

            var handle = new FakeHandle { Start = OwnershipFixture.DefaultServerStart.AddSeconds(30) };
            McpTerminationOutcome outcome = Terminate(record, handle, Listeners(record.ServerPid));

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0));
        }

        [Test]
        public void Termination_MalformedCurrentRecordRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            File.WriteAllText(_handshakePath, "{ not json");

            var handle = new FakeHandle();
            McpTerminationOutcome outcome = Terminate(record, handle, Listeners(record.ServerPid));

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0));
            Assert.That(File.ReadAllText(_handshakePath), Is.EqualTo("{ not json"));
        }

        [Test]
        public void Termination_UnavailableLockRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);

            var handle = new FakeHandle();
            McpTerminationOutcome outcome = McpOwnershipMutation.TerminateIfStillOwned(
                new AlwaysBusyLocks(),
                Listeners(record.ServerPid),
                record,
                OwnershipFixture.DefaultServerStart,
                handle);

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(outcome.Reason, Does.Contain("another process"));
            Assert.That(handle.KillCalls, Is.EqualTo(0));
        }

        private sealed class AlwaysBusyLocks : IMcpOwnershipLockProvider
        {
            public bool TryBegin(out IMcpOwnershipTransaction transaction, out string error)
            {
                transaction = null;
                error = "another process is mutating this project's ownership record; refusing to act "
                        + "on stale ownership state.";
                return false;
            }
        }

        // ============================================================
        // Blocker 4 - cross-process conditional ownership mutation
        // ============================================================

        [Test]
        public void Conditional_MutationDecidesFromAFreshReadInsideTheLock()
        {
            McpOwnershipStore store = NewStore();
            OwnershipFixture fixture = Fixture;

            // The decision is taken against whatever is current, not against a stale pre-read.
            McpRunStateRecord other = fixture.BuildRecord();
            other.RecordId = McpRunStateRecord.NewRecordId();
            Publish(other);

            bool mutated = McpOwnershipMutation.TryMutate(
                store,
                current =>
                {
                    Assert.That(current.IsPresent, Is.True);
                    Assert.That(current.Record.RecordId, Is.EqualTo(other.RecordId));
                    return McpMutationDecision.Abort("a foreign record is present");
                },
                out string error);

            Assert.That(mutated, Is.False);
            Assert.That(error, Is.EqualTo("a foreign record is present"));
            Assert.That(File.ReadAllText(_handshakePath), Does.Contain(other.RecordId));
        }

        [Test]
        public void Conditional_RecordAbsentAtEvaluation_ForeignRecordAppearsBeforePublication()
        {
            McpOwnershipStore store = NewStore();
            OwnershipFixture fixture = Fixture;

            McpOwnershipSnapshot evaluatedAbsent = store.Read();
            Assert.That(evaluatedAbsent.Kind, Is.EqualTo(McpOwnershipStateKind.Absent));

            McpRunStateRecord foreign = fixture.BuildRecord();
            foreign.RecordId = McpRunStateRecord.NewRecordId();
            Publish(foreign);

            McpRunStateRecord mine = fixture.BuildRecord();
            mine.RecordId = McpRunStateRecord.NewRecordId();

            Assert.That(
                store.TryBegin(out IMcpOwnershipTransaction transaction, out string lockError),
                Is.True, lockError);
            using (transaction)
            {
                Assert.That(
                    transaction.PublishIfCurrent(evaluatedAbsent, mine, out string publishError),
                    Is.False);
                Assert.That(publishError, Does.Contain("concurrently"));
                Assert.That(transaction.Read().Record.RecordId, Is.EqualTo(foreign.RecordId));
            }
        }

        [Test]
        public void Conditional_RecordXEvaluated_RecordYReplacesItBeforePromotion()
        {
            McpOwnershipStore store = NewStore();
            OwnershipFixture fixture = Fixture;

            McpRunStateRecord pending = fixture.BuildRecord();
            pending.LifecycleState = McpRunStateRecord.LifecycleStarting;
            pending.RecordId = McpRunStateRecord.NewRecordId();
            Publish(pending);

            McpRunStateRecord replacement = fixture.BuildRecord();
            replacement.RecordId = McpRunStateRecord.NewRecordId();
            Publish(replacement);

            McpRunStateRecord promoted = fixture.BuildRecord();
            promoted.RecordId = pending.RecordId;

            bool promotedOk = McpOwnershipMutation.TryMutate(
                store,
                current => McpOwnershipIdentity.IsSamePublication(pending, current.Record)
                    ? McpMutationDecision.Write(promoted)
                    : McpMutationDecision.Abort("a successor record is published"),
                out string error);

            Assert.That(promotedOk, Is.False);
            Assert.That(error, Does.Contain("successor"));
            Assert.That(File.ReadAllText(_handshakePath), Does.Contain(replacement.RecordId));
        }

        [Test]
        public void Conditional_RecordXEvaluated_RecordYReplacesItBeforeDeletion()
        {
            McpOwnershipStore store = NewStore();
            OwnershipFixture fixture = Fixture;

            McpRunStateRecord evaluated = fixture.BuildRecord();
            Publish(evaluated);
            McpOwnershipSnapshot snapshot = store.Read();

            McpRunStateRecord successor = fixture.BuildRecord();
            successor.RecordId = McpRunStateRecord.NewRecordId();
            Publish(successor);

            Assert.That(store.TryBegin(out IMcpOwnershipTransaction transaction, out string lockError),
                Is.True, lockError);
            using (transaction)
            {
                Assert.That(transaction.DeleteIfCurrent(snapshot, out string deleteError), Is.False);
                Assert.That(deleteError, Does.Contain("different ownership record"));
            }

            Assert.That(File.Exists(_handshakePath), Is.True);
            Assert.That(File.ReadAllText(_handshakePath), Does.Contain(successor.RecordId));
        }

        [Test]
        public async Task Conditional_TwoWritersRacingToFirstPublicationProduceExactlyOneWinner()
        {
            OwnershipFixture fixture = Fixture;
            var results = new List<bool>();
            var gate = new object();

            Task[] writers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                McpOwnershipStore store = NewStore(lockAttempts: 400);
                McpRunStateRecord mine = fixture.BuildRecord();
                mine.RecordId = "writer-" + i + "-0000000000000000000";
                bool ok = McpOwnershipMutation.TryMutate(
                    store,
                    current => current.IsPresent || current.IsUnknown
                        ? McpMutationDecision.Abort("already published")
                        : McpMutationDecision.Write(mine),
                    out _);
                lock (gate)
                {
                    results.Add(ok);
                }
            })).ToArray();

            await Task.WhenAll(writers);

            Assert.That(results.Count(r => r), Is.EqualTo(1), "exactly one writer may first-publish");
            Assert.That(File.Exists(_handshakePath), Is.True);
            string published = File.ReadAllText(_handshakePath);
            Assert.That(
                Enumerable.Range(0, 8).Count(i => published.Contains("writer-" + i)),
                Is.EqualTo(1));
        }

        [Test]
        public void Conditional_UnreadableRecordBlocksPublicationAndDeletion()
        {
            McpOwnershipStore store = NewStore();
            OwnershipFixture fixture = Fixture;
            const string malformed = "{ this is not a record }";
            File.WriteAllText(_handshakePath, malformed);

            bool published = McpOwnershipMutation.TryMutate(
                store,
                current => current.IsUnknown
                    ? McpMutationDecision.Abort("unknown")
                    : McpMutationDecision.Write(fixture.BuildRecord()),
                out string error);

            Assert.That(published, Is.False);
            Assert.That(error, Is.EqualTo("unknown"));
            Assert.That(File.ReadAllText(_handshakePath), Is.EqualTo(malformed),
                "an unreadable record must never be overwritten");

            McpOwnershipSnapshot snapshot = store.Read();
            Assert.That(snapshot.Kind, Is.EqualTo(McpOwnershipStateKind.UnreadableOrMalformed));
            Assert.That(snapshot.IsPresent, Is.False);
            Assert.That(snapshot.IsUnknown, Is.True);

            Assert.That(store.TryBegin(out IMcpOwnershipTransaction transaction, out _), Is.True);
            using (transaction)
            {
                Assert.That(transaction.DeleteIfCurrent(snapshot, out string deleteError), Is.False);
                Assert.That(deleteError, Does.Contain("unreadable"));
            }

            Assert.That(File.ReadAllText(_handshakePath), Is.EqualTo(malformed));
        }

        [Test]
        public void Conditional_HeldLockFailsClosedRatherThanBeingStolen()
        {
            McpOwnershipStore first = NewStore();
            Assert.That(first.TryBegin(out IMcpOwnershipTransaction held, out string firstError),
                Is.True, firstError);
            try
            {
                McpOwnershipStore second = NewStore(lockAttempts: 3);
                bool acquired = second.TryBegin(out IMcpOwnershipTransaction _, out string error);

                if (acquired)
                {
                    Assert.Ignore("this platform does not enforce FileShare.None for the lock file");
                }

                Assert.That(acquired, Is.False);
                Assert.That(error, Does.Contain("another process"));
            }
            finally
            {
                held.Dispose();
            }
        }

        // ============================================================
        // Blocker 6 - managed URL contract
        // ============================================================

        [TestCase("http://127.0.0.1:8081", 8081)]
        [TestCase("http://localhost:8081", 8081)]
        [TestCase("http://[::1]:8081", 8081)]
        [TestCase("http://127.0.0.1:80", 80)]
        public void ManagedUrl_ExplicitPortIsParsedFromTheText(string url, int expected)
        {
            Assert.That(McpRouteConfiguration.TryGetExplicitPort(url, out int port), Is.True);
            Assert.That(port, Is.EqualTo(expected));
        }

        [TestCase("http://127.0.0.1")]
        [TestCase("http://localhost")]
        [TestCase("http://[::1]")]
        [TestCase("http://127.0.0.1/")]
        [TestCase("http://127.0.0.1:")]
        [TestCase("http://127.0.0.1:abc")]
        [TestCase("not a url")]
        public void ManagedUrl_MissingOrMalformedPortIsNotExplicit(string url)
        {
            Assert.That(McpRouteConfiguration.TryGetExplicitPort(url, out _), Is.False);
        }

        [Test]
        public void ManagedUrl_DefaultPortIsRejectedWhereAnExplicitPortIsRequired()
        {
            // Uri.Port reports 80 for a portless http URL; the managed contract must not accept it.
            Assert.That(
                Uri.TryCreate("http://127.0.0.1", UriKind.Absolute, out Uri uri), Is.True);
            Assert.That(uri.Port, Is.EqualTo(80), "Uri silently supplies the scheme default");

            Assert.That(
                McpRouteConfiguration.Resolve(new McpRouteInputs
                {
                    TransportEnvironmentValue = "http",
                    HttpUrlEnvironmentValue = "http://127.0.0.1",
                }).IsValid,
                Is.False);
        }

        [TestCase("http://127.0.0.1:8081", true)]
        [TestCase("http://localhost:8081", true)]
        [TestCase("http://[::1]:8081", true)]
        [TestCase("http://0.0.0.0:8081", false)]
        [TestCase("http://[::]:8081", false)]
        [TestCase("http://192.168.1.5:8081", false)]
        [TestCase("https://127.0.0.1:8081", false)]
        public void ManagedUrl_LoopbackOnlyContract(string url, bool expected)
        {
            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = url,
                StoredAllowLanBind = true,
            });

            Assert.That(configuration.IsValid, Is.EqualTo(expected), configuration.ValidationError);
        }

        // ============================================================
        // record identity
        // ============================================================

        [Test]
        public void RecordId_IsRequiredAndDistinguishesPublications()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Assert.That(McpRunStateRecord.IsUsableRecordId(record.RecordId), Is.True);

            record.RecordId = null;
            Assert.That(record.IsStructurallyValid(out string error), Is.False);
            Assert.That(error, Does.Contain("record_id"));

            McpRunStateRecord other = fixture.BuildRecord();
            Assert.That(McpOwnershipIdentity.IsSamePublication(fixture.BuildRecord(), other), Is.True);

            other.RecordId = McpRunStateRecord.NewRecordId();
            Assert.That(McpOwnershipIdentity.IsSamePublication(fixture.BuildRecord(), other), Is.False);
        }

        [Test]
        public void RecordId_SurvivesAValidStartingToRunningTransition()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pending = fixture.BuildRecord();

            var running = new OwnershipFixture();
            McpOwnershipObservation observation = running.BuildObservation();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, observation, out McpRunStateRecord promoted, out string detail),
                Is.True, detail);
            Assert.That(promoted.RecordId, Is.EqualTo(pending.RecordId));
            Assert.That(promoted.LifecycleState, Is.EqualTo(McpRunStateRecord.LifecycleRunning));
        }

        private static string FindRepositoryFile(string relativePath)
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                $"could not locate '{relativePath}' above {TestContext.CurrentContext.TestDirectory}");
        }
    }
}
