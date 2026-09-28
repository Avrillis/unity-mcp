using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Route;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// Production-adapter tests for the C17-R3-FIX1 blockers that the pure decision-class
    /// fixtures could not reach: atomic state publication, pending classification, the connection
    /// gate, retained termination identity, managed server source resolution, environment
    /// sanitization, client route configuration and guarded launch argument construction.
    ///
    /// The sources compiled here ARE the production implementations; only their Unity-facing
    /// shells (Application / EditorPrefs / PackageInfo) are substituted.
    /// </summary>
    [TestFixture]
    public class McpProductionAdapterTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "mcp-route-adapter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
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

        private string RecordPath => Path.Combine(_directory, "handshake.json");

        // ============================================================
        // 1. State store: atomic publication and replacement
        // ============================================================

        [Test]
        public void StateStore_FirstPublicationIsAtomicAndReadable()
        {
            Assert.That(
                McpRunStateFile.TryWriteAtomic(RecordPath, "{\"v\":1}", out string error),
                Is.True, error);

            Assert.That(File.ReadAllText(RecordPath), Is.EqualTo("{\"v\":1}"));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void StateStore_ReplacementLeavesExactlyOneCompleteRecord()
        {
            Assert.That(McpRunStateFile.TryWriteAtomic(RecordPath, "{\"v\":1}", out _), Is.True);
            Assert.That(
                McpRunStateFile.TryWriteAtomic(RecordPath, "{\"v\":2}", out string error),
                Is.True, error);

            Assert.That(File.ReadAllText(RecordPath), Is.EqualTo("{\"v\":2}"));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void StateStore_FailedReplacementNeverRemovesTheExistingRecord()
        {
            Assert.That(McpRunStateFile.TryWriteAtomic(RecordPath, "{\"v\":1}", out _), Is.True);

            bool published;
            using (new FileStream(RecordPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                published = McpRunStateFile.TryWriteAtomic(RecordPath, "{\"v\":2}", out string error);
                if (!published)
                {
                    Assert.That(error, Is.Not.Null.And.Not.Empty);
                }
            }

            // The record is never left missing or half-written: a complete record always exists.
            Assert.That(File.Exists(RecordPath), Is.True);
            Assert.That(
                File.ReadAllText(RecordPath),
                Is.EqualTo(published ? "{\"v\":2}" : "{\"v\":1}"));

            if (OperatingSystem.IsWindows())
            {
                Assert.That(
                    published, Is.False,
                    "a locked record must not be replaceable on Windows");
            }

            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void StateStore_TempNamesAreUniquePerWriter()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < 200; i++)
            {
                names.Add(McpRunStateFile.CreateUniqueTempPath(_directory));
            }

            Assert.That(names.Count, Is.EqualTo(200));
            foreach (string name in names)
            {
                Assert.That(name, Does.StartWith(RecordPath));
                Assert.That(name, Does.EndWith(McpRunStateFile.TempFileSuffix));
            }
        }

        [Test]
        public async Task StateStore_ConcurrentWritersNeverShareATempName()
        {
            Task<bool>[] writers = Enumerable.Range(0, 8)
                .Select(i => Task.Run(() =>
                    McpRunStateFile.TryWriteAtomic(RecordPath, "{\"writer\":" + i + "}", out _)))
                .ToArray();

            bool[] results = await Task.WhenAll(writers);

            Assert.That(results.Any(ok => ok), Is.True, "at least one concurrent write must succeed");
            Assert.That(File.Exists(RecordPath), Is.True);

            string content = File.ReadAllText(RecordPath);
            Assert.That(
                Enumerable.Range(0, 8).Any(i => content == "{\"writer\":" + i + "}"),
                Is.True,
                "the published record must be exactly one writer's content, was '" + content + "'");

            Assert.That(
                Directory.GetFiles(_directory, "*.tmp"), Is.Empty,
                "each writer cleans up its own temporary file");
        }

        [Test]
        public void StateStore_AnOldTempFileIsNeverReclaimedOnAgeAlone()
        {
            // Six hours of existence does not prove a temporary file's writer has exited, so an
            // unrelated abandoned temporary is left in place and never removed by another writer.
            string oldTemp = McpRunStateFile.CreateUniqueTempPath(_directory);
            File.WriteAllText(oldTemp, "stale");
            File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow - TimeSpan.FromDays(1));

            string freshTemp = McpRunStateFile.CreateUniqueTempPath(_directory);
            File.WriteAllText(freshTemp, "in flight");

            Assert.That(McpRunStateFile.TryWriteAtomic(RecordPath, "{\"v\":1}", out _), Is.True);

            Assert.That(File.Exists(oldTemp), Is.True, "age alone never authorises reclaiming a temp");
            Assert.That(File.Exists(freshTemp), Is.True);
            Assert.That(File.ReadAllText(RecordPath), Is.EqualTo("{\"v\":1}"));
        }

        [Test]
        public void StateStore_UnusableRecordIsNeverTreatedAsValidNorReplaceable()
        {
            var fixture = new OwnershipFixture();

            Assert.That(McpRunStateRecord.TryParse("{not json", out _, out string parseError), Is.False);
            Assert.That(parseError, Is.Not.Null.And.Not.Empty);

            McpRunStateRecord missingWriteTime = fixture.BuildRecord();
            missingWriteTime.WrittenUtc = null;
            Assert.That(missingWriteTime.IsStructurallyValid(out string structuralError), Is.False);
            Assert.That(structuralError, Does.Contain("written_utc"));

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                missingWriteTime, fixture.BuildObservation(), out string detail);
            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.Unknown));
            Assert.That(detail, Does.Contain("unknown"));
        }

        // ============================================================
        // 2. Pending ownership classification
        // ============================================================

        private static McpOwnershipObservation PendingObservation(OwnershipFixture fixture)
        {
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerPid = OwnershipFixture.DefaultServerPid;
            return observation;
        }

        [Test]
        public void Pending_AnotherLifetimePendingRecordIsNeverOverwrittenWithoutAListener()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pendingRecord = fixture.BuildRecord();

            McpOwnershipObservation observation = PendingObservation(fixture);
            observation.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1000;
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddMinutes(5);
            observation.ListeningProcessIds = new List<int>();
            observation.ServerProcessExists = false;
            observation.ServerProcessExistenceKnown = true;
            observation.RecordedEditorProcessExists = true;

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                pendingRecord, observation, out string detail);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
            Assert.That(detail, Does.Contain("pending"));
        }

        [Test]
        public void Pending_AnotherLifetimePendingRecordIsNeverOverwrittenWithoutAPidFile()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpRunStateRecord pendingRecord = fixture.BuildRecord();

            McpOwnershipObservation observation = PendingObservation(fixture);
            observation.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1000;
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddMinutes(5);
            observation.PidFileExists = false;
            observation.PidFileReadable = false;
            observation.PidFilePid = 0;
            observation.ServerPid = 0;
            observation.RecordedEditorProcessExists = true;

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                pendingRecord, observation, out _);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
        }

        [Test]
        public void Pending_TemporarilyUnobservableProcessIsNotStale()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord running = fixture.BuildRecord();

            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerProcessExists = false;
            observation.ServerProcessExistenceKnown = false;
            observation.ServerProcessLifetimeAvailable = false;
            observation.ServerProcessStartUtc = null;
            observation.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1000;
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddMinutes(5);
            observation.RecordedEditorProcessExists = true;

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                running, observation, out string detail);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
            Assert.That(detail, Does.Contain("refusing to adopt"));
        }

        [Test]
        public void Pending_OwnPendingRecordIsOursToReuseOrReplace()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStarting };
            McpOwnershipObservation observation = PendingObservation(fixture);

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                fixture.BuildRecord(), observation, out string detail);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.OwnedByCurrentLifetime));
            Assert.That(detail, Does.Contain("this editor lifetime"));
        }

        [Test]
        public void Pending_GoneServerOwnedByALiveEditorIsStillNotStale()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord foreign = fixture.BuildRecord();

            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.CurrentEditorPid = OwnershipFixture.DefaultEditorPid + 1000;
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddMinutes(5);
            observation.ServerProcessExists = false;
            observation.ServerProcessExistenceKnown = true;
            observation.ListeningProcessIds = new List<int>();
            observation.RecordedEditorProcessExists = true;

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                foreign, observation, out _);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
        }

        // ============================================================
        // 3. Connection gate (launch nonce)
        // ============================================================

        [Test]
        public void Gate_CoherentOwnershipReleasesTheNonce()
        {
            var fixture = new OwnershipFixture();

            bool allowed = McpManagedConnectionGate.TryGetLaunchToken(
                fixture.BuildRecord(),
                fixture.BuildObservation(),
                out string token,
                out McpOwnershipDenyReason reason,
                out string detail);

            Assert.That(allowed, Is.True, detail);
            Assert.That(token, Is.EqualTo(OwnershipFixture.DefaultNonce));
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.None));
        }

        [Test]
        public void Gate_CopiedRecordCannotProvideANonce_EditorStartMismatch()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddMinutes(3);

            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    fixture.BuildRecord(), observation, out string token,
                    out McpOwnershipDenyReason reason, out _),
                Is.False);
            Assert.That(token, Is.Null);
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEditorLifetime));
        }

        [Test]
        public void Gate_CopiedRecordCannotProvideANonce_ForeignEndpoint()
        {
            var fixture = new OwnershipFixture();
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.Endpoint = "http://127.0.0.1:8202";

            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    fixture.BuildRecord(), observation, out string token,
                    out McpOwnershipDenyReason reason, out _),
                Is.False);
            Assert.That(token, Is.Null);
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEndpoint));
        }

        [Test]
        public void Gate_WrongLifecycleCannotProvideANonce()
        {
            var fixture = new OwnershipFixture { LifecycleState = McpRunStateRecord.LifecycleStopping };
            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.ServerPid = fixture.ServerPid;

            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    fixture.BuildRecord(), observation, out string token,
                    out McpOwnershipDenyReason reason, out _),
                Is.False);
            Assert.That(token, Is.Null);
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.IncompleteRecord));
        }

        [Test]
        public void Gate_CopiedHandshakeFromAnotherProjectNeverProvidesANonce()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord copied = fixture.BuildRecord();

            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    copied, fixture.BuildObservation(), out _, out _, out _),
                Is.True);

            copied.CanonicalProjectRoot = McpRunStatePaths.Canonicalize(OwnershipFixture.WorkerOneRoot);
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    copied, fixture.BuildObservation(), out string token,
                    out McpOwnershipDenyReason reason, out _),
                Is.False);
            Assert.That(token, Is.Null);
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.ForeignProject));
        }

        [Test]
        public void Gate_NoRecordAndMismatchedNonceFailClosed()
        {
            var fixture = new OwnershipFixture();

            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    null, fixture.BuildObservation(), out string token,
                    out McpOwnershipDenyReason reason, out _),
                Is.False);
            Assert.That(token, Is.Null);
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.NoRecord));

            McpRunStateRecord record = fixture.BuildRecord();
            record.InstanceToken = "a-different-nonce";
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    record, fixture.BuildObservation(), out _, out reason, out _),
                Is.False);
            Assert.That(reason, Is.EqualTo(McpOwnershipDenyReason.NonceMismatch));
        }

        [Test]
        public void Gate_InvalidProjectConfigurationNeverProvidesANonce()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord record = fixture.BuildRecord();
            record.CanonicalProjectRoot = "not-a-real-project-root";

            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    record, fixture.BuildObservation(), out string token, out _, out _),
                Is.False);
            Assert.That(token, Is.Null);
        }

        // ============================================================
        // 4. Termination identity (PID reuse / TOCTOU)
        // ============================================================

        private sealed class FakeProcessHandle : IRetainedProcessHandle
        {
            private readonly DateTime? _start;
            private readonly bool _killResult;

            public FakeProcessHandle(DateTime? start, bool killResult = true)
            {
                _start = start;
                _killResult = killResult;
            }

            public int KillCalls { get; private set; }

            public bool TryGetStartTimeUtc(out DateTime startUtc)
            {
                startUtc = _start ?? default;
                return _start.HasValue;
            }

            public bool Kill()
            {
                KillCalls++;
                return _killResult;
            }
        }

        [Test]
        public void Termination_ValidatedLifetimeIsTerminated()
        {
            var fixture = new OwnershipFixture();
            var handle = new FakeProcessHandle(fixture.ServerStart);

            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    fixture.BuildRecord(), fixture.ServerStart, handle, out string error),
                Is.True, error);
            Assert.That(handle.KillCalls, Is.EqualTo(1));
        }

        [Test]
        public void Termination_PidReusedByAnotherProcessIsNeverKilled()
        {
            var fixture = new OwnershipFixture();

            var reused = new FakeProcessHandle(fixture.ServerStart.AddSeconds(30));
            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    fixture.BuildRecord(), fixture.ServerStart, reused, out string error),
                Is.False);
            Assert.That(error, Does.Contain("PID reuse"));
            Assert.That(reused.KillCalls, Is.EqualTo(0));

            var nearly = new FakeProcessHandle(fixture.ServerStart.AddMilliseconds(1));
            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    fixture.BuildRecord(), fixture.ServerStart, nearly, out _),
                Is.False);
            Assert.That(nearly.KillCalls, Is.EqualTo(0));
        }

        [Test]
        public void Termination_ExitedProcessIsNeverKilled()
        {
            var fixture = new OwnershipFixture();
            var exited = new FakeProcessHandle(start: null);

            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    fixture.BuildRecord(), fixture.ServerStart, exited, out string error),
                Is.False);
            Assert.That(error, Does.Contain("could not be read"));
            Assert.That(exited.KillCalls, Is.EqualTo(0));
        }

        [Test]
        public void Termination_NoRetainedIdentityMeansNoKill()
        {
            var fixture = new OwnershipFixture();

            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    fixture.BuildRecord(), fixture.ServerStart, null, out string error),
                Is.False);
            Assert.That(error, Does.Contain("by PID alone"));
        }

        [Test]
        public void Termination_RecordWithoutServerPidIsRefused()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord record = fixture.BuildRecord();
            record.ServerPid = 0;

            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    record, fixture.ServerStart,
                    new FakeProcessHandle(fixture.ServerStart), out string error),
                Is.False);
            Assert.That(error, Does.Contain("no server PID"));
        }

        [Test]
        public void Termination_FailedKillIsReported()
        {
            var fixture = new OwnershipFixture();
            var handle = new FakeProcessHandle(fixture.ServerStart, killResult: false);

            Assert.That(
                McpTerminationIdentity.TryTerminate(
                    fixture.BuildRecord(), fixture.ServerStart, handle, out string error),
                Is.False);
            Assert.That(error, Does.Contain("could not be terminated"));
            Assert.That(handle.KillCalls, Is.EqualTo(1));
        }

        // ============================================================
        // 5. Managed server source resolution
        // ============================================================

        private const string ForkRepository = "https://github.com/Avrillis/unity-mcp.git";
        private const string ForkCommit = "30d22075093d1d35dfb0091c1c7550e9ad948577";
        private const string ExpectedSource =
            "git+https://github.com/Avrillis/unity-mcp.git@30d22075093d1d35dfb0091c1c7550e9ad948577#subdirectory=Server";

        private const string ManifestValue =
            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#" + ForkCommit;

        /// <summary>
        /// Builds a fully coherent provenance set (executing package + manifest pin + lock entry)
        /// that individual tests then perturb one corroborating source at a time.
        /// </summary>
        private static McpServerPackageProvenance Provenance(
            string installedName = McpServerSourceResolver.ApprovedPackageName,
            string sourceKind = "git",
            string packageJsonName = McpServerSourceResolver.ApprovedPackageName,
            string resolvedPath =
                @"C:\proj\Library\PackageCache\com.coplaydev.unity-mcp@abc\MCPForUnity",
            bool resolvedPathExists = true,
            bool insidePackageCache = true,
            bool hasManifestEntry = true,
            bool directManifest = true,
            string manifestPackageName = McpServerSourceResolver.ApprovedPackageName,
            string manifestValue = ManifestValue,
            bool hasLockEntry = true,
            string lockSourceKind = "git",
            string lockRepository = ForkRepository,
            string lockRevision = ForkCommit,
            int lockDepth = 0)
        {
            return new McpServerPackageProvenance
            {
                Installed = new McpInstalledPackageIdentity
                {
                    Name = installedName,
                    SourceKind = sourceKind,
                    ResolvedPath = resolvedPath,
                    PackageJsonName = packageJsonName,
                    ResolvedPathExists = resolvedPathExists,
                    ResolvedPathInsideProjectPackageCache = insidePackageCache,
                },
                Manifest = hasManifestEntry
                    ? new McpManifestGitDependency
                    {
                        PackageName = manifestPackageName,
                        RawValue = manifestValue,
                        IsDirectDependency = directManifest,
                    }
                    : null,
                Lock = hasLockEntry
                    ? new McpLockGitEntry
                    {
                        PackageName = McpServerSourceResolver.ApprovedPackageName,
                        SourceKind = lockSourceKind,
                        RawVersion = lockRepository + "?path=/MCPForUnity#main",
                        Revision = lockRevision,
                        Depth = lockDepth,
                    }
                    : null,
            };
        }

        [Test]
        public void SourceResolver_PackageAndServerComeFromTheSameRepositoryAndCommit()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(Provenance());

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Source, Is.EqualTo(ExpectedSource));
            Assert.That(resolution.Repository, Is.EqualTo(ForkRepository));
            Assert.That(resolution.Revision, Is.EqualTo(ForkCommit));
        }

        // ---------------------------------------------------------------
        // Blocker 1: every independent provenance source must agree.
        // ---------------------------------------------------------------

        [Test]
        public void Provenance_FloatingManifestIsRefusedEvenWhenTheLockHasAFullHash()
        {
            // The exact defect Sol found: a floating '#main' manifest with a resolved full
            // commit in packages-lock.json must NOT authorize a managed route.
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    manifestValue: "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#main",
                    lockRevision: ForkCommit));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("floating-or-malformed-revision"));
        }

        [TestCase("https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity")]          // missing revision
        [TestCase("https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#main")]      // branch
        [TestCase("https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#v10.2.0")]   // tag
        [TestCase("https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075")]  // short id
        [TestCase("https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#")]
        public void Provenance_UnpinnedManifestIsRefused(string manifestValue)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(manifestValue: manifestValue));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Provenance_MissingManifestEntryIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(hasManifestEntry: false));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("missing-manifest-dependency"));
        }

        [Test]
        public void Provenance_IndirectOnlyManifestDependencyIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(directManifest: false));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("indirect-manifest-dependency"));
        }

        [Test]
        public void Provenance_WrongManifestPackageNameIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(manifestPackageName: "com.someone.else"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("indirect-manifest-dependency"));
        }

        [TestCase("com.someone.else")]
        public void Provenance_WrongExecutingPackageIsRefused(string installedName)
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(installedName: installedName));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("unexpected-package"));
        }

        [Test]
        public void Provenance_PackageJsonDisagreementIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(packageJsonName: "com.someone.else"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("installed-metadata-mismatch"));
        }

        [TestCase("/OtherPath")]
        [TestCase("/server")]
        [TestCase("")]
        public void Provenance_WrongSubpathIsRefused(string subPath)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    manifestValue:
                        "https://github.com/Avrillis/unity-mcp.git?path=" + subPath + "#" + ForkCommit));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
        }

        [Test]
        public void Provenance_MissingPathQueryIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    manifestValue: "https://github.com/Avrillis/unity-mcp.git#" + ForkCommit));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("unexpected-subpath"));
        }

        [Test]
        public void Provenance_ManifestAndLockRevisionDisagreementIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(lockRevision: "c1ca730ed77946c4fc9895d12ab1e00c4bdff0a5"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("revision-mismatch"));
        }

        [Test]
        public void Provenance_ManifestAndLockRepositoryDisagreementIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(lockRepository: "https://github.com/CoplayDev/unity-mcp.git"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("repository-mismatch"));
        }

        [TestCase("https://user:secret-token@github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("https://token@github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        public void Provenance_CredentialBearingHttpsUrlIsRefusedAndNeverEchoed(string manifestValue)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(manifestValue: manifestValue));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Error, Does.Not.Contain("secret-token"));
            Assert.That(resolution.Error, Does.Not.Contain("token@"));
        }

        [TestCase("git@github.com:Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("ssh://git@github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("ssh://user:password@github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("git://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("http://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        public void Provenance_NonApprovedRepositoryFormsAreRefused(string manifestValue)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(manifestValue: manifestValue));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
        }

        [TestCase("not a url at all")]
        [TestCase("https://github.com/Avrillis/unity-mcp/tree/main#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("https://127.0.0.1/Avrillis/unity-mcp.git?path=/MCPForUnity#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        [TestCase("https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity&depth=1#30d22075093d1d35dfb0091c1c7550e9ad948577")]
        public void Provenance_MalformedRepositoryIsRefused(string manifestValue)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(manifestValue: manifestValue));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Provenance_InstalledPathOutsideThePackageCacheIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(insidePackageCache: false));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [Test]
        public void Provenance_ResolvedPathWhoseLeafIsNotMCPForUnityIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(
                    Provenance(resolvedPath: @"C:\proj\Library\PackageCache\com.coplaydev.unity-mcp@abc"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [Test]
        public void Provenance_ResolvedPathOutsideThePackageCacheIsRefusedEvenWithTheRightLeaf()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath: @"C:\proj\Assets\SomeOtherFolder\MCPForUnity",
                    insidePackageCache: false));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [Test]
        public void Provenance_DisappearedResolvedPathIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(resolvedPathExists: false));

            Assert.That(resolution.IsResolved, Is.False);
        }

        [Test]
        public void Provenance_NonGitLockEntryIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(lockSourceKind: "embedded"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("non-git-lock-entry"));
        }

        [Test]
        public void Provenance_TransitiveLockEntryIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(lockDepth: 1));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("indirect-lock-entry"));
        }

        [Test]
        public void Provenance_MissingLockEntryIsRefused()
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(hasLockEntry: false));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("missing-lock-entry"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("main")]
        [TestCase("v10.2.0")]
        [TestCase("30d22075")]
        [TestCase("c1ca730ed77946c4fc9895d12ab1e00c4bdff0a5-extra")]
        [TestCase("30d22075093d1d35dfb0091c1c7550e9ad94857g")]
        public void SourceResolver_FloatingOrMalformedRevisionIsRefused(string revision)
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(
                    Provenance(
                        manifestValue: "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#"
                                       + revision));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("registry")]
        [TestCase("local")]
        [TestCase("embedded")]
        public void SourceResolver_NonGitPackageIsRefused(string sourceKind)
        {
            McpServerSourceResolution resolution =
                McpServerSourceResolver.ResolveManaged(Provenance(sourceKind: sourceKind));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("non-git-package"));
        }

        [Test]
        public void SourceResolver_MissingProvenanceIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(null);

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("missing-provenance"));
            Assert.That(resolution.Error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void SourceResolver_RevisionIsNormalizedToLowerCase()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    manifestValue: "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#"
                                   + ForkCommit.ToUpperInvariant(),
                    lockRevision: ForkCommit.ToUpperInvariant()));

            Assert.That(resolution.IsResolved, Is.True);
            Assert.That(resolution.Revision, Is.EqualTo(ForkCommit));
        }

        private const string LockJson =
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.coplaydev.unity-mcp\": {\n" +
            "      \"version\": \"https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#main\",\n" +
            "      \"depth\": 0,\n" +
            "      \"source\": \"git\",\n" +
            "      \"dependencies\": {},\n" +
            "      \"hash\": \"30d22075093d1d35dfb0091c1c7550e9ad948577\"\n" +
            "    },\n" +
            "    \"com.unity.ugui\": { \"version\": \"2.5.0\", \"source\": \"builtin\" }\n" +
            "  }\n" +
            "}";

        [Test]
        public void LockFileProvenance_ReadsResolvedRepositoryAndFullCommit()
        {
            bool ok = McpPackageLockProvenance.TryRead(
                LockJson,
                "com.coplaydev.unity-mcp",
                out McpLockGitEntry entry,
                out string error);

            Assert.That(ok, Is.True, error);
            Assert.That(entry.SourceKind, Is.EqualTo("git"));
            Assert.That(entry.RawVersion, Does.StartWith(ForkRepository));
            Assert.That(entry.Revision, Is.EqualTo(ForkCommit));
            Assert.That(entry.Depth, Is.EqualTo(0));
        }

        [Test]
        public void LockFileProvenance_LockOnlyTrustIsNotEnough()
        {
            // Reading the lock file alone yields a lock entry, but a managed source still needs
            // the corroborating executing package and manifest pin.
            Assert.That(
                McpPackageLockProvenance.TryRead(
                    LockJson, "com.coplaydev.unity-mcp", out McpLockGitEntry entry, out _),
                Is.True);

            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                new McpServerPackageProvenance { Lock = entry });

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("missing-installed-package"));
        }

        [Test]
        public void ManifestProvenance_ReadsDirectDependencyAndRejectsTransitiveOnly()
        {
            string manifest =
                "{ \"dependencies\": { "
                + "\"com.coplaydev.unity-mcp\": \"" + ManifestValue + "\", "
                + "\"com.unity.ugui\": \"2.5.0\" } }";

            Assert.That(
                McpPackageManifestProvenance.TryRead(
                    manifest, "com.coplaydev.unity-mcp",
                    out McpManifestGitDependency dependency, out string error),
                Is.True, error);
            Assert.That(dependency.IsDirectDependency, Is.True);
            Assert.That(dependency.RawValue, Is.EqualTo(ManifestValue));

            // Present in the lock file, absent from the manifest: never a project-owned pin.
            Assert.That(
                McpPackageManifestProvenance.TryRead(
                    manifest, "com.other.package", out _, out string missing),
                Is.False);
            Assert.That(missing, Does.Contain("com.other.package"));

            Assert.That(
                McpPackageManifestProvenance.TryRead(
                    "{ \"dependencies\": { \"com.coplaydev.unity-mcp\": { \"version\": \"1\" } } }",
                    "com.coplaydev.unity-mcp", out _, out _),
                Is.False);
            Assert.That(
                McpPackageManifestProvenance.TryRead(
                    "not json", "com.coplaydev.unity-mcp", out _, out string badJson),
                Is.False);
            Assert.That(badJson, Does.Contain("not valid JSON"));
        }

        [Test]
        public void InstalledPackageMetadata_ReadsNameAndRejectsUnusable()
        {
            Assert.That(
                McpInstalledPackageMetadata.TryRead(
                    "{ \"name\": \"com.coplaydev.unity-mcp\", \"version\": \"10.2.0\" }",
                    out string name, out string error),
                Is.True, error);
            Assert.That(name, Is.EqualTo("com.coplaydev.unity-mcp"));

            Assert.That(
                McpInstalledPackageMetadata.TryRead(
                    "{ \"version\": \"10.2.0\" }", out _, out string noName),
                Is.False);
            Assert.That(noName, Does.Contain("name"));
            Assert.That(
                McpInstalledPackageMetadata.TryRead("", out _, out _), Is.False);
        }

        [Test]
        public void LockFileProvenance_MissingEntryOrFieldsFailClosed()
        {
            Assert.That(
                McpPackageLockProvenance.TryRead(
                    LockJson, "com.other.package", out _, out string missingEntry),
                Is.False);
            Assert.That(missingEntry, Does.Contain("com.other.package"));

            Assert.That(
                McpPackageLockProvenance.TryRead(
                    "{ \"dependencies\": { \"com.coplaydev.unity-mcp\": { \"source\": \"git\" } } }",
                    "com.coplaydev.unity-mcp", out _, out string missingVersion),
                Is.False);
            Assert.That(missingVersion, Does.Contain("resolved version"));

            Assert.That(
                McpPackageLockProvenance.TryRead(
                    "{ \"dependencies\": { \"com.coplaydev.unity-mcp\": "
                    + "{ \"source\": \"git\", \"version\": \"https://x/y.git\" } } }",
                    "com.coplaydev.unity-mcp", out _, out string missingHash),
                Is.False);
            Assert.That(missingHash, Does.Contain("resolved commit"));

            Assert.That(
                McpPackageLockProvenance.TryRead(
                    "not json", "com.coplaydev.unity-mcp", out _, out string badJson),
                Is.False);
            Assert.That(badJson, Does.Contain("not valid JSON"));

            Assert.That(
                McpPackageLockProvenance.TryRead(
                    "", "com.coplaydev.unity-mcp", out _, out string empty),
                Is.False);
            Assert.That(empty, Does.Contain("packages-lock.json"));
        }

        [Test]
        public void LockFileProvenance_BranchOnlyLockEntryCannotProduceAServer()
        {
            const string branchLock =
                "{ \"dependencies\": { \"com.coplaydev.unity-mcp\": { "
                + "\"version\": \"https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#main\", "
                + "\"source\": \"git\", \"hash\": \"main\" } } }";

            Assert.That(
                McpPackageLockProvenance.TryRead(
                    branchLock, "com.coplaydev.unity-mcp",
                    out McpLockGitEntry entry, out _),
                Is.True);

            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(lockRevision: entry.Revision));
            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Category, Is.EqualTo("floating-or-malformed-revision"));
            Assert.That(resolution.Source, Is.Null);
        }

        // ============================================================
        // 6. Server environment sanitization
        // ============================================================

        [Test]
        public void EnvironmentSanitizer_RemovesInheritedRoutingVariables()
        {
            var environment = new Dictionary<string, string>
            {
                ["UNITY_MCP_HTTP_URL"] = "http://127.0.0.1:9999",
                ["UNITY_MCP_HTTP_HOST"] = "10.0.0.5",
                ["UNITY_MCP_HTTP_PORT"] = "9999",
                ["PATH"] = "/usr/bin",
            };

            IReadOnlyList<string> removed =
                McpServerEnvironmentSanitizer.RemoveRoutingVariables(environment);

            Assert.That(
                removed,
                Is.EquivalentTo(new[]
                {
                    "UNITY_MCP_HTTP_URL", "UNITY_MCP_HTTP_HOST", "UNITY_MCP_HTTP_PORT",
                }));
            Assert.That(environment.Keys, Is.EquivalentTo(new[] { "PATH" }));
        }

        [Test]
        public void EnvironmentSanitizer_LeavesUnrelatedVariablesAlone()
        {
            var environment = new Dictionary<string, string>
            {
                ["SystemRoot"] = @"C:\Windows",
                ["UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S"] = "5",
            };

            Assert.That(
                McpServerEnvironmentSanitizer.RemoveRoutingVariables(environment), Is.Empty);
            Assert.That(environment.Count, Is.EqualTo(2));
            Assert.That(
                McpServerEnvironmentSanitizer.RemoveRoutingVariables(
                    (IDictionary<string, string>)null),
                Is.Empty);
        }

        // ============================================================
        // 7 + 8. Client configuration uses the resolved route
        // ============================================================

        private static McpRouteConfiguration ManagedRoute(
            string url = "http://127.0.0.1:8101")
        {
            return McpRouteConfiguration.Resolve(new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = url,
                StoredUseHttpTransport = false,
                StoredLocalHttpBaseUrl = "http://127.0.0.1:8080",
                StoredAutoStartOnLoad = true,
            });
        }

        [Test]
        public void ClientRoute_ManagedProcessRouteWinsOverStoredPreferences()
        {
            McpRouteConfiguration route = ManagedRoute();

            Assert.That(McpClientRoute.UseHttp(route, storedUseHttp: false), Is.True);
            Assert.That(
                McpClientRoute.ResolveBaseUrl(
                    route, "http://127.0.0.1:8080", "https://remote.example",
                    storedRemoteScope: true),
                Is.EqualTo("http://127.0.0.1:8101"));
        }

        [Test]
        public void ClientRoute_UnmanagedBehaviourStillFollowsStoredPreferences()
        {
            McpRouteConfiguration route = McpRouteConfiguration.Resolve(new McpRouteInputs
            {
                StoredUseHttpTransport = true,
                StoredLocalHttpBaseUrl = "http://127.0.0.1:8080",
            });

            Assert.That(McpClientRoute.UseHttp(route, storedUseHttp: true), Is.True);
            Assert.That(McpClientRoute.UseHttp(route, storedUseHttp: false), Is.False);
            Assert.That(
                McpClientRoute.ResolveBaseUrl(
                    route, "http://127.0.0.1:8080", "https://remote.example",
                    storedRemoteScope: false),
                Is.EqualTo("http://127.0.0.1:8080"));
            Assert.That(
                McpClientRoute.ResolveBaseUrl(
                    route, "http://127.0.0.1:8080", "https://remote.example",
                    storedRemoteScope: true),
                Is.EqualTo("https://remote.example"));
        }

        [Test]
        public void ClientRoute_InvalidManagedConfigurationProducesNoEndpoint()
        {
            McpRouteConfiguration invalid = McpRouteConfiguration.Resolve(new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = "http://10.0.0.5:8101",
                StoredLocalHttpBaseUrl = "http://127.0.0.1:8080",
            });

            Assert.That(invalid.IsValid, Is.False);
            Assert.That(McpClientRoute.UseHttp(invalid, storedUseHttp: true), Is.False);
            Assert.That(
                McpClientRoute.ResolveBaseUrl(invalid, "http://127.0.0.1:8080", "", false),
                Is.Empty);
        }

        [Test]
        public void DomainReload_UnchangedEnvironmentResolvesToTheSameRoute()
        {
            McpRouteInputs inputs = new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = "http://127.0.0.1:8101",
                StoredUseHttpTransport = false,
                StoredLocalHttpBaseUrl = "http://127.0.0.1:8080",
            };

            McpRouteConfiguration before = McpRouteConfiguration.Resolve(inputs);
            McpRouteConfiguration after = McpRouteConfiguration.Resolve(inputs);

            Assert.That(after.IsValid, Is.True);
            Assert.That(after.LocalHttpBaseUrl, Is.EqualTo(before.LocalHttpBaseUrl));
            Assert.That(after.Transport, Is.EqualTo(before.Transport));
        }

        [Test]
        public void DomainReload_StoredPreferenceChangeCannotRedirectAManagedRoute()
        {
            McpRouteInputs inputs = new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = "http://127.0.0.1:8101",
                StoredLocalHttpBaseUrl = "http://127.0.0.1:8080",
            };

            McpRouteConfiguration before = McpRouteConfiguration.Resolve(inputs);

            // EditorPrefs changed between the two resolutions.
            var afterReload = new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = "http://127.0.0.1:8101",
                StoredLocalHttpBaseUrl = "http://127.0.0.1:9999",
                StoredUseHttpTransport = false,
            };
            McpRouteConfiguration after = McpRouteConfiguration.Resolve(afterReload);

            Assert.That(after.LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8101"));
            Assert.That(after.LocalHttpBaseUrl, Is.EqualTo(before.LocalHttpBaseUrl));
        }

        [Test]
        public void DomainReload_PidReuseCannotAdoptAnOldRecord()
        {
            var fixture = new OwnershipFixture();
            McpRunStateRecord oldRecord = fixture.BuildRecord();

            McpOwnershipObservation observation = fixture.BuildObservation();
            observation.CurrentEditorStartUtc = fixture.EditorStart.AddHours(2);

            Assert.That(
                McpOwnershipEvaluator.IsCurrentEditorLifetime(oldRecord, observation), Is.False);
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    oldRecord, observation, out string token, out _, out _),
                Is.False);
            Assert.That(token, Is.Null);
        }

        [Test]
        public void ManagedRouteUrl_RejectsHttpsAndCredentialBearingForms()
        {
            Assert.That(
                McpRouteConfiguration.Resolve(new McpRouteInputs
                {
                    TransportEnvironmentValue = "http",
                    HttpUrlEnvironmentValue = "https://127.0.0.1:8101",
                }).IsValid,
                Is.False);

            Assert.That(
                McpRouteConfiguration.Resolve(new McpRouteInputs
                {
                    TransportEnvironmentValue = "http",
                    HttpUrlEnvironmentValue = "http://user:pass@127.0.0.1:8101",
                }).IsValid,
                Is.False);

            Assert.That(
                McpRouteConfiguration.Resolve(new McpRouteInputs
                {
                    TransportEnvironmentValue = "http",
                    HttpUrlEnvironmentValue = "http://127.0.0.1:8101?token=1",
                }).IsValid,
                Is.False);
        }

        // ============================================================
        // 9. Guarded launch argument construction
        // ============================================================

        [Test]
        public void ManagedArguments_AppendPidFileAndNonce()
        {
            bool ok = McpManagedServerArguments.TryAppendLaunchIdentity(
                "uvx --from \"" + ExpectedSource + "\" mcp-for-unity "
                + "--transport http --http-url http://127.0.0.1:8101 "
                + "--unity-project-root \"C:\\Projects\\Workers\\WORKER-01\"",
                @"C:\Projects\Workers\WORKER-01\Library\MCPForUnity\RunState\mcp_http_8101.pid",
                "0f0a1b2c3d4e5f60718293a4b5c6d7e8",
                out string command,
                out string error);

            Assert.That(ok, Is.True, error);
            Assert.That(command, Does.Contain("--pidfile"));
            Assert.That(command, Does.Contain("mcp_http_8101.pid"));
            Assert.That(
                command,
                Does.Contain("--unity-instance-token 0f0a1b2c3d4e5f60718293a4b5c6d7e8"));
            Assert.That(command, Does.Contain("--unity-project-root"));
            Assert.That(command, Does.Contain("#subdirectory=Server"));
            Assert.That(command, Does.Not.Contain("mcpforunityserver"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("bad token")]
        [TestCase("nonce;del C:\\")]
        public void ManagedArguments_UnsafeOrMissingNonceIsRefused(string token)
        {
            Assert.That(
                McpManagedServerArguments.TryAppendLaunchIdentity(
                    "uvx mcp-for-unity", "/tmp/x.pid", token, out string command, out string error),
                Is.False);
            Assert.That(command, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ManagedArguments_MissingPidFileOrBaseCommandIsRefused()
        {
            Assert.That(
                McpManagedServerArguments.TryAppendLaunchIdentity(
                    "uvx mcp-for-unity", null, "abc123", out _, out string pidError),
                Is.False);
            Assert.That(pidError, Does.Contain("pidfile"));

            Assert.That(
                McpManagedServerArguments.TryAppendLaunchIdentity(
                    "  ", "/tmp/x.pid", "abc123", out _, out string baseError),
                Is.False);
            Assert.That(baseError, Does.Contain("base launch command"));
        }

        [Test]
        public void ManagedArguments_QuotingOnlyWhenNeeded()
        {
            Assert.That(
                McpManagedServerArguments.QuoteIfNeeded("/tmp/x.pid"), Is.EqualTo("/tmp/x.pid"));
            Assert.That(
                McpManagedServerArguments.QuoteIfNeeded(@"C:\A B\x.pid"),
                Is.EqualTo("\"C:\\A B\\x.pid\""));
        }

        [Test]
        public void ManagedArguments_ManagedFromArgsNeverUsesPyPi()
        {
            Assert.That(
                McpManagedServerArguments.TryBuildManagedFromArgs(
                    ExpectedSource, out string fromArgs, out string error),
                Is.True, error);
            Assert.That(fromArgs, Does.Contain("git+https://github.com/Avrillis/unity-mcp.git@"));
            Assert.That(fromArgs, Does.Not.Contain("mcpforunityserver"));
            Assert.That(fromArgs, Does.Contain("#subdirectory=Server"));

            Assert.That(
                McpManagedServerArguments.TryBuildManagedFromArgs(
                    "", out string none, out string noneError),
                Is.False);
            Assert.That(none, Is.Null);
            Assert.That(noneError, Does.Contain("compatible server source"));
        }

        [Test]
        public void ManagedArguments_UnresolvedSourceIsNeverAResolvedFromArgument()
        {
            McpServerSourceResolution unresolved =
                McpServerSourceResolver.ResolveManaged(
                    Provenance(
                        manifestValue:
                            "https://github.com/Avrillis/unity-mcp.git?path=/MCPForUnity#main"));

            Assert.That(unresolved.IsResolved, Is.False);
            Assert.That(
                McpManagedServerArguments.TryBuildManagedFromArgs(
                    unresolved.Source, out string fromArgs, out _),
                Is.False);
            Assert.That(fromArgs, Is.Null);
        }

        // ============================================================
        // 10. Observation builder (production adapter for gate + stop)
        // ============================================================

        private sealed class FakeStateStore : IMcpRouteStateStore
        {
            public string Root { get; set; } =
                McpRunStatePaths.Canonicalize(OwnershipFixture.MainRoot);

            public string PidFileText { get; set; } = string.Empty;
            public string HandshakeJson { get; set; }

            public string GetCanonicalProjectRoot() => Root;
            public string GetRunStateDirectory() => McpRunStatePaths.GetRunStateDirectory(Root);
            public string GetHandshakePath() => McpRunStatePaths.GetHandshakePath(Root);
            public string GetPidFilePath(int port) => McpRunStatePaths.GetPidFilePath(Root, port);

            public bool TryRead(out McpRunStateRecord record, out string error)
            {
                if (HandshakeJson == null)
                {
                    record = null;
                    error = "no ownership record is present.";
                    return false;
                }

                return McpRunStateRecord.TryParse(HandshakeJson, out record, out error);
            }

            public McpOwnershipSnapshot ReadOwnershipState()
            {
                if (Unreadable)
                {
                    return McpOwnershipSnapshot.Unknown("the ownership record is unreadable.");
                }

                if (HandshakeJson == null)
                {
                    return McpOwnershipSnapshot.Absent("no ownership record is present.");
                }

                return McpRunStateRecord.TryParse(HandshakeJson, out McpRunStateRecord record, out string error)
                    ? McpOwnershipSnapshot.Of(record)
                    : McpOwnershipSnapshot.Unknown(error);
            }

            // This in-memory store is only used for observation/gate tests; ownership mutations in
            // the termination tests run against the real file-backed McpOwnershipStore.
            public IMcpOwnershipLockProvider OwnershipLocks => null;

            public bool Write(McpRunStateRecord record, out string error)
            {
                error = null;
                HandshakeJson = record?.ToJson();
                return true;
            }

            public bool TryReadPidFile(string pidFilePath, out int pid, out bool fileExists)
            {
                pid = 0;
                fileExists = !string.IsNullOrEmpty(PidFileText);
                return fileExists && int.TryParse(PidFileText.Trim(), out pid);
            }

            public void DeleteRecord() => HandshakeJson = null;

            public bool Unreadable { get; set; }
        }

        private sealed class FakeInspector : IMcpProcessInspector
        {
            public int CurrentPid { get; set; } = OwnershipFixture.DefaultEditorPid;
            public DateTime? CurrentStart { get; set; } = OwnershipFixture.DefaultEditorStart;
            public int ServerPid { get; set; } = OwnershipFixture.DefaultServerPid;
            public DateTime? ServerStart { get; set; } = OwnershipFixture.DefaultServerStart;
            public bool ExistenceKnown { get; set; } = true;
            public bool Exists { get; set; } = true;
            public string CommandLine { get; set; } = string.Empty;
            public List<int> Listeners { get; set; } =
                new List<int> { OwnershipFixture.DefaultServerPid };
            public bool RecordedEditorExists { get; set; } = true;

            public int GetCurrentProcessId() => CurrentPid;

            public bool TryGetCurrentProcessStartTimeUtc(out DateTime startUtc)
            {
                startUtc = CurrentStart ?? default;
                return CurrentStart.HasValue;
            }

            public bool TryGetProcessStartTimeUtc(int pid, out DateTime startUtc)
            {
                startUtc = ServerStart ?? default;
                return ServerStart.HasValue;
            }

            public bool ProcessExists(int pid) => Exists;

            public bool TryObserveProcessExistence(int pid, out bool exists)
            {
                exists = pid == CurrentPid || (pid == ServerPid ? Exists : RecordedEditorExists);
                return ExistenceKnown;
            }

            public bool TryOpenRetainedProcess(int pid, out IRetainedProcessHandle handle)
            {
                handle = ServerStart.HasValue ? new FakeProcessHandle(ServerStart.Value) : null;
                return handle != null;
            }

            public bool TryGetCommandLine(int pid, out string commandLine)
            {
                commandLine = CommandLine;
                return !string.IsNullOrEmpty(CommandLine);
            }

            public IReadOnlyList<int> GetListeningProcessIds(int port) => Listeners;
        }

        private static string CoherentCommandLine(OwnershipFixture fixture)
        {
            string root = McpRunStatePaths.Canonicalize(fixture.ProjectRoot);
            string pidFile = McpRunStatePaths.GetPidFilePath(root, 8101);
            return "uvx mcp-for-unity --transport http --http-url " + fixture.Endpoint + " "
                   + "--unity-project-root \"" + root + "\" --pidfile \"" + pidFile + "\" "
                   + "--unity-instance-token " + fixture.Nonce;
        }

        [Test]
        public void ObservationBuilder_BuildsACoherentObservationFromLiveSources()
        {
            var fixture = new OwnershipFixture();
            var store = new FakeStateStore();
            var inspector = new FakeInspector { CommandLine = CoherentCommandLine(fixture) };

            McpOwnershipObservation observation = McpRouteObservationBuilder.Build(
                store, inspector,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = store.GetCanonicalProjectRoot(),
                    Endpoint = fixture.Endpoint,
                    CurrentEditorPid = inspector.CurrentPid,
                    RecordedServerPid = fixture.ServerPid,
                    RecordedEditorPid = OwnershipFixture.DefaultEditorPid,
                });

            Assert.That(observation.ServerProcessExistenceKnown, Is.True);
            Assert.That(observation.ServerProcessExists, Is.True);
            Assert.That(observation.ServerProcessLifetimeAvailable, Is.True);
            Assert.That(observation.ServerCommandLineAvailable, Is.True);
            Assert.That(
                observation.PidFilePath,
                Is.EqualTo(McpRunStatePaths.GetPidFilePath(store.GetCanonicalProjectRoot(), 8101)));
            Assert.That(observation.ListeningProcessIds, Is.EqualTo(new[] { fixture.ServerPid }));
            Assert.That(observation.RecordedEditorProcessExists, Is.True);
        }

        [Test]
        public void ObservationBuilder_UnknownExistenceIsReportedAsUnknown()
        {
            var fixture = new OwnershipFixture();
            var store = new FakeStateStore();
            var inspector = new FakeInspector
            {
                ExistenceKnown = false,
                Exists = false,
                CommandLine = CoherentCommandLine(fixture),
            };

            McpOwnershipObservation observation = McpRouteObservationBuilder.Build(
                store, inspector,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = store.GetCanonicalProjectRoot(),
                    Endpoint = fixture.Endpoint,
                    CurrentEditorPid = inspector.CurrentPid,
                    RecordedServerPid = fixture.ServerPid,
                });

            Assert.That(observation.ServerProcessExistenceKnown, Is.False);
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(fixture.BuildRecord(), observation).Reason,
                Is.EqualTo(McpOwnershipDenyReason.MissingProcessLifetime));
        }

        [Test]
        public void ObservationBuilder_RecordedEditorProcessExistenceIsObserved()
        {
            var fixture = new OwnershipFixture();
            var store = new FakeStateStore();
            var inspector = new FakeInspector
            {
                CommandLine = CoherentCommandLine(fixture),
                RecordedEditorExists = false,
                CurrentPid = OwnershipFixture.DefaultEditorPid + 1,
            };

            McpOwnershipObservation observation = McpRouteObservationBuilder.Build(
                store, inspector,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = store.GetCanonicalProjectRoot(),
                    Endpoint = fixture.Endpoint,
                    CurrentEditorPid = inspector.CurrentPid,
                    RecordedServerPid = fixture.ServerPid,
                    RecordedEditorPid = OwnershipFixture.DefaultEditorPid,
                });

            Assert.That(observation.RecordedEditorProcessExists, Is.False);
        }

        [Test]
        public void ObservationBuilder_PortIsDerivedFromTheEndpoint()
        {
            Assert.That(
                McpRouteObservationBuilder.PortFromEndpoint("http://127.0.0.1:8101"),
                Is.EqualTo(8101));
            Assert.That(McpRouteObservationBuilder.PortFromEndpoint("not a url"), Is.EqualTo(0));
            Assert.That(McpRouteObservationBuilder.PortFromEndpoint(null), Is.EqualTo(0));
        }
    }
}
