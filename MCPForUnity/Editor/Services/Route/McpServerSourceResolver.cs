using System;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Identity of the MCPForUnity package whose assembly is executing, as reported by Unity's
    /// package manager plus the package's own on-disk metadata.
    ///
    /// Every field is an independent observation. A managed route may only be derived when they
    /// agree, so a single forged or stale source cannot name the server to launch.
    /// </summary>
    public sealed class McpInstalledPackageIdentity
    {
        /// <summary><c>PackageInfo.name</c> of the package whose assembly is executing.</summary>
        public string Name;

        /// <summary><c>PackageInfo.source</c>, normalized (expected: "git").</summary>
        public string SourceKind;

        /// <summary><c>PackageInfo.resolvedPath</c> (the folder that holds package.json).</summary>
        public string ResolvedPath;

        /// <summary>
        /// The <c>name</c> declared by the package.json found at <see cref="ResolvedPath"/>.
        /// Read from disk rather than from the package manager, so it is an independent witness.
        /// </summary>
        public string PackageJsonName;

        /// <summary>True when <see cref="ResolvedPath"/> exists as a directory.</summary>
        public bool ResolvedPathExists;

        /// <summary>
        /// True when <see cref="ResolvedPath"/> is inside <c>&lt;project&gt;/Library/PackageCache</c>,
        /// which is where UPM installs a Git dependency. A package loaded from anywhere else is
        /// not the pinned Git install this contract is written for.
        /// </summary>
        public bool ResolvedPathInsideProjectPackageCache;
    }

    /// <summary>A Git dependency declared directly in <c>Packages/manifest.json</c>.</summary>
    public sealed class McpManifestGitDependency
    {
        /// <summary>The dependency key, e.g. <c>com.coplaydev.unity-mcp</c>.</summary>
        public string PackageName;

        /// <summary>The raw manifest value, e.g. <c>https://host/repo.git?path=/MCPForUnity#&lt;sha&gt;</c>.</summary>
        public string RawValue;

        /// <summary>
        /// True only when the dependency appears in the top-level <c>dependencies</c> object.
        /// A package that is merely pulled in transitively must never authorize a managed route.
        /// </summary>
        public bool IsDirectDependency;
    }

    /// <summary>The corresponding entry in <c>Packages/packages-lock.json</c>.</summary>
    public sealed class McpLockGitEntry
    {
        public string PackageName;

        /// <summary>Lockfile <c>source</c> (expected: "git").</summary>
        public string SourceKind;

        /// <summary>Lockfile <c>version</c> (the resolved URL UPM recorded).</summary>
        public string RawVersion;

        /// <summary>Lockfile <c>hash</c> (the resolved commit).</summary>
        public string Revision;

        /// <summary>Lockfile <c>depth</c>: 0 for a direct dependency, &gt;0 for a transitive one.</summary>
        public int Depth;
    }

    /// <summary>
    /// Everything the managed server source is derived from. The package and the compatible Python
    /// server live in the same repository at the same revision, so the only safe managed server
    /// source is "that repository, that exact commit, Server/ subdirectory".
    /// </summary>
    public sealed class McpServerPackageProvenance
    {
        public McpInstalledPackageIdentity Installed;
        public McpManifestGitDependency Manifest;
        public McpLockGitEntry Lock;
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
    /// Derives the managed (supervisor-launched) Python server source from corroborated provenance
    /// of the installed UPM Git package.
    ///
    /// Invariants (any violation fails closed and yields no source):
    ///   * the executing package is exactly <c>com.coplaydev.unity-mcp</c> and is a Git package;
    ///   * its resolved path exists inside the project's PackageCache and its own package.json
    ///     agrees with the package manager's identity;
    ///   * Packages/manifest.json declares it as a DIRECT Git dependency pinned to a FULL 40-hex
    ///     commit with <c>?path=/MCPForUnity</c>;
    ///   * Packages/packages-lock.json records the same repository and the same full commit;
    ///   * the resolved source is that repository at that commit, Server/ subdirectory.
    ///
    /// It never falls back to PyPI, a branch, a tag, a short id, a lock-only entry or a global
    /// override, and it never logs credentials.
    /// </summary>
    public static class McpServerSourceResolver
    {
        /// <summary>Subdirectory of the repository that holds the Python server.</summary>
        public const string ServerSubdirectory = "Server";

        /// <summary>UPM source kind that carries a resolved repository and revision.</summary>
        public const string GitSourceKind = "git";

        /// <summary>The only package this contract may derive a managed server for.</summary>
        public const string ApprovedPackageName = "com.coplaydev.unity-mcp";

        /// <summary>The only manifest subpath this contract may derive a managed server for.</summary>
        public const string ApprovedManifestSubPath = "/MCPForUnity";

        public static McpServerSourceResolution ResolveManaged(McpServerPackageProvenance provenance)
        {
            var result = new McpServerSourceResolution();

            if (provenance == null)
            {
                return Refuse(result, "missing-provenance",
                    "the installed package provenance could not be read.");
            }

            // ---- A. the executing package is the approved package ----------------------
            McpInstalledPackageIdentity installed = provenance.Installed;
            if (installed == null)
            {
                return Refuse(result, "missing-installed-package",
                    "the installed MCP package identity could not be read.");
            }

            if (!string.Equals(installed.Name, ApprovedPackageName, StringComparison.Ordinal))
            {
                return Refuse(result, "unexpected-package",
                    $"the executing package is not '{ApprovedPackageName}', so no managed server "
                    + "source may be derived from it.");
            }

            // ---- B. Unity reports it as a Git package -----------------------------------
            if (string.IsNullOrWhiteSpace(installed.SourceKind)
                || !string.Equals(installed.SourceKind.Trim(), GitSourceKind, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(result, "non-git-package",
                    "the installed MCP package is not a Git package, so no immutable server "
                    + "revision can be derived from it.");
            }

            // ---- C. the resolved path really is this package ---------------------------
            if (string.IsNullOrWhiteSpace(installed.ResolvedPath)
                || !installed.ResolvedPathExists
                || !installed.ResolvedPathInsideProjectPackageCache
                || !string.Equals(
                    LeafName(installed.ResolvedPath), "MCPForUnity", StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(result, "installed-path-mismatch",
                    "the installed package path is not the 'MCPForUnity' folder of a "
                    + "PackageCache Git install, so it does not match this contract.");
            }

            if (!string.Equals(installed.PackageJsonName, installed.Name, StringComparison.Ordinal))
            {
                return Refuse(result, "installed-metadata-mismatch",
                    "the installed package's own package.json does not name the executing package.");
            }

            // ---- D/E. the manifest declares a direct, full-commit-pinned dependency ----
            McpManifestGitDependency manifest = provenance.Manifest;
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.RawValue))
            {
                return Refuse(result, "missing-manifest-dependency",
                    $"Packages/manifest.json has no entry for '{ApprovedPackageName}'.");
            }

            if (!manifest.IsDirectDependency
                || !string.Equals(manifest.PackageName, ApprovedPackageName, StringComparison.Ordinal))
            {
                return Refuse(result, "indirect-manifest-dependency",
                    $"'{ApprovedPackageName}' is not a direct dependency in Packages/manifest.json, "
                    + "so its revision is not the project's own pin.");
            }

            if (!TryParseUpmGitUrl(
                    manifest.RawValue,
                    out string manifestRepository,
                    out string manifestSubPath,
                    out string manifestRevision,
                    out string manifestError))
            {
                return Refuse(result, "malformed-manifest-dependency", manifestError);
            }

            if (!string.Equals(manifestSubPath, ApprovedManifestSubPath, StringComparison.Ordinal))
            {
                return Refuse(result, "unexpected-subpath",
                    $"the manifest Git subpath must be exactly '{ApprovedManifestSubPath}'.");
            }

            if (!IsApprovedRepository(manifestRepository))
            {
                return Refuse(result, "unsupported-repository-form",
                    "the manifest repository must be the approved HTTPS Git form with no credentials.");
            }

            if (!TryNormalizeCommit(manifestRevision, out string manifestCommit))
            {
                return Refuse(result, "floating-or-malformed-revision",
                    "the manifest dependency is not pinned to a full 40-hex commit id (a branch, "
                    + "tag or short id cannot be trusted to name the same tree as the package).");
            }

            // ---- F/G. the lock file agrees exactly -------------------------------------
            McpLockGitEntry lockEntry = provenance.Lock;
            if (lockEntry == null || string.IsNullOrWhiteSpace(lockEntry.RawVersion))
            {
                return Refuse(result, "missing-lock-entry",
                    $"Packages/packages-lock.json has no entry for '{ApprovedPackageName}'.");
            }

            if (!string.Equals(lockEntry.SourceKind, GitSourceKind, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(result, "non-git-lock-entry",
                    "the lock entry for the MCP package is not a Git package.");
            }

            if (lockEntry.Depth != 0)
            {
                return Refuse(result, "indirect-lock-entry",
                    "the lock entry for the MCP package is a transitive dependency, not the pin.");
            }

            if (!TryParseUpmGitUrl(
                    lockEntry.RawVersion,
                    out string lockRepository,
                    out _,
                    out _,
                    out string lockUrlError))
            {
                return Refuse(result, "malformed-lock-entry", lockUrlError);
            }

            if (!IsApprovedRepository(lockRepository))
            {
                return Refuse(result, "unsupported-repository-form",
                    "the lock repository must be the approved HTTPS Git form with no credentials.");
            }

            if (!string.Equals(CanonicalRepository(manifestRepository), CanonicalRepository(lockRepository),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(result, "repository-mismatch",
                    "the manifest and the lock file name different repositories.");
            }

            if (!TryNormalizeCommit(lockEntry.Revision, out string lockCommit))
            {
                return Refuse(result, "floating-or-malformed-revision",
                    "the lock entry is not resolved to a full 40-hex commit id.");
            }

            if (!string.Equals(manifestCommit, lockCommit, StringComparison.Ordinal))
            {
                return Refuse(result, "revision-mismatch",
                    "the manifest pin and the resolved lock revision disagree.");
            }

            // ---- H. the derived source ------------------------------------------------
            string repository = CanonicalRepository(manifestRepository);
            string source = $"git+{repository}@{manifestCommit}#subdirectory={ServerSubdirectory}";
            if (!IsSafeCommandToken(source))
            {
                return Refuse(result, "unsafe-source",
                    "the derived server source is not a safe command token.");
            }

            result.IsResolved = true;
            result.Source = source;
            result.Repository = repository;
            result.Revision = manifestCommit;
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
        /// Parses a UPM Git dependency string of the approved shape
        /// <c>https://host/owner/repo.git?path=/MCPForUnity#&lt;ref&gt;</c>.
        /// Credentials, other schemes and other query keys are refused. Nothing from the raw
        /// string is echoed in an error, because it may carry a credential.
        /// </summary>
        public static bool TryParseUpmGitUrl(
            string upmUrl,
            out string repository,
            out string subPath,
            out string revision,
            out string error)
        {
            repository = null;
            subPath = null;
            revision = null;
            error = null;

            if (string.IsNullOrWhiteSpace(upmUrl))
            {
                error = "the dependency value is empty.";
                return false;
            }

            string trimmed = upmUrl.Trim();

            string fragment = null;
            int hashIndex = trimmed.IndexOf('#');
            if (hashIndex >= 0)
            {
                fragment = trimmed.Substring(hashIndex + 1);
                trimmed = trimmed.Substring(0, hashIndex);
            }

            string query = null;
            int queryIndex = trimmed.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = trimmed.Substring(queryIndex + 1);
                trimmed = trimmed.Substring(0, queryIndex);
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri))
            {
                error = "the dependency repository value is not an absolute URL.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                // Never echo the value: it may embed a token.
                error = "the dependency repository URL embeds credentials; refusing to use it.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                error = "the dependency repository URL carries an unexpected fragment.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query))
            {
                error = "the dependency repository URL carries an unexpected query string.";
                return false;
            }

            string subPathValue = null;
            if (query != null)
            {
                // Deliberately narrow: exactly one 'path' parameter with one unambiguous value.
                // Duplicates, unknown keys, empty components and encoded separators are all
                // refused rather than resolved to whichever component the parser happened to
                // keep last - two readers must never disagree about which sub-path this names.
                string[] parts = query.Split('&');
                if (parts.Length != 1)
                {
                    error = "the dependency URL carries an ambiguous query string; "
                            + "exactly one 'path' parameter is allowed.";
                    return false;
                }

                string part = parts[0];
                int equals = part.IndexOf('=');
                if (equals <= 0)
                {
                    error = "the dependency URL carries a malformed query parameter.";
                    return false;
                }

                string key = part.Substring(0, equals);
                if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"the dependency URL carries an unsupported query parameter '{key}'.";
                    return false;
                }

                if (!TryDecodeUnambiguousQueryValue(part.Substring(equals + 1), out string pathValue))
                {
                    error = "the dependency URL's 'path' parameter is empty or not an "
                            + "unambiguous path value.";
                    return false;
                }

                subPathValue = pathValue;
            }

            string path = uri.AbsolutePath;
            if (path.Length <= 1 || path == "/")
            {
                error = "the dependency repository URL has no repository path.";
                return false;
            }

            // UriComponents.Path is unescaped inconsistently across runtimes; rebuild from parts so
            // the canonical form is deterministic.
            var builder = new StringBuilder();
            builder.Append(uri.Scheme.ToLowerInvariant()).Append("://");
            builder.Append(uri.Host.ToLowerInvariant());
            if (!uri.IsDefaultPort)
            {
                builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(path.TrimEnd('/'));
            repository = builder.ToString();

            subPath = subPathValue;
            revision = fragment;
            return true;
        }

        /// <summary>
        /// The approved managed repository form: HTTPS, no credentials, a DNS host (never a literal
        /// address) and a repository path ending in <c>.git</c>. Anything else - including any SSH
        /// form and any credential-bearing HTTPS URL - is refused rather than normalized.
        /// </summary>
        public static bool IsApprovedRepository(string repository)
        {
            if (string.IsNullOrWhiteSpace(repository))
            {
                return false;
            }

            if (!Uri.TryCreate(repository.Trim(), UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                return false;
            }

            // A literal address (v4 or v6) is not an approved repository host.
            if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out System.Net.IPAddress _))
            {
                return false;
            }

            if (!uri.IsDefaultPort && uri.Port != 443)
            {
                return false;
            }

            string path = uri.AbsolutePath;
            if (!path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] segments = path.Trim('/').Split('/');
            if (segments.Length < 2
                || string.IsNullOrEmpty(segments[0])
                || string.IsNullOrEmpty(segments[segments.Length - 1]))
            {
                return false;
            }

            return IsSafeCommandToken(repository.Trim());
        }

        /// <summary>Canonical spelling of an already-approved repository URL.</summary>
        public static string CanonicalRepository(string repository)
        {
            if (!Uri.TryCreate(repository?.Trim(), UriKind.Absolute, out Uri uri))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append(Uri.UriSchemeHttps).Append("://");
            builder.Append(uri.Host.ToLowerInvariant());
            if (!uri.IsDefaultPort && uri.Port != 443)
            {
                builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(uri.AbsolutePath.TrimEnd('/'));
            return builder.ToString();
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

        /// <summary>
        /// Decodes one UPM query-parameter value, refusing anything that could make a single
        /// component read as more than one.
        ///
        /// Percent escapes are decoded, but an escape that would reintroduce a query separator
        /// (<c>&amp; = ? # %</c>) is refused: <c>?path=/A%26path=/B</c> names two paths to a
        /// permissive reader and must not be silently collapsed to one. Malformed escapes are
        /// refused too, so an undecodable value never falls through as a raw string.
        /// </summary>
        private static bool TryDecodeUnambiguousQueryValue(string raw, out string decoded)
        {
            decoded = null;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            var builder = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '&' || c == '=' || c == '?' || c == '#')
                {
                    return false;
                }

                if (c != '%')
                {
                    builder.Append(c);
                    continue;
                }

                if (i + 2 >= raw.Length)
                {
                    return false;
                }

                int high = HexDigitValue(raw[i + 1]);
                int low = HexDigitValue(raw[i + 2]);
                if (high < 0 || low < 0)
                {
                    return false;
                }

                char unescaped = (char)((high << 4) | low);
                if (unescaped == '&' || unescaped == '=' || unescaped == '?'
                    || unescaped == '#' || unescaped == '%')
                {
                    return false;
                }

                builder.Append(unescaped);
                i += 2;
            }

            decoded = builder.ToString();
            return decoded.Length > 0;
        }

        private static int HexDigitValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim().TrimEnd('/', '\\');
            int index = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            return index < 0 ? trimmed : trimmed.Substring(index + 1);
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
    /// Reads the direct Git dependency declared for a package in <c>Packages/manifest.json</c>.
    ///
    /// A package that is only present transitively is never a project-owned pin, so an absent
    /// top-level entry fails closed rather than being satisfied by a transitive edge elsewhere
    /// in the file.
    /// </summary>
    public static class McpPackageManifestProvenance
    {
        public static bool TryRead(
            string manifestJson,
            string packageName,
            out McpManifestGitDependency dependency,
            out string error)
        {
            dependency = null;
            error = null;

            if (string.IsNullOrWhiteSpace(packageName))
            {
                error = "no package name was supplied.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                error = "Packages/manifest.json could not be read.";
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(manifestJson);
            }
            catch (Exception ex)
            {
                error = $"Packages/manifest.json is not valid JSON: {ex.Message}";
                return false;
            }

            if (root["dependencies"] is not JObject dependencies
                || dependencies[packageName] is not JValue value
                || value.Type != JTokenType.String)
            {
                error = $"Packages/manifest.json has no direct dependency named '{packageName}'.";
                return false;
            }

            dependency = new McpManifestGitDependency
            {
                PackageName = packageName,
                RawValue = value.Value<string>(),
                IsDirectDependency = true,
            };
            return true;
        }
    }

    /// <summary>
    /// Reads the installed package's own <c>package.json</c>. This is an independent witness of
    /// the package identity at the resolved path, not a restatement of the package manager.
    /// </summary>
    public static class McpInstalledPackageMetadata
    {
        public static bool TryRead(string packageJson, out string name, out string error)
        {
            name = null;
            error = null;

            if (string.IsNullOrWhiteSpace(packageJson))
            {
                error = "the installed package's package.json could not be read.";
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(packageJson);
            }
            catch (Exception ex)
            {
                error = $"the installed package's package.json is not valid JSON: {ex.Message}";
                return false;
            }

            string parsed = root.Value<string>("name");
            if (string.IsNullOrWhiteSpace(parsed))
            {
                error = "the installed package's package.json does not declare a name.";
                return false;
            }

            name = parsed.Trim();
            return true;
        }
    }

    /// <summary>
    /// Reads the installed-package entry out of the project's UPM lock file
    /// (<c>&lt;project&gt;/Packages/packages-lock.json</c>).
    ///
    /// The lock file is UPM's own record of what is installed, and for a Git package it carries
    /// both the resolved repository and the full resolved commit ("hash"). It is corroboration,
    /// never the sole authority: the manifest pin and the installed package identity must agree.
    /// </summary>
    public static class McpPackageLockProvenance
    {
        public static bool TryRead(
            string lockFileJson,
            string packageName,
            out McpLockGitEntry entry,
            out string error)
        {
            entry = null;
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
                || dependencies[packageName] is not JObject packageEntry)
            {
                error = $"Packages/packages-lock.json has no entry for '{packageName}'.";
                return false;
            }

            string sourceKind = packageEntry.Value<string>("source");
            string version = packageEntry.Value<string>("version");
            string hash = packageEntry.Value<string>("hash");

            if (string.IsNullOrWhiteSpace(sourceKind))
            {
                error = $"the lock entry for '{packageName}' has no source kind.";
                return false;
            }

            if (!string.Equals(sourceKind.Trim(), McpServerSourceResolver.GitSourceKind,
                    StringComparison.OrdinalIgnoreCase))
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

            // An explicit, integer, zero 'depth' is required. A missing, null, non-integer or
            // non-zero depth is refused: inferring "direct dependency" from a default would let a
            // transitively-installed copy of the package authorize a managed route.
            JToken depthToken = packageEntry["depth"];
            if (depthToken == null || depthToken.Type != JTokenType.Integer)
            {
                error = $"the lock entry for '{packageName}' does not declare an explicit "
                        + "integer 'depth'.";
                return false;
            }

            long depthValue = depthToken.Value<long>();
            if (depthValue != 0)
            {
                error = $"the lock entry for '{packageName}' has depth {depthValue}; only a direct "
                        + "(depth 0) dependency may authorize a managed route.";
                return false;
            }

            entry = new McpLockGitEntry
            {
                PackageName = packageName,
                SourceKind = sourceKind.Trim(),
                RawVersion = version.Trim(),
                Revision = hash.Trim(),
                Depth = 0,
            };
            return true;
        }
    }
}
