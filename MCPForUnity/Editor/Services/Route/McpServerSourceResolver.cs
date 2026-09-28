using System;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Immutable provenance of the installed MCPForUnity UPM package.
    ///
    /// The package and the compatible Python server live in the same repository at the same
    /// revision, so the only safe managed-server source is "that repository, that exact commit,
    /// Server/ subdirectory". Equal version strings prove nothing about the Python code.
    /// </summary>
    public sealed class McpServerPackageProvenance
    {
        public string PackageName;

        /// <summary>UPM source kind (expected: "git").</summary>
        public string SourceKind;

        /// <summary>Resolved repository URL, without a ref, query or credentials.</summary>
        public string RepositoryUrl;

        /// <summary>Resolved revision exactly as recorded by UPM (expected: a full commit id).</summary>
        public string ResolvedRevision;

        /// <summary>Absolute install path of the package (the MCPForUnity/ folder).</summary>
        public string PackageResolvedPath;
    }

    /// <summary>Outcome of resolving the managed server source.</summary>
    public sealed class McpServerSourceResolution
    {
        public bool IsResolved { get; internal set; }

        /// <summary>The uvx <c>--from</c> value, e.g. git+https://host/repo.git@&lt;sha&gt;#subdirectory=Server.</summary>
        public string Source { get; internal set; }

        public string Repository { get; internal set; }
        public string Revision { get; internal set; }

        /// <summary>Human-readable refusal reason; never contains credentials.</summary>
        public string Error { get; internal set; }

        /// <summary>Stable category for logging/tests (never contains credentials).</summary>
        public string Category { get; internal set; }
    }

    /// <summary>
    /// Derives the managed (supervisor-launched) Python server source from the immutable
    /// provenance of the installed UPM Git package.
    ///
    /// Invariants (any violation fails closed and yields no source):
    ///   * the package must come from a Git source;
    ///   * the repository must be a well-formed remote URL with no credentials/query/fragment;
    ///   * the revision must be a FULL commit id, never a branch, tag or short id;
    ///   * the resolved source is that repository at that tree, Server/ subdirectory.
    ///
    /// It never falls back to PyPI and never goes through a global override.
    /// </summary>
    public static class McpServerSourceResolver
    {
        /// <summary>Subdirectory of the repository that holds the Python server.</summary>
        public const string ServerSubdirectory = "Server";

        /// <summary>UPM source kind that carries a resolved repository and revision.</summary>
        public const string GitSourceKind = "git";

        public static McpServerSourceResolution ResolveManaged(McpServerPackageProvenance provenance)
        {
            var result = new McpServerSourceResolution();

            if (provenance == null)
            {
                return Refuse(result, "missing-provenance",
                    "the installed package provenance could not be read.");
            }

            if (string.IsNullOrWhiteSpace(provenance.SourceKind)
                || !string.Equals(provenance.SourceKind.Trim(), GitSourceKind, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(result, "non-git-package",
                    "the installed MCP package is not a Git package, so no immutable server "
                    + "revision can be derived from it.");
            }

            if (!TryNormalizeRepositoryUrl(provenance.RepositoryUrl, out string repository, out string urlError))
            {
                return Refuse(result, "malformed-repository", urlError);
            }

            if (!TryNormalizeCommit(provenance.ResolvedRevision, out string commit))
            {
                return Refuse(result, "floating-or-malformed-revision",
                    "the installed MCP package is not pinned to a full commit id (a branch, tag "
                    + "or short id cannot be trusted to name the same tree as the package).");
            }

            string source = $"git+{repository}@{commit}#subdirectory={ServerSubdirectory}";
            if (!IsSafeCommandToken(source))
            {
                return Refuse(result, "unsafe-source",
                    "the derived server source is not a safe command token.");
            }

            result.IsResolved = true;
            result.Source = source;
            result.Repository = repository;
            result.Revision = commit;
            return result;
        }

        /// <summary>
        /// True when <paramref name="revision"/> is exactly 40 hexadecimal characters (no
        /// short id, branch, tag or prefix is accepted).
        /// </summary>
        public static bool TryNormalizeCommit(string revision, out string commit)
        {
            commit = null;
            if (string.IsNullOrWhiteSpace(revision))
            {
                return false;
            }

            string trimmed = revision.Trim();
            if (trimmed.Length != 40)
            {
                return false;
            }

            foreach (char c in trimmed)
            {
                bool hex = (c >= '0' && c <= '9')
                           || (c >= 'a' && c <= 'f')
                           || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            commit = trimmed.ToLowerInvariant();
            return true;
        }

        /// <summary>
        /// Normalizes a git remote URL for use in a <c>git+</c> requirement.
        /// Rejects userinfo (credentials), query strings, fragments, whitespace and non-git
        /// schemes rather than passing them through to a command line or a log.
        /// </summary>
        public static bool TryNormalizeRepositoryUrl(string url, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                error = "the installed package has no repository URL.";
                return false;
            }

            string trimmed = url.Trim();

            // scp-like form: git@host:owner/repo.git
            int colon = trimmed.IndexOf(':');
            bool scpLike = colon > 0
                           && trimmed.IndexOf("://", StringComparison.Ordinal) < 0
                           && trimmed.Substring(0, colon).IndexOf('@') >= 0;
            if (scpLike)
            {
                string host = trimmed.Substring(colon + 1).TrimStart('/');
                trimmed = $"ssh://{trimmed.Substring(0, colon)}/{host}";
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri))
            {
                error = "the installed package repository URL is not an absolute URL.";
                return false;
            }

            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "https" && scheme != "http" && scheme != "ssh" && scheme != "git")
            {
                error = $"the installed package uses an unsupported repository scheme '{scheme}'.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                // http(s) never needs userinfo for a clone, and anything there could be an
                // embedded token. ssh conventionally carries a bare username ("git@"), which is
                // not a secret; a password separator is refused in every scheme.
                bool passwordPresent = uri.UserInfo.IndexOf(':') >= 0;
                bool usernameAllowed = scheme == "ssh" || scheme == "git";

                if (passwordPresent || !usernameAllowed)
                {
                    error = "the installed package repository URL embeds credentials; refusing to use it.";
                    return false;
                }
            }

            if (string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath.Length <= 1)
            {
                error = "the installed package repository URL has no host or path.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "the installed package repository URL carries a query or fragment.";
                return false;
            }

            string path = uri.AbsolutePath.TrimEnd('/');
            string authority = string.IsNullOrEmpty(uri.UserInfo)
                ? uri.Host
                : $"{uri.UserInfo}@{uri.Host}";
            string repository =
                $"{scheme}://{authority}"
                + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture))
                + path;
            if (!IsSafeCommandToken(repository))
            {
                error = "the installed package repository URL is not a safe command token.";
                return false;
            }

            normalized = repository;
            return true;
        }

        private static bool IsSafeCommandToken(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '`')
                {
                    return false;
                }
            }

            return true;
        }

        private static McpServerSourceResolution Refuse(
            McpServerSourceResolution result,
            string category,
            string message)
        {
            result.IsResolved = false;
            result.Source = null;
            result.Category = category;
            result.Error = message;
            return result;
        }
    }

    /// <summary>
    /// Reads the installed-package provenance out of the project's UPM lock file
    /// (<c>&lt;project&gt;/Packages/packages-lock.json</c>).
    ///
    /// The lock file is UPM's own record of what is actually installed, and for a Git package it
    /// carries both the resolved repository and the full resolved commit ("hash"). That is the
    /// only locally authoritative statement of "package at revision R" - which is exactly what
    /// the compatible server source has to match.
    /// </summary>
    public static class McpPackageLockProvenance
    {
        public static bool TryRead(
            string lockFileJson,
            string packageName,
            string packageResolvedPath,
            out McpServerPackageProvenance provenance,
            out string error)
        {
            provenance = null;
            error = null;

            if (string.IsNullOrWhiteSpace(packageName))
            {
                error = "no package name was supplied.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(lockFileJson))
            {
                error = $"'{packageName}' is not recorded in Packages/packages-lock.json.";
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(lockFileJson);
            }
            catch (Exception ex)
            {
                error = $"Packages/packages-lock.json is not valid JSON: {ex.Message}";
                return false;
            }

            if (root["dependencies"] is not JObject dependencies
                || dependencies[packageName] is not JObject entry)
            {
                error = $"Packages/packages-lock.json has no entry for '{packageName}'.";
                return false;
            }

            string sourceKind = entry.Value<string>("source");
            string version = entry.Value<string>("version");
            string hash = entry.Value<string>("hash");

            if (string.IsNullOrWhiteSpace(sourceKind))
            {
                error = $"the lock entry for '{packageName}' has no source kind.";
                return false;
            }

            if (!string.Equals(sourceKind.Trim(), McpServerSourceResolver.GitSourceKind, StringComparison.OrdinalIgnoreCase))
            {
                error = $"the lock entry for '{packageName}' is not a Git package (source '{sourceKind}').";
                return false;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                error = $"the lock entry for '{packageName}' has no resolved version/URL.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(hash))
            {
                error = $"the lock entry for '{packageName}' has no resolved commit.";
                return false;
            }

            provenance = new McpServerPackageProvenance
            {
                PackageName = packageName,
                SourceKind = sourceKind.Trim(),
                RepositoryUrl = StripGitRef(version),
                ResolvedRevision = hash.Trim(),
                PackageResolvedPath = packageResolvedPath,
            };
            return true;
        }

        /// <summary>
        /// Removes the <c>?path=</c> query and <c>#ref</c> fragment from a UPM Git URL so only the
        /// repository identity remains.
        /// </summary>
        public static string StripGitRef(string upmUrl)
        {
            if (string.IsNullOrWhiteSpace(upmUrl))
            {
                return upmUrl;
            }

            string trimmed = upmUrl.Trim();
            int hash = trimmed.IndexOf('#');
            if (hash >= 0)
            {
                trimmed = trimmed.Substring(0, hash);
            }

            int query = trimmed.IndexOf('?');
            if (query >= 0)
            {
                trimmed = trimmed.Substring(0, query);
            }

            return trimmed.TrimEnd('/');
        }
    }
}
