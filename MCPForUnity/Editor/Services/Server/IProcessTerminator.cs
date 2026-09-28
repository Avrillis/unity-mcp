using System;
using MCPForUnity.Editor.Services.Route;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Interface for platform-specific process termination.
    /// Provides methods to terminate processes gracefully or forcefully.
    /// </summary>
    public interface IProcessTerminator
    {
        /// <summary>
        /// Terminates a process using platform-appropriate methods.
        /// On Unix: Tries SIGTERM first with grace period, then SIGKILL.
        /// On Windows: Tries taskkill, then taskkill /F.
        /// </summary>
        /// <param name="pid">The process ID to terminate</param>
        /// <returns>True if the process was terminated successfully</returns>
        bool Terminate(int pid);

        /// <summary>
        /// Terminates a process that was already validated as this editor's managed server.
        ///
        /// Unlike <see cref="Terminate(int)"/> this never resolves a PID again: it acts on the
        /// retained process identity the ownership evaluation was performed against, and re-checks
        /// the process creation instant immediately before the kill. A process that exited (and
        /// whose PID was reused in the meantime) fails this check and is left untouched.
        /// </summary>
        /// <param name="handle">Retained handle to the validated process lifetime</param>
        /// <param name="validatedStartUtc">Creation instant the ownership evaluation validated</param>
        /// <param name="error">Reason when the termination was refused or failed</param>
        /// <returns>True only when the validated process was terminated</returns>
        bool TerminateValidated(IRetainedProcessHandle handle, DateTime validatedStartUtc, out string error);
    }
}
