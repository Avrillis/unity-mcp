using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// Measurements for R1-01. They run the REAL PowerShell/CIM query on this host and report how long an
    /// Editor-facing callback takes next to how long the verification itself takes, how many query
    /// children ever run at once, and the process overhead. They assert only generous bounds; the numbers
    /// are written to the test output (and appended to $MCP_WIN01_PERF_OUT when that is set).
    ///
    /// These are .NET host timings on a development machine. They are NOT Unity Editor frame timings: no
    /// Unity Editor was launched.
    /// </summary>
    [TestFixture]
    [Platform("Win")]
    [Category("Performance")]
    [NonParallelizable]
    public class McpWin01PerformanceTests
    {
        private static int OwnPid => Process.GetCurrentProcess().Id;

        private static void Record(string name, string values)
        {
            string line = name + ": " + values;
            TestContext.Out.WriteLine(line);
            string path = Environment.GetEnvironmentVariable("MCP_WIN01_PERF_OUT");
            if (!string.IsNullOrEmpty(path))
            {
                try { File.AppendAllText(path, line + Environment.NewLine); } catch { /* measurement aid only */ }
            }
        }

        private static string Stats(IEnumerable<double> values)
        {
            double[] v = values.ToArray();
            return v.Length == 0
                ? "n=0"
                : $"n={v.Length} min={v.Min():F3} mean={v.Average():F3} p50={Timing.Percentile(v, 0.5):F3} "
                  + $"p95={Timing.Percentile(v, 0.95):F3} p99={Timing.Percentile(v, 0.99):F3} max={v.Max():F3} (ms)";
        }

        private static ProcessCommandLineObserver RealObserver()
            => new ProcessCommandLineObserver(
                ProcessCommandLineObserver.ReadProcessCreationUtc,
                (pid, token) => WindowsProcessCommandLine.Query(
                    pid, null, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));

        [Test]
        public void RealCimLookup_EditorCallbackLatencyVersusEndToEndVerificationTime()
        {
            const int Runs = 10;
            var endToEnd = new List<double>();
            var callbacks = new List<double>();

            for (int run = 0; run < Runs; run++)
            {
                ProcessCommandLineObserver observer = RealObserver();
                var clock = Stopwatch.StartNew();

                CommandLineObservation seen = null;
                while (true)
                {
                    double ms = Timing.MillisecondsOf(() => seen = observer.Observe(OwnPid));
                    callbacks.Add(ms);
                    if (!seen.IsPending)
                    {
                        break;
                    }

                    Thread.Sleep(1);
                    Assert.That(clock.ElapsedMilliseconds, Is.LessThan(30000), "the query must finish");
                }

                endToEnd.Add(clock.Elapsed.TotalMilliseconds);
                Assert.That(seen.IsReady, Is.True, seen.Detail);
            }

            Record("real_cim.end_to_end_verification", Stats(endToEnd));
            Record("real_cim.editor_callback_latency", Stats(callbacks));

            Assert.That(Timing.Percentile(callbacks, 0.99), Is.LessThan(50), "Editor-facing callback p99");
            Assert.That(endToEnd.Max(), Is.LessThan(WindowsProcessCommandLine.DefaultTimeoutMs));
        }

        [Test]
        public void SlowQuery_650ms_CallbacksStayFast_WhileVerificationTakesItsTime()
        {
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);
            var runner = new ScriptedRunner((exe, args, timeoutMs) =>
            {
                Thread.Sleep(650);
                return Win01.Ok("tool.exe --x");
            });
            var observer = new ProcessCommandLineObserver(
                table.Get,
                (pid, token) => WindowsProcessCommandLine.Query(pid, runner, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));

            var callbacks = new List<double>();
            var clock = Stopwatch.StartNew();
            CommandLineObservation seen;
            do
            {
                seen = null;
                callbacks.Add(Timing.MillisecondsOf(() => seen = observer.Observe(Win01.ServerPid)));
                Thread.Sleep(1);
            }
            while (seen.IsPending);
            double total = clock.Elapsed.TotalMilliseconds;

            Record("scripted_650ms.end_to_end_verification", $"{total:F1} ms");
            Record("scripted_650ms.editor_callback_latency", Stats(callbacks));

            Assert.That(total, Is.GreaterThan(600));
            Assert.That(Timing.Percentile(callbacks, 0.99), Is.LessThan(50));
            Assert.That(seen.IsReady, Is.True);
        }

        [Test]
        public void ManyDistinctLookups_NeverRunMoreThanOneRealChildAtOnce_AndLeaveNoOverheadBehind()
        {
            const int Pids = 12;
            var table = new LifetimeTable();
            for (int i = 0; i < Pids; i++)
            {
                table.Set(5000 + i, Win01.ServerStart.AddSeconds(i));
            }

            int active = 0;
            int peak = 0;
            int children = 0;
            var observer = new ProcessCommandLineObserver(
                table.Get,
                (pid, token) =>
                {
                    int now = Interlocked.Increment(ref active);
                    int seenPeak;
                    while (now > (seenPeak = Volatile.Read(ref peak))
                           && Interlocked.CompareExchange(ref peak, now, seenPeak) != seenPeak)
                    {
                    }

                    Interlocked.Increment(ref children);
                    try
                    {
                        // A genuine hardened PowerShell/CIM child per query.
                        return WindowsProcessCommandLine.Query(
                            OwnPid, null, null, WindowsProcessCommandLine.DefaultTimeoutMs, token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                },
                maxOutstanding: 3);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Process self = Process.GetCurrentProcess();
            self.Refresh();
            long workingSetBefore = self.WorkingSet64;
            int threadsBefore = self.Threads.Count;
            int powershellBefore = Process.GetProcessesByName("powershell").Length;

            // 400 callbacks across 12 distinct lifetimes, from 8 threads, while the queries run.
            var callbackMs = new System.Collections.Concurrent.ConcurrentBag<double>();
            int peakPowerShell = powershellBefore;
            using (var sampling = new CancellationTokenSource())
            {
                Task sampler = Task.Run(() =>
                {
                    while (!sampling.IsCancellationRequested)
                    {
                        int count = Process.GetProcessesByName("powershell").Length;
                        int seenMax;
                        while (count > (seenMax = Volatile.Read(ref peakPowerShell))
                               && Interlocked.CompareExchange(ref peakPowerShell, count, seenMax) != seenMax)
                        {
                        }

                        Thread.Sleep(20);
                    }
                });

                var clock = Stopwatch.StartNew();
                Parallel.For(
                    0,
                    8,
                    new ParallelOptions { MaxDegreeOfParallelism = 8 },
                    worker =>
                    {
                        for (int i = 0; i < 50; i++)
                        {
                            int pid = 5000 + ((worker + i) % Pids);
                            callbackMs.Add(Timing.MillisecondsOf(() => observer.Observe(pid)));
                            Thread.Sleep(2);
                        }
                    });

                Assert.That(Timing.WaitUntil(() => observer.Outstanding == 0, 60000), Is.True, "all queued queries finish");
                double drained = clock.Elapsed.TotalMilliseconds;
                sampling.Cancel();
                sampler.Wait(5000);

                Thread.Sleep(800); // let pool threads retire
                GC.Collect();
                GC.WaitForPendingFinalizers();
                self.Refresh();
                long workingSetAfter = self.WorkingSet64;
                int threadsAfter = self.Threads.Count;

                Record("burst.callbacks", Stats(callbackMs));
                Record(
                    "burst.queries",
                    $"lookups_requested=400 distinct_lifetimes={Pids} queries_started={observer.QueriesStarted} "
                    + $"children_launched={children} peak_concurrent_queries={peak} drained_in={drained:F0} ms");
                Record("burst.powershell_processes", $"baseline={powershellBefore} peak_sampled={peakPowerShell} after={Process.GetProcessesByName("powershell").Length}");
                Record(
                    "burst.process_overhead",
                    $"working_set_before={workingSetBefore / 1048576.0:F1} MiB after={workingSetAfter / 1048576.0:F1} MiB "
                    + $"threads_before={threadsBefore} threads_after={threadsAfter}");

                Assert.That(peak, Is.EqualTo(1), "never more than one query child at any moment");
                Assert.That(observer.QueriesStarted, Is.LessThanOrEqualTo(Pids), "at most one query per distinct lifetime");
                Assert.That(children, Is.EqualTo(observer.QueriesStarted), "one child per query");
                Assert.That(Timing.Percentile(callbackMs, 0.99), Is.LessThan(50));
                Assert.That(threadsAfter - threadsBefore, Is.LessThanOrEqualTo(12), "no thread accumulation");
            }
        }

        [Test]
        public void CallbackThroughput_WhileAQueryIsPending()
        {
            var gate = new GatedQuery();
            var table = new LifetimeTable();
            table.Set(Win01.ServerPid, Win01.ServerStart);
            var observer = new ProcessCommandLineObserver(table.Get, gate.Invoke);

            observer.Observe(Win01.ServerPid);
            Assert.That(gate.WaitUntilEntered(), Is.True);

            const int Calls = 200000;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < Calls; i++)
            {
                observer.Observe(Win01.ServerPid);
            }

            double perCallUs = clock.Elapsed.TotalMilliseconds * 1000.0 / Calls;
            Record("pending_poll.cost", $"{perCallUs:F2} us per Observe over {Calls} calls; queries started={observer.QueriesStarted}");

            gate.ReleaseWith("tool.exe --x");
            Assert.That(Timing.WaitUntil(() => observer.Outstanding == 0), Is.True);
            Assert.That(gate.Calls, Is.EqualTo(1));
        }
    }
}
