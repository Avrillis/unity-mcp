using System;
using System.Globalization;

namespace MCPForUnity.Editor.Services.Route
{
    /// <summary>
    /// Transport selected for this editor process.
    /// </summary>
    public enum McpTransportSelection
    {
        Stdio = 0,
        Http = 1,
    }

    /// <summary>
    /// Raw inputs used to resolve one editor process's MCP route.
    ///
    /// Every value is supplied by the caller so the resolver stays free of Unity and
    /// EditorPrefs dependencies and can be unit-tested without an Editor.
    ///
    /// Environment values are <c>null</c> when the variable is absent. A present but
    /// blank value counts as an explicitly supplied override and is therefore invalid:
    /// an explicitly supplied override must never silently fall back.
    /// </summary>
    public sealed class McpRouteInputs
    {
        /// <summary>Value of <c>UNITY_MCP_TRANSPORT</c>, or null when unset.</summary>
        public string TransportEnvironmentValue;

        /// <summary>Value of <c>UNITY_MCP_HTTP_URL</c>, or null when unset.</summary>
        public string HttpUrlEnvironmentValue;

        /// <summary>Value of <c>UNITY_MCP_AUTOSTART</c>, or null when unset.</summary>
        public string AutoStartEnvironmentValue;

        /// <summary>Existing EditorPrefs transport selection (true = HTTP).</summary>
        public bool StoredUseHttpTransport;

        /// <summary>Existing EditorPrefs local HTTP base URL (already normalized).</summary>
        public string StoredLocalHttpBaseUrl;

        /// <summary>Existing EditorPrefs auto-start-on-load flag.</summary>
        public bool StoredAutoStartOnLoad;

        /// <summary>Existing EditorPrefs opt-in allowing a bind-all local URL.</summary>
        public bool StoredAllowLanBind;
    }

    /// <summary>
    /// The single resolved MCP configuration for one editor process.
    ///
    /// Precedence, per setting:
    ///     valid process override  &gt;  existing EditorPrefs value  &gt;  existing default
    ///
    /// With no process override every value mirrors today's package behaviour. With a
    /// process override the value is fixed for the lifetime of the editor process: a later
    /// domain reload or EditorPrefs edit cannot redirect the route.
    ///
    /// An explicitly supplied but invalid override fails closed (<see cref="IsValid"/> is
    /// false) rather than falling back to a stored value.
    /// </summary>
    public sealed class McpRouteConfiguration
    {
        public const string TransportEnvironmentVariable = "UNITY_MCP_TRANSPORT";
        public const string HttpUrlEnvironmentVariable = "UNITY_MCP_HTTP_URL";
        public const string AutoStartEnvironmentVariable = "UNITY_MCP_AUTOSTART";

        public const string TransportValueHttp = "http";
        public const string TransportValueStdio = "stdio";

        private McpRouteConfiguration()
        {
        }

        /// <summary>False when an explicitly supplied override was rejected.</summary>
        public bool IsValid { get; private set; }

        /// <summary>Human readable reason when <see cref="IsValid"/> is false.</summary>
        public string ValidationError { get; private set; }

        public bool HasTransportOverride { get; private set; }
        public bool HasHttpUrlOverride { get; private set; }
        public bool HasAutoStartOverride { get; private set; }

        /// <summary>True when any of the three process overrides was supplied.</summary>
        public bool HasAnyOverride => HasTransportOverride || HasHttpUrlOverride || HasAutoStartOverride;

        /// <summary>
        /// True when the endpoint came from the process environment. The route is then
        /// owned by the launching supervisor, local scope is forced, and managed-route
        /// lifecycle rules apply.
        /// </summary>
        public bool IsManagedRoute => HasHttpUrlOverride;

        /// <summary>Resolved transport for this process.</summary>
        public McpTransportSelection Transport { get; private set; }

        /// <summary>
        /// Resolved local HTTP base URL (no trailing slash, no path). Empty when the
        /// configuration is invalid, so every launch/connect path fails closed.
        /// </summary>
        public string LocalHttpBaseUrl { get; private set; }

        /// <summary>Resolved auto-start-on-load flag.</summary>
        public bool AutoStart { get; private set; }

