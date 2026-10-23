using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Route;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// Shared builders for the MCP-WIN-01 tests.
    /// </summary>
    internal static class Win01
    {
        // Synthetic values shaped like a managed worker launch (spaces, hyphens, a per-user uv cache),
        // deliberately containing no real user, machine or project names.
        public const string Worker04Root = @"C:\Users\Example User\Sample Game-workers\WORKER-04-SAMPLE";
        public const string Worker03Root = @"C:\Users\Example User\Sample Game-workers\WORKER-03-OTHER";
        public const string Endpoint = "http://127.0.0.1:8181";
        public const int Port = 8181;
        public const string Token = "29bc72d164934bf8963cb94c9157e016";
        public const string OtherToken = "f690aa11bb22cc33dd44ee55ff667788";
        public const int EditorPid = 120468;
        public const int ServerPid = 106256;

        public static readonly DateTime EditorStart = new DateTime(2026, 10, 23, 13, 34, 43, DateTimeKind.Utc).AddTicks(4293872);
        public static readonly DateTime RecordWritten = new DateTime(2026, 10, 23, 13, 35, 1, DateTimeKind.Utc);
        public static readonly DateTime ServerStart = new DateTime(2026, 10, 23, 13, 35, 2, DateTimeKind.Utc).AddTicks(7661180);

        public static string ServerCommandLine(string root, string endpoint, string token)
            => "\"C:\\Users\\Example User\\AppData\\Roaming\\uv\\python\\cpython-3.12-windows-x86_64-none\\python.exe\" "
               + "\"C:\\Users\\Example User\\.uv-cache\\archive-v0\\EXAMPLEARCHIVE\\Scripts\\mcp-for-unity.exe\" "
               + $"--transport http --http-url {endpoint} --unity-project-root \"{root}\" "
               + "--project-scoped-tools "
               + $"--pidfile \"{root}\\Library\\MCPForUnity\\RunState\\mcp_http_8181.pid\" "
               + $"--unity-instance-token {token}";

        /// <summary>What the PowerShell query prints for a readable command line.</summary>
        public static WindowsCommandRunOutcome Ok(string commandLine)
            => new WindowsCommandRunOutcome
            {
                Started = true,
                ExitCode = 0,
                StandardOutput = OkLine(commandLine) + "\r\n",
                OutputComplete = true,
            };

        public static WindowsCommandRunOutcome Raw(string stdout, int exit = 0)
            => new WindowsCommandRunOutcome { Started = true, ExitCode = exit, StandardOutput = stdout, OutputComplete = true };

        public static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        /// <summary>The exact result line the query prints: OK:&lt;byte count&gt;:&lt;Base64 of the UTF-8 bytes&gt;.</summary>
        public static string OkLine(string commandLine)
            => OkLine(Encoding.UTF8.GetBytes(commandLine));

        public static string OkLine(byte[] bytes)
            => "OK:" + bytes.Length + ":" + Convert.ToBase64String(bytes);

        /// <summary>
        /// A detector whose observer uses a scripted PowerShell launch and a scripted process
        /// lifetime, so tests with invented PIDs can still bind a result to a lifetime.
        /// </summary>
        public static ProcessDetector DetectorFor(IWindowsCommandRunner runner, Func<int, DateTime?> lifetimeOf = null)
            => new ProcessDetector(ObserverFor(runner, lifetimeOf));

        public static ProcessCommandLineObserver ObserverFor(IWindowsCommandRunner runner, Func<int, DateTime?> lifetimeOf = null)
            => new ProcessCommandLineObserver(
                lifetimeOf ?? (_ => ServerStart),
                (pid, token) => WindowsProcessCommandLine.Query(
                    pid, runner, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));

        /// <summary>Waits (off any Editor thread) until the detector's query for this PID is final.</summary>
        public static void Prime(ProcessDetector detector, int pid)
            => detector.PrefetchProcessCommandLineAsync(pid, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Replaces the PowerShell launch with a scripted outcome and records how it was called.</summary>
    internal sealed class ScriptedRunner : IWindowsCommandRunner
    {
        private readonly Func<string, string, int, WindowsCommandRunOutcome> _handler;

        public ScriptedRunner(Func<string, string, int, WindowsCommandRunOutcome> handler)
        {
            _handler = handler;
        }

        public ScriptedRunner(WindowsCommandRunOutcome fixedOutcome) : this((_, __, ___) => fixedOutcome)
        {
        }

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string LastExecutable { get; private set; }
        public string LastArguments { get; private set; }
        public int LastTimeoutMs { get; private set; }
        public WindowsCommandRunRequest LastRequest { get; private set; }

        public WindowsCommandRunOutcome Run(WindowsCommandRunRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            LastExecutable = request.ExecutablePath;
            LastArguments = request.Arguments;
            LastTimeoutMs = request.TimeoutMs;
            return _handler(request.ExecutablePath, request.Arguments, request.TimeoutMs);
        }
    }

    // ================================================================================
    // 1. Query construction and the strict output parser (platform independent)
    // ================================================================================

    [TestFixture]
    public class McpWin01QueryTests
    {
        [Test]
        public void Script_EmbedsOnlyTheNumericPid_AndNoWmic()
        {
            string script = WindowsProcessCommandLine.BuildQueryScript(106256);

            Assert.That(script, Does.Contain("ProcessId=106256"));
            Assert.That(script, Does.Contain("Win32_Process"));
            Assert.That(script, Does.Not.Contain("\""), "no double quote can appear in the query");
            Assert.That(script, Does.Not.Contain("wmic").IgnoreCase);
            Assert.That(script, Does.Not.Contain("Invoke-Expression").IgnoreCase);
            Assert.That(script, Does.Not.Contain("Start-Process").IgnoreCase);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void Script_RejectsNonPositivePid(int pid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => WindowsProcessCommandLine.BuildQueryScript(pid));
        }

        [Test]
        public void Arguments_AreFixedFlags_AndTheEncodedScriptRoundTrips()
        {
            string args = WindowsProcessCommandLine.BuildArguments(4242);

            Assert.That(args, Does.StartWith("-NoLogo -NoProfile -NonInteractive -EncodedCommand "));
            Assert.That(args.All(c => c < 128), Is.True, "arguments are plain ASCII");

            string encoded = args.Substring("-NoLogo -NoProfile -NonInteractive -EncodedCommand ".Length);
            string script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            Assert.That(script, Is.EqualTo(WindowsProcessCommandLine.BuildQueryScript(4242)));
        }

        [TestCase(0)]
        [TestCase(-5)]
        [TestCase(int.MinValue)]
        public void Query_InvalidPid_NeverLaunchesAnything(int pid)
        {
            var runner = new ScriptedRunner(Win01.Ok("x"));

            WindowsCommandLineResult result = WindowsProcessCommandLine.Query(pid, runner);

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.InvalidPid));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(runner.Calls, Is.EqualTo(0));
        }

        [Test]
        public void Query_UsesTheFixedAbsoluteSystemPowerShell_NotAPathLookup()
        {
            var runner = new ScriptedRunner(Win01.Ok("c:\\x.exe --a"));

            WindowsProcessCommandLine.Query(77, runner);

            Assert.That(runner.Calls, Is.EqualTo(1));
            Assert.That(Path.IsPathRooted(runner.LastExecutable), Is.True, "no bare 'powershell.exe' PATH lookup");
            Assert.That(runner.LastExecutable, Is.EqualTo(WindowsProcessCommandLine.ResolvePowerShellPath()));
            Assert.That(
                runner.LastExecutable.Replace('/', '\\'),
                Does.EndWith(@"WindowsPowerShell\v1.0\powershell.exe").IgnoreCase);
            Assert.That(runner.LastArguments, Is.EqualTo(WindowsProcessCommandLine.BuildArguments(77)));
        }

        [Test]
        public void Query_IsTimeBounded()
        {
            var runner = new ScriptedRunner(Win01.Ok("c:\\x.exe --a"));

            WindowsProcessCommandLine.Query(77, runner);
            Assert.That(runner.LastTimeoutMs, Is.EqualTo(WindowsProcessCommandLine.DefaultTimeoutMs));

            WindowsProcessCommandLine.Query(77, runner, timeoutMs: 1500);
            Assert.That(runner.LastTimeoutMs, Is.EqualTo(1500));

            WindowsProcessCommandLine.Query(77, runner, timeoutMs: 0);
            Assert.That(
                runner.LastTimeoutMs, Is.EqualTo(WindowsProcessCommandLine.DefaultTimeoutMs),
                "a zero/negative limit never means 'wait forever'");
        }

        [Test]
        public void Query_RunnerThrows_FailsClosed()
        {
            var runner = new ScriptedRunner((_, __, ___) => throw new InvalidOperationException("boom"));

            WindowsCommandLineResult result = WindowsProcessCommandLine.Query(77, runner);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.CommandLine, Is.Null);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.RunFailed));
        }

        [Test]
        public void Query_MissingPowerShellExecutable_FailsClosedWithoutRunning()
        {
            // The analogue of the original defect: the helper executable does not exist.
            WindowsCommandLineResult result = WindowsProcessCommandLine.Query(
                77, runner: null, powerShellPath: Path.Combine(Path.GetTempPath(), "mcp-win01-missing", "powershell.exe"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(
                result.Status,
                Is.EqualTo(WindowsCommandLineStatus.ExecutableMissing)
                    .Or.EqualTo(WindowsCommandLineStatus.UnsupportedPlatform));
        }

        [Test]
        public void Query_ReturnsTheExactStringOnlyOnSuccess()
        {
            string line = Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);

            WindowsCommandLineResult ok = WindowsProcessCommandLine.Query(5, new ScriptedRunner(Win01.Ok(line)));
            Assert.That(ok.Succeeded, Is.True);
            Assert.That(ok.CommandLine, Is.EqualTo(line));

            WindowsCommandLineResult none = WindowsProcessCommandLine.Query(5, new ScriptedRunner(Win01.Raw("NOTFOUND")));
            Assert.That(none.Succeeded, Is.False);
            Assert.That(none.CommandLine, Is.Null);
        }

        // ---- output parser -------------------------------------------------------------

        private static IEnumerable<TestCaseData> RoundTripCommandLines()
        {
            yield return new TestCaseData("python.exe --x 1").SetName("plain");
            yield return new TestCaseData(Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token))
                .SetName("worker04_server_with_spaces_and_quotes");
            yield return new TestCaseData(@"""C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe"" -projectPath ""C:\Users\Example User\Sample Game-workers\W 04""")
                .SetName("quoted_paths_with_spaces");
            yield return new TestCaseData(@"tool.exe --dir C:\path\with\trailing\backslash\ ""C:\ends with slash\""")
                .SetName("backslashes_and_escaped_quote");
            yield return new TestCaseData("tool.exe --root \"C:\\Users\\Example User\\Dev\\Ünï cödé 日本語\\proj x\" --emoji \ud83d\ude80")
                .SetName("unicode_and_surrogate_pair");
            yield return new TestCaseData("tool.exe a&b|c^d%PATH%$env:X`y;z=1 'single' <in >out")
                .SetName("shell_special_characters_are_data");
            yield return new TestCaseData("tool.exe line1\r\nline2 NOTFOUND\nOK:AAAA\nNULL")
                .SetName("embedded_newlines_and_fake_markers_are_data");
            yield return new TestCaseData("tool.exe " + new string('x', 30000))
                .SetName("very_long_30000_chars");
        }

        [TestCaseSource(nameof(RoundTripCommandLines))]
        public void Parse_ValidResult_RoundTripsTheExactCommandLine(string commandLine)
        {
            WindowsCommandLineResult result = WindowsProcessCommandLine.ParseOutcome(Win01.Ok(commandLine));

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.CommandLine, Is.EqualTo(commandLine));
        }

        [Test]
        public void Parse_ToleratesBlankLinesAndCrLfAroundTheSingleResult()
        {
            string b = Win01.OkLine("tool.exe --ok");

            Assert.That(
                WindowsProcessCommandLine.ParseOutcome(Win01.Raw("\r\n\r\n" + b + "\r\n\r\n")).CommandLine,
                Is.EqualTo("tool.exe --ok"));
            Assert.That(
                WindowsProcessCommandLine.ParseOutcome(Win01.Raw("  " + b + "  \n")).CommandLine,
                Is.EqualTo("tool.exe --ok"));
        }

        [Test]
        public void Parse_NotFound_IsProcessNotFound()
        {
            WindowsCommandLineResult r = WindowsProcessCommandLine.ParseOutcome(Win01.Raw("NOTFOUND\r\n"));
            Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.ProcessNotFound));
            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.CommandLine, Is.Null);
        }

        [Test]
        public void Parse_NullCommandLine_IsUnavailable_NotAnEmptySuccess()
        {
            WindowsCommandLineResult r = WindowsProcessCommandLine.ParseOutcome(Win01.Raw("NULL\r\n"));
            Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.CommandLineUnavailable));
            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.CommandLine, Is.Null);
        }

        [Test]
        public void Parse_NotStarted_Timeout_NonZeroExit_AndTruncation_AreAllFailures()
        {
            string good = Win01.OkLine("tool.exe --ok");

            Assert.That(
                WindowsProcessCommandLine.ParseOutcome(new WindowsCommandRunOutcome { Started = false, StandardOutput = good }).Status,
                Is.EqualTo(WindowsCommandLineStatus.RunFailed));
            Assert.That(WindowsProcessCommandLine.ParseOutcome(null).Status, Is.EqualTo(WindowsCommandLineStatus.RunFailed));

            WindowsCommandLineResult timeout = WindowsProcessCommandLine.ParseOutcome(
                new WindowsCommandRunOutcome { Started = true, TimedOut = true, ExitCode = 0, StandardOutput = good, OutputComplete = true });
            Assert.That(timeout.Status, Is.EqualTo(WindowsCommandLineStatus.TimedOut));
            Assert.That(timeout.Succeeded, Is.False, "a timed-out query is never trusted even if it printed a result");

            WindowsCommandLineResult exit1 = WindowsProcessCommandLine.ParseOutcome(Win01.Raw(good, exit: 1));
            Assert.That(exit1.Status, Is.EqualTo(WindowsCommandLineStatus.RunFailed));
            Assert.That(exit1.Succeeded, Is.False);

            WindowsCommandLineResult truncated = WindowsProcessCommandLine.ParseOutcome(
                new WindowsCommandRunOutcome { Started = true, ExitCode = 0, StandardOutput = good, OutputTruncated = true, OutputComplete = true });
            Assert.That(truncated.Status, Is.EqualTo(WindowsCommandLineStatus.MalformedOutput));
        }

        [TestCase("")]
        [TestCase("   \r\n  \r\n")]
        [TestCase("hello")]
        [TestCase("ok:AAAA")]
        [TestCase("OK:")]
        [TestCase("OK: AAAA")]
        [TestCase("OK:AAA")]
        [TestCase("OK:AA=A")]
        [TestCase("OK:A===")]
        [TestCase("OK:AAAA!")]
        [TestCase("OK:AA\u00e9A")]
        // .NET's own decoder silently ignores whitespace; the strict check must not.
        [TestCase("OK:4: dG9vbA==")]
        [TestCase("OK:4:dG9v bA==")]
        [TestCase("OK:4:dG9vbA== ==")]
        [TestCase("OK:4:dG9vbA ==")]
        // The declared byte count must match exactly.
        [TestCase("OK:5:dG9vbA==")]
        [TestCase("OK:3:dG9vbA==")]
        [TestCase("OK:0:dG9vbA==")]
        [TestCase("OK:-4:dG9vbA==")]
        [TestCase("OK:+4:dG9vbA==")]
        [TestCase("OK:4 :dG9vbA==")]
        [TestCase("OK:x:dG9vbA==")]
        [TestCase("OK:4")]
        [TestCase("OK:4:")]
        [TestCase("OK::dG9vbA==")]
        [TestCase("OK:99999999999:dG9vbA==")]
        // No length at all is not the protocol.
        [TestCase("OK:dG9vbA==")]
        [TestCase("NOTFOUND extra")]
        [TestCase("NULL\nNOTFOUND")]
        [TestCase("WARNING: noise\nOK:dG9vbA==")]
        public void Parse_MalformedOutput_IsRejected(string stdout)
        {
            WindowsCommandLineResult r = WindowsProcessCommandLine.ParseOutcome(Win01.Raw(stdout));

            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.CommandLine, Is.Null);
            Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.MalformedOutput));
        }

        [Test]
        public void Parse_ValidBase64ThatIsNotValidUtf8_OrIsEmptyOrContainsNul_IsRejected()
        {
            string invalidUtf8 = Win01.OkLine(new byte[] { 0xFF, 0xFE, 0xFD, 0x41 });
            string empty = "OK:0:";
            string nul = Win01.OkLine("tool.exe\0--x");

            foreach (string stdout in new[] { invalidUtf8, empty, nul })
            {
                WindowsCommandLineResult r = WindowsProcessCommandLine.ParseOutcome(Win01.Raw(stdout));
                Assert.That(r.Succeeded, Is.False, stdout);
                Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.MalformedOutput), stdout);
            }
        }

        [Test]
        public void Parse_StderrAloneNeverProducesSuccess()
        {
            var outcome = new WindowsCommandRunOutcome
            {
                Started = true,
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = Win01.OkLine("tool.exe --forged"),
                OutputComplete = true,
            };

            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
        }
    }

    // ================================================================================
    // 2. Real Windows process queries (no scripted runner): the actual PowerShell/CIM path
    // ================================================================================

    [TestFixture]
    [Platform("Win")]
    public class McpWin01LiveProcessTests
    {
        private sealed class MarkerChild : IDisposable
        {
            public Process Process { get; }
            public string Arguments { get; }

            public MarkerChild(string marker)
            {
                // A PowerShell that only sleeps; everything after '#' is a comment, so the marker is
                // carried purely as command-line data (quotes, spaces and Unicode included).
                string comment = marker.Replace("\"", "\\\"");
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60 # " + comment + "\"";
                var psi = new ProcessStartInfo
                {
                    FileName = WindowsProcessCommandLine.ResolvePowerShellPath(),
                    Arguments = Arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process = Process.Start(psi);
                // Give the OS a moment to publish the process record.
                System.Threading.Thread.Sleep(600);
            }

            public void Dispose()
            {
                try { if (!Process.HasExited) Process.Kill(); } catch { }
                try { Process.Dispose(); } catch { }
            }
        }

        [OneTimeSetUp]
        public void RequirePowerShell()
        {
            string path = WindowsProcessCommandLine.ResolvePowerShellPath();
            Assume.That(path != null && File.Exists(path), "Windows PowerShell is required for the live tests.");
        }

        [Test]
        public void WmicIsNotRequired_ThisHostMayOrMayNotHaveIt()
        {
            // Documents the environment the rest of this fixture runs in; it asserts nothing about wmic.
            string wmic = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "wbem", "WMIC.exe");
            TestContext.Out.WriteLine($"wmic.exe present on this host: {File.Exists(wmic)}");

            WindowsCommandLineResult r = WindowsProcessCommandLine.Query(Process.GetCurrentProcess().Id);

            Assert.That(r.Succeeded, Is.True, r.Status + ": " + r.Detail);
        }

        [Test]
        public void OwnProcess_ReturnsItsRealCommandLine()
        {
            WindowsCommandLineResult r = WindowsProcessCommandLine.Query(Process.GetCurrentProcess().Id);

            Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), r.Detail);
            Assert.That(r.CommandLine, Does.Contain(Process.GetCurrentProcess().ProcessName).IgnoreCase);
        }

        [Test]
        public void Child_WithQuotesSpacesAndUnicode_ReturnsTheExactCommandLine()
        {
            string marker = "--unity-instance-token " + Win01.Token
                            + " --unity-project-root \"C:\\Users\\Example User\\Dev\\Ünï cödé 日本語\\WORKER 04\\proj x\""
                            + " --http-url " + Win01.Endpoint;

            using (var child = new MarkerChild(marker))
            {
                WindowsCommandLineResult r = WindowsProcessCommandLine.Query(child.Process.Id);

                Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), r.Detail);
                // The command line the OS reports is exactly what was passed, byte for byte.
                Assert.That(r.CommandLine, Does.Contain(child.Arguments));
                Assert.That(r.CommandLine, Does.Contain("Ünï cödé 日本語"));
                Assert.That(r.CommandLine, Does.Contain(Win01.Token));
            }
        }

        [Test]
        public void ProcessThatNeverExisted_IsProcessNotFound()
        {
            WindowsCommandLineResult r = WindowsProcessCommandLine.Query(2147483000);

            Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.ProcessNotFound));
            Assert.That(r.Succeeded, Is.False);
        }

        [Test]
        public void TerminatedProcess_IsProcessNotFound()
        {
            var psi = new ProcessStartInfo
            {
                FileName = WindowsProcessCommandLine.ResolvePowerShellPath(),
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command exit 0",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            int pid;
            using (Process p = Process.Start(psi))
            {
                pid = p.Id;
                p.WaitForExit(15000);
            }

            Assert.That(WindowsProcessCommandLine.Query(pid).Status, Is.EqualTo(WindowsCommandLineStatus.ProcessNotFound));
        }

        [Test]
        public void ProcessWhoseMetadataTheOsWithholds_NeverYieldsACommandLine()
        {
            // PID 4 is the NT kernel "System" process: it exists but has no readable command line.
            WindowsCommandLineResult r = WindowsProcessCommandLine.Query(4);

            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.CommandLine, Is.Null);
            Assert.That(
                r.Status,
                Is.EqualTo(WindowsCommandLineStatus.CommandLineUnavailable)
                    .Or.EqualTo(WindowsCommandLineStatus.ProcessNotFound));
        }

        [Test]
        public void ARealQueryThatExceedsItsLimit_TimesOutAndIsRejected()
        {
            WindowsCommandLineResult r = WindowsProcessCommandLine.Query(Process.GetCurrentProcess().Id, timeoutMs: 1);

            Assert.That(r.Status, Is.EqualTo(WindowsCommandLineStatus.TimedOut));
            Assert.That(r.Succeeded, Is.False);
        }

        [Test]
        public void TheRealProcessDetector_ReturnsTheNormalizedLowerCaseCommandLine()
        {
            string marker = "--unity-instance-token " + Win01.Token
                            + " --unity-project-root \"" + Win01.Worker04Root + "\""
                            + " --http-url " + Win01.Endpoint;

            using (var child = new MarkerChild(marker))
            {
                var detector = new ProcessDetector(new ProcessCommandLineObserver(
                    ProcessCommandLineObserver.ReadProcessCreationUtc,
                    (pid, token) => WindowsProcessCommandLine.Query(pid, null, null, WindowsProcessCommandLine.DefaultTimeoutMs, token)));
                Win01.Prime(detector, child.Process.Id);

                bool ok = detector.TryGetProcessCommandLine(child.Process.Id, out string argsLower);

                Assert.That(ok, Is.True);
                Assert.That(argsLower, Is.EqualTo(argsLower.ToLowerInvariant()));
                Assert.That(argsLower.Any(char.IsWhiteSpace), Is.False, "the established contract strips whitespace");
                Assert.That(argsLower, Does.Contain(Win01.Token));
                Assert.That(argsLower, Does.Contain(detector.NormalizeForMatch(Win01.Worker04Root)));
                Assert.That(argsLower, Does.Contain(detector.NormalizeForMatch(Win01.Endpoint)));
            }
        }

        [Test]
        public void TheRealProcessDetector_RejectsAnAbsentProcess()
        {
            var detector = new ProcessDetector(new ProcessCommandLineObserver(
                ProcessCommandLineObserver.ReadProcessCreationUtc,
                (pid, token) => WindowsProcessCommandLine.Query(pid, null, null, WindowsProcessCommandLine.DefaultTimeoutMs, token)));

            Assert.That(detector.TryGetProcessCommandLine(2147483000, out string argsLower), Is.False);
            Assert.That(argsLower, Is.Empty);
        }
    }

    // ================================================================================
    // 3. The production ProcessDetector/McpProcessInspector with a scripted PowerShell outcome
    // ================================================================================

    [TestFixture]
    [Platform("Win")]
    public class McpWin01ProcessDetectorTests
    {
        private static IEnumerable<TestCaseData> FailureOutcomes()
        {
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = false }).SetName("cannot_start");
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = true, TimedOut = true }).SetName("timeout");
            yield return new TestCaseData(Win01.Raw("NOTFOUND")).SetName("process_not_found");
            yield return new TestCaseData(Win01.Raw("NULL")).SetName("command_line_unavailable_or_access_denied");
            yield return new TestCaseData(Win01.Raw("")).SetName("empty_output");
            yield return new TestCaseData(Win01.Raw("OK:@@@@")).SetName("malformed_payload");
            yield return new TestCaseData(Win01.Raw(Win01.OkLine("x"), exit: 5)).SetName("non_zero_exit");
        }

        [TestCaseSource(nameof(FailureOutcomes))]
        public void EveryFailureMode_FailsClosed(WindowsCommandRunOutcome outcome)
        {
            ProcessDetector detector = Win01.DetectorFor(new ScriptedRunner(outcome));
            Win01.Prime(detector, 5150);

            Assert.That(detector.TryGetProcessCommandLine(5150, out string argsLower), Is.False);
            Assert.That(argsLower, Is.Empty);

            var inspector = new McpProcessInspector(detector);
            Assert.That(inspector.TryGetCommandLine(5150, out string line), Is.False);
            Assert.That(line, Is.Null);
        }

        [Test]
        public void ValidResult_IsNormalizedLowerCaseWithoutWhitespace()
        {
            string line = Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);
            ProcessDetector detector = Win01.DetectorFor(new ScriptedRunner(Win01.Ok(line)));
            Win01.Prime(detector, Win01.ServerPid);

            Assert.That(detector.TryGetProcessCommandLine(Win01.ServerPid, out string argsLower), Is.True);
            Assert.That(argsLower, Is.EqualTo(detector.NormalizeForMatch(line)));
            Assert.That(argsLower, Does.Contain(Win01.Token));
            Assert.That(argsLower, Does.Contain("--unity-instance-token"));
        }

        [Test]
        public void TheRunnerIsGivenTheRequestedPid_AndNothingElse()
        {
            var runner = new ScriptedRunner(Win01.Ok("tool.exe"));
            ProcessDetector detector = Win01.DetectorFor(runner);
            Win01.Prime(detector, Win01.ServerPid);
            detector.TryGetProcessCommandLine(Win01.ServerPid, out _);

            Assert.That(runner.Calls, Is.EqualTo(1));
            Assert.That(runner.LastArguments, Is.EqualTo(WindowsProcessCommandLine.BuildArguments(Win01.ServerPid)));
        }
    }

    // ================================================================================
    // 4. End to end: the managed ownership proof built on the new command-line source
    // ================================================================================

    [TestFixture]
    [Platform("Win")]
    public class McpWin01OwnershipChainTests
    {
        /// <summary>Everything except the command line is scripted; the command line goes through the real detector.</summary>
        private sealed class ScenarioInspector : IMcpProcessInspector
        {
            private readonly McpProcessInspector _commandLines;
            private readonly ProcessDetector _detector;

            public ScenarioInspector(IWindowsCommandRunner runner)
            {
                // The lifetime the observer binds a result to is the scenario's own, read lazily so
                // a test may change it after construction.
                _detector = Win01.DetectorFor(
                    runner,
                    pid => pid == EditorPid ? EditorStart : (ServerExists ? ServerStart : (DateTime?)null));
                _commandLines = new McpProcessInspector(_detector);
            }

            /// <summary>Lets the background command-line query finish, as the awaiting lifecycle code does.</summary>
            public void Prime() => Win01.Prime(_detector, Win01.ServerPid);

            public int EditorPid = Win01.EditorPid;
            public DateTime EditorStart = Win01.EditorStart;
            public DateTime ServerStart = Win01.ServerStart;
            public bool ServerExists = true;
            public List<int> Listeners = new List<int> { Win01.ServerPid };

            public int GetCurrentProcessId() => EditorPid;
            public bool TryGetCurrentProcessStartTimeUtc(out DateTime startUtc) { startUtc = EditorStart; return true; }
            public bool TryGetProcessStartTimeUtc(int pid, out DateTime startUtc)
            {
                startUtc = pid == EditorPid ? EditorStart : ServerStart;
                return pid == EditorPid || ServerExists;
            }

            public bool ProcessExists(int pid) => pid == EditorPid || ServerExists;
            public bool TryObserveProcessExistence(int pid, out bool exists) { exists = pid == EditorPid || ServerExists; return true; }
            public bool TryOpenRetainedProcess(int pid, out IRetainedProcessHandle handle) { handle = null; return false; }
            public bool TryGetCommandLine(int pid, out string commandLine) => _commandLines.TryGetCommandLine(pid, out commandLine);
            public IReadOnlyList<int> GetListeningProcessIds(int port) => Listeners;
        }

        private sealed class ScenarioStore : IMcpRouteStateStore
        {
            public string Root = McpRunStatePaths.Canonicalize(Win01.Worker04Root);
            public string PidFileText = Win01.ServerPid.ToString();

            public string GetCanonicalProjectRoot() => Root;
            public string GetRunStateDirectory() => McpRunStatePaths.GetRunStateDirectory(Root);
            public string GetHandshakePath() => McpRunStatePaths.GetHandshakePath(Root);
            public string GetPidFilePath(int port) => McpRunStatePaths.GetPidFilePath(Root, port);
            public bool TryRead(out McpRunStateRecord record, out string error) { record = null; error = "n/a"; return false; }
            public McpOwnershipSnapshot ReadOwnershipState() => throw new NotSupportedException();
            public IMcpOwnershipLockProvider OwnershipLocks => throw new NotSupportedException();
            public bool Write(McpRunStateRecord record, out string error) { error = "n/a"; return false; }

            public bool TryReadPidFile(string pidFilePath, out int pid, out bool fileExists)
            {
                fileExists = PidFileText != null;
                pid = 0;
                return fileExists && int.TryParse(PidFileText, out pid) && pid > 0;
            }

            public void DeleteRecord() { }
        }

        private static McpRunStateRecord PendingRecord(string root = Win01.Worker04Root, string endpoint = Win01.Endpoint, string token = Win01.Token)
        {
            string canonical = McpRunStatePaths.Canonicalize(root);
            return new McpRunStateRecord
            {
                SchemaVersion = McpRunStateRecord.CurrentSchemaVersion,
                RecordId = "0a0e121a6eb34d838867e4d547b1f2bb",
                CanonicalProjectRoot = canonical,
                Endpoint = endpoint,
                EditorPid = Win01.EditorPid,
                EditorStartUtc = McpRunStateRecord.FormatUtc(Win01.EditorStart),
                ServerPid = 0,
                ServerStartUtc = null,
                InstanceToken = token,
                PidFilePath = McpRunStatePaths.GetPidFilePath(canonical, Win01.Port),
                LifecycleState = McpRunStateRecord.LifecycleStarting,
                WrittenUtc = McpRunStateRecord.FormatUtc(Win01.RecordWritten),
            };
        }

        private static McpOwnershipObservation Observe(
            ScenarioInspector inspector, ScenarioStore store, McpRunStateRecord record, string endpoint = Win01.Endpoint, string root = null)
        {
            inspector.Prime();
            McpOwnershipObservation o = McpRouteObservationBuilder.Build(
                store,
                inspector,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = root ?? store.Root,
                    Endpoint = endpoint,
                    CurrentEditorPid = inspector.GetCurrentProcessId(),
                    RecordedServerPid = record != null && record.ServerPid > 0 ? record.ServerPid : 0,
                    RecordedEditorPid = record != null ? record.EditorPid : 0,
                });
            o.ServerPid = o.PidFilePid; // exactly what ServerManagementService.TryPromotePendingRecord does
            return o;
        }

        private static ScriptedRunner RunnerFor(string commandLine) => new ScriptedRunner(Win01.Ok(commandLine));

        private static string GoodCommandLine => Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);

        // ---- the repaired behaviour ----------------------------------------------------

        [Test]
        public void ValidWorker04Server_IsPromotedToRunning_AndTheGateReleasesTheNonce()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine));
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            bool promoted = McpOwnershipEvaluator.TryIdentifyPendingServer(
                pending, Observe(inspector, store, pending), out McpRunStateRecord running, out string detail);

            Assert.That(promoted, Is.True, detail);
            Assert.That(running.LifecycleState, Is.EqualTo(McpRunStateRecord.LifecycleRunning));
            Assert.That(running.ServerPid, Is.EqualTo(Win01.ServerPid));
            Assert.That(running.RecordId, Is.EqualTo(pending.RecordId), "identity survives starting -> running");

            McpOwnershipObservation after = Observe(inspector, store, running);
            Assert.That(McpOwnershipEvaluator.EvaluateStop(running, after).Allowed, Is.True);
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(running, after, out string token, out _, out _),
                Is.True);
            Assert.That(token, Is.EqualTo(Win01.Token));
        }

        [Test]
        public void TheNonceMustMatchExactly_NotCaseInsensitively()
        {
            // The server compares the nonce case-sensitively, and a managed launch emits a lower-case
            // GUID, so an upper-cased line is a different token, not a spelling of the same one.
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine.Replace(Win01.Token, Win01.Token.ToUpperInvariant())));
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out string detail),
                Is.False);
            Assert.That(detail, Does.Contain("nonce"));
        }

        // ---- original defect, now fail-closed on any query failure ---------------------

        private static IEnumerable<TestCaseData> UnreadableCommandLine()
        {
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = false }).SetName("powershell_missing_or_blocked");
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = true, TimedOut = true }).SetName("process_timeout");
            yield return new TestCaseData(Win01.Raw("NOTFOUND")).SetName("process_not_found");
            yield return new TestCaseData(Win01.Raw("NULL")).SetName("inaccessible_metadata");
            yield return new TestCaseData(Win01.Raw("OK:")).SetName("empty_command_line");
            yield return new TestCaseData(Win01.Raw("garbage")).SetName("malformed_output");
        }

        [TestCaseSource(nameof(UnreadableCommandLine))]
        public void WhenTheCommandLineCannotBeRead_OwnershipIsNeverGranted(WindowsCommandRunOutcome outcome)
        {
            var inspector = new ScenarioInspector(new ScriptedRunner(outcome));
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            bool promoted = McpOwnershipEvaluator.TryIdentifyPendingServer(
                pending, Observe(inspector, store, pending), out McpRunStateRecord running, out string detail);

            Assert.That(promoted, Is.False);
            Assert.That(running, Is.Null);
            Assert.That(detail, Does.Contain("nonce"));

            // A record that is already "running" is not enough either: the live proof is still required.
            McpRunStateRecord alreadyRunning = RunningRecord();
            McpOwnershipObservation obs = Observe(inspector, store, alreadyRunning);
            Assert.That(McpOwnershipEvaluator.EvaluateStop(alreadyRunning, obs).Allowed, Is.False);
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(alreadyRunning, obs, out string token, out _, out _),
                Is.False);
            Assert.That(token, Is.Null);
        }

        private static McpRunStateRecord RunningRecord()
        {
            McpRunStateRecord r = PendingRecord();
            r.LifecycleState = McpRunStateRecord.LifecycleRunning;
            r.ServerPid = Win01.ServerPid;
            r.ServerStartUtc = McpRunStateRecord.FormatUtc(Win01.ServerStart);
            return r;
        }

        // ---- identity invariants are unchanged -----------------------------------------

        [Test]
        public void ANonceMismatch_IsRefused()
        {
            var inspector = new ScenarioInspector(
                RunnerFor(Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.OtherToken)));
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out string detail),
                Is.False);
            Assert.That(detail, Does.Contain("nonce"));

            McpRunStateRecord running = RunningRecord();
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(running, Observe(inspector, store, running)).Allowed, Is.False);
        }

        [Test]
        public void AProcessThatMerelyExists_WithNoNonceInItsCommandLine_IsNotOwned()
        {
            var inspector = new ScenarioInspector(RunnerFor("python.exe -m http.server 8181"));
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out _),
                Is.False);
        }

        [Test]
        public void TheWrongPort_IsRefused()
        {
            var inspector = new ScenarioInspector(
                RunnerFor(Win01.ServerCommandLine(Win01.Worker04Root, "http://127.0.0.1:8182", Win01.Token)));
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            // The editor observes a different endpoint than the one the record was written for.
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending, endpoint: "http://127.0.0.1:8182"), out _, out _),
                Is.False);

            McpRunStateRecord running = RunningRecord();
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(
                    running, Observe(inspector, store, running, endpoint: "http://127.0.0.1:8182")).Allowed,
                Is.False);
        }

        [Test]
        public void AnotherWorkersWorktree_IsRefused()
        {
            var inspector = new ScenarioInspector(
                RunnerFor(Win01.ServerCommandLine(Win01.Worker03Root, Win01.Endpoint, Win01.Token)));
            var other = new ScenarioStore { Root = McpRunStatePaths.Canonicalize(Win01.Worker03Root) };
            McpRunStateRecord pending = PendingRecord(); // belongs to WORKER-04

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, other, pending), out _, out string detail),
                Is.False);
            Assert.That(detail, Does.Contain("different project").Or.Contain("RunState"));
        }

        [Test]
        public void PidReuse_ServerProcessOlderThanTheRecord_IsRefused()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine))
            {
                ServerStart = Win01.RecordWritten.AddSeconds(-30),
            };
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out string detail),
                Is.False);
            Assert.That(detail, Does.Contain("PID reuse"));
        }

        [Test]
        public void PidReuse_ARunningRecordWhoseServerLifetimeDiffers_IsRefused()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine))
            {
                ServerStart = Win01.ServerStart.AddSeconds(90),
            };
            var store = new ScenarioStore();
            McpRunStateRecord running = RunningRecord();

            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(running, Observe(inspector, store, running)).Allowed, Is.False);
        }

        [Test]
        public void AnotherEditorLifetime_IsRefused()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine))
            {
                EditorStart = Win01.EditorStart.AddMinutes(5),
            };
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out string detail),
                Is.False);
            Assert.That(detail, Does.Contain("editor lifetime"));
        }

        [Test]
        public void MultipleListeners_OrAForeignListener_IsRefused()
        {
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            var shared = new ScenarioInspector(RunnerFor(GoodCommandLine))
            {
                Listeners = new List<int> { Win01.ServerPid, 9999 },
            };
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(shared, store, pending), out _, out string sharedDetail),
                Is.False);
            Assert.That(sharedDetail, Does.Contain("exclusively"));

            var foreign = new ScenarioInspector(RunnerFor(GoodCommandLine))
            {
                Listeners = new List<int> { 9999 },
            };
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(foreign, store, pending), out _, out _),
                Is.False);

            var none = new ScenarioInspector(RunnerFor(GoodCommandLine)) { Listeners = new List<int>() };
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(none, store, pending), out _, out _),
                Is.False);
        }

        [Test]
        public void AServerThatIsGone_IsRefused()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine)) { ServerExists = false };
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out _),
                Is.False);
        }

        [Test]
        public void ThePidFileMustNameTheServer()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine));
            McpRunStateRecord pending = PendingRecord();

            foreach (string text in new[] { null, "", "not-a-pid", "0", "-3" })
            {
                var store = new ScenarioStore { PidFileText = text };
                Assert.That(
                    McpOwnershipEvaluator.TryIdentifyPendingServer(
                        pending, Observe(inspector, store, pending), out _, out _),
                    Is.False,
                    "pid file text: " + (text ?? "<absent>"));
            }
        }

        [Test]
        public void TheEditorProcessItselfIsNeverAcceptedAsTheServer()
        {
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine))
            {
                Listeners = new List<int> { Win01.EditorPid },
            };
            var store = new ScenarioStore { PidFileText = Win01.EditorPid.ToString() };
            McpRunStateRecord pending = PendingRecord();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, Observe(inspector, store, pending), out _, out _),
                Is.False);
        }

        [Test]
        public void AdoptionIsStillBlockedForALivePendingRecordOfAnotherLifetime()
        {
            // Unchanged safety: a leftover pending record from a different editor is never adopted,
            // even though the command line can now be read.
            var inspector = new ScenarioInspector(RunnerFor(GoodCommandLine)) { EditorPid = 777, EditorStart = Win01.EditorStart.AddHours(1) };
            var store = new ScenarioStore();
            McpRunStateRecord pending = PendingRecord();

            McpAdoptionOutcome outcome = McpOwnershipEvaluator.EvaluateAdoption(
                pending, Observe(inspector, store, pending), out _);

            Assert.That(outcome, Is.EqualTo(McpAdoptionOutcome.LiveForeign));
        }
    }
}
