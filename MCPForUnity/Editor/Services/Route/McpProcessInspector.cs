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
