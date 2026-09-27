using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Route
{
    /// <inheritdoc/>
    public sealed class McpRouteStateStore : IMcpRouteStateStore
    {
        private readonly string _projectRoot;

        public McpRouteStateStore() : this(ResolveProjectRoot())
        {
        }

        public McpRouteStateStore(string canonicalProjectRoot)
        {
            _projectRoot = McpRunStatePaths.Canonicalize(canonicalProjectRoot);
        }

        /// <summary>Canonical project root for the running editor.</summary>
        public static string ResolveProjectRoot()
        {
            try
            {
                // Application.dataPath is ".../<Project>/Assets"
                return McpRunStatePaths.Canonicalize(Path.Combine(Application.dataPath, ".."));
            }
            catch
            {
                return McpRunStatePaths.Canonicalize(Application.dataPath);
            }
        }

        /// <inheritdoc/>
        public string GetCanonicalProjectRoot() => _projectRoot;

        /// <inheritdoc/>
        public string GetRunStateDirectory()
            => McpRunStatePaths.GetRunStateDirectory(_projectRoot);

        /// <inheritdoc/>
        public string GetHandshakePath()
        {
            string path = McpRunStatePaths.GetHandshakePath(_projectRoot);
            return IsInsideRunState(path) ? path : string.Empty;
        }

        /// <inheritdoc/>
        public string GetPidFilePath(int port)
        {
            string path = McpRunStatePaths.GetPidFilePath(_projectRoot, port);
            return IsInsideRunState(path) ? path : string.Empty;
        }

        /// <inheritdoc/>
        public bool TryRead(out McpRunStateRecord record, out string error)
        {
            record = null;
            error = null;

            string path = GetHandshakePath();
            if (string.IsNullOrEmpty(path))
            {
                error = "the project-local RunState directory could not be resolved.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = "no ownership record is present.";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                error = $"the ownership record at '{path}' could not be read: {ex.Message}";
                return false;
            }

            if (!McpRunStateRecord.TryParse(json, out record, out string parseError))
            {
                error = $"the ownership record at '{path}' is unusable: {parseError}";
                record = null;
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public bool Write(McpRunStateRecord record, out string error)
        {
            error = null;
            if (record == null)
            {
                error = "no record was supplied.";
                return false;
            }

            string path = GetHandshakePath();
            if (string.IsNullOrEmpty(path))
            {
                error = "the project-local RunState directory could not be resolved.";
                return false;
            }

            string directory = GetRunStateDirectory();
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = $"the RunState directory '{directory}' could not be created: {ex.Message}";
                return false;
            }

            string json = record.ToJson();
            string tempPath = path + ".tmp";

            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                ReplaceAtomically(tempPath, path);
                return true;
            }
            catch (Exception ex)
            {
                error = $"the ownership record could not be written to '{path}': {ex.Message}";
                TryDelete(tempPath);
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryReadPidFile(string pidFilePath, out int pid, out bool fileExists)
        {
            pid = 0;
            fileExists = false;

            if (string.IsNullOrWhiteSpace(pidFilePath))
            {
                return false;
            }

            try
            {
                fileExists = File.Exists(pidFilePath);
                if (!fileExists)
                {
                    return false;
                }

                string text = File.ReadAllText(pidFilePath).Trim();
                if (int.TryParse(text, out pid) && pid > 0)
                {
                    return true;
                }

                foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(line.Trim(), out pid) && pid > 0)
                    {
                        return true;
                    }
                }

                pid = 0;
                return false;
            }
            catch
            {
                pid = 0;
                return false;
            }
        }

        /// <inheritdoc/>
        public void DeleteRecord()
        {
            string path = GetHandshakePath();
            if (!string.IsNullOrEmpty(path))
            {
                TryDelete(path);
                TryDelete(path + ".tmp");
            }
        }

        private bool IsInsideRunState(string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            string runState = GetRunStateDirectory();
            return !string.IsNullOrEmpty(runState) && McpRunStatePaths.IsPathInside(candidate, runState);
        }

        /// <summary>
        /// Replaces the destination with the freshly written temp file. File.Replace is
        /// atomic on NTFS; where it is unsupported we fall back to a delete + move, which is
        /// still safe here because only this editor writes its own record.
        /// </summary>
        private static void ReplaceAtomically(string tempPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
                    return;
                }
                catch (Exception)
                {
                    File.Delete(destinationPath);
                }
            }

            File.Move(tempPath, destinationPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
