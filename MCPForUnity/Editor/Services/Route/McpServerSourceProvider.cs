using System;
using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Reads the provenance of the installed MCPForUnity UPM package and derives the Python
    /// server source a managed route must use.
    ///
    /// Unity keeps only the package subfolder for a <c>?path=/MCPForUnity</c> Git install, so the
    /// compatible <c>Server/</c> tree cannot be read from disk next to the package. The lock file
    /// records the resolved repository and the FULL resolved commit, which is what lets the
    /// managed launch pin the server to exactly the revision of the package it is paired with.
    /// </summary>
    public static class McpServerSourceProvider
    {
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

                string projectRoot = McpRouteStateStore.ResolveProjectRoot();
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return Unresolved("missing-project-root", "the Unity project root could not be resolved.", out error);
                }

                string lockPath = Path.Combine(
                    projectRoot, "Packages", "packages-lock.json");
                if (!File.Exists(lockPath))
                {
                    return Unresolved(
                        "missing-lockfile",
                        $"{LockFileRelativePath} is missing, so the installed package revision is unknown.",
                        out error);
                }

                string lockJson = File.ReadAllText(lockPath);
                if (!McpPackageLockProvenance.TryRead(
                        lockJson,
                        package.name,
                        package.resolvedPath,
                        out McpServerPackageProvenance provenance,
                        out string readError))
                {
                    return Unresolved("missing-provenance", readError, out error);
                }

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
