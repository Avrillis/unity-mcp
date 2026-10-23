using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Route;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// R1-01 (BLOCKER): process inspection must never make an Editor-facing callback wait for a slow
    /// CIM/PowerShell query, and nothing may be authorized while evidence is still pending, stale or
    /// failed. These tests drive the REAL observer, detector, inspector, observation builder,
    /// evaluator and pre-connect gate; only the operating system (and the speed of the query) is scripted.
    /// </summary>
    [TestFixture]
    public class McpWin01ObserverTests
    {
        // The budget the independent review proposed for the synchronous prefix of an Editor callback.
        private const double CallbackBudgetMs = 50;

        // Generous ceiling for a single outlier (GC pause, JIT) on a loaded machine.
        private const double CallbackOutlierMs = 250;

        private static string GoodCommandLine
            => Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);

        private static List<double> Sample(int count, Action call)
        {
            call(); // warm-up: JIT is not what is being measured
            var samples = new List<double>(count);
            for (int i = 0; i < count; i++)
            {
                samples.Add(Timing.MillisecondsOf(call));
            }

            return samples;
        }

        // ================================================================================
        // 1. Callback latency is independent of how slow the query is
        // ================================================================================

        [Test]
        public void SlowCimLookup_DoesNotBlockTheEditorFacingCallbacks()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord pending = harness.PendingRecord();

            // The first call starts the (held-open) query and returns at once.
            Assert.That(harness.TryGetCommandLine(Win01.ServerPid, out _), Is.False);
            Assert.That(gate.WaitUntilEntered(), Is.True, "the background query must have started");

            // Every layer an Editor callback can reach: observer, detector, inspector, whole observation.
            List<double> observer = Sample(200, () => harness.Observer.Observe(Win01.ServerPid));
            List<double> detector = Sample(200, () => harness.Detector.TryGetRawProcessCommandLine(Win01.ServerPid, out _));
            List<double> inspector = Sample(200, () => harness.TryGetCommandLine(Win01.ServerPid, out _));
            List<double> observation = Sample(100, () => harness.Observe(pending));

            foreach ((string layer, List<double> samples) in new[]
                     {
                         ("observer", observer), ("detector", detector), ("inspector", inspector), ("observation", observation),
                     })
            {
                TestContext.Out.WriteLine(
                    $"{layer,-12} p50={Timing.Percentile(samples, 0.5):F3} ms  p95={Timing.Percentile(samples, 0.95):F3} ms  max={samples.Max():F3} ms");
                Assert.That(Timing.Percentile(samples, 0.95), Is.LessThan(CallbackBudgetMs), layer + " p95");
                Assert.That(samples.Max(), Is.LessThan(CallbackOutlierMs), layer + " max");
            }

            Assert.That(gate.Calls, Is.EqualTo(1), "all of that polling shared ONE query");

            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();
        }

        [Test]
        [Category("Slow")]
        public void AnEightSecondQueryTimeout_CannotCauseAnEightSecondEditorStall()
        {
            // A query that burns its WHOLE default budget before timing out, exactly like a hung PowerShell.
            var runner = new ScriptedRunner((exe, args, timeoutMs) =>
            {
                Thread.Sleep(timeoutMs);
                return new WindowsCommandRunOutcome { Started = true, TimedOut = true };
            });
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);
            var observer = new ProcessCommandLineObserver(
                table.Get,
                (pid, token) => WindowsProcessCommandLine.Query(
                    pid, runner, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));

            var worst = 0.0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.That(observer.Observe(Win01.ServerPid).IsPending, Is.True);

            CommandLineObservation last = null;
            while (started.ElapsedMilliseconds < WindowsProcessCommandLine.DefaultTimeoutMs + 6000)
            {
                double ms = Timing.MillisecondsOf(() => last = observer.Observe(Win01.ServerPid));
                worst = Math.Max(worst, ms);
                if (!last.IsPending)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            TestContext.Out.WriteLine(
                $"query ran {started.ElapsedMilliseconds} ms end to end; slowest Editor-facing callback {worst:F3} ms");
            Assert.That(last.Status, Is.EqualTo(CommandLineObservationStatus.Failed));
            Assert.That(last.Failure, Is.EqualTo(WindowsCommandLineStatus.TimedOut));
            Assert.That(started.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(WindowsProcessCommandLine.DefaultTimeoutMs - 200));
            Assert.That(worst, Is.LessThan(CallbackOutlierMs), "no callback waited for the 8 second query");
        }

        // ================================================================================
        // 2. Pending / failed evidence never authorizes anything
        // ================================================================================

        [Test]
        public void APendingQuery_CannotAuthorizeOwnership_AtAnyDecisionPoint()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord pending = harness.PendingRecord();
            McpRunStateRecord running = harness.RunningRecord();

            McpOwnershipObservation first = harness.Observe(pending);
            Assert.That(gate.WaitUntilEntered(), Is.True);
            McpOwnershipObservation observationOfPending = harness.Observe(pending);
            McpOwnershipObservation observationOfRunning = harness.Observe(running);

            Assert.That(first.ServerCommandLineAvailable, Is.False);
            Assert.That(observationOfPending.ServerCommandLineAvailable, Is.False);

            // Promotion of a starting record.
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, observationOfPending, out McpRunStateRecord promoted, out string detail),
                Is.False);
            Assert.That(promoted, Is.Null);
            Assert.That(detail, Does.Contain("nonce"));

            // Termination / "is it running" decision.
            McpOwnershipDecision stop = McpOwnershipEvaluator.EvaluateStop(running, observationOfRunning);
            Assert.That(stop.Allowed, Is.False);
            Assert.That(stop.Reason, Is.EqualTo(McpOwnershipDenyReason.NonceMismatch));

            // Connection gate (nonce release).
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(running, observationOfRunning, out string token, out _, out _),
                Is.False);
            Assert.That(token, Is.Null);

            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();
        }

        [Test]
        public void ASuccessfulQuery_EventuallyPromotesTheCorrectRecord()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord pending = harness.PendingRecord();

            harness.Observe(pending);
            Assert.That(gate.WaitUntilEntered(), Is.True);
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, harness.Observe(pending), out _, out _),
                Is.False,
                "still pending");

            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    pending, harness.Observe(pending), out McpRunStateRecord promoted, out string detail),
                Is.True,
                detail);
            Assert.That(promoted.RecordId, Is.EqualTo(pending.RecordId), "the same lifecycle, promoted");
            Assert.That(promoted.ServerPid, Is.EqualTo(Win01.ServerPid));
            Assert.That(promoted.LifecycleState, Is.EqualTo(McpRunStateRecord.LifecycleRunning));
            Assert.That(promoted.InstanceToken, Is.EqualTo(Win01.Token));

            McpRunStateRecord running = harness.RunningRecord();
            Assert.That(McpOwnershipEvaluator.EvaluateStop(running, harness.Observe(running)).Allowed, Is.True);
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(running, harness.Observe(running), out string token, out _, out _),
                Is.True);
            Assert.That(token, Is.EqualTo(Win01.Token));
            Assert.That(gate.Calls, Is.EqualTo(1), "one query served every decision");
        }

        private static IEnumerable<TestCaseData> FailedQueries()
        {
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = false }).SetName("cannot_start");
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = true, TimedOut = true }).SetName("timeout");
            yield return new TestCaseData(new WindowsCommandRunOutcome { Started = true, Cancelled = true }).SetName("cancelled");
            yield return new TestCaseData(Win01.Raw("NOTFOUND")).SetName("process_not_found");
            yield return new TestCaseData(Win01.Raw("NULL")).SetName("command_line_unavailable");
            yield return new TestCaseData(Win01.Raw("OK:@@@@")).SetName("malformed_payload");
            yield return new TestCaseData(Win01.Raw(Win01.OkLine("x"), exit: 5)).SetName("non_zero_exit");
            yield return new TestCaseData(Win01.Raw("UNTRUSTED:module")).SetName("untrusted_provider");
            yield return new TestCaseData(
                new WindowsCommandRunOutcome { Started = true, ExitCode = 0, StandardOutput = Win01.OkLine("tool.exe"), OutputComplete = false })
                .SetName("valid_output_but_incomplete_read");
        }

        [TestCaseSource(nameof(FailedQueries))]
        public void AFailedQuery_RemainsFailClosed_AndIsNotHammered(WindowsCommandRunOutcome outcome)
        {
            var runner = new ScriptedRunner(outcome);
            var harness = new ChainHarness((pid, token) => WindowsProcessCommandLine.Query(
                pid, runner, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));
            McpRunStateRecord pending = harness.PendingRecord();
            McpRunStateRecord running = harness.RunningRecord();

            harness.Observe(pending);
            harness.Settle();

            for (int i = 0; i < 25; i++)
            {
                McpOwnershipObservation observation = harness.Observe(pending);
                Assert.That(observation.ServerCommandLineAvailable, Is.False);
                Assert.That(
                    McpOwnershipEvaluator.TryIdentifyPendingServer(pending, observation, out _, out _), Is.False);
                Assert.That(McpOwnershipEvaluator.EvaluateStop(running, harness.Observe(running)).Allowed, Is.False);
            }

            Assert.That(runner.Calls, Is.EqualTo(1), "a failure is remembered briefly, not retried on every poll");
        }

        [Test]
        public void AFailedLookup_IsRetriedOnlyAfterTheCooldown_AndIdleSuccessExpires()
        {
            DateTime now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);

            int calls = 0;
            bool succeed = false;
            var observer = new ProcessCommandLineObserver(
                table.Get,
                (pid, token) =>
                {
                    Interlocked.Increment(ref calls);
                    return succeed
                        ? WindowsProcessCommandLine.ParseOutcome(Win01.Ok(GoodCommandLine))
                        : WindowsProcessCommandLine.ParseOutcome(Win01.Raw("NULL"));
                },
                clock: () => now);

            observer.Observe(Win01.ServerPid);
            Assert.That(observer.ObserveAsync(Win01.ServerPid, CancellationToken.None).GetAwaiter().GetResult().Status,
                Is.EqualTo(CommandLineObservationStatus.Failed));
            Assert.That(calls, Is.EqualTo(1));

            now = now.AddSeconds(1);
            Assert.That(observer.Observe(Win01.ServerPid).Status, Is.EqualTo(CommandLineObservationStatus.Failed));
            Assert.That(calls, Is.EqualTo(1), "inside the cooldown");

            succeed = true;
            now = now.AddSeconds(3);
            Assert.That(observer.Observe(Win01.ServerPid).IsPending, Is.True, "cooldown over: a new query starts");
            Assert.That(observer.ObserveAsync(Win01.ServerPid, CancellationToken.None).GetAwaiter().GetResult().IsReady, Is.True);
            Assert.That(calls, Is.EqualTo(2));

            now = now.AddHours(7);
            Assert.That(observer.Observe(Win01.ServerPid).IsReady, Is.True, "still within the idle window");
            Assert.That(calls, Is.EqualTo(2));

            now = now.AddHours(9);
            Assert.That(observer.Observe(Win01.ServerPid).IsPending, Is.True, "an idle observation expires and is re-verified");
            observer.ObserveAsync(Win01.ServerPid, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(calls, Is.EqualTo(3));
        }

        // ================================================================================
        // 3. Stale results can never validate a different server, PID, nonce or Editor lifetime
        // ================================================================================

        [Test]
        public void Cancellation_PreventsAStaleSuccess()
        {
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);

            // A misbehaving query that ignores cancellation and later reports success.
            var release = new ManualResetEventSlim(false);
            int calls = 0;
            var observer = new ProcessCommandLineObserver(
                table.Get,
                (pid, token) =>
                {
                    Interlocked.Increment(ref calls);
                    release.Wait(10000);
                    return WindowsProcessCommandLine.ParseOutcome(Win01.Ok(GoodCommandLine));
                });

            Assert.That(observer.Observe(Win01.ServerPid).IsPending, Is.True);
            Task<CommandLineObservation> waiter = observer.ObserveAsync(Win01.ServerPid, CancellationToken.None);
            Assert.That(Timing.WaitUntil(() => calls == 1), Is.True);

            observer.Reset(); // the Editor lifetime/reload ended; everything in flight is void

            Assert.That(waiter.Wait(2000), Is.True, "the waiter is released immediately by the reset");
            Assert.That(waiter.Result.Status, Is.EqualTo(CommandLineObservationStatus.Failed));
            Assert.That(waiter.Result.Failure, Is.EqualTo(WindowsCommandLineStatus.Cancelled));

            release.Set(); // the stale "success" arrives late
            Assert.That(Timing.WaitUntil(() => observer.Outstanding == 0), Is.True);

            CommandLineObservation after = observer.Observe(Win01.ServerPid);
            Assert.That(after.IsReady, Is.False, "a result from before the reset is never served");
            Assert.That(after.IsPending, Is.True, "a fresh query is needed");
            Assert.That(observer.ObserveAsync(Win01.ServerPid, CancellationToken.None).GetAwaiter().GetResult().IsReady, Is.True);
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void Reset_CancelsTheRunningQuery_SoNoChildOutlivesTheEditorLifetime()
        {
            var gate = new GatedQuery();
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);
            var observer = new ProcessCommandLineObserver(table.Get, gate.Invoke);

            observer.Observe(Win01.ServerPid);
            Assert.That(gate.WaitUntilEntered(), Is.True);

            observer.Reset();

            Assert.That(Timing.WaitUntil(() => gate.CancelledCalls == 1), Is.True, "the query's token was cancelled");
            Assert.That(Timing.WaitUntil(() => observer.Outstanding == 0), Is.True);
        }

        [Test]
        public void PidReuse_InvalidatesADelayedResult()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord pending = harness.PendingRecord();
            McpRunStateRecord running = harness.RunningRecord();

            harness.Observe(pending);
            Task<CommandLineObservation> waiter = harness.Observer.ObserveAsync(Win01.ServerPid, CancellationToken.None);
            Assert.That(gate.WaitUntilEntered(), Is.True);

            // While the query runs, the server exits and a DIFFERENT process takes over the PID.
            string oldProcessLine = GoodCommandLine;
            harness.ServerStart = Win01.ServerStart.AddSeconds(41);
            gate.ReleaseWith(oldProcessLine);
            Assert.That(Timing.WaitUntil(() => harness.Observer.Outstanding == 0), Is.True);

            // Whoever was already waiting on the old lifetime's query is told it failed, not handed the line.
            Assert.That(waiter.Wait(5000), Is.True);
            Assert.That(waiter.Result.IsReady, Is.False);
            Assert.That(waiter.Result.CommandLine, Is.Null);

            // The delayed line described the old process; it must not be attributed to the new one.
            Assert.That(harness.Observe(running).ServerCommandLineAvailable, Is.False);

            // The new lifetime needed (and got) its own query ...
            harness.Settle();
            Assert.That(gate.Calls, Is.EqualTo(2), "the recycled PID was queried afresh, not served from the old result");

            // ... and even when that succeeds, the recorded old lifetime no longer matches.
            McpOwnershipObservation fresh = harness.Observe(running);
            Assert.That(McpOwnershipEvaluator.EvaluateStop(running, fresh).Allowed, Is.False);
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(running, fresh).Reason,
                Is.EqualTo(McpOwnershipDenyReason.PidReuse));
        }

        [Test]
        public void ARecycledPid_NeverSeesTheOldProcessesObservation()
        {
            var table = new LifetimeTable();
            DateTime first = Win01.ServerStart;
            DateTime second = first.AddMinutes(7);
            table.Set(Win01.ServerPid, first);

            string current = "first-process.exe --a";
            var observer = new ProcessCommandLineObserver(
                table.Get,
                (pid, token) => WindowsProcessCommandLine.ParseOutcome(Win01.Ok(current)));

            Assert.That(
                observer.ObserveAsync(Win01.ServerPid, CancellationToken.None).GetAwaiter().GetResult().CommandLine,
                Is.EqualTo("first-process.exe --a"));
            Assert.That(observer.Observe(Win01.ServerPid).IsReady, Is.True);

            current = "second-process.exe --b";
            table.Set(Win01.ServerPid, second); // same PID, new process

            CommandLineObservation seen = observer.Observe(Win01.ServerPid);
            Assert.That(seen.IsReady, Is.False, "the cached line belongs to the previous lifetime");
            Assert.That(
                observer.ObserveAsync(Win01.ServerPid, CancellationToken.None).GetAwaiter().GetResult().CommandLine,
                Is.EqualTo("second-process.exe --b"));
        }

        [Test]
        public void ALifetimeThatChangesBetweenTheTwoReadsOfTheBuilder_WithholdsTheCommandLine()
        {
            // The builder reads the lifetime, then the command line, then the lifetime again. If a
            // different process took the PID in between, the line is withheld.
            var harness = ChainHarness.Returning(GoodCommandLine);
            McpRunStateRecord running = harness.RunningRecord();
            harness.Observe(running);
            harness.Settle();
            Assert.That(harness.Observe(running).ServerCommandLineAvailable, Is.True, "control: coherent world");

            var flapping = new FlappingInspector(harness);
            McpOwnershipObservation observation = McpRouteObservationBuilder.Build(
                harness,
                flapping,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = harness.Root,
                    Endpoint = Win01.Endpoint,
                    CurrentEditorPid = harness.GetCurrentProcessId(),
                    RecordedServerPid = Win01.ServerPid,
                    RecordedEditorPid = harness.EditorPid,
                });

            Assert.That(observation.ServerCommandLineAvailable, Is.False);
            Assert.That(observation.ServerCommandLine, Is.Null);
            Assert.That(McpOwnershipEvaluator.EvaluateStop(running, observation).Allowed, Is.False);
        }

        /// <summary>Reports a different creation instant for the server on every second read.</summary>
        private sealed class FlappingInspector : IMcpProcessInspector
        {
            private readonly ChainHarness _inner;
            private int _reads;

            public FlappingInspector(ChainHarness inner)
            {
                _inner = inner;
            }

            public int GetCurrentProcessId() => _inner.GetCurrentProcessId();
            public bool TryGetCurrentProcessStartTimeUtc(out DateTime startUtc) => _inner.TryGetCurrentProcessStartTimeUtc(out startUtc);

            public bool TryGetProcessStartTimeUtc(int pid, out DateTime startUtc)
            {
                bool ok = _inner.TryGetProcessStartTimeUtc(pid, out startUtc);
                if (pid == Win01.ServerPid && Interlocked.Increment(ref _reads) % 2 == 0)
                {
                    startUtc = startUtc.AddSeconds(17);
                }

                return ok;
            }

            public bool ProcessExists(int pid) => _inner.ProcessExists(pid);
            public bool TryObserveProcessExistence(int pid, out bool exists) => _inner.TryObserveProcessExistence(pid, out exists);
            public bool TryOpenRetainedProcess(int pid, out IRetainedProcessHandle handle) => _inner.TryOpenRetainedProcess(pid, out handle);
            public bool TryGetCommandLine(int pid, out string commandLine) => _inner.TryGetCommandLine(pid, out commandLine);
            public IReadOnlyList<int> GetListeningProcessIds(int port) => _inner.GetListeningProcessIds(port);
        }

        [Test]
        public void AReplacedNonce_InvalidatesADelayedResult()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord original = harness.PendingRecord(Win01.Token);

            harness.Observe(original);
            Assert.That(gate.WaitUntilEntered(), Is.True);

            // The record is replaced by a new launch (new nonce) while the old query is still running.
            McpRunStateRecord replacement = harness.PendingRecord(Win01.OtherToken);
            replacement.RecordId = "5b6c7d8e9f0a4b1c8d2e3f4a5b6c7d8e";

            // The delayed result still carries the ORIGINAL nonce.
            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();

            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(
                    replacement, harness.Observe(replacement), out McpRunStateRecord promoted, out string detail),
                Is.False);
            Assert.That(promoted, Is.Null);
            Assert.That(detail, Does.Contain("nonce"));

            McpRunStateRecord replacementRunning = harness.RunningRecord(Win01.OtherToken);
            Assert.That(
                McpOwnershipEvaluator.EvaluateStop(replacementRunning, harness.Observe(replacementRunning)).Allowed,
                Is.False);
            Assert.That(
                McpManagedConnectionGate.TryGetLaunchToken(
                    replacementRunning, harness.Observe(replacementRunning), out string token, out _, out _),
                Is.False);
            Assert.That(token, Is.Null);

            // Control: the result is faithful for the record it actually describes.
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(original, harness.Observe(original), out _, out _),
                Is.True);
        }

        [Test]
        public void AnEditorLifetimeChange_InvalidatesADelayedResult()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord running = harness.RunningRecord();

            harness.Observe(running);
            Assert.That(gate.WaitUntilEntered(), Is.True);

            // The Editor this record was written for is replaced by a new one (new creation instant).
            harness.EditorStart = Win01.EditorStart.AddMinutes(30);
            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();

            McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(running, harness.Observe(running));
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEditorLifetime));

            McpRunStateRecord pending = harness.PendingRecord();
            pending.EditorStartUtc = McpRunStateRecord.FormatUtc(Win01.EditorStart);
            Assert.That(
                McpOwnershipEvaluator.TryIdentifyPendingServer(pending, harness.Observe(pending), out _, out string detail),
                Is.False);
            Assert.That(detail, Does.Contain("editor lifetime"));
        }

        // ================================================================================
        // 4. No uncontrolled fan-out
        // ================================================================================

        [Test]
        public void ManyConcurrentCallsForOneProcess_ShareASingleQuery()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);

            Parallel.For(
                0,
                64,
                new ParallelOptions { MaxDegreeOfParallelism = 32 },
                worker =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        harness.Observer.Observe(Win01.ServerPid);
                        harness.TryGetCommandLine(Win01.ServerPid, out _);
                    }
                });

            Assert.That(gate.WaitUntilEntered(), Is.True);
            Assert.That(gate.Calls, Is.EqualTo(1));
            Assert.That(harness.Observer.QueriesStarted, Is.EqualTo(1));

            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();

            Parallel.For(0, 32, _ => { for (int i = 0; i < 100; i++) harness.Observer.Observe(Win01.ServerPid); });
            Assert.That(gate.Calls, Is.EqualTo(1), "a finished result is reused for the same lifetime");
        }

        [Test]
        public void ManyDistinctProcesses_AreBoundedAndRunOneAtATime()
        {
            var gate = new GatedQuery();
            var table = new LifetimeTable();
            for (int pid = 1000; pid < 1040; pid++)
            {
                table.Set(pid, Win01.ServerStart.AddSeconds(pid));
            }

            var observer = new ProcessCommandLineObserver(table.Get, gate.Invoke, maxOutstanding: 3);

            var statuses = new List<CommandLineObservation>();
            for (int pid = 1000; pid < 1040; pid++)
            {
                statuses.Add(observer.Observe(pid));
            }

            Assert.That(gate.WaitUntilEntered(), Is.True);
            Assert.That(observer.Outstanding, Is.EqualTo(3), "at most three lifetimes may be queued or running");
            Assert.That(statuses.Count(s => s.IsPending), Is.EqualTo(3));
            Assert.That(
                statuses.Count(s => s.Status == CommandLineObservationStatus.Failed),
                Is.EqualTo(37),
                "the rest fail fast instead of piling up");

            Assert.That(observer.PeakActiveQueries, Is.EqualTo(1));
            Assert.That(gate.Calls, Is.EqualTo(1), "only the head of the queue runs");

            gate.ReleaseWith("tool.exe --x");
            Assert.That(Timing.WaitUntil(() => observer.Outstanding == 0), Is.True);

            Assert.That(observer.QueriesStarted, Is.EqualTo(3));
            Assert.That(observer.PeakActiveQueries, Is.EqualTo(1), "never more than one child at any moment");
        }

        // ================================================================================
        // 5. Awaiting keeps the Editor thread free, and resumes where it started
        // ================================================================================

        [Test]
        public void AwaitingTheQuery_KeepsEditorTicksFlowing_AndResumesOnTheEditorThread()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);

            using (var editor = new FakeEditorThread())
            {
                Task<int> awaiting = editor.RunAsync(async () =>
                {
                    CommandLineObservation result =
                        await harness.Observer.ObserveAsync(Win01.ServerPid, CancellationToken.None);
                    Assert.That(result.IsReady, Is.True);
                    return Thread.CurrentThread.ManagedThreadId;
                });

                Assert.That(gate.WaitUntilEntered(), Is.True);

                // The query is held open for ~400 ms while "Editor ticks" keep arriving.
                List<double> latencies = editor.MeasureTickLatency(400, 10);
                gate.ReleaseWith(GoodCommandLine);

                Assert.That(awaiting.Wait(10000), Is.True);
                Assert.That(awaiting.Result, Is.EqualTo(editor.Thread.ManagedThreadId), "resumed on the Editor thread");

                TestContext.Out.WriteLine(
                    $"{latencies.Count} editor ticks during a 400 ms query; worst delay {latencies.Max():F2} ms");
                Assert.That(latencies.Count, Is.GreaterThan(20));
                Assert.That(latencies.Max(), Is.LessThan(150), "no tick waited for the query");
            }
        }

        [Test]
        public void Control_AnInlineBlockingQuery_WouldFreezeTheEditorThread()
        {
            // The R1 behaviour, kept as a control so the measurement above is known to detect a stall.
            var slow = new ScriptedRunner((exe, args, timeout) =>
            {
                Thread.Sleep(400);
                return Win01.Ok("tool.exe --x");
            });

            using (var editor = new FakeEditorThread())
            {
                Task<List<double>> ticks = Task.Run(() => editor.MeasureTickLatency(900, 10));
                Thread.Sleep(100);
                editor.Post(_ => WindowsProcessCommandLine.Query(1234, slow), null);

                List<double> latencies = ticks.Result;
                TestContext.Out.WriteLine($"control: worst editor tick delay with an inline query {latencies.Max():F1} ms");
                Assert.That(latencies.Max(), Is.GreaterThan(300), "an inline 400 ms query stalls the thread");
            }
        }

        [Test]
        public void ACancelledWait_DoesNotCancelTheSharedQuery_ForOtherWaiters()
        {
            var gate = new GatedQuery();
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);
            var observer = new ProcessCommandLineObserver(table.Get, gate.Invoke);

            using (var cts = new CancellationTokenSource())
            {
                Task<CommandLineObservation> impatient = observer.ObserveAsync(Win01.ServerPid, cts.Token);
                Task<CommandLineObservation> patient = observer.ObserveAsync(Win01.ServerPid, CancellationToken.None);
                Assert.That(gate.WaitUntilEntered(), Is.True);

                cts.Cancel();
                Assert.That(impatient.Wait(2000), Is.True);
                Assert.That(impatient.Result.Failure, Is.EqualTo(WindowsCommandLineStatus.Cancelled));
                Assert.That(gate.CancelledCalls, Is.EqualTo(0), "the query itself was not cancelled");

                gate.ReleaseWith("tool.exe --x");
                Assert.That(patient.Wait(5000), Is.True);
                Assert.That(patient.Result.IsReady, Is.True);
                Assert.That(gate.Calls, Is.EqualTo(1));
            }
        }

        // ================================================================================
        // 6. The managed pre-connect gate awaits the evidence instead of blocking on it
        // ================================================================================

        private sealed class HarnessAuthorizer : IMcpManagedConnectionAuthorizer, IMcpManagedEvidencePreparer
        {
            private readonly ChainHarness _harness;
            private readonly McpRunStateRecord _record;
            private readonly bool _prepares;

            public HarnessAuthorizer(ChainHarness harness, McpRunStateRecord record, bool prepares = true)
            {
                _harness = harness;
                _record = record;
                _prepares = prepares;
            }

            public int DecisionThread { get; private set; }

            public bool IsManagedRoute => true;

            public Task PrepareAsync(CancellationToken token)
                => _prepares
                    ? _harness.Detector.PrefetchProcessCommandLineAsync(Win01.ServerPid, token)
                    : Task.CompletedTask;

            public bool TryGetLaunchToken(out string instanceToken, out string reason)
            {
                DecisionThread = Thread.CurrentThread.ManagedThreadId;
                bool ok = McpManagedConnectionGate.TryGetLaunchToken(
                    _record, _harness.Observe(_record), out instanceToken, out McpOwnershipDenyReason deny, out string detail);
                reason = ok ? null : $"{deny}: {detail}";
                return ok;
            }
        }

        [Test]
        public void PreConnectGate_ReturnsPromptly_OpensNothingUntilEvidenceIsComplete_ThenAuthorizes()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            McpRunStateRecord running = harness.RunningRecord();
            var authorizer = new HarnessAuthorizer(harness, running);
            int opened = 0;

            using (var editor = new FakeEditorThread())
            {
                Task<McpConnectAttempt> attempt = null;
                double callMs = 0;
                editor.RunSync(() =>
                {
                    callMs = Timing.MillisecondsOf(() =>
                    {
                        attempt = McpManagedPreConnectGate.ConnectWithGateAsync(
                            authorizer,
                            _ => { Interlocked.Increment(ref opened); return Task.FromResult(true); },
                            null,
                            CancellationToken.None);
                    });
                });

                TestContext.Out.WriteLine($"ConnectWithGateAsync returned to the Editor thread in {callMs:F2} ms (query still pending)");
                Assert.That(callMs, Is.LessThan(CallbackBudgetMs));
                Assert.That(gate.WaitUntilEntered(), Is.True);
                Assert.That(attempt.IsCompleted, Is.False, "the attempt waits for the evidence");
                Thread.Sleep(150);
                Assert.That(opened, Is.EqualTo(0), "no socket while the proof is pending");

                gate.ReleaseWith(GoodCommandLine);
                Assert.That(attempt.Wait(10000), Is.True);
                Assert.That(attempt.Result.Authorized, Is.True);
                Assert.That(attempt.Result.Opened, Is.True);
                Assert.That(opened, Is.EqualTo(1));
                Assert.That(
                    authorizer.DecisionThread,
                    Is.EqualTo(editor.Thread.ManagedThreadId),
                    "the synchronous decision resumed in the caller's own context");
            }
        }

        [Test]
        public void PreConnectGate_NeverOpens_WhenTheEvidenceFailsOrIsNeverPrepared()
        {
            var failing = new GatedQuery();
            var harness = new ChainHarness(failing.Invoke);
            McpRunStateRecord running = harness.RunningRecord();
            int opened = 0;
            Func<CancellationToken, Task<bool>> opener = _ => { Interlocked.Increment(ref opened); return Task.FromResult(true); };

            failing.ReleaseFailure(new WindowsCommandRunOutcome { Started = true, TimedOut = true });
            McpConnectAttempt refused = McpManagedPreConnectGate
                .ConnectWithGateAsync(new HarnessAuthorizer(harness, running), opener, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.That(refused.Authorized, Is.False);
            Assert.That(refused.Reason, Does.Contain("without proven ownership"));

            // Without the preparing step the cold evidence is simply absent: still a refusal.
            var cold = new GatedQuery();
            var coldHarness = new ChainHarness(cold.Invoke);
            McpConnectAttempt unprepared = McpManagedPreConnectGate
                .ConnectWithGateAsync(
                    new HarnessAuthorizer(coldHarness, coldHarness.RunningRecord(), prepares: false),
                    opener,
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.That(unprepared.Authorized, Is.False);
            Assert.That(opened, Is.EqualTo(0));

            cold.ReleaseWith(GoodCommandLine);
            coldHarness.Settle();
        }

        [Test]
        public void PreConnectGate_CancellationWhilePreparing_RefusesWithoutOpening()
        {
            var gate = new GatedQuery();
            var harness = new ChainHarness(gate.Invoke);
            int opened = 0;

            using (var cts = new CancellationTokenSource())
            {
                Task<McpConnectAttempt> attempt = McpManagedPreConnectGate.ConnectWithGateAsync(
                    new HarnessAuthorizer(harness, harness.RunningRecord()),
                    _ => { Interlocked.Increment(ref opened); return Task.FromResult(true); },
                    null,
                    cts.Token);
                Assert.That(gate.WaitUntilEntered(), Is.True);

                cts.Cancel();
                Assert.That(attempt.Wait(5000), Is.True);
                Assert.That(attempt.Result.Authorized, Is.False);
                Assert.That(opened, Is.EqualTo(0));
            }

            gate.ReleaseWith(GoodCommandLine);
            harness.Settle();
        }
    }
}
