using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Route;
using MCPForUnity.Editor.Services.Server;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// A query that stays in flight until the test releases it, honouring cancellation exactly as
    /// the real runner does. Lets a test hold a "slow CIM lookup" open and decide how it ends.
    /// </summary>
    internal sealed class GatedQuery
    {
        private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private volatile WindowsCommandLineResult _result;
        private int _calls;
        private int _cancelled;

        public int Calls => Volatile.Read(ref _calls);

        public int CancelledCalls => Volatile.Read(ref _cancelled);

        public bool WaitUntilEntered(int timeoutMs = 5000) => _entered.Wait(timeoutMs);

        public WindowsCommandLineResult Invoke(int pid, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            _entered.Set();

            int signalled = WaitHandle.WaitAny(new[] { _release.WaitHandle, token.WaitHandle });
            if (signalled == 1)
            {
                Interlocked.Increment(ref _cancelled);
                return WindowsProcessCommandLine.ParseOutcome(new WindowsCommandRunOutcome { Started = true, Cancelled = true });
            }

            return _result;
        }

        /// <summary>Lets the held query finish successfully with this command line.</summary>
        public void ReleaseWith(string commandLine)
        {
            _result = WindowsProcessCommandLine.ParseOutcome(Win01.Ok(commandLine));
            _release.Set();
        }

        /// <summary>Lets the held query finish with a failed outcome.</summary>
        public void ReleaseFailure(WindowsCommandRunOutcome outcome)
        {
            _result = WindowsProcessCommandLine.ParseOutcome(outcome);
            _release.Set();
        }
    }

    /// <summary>A mutable table of "running processes" and their creation instants.</summary>
    internal sealed class LifetimeTable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<int, DateTime> _lifetimes = new Dictionary<int, DateTime>();

        public void Set(int pid, DateTime creation)
        {
            lock (_gate) { _lifetimes[pid] = creation; }
        }

        public void Remove(int pid)
        {
            lock (_gate) { _lifetimes.Remove(pid); }
        }

        public DateTime? Get(int pid)
        {
            lock (_gate) { return _lifetimes.TryGetValue(pid, out DateTime value) ? value : (DateTime?)null; }
        }
    }

    /// <summary>
    /// A single-threaded message loop standing in for the Unity Editor main thread: work posted to
    /// it runs one item at a time, so anything that blocks it delays every later item.
    /// </summary>
    internal sealed class FakeEditorThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> _queue =
            new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();

        public FakeEditorThread()
        {
            Thread = new Thread(Loop) { IsBackground = true, Name = "fake-editor-main" };
            Thread.Start();
        }

        public Thread Thread { get; }

        public override void Post(SendOrPostCallback d, object state)
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state));
            }
        }

        /// <summary>Runs an async body as if started from an Editor callback; the task ends when the body ends.</summary>
        public Task<T> RunAsync<T>(Func<Task<T>> body)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                async _ =>
                {
                    try { completion.SetResult(await body()); }
                    catch (Exception ex) { completion.SetException(ex); }
                },
                null);
            return completion.Task;
        }

        /// <summary>Runs a synchronous body on the editor thread and returns when it finished.</summary>
        public void RunSync(Action body)
        {
            var done = new ManualResetEventSlim(false);
            Post(_ => { try { body(); } finally { done.Set(); } }, null);
            done.Wait();
        }

        /// <summary>
        /// Posts a tick every <paramref name="intervalMs"/> for <paramref name="durationMs"/> and returns how
        /// late each one ran (post time to run time). A blocked editor thread shows up as large values.
        /// </summary>
        public List<double> MeasureTickLatency(int durationMs, int intervalMs)
        {
            var latencies = new List<double>();
            var finished = new CountdownEvent(1);
            var sw = Stopwatch.StartNew();
            int posted = 0;
            while (sw.ElapsedMilliseconds < durationMs)
            {
                long postedAt = sw.ElapsedTicks;
                Interlocked.Increment(ref posted);
                finished.AddCount();
                Post(
                    _ =>
                    {
                        double late = (sw.ElapsedTicks - postedAt) * 1000.0 / Stopwatch.Frequency;
                        lock (latencies) { latencies.Add(late); }
                        finished.Signal();
                    },
                    null);
                Thread.Sleep(intervalMs);
            }

            finished.Signal();
            finished.Wait(30000);
            lock (latencies) { return new List<double>(latencies); }
        }

        private void Loop()
        {
            SetSynchronizationContext(this);
            foreach (KeyValuePair<SendOrPostCallback, object> item in _queue.GetConsumingEnumerable())
            {
                item.Key(item.Value);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }

    /// <summary>Timing helpers.</summary>
    internal static class Timing
    {
        public static double MillisecondsOf(Action action)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        public static double Percentile(IEnumerable<double> values, double fraction)
        {
            double[] sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0)
            {
                return 0;
            }

            int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, index))];
        }

        /// <summary>Polls until the condition holds or the budget ends; true when it held.</summary>
        public static bool WaitUntil(Func<bool> condition, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            return condition();
        }
    }

    /// <summary>
    /// A complete scripted world for the ownership chain: process lifetimes, listeners, the PID file,
    /// and a REAL observer + detector + inspector for the command line. Only the OS is faked; every
    /// production decision (builder, evaluator, gate) runs for real.
    /// </summary>
    internal sealed class ChainHarness : IMcpProcessInspector, IMcpRouteStateStore
    {
        public int EditorPid = Win01.EditorPid;
        public DateTime EditorStart = Win01.EditorStart;
        public DateTime ServerStart = Win01.ServerStart;
        public bool ServerExists = true;
        public List<int> Listeners = new List<int> { Win01.ServerPid };
        public string PidFileText = Win01.ServerPid.ToString();
        public string Root = McpRunStatePaths.Canonicalize(Win01.Worker04Root);

        private readonly McpProcessInspector _commands;

        public ChainHarness(Func<int, CancellationToken, WindowsCommandLineResult> query)
        {
            Observer = new ProcessCommandLineObserver(LifetimeOf, query);
            Detector = new ProcessDetector(Observer);
            _commands = new McpProcessInspector(Detector);
        }

        public ProcessCommandLineObserver Observer { get; }

        public ProcessDetector Detector { get; }

        public static ChainHarness Returning(string commandLine)
            => new ChainHarness((pid, token) => WindowsProcessCommandLine.Query(
                pid, new ScriptedRunner(Win01.Ok(commandLine)), null, WindowsProcessCommandLine.DefaultTimeoutMs, token));

        public DateTime? LifetimeOf(int pid)
        {
            if (pid == EditorPid)
            {
                return EditorStart;
            }

            return ServerExists ? ServerStart : (DateTime?)null;
        }

        /// <summary>Waits (without blocking a thread) until the server's command line query is final.</summary>
        public void Settle() => Win01.Prime(Detector, Win01.ServerPid);

        public McpRunStateRecord PendingRecord(string token = Win01.Token, string endpoint = Win01.Endpoint)
        {
            return new McpRunStateRecord
            {
                SchemaVersion = McpRunStateRecord.CurrentSchemaVersion,
                RecordId = "0a0e121a6eb34d838867e4d547b1f2bb",
                CanonicalProjectRoot = Root,
                Endpoint = endpoint,
                EditorPid = EditorPid,
                EditorStartUtc = McpRunStateRecord.FormatUtc(EditorStart),
                ServerPid = 0,
                ServerStartUtc = null,
                InstanceToken = token,
                PidFilePath = McpRunStatePaths.GetPidFilePath(Root, Win01.Port),
                LifecycleState = McpRunStateRecord.LifecycleStarting,
                WrittenUtc = McpRunStateRecord.FormatUtc(Win01.RecordWritten),
            };
        }

        public McpRunStateRecord RunningRecord(string token = Win01.Token, string endpoint = Win01.Endpoint)
        {
            McpRunStateRecord record = PendingRecord(token, endpoint);
            record.LifecycleState = McpRunStateRecord.LifecycleRunning;
            record.ServerPid = Win01.ServerPid;
            record.ServerStartUtc = McpRunStateRecord.FormatUtc(Win01.ServerStart);
            return record;
        }

        /// <summary>One live observation, built by the production builder.</summary>
        public McpOwnershipObservation Observe(McpRunStateRecord record, string endpoint = Win01.Endpoint)
        {
            McpOwnershipObservation observation = McpRouteObservationBuilder.Build(
                this,
                this,
                new McpObservationRequest
                {
                    CanonicalProjectRoot = Root,
                    Endpoint = endpoint,
                    CurrentEditorPid = GetCurrentProcessId(),
                    RecordedServerPid = record != null && record.ServerPid > 0 ? record.ServerPid : 0,
                    RecordedEditorPid = record != null ? record.EditorPid : 0,
                });

            // Exactly what ServerManagementService.TryPromotePendingRecord does for a pending record.
            observation.ServerPid = observation.PidFilePid;
            return observation;
        }

        // ---- IMcpProcessInspector ---------------------------------------------------------
        public int GetCurrentProcessId() => EditorPid;

        public bool TryGetCurrentProcessStartTimeUtc(out DateTime startUtc)
        {
            startUtc = EditorStart;
            return true;
        }

        public bool TryGetProcessStartTimeUtc(int pid, out DateTime startUtc)
        {
            DateTime? life = LifetimeOf(pid);
            startUtc = life ?? default;
            return life.HasValue;
        }

        public bool ProcessExists(int pid) => pid == EditorPid || ServerExists;

        public bool TryObserveProcessExistence(int pid, out bool exists)
        {
            exists = pid == EditorPid || ServerExists;
            return true;
        }

        public bool TryOpenRetainedProcess(int pid, out IRetainedProcessHandle handle)
        {
            handle = null;
            return false;
        }

        public bool TryGetCommandLine(int pid, out string commandLine) => _commands.TryGetCommandLine(pid, out commandLine);

        public IReadOnlyList<int> GetListeningProcessIds(int port) => Listeners;

        // ---- IMcpRouteStateStore ----------------------------------------------------------
        public string GetCanonicalProjectRoot() => Root;

        public string GetRunStateDirectory() => McpRunStatePaths.GetRunStateDirectory(Root);

        public string GetHandshakePath() => McpRunStatePaths.GetHandshakePath(Root);

        public string GetPidFilePath(int port) => McpRunStatePaths.GetPidFilePath(Root, port);

        public bool TryRead(out McpRunStateRecord record, out string error)
        {
            record = null;
            error = "n/a";
            return false;
        }

        public McpOwnershipSnapshot ReadOwnershipState() => throw new NotSupportedException();

        public IMcpOwnershipLockProvider OwnershipLocks => throw new NotSupportedException();

        public bool Write(McpRunStateRecord record, out string error)
        {
            error = "n/a";
            return false;
        }

        public bool TryReadPidFile(string pidFilePath, out int pid, out bool fileExists)
        {
            fileExists = PidFileText != null;
            pid = 0;
            return fileExists && int.TryParse(PidFileText, out pid) && pid > 0;
        }

        public void DeleteRecord()
        {
        }
    }
}
