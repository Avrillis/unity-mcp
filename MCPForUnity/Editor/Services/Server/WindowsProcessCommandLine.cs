using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>Why a Windows command-line query did or did not produce a command line.</summary>
    public enum WindowsCommandLineStatus
    {
        /// <summary>The exact command line of the requested process was read.</summary>
        Ok = 0,

        /// <summary>The PID is not a positive integer, so no query was made.</summary>
        InvalidPid,

        /// <summary>Not a Windows host, so no query was made.</summary>
        UnsupportedPlatform,

        /// <summary>The fixed system PowerShell executable does not exist.</summary>
        ExecutableMissing,

        /// <summary>The query process could not be started, exited non-zero or threw.</summary>
        RunFailed,

        /// <summary>The query exceeded its time limit and was stopped.</summary>
        TimedOut,

        /// <summary>No process with this PID exists (it never did, or it has exited).</summary>
        ProcessNotFound,

        /// <summary>The process exists but the OS returned no command line (for example, access denied).</summary>
        CommandLineUnavailable,

        /// <summary>The output was not exactly one well-formed result line.</summary>
        MalformedOutput,
    }

    /// <summary>Result of one Windows command-line query. Only <see cref="Ok"/> carries a command line.</summary>
    public sealed class WindowsCommandLineResult
    {
        public WindowsCommandLineStatus Status { get; internal set; }

        /// <summary>The exact (not normalized) command line; null unless <see cref="Succeeded"/>.</summary>
        public string CommandLine { get; internal set; }

        /// <summary>Short, credential-free explanation for logs and tests.</summary>
        public string Detail { get; internal set; }

        public bool Succeeded => Status == WindowsCommandLineStatus.Ok && !string.IsNullOrEmpty(CommandLine);

        internal static WindowsCommandLineResult Fail(WindowsCommandLineStatus status, string detail)
            => new WindowsCommandLineResult { Status = status, Detail = detail };
    }

    /// <summary>What one bounded child-process run produced.</summary>
    public sealed class WindowsCommandRunOutcome
    {
        public bool Started { get; set; }
        public bool TimedOut { get; set; }
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;

        /// <summary>True when more output was produced than the permitted bound.</summary>
        public bool OutputTruncated { get; set; }
    }

    /// <summary>Seam that lets tests replace the child-process launch. Production uses a bounded runner.</summary>
    public interface IWindowsCommandRunner
    {
        WindowsCommandRunOutcome Run(string executablePath, string arguments, int timeoutMs);
    }

    /// <summary>
    /// Reads the exact command line of one Windows process through CIM (<c>Win32_Process</c>),
    /// without <c>wmic.exe</c>, which Windows 11 no longer ships.
    ///
    /// Why this exists: the managed MCP ownership proof needs the server's command line so it can
    /// confirm the per-launch nonce. If that line cannot be read the proof fails closed and the
    /// Editor refuses to register with its own dedicated server.
    ///
    /// Security shape:
    ///   * the executable is the fixed system Windows PowerShell path, never resolved through PATH,
    ///     the current directory or a profile;
    ///   * the only caller-supplied value is a positive integer PID, validated before use and
    ///     embedded as a number, so nothing can be injected into the query;
    ///   * the query is passed as <c>-EncodedCommand</c>, so quoting rules never apply to it;
    ///   * the result is a single line, and the command line itself is Base64 (UTF-8) encoded, so
    ///     the data of an arbitrary process can never imitate a status line, and Unicode survives
    ///     the console code page;
    ///   * the run is time-bounded and output-bounded;
    ///   * anything other than exactly one well-formed result is a failure. A PID that exists is
    ///     never, by itself, evidence of anything.
    ///
    /// This type is deliberately free of Unity dependencies so it can be exercised without an Editor.
    /// </summary>
    public static class WindowsProcessCommandLine
    {
        /// <summary>Upper bound for one query. A cold Windows PowerShell start takes well under a second.</summary>
        public const int DefaultTimeoutMs = 8000;

        /// <summary>Upper bound for captured output, in characters.</summary>
        public const int MaxOutputChars = 262144;

        private const string NotFoundMarker = "NOTFOUND";
        private const string NullMarker = "NULL";
        private const string OkPrefix = "OK:";

        /// <summary>Fixed path of the in-box Windows PowerShell 5.1 host, or null when it cannot be derived.</summary>
        public static string ResolvePowerShellPath()
        {
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (string.IsNullOrWhiteSpace(system))
                {
                    return null;
                }

                return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The query script. The PID is the only interpolated value and is a validated positive
        /// integer. The script contains no double quotes and reads nothing else from the caller.
        /// </summary>
        public static string BuildQueryScript(int pid)
        {
            if (pid <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pid), "The process id must be a positive integer.");
            }

            string number = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "$ErrorActionPreference='Stop';"
                   + "$p=Get-CimInstance -ClassName Win32_Process -Filter 'ProcessId=" + number + "';"
                   + "if($null -eq $p){'" + NotFoundMarker + "'}"
                   + "elseif([string]::IsNullOrEmpty($p.CommandLine)){'" + NullMarker + "'}"
                   + "else{'" + OkPrefix + "'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$p.CommandLine))}";
        }

        /// <summary>Argument string for the fixed PowerShell host: no profile, non-interactive, encoded script.</summary>
        public static string BuildArguments(int pid)
        {
            string script = BuildQueryScript(pid);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded;
        }

        /// <summary>
        /// Interprets one run. Success requires a clean exit, no timeout, no truncation and
        /// exactly one non-empty output line that is a valid <c>OK:</c> result.
        /// </summary>
        public static WindowsCommandLineResult ParseOutcome(WindowsCommandRunOutcome outcome)
        {
            if (outcome == null || !outcome.Started)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.RunFailed, "the query process could not be started.");
            }

            if (outcome.TimedOut)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.TimedOut, "the query exceeded its time limit.");
            }

            if (outcome.ExitCode != 0)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.RunFailed, $"the query exited with code {outcome.ExitCode}.");
            }

            if (outcome.OutputTruncated)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the query output exceeded the permitted size.");
            }

            string line = null;
            foreach (string candidate in (outcome.StandardOutput ?? string.Empty)
                         .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = candidate.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (line != null)
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.MalformedOutput, "the query produced more than one result line.");
                }

                line = trimmed;
            }

            if (line == null)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the query produced no result.");
            }

            if (string.Equals(line, NotFoundMarker, StringComparison.Ordinal))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.ProcessNotFound, "no process with this id exists.");
            }

            if (string.Equals(line, NullMarker, StringComparison.Ordinal))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.CommandLineUnavailable,
                    "the process exists but its command line is not available.");
            }

            if (!line.StartsWith(OkPrefix, StringComparison.Ordinal))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the query result was not recognised.");
            }

            string payload = line.Substring(OkPrefix.Length);
            if (!IsStrictBase64(payload))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the command line payload was not valid Base64.");
            }

            string commandLine;
            try
            {
                byte[] bytes = Convert.FromBase64String(payload);
                commandLine = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (Exception)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the command line payload could not be decoded.");
            }

            if (string.IsNullOrEmpty(commandLine) || commandLine.IndexOf('\0') >= 0)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the decoded command line was empty or malformed.");
            }

            return new WindowsCommandLineResult
            {
                Status = WindowsCommandLineStatus.Ok,
                CommandLine = commandLine,
                Detail = "the command line was read.",
            };
        }

        /// <summary>
        /// Queries one process. <paramref name="runner"/> and <paramref name="powerShellPath"/> exist only
        /// so tests can substitute them; production passes neither. Never throws.
        /// </summary>
        public static WindowsCommandLineResult Query(
            int pid,
            IWindowsCommandRunner runner = null,
            string powerShellPath = null,
            int timeoutMs = DefaultTimeoutMs)
        {
            try
            {
                if (pid <= 0)
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.InvalidPid, "the process id must be a positive integer.");
                }

                if (runner == null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.UnsupportedPlatform, "Windows process inspection is not available on this platform.");
                }

                string executable = string.IsNullOrWhiteSpace(powerShellPath) ? ResolvePowerShellPath() : powerShellPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.ExecutableMissing, "the system PowerShell path could not be derived.");
                }

                // A test runner stands in for the process launch, so only the real launch needs the file.
                if (runner == null && !File.Exists(executable))
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.ExecutableMissing, "the system PowerShell executable was not found.");
                }

                int bound = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
                WindowsCommandRunOutcome outcome =
                    (runner ?? BoundedProcessRunner.Instance).Run(executable, BuildArguments(pid), bound);
                return ParseOutcome(outcome);
            }
            catch (Exception ex)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.RunFailed, "the query failed: " + ex.GetType().Name);
            }
        }

        /// <summary>Production entry point: true only when the exact command line was read.</summary>
        public static bool TryGetCommandLine(int pid, out string commandLine)
            => TryGetCommandLine(pid, null, out commandLine);

        /// <summary>As <see cref="TryGetCommandLine(int, out string)"/> with an injectable runner (tests only).</summary>
        public static bool TryGetCommandLine(int pid, IWindowsCommandRunner runner, out string commandLine)
        {
            WindowsCommandLineResult result = Query(pid, runner);
            commandLine = result.Succeeded ? result.CommandLine : null;
            return result.Succeeded;
        }

        private static bool IsStrictBase64(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length % 4 != 0)
            {
                return false;
            }

            int padding = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '=')
                {
                    // Padding is only legal as the last one or two characters.
                    if (i < value.Length - 2)
                    {
                        return false;
                    }

                    padding++;
                    continue;
                }

                if (padding > 0)
                {
                    return false;
                }

                bool valid = (c >= 'A' && c <= 'Z')
                             || (c >= 'a' && c <= 'z')
                             || (c >= '0' && c <= '9')
                             || c == '+'
                             || c == '/';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Starts the fixed executable with redirected, bounded I/O and kills it on timeout.</summary>
        private sealed class BoundedProcessRunner : IWindowsCommandRunner
        {
            public static readonly BoundedProcessRunner Instance = new BoundedProcessRunner();

            public WindowsCommandRunOutcome Run(string executablePath, string arguments, int timeoutMs)
            {
                var outcome = new WindowsCommandRunOutcome();
                Process process = null;
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                    };

                    process = Process.Start(startInfo);
                    if (process == null)
                    {
                        return outcome;
                    }

                    outcome.Started = true;

                    try { process.StandardInput.Close(); } catch { /* the child needs no input */ }

                    var stdout = new BoundedReader(process.StandardOutput);
                    var stderr = new BoundedReader(process.StandardError);
                    Task stdoutTask = Task.Run(stdout.Pump);
                    Task stderrTask = Task.Run(stderr.Pump);

                    if (!process.WaitForExit(timeoutMs))
                    {
                        outcome.TimedOut = true;
                        try { process.Kill(); } catch { /* already exiting */ }
                        return outcome;
                    }

                    // Let the readers drain what was already written, but never wait unboundedly.
                    Task.WaitAll(new[] { stdoutTask, stderrTask }, 2000);

                    outcome.ExitCode = process.ExitCode;
                    outcome.StandardOutput = stdout.Text;
                    outcome.StandardError = stderr.Text;
                    outcome.OutputTruncated = stdout.Truncated || stderr.Truncated;
                    return outcome;
                }
                catch (Exception)
                {
                    // Any launch or I/O failure is a failed query, never a partial success.
                    outcome.ExitCode = -1;
                    return outcome;
                }
                finally
                {
                    try { process?.Dispose(); } catch { /* best effort */ }
                }
            }
        }

        private sealed class BoundedReader
        {
            private readonly TextReader _reader;
            private readonly StringBuilder _buffer = new StringBuilder();

            public BoundedReader(TextReader reader)
            {
                _reader = reader;
            }

            public bool Truncated { get; private set; }

            public string Text
            {
                get
                {
                    lock (_buffer)
                    {
                        return _buffer.ToString();
                    }
                }
            }

            public void Pump()
            {
                var chunk = new char[4096];
                try
                {
                    int read;
                    while ((read = _reader.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        lock (_buffer)
                        {
                            if (_buffer.Length + read > MaxOutputChars)
                            {
                                // Keep draining so the child never blocks on a full pipe, but store no more.
                                Truncated = true;
                                continue;
                            }

                            _buffer.Append(chunk, 0, read);
                        }
                    }
                }
                catch (Exception)
                {
                    // The stream closed under us (child killed): whatever was read is all there is.
                }
            }
        }
    }
}