        /// <summary>
        /// True when this configuration forces the local (non-remote) HTTP scope because
        /// an explicit URL was supplied.
        /// </summary>
        public bool ForcesLocalScope => HasHttpUrlOverride;

        /// <summary>Resolves the effective configuration from a raw input snapshot.</summary>
        public static McpRouteConfiguration Resolve(McpRouteInputs inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            var config = new McpRouteConfiguration();
            config.HasTransportOverride = inputs.TransportEnvironmentValue != null;
            config.HasHttpUrlOverride = inputs.HttpUrlEnvironmentValue != null;
            config.HasAutoStartOverride = inputs.AutoStartEnvironmentValue != null;
            config.IsValid = true;

            // ---- transport -------------------------------------------------------
            if (config.HasTransportOverride)
            {
                string raw = inputs.TransportEnvironmentValue;
                string trimmed = raw == null ? string.Empty : raw.Trim();
                if (string.Equals(trimmed, TransportValueHttp, StringComparison.OrdinalIgnoreCase))
                {
                    config.Transport = McpTransportSelection.Http;
                }
                else if (string.Equals(trimmed, TransportValueStdio, StringComparison.OrdinalIgnoreCase))
                {
                    config.Transport = McpTransportSelection.Stdio;
                }
                else
                {
                    return config.Invalid(
                        $"{TransportEnvironmentVariable} must be exactly '{TransportValueHttp}' or " +
                        $"'{TransportValueStdio}' (received '{Describe(raw)}').");
                }
            }
            else
            {
                config.Transport = inputs.StoredUseHttpTransport
                    ? McpTransportSelection.Http
                    : McpTransportSelection.Stdio;
            }

            // ---- HTTP URL --------------------------------------------------------
            if (config.HasHttpUrlOverride)
            {
                // A process-scoped (managed) URL has a stricter contract than a stored one: it must
                // describe exactly the loopback server this editor launches, so it may neither rely
                // on the scheme's default port nor on the legacy LAN opt-in.
                if (!TryValidateManagedHttpUrl(
                        inputs.HttpUrlEnvironmentValue,
                        out string normalized,
                        out string urlError))
                {
                    return config.Invalid($"{HttpUrlEnvironmentVariable} is invalid: {urlError}");
                }

                // A dedicated local URL with stdio transport is a contradictory tuple.
                if (config.Transport != McpTransportSelection.Http)
                {
                    return config.Invalid(
                        $"{HttpUrlEnvironmentVariable} was supplied together with a non-HTTP " +
                        $"{TransportEnvironmentVariable}; the managed route is contradictory.");
                }

                config.LocalHttpBaseUrl = normalized;
            }
            else if (config.Transport == McpTransportSelection.Http)
            {
                config.LocalHttpBaseUrl = inputs.StoredLocalHttpBaseUrl ?? string.Empty;
            }
            else
            {
                // Stdio keeps the stored URL available for UI display but never launches it.
                config.LocalHttpBaseUrl = inputs.StoredLocalHttpBaseUrl ?? string.Empty;
            }

            // ---- auto-start ------------------------------------------------------
            if (config.HasAutoStartOverride)
            {
                string raw = inputs.AutoStartEnvironmentValue;
                string trimmed = raw == null ? string.Empty : raw.Trim();
                if (trimmed == "1")
                {
                    config.AutoStart = true;
                }
                else if (trimmed == "0")
                {
                    config.AutoStart = false;
                }
                else
                {
                    return config.Invalid(
                        $"{AutoStartEnvironmentVariable} must be exactly '0' or '1' " +
                        $"(received '{Describe(raw)}').");
                }
            }
            else
            {
                config.AutoStart = inputs.StoredAutoStartOnLoad;
            }

            return config;
        }

        /// <summary>
        /// Validates and normalizes an explicitly supplied local HTTP base URL.
        /// Rejects credentials, malformed values, unsupported schemes, non-loopback
        /// hosts (unless LAN binding is explicitly allowed) and unexpected paths.
        /// </summary>
        public static bool TryValidateLocalHttpUrl(
            string value,
            bool allowLanBind,
            out string normalized,
            out string error)
        {
            normalized = null;
            error = null;

            if (value == null)
            {
                error = "no URL was supplied.";
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                error = "the URL is empty.";
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri))
            {
                error = $"'{trimmed}' is not an absolute URL.";
                return false;
            }

