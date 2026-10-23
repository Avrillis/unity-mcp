using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

        /// <summary>The query was cancelled before it completed.</summary>
        Cancelled,

        /// <summary>
        /// The child ran, but its output could not be proven complete: a pipe was not read to the
        /// end, a read failed, or unexpected diagnostic output accompanied the result.
        /// </summary>
        IncompleteOutput,

        /// <summary>
        /// The in-box CIM provider could not be established as the trusted Windows component, so
        /// whatever it might have returned is not evidence.
        /// </summary>
        UntrustedProvider,
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
        public bool Cancelled { get; set; }
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;

        /// <summary>True when more output was produced than the permitted bound.</summary>
        public bool OutputTruncated { get; set; }

        /// <summary>
        /// True only when the child's exit was confirmed AND both output pipes were read to their
        /// end without any read failure. It defaults to false, so an outcome that does not assert
        /// completion can never authorize anything.
        /// </summary>
        public bool OutputComplete { get; set; }
    }

    /// <summary>One request to run the fixed system executable.</summary>
    public sealed class WindowsCommandRunRequest
    {
        public string ExecutablePath { get; set; }
        public string Arguments { get; set; }

        /// <summary>Total budget for the whole run: start, execution, exit and output drain.</summary>
        public int TimeoutMs { get; set; }

        /// <summary>Environment variables set (not merged) in the child; null inherits unchanged.</summary>
        public IReadOnlyDictionary<string, string> Environment { get; set; }
    }

    /// <summary>Seam that lets tests replace the child-process launch. Production uses a bounded runner.</summary>
    public interface IWindowsCommandRunner
    {
        /// <summary>
        /// Runs the request to completion, cancellation or its deadline. Blocking: never call it
        /// from the Editor main thread (use <see cref="ProcessCommandLineObserver"/>).
        /// </summary>
        WindowsCommandRunOutcome Run(WindowsCommandRunRequest request, CancellationToken cancellationToken);
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
    ///   * the CIM provider is NOT resolved through any module search path. The child's
    ///     <c>PSModulePath</c> is replaced with the system module directory, the script imports
    ///     <c>CimCmdlets</c> from its absolute system location, verifies the module identity (name,
    ///     GUID, base directory) and that the cmdlet's assembly lives under the Windows directory,
    ///     and invokes the verified <c>CmdletInfo</c> object itself, so a same-named function,
    ///     alias or module cannot take its place. Anything else prints <c>UNTRUSTED:*</c>;
    ///   * the only caller-supplied value is a positive integer PID, validated before use and
    ///     embedded as a number, so nothing can be injected into the query;
    ///   * the query is passed as <c>-EncodedCommand</c>, so quoting rules never apply to it;
    ///   * the result is a single line, and the command line itself is Base64 (UTF-8) encoded, so
    ///     the data of an arbitrary process can never imitate a status line, and Unicode survives
    ///     the console code page;
    ///   * the whole operation is bounded by one deadline and by an output cap;
    ///   * a result counts only after the COMPLETE operation succeeded: confirmed exit code 0,
    ///     both pipes read to the end without error, no timeout, no cancellation, no unexpected
    ///     stderr, and exactly one well-formed result line. Partial output never wins over a
    ///     later failure. A PID that exists is never, by itself, evidence of anything.
    ///
    /// This type is deliberately free of Unity dependencies so it can be exercised without an Editor.
    /// </summary>
    public static class WindowsProcessCommandLine
    {
        /// <summary>Upper bound for one whole query (start, run, exit and drain).</summary>
        public const int DefaultTimeoutMs = 8000;

        /// <summary>Upper bound for captured output, in characters.</summary>
        public const int MaxOutputChars = 262144;

        /// <summary>Extra time allowed after the process exited for its pipes to reach end-of-stream.</summary>
        internal const int DrainGraceMs = 750;

        private const string NotFoundMarker = "NOTFOUND";
        private const string NullMarker = "NULL";
        private const string OkPrefix = "OK:";
        private const string UntrustedPrefix = "UNTRUSTED:";

        private const string CimCmdletsGuid = "fb6cc51d-c096-4b38-b78d-0fed6277096a";

        /// <summary>Fixed path of the in-box Windows PowerShell 5.1 host, or null when it cannot be derived.</summary>
        public static string ResolvePowerShellPath()
        {
            string home = ResolvePowerShellHome();
            return home == null ? null : Path.Combine(home, "powershell.exe");
        }

        /// <summary>Directory of the in-box Windows PowerShell 5.1 host (its <c>$PSHOME</c>), or null.</summary>
        internal static string ResolvePowerShellHome()
        {
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (string.IsNullOrWhiteSpace(system))
                {
                    return null;
                }

                return Path.Combine(system, "WindowsPowerShell", "v1.0");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The query script for the real system host. The PID is the only interpolated value and
        /// is a validated positive integer. The script contains no double quotes.
        /// </summary>
        public static string BuildQueryScript(int pid)
            => BuildQueryScript(pid, ResolvePowerShellHome(), moduleBaseOverride: null);

        /// <summary>
        /// As <see cref="BuildQueryScript(int)"/> with the trusted host directory and (for tests that
        /// need to exercise the identity checks) the module directory spelled out. Both reach the
        /// script as Base64, so no path text is ever parsed as script.
        /// </summary>
        internal static string BuildQueryScript(int pid, string hostRoot, string moduleBaseOverride)
        {
            if (pid <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pid), "The process id must be a positive integer.");
            }

            string number = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string host = Base64Utf8(hostRoot ?? string.Empty);
            string moduleBase = moduleBaseOverride == null ? "''" : "'" + Base64Utf8(moduleBaseOverride) + "'";

            return "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';"
                   + "$root=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + host + "')).TrimEnd('\\');"
                   + "$ovr=" + moduleBase + ";"
                   + "if($ovr -ne ''){$ovr=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ovr)).TrimEnd('\\')};"
                   + "try{"
                   + "if($root -eq '' -or -not [string]::Equals($PSHOME.TrimEnd('\\'),$root,[StringComparison]::OrdinalIgnoreCase))"
                   + "{'" + UntrustedPrefix + "host';return};"
                   + "$base=if($ovr -ne ''){$ovr}else{[IO.Path]::Combine($root,'Modules','CimCmdlets')};"
                   + "$m=Microsoft.PowerShell.Core\\Import-Module -Name ([IO.Path]::Combine($base,'CimCmdlets.psd1')) -PassThru;"
                   + "if($m.Name -ne 'CimCmdlets' -or $m.Guid -ne [guid]'" + CimCmdletsGuid + "'"
                   + " -or -not [string]::Equals($m.ModuleBase,$base,[StringComparison]::OrdinalIgnoreCase))"
                   + "{'" + UntrustedPrefix + "module';return};"
                   + "$c=$m.ExportedCmdlets['Get-CimInstance'];"
                   + "$win=[Environment]::GetFolderPath('Windows').TrimEnd('\\')+'\\';"
                   + "if($null -eq $c -or $c.CommandType -ne 'Cmdlet'"
                   + " -or -not $c.ImplementingType.Assembly.Location.StartsWith($win,[StringComparison]::OrdinalIgnoreCase))"
                   + "{'" + UntrustedPrefix + "cmdlet';return}"
                   + "}catch{'" + UntrustedPrefix + "unavailable';return};"
                   + "$p=& $c -ClassName Win32_Process -Filter 'ProcessId=" + number + "';"
                   + "if($null -eq $p){'" + NotFoundMarker + "'}"
                   + "elseif([string]::IsNullOrEmpty($p.CommandLine)){'" + NullMarker + "'}"
                   + "else{$b=[Text.Encoding]::UTF8.GetBytes([string]$p.CommandLine);'" + OkPrefix + "'+$b.Length+':'+[Convert]::ToBase64String($b)}";
        }

        /// <summary>Argument string for the fixed PowerShell host: no profile, non-interactive, encoded script.</summary>
        public static string BuildArguments(int pid)
            => BuildArguments(BuildQueryScript(pid));

        internal static string BuildArguments(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded;
        }

        /// <summary>
        /// Environment for the child: the module search path is replaced, not extended, so no
        /// inherited, user-writable or machine-wide module location is ever consulted.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> BuildChildEnvironment(string hostRoot)
        {
            if (string.IsNullOrWhiteSpace(hostRoot))
            {
                return null;
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PSModulePath"] = Path.Combine(hostRoot, "Modules"),
            };
        }

        /// <summary>
        /// Interprets one run. Success requires the COMPLETE operation to have succeeded: a started
        /// child, no cancellation or timeout, confirmed exit code 0, both pipes read to the end
        /// without error, no truncation, no unexpected diagnostic output and exactly one non-empty
        /// output line that is a valid <c>OK:</c> result.
        /// </summary>
        public static WindowsCommandLineResult ParseOutcome(WindowsCommandRunOutcome outcome)
        {
            if (outcome != null && outcome.Cancelled)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.Cancelled, "the query was cancelled.");
            }

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

            if (!outcome.OutputComplete)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.IncompleteOutput,
                    "the query output was not read completely, so it cannot be trusted.");
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

            if (!IsBenignDiagnosticStream(outcome.StandardError))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.IncompleteOutput,
                    "the query wrote unexpected diagnostic output, so the result is not trusted.");
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

            if (line.StartsWith(UntrustedPrefix, StringComparison.Ordinal))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.UntrustedProvider,
                    "the trusted Windows CIM provider could not be established ("
                    + line.Substring(UntrustedPrefix.Length) + ").");
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

            // The result is "OK:<byte count>:<Base64>". The count lets the decoder prove the payload is
            // whole even if a cut happened to fall on a Base64 boundary, so a truncated line can never
            // be mistaken for a shorter, complete one.
            string frame = line.Substring(OkPrefix.Length);
            int separator = frame.IndexOf(':');
            if (separator <= 0
                || separator > 9
                || !IsDecimalDigits(frame, separator)
                || !int.TryParse(
                    frame.Substring(0, separator),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int expectedBytes)
                || expectedBytes <= 0)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the command line payload had no valid length.");
            }

            string payload = frame.Substring(separator + 1);
            if (!IsStrictBase64(payload))
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.MalformedOutput, "the command line payload was not valid Base64.");
            }

            string commandLine;
            try
            {
                byte[] bytes = Convert.FromBase64String(payload);
                if (bytes.Length != expectedBytes)
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.MalformedOutput,
                        "the command line payload did not match its declared length (truncated or altered).");
                }

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
        /// Queries one process and blocks until the whole operation finished, was cancelled or hit
        /// its deadline. Never throws.
        ///
        /// BLOCKING: a cold query takes hundreds of milliseconds and may take seconds. Editor
        /// callbacks must go through <see cref="ProcessCommandLineObserver"/> instead, which runs
        /// this off the calling thread. <paramref name="runner"/> and <paramref name="powerShellPath"/>
        /// exist only so tests can substitute them; production passes neither.
        /// </summary>
        internal static WindowsCommandLineResult Query(
            int pid,
            IWindowsCommandRunner runner = null,
            string powerShellPath = null,
            int timeoutMs = DefaultTimeoutMs,
            CancellationToken cancellationToken = default)
            => QueryWithScript(pid, runner, powerShellPath, timeoutMs, cancellationToken, hostRootOverride: null, moduleBaseOverride: null);

        internal static WindowsCommandLineResult QueryWithScript(
            int pid,
            IWindowsCommandRunner runner,
            string powerShellPath,
            int timeoutMs,
            CancellationToken cancellationToken,
            string hostRootOverride,
            string moduleBaseOverride)
        {
            try
            {
                if (pid <= 0)
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.InvalidPid, "the process id must be a positive integer.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.Cancelled, "the query was cancelled.");
                }

                if (runner == null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return WindowsCommandLineResult.Fail(
                        WindowsCommandLineStatus.UnsupportedPlatform, "Windows process inspection is not available on this platform.");
                }

                string hostRoot = hostRootOverride ?? ResolvePowerShellHome();
                string executable = string.IsNullOrWhiteSpace(powerShellPath)
                    ? ResolvePowerShellPath()
                    : powerShellPath;
                if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(hostRoot))
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

                var request = new WindowsCommandRunRequest
                {
                    ExecutablePath = executable,
                    Arguments = BuildArguments(BuildQueryScript(pid, hostRoot, moduleBaseOverride)),
                    TimeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs,
                    Environment = BuildChildEnvironment(hostRoot),
                };

                WindowsCommandRunOutcome outcome =
                    (runner ?? BoundedProcessRunner.Instance).Run(request, cancellationToken);
                return ParseOutcome(outcome);
            }
            catch (OperationCanceledException)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.Cancelled, "the query was cancelled.");
            }
            catch (Exception ex)
            {
                return WindowsCommandLineResult.Fail(
                    WindowsCommandLineStatus.RunFailed, "the query failed: " + ex.GetType().Name);
            }
        }

        private static string Base64Utf8(string text)
            => Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text ?? string.Empty));

        /// <summary>
        /// PowerShell writes CLIXML progress records ("preparing modules for first use") to a
        /// redirected stderr even on success. That envelope is benign; any other text, or CLIXML
        /// that carries an error stream, is an anomaly and invalidates the result.
        /// </summary>
        internal static bool IsBenignDiagnosticStream(string standardError)
        {
            string text = (standardError ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return true;
            }

            if (!text.StartsWith("#< CLIXML", StringComparison.Ordinal))
            {
                return false;
            }

            return text.IndexOf("S=\"Error\"", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsDecimalDigits(string value, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return length > 0;
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

        /// <summary>
        /// Starts the fixed executable with redirected, bounded I/O under ONE deadline, and reports
        /// the outcome as complete only after the exit was confirmed and both pipes were read to
        /// their end without error. On timeout, cancellation or any failure the child is killed and
        /// its exit awaited, so no query process outlives the call.
        /// </summary>
        internal sealed class BoundedProcessRunner : IWindowsCommandRunner
        {
            public static readonly BoundedProcessRunner Instance = new BoundedProcessRunner();

            private const int PollSliceMs = 20;
            private const int KillConfirmMs = 2000;

            private readonly Action<Process> _onStarted;
            private readonly Func<TextReader, TextReader> _wrapStandardOutput;
            private readonly Func<TextReader, TextReader> _wrapStandardError;

            public BoundedProcessRunner()
                : this(null, null, null)
            {
            }

            /// <summary>Test seams: observe the started child, or wrap its pipe readers to inject faults.</summary>
            internal BoundedProcessRunner(
                Action<Process> onStarted,
                Func<TextReader, TextReader> wrapStandardOutput,
                Func<TextReader, TextReader> wrapStandardError)
            {
                _onStarted = onStarted;
                _wrapStandardOutput = wrapStandardOutput;
                _wrapStandardError = wrapStandardError;
            }

            public WindowsCommandRunOutcome Run(WindowsCommandRunRequest request, CancellationToken cancellationToken)
            {
                var outcome = new WindowsCommandRunOutcome();
                Process process = null;
                BoundedReader stdout = null;
                BoundedReader stderr = null;
                Task stdoutTask = null;
                Task stderrTask = null;
                var clock = Stopwatch.StartNew();

                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        outcome.Cancelled = true;
                        return outcome;
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = request.ExecutablePath,
                        Arguments = request.Arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                    };

                    if (request.Environment != null)
                    {
                        foreach (KeyValuePair<string, string> pair in request.Environment)
                        {
                            startInfo.EnvironmentVariables[pair.Key] = pair.Value;
                        }
                    }

                    process = Process.Start(startInfo);
                    if (process == null)
                    {
                        return outcome;
                    }

                    outcome.Started = true;
                    _onStarted?.Invoke(process);

                    try { process.StandardInput.Close(); } catch { /* the child needs no input */ }

                    TextReader outReader = process.StandardOutput;
                    if (_wrapStandardOutput != null)
                    {
                        outReader = _wrapStandardOutput(outReader);
                    }

                    TextReader errReader = process.StandardError;
                    if (_wrapStandardError != null)
                    {
                        errReader = _wrapStandardError(errReader);
                    }

                    stdout = new BoundedReader(outReader);
                    stderr = new BoundedReader(errReader);
                    stdoutTask = Task.Run(stdout.Pump);
                    stderrTask = Task.Run(stderr.Pump);

                    int budgetMs = request.TimeoutMs > 0 ? request.TimeoutMs : DefaultTimeoutMs;
                    bool exited = false;
                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            outcome.Cancelled = true;
                            break;
                        }

                        long remaining = budgetMs - clock.ElapsedMilliseconds;
                        if (remaining <= 0)
                        {
                            outcome.TimedOut = true;
                            break;
                        }

                        if (process.WaitForExit((int)Math.Min(PollSliceMs, remaining)))
                        {
                            exited = true;
                            break;
                        }
                    }

                    if (!exited)
                    {
                        KillAndConfirm(process);
                        JoinReaders(stdoutTask, stderrTask, DrainGraceMs);

                        // Whatever was printed before the stop is kept for diagnosis only. The outcome stays
                        // timed out / cancelled and incomplete, so it can never be read as a result.
                        outcome.StandardOutput = stdout.Text;
                        outcome.StandardError = stderr.Text;
                        outcome.OutputTruncated = stdout.Truncated || stderr.Truncated;
                        return outcome;
                    }

                    // The exit is confirmed. The pipes must now reach end-of-stream within the grace
                    // period; a result is only complete once they did, and without a read error.
                    JoinReaders(stdoutTask, stderrTask, DrainGraceMs);

                    outcome.ExitCode = process.ExitCode;
                    outcome.StandardOutput = stdout.Text;
                    outcome.StandardError = stderr.Text;
                    outcome.OutputTruncated = stdout.Truncated || stderr.Truncated;
                    outcome.OutputComplete = stdout.CompletedCleanly && stderr.CompletedCleanly;
                    return outcome;
                }
                catch (Exception)
                {
                    // Any launch or I/O failure is a failed query, never a partial success.
                    outcome.ExitCode = -1;
                    outcome.OutputComplete = false;
                    return outcome;
                }
                finally
                {
                    if (process != null)
                    {
                        // Leave no query child behind on any path (exception, early return, abandoned run).
                        KillAndConfirm(process);
                        try { process.Dispose(); } catch { /* best effort */ }
                    }

                    // Disposing the process closes the pipes, which ends any reader still blocked.
                    JoinReaders(stdoutTask, stderrTask, 500);
                }
            }

            private static void KillAndConfirm(Process process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }

                try { process.Kill(); } catch { /* already exiting */ }
                try { process.WaitForExit(KillConfirmMs); } catch { /* best effort */ }
            }

            private static void JoinReaders(Task stdoutTask, Task stderrTask, int graceMs)
            {
                if (stdoutTask == null || stderrTask == null)
                {
                    return;
                }

                try
                {
                    Task.WaitAll(new[] { stdoutTask, stderrTask }, graceMs);
                }
                catch
                {
                    // A reader that failed is reported through its own state.
                }
            }
        }

        /// <summary>
        /// Drains one pipe, keeping at most <see cref="MaxOutputChars"/> characters. It records whether
        /// the stream reached its end and whether a read failed, so a result is never accepted on
        /// the strength of output that may have been cut short.
        /// </summary>
        internal sealed class BoundedReader
        {
            private readonly TextReader _reader;
            private readonly StringBuilder _buffer = new StringBuilder();
            private int _completed;
            private int _failed;

            public BoundedReader(TextReader reader)
            {
                _reader = reader;
            }

            public bool Truncated { get; private set; }

            /// <summary>True only when end-of-stream was reached and no read failed.</summary>
            public bool CompletedCleanly => Volatile.Read(ref _completed) == 1 && Volatile.Read(ref _failed) == 0;

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

                    Volatile.Write(ref _completed, 1);
                }
                catch (Exception)
                {
                    // Not end-of-stream: the remainder is unknown, so the stream is not complete.
                    Volatile.Write(ref _failed, 1);
                }
            }
        }
    }
}
