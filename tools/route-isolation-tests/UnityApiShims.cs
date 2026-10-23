using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Test-only stand-ins for the handful of Unity/package APIs that
// MCPForUnity/Editor/Services/Server/ProcessDetector.cs touches. They let the REAL ProcessDetector
// and McpProcessInspector be compiled and exercised without an Editor. Nothing here ships.

namespace UnityEngine
{
    internal enum RuntimePlatform
    {
        WindowsEditor,
        OSXEditor,
        LinuxEditor,
    }

    internal static class Application
    {
        public static RuntimePlatform platform
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return RuntimePlatform.WindowsEditor;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return RuntimePlatform.OSXEditor;
                return RuntimePlatform.LinuxEditor;
            }
        }

        public static string dataPath => Path.GetTempPath();
    }
}

namespace MCPForUnity.Editor.Helpers
{
    internal static class McpLog
    {
        public static void Warn(string message)
        {
        }
    }

    internal static class ExecPath
    {
        // Mirrors the production signature. Only reached by the unchanged tasklist / netstat / ps paths.
        internal static bool TryRun(
            string file,
            string args,
            string workingDir,
            out string stdout,
            out string stderr,
            int timeoutMs = 15000,
            string extraPathPrepend = null)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return false;
                var outText = new StringBuilder();
                var errText = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) outText.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errText.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                process.WaitForExit();
                stdout = outText.ToString();
                stderr = errText.ToString();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
