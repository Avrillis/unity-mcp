using System;
using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Gathers the corroborating provenance of the installed MCPForUnity UPM package and derives
    /// the Python server source a managed route must use.
    ///
    /// Unity keeps only the package subfolder for a <c>?path=/MCPForUnity</c> Git install, so the
    /// compatible <c>Server/</c> tree cannot be read from disk next to the package. Instead every
    /// available source of package identity is collected and handed to
    /// <see cref="McpServerSourceResolver"/>, which refuses unless they all agree:
    ///
    ///   * the executing package, via <see cref="PackageInfo.FindForAssembly"/>;
    ///   * the installed package's own <c>package.json</c> at its resolved path;
    ///   * the project's direct dependency pin in <c>Packages/manifest.json</c>;
    ///   * the resolved repository and commit in <c>Packages/packages-lock.json</c>.
    ///
    /// No single one of those is sufficient: in particular the lock file alone must never name the
    /// server to launch, and a manifest that floats (for example <c>#main</c>) must fail closed even
    /// when the lock file currently holds a full hash.
    /// </summary>
    public static class McpServerSourceProvider
    {
        /// <summary>Project-relative path of the UPM manifest.</summary>
        public const string ManifestFileRelativePath = "Packages/manifest.json";

        /// <summary>Project-relative path of the UPM lock file.</summary>
        public const string LockFileRelativePath = "Packages/packages-lock.json";

        /// <summary>Never throws; a failure yields an unresolved (fail-closed) result.</summary>
        public static McpServerSourceResolution ResolveManaged()
            => ResolveManaged(out _);

        /// <summary>Never throws; a failure yields an unresolved (fail-closed) result.</summary>
        public static McpServerSourceResolution ResolveManaged(out string error)
        {
            error = null;

            try
            {
                PackageInfo package = PackageInfo.FindForAssembly(typeof(McpServerSourceProvider).Assembly);
                if (package == null || string.IsNullOrEmpty(package.name))
                {
                    return Unresolved("missing-package", "the installed MCP package could not be identified.", out error);
                }

                if (!string.Equals(
                        package.name, McpServerSourceResolver.ApprovedPackageName, StringComparison.Ordinal))
                {
                    return Unresolved(
                        "unexpected-package",
                        $"the executing package is '{package.name}', not "
                        + $"'{McpServerSourceResolver.ApprovedPackageName}'.",
                        out error);
                }

                string projectRoot = McpRouteStateStore.ResolveProjectRoot();
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return Unresolved("missing-project-root", "the Unity project root could not be resolved.", out error);
                }

                string packageRoot = package.resolvedPath;

                var provenance = new McpServerPackageProvenance
                {
                    Installed = ReadInstalledIdentity(package, packageRoot, projectRoot),
                };

                string manifestPath = Path.Combine(
                    projectRoot, "Packages", "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return Unresolved(
                        "missing-manifest",
                        $"{ManifestFileRelativePath} is missing, so the project's package pin is unknown.",
                        out error);
                }

                if (!McpPackageManifestProvenance.TryRead(
                        File.ReadAllText(manifestPath),
                        package.name,
                        out McpManifestGitDependency manifest,
                        out string manifestError))
                {
                    return Unresolved("missing-manifest-dependency", manifestError, out error);
                }

                provenance.Manifest = manifest;

                string lockPath = Path.Combine(
                    projectRoot, "Packages", "packages-lock.json");
                if (!File.Exists(lockPath))
                {
                    return Unresolved(
                        "missing-lockfile",
                        $"{LockFileRelativePath} is missing, so the installed package revision is unknown.",
                        out error);
                }

                if (!McpPackageLockProvenance.TryRead(
                        File.ReadAllText(lockPath),
                        package.name,
                        out McpLockGitEntry lockEntry,
                        out string lockError))
                {
                    return Unresolved("missing-lock-entry", lockError, out error);
                }

                provenance.Lock = lockEntry;

                McpServerSourceResolution resolution = McpServerSourceResolver.ResolveManaged(provenance);
                if (!resolution.IsResolved)
                {
                    error = resolution.Error;
                }

                return resolution;
            }
            catch (Exception ex)
            {
                return Unresolved(
                    "provenance-read-failed",
                    $"the installed package provenance could not be read: {ex.Message}",
                    out error);
            }
        }

        /// <summary>
        /// Reads every independent observation of the installed package identity: the package
        /// manager's report, the package's own metadata on disk, and where that path sits.
        /// </summary>
        private static McpInstalledPackageIdentity ReadInstalledIdentity(
            PackageInfo package,
            string packageRoot,
            string projectRoot)
        {
            bool pathExists = false;
            string packageJsonName = null;

            try
            {
                pathExists = !string.IsNullOrEmpty(packageRoot) && Directory.Exists(packageRoot);
                if (pathExists)
                {
                    string packageJsonPath = Path.Combine(packageRoot, "package.json");
                    if (File.Exists(packageJsonPath)
                        && McpInstalledPackageMetadata.TryRead(
                            File.ReadAllText(packageJsonPath), out string name, out _))
                    {
                        packageJsonName = name;
                    }
                }
            }
            catch
            {
                // A path that cannot be inspected simply fails the agreement check below.
            }

            string packageCache = McpRunStatePaths.Canonicalize(
                Path.Combine(projectRoot, "Library", "PackageCache"));

            return new McpInstalledPackageIdentity
            {
                Name = package.name,
                SourceKind = DescribeSource(package.source),
                ResolvedPath = packageRoot,
                PackageJsonName = packageJsonName,
                ResolvedPathExists = pathExists,
                ResolvedPathInsideProjectPackageCache =
                    !string.IsNullOrEmpty(packageRoot)
                    && !string.IsNullOrEmpty(packageCache)
                    && McpRunStatePaths.IsPathInside(packageRoot, packageCache),
            };
        }

        /// <summary>
        /// Normalizes <see cref="PackageSource"/>. Only <see cref="PackageSource.Git"/> can carry an
        /// immutable full-commit identity, so everything else is reported verbatim and refused.
        /// </summary>
        private static string DescribeSource(UnityEditor.PackageManager.PackageSource source)
            => source == UnityEditor.PackageManager.PackageSource.Git
                ? McpServerSourceResolver.GitSourceKind
                : source.ToString().ToLowerInvariant();

        private static McpServerSourceResolution Unresolved(string category, string message, out string error)
        {
            error = message;
            return new McpServerSourceResolution
            {
                IsResolved = false,
                Category = category,
                Error = message,
                Source = null,
            };
        }
    }
}
