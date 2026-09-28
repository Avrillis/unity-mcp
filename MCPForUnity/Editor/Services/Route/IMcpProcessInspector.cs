using System;
using System.Collections.Generic;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Read-only process observations used to corroborate an ownership record.
    /// Every method is best-effort and must never throw.
    /// </summary>
    public interface IMcpProcessInspector
    {
        /// <summary>PID of the running Unity Editor.</summary>
        int GetCurrentProcessId();

        /// <summary>Creation time of the running Unity Editor, in UTC.</summary>
        bool TryGetCurrentProcessStartTimeUtc(out DateTime startUtc);

        /// <summary>Creates the observation for the PID evidence gathered elsewhere.</summary>
        bool TryGetProcessStartTimeUtc(int pid, out DateTime startUtc);

        /// <summary>True when a process with this PID exists.</summary>
        bool ProcessExists(int pid);

        /// <summary>
        /// Distinguishes "the process is gone" from "the check could not be completed".
        /// Returns false when the answer is unknown, so a failed query is never mistaken for
        /// evidence that a process no longer exists.
        /// </summary>
        bool TryObserveProcessExistence(int pid, out bool exists);

        /// <summary>
        /// Opens a retained handle to one specific process lifetime.
        ///
        /// The handle is bound to the kernel process object rather than to the numeric PID, so
        /// it cannot be redirected onto a different process that later reuses the same PID.
        /// </summary>
        bool TryOpenRetainedProcess(int pid, out IRetainedProcessHandle handle);

        /// <summary>Command line of the process, or false when it cannot be read.</summary>
        bool TryGetCommandLine(int pid, out string commandLine);

        /// <summary>PIDs currently listening on a TCP port.</summary>
        IReadOnlyList<int> GetListeningProcessIds(int port);
    }
}
