using System;
using System.IO;
using Newtonsoft.Json;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Path helpers for the project-local MCP run state.
    ///
    /// Everything a managed editor writes lives under
    /// <c>&lt;project&gt;/Library/MCPForUnity/RunState</c>. Keeping the location inside the
    /// project (rather than EditorPrefs, which is machine-global) is what stops one editor's
    /// record from being mistaken for another's. The location is storage only; the record is
    /// never sufficient proof of ownership on its own.
    /// </summary>
    public static class McpRunStatePaths
    {
        public const string HandshakeFileName = "handshake.json";

        /// <summary>Absolute RunState directory for a project root.</summary>
        public static string GetRunStateDirectory(string canonicalProjectRoot)
        {
            if (string.IsNullOrWhiteSpace(canonicalProjectRoot))
            {
                return string.Empty;
            }

            return Path.Combine(
                Canonicalize(canonicalProjectRoot),
                "Library",
                "MCPForUnity",
                "RunState");
        }

        /// <summary>Absolute handshake record path for a project root.</summary>
        public static string GetHandshakePath(string canonicalProjectRoot)
        {
            string directory = GetRunStateDirectory(canonicalProjectRoot);
            return string.IsNullOrEmpty(directory)
                ? string.Empty
                : Path.Combine(directory, HandshakeFileName);
        }

        /// <summary>Absolute per-port server PID evidence path for a project root.</summary>
        public static string GetPidFilePath(string canonicalProjectRoot, int port)
        {
            string directory = GetRunStateDirectory(canonicalProjectRoot);
            return string.IsNullOrEmpty(directory) || port <= 0
                ? string.Empty
                : Path.Combine(directory, $"mcp_http_{port}.pid");
        }

        /// <summary>
        /// Full-path normalization used for every ownership comparison. Relative segments
        /// are resolved and trailing separators removed so two spellings of the same path
        /// compare equal.
        /// </summary>
        public static string Canonicalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string full = path.Trim();
            try
            {
                full = Path.GetFullPath(full);
            }
            catch
            {
                // Keep the trimmed original when the platform rejects it.
            }

            if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
            {
                full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            string root = Path.GetPathRoot(full) ?? string.Empty;
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length < root.Length ? root : trimmed;
        }

        /// <summary>True when either path looks like a Windows path (drive letter or backslash).</summary>
        public static bool LooksLikeWindowsPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (path.IndexOf('\\') >= 0)
            {
                return true;
            }

            return path.Length >= 2 && path[1] == ':';
        }

        /// <summary>Case-insensitive only where the platform is.</summary>
        public static StringComparison ComparisonFor(string path)
        {
            bool windows = Path.DirectorySeparatorChar == '\\' || LooksLikeWindowsPath(path);
            return windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        /// <summary>Canonical, platform-correct equality for two filesystem paths.</summary>
        public static bool PathsEqual(string left, string right)
        {
            string a = Canonicalize(left);
            string b = Canonicalize(right);
            if (a.Length == 0 || b.Length == 0)
            {
                return false;
            }

            return string.Equals(a, b, ComparisonFor(a));
        }

        /// <summary>
        /// True when <paramref name="candidatePath"/> is the same as, or nested under,
        /// <paramref name="directoryPath"/>. Used to prove every path a cleanup would touch
        /// stays inside that project's RunState directory.
        /// </summary>
        public static bool IsPathInside(string candidatePath, string directoryPath)
        {
            string candidate = Canonicalize(candidatePath);
            string directory = Canonicalize(directoryPath);
            if (candidate.Length == 0 || directory.Length == 0)
            {
                return false;
            }

            StringComparison comparison = ComparisonFor(directory);
            if (string.Equals(candidate, directory, comparison))
            {
                return true;
            }

            string prefix = directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directory
                : directory + Path.DirectorySeparatorChar;

            return candidate.StartsWith(prefix, comparison);
        }
    }

    /// <summary>
    /// Versioned, atomically written project-local ownership record for one managed
    /// dedicated MCP server route.
    ///
    /// The record correlates a project root, an editor lifetime, a server lifetime, an
    /// endpoint and a per-launch nonce. It is evidence, never proof: every stop decision
    /// re-corroborates it against live OS observations (see <see cref="McpOwnershipEvaluator"/>).
    /// </summary>
    public sealed class McpRunStateRecord
    {
        public const int CurrentSchemaVersion = 1;

        public const string LifecycleStarting = "starting";
        public const string LifecycleRunning = "running";
        public const string LifecycleStopping = "stopping";

        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("canonical_project_root")]
        public string CanonicalProjectRoot { get; set; }

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; }

        [JsonProperty("editor_pid")]
        public int EditorPid { get; set; }

        [JsonProperty("editor_start_utc")]
        public string EditorStartUtc { get; set; }

        [JsonProperty("server_pid")]
        public int ServerPid { get; set; }

        [JsonProperty("server_start_utc")]
        public string ServerStartUtc { get; set; }

        [JsonProperty("instance_token")]
        public string InstanceToken { get; set; }

        [JsonProperty("pidfile_path")]
        public string PidFilePath { get; set; }

        [JsonProperty("lifecycle_state")]
        public string LifecycleState { get; set; } = LifecycleStarting;

        [JsonProperty("written_utc")]
        public string WrittenUtc { get; set; }

        /// <summary>Serializes with the package's pinned, explicit JSON shape.</summary>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        /// <summary>
        /// Parses a record. Malformed JSON, unknown/absent schema versions and missing
        /// required fields all fail (return false) rather than yielding a partial record.
        /// </summary>
        public static bool TryParse(string json, out McpRunStateRecord record, out string error)
        {
            record = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the record is empty.";
                return false;
            }

            McpRunStateRecord parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<McpRunStateRecord>(json);
            }
            catch (Exception ex)
            {
                error = $"the record is not valid JSON: {ex.Message}";
                return false;
            }

            if (parsed == null)
            {
                error = "the record deserialized to null.";
                return false;
            }

            if (!parsed.IsStructurallyValid(out string structuralError))
            {
                error = structuralError;
                return false;
            }

            record = parsed;
            return true;
        }

        /// <summary>
        /// Structural (schema-level) validation only. Ownership validation against live
        /// process observations is a separate, stronger step.
        /// </summary>
        public bool IsStructurallyValid(out string error)
        {
            error = null;

            if (SchemaVersion != CurrentSchemaVersion)
            {
                error = $"unsupported schema_version {SchemaVersion} (expected {CurrentSchemaVersion}).";
                return false;
            }

            if (string.IsNullOrWhiteSpace(CanonicalProjectRoot))
            {
                error = "canonical_project_root is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Endpoint))
            {
                error = "endpoint is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(InstanceToken))
            {
                error = "instance_token is missing.";
                return false;
            }

            if (EditorPid <= 0)
            {
                error = "editor_pid is missing or invalid.";
                return false;
            }

            if (!TryParseUtc(EditorStartUtc, out _))
            {
                error = "editor_start_utc is missing or malformed.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(PidFilePath))
            {
                error = "pidfile_path is missing.";
                return false;
            }

            // The record is written before the server process exists (so the nonce survives a
            // domain reload during launch). In that window the server lifetime is legitimately
            // unknown. Only a record that claims to be running must carry a full server identity.
            if (!string.Equals(LifecycleState, LifecycleStarting, StringComparison.Ordinal)
                && !string.Equals(LifecycleState, LifecycleRunning, StringComparison.Ordinal)
                && !string.Equals(LifecycleState, LifecycleStopping, StringComparison.Ordinal))
            {
                error = $"lifecycle_state '{LifecycleState}' is not a known state.";
                return false;
            }

            if (string.Equals(LifecycleState, LifecycleStarting, StringComparison.Ordinal))
            {
                return true;
            }

            if (ServerPid <= 0)
            {
                error = "server_pid is missing or invalid.";
                return false;
            }

            if (!TryParseUtc(ServerStartUtc, out _))
            {
                error = "server_start_utc is missing or malformed.";
                return false;
            }

            return true;
        }

        /// <summary>Formats a timestamp for storage in this record.</summary>
        public static string FormatUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Parses a stored timestamp as UTC.</summary>
        public static bool TryParseUtc(string value, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!DateTime.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTime parsed))
            {
                return false;
            }

            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
    }
}
