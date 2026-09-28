using System;
using System.Collections.Generic;
using System.Diagnostics;
using MCPForUnity.Editor.Services.Server;

namespace MCPForUnity.Editor.Services.Route
{
    /// <inheritdoc/>
    public sealed class McpProcessInspector : IMcpProcessInspector
    {
        private readonly IProcessDetector _processDetector;

        public McpProcessInspector() : this(null)
        {
        }

        public McpProcessInspector(IProcessDetector processDetector)
        {
            _processDetector = processDetector ?? new ProcessDetector();
        }

        /// <inheritdoc/>
        public int GetCurrentProcessId()
        {
            try
            {
                return _processDetector.GetCurrentProcessId();
            }
            catch
            {
                return -1;
            }
        }

        /// <inheritdoc/>
        public bool TryGetCurrentProcessStartTimeUtc(out DateTime startUtc)
            => TryGetProcessStartTimeUtc(GetCurrentProcessId(), out startUtc);

        /// <inheritdoc/>
        public bool TryGetProcessStartTimeUtc(int pid, out DateTime startUtc)
        {
            startUtc = default;
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                startUtc = process.StartTime.ToUniversalTime();
                return true;
            }
            catch
            {
                // The process exited, is inaccessible, or the platform refuses StartTime.
                return false;
            }
        }

        /// <inheritdoc/>
        public bool ProcessExists(int pid)
        {
            try
            {
                return _processDetector.ProcessExists(pid);
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryObserveProcessExistence(int pid, out bool exists)
        {
            exists = false;
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                exists = true;
                return true;
            }
            catch (ArgumentException)
            {
                // Definitively "no such process": the OS no longer has this PID.
                exists = false;
                return true;
            }
            catch
            {
                // Permission or platform refusal: the answer is unknown, not "gone".
                exists = false;
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryOpenRetainedProcess(int pid, out IRetainedProcessHandle handle)
        {
            handle = null;
            if (pid <= 0)
            {
                return false;
            }

            Process process = null;
            try
            {
                process = Process.GetProcessById(pid);

                // Reading StartTime caches the creation instant and keeps the process object
                // alive for the whole termination sequence. The retained object is what the
                // termination step re-reads immediately before killing, so a PID that has been
                // recycled in the meantime is detected instead of being killed.
                _ = process.StartTime;

                handle = new RetainedProcessHandle(process);
                process = null;
                return true;
            }
            catch
            {
                process?.Dispose();
                return false;
            }
        }

        private sealed class RetainedProcessHandle : IRetainedProcessHandle
        {
            private readonly Process _process;
            private bool _disposed;

            public RetainedProcessHandle(Process process)
            {
                _process = process;
            }

            public bool TryGetStartTimeUtc(out DateTime startUtc)
            {
                startUtc = default;
                if (_disposed)
                {
                    return false;
                }

                try
                {
                    startUtc = _process.StartTime.ToUniversalTime();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public bool Kill()
            {
                if (_disposed)
                {
                    return false;
                }

                try
                {
                    _process.Kill();
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    Dispose();
                }
            }

            private void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _process.Dispose();
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        /// <inheritdoc/>
        public bool TryGetCommandLine(int pid, out string commandLine)
        {
            commandLine = null;
            try
            {
                if (!_processDetector.TryGetProcessCommandLine(pid, out string normalized) || string.IsNullOrEmpty(normalized))
                {
                    return false;
                }

                commandLine = normalized;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<int> GetListeningProcessIds(int port)
        {
            try
            {
                return _processDetector.GetListeningProcessIdsForPort(port) ?? new List<int>();
            }
            catch
            {
                return new List<int>();
            }
        }
    }
}
