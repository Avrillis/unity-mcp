using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Crash-safe, same-directory publication of the project-local ownership record.
    ///
    /// The record is the only evidence that ties a launch nonce to a server process, so a
    /// half-written or missing file must never replace a good one:
    ///
    ///   * every write uses a unique temporary name in the SAME directory as the destination,
    ///     so two concurrent writers can never collide on a shared ".tmp" path;
    ///   * the temporary file is fully written and flushed before it is published;
    ///   * an existing destination is replaced with <see cref="File.Replace(string,string,string)"/>,
    ///     which is atomic on NTFS and a rename on POSIX;
    ///   * the previous record is NEVER deleted first. If the atomic replacement is unavailable
    ///     or fails, the write fails closed and the previous record stays intact.
    ///
    /// This type is deliberately free of Unity dependencies so the publication rules can be
    /// exercised without an Editor.
    /// </summary>
    public static class McpRunStateFile
    {
        /// <summary>Prefix of the unique per-writer temporary file name.</summary>
        public const string TempFilePrefix = "handshake.json.";

        /// <summary>Suffix of the unique per-writer temporary file name.</summary>
        public const string TempFileSuffix = ".tmp";

        /// <summary>
        /// Abandoned temporaries younger than this are left alone: they may still belong to a
        /// writer that is mid-publication.
        /// </summary>
        public static readonly TimeSpan AbandonedTempAge = TimeSpan.FromHours(6);

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="destinationPath"/> atomically.
        /// Returns false (leaving any existing record untouched) when the publication cannot be
        /// performed safely.
        /// </summary>
        public static bool TryWriteAtomic(string destinationPath, string content, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                error = "the destination path is empty.";
                return false;
            }

            string directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory))
            {
                error = $"'{destinationPath}' has no directory to publish into.";
                return false;
            }

            string tempPath = null;
            try
            {
                Directory.CreateDirectory(directory);
                CleanupAbandonedTemps(directory);

                tempPath = CreateUniqueTempPath(directory);
                using (var stream = new FileStream(
                           tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                        .GetBytes(content ?? string.Empty);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                Publish(tempPath, destinationPath);
                tempPath = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"the ownership record could not be published to '{destinationPath}': {ex.Message}";
                return false;
            }
            finally
            {
                // Only our own uniquely named temporary is removed here: it can never be the
                // published record, and no other writer can be using this exact name.
                if (tempPath != null)
                {
                    TryDelete(tempPath);
                }
            }
        }

        /// <summary>Unique, same-directory temporary path for one write.</summary>
        public static string CreateUniqueTempPath(string directory)
        {
            return Path.Combine(
                directory,
                TempFilePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TempFileSuffix);
        }

        /// <summary>
        /// Removes temporaries old enough that no live writer can still own them. Best effort:
        /// failures are ignored because a leftover temporary never affects ownership.
        /// </summary>
        public static void CleanupAbandonedTemps(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow - AbandonedTempAge;
                foreach (string candidate in Directory.GetFiles(
                             directory,
                             TempFilePrefix + "*" + TempFileSuffix,
                             SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(candidate) < cutoff)
                        {
                            TryDelete(candidate);
                        }
                    }
                    catch
                    {
                        // Ignore an individual temporary; ownership never depends on it.
                    }
                }
            }
            catch
            {
                // Never let housekeeping fail a write.
            }
        }

        private static void Publish(string tempPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                // Atomic replace. Deliberately no delete-then-move fallback: an interruption
                // between a delete and a move would leave no ownership record at all.
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
                return;
            }

            // File.Move fails when the destination appeared concurrently, which is the correct
            // fail-closed outcome for a first publication race.
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