            // A managed route is served by the plain-HTTP local server this editor launches, so
            // only http:// is accepted here. https:// would silently describe an endpoint that is
            // not the one being launched.
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                error = $"managed routes must use http:// (received scheme '{uri.Scheme}').";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "URL must not contain credentials/userinfo.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "URL must not contain a query string or fragment.";
                return false;
            }

            if (uri.Port <= 0)
            {
                error = "URL must include a valid TCP port.";
                return false;
            }

            string absolutePath = uri.AbsolutePath;
            if (!(absolutePath.Length == 0
                  || absolutePath == "/"
                  || string.Equals(absolutePath, "/mcp", StringComparison.OrdinalIgnoreCase)))
            {
                error = $"unexpected path '{absolutePath}'; use a bare origin such as http://127.0.0.1:8123.";
                return false;
            }

            bool loopback = IsLoopbackHost(uri.Host);
            bool bindAll = IsBindAllHost(uri.Host);
            if (!loopback && !(bindAll && allowLanBind))
            {
                error = "URL must target loopback (localhost, 127.0.0.1 or ::1); "
                        + "bind-all addresses require the existing LAN opt-in.";
                return false;
            }

            normalized = BuildNormalizedBaseUrl(uri);
            return true;
        }

        /// <summary>
        /// Validates and normalizes a MANAGED (process-scoped) local HTTP base URL.
        ///
        /// The extra rules over <see cref="TryValidateLocalHttpUrl"/> are what stop a managed route
        /// from silently describing a different endpoint than the one this editor launches:
        ///   * the URL text must carry an explicit TCP port - <see cref="Uri.Port"/> reports the
        ///     scheme default (80 for http) when no port was written, so a portless URL would
        ///     otherwise masquerade as a concrete endpoint;
        ///   * the host must be exactly one of <c>localhost</c>, <c>127.0.0.1</c> or <c>::1</c>.
        ///     The legacy LAN/bind-all opt-in does not apply, and the wider 127/8 loopback range is
        ///     refused because two workers on one machine could otherwise name each other's port.
        /// </summary>
        public static bool TryValidateManagedHttpUrl(
            string value,
            out string normalized,
            out string error)
        {
            normalized = null;
            error = null;

            if (!TryGetExplicitPort(value, out int explicitPort))
            {
                error = "managed routes require an explicitly written loopback port "
                        + "(for example http://127.0.0.1:8081); the scheme default port is not allowed.";
                return false;
            }

            // The managed allowlist is deliberately narrower than the legacy loopback check: the
            // whole 127/8 range is NOT a managed endpoint. Only the three approved spellings (and
            // host forms that canonicalize to them, e.g. 127.1 -> 127.0.0.1) are accepted, so a
            // sibling worker's chosen loopback address can never be described as this route.
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri managedUri)
                || !IsManagedLoopbackHost(managedUri.Host))
            {
                error = "managed routes must target exactly localhost, 127.0.0.1 or [::1]; "
                        + "other loopback addresses are not an approved managed endpoint.";
                return false;
            }

            // Never the LAN opt-in: managed routes are local to one editor.
            if (!TryValidateLocalHttpUrl(value, allowLanBind: false, out normalized, out error))
            {
                return false;
            }

            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri uri) && uri.Port != explicitPort)
            {
                normalized = null;
                error = "the URL's explicit port could not be parsed consistently.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// True only for the exact hosts a MANAGED (process-scoped) route may name:
        /// <c>localhost</c>, <c>127.0.0.1</c> and <c>::1</c>.
        ///
        /// This is intentionally stricter than <see cref="IsLoopbackHost"/>: the legacy helper
        /// accepts the whole IPv4 loopback range (127/8), which would let a managed route silently
        /// describe an endpoint belonging to a sibling worker on the same machine. Host forms that
        /// canonicalize to an approved address (for example <c>127.1</c> or the expanded IPv6
        /// spelling of <c>::1</c>) are accepted, because they name the identical address.
        /// </summary>
        public static bool IsManagedLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            string normalized = host.Trim();
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
            {
                normalized = normalized.Substring(1, normalized.Length - 2);
            }

            if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return System.Net.IPAddress.TryParse(normalized, out System.Net.IPAddress parsed)
                   && (parsed.Equals(System.Net.IPAddress.Loopback)
                       || parsed.Equals(System.Net.IPAddress.IPv6Loopback));
        }

        /// <summary>
        /// Extracts the explicitly written TCP port from raw URL text, or returns false when the
        /// authority carries no port. This deliberately inspects the text rather than
        /// <see cref="Uri.Port"/>, which reports the scheme default for a portless URL.
        /// </summary>
        public static bool TryGetExplicitPort(string rawUrl, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return false;
            }

            string trimmed = rawUrl.Trim();
            int schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                return false;
            }

            int authorityStart = schemeEnd + 3;
            int authorityEnd = trimmed.Length;
            foreach (char terminator in new[] { '/', '?', '#' })
            {
                int index = trimmed.IndexOf(terminator, authorityStart);
                if (index >= 0 && index < authorityEnd)
                {
                    authorityEnd = index;
                }
            }

            string authority = trimmed.Substring(authorityStart, authorityEnd - authorityStart);
            int hostStart = authority.LastIndexOf('@') + 1;
            if (hostStart >= authority.Length)
            {
                return false;
            }

            int hostEnd;
            if (authority[hostStart] == '[')
            {
                int close = authority.IndexOf(']', hostStart);
                if (close < 0)
                {
                    return false;
                }

                hostEnd = close + 1;
            }
            else
            {
                int colon = authority.IndexOf(':', hostStart);
                hostEnd = colon < 0 ? authority.Length : colon;
            }

            if (hostEnd >= authority.Length || authority[hostEnd] != ':')
            {
                return false;
            }

            string portText = authority.Substring(hostEnd + 1);
            if (portText.Length == 0)
            {
                return false;
            }

            foreach (char c in portText)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return int.TryParse(
                       portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                   && port > 0
                   && port <= 65535;
        }

        /// <summary>
        /// Whether a global MCP server-source override (the package's "Server source
        /// override" / <c>--from</c> setting) may be honoured.
        ///
        /// A managed route must use the approved server build, because the project-root and
        /// launch-nonce guard lives in that implementation. Substituting another build would
        /// silently remove the guard, so a managed route refuses the override.
        /// </summary>
        public static bool IsServerSourceOverrideAllowed(bool isManagedRoute, string sourceOverride)
            => !isManagedRoute || string.IsNullOrWhiteSpace(sourceOverride);

        private static string BuildNormalizedBaseUrl(Uri uri)
        {
            var builder = new UriBuilder(uri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };

            // Match the package's existing local convention: emit the literal v4 loopback
            // so clients without Happy Eyeballs cannot land on an unbound v6 socket.
            if (string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                builder.Host = "127.0.0.1";
            }

            string baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
            return baseUrl.TrimEnd('/');
        }

        /// <summary>True for localhost/loopback literal hosts.</summary>
        public static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            string normalized = host.Trim().Trim('[', ']').ToLowerInvariant();
            if (normalized == "localhost")
            {
                return true;
            }

            return System.Net.IPAddress.TryParse(normalized, out System.Net.IPAddress parsed)
                   && System.Net.IPAddress.IsLoopback(parsed);
        }

        /// <summary>True for bind-all-interfaces literal hosts.</summary>
        public static bool IsBindAllHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            string normalized = host.Trim().Trim('[', ']').ToLowerInvariant();
            return System.Net.IPAddress.TryParse(normalized, out System.Net.IPAddress parsed)
                   && (parsed.Equals(System.Net.IPAddress.Any) || parsed.Equals(System.Net.IPAddress.IPv6Any));
        }

        private static string Describe(string raw)
        {
            return raw == null ? "<unset>" : raw.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private McpRouteConfiguration Invalid(string error)
        {
            IsValid = false;
            ValidationError = error;
            LocalHttpBaseUrl = string.Empty;
            AutoStart = false;
            return this;
        }
    }
}
