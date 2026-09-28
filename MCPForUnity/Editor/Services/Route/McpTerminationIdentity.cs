using System;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// A retained handle to one specific OS process lifetime.
    ///
    /// The point of a retained handle is that it is bound to the kernel object, not to a bare
    /// PID: if the original process exits and the PID is reused, the handle no longer refers to
    /// the new process. That is what makes "kill the process I just validated" safe.
    /// </summary>
    public interface IRetainedProcessHandle
    {
        /// <summary>Creation instant of the retained process, or false when it cannot be read.</summary>
        bool TryGetStartTimeUtc(out DateTime startUtc);

        /// <summary>Terminates the retained process. False when the handle is no longer valid.</summary>
        bool Kill();
    }

    /// <summary>
    /// Terminates a managed server through a retained process identity.
    ///
    /// A validated ownership evaluation is not enough on its own: between that evaluation and
    /// the kill the process could exit and its PID could be handed to an unrelated process. The
    /// retained handle closes that window, and the creation instant is re-checked exactly
    /// immediately before the kill as a second, independent belt.
    ///
    /// Anything that cannot be re-proven leaves the process untouched.
    /// </summary>
    public static class McpTerminationIdentity
    {
        public static bool TryTerminate(
            McpRunStateRecord record,
            DateTime validatedServerStartUtc,
            IRetainedProcessHandle handle,
            out string error)
        {
            error = null;

            if (record == null)
            {
                error = "no validated ownership record was supplied.";
                return false;
            }

            if (record.ServerPid <= 0)
            {
                error = "the validated record has no server PID.";
                return false;
            }

            return TryTerminate(validatedServerStartUtc, handle, out error);
        }

        /// <summary>
        /// Terminates the retained process only when its creation instant is still exactly the
        /// validated one.
        /// </summary>
        public static bool TryTerminate(
            DateTime validatedServerStartUtc,
            IRetainedProcessHandle handle,
            out string error)
        {
            error = null;

            if (handle == null)
            {
                error = "no retained process identity is available; refusing to kill by PID alone.";
                return false;
            }

            if (!handle.TryGetStartTimeUtc(out DateTime retainedStart))
            {
                error = "the retained process lifetime could not be read; refusing to kill.";
                return false;
            }

            if (!McpOwnershipEvaluator.SameProcessInstant(retainedStart, validatedServerStartUtc))
            {
                error = "the retained process is not the validated server lifetime (PID reuse); refusing to kill.";
                return false;
            }

            if (!handle.Kill())
            {
                error = "the retained process could not be terminated.";
                return false;
            }

            return true;
        }
    }
}
