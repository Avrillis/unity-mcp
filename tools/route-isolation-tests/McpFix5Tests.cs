using MCPForUnity.Editor.Services.Route;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// R2-FIX5 coverage for the production-only blocker R3-A found: the managed server-source
    /// resolver required <c>LeafName(resolvedPath) == "MCPForUnity"</c>, which the real Unity UPM
    /// install never produces for a <c>?path=/MCPForUnity</c> Git dependency.
    ///
    /// UPM materializes that dependency as
    /// <c>&lt;project&gt;/Library/PackageCache/&lt;package-name&gt;@&lt;fingerprint&gt;</c> with the package
    /// contents at that root (observed in TLN:
    /// <c>C:\Users\Avril\The Last Noob\Library\PackageCache\com.coplaydev.unity-mcp@bfc85ac860cf</c>).
    /// That layout must be accepted, and the nested <c>MCPForUnity</c> layout must keep working.
    ///
    /// The folder leaf/fingerprint is a layout witness only: it is never compared to a Git revision
    /// and never used to derive one, which the FIX5 cases below pin explicitly.
    /// </summary>
    [TestFixture]
    public class McpFix5PackageCacheLayoutTests
    {
        private const string Package = "com.coplaydev.unity-mcp";
        private const string Repository = "https://github.com/Avrillis/unity-mcp.git";
        private const string Commit = "e791d9dc0a1ee8ce9324985901737d65ee2dd075";

        // The literal shape observed on the machine that raised R3-A. Test data only; production
        // code never contains a user path.
        private const string ObservedProjectRoot = @"C:\Users\Avril\The Last Noob";
        private const string ObservedPackageCache = ObservedProjectRoot + @"\Library\PackageCache";
        private const string ObservedResolvedPath =
            ObservedPackageCache + @"\com.coplaydev.unity-mcp@bfc85ac860cf";

        private const string ManifestValue = Repository + "?path=/MCPForUnity#" + Commit;
        private const string ExpectedSource =
            "git+" + Repository + "@" + Commit + "#subdirectory=Server";

        private static string ManifestJson(string packageName, string value)
            => "{ \"dependencies\": { \"" + packageName + "\": \"" + value + "\" } }";

        private static string LockJson(
            string packageName, string version, string hash, string source, string depth)
            => "{\n"
               + "  \"dependencies\": {\n"
               + "    \"" + packageName + "\": {\n"
               + "      \"version\": \"" + version + "\",\n"
               + "      \"source\": \"" + source + "\",\n"
               + "      \"depth\": " + depth + ",\n"
               + "      \"hash\": \"" + hash + "\"\n"
               + "    }\n"
               + "  }\n"
               + "}";

        /// <summary>
        /// Builds a coherent provenance set through the production manifest/lock readers, so every
        /// witness is parsed by the same code the adapter uses. Individual cases perturb exactly one
        /// witness at a time.
        /// </summary>
        private static McpServerPackageProvenance Provenance(
            string resolvedPath = ObservedResolvedPath,
            string packageCachePath = ObservedPackageCache,
            bool resolvedPathExists = true,
            bool insidePackageCache = true,
            string installedName = Package,
            string sourceKind = "git",
            string packageJsonName = Package,
            string manifestPackageName = Package,
            string manifestValue = ManifestValue,
            bool directManifest = true,
            string lockPackageName = Package,
            string lockVersion = null,
            string lockHash = Commit,
            string lockSource = "git",
            string lockDepth = "0")
        {
            if (lockVersion == null)
            {
                lockVersion = Repository + "?path=/MCPForUnity";
            }

            McpPackageManifestProvenance.TryRead(
                ManifestJson(manifestPackageName, manifestValue),
                Package,
                out McpManifestGitDependency manifest,
                out _);
            if (manifest != null)
            {
                manifest.IsDirectDependency = directManifest;
            }

            McpPackageLockProvenance.TryRead(
                LockJson(lockPackageName, lockVersion, lockHash, lockSource, lockDepth),
                Package,
                out McpLockGitEntry lockEntry,
                out _);

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
                    ProjectPackageCachePath = packageCachePath,
                },
                Manifest = manifest,
                Lock = lockEntry,
            };
        }

        // ------------------------------------------------------------------ PASS

        [Test]
        public void SourceResolver_RealObservedPackageCacheLayoutIsAccepted()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(Provenance());

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Source, Is.EqualTo(ExpectedSource));
            Assert.That(resolution.Repository, Is.EqualTo(Repository));
            Assert.That(resolution.Revision, Is.EqualTo(Commit));
        }

        [Test]
        public void SourceResolver_NestedMCPForUnityLayoutIsStillAccepted()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath: ObservedPackageCache + @"\com.coplaydev.unity-mcp@bfc85ac860cf\MCPForUnity"));

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Source, Is.EqualTo(ExpectedSource));
        }

        [Test]
        public void SourceResolver_FingerprintUnrelatedToTheGitShaIsAccepted()
        {
            // The suffix is UPM's content fingerprint. Here it cannot even be mistaken for a commit:
            // acceptance must rest entirely on the manifest/lock witnesses.
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath: ObservedPackageCache + @"\com.coplaydev.unity-mcp@not-a-sha.at.all"));

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Revision, Is.EqualTo(Commit));
        }

        [Test]
        public void SourceResolver_FolderSuffixIsNeverUsedAsTheRevision()
        {
            // The folder name carries a full 40-hex string that is NOT the reviewed commit. If the
            // resolver ever used it as provenance the derived revision would change; it must not.
            const string decoy = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
            Assert.That(decoy, Is.Not.EqualTo(Commit));

            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(resolvedPath: ObservedPackageCache + @"\com.coplaydev.unity-mcp@" + decoy));

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Revision, Is.EqualTo(Commit));
            Assert.That(resolution.Source, Is.EqualTo(ExpectedSource));
            Assert.That(resolution.Source, Does.Not.Contain(decoy));
        }

        [Test]
        public void SourceResolver_PackageCacheEntryUsesOnlyTheManifestAndLockWitnesses()
        {
            // A different reviewed revision must flow through the folder suffix untouched.
            const string otherCommit = "30d22075093d1d35dfb0091c1c7550e9ad948577";
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath: ObservedPackageCache + @"\com.coplaydev.unity-mcp@bfc85ac860cf",
                    manifestValue: Repository + "?path=/MCPForUnity#" + otherCommit,
                    lockHash: otherCommit));

            Assert.That(resolution.IsResolved, Is.True, resolution.Error);
            Assert.That(resolution.Revision, Is.EqualTo(otherCommit));
            Assert.That(
                resolution.Source,
                Is.EqualTo("git+" + Repository + "@" + otherCommit + "#subdirectory=Server"));
        }

        // ------------------------------------------------------------------ FAIL: layout

        [TestCase(@"C:\Tools\com.coplaydev.unity-mcp@abc")]
        [TestCase(@"C:\proj\com.coplaydev.unity-mcp@abc")]
        [TestCase(@"C:\Users\Avril\The Last Noob\Assets\com.coplaydev.unity-mcp@abc")]
        public void SourceResolver_MatchingFolderOutsideThePackageCacheIsRefused(string resolvedPath)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(resolvedPath: resolvedPath, insidePackageCache: false));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [Test]
        public void SourceResolver_EntryOfAnotherUnityProjectsPackageCacheIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath: @"C:\Other Project\Library\PackageCache\com.coplaydev.unity-mcp@abc",
                    packageCachePath: ObservedPackageCache,
                    insidePackageCache: true));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [TestCase(@"C:\Users\Avril\The Last Noob\Library\PackageCache\..\..\evil\com.coplaydev.unity-mcp@abc")]
        [TestCase(@"C:\Users\Avril\The Last Noob\Library\PackageCache\..\..\evil\MCPForUnity")]
        [TestCase(@"C:\Users\Avril\The Last Noob\Library\PackageCache\sub\..\..\..\Windows\Temp\com.coplaydev.unity-mcp@abc")]
        public void SourceResolver_TraversalOrCanonicalizationEscapeIsRefused(string resolvedPath)
        {
            // The caller's containment flag is deliberately set inconsistently (true) so the
            // resolver must re-prove containment from the canonical paths themselves.
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(resolvedPath: resolvedPath, insidePackageCache: true));

            Assert.That(resolution.IsResolved, Is.False, resolution.Error);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [Test]
        public void SourceResolver_DeeplyNestedSpoofUnderThePackageCacheIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath:
                        ObservedPackageCache + @"\fake\com.coplaydev.unity-mcp@bfc85ac860cf"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [TestCase(@"com.other.tool@abc")]
        [TestCase(@"com.coplaydev.unity-mcp-extra@abc")]
        [TestCase(@"com.coplaydev.unity@abc")]
        [TestCase(@"unity-mcp@abc")]
        [TestCase(@"MCPForUnity-copy")]
        public void SourceResolver_WrongPackageIdentityFolderIsRefused(string leaf)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    resolvedPath: ObservedPackageCache + "\\" + leaf,
                    packageJsonName: Package));

            Assert.That(resolution.IsResolved, Is.False, resolution.Error);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [TestCase(@"com.coplaydev.unity-mcp@")]
        [TestCase(@"com.coplaydev.unity-mcp@..")]
        [TestCase(@"com.coplaydev.unity-mcp@.")]
        [TestCase(@"com.coplaydev.unity-mcp@abc@def")]
        [TestCase(@"com.coplaydev.unity-mcp@ab cd")]
        [TestCase(@"com.coplaydev.unity-mcp@abc:def")]
        public void SourceResolver_MalformedPackageCacheSuffixIsRefused(string leaf)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(resolvedPath: ObservedPackageCache + "\\" + leaf));

            Assert.That(resolution.IsResolved, Is.False, resolution.Error);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void SourceResolver_MissingProjectPackageCachePathIsRefused(string packageCachePath)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(packageCachePath: packageCachePath));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-path-mismatch"));
        }

        // ------------------------------------------------------------------ FAIL: witnesses

        [Test]
        public void SourceResolver_ManifestSubdirectoryThatIsNotMCPForUnityIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(manifestValue: Repository + "?path=/Package#" + Commit));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("unexpected-subpath"));
        }

        [Test]
        public void SourceResolver_ManifestRevisionThatDiffersFromTheLockHashIsRefused()
        {
            const string otherCommit = "30d22075093d1d35dfb0091c1c7550e9ad948577";
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    manifestValue: Repository + "?path=/MCPForUnity#" + otherCommit,
                    lockHash: Commit));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("revision-mismatch"));
        }

        [Test]
        public void SourceResolver_RepositoryMismatchIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    lockVersion: "https://github.com/SomeoneElse/unity-mcp.git?path=/MCPForUnity"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("repository-mismatch"));
        }

        [Test]
        public void SourceResolver_InstalledPackageJsonNameMismatchIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(packageJsonName: "com.someone.else"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-metadata-mismatch"));
        }

        [Test]
        public void SourceResolver_MissingInstalledPackageJsonIsRefused()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(packageJsonName: null));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("installed-metadata-mismatch"));
        }

        [TestCase("main")]
        [TestCase("v1.2.3")]
        [TestCase("30d2207")]
        [TestCase("")]
        public void SourceResolver_FloatingOrShortManifestRevisionIsRefused(string revision)
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(
                    manifestValue: Repository + "?path=/MCPForUnity#" + revision,
                    lockHash: revision));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("floating-or-malformed-revision"));
        }

        [Test]
        public void SourceResolver_NonGitInstalledPackageIsRefusedInThePackageCacheLayout()
        {
            McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(
                Provenance(sourceKind: "registry"));

            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.Source, Is.Null);
            Assert.That(resolution.Category, Is.EqualTo("non-git-package"));
        }

        [Test]
        public void SourceResolver_EveryAcceptedResolutionNamesThePinnedImmutableServer()
        {
            // No fallback path exists: whatever the accepted layout, the only source that can come
            // back is the paired repository/commit with the Server/ subdirectory.
            McpServerSourceResolution materialized = McpServerSourceResolver.ResolveManaged(Provenance());
            McpServerSourceResolution nested = McpServerSourceResolver.ResolveManaged(
                Provenance(resolvedPath: ObservedPackageCache + @"\com.coplaydev.unity-mcp@bfc85ac860cf\MCPForUnity"));

            Assert.That(materialized.Source, Is.EqualTo(nested.Source));
            Assert.That(materialized.Source, Does.StartWith("git+https://"));
            Assert.That(materialized.Source, Does.EndWith("#subdirectory=Server"));
            Assert.That(materialized.Source, Does.Not.Contain("pypi"));
            Assert.That(materialized.Source, Does.Not.Contain("mcpforunityserver"));
        }
    }
}
