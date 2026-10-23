using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>How the raw command line of a live process was produced, which fixes how it is split.</summary>
    public enum McpCommandLineFormat
    {
        /// <summary>
        /// The exact Windows command line. It is split with the C runtime rules (quotes, backslashes
        /// before quotes), which is how the server process itself receives its arguments.
        /// </summary>
        WindowsQuoted = 0,

        /// <summary>
        /// <c>ps -o args=</c> output: the arguments joined by spaces with all quoting already lost, so
        /// it can only be split on whitespace. A value that itself contains spaces is therefore
        /// matched as a run of words, never as a single guaranteed argument.
        /// </summary>
        PosixFlat,
    }

    /// <summary>Which part of the launch identity a verification failed on.</summary>
    public enum McpLaunchIdentityArea
    {
        None = 0,

        /// <summary>The command line was empty, unparseable, or the expected value could not be built.</summary>
        CommandLine,

        /// <summary><c>--transport</c> is not exactly one <c>http</c>.</summary>
        Transport,

        /// <summary><c>--unity-instance-token</c> is not exactly one argument carrying the launch nonce.</summary>
        Token,

        /// <summary><c>--http-url</c> is not exactly one argument naming the expected endpoint.</summary>
        Endpoint,

        /// <summary><c>--unity-project-root</c> is not exactly one argument naming the expected project.</summary>
        ProjectRoot,

        /// <summary><c>--pidfile</c> is not exactly one argument naming the expected PID file.</summary>
        PidFile,
    }

    /// <summary>The identity a live managed server's command line must carry.</summary>
    public sealed class McpExpectedLaunch
    {
        public string InstanceToken;
        public string ProjectRoot;
        public string Endpoint;
        public string PidFilePath;
    }

    /// <summary>Outcome of checking a live command line against the expected launch identity.</summary>
    public sealed class McpLaunchIdentityResult
    {
        private McpLaunchIdentityResult(bool verified, McpLaunchIdentityArea area, string detail)
        {
            Verified = verified;
            Area = area;
            Detail = detail;
        }

        public bool Verified { get; }

        public McpLaunchIdentityArea Area { get; }

        /// <summary>Names flags and problems only; it never repeats the command line itself.</summary>
        public string Detail { get; }

        internal static McpLaunchIdentityResult Success()
            => new McpLaunchIdentityResult(true, McpLaunchIdentityArea.None, "the command line carries the expected launch identity.");

        internal static McpLaunchIdentityResult Fail(McpLaunchIdentityArea area, string detail)
            => new McpLaunchIdentityResult(false, area, detail);
    }

    /// <summary>
    /// Verifies that a live process was launched with exactly the arguments this editor's managed
    /// launch produced, by parsing its raw command line into arguments.
    ///
    /// Why parsing rather than searching: a managed launch ends with
    /// <c>--pidfile PATH --unity-instance-token NONCE</c> and carries <c>--http-url</c> and
    /// <c>--unity-project-root</c>. Finding the nonce as a substring would also accept a process that
    /// merely mentions it in another argument, a path or a comment, or that carries it beside a
    /// different project or endpoint. Here every identity flag must occur exactly once, in its plain
    /// separated form, with a value equal to the expected one:
    ///
    ///   * <c>--transport</c> is <c>http</c>;
    ///   * <c>--http-url</c> equals the record's endpoint (case-insensitive, trailing slash ignored);
    ///   * <c>--unity-project-root</c> and <c>--pidfile</c> are absolute paths equal to the record's;
    ///   * <c>--unity-instance-token</c> equals the nonce, exactly (case-sensitive, no prefix/suffix).
    ///
    /// A flag spelled as <c>--flag=value</c> or abbreviated (the server's argument parser accepts
    /// unambiguous prefixes) is not the form a managed launch emits; it counts as an occurrence, so it
    /// can never be the one accepted occurrence and can never hide a duplicate.
    ///
    /// Free of Unity dependencies. This proves what the process was started with; it is one part of
    /// the ownership tuple and replaces none of the other proofs (lifetime, PID file, listener).
    /// </summary>
    public static class McpLaunchArguments
    {
        public const string TransportFlag = "--transport";
        public const string HttpUrlFlag = "--http-url";
        public const string ProjectRootFlag = "--unity-project-root";
        public const string PidFileFlag = "--pidfile";
        public const string TokenFlag = "--unity-instance-token";

        /// <summary>Splits a raw command line into arguments according to <paramref name="format"/>.</summary>
        public static IReadOnlyList<string> Tokenize(string commandLine, McpCommandLineFormat format)
        {
            if (string.IsNullOrEmpty(commandLine))
            {
                return new List<string>();
            }

            return format == McpCommandLineFormat.PosixFlat
                ? TokenizeFlat(commandLine)
                : TokenizeWindows(commandLine);
        }

        public static McpLaunchIdentityResult Verify(
            string commandLine,
            McpCommandLineFormat format,
            McpExpectedLaunch expected)
        {
            if (expected == null)
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.CommandLine, "no expected launch identity was supplied.");
            }

            if (!McpManagedServerArguments.IsSafeToken(expected.InstanceToken))
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.Token, "the expected launch nonce is missing or not a safe token.");
            }

            if (string.IsNullOrWhiteSpace(commandLine) || commandLine.IndexOf('\0') >= 0)
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.CommandLine, "the server command line is empty or malformed.");
            }

            IReadOnlyList<string> arguments = Tokenize(commandLine, format);
            if (arguments.Count == 0)
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.CommandLine, "the server command line has no arguments.");
            }

            // Transport: a managed server is always the HTTP server.
            McpLaunchIdentityResult found = FindOnce(arguments, TransportFlag, McpLaunchIdentityArea.Transport, out int transportIndex);
            if (!found.Verified)
            {
                return found;
            }

            if (!string.Equals(ValueAt(arguments, transportIndex), "http", StringComparison.Ordinal))
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.Transport, "--transport is not http.");
            }

            // Endpoint.
            found = FindOnce(arguments, HttpUrlFlag, McpLaunchIdentityArea.Endpoint, out int urlIndex);
            if (!found.Verified)
            {
                return found;
            }

            if (!string.Equals(
                    NormalizeEndpoint(ValueAt(arguments, urlIndex)),
                    NormalizeEndpoint(expected.Endpoint),
                    StringComparison.OrdinalIgnoreCase)
                || NormalizeEndpoint(expected.Endpoint).Length == 0)
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.Endpoint, "--http-url does not name the expected endpoint.");
            }

            // Project root.
            found = FindOnce(arguments, ProjectRootFlag, McpLaunchIdentityArea.ProjectRoot, out int rootIndex);
            if (!found.Verified)
            {
                return found;
            }

            if (!PathValueMatches(arguments, rootIndex, format, expected.ProjectRoot))
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.ProjectRoot, "--unity-project-root does not name the expected project.");
            }

            // PID file.
            found = FindOnce(arguments, PidFileFlag, McpLaunchIdentityArea.PidFile, out int pidFileIndex);
            if (!found.Verified)
            {
                return found;
            }

            if (!PathValueMatches(arguments, pidFileIndex, format, expected.PidFilePath))
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.PidFile, "--pidfile does not name the expected PID file.");
            }

            // Launch nonce: the whole argument, exactly.
            found = FindOnce(arguments, TokenFlag, McpLaunchIdentityArea.Token, out int tokenIndex);
            if (!found.Verified)
            {
                return found;
            }

            if (!string.Equals(ValueAt(arguments, tokenIndex), expected.InstanceToken, StringComparison.Ordinal))
            {
                return McpLaunchIdentityResult.Fail(
                    McpLaunchIdentityArea.Token, "--unity-instance-token is not the expected launch nonce.");
            }

            return McpLaunchIdentityResult.Success();
        }

        // ---- identity flags --------------------------------------------------------------

        /// <summary>
        /// Requires the flag to occur exactly once and in the plain separated form. Any other
        /// occurrence (a repeat, <c>--flag=value</c>, an abbreviation) fails the check.
        /// </summary>
        private static McpLaunchIdentityResult FindOnce(
            IReadOnlyList<string> arguments,
            string flag,
            McpLaunchIdentityArea area,
            out int index)
        {
            index = -1;
            int occurrences = 0;
            bool plainForm = false;

            // argv[0] is the program, never a flag.
            for (int i = 1; i < arguments.Count; i++)
            {
                if (!IsOccurrence(arguments[i], flag))
                {
                    continue;
                }

                occurrences++;
                if (string.Equals(arguments[i], flag, StringComparison.Ordinal))
                {
                    plainForm = true;
                    index = i;
                }
            }

            if (occurrences == 0)
            {
                return McpLaunchIdentityResult.Fail(area, flag + " is missing.");
            }

            if (occurrences > 1)
            {
                return McpLaunchIdentityResult.Fail(area, flag + " occurs more than once.");
            }

            if (!plainForm)
            {
                return McpLaunchIdentityResult.Fail(area, flag + " is not in its plain separated form.");
            }

            if (index + 1 >= arguments.Count)
            {
                return McpLaunchIdentityResult.Fail(area, flag + " has no value.");
            }

            return McpLaunchIdentityResult.Success();
        }

        private static bool IsOccurrence(string argument, string flag)
        {
            if (argument == null || !argument.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            int equals = argument.IndexOf('=');
            string name = equals >= 0 ? argument.Substring(0, equals) : argument;

            // Exact, "--flag=value", or an abbreviation of the flag (the server accepts those).
            return name.Length > 2 && flag.StartsWith(name, StringComparison.Ordinal);
        }

        private static string ValueAt(IReadOnlyList<string> arguments, int flagIndex)
            => arguments[flagIndex + 1];

        private static string NormalizeEndpoint(string endpoint)
            => string.IsNullOrWhiteSpace(endpoint) ? string.Empty : endpoint.Trim().TrimEnd('/');

        // ---- paths -----------------------------------------------------------------------

        private static bool PathValueMatches(
            IReadOnlyList<string> arguments,
            int flagIndex,
            McpCommandLineFormat format,
            string expectedPath)
        {
            if (string.IsNullOrWhiteSpace(expectedPath))
            {
                return false;
            }

            string value;
            if (format == McpCommandLineFormat.PosixFlat)
            {
                // Quoting is gone: take the words up to the next flag as the value.
                var words = new List<string>();
                for (int i = flagIndex + 1; i < arguments.Count; i++)
                {
                    if (arguments[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        break;
                    }

                    words.Add(arguments[i]);
                }

                value = string.Join(" ", words);
            }
            else
            {
                value = arguments[flagIndex + 1];
            }

            // A relative value would be resolved against the editor's own working directory.
            if (string.IsNullOrWhiteSpace(value) || !IsAbsolute(value))
            {
                return false;
            }

            return McpRunStatePaths.PathsEqual(value, expectedPath);
        }

        private static bool IsAbsolute(string path)
        {
            try
            {
                if (Path.IsPathRooted(path))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Some runtimes reject invalid path characters outright; such a value is not a usable path.
                return false;
            }

            // A Windows path is absolute for the process that received it whatever this host is.
            return (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                   || path.StartsWith("\\\\", StringComparison.Ordinal);
        }

        // ---- tokenizers ------------------------------------------------------------------

        private static List<string> TokenizeFlat(string commandLine)
        {
            var arguments = new List<string>();
            foreach (string word in commandLine.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                arguments.Add(word);
            }

            return arguments;
        }

        /// <summary>
        /// The C runtime argument rules (what CommandLineToArgvW and the server's runtime apply): the
        /// program name is read up to its closing quote without escapes; elsewhere a quote toggles
        /// quoting, <c>2n</c> backslashes before a quote yield <c>n</c> backslashes and toggle,
        /// <c>2n+1</c> yield <c>n</c> backslashes and a literal quote, and a doubled quote inside
        /// quotes is one literal quote.
        /// </summary>
        private static List<string> TokenizeWindows(string commandLine)
        {
            var arguments = new List<string>();
            int i = 0;
            int length = commandLine.Length;

            while (i < length && IsBlank(commandLine[i]))
            {
                i++;
            }

            if (i >= length)
            {
                return arguments;
            }

            var program = new StringBuilder();
            if (commandLine[i] == '"')
            {
                i++;
                while (i < length && commandLine[i] != '"')
                {
                    program.Append(commandLine[i++]);
                }

                if (i < length)
                {
                    i++;
                }
            }
            else
            {
                while (i < length && !IsBlank(commandLine[i]))
                {
                    program.Append(commandLine[i++]);
                }
            }

            arguments.Add(program.ToString());

            while (true)
            {
                while (i < length && IsBlank(commandLine[i]))
                {
                    i++;
                }

                if (i >= length)
                {
                    break;
                }

                var current = new StringBuilder();
                bool inQuotes = false;
                while (i < length)
                {
                    char c = commandLine[i];
                    if (c == '\\')
                    {
                        int slashes = 0;
                        while (i < length && commandLine[i] == '\\')
                        {
                            slashes++;
                            i++;
                        }

                        if (i < length && commandLine[i] == '"')
                        {
                            current.Append('\\', slashes / 2);
                            if (slashes % 2 == 1)
                            {
                                current.Append('"');
                                i++;
                            }
                        }
                        else
                        {
                            current.Append('\\', slashes);
                        }
                    }
                    else if (c == '"')
                    {
                        if (inQuotes && i + 1 < length && commandLine[i + 1] == '"')
                        {
                            current.Append('"');
                            i += 2;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                            i++;
                        }
                    }
                    else if (!inQuotes && IsBlank(c))
                    {
                        break;
                    }
                    else
                    {
                        current.Append(c);
                        i++;
                    }
                }

                arguments.Add(current.ToString());
            }

            return arguments;
        }

        private static bool IsBlank(char c) => c == ' ' || c == '\t';
    }
}
