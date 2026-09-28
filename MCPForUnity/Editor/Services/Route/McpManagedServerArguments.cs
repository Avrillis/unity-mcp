using System;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Builds the argument tail of a guarded managed server launch.
    ///
    /// Kept free of Unity dependencies so the exact arguments a managed launch produces - the
    /// resolved source pin, the project root, the pidfile and the launch nonce - can be asserted
    /// without opening an Editor.
    /// </summary>
    public static class McpManagedServerArguments
    {
        /// <summary>Quotes a value when it contains a space; otherwise returns it unchanged.</summary>
        public static string QuoteIfNeeded(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            return input.IndexOf(' ') >= 0 ? $"\"{input}\"" : input;
        }

        /// <summary>
        /// Appends the launch-identity arguments (<c>--pidfile</c> and
        /// <c>--unity-instance-token</c>) to a base command.
        ///
        /// A managed launch without both of these could not be reconciled or stopped safely, so
        /// an unusable pidfile path or nonce fails closed instead of producing a command.
        /// </summary>
        public static bool TryAppendLaunchIdentity(
            string baseCommand,
            string pidFilePath,
            string instanceToken,
            out string command,
            out string error)
        {
            command = null;
            error = null;

            if (string.IsNullOrWhiteSpace(baseCommand))
            {
                error = "the base launch command is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(pidFilePath))
            {
                error = "the launch has no pidfile path.";
                return false;
            }

            if (!IsSafeToken(instanceToken))
            {
                error = "the launch nonce is missing or not a safe token.";
                return false;
            }

            command = $"{baseCommand} --pidfile {QuoteIfNeeded(pidFilePath)} --unity-instance-token {instanceToken}";
            return true;
        }

        /// <summary>
        /// True when the value is a non-empty token made only of characters that are safe to
        /// place on a command line unquoted (hex nonce / GUID "N" form).
        /// </summary>
        public static bool IsSafeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            foreach (char c in trimmed)
            {
                bool alphanumeric = (c >= '0' && c <= '9')
                                    || (c >= 'a' && c <= 'z')
                                    || (c >= 'A' && c <= 'Z')
                                    || c == '-' || c == '_';
                if (!alphanumeric)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Formats the <c>--from</c> prefix for a managed launch from an already-resolved source.
        /// Never returns an implicit PyPI reference.
        /// </summary>
        public static bool TryBuildManagedFromArgs(string resolvedSource, out string fromArgs, out string error)
        {
            fromArgs = null;
            error = null;

            if (string.IsNullOrWhiteSpace(resolvedSource))
            {
                error = "no compatible server source could be resolved for this managed route.";
                return false;
            }

            fromArgs = $"--from \"{resolvedSource}\"";
            return true;
        }
    }
}
