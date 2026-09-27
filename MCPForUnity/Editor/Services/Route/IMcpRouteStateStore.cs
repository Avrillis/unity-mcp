namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Project-local storage for the managed-route ownership record.
    ///
    /// Everything lives under <c>&lt;project&gt;/Library/MCPForUnity/RunState</c> so that two
    /// editors on the same machine can never share (or overwrite) each other's evidence.
    /// </summary>
    public interface IMcpRouteStateStore
    {
        /// <summary>Absolute project root this store is bound to.</summary>
        string GetCanonicalProjectRoot();

        /// <summary>Absolute RunState directory for the bound project.</summary>
        string GetRunStateDirectory();

        /// <summary>Absolute path of the versioned ownership record.</summary>
        string GetHandshakePath();

        /// <summary>Absolute path of the per-port server PID evidence file.</summary>
        string GetPidFilePath(int port);

        /// <summary>
        /// Reads and parses the ownership record. Returns false (with a reason) when the
        /// record is absent, unreadable or malformed.
        /// </summary>
        bool TryRead(out McpRunStateRecord record, out string error);

        /// <summary>Atomically writes the ownership record.</summary>
        bool Write(McpRunStateRecord record, out string error);

        /// <summary>Reads the server PID evidence file written by the Python server.</summary>
        bool TryReadPidFile(string pidFilePath, out int pid, out bool fileExists);

        /// <summary>Removes the ownership record (never touches a live process).</summary>
        void DeleteRecord();
    }
}
