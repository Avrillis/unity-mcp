using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MCPForUnity.Editor.Services.Route;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// C17-R3-FIX3 coverage for the five narrow gaps Sol found in the FIX2 checkpoint:
    ///
    ///   * blocker 1 - an explicit integer <c>depth: 0</c> is required, and the UPM query parser
    ///     rejects every duplicate / ambiguous spelling;
    ///   * blocker 2 - the final termination revalidation refreshes the live PID-file and nonce
    ///     evidence inside the critical section;
    ///   * blocker 3 - an unreadable ownership record is UNKNOWN, never ABSENT, and blocks
    ///     publication, replacement, promotion and deletion;
    ///   * blocker 5 - the managed host allowlist is exactly localhost / 127.0.0.1 / ::1.
    ///
    /// Every case runs against the production classes; only OS process observations are
    /// substituted.
    /// </summary>
    [TestFixture]
    public class McpFix3ProvenanceTests
    {
        private const string Package = "com.coplaydev.unity-mcp";
        private const string Repository = "https://github.com/Avrillis/unity-mcp.git";
        private const string Commit = "30d22075093d1d35dfb0091c1c7550e9ad948577";
        private const string ManifestValue = Repository + "?path=/MCPForUnity#" + Commit;

        private static string LockJson(string version, string depthJson)
        {
            string depth = depthJson == null ? string.Empty : "      \"depth\": " + depthJson + ",\n";
            return "{\n"
                   + "  \"dependencies\": {\n"
                   + "    \"" + Package + "\": {\n"
                   + "      \"version\": \"" + version + "\",\n"
                   + "      \"source\": \"git\",\n"
                   + depth
                   + "      \"hash\": \"" + Commit + "\"\n"
                   + "    }\n"
                   + "  }\n"
                   + "}";
        }

        private static McpServerPackageProvenance Provenance(string lockJson)
        {
            McpPackageManifestProvenance.TryRead(
                "{ \"dependencies\": { \"" + Package + "\": \"" + ManifestValue + "\" } }",
                Package,
                out McpManifestGitDependency manifest,
                out _);

            McpPackageLockProvenance.TryRead(lockJson, Package, out McpLockGitEntry lockEntry, out _);

            return new McpServerPackageProvenance
            {
                Installed = new McpInstalledPackageIdentity
                {
                    Name = Package,
                    SourceKind = "git",
                    ResolvedPath =
                        @"C:\proj\Library\PackageCache\com.coplaydev.unity-mcp@abc\MCPForUnity",
                    PackageJsonName = Package,
                    ResolvedPathExists = true,
                    ResolvedPathInsideProjectPackageCache = true,
                },
                Manifest = manifest,
                Lock = lockEntry,
            };
        }

        private static string RootLockUrl =>
            Repository + "?path=/MCPForUnity";

        // ------------------------------------------------------------------ lock depth

        [Test]
        public void LockDepth_ExplicitZeroIsAccepted()
        {
            Assert.That(
                McpPackageLockProvenance.TryRead(
                    LockJson(RootLockUrl, "0"), Package,
                    out McpLockGitEntry entry, out string error),
                Is.True, error);
            Assert.That(entry.Depth, Is.EqualTo(0));
        }

        [Test]
        public void LockDepth_ExplicitZeroProducesTheFullManagedSource()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(LockJson(RootLockUrl, "0")));

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Source, Is.EqualTo(
                "git+" + Repository + "@" + Commit + "#subdirectory=Server"));
        }

        [Test]
        public void LockDepth_MissingIsRefused()
        {
            Assert.That(
                McpPackageLockProvenance.TryRead(
                    LockJson(RootLockUrl, null), Package,
                    out McpLockGitEntry entry, out string error),
                Is.False);
            Assert.That(entry, Is.Null);
            Assert.That(error, Does.Contain("depth"));

            // The resolver must not invent a direct dependency from the manifest alone.
            Assert.That(
                McpServerSourceResolver.ResolveManaged(Provenance(LockJson(RootLockUrl, null)))
                    .IsResolved,
                Is.False);
        }

        [TestCase("1")]
        [TestCase("-1")]
        [TestCase("7")]
        public void LockDepth_NonZeroIsRefused(string depthJson)
        {
            Assert.That(
                McpPackageLockProvenance.TryRead(
                    LockJson(RootLockUrl, depthJson), Package,
                    out McpLockGitEntry entry, out string error),
                Is.False, "a transitive dependency must never authorize a managed route");
            Assert.That(entry, Is.Null);
            Assert.That(error, Does.Contain("depth"));
        }

        [TestCase("\"0\"")]
        [TestCase("\"zero\"")]
        [TestCase("null")]
        [TestCase("0.0")]
        [TestCase("false")]
        public void LockDepth_NonIntegerIsRefused(string depthJson)
        {
            Assert.That(
                McpPackageLockProvenance.TryRead(
                    LockJson(RootLockUrl, depthJson), Package,
                    out McpLockGitEntry entry, out string error),
                Is.False);
            Assert.That(entry, Is.Null);
            Assert.That(error, Does.Contain("depth"));
        }

        [Test]
        public void FloatingMainManifestIsRefusedEvenWithAFullLockHash()
        {
            // The exact defect Sol named: '#main' in the manifest plus a resolved full commit in
            // packages-lock.json must NOT authorize a managed route.
            McpPackageManifestProvenance.TryRead(
                "{ \"dependencies\": { \"" + Package + "\": \""
                + Repository + "?path=/MCPForUnity#main\" } }",
                Package,
                out McpManifestGitDependency manifest,
                out _);

            McpPackageLockProvenance.TryRead(
                LockJson(RootLockUrl, "0"), Package, out McpLockGitEntry lockEntry, out _);

            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                new McpServerPackageProvenance
                {
                    Installed = new McpInstalledPackageIdentity
                    {
                        Name = Package,
                        SourceKind = "git",
                        ResolvedPath =
                            @"C:\proj\Library\PackageCache\com.coplaydev.unity-mcp@abc\MCPForUnity",
                        PackageJsonName = Package,
                        ResolvedPathExists = true,
                        ResolvedPathInsideProjectPackageCache = true,
                    },
                    Manifest = manifest,
                    Lock = lockEntry,
                });

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("floating-or-malformed-revision"));
        }

        // ------------------------------------------------------------------ UPM query parsing

        [Test]
        public void UpmQuery_ExactlyOneApprovedPathParameterIsAccepted()
        {
            Assert.That(
                McpServerSourceResolver.TryParseUpmGitUrl(
                    ManifestValue, out string repository, out string subPath,
                    out string revision, out string error),
                Is.True, error);
            Assert.That(repository, Is.EqualTo(Repository));
            Assert.That(subPath, Is.EqualTo("/MCPForUnity"));
            Assert.That(revision, Is.EqualTo(Commit));

            // The lockfile records the pin without a fragment.
            Assert.That(
                McpServerSourceResolver.TryParseUpmGitUrl(
                    Repository + "?path=/MCPForUnity", out _, out string lockSubPath,
                    out _, out string lockError),
                Is.True, lockError);
            Assert.That(lockSubPath, Is.EqualTo("/MCPForUnity"));
        }

        [TestCase("duplicate-path-same-value",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity&path=/MCPForUnity#abc")]
        [TestCase("duplicate-path-different-value",
            "https://github.com/Avrillis/unity-mcp.git?path=/Other&path=/MCPForUnity#abc")]
        [TestCase("empty-then-path",
            "https://github.com/Avrillis/unity-mcp.git?path=&path=/MCPForUnity#abc")]
        [TestCase("duplicate-unsupported-key",
            "https://github.com/Avrillis/unity-mcp.git?foo=x&path=/MCPForUnity#abc")]
        [TestCase("unexpected-query-key",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity&revision=main#abc")]
        [TestCase("empty-path",
            "https://github.com/Avrillis/unity-mcp.git?path=#abc")]
        [TestCase("path-without-value-separator",
            "https://github.com/Avrillis/unity-mcp.git?path#abc")]
        [TestCase("empty-query",
            "https://github.com/Avrillis/unity-mcp.git?#abc")]
        [TestCase("trailing-empty-component",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity&#abc")]
        [TestCase("leading-empty-component",
            "https://github.com/Avrillis/unity-mcp.git?&path=/MCPForUnity#abc")]
        [TestCase("malformed-percent-escape",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity%ZZ#abc")]
        [TestCase("truncated-percent-escape",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCP%2#abc")]
        [TestCase("encoded-query-separator",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity%26path=/Other#abc")]
        [TestCase("encoded-value-separator",
            "https://github.com/Avrillis/unity-mcp.git?path=%3D%2FMCPForUnity#abc")]
        [TestCase("encoded-question-mark",
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity%3Fpath=/Other#abc")]
        public void UpmQuery_AmbiguousFormsAreRefused(string name, string url)
        {
            Assert.That(
                McpServerSourceResolver.TryParseUpmGitUrl(
                    url, out string repository, out string subPath,
                    out string revision, out string error),
                Is.False,
                $"'{name}' must fail closed");
            Assert.That(repository, Is.Null);
            Assert.That(subPath, Is.Null);
            Assert.That(revision, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }
    }

    /// <summary>
    /// Blocker 2: the final termination revalidation re-observes every mutable piece of live
    /// ownership evidence inside the cross-process critical section, immediately before the kill.
    /// </summary>
    [TestFixture]
    public class McpFix3TerminationTests
    {
        private string _directory;
        private string _handshakePath;

        private OwnershipFixture Fixture => new OwnershipFixture();

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "mcp-fix3-" + Guid.NewGuid().ToString("N"));
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

        private McpOwnershipStore NewStore()
            => new McpOwnershipStore(_handshakePath) { LockAttempts = 20, LockRetryDelayMs = 5 };

        private void Publish(McpRunStateRecord record)
        {
            Assert.That(
                McpRunStateFile.TryWriteAtomic(_handshakePath, record.ToJson(), out string error),
                Is.True, error);
        }

        private sealed class FakeHandle : IRetainedProcessHandle
        {
            public DateTime? Start { get; set; } = OwnershipFixture.DefaultServerStart;
            public int KillCalls { get; private set; }

            public bool TryGetStartTimeUtc(out DateTime startUtc)
            {
                startUtc = Start ?? default;
                return Start.HasValue;
            }

            public bool Kill()
            {
                KillCalls++;
                return true;
            }
        }

        private static Func<McpOwnershipObservation> Observing(
            OwnershipFixture fixture, Action<McpOwnershipObservation> perturb = null)
        {
            return () =>
            {
                McpOwnershipObservation observation = fixture.BuildObservation();
                perturb?.Invoke(observation);
                return observation;
            };
        }

        private McpTerminationOutcome Terminate(
            McpRunStateRecord evaluated,
            FakeHandle handle,
            Func<McpOwnershipObservation> reobserve)
            => McpOwnershipMutation.TerminateIfStillOwned(
                NewStore(),
                reobserve,
                evaluated,
                OwnershipFixture.DefaultServerStart,
                handle);

        private void AssertRefusedWithoutKill(
            OwnershipFixture fixture, Action<McpOwnershipObservation> perturb)
        {
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);
            var handle = new FakeHandle();

            McpTerminationOutcome outcome = Terminate(record, handle, Observing(fixture, perturb));

            Assert.That(outcome.Terminated, Is.False, outcome.Reason);
            Assert.That(handle.KillCalls, Is.EqualTo(0), "the process must be left untouched");
            Assert.That(File.Exists(_handshakePath), Is.True, "the record is left for its owner");
        }

        [Test]
        public void Termination_AllEvidenceUnchangedTerminatesThroughTheRetainedHandle()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);
            var handle = new FakeHandle();

            McpTerminationOutcome outcome = Terminate(record, handle, Observing(fixture));

            Assert.That(outcome.Terminated, Is.True, outcome.Reason);
            Assert.That(outcome.RecordRemoved, Is.True, outcome.Reason);
            Assert.That(handle.KillCalls, Is.EqualTo(1));
            Assert.That(File.Exists(_handshakePath), Is.False);
        }

        [Test]
        public void Termination_PidFileChangedAfterEvaluationRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation => observation.PidFilePid = OwnershipFixture.DefaultServerPid + 1);
        }

        [Test]
        public void Termination_PidFileNamesAnotherPidRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation => observation.PidFilePid = 999999);
        }

        [Test]
        public void Termination_PidFileDisappearedRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation =>
                {
                    observation.PidFileExists = false;
                    observation.PidFileReadable = false;
                    observation.PidFilePid = 0;
                });
        }

        [Test]
        public void Termination_PidFileUnreadableRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation =>
                {
                    observation.PidFileExists = true;
                    observation.PidFileReadable = false;
                });
        }

        [Test]
        public void Termination_PidFilePathChangedRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation => observation.PidFilePath = @"C:\elsewhere\mcp_http_8101.pid");
        }

        [Test]
        public void Termination_NonceRemovedFromTheCommandLineRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation => observation.ServerCommandLine =
                    $"uvx mcp-for-unity --transport http --http-url {observation.Endpoint}");
        }

        [Test]
        public void Termination_NonceChangedInTheCommandLineRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation => observation.ServerCommandLine =
                    observation.ServerCommandLine.Replace(
                        OwnershipFixture.DefaultNonce, "some-other-nonce"));
        }

        [Test]
        public void Termination_CommandLineUnavailableRefusesTheKill()
        {
            AssertRefusedWithoutKill(
                Fixture,
                observation =>
                {
                    observation.ServerCommandLineAvailable = false;
                    observation.ServerCommandLine = null;
                });
        }

        [Test]
        public void Termination_RecordNonceChangedRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord evaluated = fixture.BuildRecord();
            Publish(evaluated);

            McpRunStateRecord successor = fixture.BuildRecord();
            successor.RecordId = McpRunStateRecord.NewRecordId();
            successor.InstanceToken = "successor-nonce-1a2b3c4d";
            Publish(successor);

            var handle = new FakeHandle();
            McpTerminationOutcome outcome =
                Terminate(evaluated, handle, Observing(fixture));

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0));
            Assert.That(File.ReadAllText(_handshakePath), Does.Contain("successor-nonce-1a2b3c4d"));
        }

        [Test]
        public void Termination_MissingReobservationRefusesTheKill()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            Publish(record);
            var handle = new FakeHandle();

            McpTerminationOutcome outcome = McpOwnershipMutation.TerminateIfStillOwned(
                NewStore(),
                null,
                record,
                OwnershipFixture.DefaultServerStart,
                handle);

            Assert.That(outcome.Terminated, Is.False);
            Assert.That(handle.KillCalls, Is.EqualTo(0));
        }
    }

    /// <summary>
    /// Blocker 3: an ownership file that exists but cannot be read must be UNKNOWN, and UNKNOWN
    /// must block publication, replacement, promotion and deletion.
    /// </summary>
    [TestFixture]
    public class McpFix3OwnershipStateTests
    {
        private string _directory;
        private string _handshakePath;

        private OwnershipFixture Fixture => new OwnershipFixture();

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "mcp-fix3-state-" + Guid.NewGuid().ToString("N"));
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

        private McpOwnershipStore NewStore()
            => new McpOwnershipStore(_handshakePath) { LockAttempts = 20, LockRetryDelayMs = 5 };

        private McpRunStateRecord ReplacementRecord(OwnershipFixture fixture)
        {
            McpRunStateRecord replacement = fixture.BuildRecord();
            replacement.RecordId = McpRunStateRecord.NewRecordId();
            return replacement;
        }

        // ------------------------------------------------------------------ classification

        [Test]
        public void OwnershipRead_GenuinelyAbsentRecordIsAbsent()
        {
            McpOwnershipSnapshot snapshot = NewStore().Read();

            Assert.That(snapshot.Kind, Is.EqualTo(McpOwnershipStateKind.Absent));
            Assert.That(snapshot.IsUnknown, Is.False);
        }

        [Test]
        public void OwnershipRead_MissingRunStateDirectoryIsAbsent()
        {
            string missing = Path.Combine(_directory, "nested", McpRunStatePaths.HandshakeFileName);
            McpOwnershipSnapshot snapshot =
                new McpOwnershipStore(missing) { LockAttempts = 5, LockRetryDelayMs = 1 }.Read();

            Assert.That(snapshot.Kind, Is.EqualTo(McpOwnershipStateKind.Absent));
        }

        [Test]
        public void OwnershipRead_ValidRecordIsPresent()
        {
            OwnershipFixture fixture = Fixture;
            McpRunStateRecord record = fixture.BuildRecord();
            File.WriteAllText(_handshakePath, record.ToJson(), Encoding.UTF8);

            McpOwnershipSnapshot snapshot = NewStore().Read();

            Assert.That(snapshot.Kind, Is.EqualTo(McpOwnershipStateKind.Present));
            Assert.That(snapshot.Record.RecordId, Is.EqualTo(record.RecordId));
        }

        [Test]
        public void OwnershipRead_MalformedRecordIsUnknown()
        {
            File.WriteAllText(_handshakePath, "{ not json", Encoding.UTF8);

            McpOwnershipSnapshot snapshot = NewStore().Read();

            Assert.That(snapshot.Kind, Is.EqualTo(McpOwnershipStateKind.UnreadableOrMalformed));
            Assert.That(snapshot.IsPresent, Is.False);
            Assert.That(snapshot.IsUnknown, Is.True);
        }

        [Test]
        public void OwnershipRead_AccessDeniedPathIsUnknownNotAbsent()
        {
            // A directory occupying the record path makes the open fail with an access error on
            // Windows - exactly the case where File.Exists would have reported "absent".
            Directory.CreateDirectory(_handshakePath);

            McpOwnershipSnapshot snapshot = NewStore().Read();

            Assert.That(snapshot.IsUnknown, Is.True,
                "an unreadable existing path must never be classified as absent");
            Assert.That(snapshot.IsPresent, Is.False);
        }

        [Test]
        public void OwnershipRead_SharingViolationIsUnknownNotAbsent()
        {
            File.WriteAllText(_handshakePath, Fixture.BuildRecord().ToJson(), Encoding.UTF8);

            using (new FileStream(_handshakePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                McpOwnershipSnapshot snapshot = NewStore().Read();

                Assert.That(snapshot.IsUnknown, Is.True,
                    "a sharing violation means the record could not be observed, not that it is gone");
                Assert.That(snapshot.IsPresent, Is.False);
            }
        }

        [Test]
        public void OwnershipRead_RecordRemovedDuringReadIsSafelyAbsent()
        {
            File.WriteAllText(_handshakePath, Fixture.BuildRecord().ToJson(), Encoding.UTF8);
            File.Delete(_handshakePath);

            McpOwnershipSnapshot snapshot = NewStore().Read();

            Assert.That(snapshot.Kind, Is.EqualTo(McpOwnershipStateKind.Absent));
        }

        // ------------------------------------------------------------------ blocking

        private McpOwnershipStore UnreadableStore()
        {
            Directory.CreateDirectory(_handshakePath);
            return NewStore();
        }

        [Test]
        public void InaccessibleRecord_BlocksFirstPublication()
        {
            McpOwnershipStore store = UnreadableStore();
            McpRunStateRecord replacement = ReplacementRecord(Fixture);

            bool mutated = McpOwnershipMutation.TryMutate(
                store,
                _ => McpMutationDecision.Write(replacement),
                out string error);

            Assert.That(mutated, Is.False, "an unreadable record must block a first publication");
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(Directory.Exists(_handshakePath), Is.True, "the unreadable path is untouched");
        }

        [Test]
        public void InaccessibleRecord_BlocksReplacement()
        {
            McpOwnershipStore store = UnreadableStore();
            McpRunStateRecord replacement = ReplacementRecord(Fixture);

            bool published = store.PublishIfCurrent(
                expected: null, replacement, out string error);

            Assert.That(published, Is.False);
            Assert.That(error, Does.Contain("unreadable"));
        }

        [Test]
        public void InaccessibleRecord_BlocksPromotion()
        {
            // The production promotion guard aborts whenever the record is not present, and it
            // distinguishes "unreadable" from "gone". Promotion is therefore impossible unless the
            // store reports UNKNOWN here rather than ABSENT.
            McpOwnershipStore store = UnreadableStore();

            McpOwnershipSnapshot snapshot = store.Read();

            Assert.That(snapshot.IsPresent, Is.False);
            Assert.That(snapshot.IsUnknown, Is.True);

            bool mutated = McpOwnershipMutation.TryMutate(
                store,
                current => McpMutationDecision.Abort(
                    current.IsUnknown
                        ? "the ownership record is unreadable; refusing to promote it."
                        : "the pending ownership record is gone; refusing to promote it."),
                out string error);

            Assert.That(mutated, Is.False);
            Assert.That(error, Does.Contain("unreadable"));
        }

        [Test]
        public void InaccessibleRecord_BlocksDeletion()
        {
            McpOwnershipStore store = UnreadableStore();

            bool mutated = McpOwnershipMutation.TryMutate(
                store,
                _ => McpMutationDecision.Remove(),
                out string error);

            Assert.That(mutated, Is.False, "an unreadable record must never be deleted");
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(Directory.Exists(_handshakePath), Is.True);
        }

        [Test]
        public void InaccessibleRecord_BlocksDirectDelete()
        {
            McpOwnershipStore store = UnreadableStore();

            bool deleted = store.DeleteIfCurrent(expected: null, out string error);

            Assert.That(deleted, Is.False);
            Assert.That(error, Does.Contain("unreadable"));
            Assert.That(Directory.Exists(_handshakePath), Is.True);
        }
    }

    /// <summary>
    /// Blocker 5: the managed host allowlist is exactly localhost / 127.0.0.1 / ::1. The legacy
    /// unmanaged validator keeps its wider loopback behaviour.
    /// </summary>
    [TestFixture]
    public class McpFix3ManagedHostTests
    {
        [TestCase("http://localhost:8081")]
        [TestCase("http://127.0.0.1:8081")]
        [TestCase("http://[::1]:8081")]
        [TestCase("http://LOCALHOST:8081")]
        // Host spellings that canonicalize to 127.0.0.1 are deliberately accepted.
        [TestCase("http://127.1:8081")]
        [TestCase("http://0177.0.0.1:8081")]
        [TestCase("http://2130706433:8081")]
        public void ManagedUrl_ApprovedHostsAreAccepted(string url)
        {
            Assert.That(
                McpRouteConfiguration.TryValidateManagedHttpUrl(
                    url, out string normalized, out string error),
                Is.True, error);
            Assert.That(normalized, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("http://127.0.0.2:8081")]
        [TestCase("http://127.1.2.3:8081")]
        [TestCase("http://127.255.255.254:8081")]
        [TestCase("http://0.0.0.0:8081")]
        [TestCase("http://[::]:8081")]
        [TestCase("http://192.168.1.2:8081")]
        [TestCase("http://10.0.0.7:8081")]
        [TestCase("http://172.16.5.5:8081")]
        [TestCase("http://example.com:8081")]
        [TestCase("http://localhost.example.com:8081")]
        [TestCase("http://localhost")]
        [TestCase("http://127.0.0.1")]
        [TestCase("https://127.0.0.1:8081")]
        public void ManagedUrl_EverythingElseIsRefused(string url)
        {
            Assert.That(
                McpRouteConfiguration.TryValidateManagedHttpUrl(
                    url, out string normalized, out string error),
                Is.False, $"'{url}' must not be an approved managed endpoint");
            Assert.That(normalized, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ManagedHostCheck_IsExact()
        {
            Assert.That(McpRouteConfiguration.IsManagedLoopbackHost("127.0.0.1"), Is.True);
            Assert.That(McpRouteConfiguration.IsManagedLoopbackHost("localhost"), Is.True);
            Assert.That(McpRouteConfiguration.IsManagedLoopbackHost("[::1]"), Is.True);

            Assert.That(McpRouteConfiguration.IsManagedLoopbackHost("127.0.0.2"), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedLoopbackHost("127.0.0.1 "), Is.True,
                "surrounding whitespace is normalized, not treated as a different host");
        }

        [Test]
        public void LegacyValidator_KeepsItsWiderLoopbackBehaviour()
        {
            // The unmanaged path is unchanged: it is the legacy behaviour, not the managed rule.
            Assert.That(
                McpRouteConfiguration.TryValidateLocalHttpUrl(
                    "http://127.0.0.2:8081", allowLanBind: false,
                    out string normalized, out string error),
                Is.True, error);
            Assert.That(normalized, Is.EqualTo("http://127.0.0.2:8081"));
            Assert.That(McpRouteConfiguration.IsLoopbackHost("127.0.0.2"), Is.True);
        }
    }
}
