using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// R1-03 (MAJOR): a process-inspection result counts only after the ENTIRE operation succeeded -
    /// confirmed exit 0, both pipes read to the end without error, no timeout, no cancellation, no
    /// truncation, no unexpected diagnostic output and one well-formed result line. Apparently valid
    /// partial output must never override a later failure.
    ///
    /// The runner tests start real child PowerShell processes. Every child is started by the test
    /// itself and is only ever stopped through the <see cref="Process"/> handle the runner holds for
    /// it; nothing here looks a process up by PID or touches any process it did not start.
    /// </summary>
    [TestFixture]
    public class McpWin01ReaderContractTests
    {
        private static string ValidLine => Win01.OkLine("tool.exe --unity-instance-token " + Win01.Token);

        private static WindowsCommandLineResult Parse(string stdout, string stderr = "", int exit = 0, bool complete = true, bool truncated = false)
            => WindowsProcessCommandLine.ParseOutcome(new WindowsCommandRunOutcome
            {
                Started = true,
                ExitCode = exit,
                StandardOutput = stdout,
                StandardError = stderr,
                OutputComplete = complete,
                OutputTruncated = truncated,
            });

        // ---- parser-level contract ---------------------------------------------------------

        [Test]
        public void ACompleteResponse_Succeeds()
        {
            WindowsCommandLineResult result = Parse(ValidLine + "\r\n");

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok));
            Assert.That(result.CommandLine, Does.Contain(Win01.Token));
        }

        [Test]
        public void ValidOutput_ButTheReadWasNotCompleted_IsRejected()
        {
            WindowsCommandLineResult result = Parse(ValidLine, complete: false);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.IncompleteOutput));
            Assert.That(result.CommandLine, Is.Null);
        }

        [Test]
        public void AnOutcomeThatDoesNotAssertCompletion_CanNeverAuthorize()
        {
            // Completion defaults to false: an outcome must positively prove it.
            var outcome = new WindowsCommandRunOutcome { Started = true, ExitCode = 0, StandardOutput = ValidLine };

            Assert.That(outcome.OutputComplete, Is.False);
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
        }

        [TestCase("OK:4:dG9vbA=")]
        [TestCase("OK:4:dG9vbA")]
        [TestCase("OK:4:dG9v")]
        [TestCase("OK:4:dG9vbA==dG9vbA==")]
        [TestCase("OK:4:dG9vb")]
        [TestCase("OK:4:d")]
        public void ATruncatedOrMangledBase64Payload_IsRejected(string stdout)
        {
            WindowsCommandLineResult result = Parse(stdout);

            Assert.That(result.Succeeded, Is.False, stdout);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.MalformedOutput), stdout);
        }

        [Test]
        public void ARealPayloadCutAtEveryLength_IsRejected_EvenOnABase64Boundary()
        {
            string commandLine = Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);
            string full = Win01.OkLine(commandLine);
            int prefix = full.IndexOf(':', 3) + 1; // after "OK:<count>:"

            Assert.That(Parse(full).Succeeded, Is.True, "control: the whole line");

            for (int cut = 0; cut < full.Length; cut++)
            {
                WindowsCommandLineResult result = Parse(full.Substring(0, cut));
                Assert.That(result.Succeeded, Is.False, "cut at " + cut);
            }

            // The same payload cut on a four-character Base64 boundary, with the ORIGINAL length kept:
            // well-formed Base64, but not the bytes the length promised.
            string boundaryCut = full.Substring(0, prefix + ((full.Length - prefix) / 8) * 4);
            Assert.That(Parse(boundaryCut).Succeeded, Is.False);
        }

        [TestCase("garbage\nVALID")]
        [TestCase("VALID\ngarbage")]
        [TestCase("VALID\nVALID")]
        [TestCase("VALID\n\nNOTFOUND")]
        [TestCase("NULL\nVALID")]
        [TestCase("VALID\nUNTRUSTED:module")]
        [TestCase("UNTRUSTED:module\nVALID")]
        public void MixedValidAndInvalidLines_AreRejected(string template)
        {
            WindowsCommandLineResult result = Parse(template.Replace("VALID", ValidLine));

            Assert.That(result.Succeeded, Is.False, template);
            Assert.That(result.CommandLine, Is.Null);
        }

        [Test]
        public void AnUntrustedProviderMarker_IsNotAResult()
        {
            WindowsCommandLineResult result = Parse("UNTRUSTED:module");

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.UntrustedProvider));
            Assert.That(result.Succeeded, Is.False);
        }

        // ---- stderr handling: the documented contract ---------------------------------------
        // Success needs stderr to have been read to its end without error, and to hold either nothing or
        // only PowerShell's CLIXML progress envelope. Any other diagnostic text is an anomaly.

        private const string ProgressOnly =
            "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
            + "<Obj S=\"progress\" RefId=\"0\"><MS><I64 N=\"SourceId\">1</I64></MS></Obj></Objs>";

        [Test]
        public void ErrorTextOnStderr_AfterApparentSuccess_InvalidatesTheResult()
        {
            WindowsCommandLineResult result = Parse(ValidLine, stderr: "Get-CimInstance : something went wrong\r\n");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.IncompleteOutput));
        }

        [Test]
        public void ACliXmlErrorStreamOnStderr_InvalidatesTheResult()
        {
            string withError = ProgressOnly.Replace("S=\"progress\"", "S=\"Error\"");

            Assert.That(Parse(ValidLine, stderr: withError).Succeeded, Is.False);
        }

        [Test]
        public void BenignCliXmlProgressOnStderr_DoesNotInvalidateAGoodResult()
        {
            Assert.That(Parse(ValidLine, stderr: ProgressOnly).Succeeded, Is.True);
            Assert.That(Parse(ValidLine, stderr: "").Succeeded, Is.True);
            Assert.That(Parse(ValidLine, stderr: "   \r\n").Succeeded, Is.True);
        }

        [Test]
        public void StderrNeverBecomesTheResult()
        {
            Assert.That(Parse("", stderr: ValidLine).Succeeded, Is.False);
        }

        // ---- the real runner against real children ------------------------------------------

        private static string PowerShell => WindowsProcessCommandLine.ResolvePowerShellPath();

        private static WindowsCommandRunRequest ChildRequest(string script, int timeoutMs = 20000)
            => new WindowsCommandRunRequest
            {
                ExecutablePath = PowerShell,
                Arguments = WindowsProcessCommandLine.BuildArguments(script),
                TimeoutMs = timeoutMs,
            };

        private static string PrintValidLine()
            => "[Console]::Out.Write('" + ValidLine + "');[Console]::Out.Flush();";

        private sealed class FaultAfterFirstRead : TextReader
        {
            private readonly TextReader _inner;
            private int _reads;

            public FaultAfterFirstRead(TextReader inner)
            {
                _inner = inner;
            }

            public override int Read(char[] buffer, int index, int count)
            {
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    return _inner.Read(buffer, index, count);
                }

                throw new IOException("injected pipe failure after the first chunk");
            }
        }

        /// <summary>A started child's identity, kept so a test can prove it is really gone.</summary>
        private sealed class ChildProbe
        {
            public int Pid;
            public DateTime Start;

            public void Capture(Process process)
            {
                Pid = process.Id;
                Start = process.StartTime;
            }

            public bool IsGone()
            {
                try
                {
                    using (Process p = Process.GetProcessById(Pid))
                    {
                        return p.StartTime != Start || p.HasExited;
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        [Test]
        [Platform("Win")]
        public void ValidOutput_FollowedByAReaderException_IsNotAuthorizing()
        {
            var probe = new ChildProbe();
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(
                probe.Capture, reader => new FaultAfterFirstRead(reader), null);

            WindowsCommandRunOutcome outcome = runner.Run(ChildRequest(PrintValidLine() + "exit 0"), CancellationToken.None);

            Assert.That(outcome.Started, Is.True);
            Assert.That(outcome.ExitCode, Is.EqualTo(0), "the child itself succeeded");
            Assert.That(outcome.StandardOutput, Does.Contain(ValidLine), "valid-looking output WAS received");
            Assert.That(outcome.OutputComplete, Is.False, "but the pipe failed afterwards");
            WindowsCommandLineResult result = WindowsProcessCommandLine.ParseOutcome(outcome);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.IncompleteOutput));
            Assert.That(probe.IsGone(), Is.True);
        }

        [Test]
        [Platform("Win")]
        public void TheProductionQuery_WithAReaderFaultAfterTheRealResult_IsNotAuthorizing()
        {
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(
                null, reader => new FaultAfterFirstRead(reader), null);

            WindowsCommandLineResult result = WindowsProcessCommandLine.Query(
                Process.GetCurrentProcess().Id, runner);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.CommandLine, Is.Null);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.IncompleteOutput));
        }

        [Test]
        [Platform("Win")]
        public void AStderrReaderFault_AfterAGoodResult_IsNotAuthorizing()
        {
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(
                null, null, reader => new FaultAfterFirstRead(reader));

            // Make sure there is something on stderr to read, so the fault actually happens.
            WindowsCommandRunOutcome outcome = runner.Run(
                ChildRequest(PrintValidLine() + "[Console]::Error.Write('x');[Console]::Error.Flush();Start-Sleep -Milliseconds 300;[Console]::Error.Write('y');exit 0"),
                CancellationToken.None);

            Assert.That(outcome.StandardOutput, Does.Contain(ValidLine));
            Assert.That(outcome.OutputComplete, Is.False);
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
        }

        [Test]
        [Platform("Win")]
        public void ValidOutput_FollowedByANonZeroExit_IsNotAuthorizing()
        {
            WindowsCommandRunOutcome outcome = WindowsProcessCommandLine.BoundedProcessRunner.Instance.Run(
                ChildRequest(PrintValidLine() + "exit 3"), CancellationToken.None);

            Assert.That(outcome.StandardOutput, Does.Contain(ValidLine));
            Assert.That(outcome.ExitCode, Is.EqualTo(3));
            WindowsCommandLineResult result = WindowsProcessCommandLine.ParseOutcome(outcome);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.RunFailed));
        }

        [Test]
        [Platform("Win")]
        public void ValidOutput_FollowedByATimeout_IsNotAuthorizing_AndTheChildIsStopped()
        {
            var probe = new ChildProbe();
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(probe.Capture, null, null);
            var clock = Stopwatch.StartNew();

            WindowsCommandRunOutcome outcome = runner.Run(
                ChildRequest(PrintValidLine() + "Start-Sleep -Seconds 60", timeoutMs: 2500), CancellationToken.None);
            clock.Stop();

            Assert.That(outcome.TimedOut, Is.True);
            Assert.That(outcome.StandardOutput, Does.Contain(ValidLine), "the line was printed before the hang");
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Status, Is.EqualTo(WindowsCommandLineStatus.TimedOut));
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
            Assert.That(probe.IsGone(), Is.True, "the query child does not outlive the call");
            Assert.That(
                clock.ElapsedMilliseconds,
                Is.LessThan(2500 + 650),
                "the child is stopped at the deadline, not after the drain grace period");
        }

        [Test]
        [Platform("Win")]
        public void ValidOutput_FollowedByCancellation_IsNotAuthorizing_AndTheChildIsStopped()
        {
            var probe = new ChildProbe();
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(probe.Capture, null, null);

            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(1200);
                var clock = Stopwatch.StartNew();

                WindowsCommandRunOutcome outcome = runner.Run(
                    ChildRequest(PrintValidLine() + "Start-Sleep -Seconds 60"), cts.Token);
                clock.Stop();

                Assert.That(outcome.Cancelled, Is.True);
                WindowsCommandLineResult result = WindowsProcessCommandLine.ParseOutcome(outcome);
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Cancelled));
                Assert.That(probe.IsGone(), Is.True);
                Assert.That(clock.ElapsedMilliseconds, Is.LessThan(8000), "cancellation is prompt");
            }
        }

        [Test]
        [Platform("Win")]
        public void ErrorTextOnStderr_AfterAGoodResult_FromARealChild_IsNotAuthorizing()
        {
            WindowsCommandRunOutcome outcome = WindowsProcessCommandLine.BoundedProcessRunner.Instance.Run(
                ChildRequest(PrintValidLine() + "[Console]::Error.Write('Get-CimInstance : boom');exit 0"), CancellationToken.None);

            Assert.That(outcome.ExitCode, Is.EqualTo(0));
            Assert.That(outcome.OutputComplete, Is.True, "the reads themselves were complete");
            WindowsCommandLineResult result = WindowsProcessCommandLine.ParseOutcome(outcome);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.IncompleteOutput));
        }

        [Test]
        [Platform("Win")]
        public void ACompleteSuccessfulRun_FromARealChild_Succeeds()
        {
            WindowsCommandRunOutcome outcome = WindowsProcessCommandLine.BoundedProcessRunner.Instance.Run(
                ChildRequest(PrintValidLine() + "exit 0"), CancellationToken.None);

            Assert.That(outcome.Started, Is.True);
            Assert.That(outcome.ExitCode, Is.EqualTo(0));
            Assert.That(outcome.OutputComplete, Is.True);
            Assert.That(outcome.OutputTruncated, Is.False);
            WindowsCommandLineResult result = WindowsProcessCommandLine.ParseOutcome(outcome);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok));
            Assert.That(result.CommandLine, Does.Contain(Win01.Token));
        }

        [Test]
        [Platform("Win")]
        public void LargeOutputOnBothPipes_NeitherDeadlocksNorIsTrusted()
        {
            // Far more than a pipe buffer on each stream: a reader that did not drain concurrently would hang.
            string script =
                "$s='x'*65536;"
                + "for($i=0;$i -lt 20;$i++){[Console]::Out.Write($s);[Console]::Error.Write($s)};"
                + "exit 0";
            var clock = Stopwatch.StartNew();

            WindowsCommandRunOutcome outcome = WindowsProcessCommandLine.BoundedProcessRunner.Instance.Run(
                ChildRequest(script, timeoutMs: 30000), CancellationToken.None);
            clock.Stop();

            Assert.That(outcome.TimedOut, Is.False, "no deadlock");
            Assert.That(outcome.ExitCode, Is.EqualTo(0));
            Assert.That(outcome.OutputComplete, Is.True, "both pipes were drained to the end");
            Assert.That(outcome.OutputTruncated, Is.True, "1.3 MB per stream exceeds the cap");
            Assert.That(outcome.StandardOutput.Length, Is.LessThanOrEqualTo(WindowsProcessCommandLine.MaxOutputChars));
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(25000));
        }

        [Test]
        [Platform("Win")]
        public void AnExceptionAfterTheChildStarted_StillStopsTheChild()
        {
            var probe = new ChildProbe();
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(
                process =>
                {
                    probe.Capture(process);
                    throw new InvalidOperationException("injected failure right after the start");
                },
                null,
                null);

            WindowsCommandRunOutcome outcome = runner.Run(ChildRequest("Start-Sleep -Seconds 60"), CancellationToken.None);

            Assert.That(outcome.OutputComplete, Is.False);
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
            Assert.That(probe.IsGone(), Is.True, "an abandoned child is stopped, not leaked");
        }

        [Test]
        [Platform("Win")]
        public void AnExecutableThatCannotStart_IsAFailedQuery()
        {
            var request = new WindowsCommandRunRequest
            {
                ExecutablePath = Path.Combine(Path.GetTempPath(), "mcp-win01-missing-" + Guid.NewGuid().ToString("N"), "powershell.exe"),
                Arguments = "-NoProfile",
                TimeoutMs = 5000,
            };

            WindowsCommandRunOutcome outcome = WindowsProcessCommandLine.BoundedProcessRunner.Instance.Run(request, CancellationToken.None);

            Assert.That(outcome.Started, Is.False);
            Assert.That(WindowsProcessCommandLine.ParseOutcome(outcome).Succeeded, Is.False);
        }

        [Test]
        [Platform("Win")]
        public void ARunThatIsCancelledBeforeItStarts_LaunchesNothing()
        {
            var probe = new ChildProbe();
            var runner = new WindowsProcessCommandLine.BoundedProcessRunner(probe.Capture, null, null);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                WindowsCommandRunOutcome outcome = runner.Run(ChildRequest("exit 0"), cts.Token);

                Assert.That(outcome.Started, Is.False);
                Assert.That(outcome.Cancelled, Is.True);
                Assert.That(probe.Pid, Is.EqualTo(0), "no child was started");
            }
        }
    }
}
