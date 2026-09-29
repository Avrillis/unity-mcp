using System;
using System.Collections.Specialized;
using System.IO;
using MCPForUnity.Editor.Services.Route;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// R2-FIX6 coverage for the Windows managed launch failure: the managed immutable server source
    /// was correct, but the launch inherited a long default tool cache and the resulting checkout
    /// exceeded the platform path limit, so no server process was ever produced.
    ///
    /// These tests pin the managed cache policy: a deliberately short per-user root, applied to the
    /// spawned child only, prepared fail-closed before any pending ownership record, with no global
    /// mutation, no repository-tool configuration, no cache deletion and no provenance or lifecycle
    /// coupling.
    ///
    /// Nothing here assumes anything about any tool's internal cache layout, and the real per-user
    /// root is never created: creation is exercised only with isolated temporary roots.
    /// </summary>
    [TestFixture]
    public class McpFix6ManagedCacheTests
    {
        // Temporary test roots are much longer than the real per-user root, so the deliberately
        // short production length guard would reject them. Validation that is not about length uses
        // this allowance; the length guard itself is exercised with the production default.
        private const int TestMaxRootLength = 4096;

        private string _sandbox;

        [SetUp]
        public void SetUp()
        {
            _sandbox = Path.Combine(
                Path.GetTempPath(), "swm-fix6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_sandbox))
                {
                    Directory.Delete(_sandbox, true);
                }
            }
            catch
            {
                // Best effort only; the sandbox is outside every repository.
            }
        }

        private static McpManagedCachePreparation PrepareForTest(string projectRoot, string cacheRoot)
            => McpManagedServerCache.Prepare(projectRoot, cacheRoot, TestMaxRootLength);

        // ------------------------------------------------------------------ 1/2. derivation

        [Test]
        public void ManagedRoot_IsDeterministicAndShapedLikeTheUserProfileRoot()
        {
            string first = McpManagedServerCache.ResolveManagedCacheRoot();
            string second = McpManagedServerCache.ResolveManagedCacheRoot();

            Assert.That(first, Is.Not.Null.And.Not.Empty);
            Assert.That(second, Is.EqualTo(first), "root derivation must be deterministic");

            string profile = McpRunStatePaths.Canonicalize(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.That(
                Path.GetFileName(first),
                Is.EqualTo(McpManagedServerCache.ManagedCacheDirectoryName));
            Assert.That(Directory.GetParent(first).FullName, Is.EqualTo(profile));
            Assert.That(
                first.Length,
                Is.LessThanOrEqualTo(McpManagedServerCache.MaxManagedCacheRootLength),
                "the managed root must stay deliberately short");
        }

        // ------------------------------------------------------------------ 3. independence

        [Test]
        public void ManagedRoot_IsIndependentOfRepositoryCwdTaskWorkerAndPort()
        {
            string repoOne = Path.Combine(_sandbox, "Repo One");
            string repoTwo = Path.Combine(_sandbox, "Repo Two");
            Directory.CreateDirectory(repoOne);
            Directory.CreateDirectory(repoTwo);

            string shared = Path.Combine(_sandbox, "shared-cache");
            McpManagedCachePreparation one = PrepareForTest(repoOne, shared);
            McpManagedCachePreparation two = PrepareForTest(repoTwo, shared);

            // The helper takes no task, worker slot, port or route input at all: the same managed
            // root is selected regardless of the project, and none of those concepts appear in it.
            Assert.That(one.Ok, Is.True, one.Detail);
            Assert.That(one.Root, Is.EqualTo(two.Root));
            Assert.That(
                McpManagedServerCache.ResolveManagedCacheRoot(),
                Is.EqualTo(McpManagedServerCache.ResolveManagedCacheRoot()),
                "resolution must not depend on the current directory or the repository");
            Assert.That(one.Root, Does.Not.Contain("WORKER"));
            Assert.That(one.Root, Does.Not.Contain("TLN-"));
            Assert.That(one.Root, Does.Not.Contain("8080"));
        }

        // ------------------------------------------------------------------ 4/5/6/7. child env

        [Test]
        public void ChildEnvironment_ReceivesTheManagedCacheRoot()
        {
            string root = Path.Combine(_sandbox, "cache-root");
            var environment = new StringDictionary { ["PATH"] = "/usr/bin" };

            McpManagedCacheApplication applied = McpManagedServerCache.ApplyTo(environment, root);

            Assert.That(applied.Applied, Is.True);
            Assert.That(
                environment[McpManagedServerCache.CacheDirectoryEnvironmentVariable],
                Is.EqualTo(McpRunStatePaths.Canonicalize(root)));
            Assert.That(applied.OverrodeInheritedValue, Is.False);
            Assert.That(
                applied.AppliedValue,
                Is.Not.EqualTo(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "uv", "cache")),
                "the managed root must never fall back to the tool default cache");
        }

        [Test]
        public void ChildEnvironment_OverridesADifferingInheritedValueForThisChildOnly()
        {
            const string variable = McpManagedServerCache.CacheDirectoryEnvironmentVariable;
            string root = Path.Combine(_sandbox, "cache-root");
            string inherited = Path.Combine(_sandbox, "long", "inherited", "tool", "cache");
            string processBefore = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process);

            var environment = new StringDictionary { [variable] = inherited };

            McpManagedCacheApplication applied = McpManagedServerCache.ApplyTo(environment, root);

            Assert.That(applied.Applied, Is.True);
            Assert.That(applied.OverrodeInheritedValue, Is.True);
            Assert.That(applied.InheritedValue, Is.EqualTo(inherited));
            Assert.That(environment[variable], Is.EqualTo(McpRunStatePaths.Canonicalize(root)));
            Assert.That(
                Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process),
                Is.EqualTo(processBefore),
                "the override must be child-only");
        }

        [Test]
        public void ChildEnvironment_AcceptsAnInheritedValueThatAlreadyMatches()
        {
            const string variable = McpManagedServerCache.CacheDirectoryEnvironmentVariable;
            string root = Path.Combine(_sandbox, "cache-root");
            string canonical = McpRunStatePaths.Canonicalize(root);
            var environment = new StringDictionary
            {
                [variable] = canonical + Path.DirectorySeparatorChar,
            };

            McpManagedCacheApplication applied = McpManagedServerCache.ApplyTo(environment, root);

            Assert.That(applied.Applied, Is.True);
            Assert.That(applied.OverrodeInheritedValue, Is.False, "an equal value is not an override");
            Assert.That(environment[variable], Is.EqualTo(canonical));
        }

        [Test]
        public void ChildEnvironment_LeavesUnrelatedVariablesUntouched()
        {
            string root = Path.Combine(_sandbox, "cache-root");
            var environment = new StringDictionary
            {
                ["PATH"] = @"C:\Windows\System32",
                ["SystemRoot"] = @"C:\Windows",
                ["UNITY_MCP_HTTP_URL"] = "http://127.0.0.1:8080",
                ["UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S"] = "5",
            };

            McpManagedServerCache.ApplyTo(environment, root);

            Assert.That(environment["PATH"], Is.EqualTo(@"C:\Windows\System32"));
            Assert.That(environment["SystemRoot"], Is.EqualTo(@"C:\Windows"));
            Assert.That(environment["UNITY_MCP_HTTP_URL"], Is.EqualTo("http://127.0.0.1:8080"));
            Assert.That(environment["UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S"], Is.EqualTo("5"));
            Assert.That(environment.Count, Is.EqualTo(5));
        }

        // ------------------------------------------------------------------ 8. no global mutation

        [Test]
        public void Helper_DoesNotMutateProcessOrPersistedEnvironment()
        {
            const string variable = McpManagedServerCache.CacheDirectoryEnvironmentVariable;
            string processBefore = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process);
            string userBefore = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User);
            string machineBefore = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine);

            string root = Path.Combine(_sandbox, "cache-root");
            PrepareForTest(Path.Combine(_sandbox, "Project With Spaces"), root);
            McpManagedServerCache.ApplyTo(new StringDictionary(), root);

            Assert.That(
                Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process),
                Is.EqualTo(processBefore));
            Assert.That(
                Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User),
                Is.EqualTo(userBefore));
            Assert.That(
                Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine),
                Is.EqualTo(machineBefore));
        }

        // ------------------------------------------------------------------ 9/10. fail closed

        [Test]
        public void Prepare_RejectsRelativeAndBlankRoots()
        {
            McpManagedCachePreparation relative = PrepareForTest(_sandbox, "relative-cache");
            Assert.That(relative.Ok, Is.False);
            Assert.That(relative.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_RELATIVE"));
            Assert.That(relative.Detail, Is.Not.Null.And.Not.Empty);

            McpManagedCachePreparation blank = PrepareForTest(_sandbox, "   ");
            Assert.That(blank.Ok, Is.False);
            Assert.That(blank.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_UNRESOLVED"));
        }

        [Test]
        public void Prepare_RejectsAnUncreatableRoot()
        {
            string project = Path.Combine(_sandbox, "Project");
            string blocker = Path.Combine(_sandbox, "not-a-directory");
            File.WriteAllText(blocker, "a file occupies this path");

            McpManagedCachePreparation result =
                PrepareForTest(project, Path.Combine(blocker, "cache"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_UNUSABLE"));
            Assert.That(result.Detail, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Prepare_RejectsAnOverlyLongRootWithTheProductionGuard()
        {
            string longRoot = Path.Combine(
                _sandbox, new string('x', McpManagedServerCache.MaxManagedCacheRootLength));

            // Production default guard (no test allowance).
            McpManagedCachePreparation result = McpManagedServerCache.Prepare(_sandbox, longRoot);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_TOO_LONG"));
            Assert.That(Directory.Exists(longRoot), Is.False, "a rejected root is never created");
        }

        // ------------------------------------------------------------------ 11/12. containment

        [Test]
        public void Prepare_RejectsTheProjectRootItselfAndPathsInsideTheProject()
        {
            string project = Path.Combine(_sandbox, "My Project");
            Directory.CreateDirectory(project);

            McpManagedCachePreparation self = PrepareForTest(project, project);
            Assert.That(self.Ok, Is.False);
            Assert.That(self.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_IS_PROJECT_ROOT"));

            McpManagedCachePreparation inside =
                PrepareForTest(project, Path.Combine(project, "uv-cache"));
            Assert.That(inside.Ok, Is.False);
            Assert.That(inside.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_INSIDE_PROJECT"));
        }

        [Test]
        public void Prepare_RejectsAnythingInsideUnityLibrary()
        {
            string project = Path.Combine(_sandbox, "My Project");
            Directory.CreateDirectory(Path.Combine(project, "Library"));

            McpManagedCachePreparation result = PrepareForTest(
                project, Path.Combine(project, "Library", "uv-cache"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Reason, Is.EqualTo("MANAGED_CACHE_ROOT_INSIDE_LIBRARY"));
        }

        // ------------------------------------------------------------------ creation / reuse

        [Test]
        public void Prepare_CreatesAMissingRootAndNeverRemovesExistingContents()
        {
            string project = Path.Combine(_sandbox, "Project");
            string root = Path.Combine(_sandbox, "created-on-demand");
            Assert.That(Directory.Exists(root), Is.False);

            McpManagedCachePreparation first = PrepareForTest(project, root);
            Assert.That(first.Ok, Is.True, first.Detail);
            Assert.That(first.AlreadyExisted, Is.False);
            Assert.That(Directory.Exists(first.Root), Is.True);

            string sentinel = Path.Combine(first.Root, "existing-cache-entry.txt");
            File.WriteAllText(sentinel, "keep me");

            McpManagedCachePreparation second = PrepareForTest(project, root);
            Assert.That(second.Ok, Is.True, second.Detail);
            Assert.That(second.AlreadyExisted, Is.True);
            Assert.That(File.Exists(sentinel), Is.True, "existing cache contents must survive");
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("keep me"));
            Assert.That(second.Root, Is.EqualTo(first.Root));
        }

        [Test]
        public void Prepare_SelectsTheSameRootForSimulatedWorkersWithoutCollision()
        {
            string root = Path.Combine(_sandbox, "shared");
            string main = Path.Combine(_sandbox, "Main Checkout");

            var roots = new System.Collections.Generic.List<string>();
            foreach (string project in new[]
                     {
                         main,
                         Path.Combine(_sandbox, "Worker One"),
                         Path.Combine(_sandbox, "Worker Two"),
                         Path.Combine(_sandbox, "Worker Three"),
                     })
            {
                Directory.CreateDirectory(project);
                McpManagedCachePreparation prepared = PrepareForTest(project, root);
                Assert.That(prepared.Ok, Is.True, prepared.Detail);
                roots.Add(prepared.Root);
            }

            Assert.That(
                roots,
                Is.All.EqualTo(McpRunStatePaths.Canonicalize(root)),
                "one shared per-user cache is selected for every managed project");
        }

        // ------------------------------------------------------------------ 13. provenance untouched

        [Test]
        public void Helper_KeepsProvenanceAndLifecycleOutOfTheCache()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                Path.Combine("MCPForUnity", "Editor", "Services", "Route", "McpManagedServerCache.cs")));

            Assert.That(source, Does.Not.Contain("McpServerSource"), "cache must not touch provenance");
            Assert.That(source, Does.Not.Contain("pypi"));
            Assert.That(source, Does.Not.Contain("subdirectory=Server"));
            Assert.That(source, Does.Not.Contain("handshake"), "cache must not touch lifecycle records");
            Assert.That(source, Does.Not.Contain("TryWriteAtomic"));
            Assert.That(source, Does.Not.Contain("authoriz"), "cache must grant no authority");

            Assert.That(McpServerSourceResolver.ServerSubdirectory, Is.EqualTo("Server"));
        }

        // ------------------------------------------------------------------ 14/15. no side surface

        [Test]
        public void Helper_HasNoProcessToolingRegistryOrDeletionSurface()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                Path.Combine("MCPForUnity", "Editor", "Services", "Route", "McpManagedServerCache.cs")));

            Assert.That(source, Does.Not.Contain("Process.Start"), "no process launch surface");
            Assert.That(source, Does.Not.Contain("ProcessStartInfo"));
            Assert.That(source, Does.Not.Contain("EnvironmentVariableTarget"), "no global environment writes");
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("setx"));
            Assert.That(source, Does.Not.Contain("git"), "no repository-tool configuration");
            Assert.That(source, Does.Not.Contain("File.Delete"), "the helper must not delete cache entries");
            Assert.That(source, Does.Not.Contain("Directory.Delete"));
            Assert.That(source, Does.Not.Contain("Kill"));
        }

        // ------------------------------------------------------------------ production wiring

        [Test]
        public void ProductionLaunch_PreparesTheManagedCacheBeforeThePendingRecord()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                Path.Combine("MCPForUnity", "Editor", "Services", "ServerManagementService.cs")));

            int prepare = source.IndexOf(
                "McpManagedServerCache.Prepare(", StringComparison.Ordinal);
            int pendingRecord = source.IndexOf(
                "var pendingRecord = new McpRunStateRecord", StringComparison.Ordinal);
            int apply = source.IndexOf(
                "McpManagedServerCache.ApplyTo(startInfo.EnvironmentVariables", StringComparison.Ordinal);
            int spawn = source.IndexOf(
                "System.Diagnostics.Process.Start(startInfo)", StringComparison.Ordinal);

            Assert.That(prepare, Is.GreaterThanOrEqualTo(0), "the launch must prepare the managed cache");
            Assert.That(pendingRecord, Is.GreaterThan(prepare),
                "cache preparation must precede the pending ownership record");
            Assert.That(apply, Is.GreaterThan(pendingRecord),
                "the child environment is pointed at the prepared root before the spawn");
            Assert.That(spawn, Is.GreaterThan(apply));
        }

        [Test]
        public void ProductionLaunch_ReportsTheOverrideWithoutEchoingTheInheritedValue()
        {
            string source = File.ReadAllText(FindRepositoryFile(
                Path.Combine("MCPForUnity", "Editor", "Services", "ServerManagementService.cs")));

            Assert.That(
                source.IndexOf("Replaced the inherited", StringComparison.Ordinal),
                Is.GreaterThanOrEqualTo(0),
                "a differing inherited value must be reported once");
            Assert.That(
                source.IndexOf("cacheApplication.InheritedValue", StringComparison.Ordinal),
                Is.EqualTo(-1),
                "the inherited value is never echoed into the log");
        }

        // ------------------------------------------------------------------ helpers

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
