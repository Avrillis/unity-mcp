using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Platform-specific process inspection for detecting MCP server processes.
    /// </summary>
    public class ProcessDetector : IProcessDetector
    {
        // Test seam for the Windows command-line query only. Null means the real, bounded runner.
        private readonly IWindowsCommandRunner _windowsCommandRunner;

        public ProcessDetector() : this(null)
        {
        }

        internal ProcessDetector(IWindowsCommandRunner windowsCommandRunner)
        {
            _windowsCommandRunner = windowsCommandRunner;
        }

        /// <inheritdoc/>
        public string NormalizeForMatch(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <inheritdoc/>
        public int GetCurrentProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        /// <inheritdoc/>
        public bool ProcessExists(int pid)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // On Windows, use tasklist to check if process exists
                    bool ok = ExecPath.TryRun("tasklist", $"/FI \"PID eq {pid}\"", Application.dataPath, out var stdout, out var stderr, 5000);
                    string combined = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).ToLowerInvariant();
                    return ok && combined.Contains(pid.ToString());
                }

                // Unix: ps exits non-zero when PID is not found.
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";
                ExecPath.TryRun(psPath, $"-p {pid} -o pid=", Application.dataPath, out var psStdout, out var psStderr, 2000);
                string combined2 = ((psStdout ?? string.Empty) + "\n" + (psStderr ?? string.Empty)).Trim();
                return !string.IsNullOrEmpty(combined2) && combined2.Any(char.IsDigit);
            }
            catch
            {
                return true; // Assume it exists if we cannot verify.
            }
        }

        /// <inheritdoc/>
        public bool TryGetProcessCommandLine(int pid, out string argsLower)
        {
            argsLower = string.Empty;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Windows: read the exact command line through CIM (Win32_Process) using the fixed
                    // system PowerShell. wmic.exe was removed from current Windows 11 releases, and
                    // without a command line the managed ownership proof can never succeed.
                    // Anything other than one well-formed result is a failure (fail closed).
                    if (!WindowsProcessCommandLine.TryGetCommandLine(pid, _windowsCommandRunner, out string commandLine))
                    {
                        return false;
                    }

                    argsLower = NormalizeForMatch(commandLine);
                    return !string.IsNullOrEmpty(argsLower);
                }

                // Unix: ps -p pid -ww -o args=
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";

                bool ok = ExecPath.TryRun(psPath, $"-p {pid} -ww -o args=", Application.dataPath, out var stdout, out var stderr, 5000);
                if (!ok && string.IsNullOrWhiteSpace(stdout))
                {
                    return false;
                }
                string combined = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).Trim();
                if (string.IsNullOrEmpty(combined)) return false;
                // Normalize for matching to tolerate ps wrapping/newlines.
                argsLower = NormalizeForMatch(combined);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public List<int> GetListeningProcessIdsForPort(int port)
        {
            var results = new List<int>();
            try
            {
                string stdout, stderr;
                bool success;

                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Run netstat -ano directly (without findstr) and filter in C#.
                    // Using findstr in a pipe causes the entire command to return exit code 1 when no matches are found,
                    // which ExecPath.TryRun interprets as failure. Running netstat alone gives us exit code 0 on success.
                    success = ExecPath.TryRun("netstat.exe", "-ano", Application.dataPath, out stdout, out stderr);

                    // Process stdout regardless of success flag - netstat might still produce valid output
                    if (!string.IsNullOrEmpty(stdout))
                    {
                        string portSuffix = $":{port}";
                        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            // Windows netstat format: Proto  Local Address          Foreign Address        State           PID
                            // Example: TCP    0.0.0.0:8080           0.0.0.0:0              LISTENING       12345
                            if (line.Contains("LISTENING") && line.Contains(portSuffix))
                            {
                                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                // Verify the local address column actually ends with :{port}
                                // parts[0] = Proto (TCP), parts[1] = Local Address, parts[2] = Foreign Address, parts[3] = State, parts[4] = PID
                                if (parts.Length >= 5)
                                {
                                    string localAddr = parts[1];
                                    if (localAddr.EndsWith(portSuffix) && int.TryParse(parts[parts.Length - 1], out int parsedPid))
                                    {
                                        results.Add(parsedPid);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // lsof: only return LISTENers (avoids capturing random clients)
                    // Use /usr/sbin/lsof directly as it might not be in PATH for Unity
                    string lsofPath = "/usr/sbin/lsof";
                    if (!File.Exists(lsofPath)) lsofPath = "lsof"; // Fallback

                    // -nP: avoid DNS/service name lookups; faster and less error-prone
                    success = ExecPath.TryRun(lsofPath, $"-nP -iTCP:{port} -sTCP:LISTEN -t", Application.dataPath, out stdout, out stderr);
                    if (success && !string.IsNullOrWhiteSpace(stdout))
                    {
                        var pidStrings = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var pidString in pidStrings)
                        {
                            if (int.TryParse(pidString.Trim(), out int parsedPid))
                            {
                                results.Add(parsedPid);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Error checking port {port}: {ex.Message}");
            }
            return results.Distinct().ToList();
        }

        /// <inheritdoc/>
        public bool LooksLikeMcpServerProcess(int pid)
        {
            try
            {
                // Windows best-effort: First check process name with tasklist, then try to get the command line via CIM
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Step 1: Check if process name matches known server executables
                    ExecPath.TryRun("cmd.exe", $"/c tasklist /FI \"PID eq {pid}\"", Application.dataPath, out var tasklistOut, out var tasklistErr, 5000);
                    string tasklistCombined = ((tasklistOut ?? string.Empty) + "\n" + (tasklistErr ?? string.Empty)).ToLowerInvariant();

                    // Check for common process names
                    bool isPythonOrUv = tasklistCombined.Contains("python") || tasklistCombined.Contains("uvx") || tasklistCombined.Contains("uv.exe");
                    if (!isPythonOrUv)
                    {
                        return false;
                    }

                    // Step 2: Try to get the command line (CIM, no wmic) for better validation
                    bool haveCommandLine = WindowsProcessCommandLine.TryGetCommandLine(pid, _windowsCommandRunner, out string rawCommandLine);
                    string cmdLower = haveCommandLine ? rawCommandLine.ToLowerInvariant() : string.Empty;
                    string cmdCompact = haveCommandLine ? NormalizeForMatch(rawCommandLine) : string.Empty;

                    // If we can see the command line, validate it's our server
                    if (haveCommandLine && !string.IsNullOrEmpty(cmdLower))
                    {
                        bool mentionsMcp = cmdCompact.Contains("mcp-for-unity")
                                           || cmdCompact.Contains("mcp_for_unity")
                                           || cmdCompact.Contains("mcpforunity")
                                           || cmdCompact.Contains("mcpforunityserver");
                        bool mentionsTransport = cmdCompact.Contains("--transporthttp") || (cmdCompact.Contains("--transport") && cmdCompact.Contains("http"));
                        bool mentionsUvicorn = cmdLower.Contains("uvicorn");

                        if (mentionsMcp || mentionsTransport || mentionsUvicorn)
                        {
                            return true;
                        }
                    }

                    // Fall back to just checking for python/uv processes if no command line was available
                    // This is less precise but necessary for cases where process metadata access is restricted
                    return isPythonOrUv;
                }

                // macOS/Linux: ps -p pid -ww -o comm= -o args=
                // Use -ww to avoid truncating long command lines (important for reliably spotting 'mcp-for-unity').
                // Use an absolute ps path to avoid relying on PATH inside the Unity Editor process.
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";
                // Important: ExecPath.TryRun returns false when exit code != 0, but ps output can still be useful.
                // Always parse stdout/stderr regardless of exit code to avoid false negatives.
                ExecPath.TryRun(psPath, $"-p {pid} -ww -o comm= -o args=", Application.dataPath, out var psOut, out var psErr, 5000);
                string raw = ((psOut ?? string.Empty) + "\n" + (psErr ?? string.Empty)).Trim();
                string s = raw.ToLowerInvariant();
                string sCompact = NormalizeForMatch(raw);
                if (!string.IsNullOrEmpty(s))
                {
                    bool mentionsMcp = sCompact.Contains("mcp-for-unity")
                                       || sCompact.Contains("mcp_for_unity")
                                       || sCompact.Contains("mcpforunity");

                    // If it explicitly mentions the server package/entrypoint, that is sufficient.
                    // Note: Check before Unity exclusion since "mcp-for-unity" contains "unity".
                    if (mentionsMcp)
                    {
                        return true;
                    }

                    // Explicitly never kill Unity / Unity Hub processes
                    // Note: explicit !mentionsMcp is defensive; we already return early for mentionsMcp above.
                    if (s.Contains("unityhub") || s.Contains("unity hub") || (s.Contains("unity") && !mentionsMcp))
                    {
                        return false;
                    }

                    // Positive indicators
                    bool mentionsUvx = s.Contains("uvx") || s.Contains(" uvx ");
                    bool mentionsUv = s.Contains("uv ") || s.Contains("/uv");
                    bool mentionsPython = s.Contains("python");
                    bool mentionsUvicorn = s.Contains("uvicorn");
                    bool mentionsTransport = sCompact.Contains("--transporthttp") || (sCompact.Contains("--transport") && sCompact.Contains("http"));

                    // Accept if it looks like uv/uvx/python launching our server package/entrypoint
                    if ((mentionsUvx || mentionsUv || mentionsPython || mentionsUvicorn) && mentionsTransport)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
