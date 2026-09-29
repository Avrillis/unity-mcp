using System;
using System.Collections.Specialized;
using System.IO;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Outcome of preparing the managed uv cache root for a managed server launch.
    /// </summary>
    public sealed class McpManagedCachePreparation
    {
        /// <summary>True only when the root is validated, present and provably writable.</summary>
        public bool Ok { get; internal set; }

        /// <summary>Canonical absolute managed cache root. Meaningful only when <see cref="Ok"/>.</summary>
        public string Root { get; internal set; }

        /// <summary>Stable machine-readable reason. Never contains credentials.</summary>
        public string Reason { get; internal set; }

        /// <summary>Human-readable detail suitable for a launch failure message.</summary>
        public string Detail { get; internal set; }

        /// <summary>True when the root already existed before this preparation.</summary>
        public bool AlreadyExisted { get; internal set; }
    }

    /// <summary>
    /// Outcome of applying the managed cache root to one child environment block.
    /// </summary>
    public sealed class McpManagedCacheApplication
    {
        /// <summary>True when the child environment was given the managed cache root.</summary>
        public bool Applied { get; internal set; }

        /// <summary>True when a differing inherited value was replaced for this child only.</summary>
        public bool OverrodeInheritedValue { get; internal set; }

        /// <summary>The inherited value that was replaced, or null when none was present.</summary>
        public string InheritedValue { get; internal set; }

        /// <summary>The value written to the child environment.</summary>
        public string AppliedValue { get; internal set; }
    }

    /// <summary>
    /// Prepares the deliberately short per-user uv cache that a managed immutable server launch
    /// must use.
    ///
    /// The managed server source is fetched by uv from the reviewed repository revision. uv
    /// materializes that fetch beneath its cache directory, and the inherited default cache on
    /// Windows is long enough that a real checkout can exceed the platform path limit and fail
    /// before any server process exists. A short, stable, per-user root removes that dependency on
    /// the inherited location without changing what is launched.
    ///
    /// Scope and non-goals:
    ///   * the root is plan/route/worker independent - it is NOT provenance, NOT authority and NOT
    ///     route identity, and it is never read as evidence;
    ///   * nothing global is touched: no registry, no persist flags, no shell tooling, no
    ///     repository-tool configuration and no change to this process's environment;
    ///   * the only environment change is a child-only entry on the supplied environment block;
    ///   * existing cache contents are never removed.
    ///
    /// The helper is deliberately dependency-free so it can be compiled and unit tested without an
    /// Editor.
    /// </summary>
    public static class McpManagedServerCache
    {
        /// <summary>Environment variable uv reads for its cache root.</summary>
        public const string CacheDirectoryEnvironmentVariable = "UV_CACHE_DIR";

        /// <summary>Directory name used directly beneath the user profile.</summary>
        public const string ManagedCacheDirectoryName = ".swm-uv";

        /// <summary>
        /// The managed cache root for the current user, or an empty string when no user profile is
        /// available. Resolution is deterministic and independent of the repository, the current
        /// directory, the worker slot, the task and the route port.
        /// </summary>
        public static string ResolveManagedCacheRoot()
        {
            string profile;
            try
            {
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(profile))
            {
                return string.Empty;
            }

            try
            {
                return McpRunStatePaths.Canonicalize(Path.Combine(profile, ManagedCacheDirectoryName));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Validates and prepares the managed cache root. Never throws for a bad environment: every
        /// failure is returned as a structured result so the caller can fail the launch closed.
        /// </summary>
        /// <param name="projectRoot">
        /// Canonical Unity project root, used only to prove the cache is not the project root, is
        /// not inside the project and is not inside the project's Library directory.
        /// </param>
        /// <param name="cacheRoot">
        /// Optional explicit root. Production callers omit it so the managed per-user default is
        /// used; tests supply isolated temporary roots. An explicitly supplied but blank value is
        /// invalid rather than silently replaced by the default.
        /// </param>
        public static McpManagedCachePreparation Prepare(
            string projectRoot,
            string cacheRoot = null)
        {
            var result = new McpManagedCachePreparation();

            string requested = cacheRoot == null ? ResolveManagedCacheRoot() : cacheRoot;
            if (string.IsNullOrWhiteSpace(requested))
            {
                return Fail(
                    result,
                    "MANAGED_CACHE_ROOT_UNRESOLVED",
                    "The managed uv cache root could not be derived for this user, so the managed "
                    + "server was not launched.");
            }

            if (!Path.IsPathRooted(requested))
            {
                return Fail(
                    result,
                    "MANAGED_CACHE_ROOT_RELATIVE",
                    $"The managed uv cache root '{requested}' is not an absolute path, so the "
                    + "managed server was not launched.");
            }

            string canonical;
            try
            {
                canonical = McpRunStatePaths.Canonicalize(requested);
            }
            catch
            {
                canonical = string.Empty;
            }

            if (canonical.Length == 0)
            {
                return Fail(
                    result,
                    "MANAGED_CACHE_ROOT_INVALID",
                    $"The managed uv cache root '{requested}' could not be canonicalized, so the "
                    + "managed server was not launched.");
            }

            result.Root = canonical;

            string project = string.Empty;
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                try
                {
                    project = McpRunStatePaths.Canonicalize(projectRoot);
                }
                catch
                {
                    project = string.Empty;
                }
            }

            if (project.Length > 0)
            {
                if (McpRunStatePaths.PathsEqual(canonical, project))
                {
                    return Fail(
                        result,
                        "MANAGED_CACHE_ROOT_IS_PROJECT_ROOT",
                        "The managed uv cache root resolves to the Unity project root, so the "
                        + "managed server was not launched.");
                }

                string library = McpRunStatePaths.Canonicalize(Path.Combine(project, "Library"));
                if (library.Length > 0 && McpRunStatePaths.IsPathInside(canonical, library))
                {
                    return Fail(
                        result,
                        "MANAGED_CACHE_ROOT_INSIDE_LIBRARY",
                        "The managed uv cache root is inside the Unity Library directory, so the "
                        + "managed server was not launched.");
                }

                if (McpRunStatePaths.IsPathInside(canonical, project))
                {
                    return Fail(
                        result,
                        "MANAGED_CACHE_ROOT_INSIDE_PROJECT",
                        "The managed uv cache root is inside the Unity project, so the managed "
                        + "server was not launched.");
                }
            }

            try
            {
                bool existed = Directory.Exists(canonical);
                Directory.CreateDirectory(canonical);
                if (!Directory.Exists(canonical))
                {
                    return Fail(
                        result,
                        "MANAGED_CACHE_ROOT_UNUSABLE",
                        $"The managed uv cache root '{canonical}' is not a usable directory, so the "
                        + "managed server was not launched.");
                }

                // Prove the root is writable. The probe is uniquely named, is created with
                // delete-on-close and is the only entry this helper ever removes; existing cache
                // contents are untouched.
                string probe = Path.Combine(
                    canonical, ".swm-uv-probe-" + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(
                           probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                           FileOptions.DeleteOnClose))
                {
                    stream.WriteByte(0);
                }

                result.AlreadyExisted = existed;
            }
            catch (Exception ex)
            {
                return Fail(
                    result,
                    "MANAGED_CACHE_ROOT_UNUSABLE",
                    $"The managed uv cache root '{canonical}' could not be created or written "
                    + $"({ex.GetType().Name}), so the managed server was not launched.");
            }

            result.Ok = true;
            result.Reason = "MANAGED_CACHE_READY";
            result.Detail = $"The managed uv cache root '{canonical}' is ready.";
            return result;
        }

        /// <summary>
        /// Points one child environment block at the managed cache root.
        ///
        /// A differing inherited value is replaced for this child only; the inherited value itself
        /// is reported back to the caller so the launch can record a single warning. An inherited
        /// value that already resolves to the managed root is accepted as-is.
        /// </summary>
        public static McpManagedCacheApplication ApplyTo(StringDictionary environment, string cacheRoot)
        {
            var result = new McpManagedCacheApplication();

            string canonical;
            try
            {
                canonical = McpRunStatePaths.Canonicalize(cacheRoot);
            }
            catch
            {
                canonical = string.Empty;
            }

            if (environment == null || canonical.Length == 0)
            {
                return result;
            }

            string inherited = null;
            if (environment.ContainsKey(CacheDirectoryEnvironmentVariable))
            {
                inherited = environment[CacheDirectoryEnvironmentVariable];
            }

            environment[CacheDirectoryEnvironmentVariable] = canonical;
            result.Applied = true;
            result.AppliedValue = canonical;
            result.InheritedValue = inherited;
            result.OverrodeInheritedValue =
                !string.IsNullOrWhiteSpace(inherited)
                && !McpRunStatePaths.PathsEqual(inherited, canonical);
            return result;
        }

        private static McpManagedCachePreparation Fail(
            McpManagedCachePreparation result, string reason, string detail)
        {
            result.Ok = false;
            result.Reason = reason;
            result.Detail = detail;
            return result;
        }
    }
}
