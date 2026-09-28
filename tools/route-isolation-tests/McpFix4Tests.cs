using MCPForUnity.Editor.Services.Route;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// C17-R3-FIX4 coverage for the one remaining blocker: the MANAGED host allowlist must be the
    /// exact raw authority text <c>localhost</c> / <c>127.0.0.1</c> / <c>[::1]</c>, and must not
    /// accept alternate spellings that the platform URI parser canonicalizes to 127.0.0.1
    /// (<c>127.1</c>, <c>0177.0.0.1</c>, <c>2130706433</c>).
    ///
    /// Every case runs against the production validator and the production raw-authority reader.
    /// </summary>
    [TestFixture]
    public class McpFix4ManagedAuthorityTests
    {
        private static bool TryValidate(string url, out string normalized, out string error)
            => McpRouteConfiguration.TryValidateManagedHttpUrl(url, out normalized, out error);

        [TestCase("http://localhost:8081", "http://127.0.0.1:8081")]
        [TestCase("http://LOCALHOST:8081", "http://127.0.0.1:8081")]
        [TestCase("http://LocalHost:8081", "http://127.0.0.1:8081")]
        [TestCase("http://127.0.0.1:8081", "http://127.0.0.1:8081")]
        // UriBuilder collapses the scheme default port, so an explicit :80 normalizes without it.
        // That pre-existing normalization is untouched by FIX4; the text-level port requirement is
        // still enforced before normalization.
        [TestCase("http://127.0.0.1:80", "http://127.0.0.1")]
        [TestCase("http://127.0.0.1:65535", "http://127.0.0.1:65535")]
        [TestCase("http://[::1]:8081", "http://[::1]:8081")]
        [TestCase("http://[::1]:65535", "http://[::1]:65535")]
        public void ManagedUrl_AcceptsOnlyExactLoopbackSpellings(string url, string expected)
        {
            Assert.That(TryValidate(url, out string normalized, out string error), Is.True, error);
            Assert.That(normalized, Is.EqualTo(expected));
        }

        // Alternate spellings that canonicalize to 127.0.0.1 are refused: the managed contract is
        // the raw authority text, so a differently-spelled loopback endpoint can never be accepted.
        [TestCase("http://127.1:8081")]
        [TestCase("http://0177.0.0.1:8081")]
        [TestCase("http://2130706433:8081")]
        [TestCase("http://127.0.0.2:8081")]
        [TestCase("http://127.1.2.3:8081")]
        [TestCase("http://127.255.255.254:8081")]
        [TestCase("http://0.0.0.0:8081")]
        [TestCase("http://[::]:8081")]
        [TestCase("http://192.168.1.2:8081")]
        [TestCase("http://10.0.0.1:8081")]
        [TestCase("http://172.16.0.1:8081")]
        [TestCase("http://example.com:8081")]
        [TestCase("http://localhost.example.com:8081")]
        public void ManagedUrl_RefusesNonCanonicalAndForeignHosts(string url)
        {
            Assert.That(TryValidate(url, out string normalized, out string error), Is.False,
                $"'{url}' must not be an approved managed endpoint");
            Assert.That(normalized, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("http://127.0.0.1", "scheme default port")]
        [TestCase("http://localhost", "scheme default port")]
        [TestCase("http://[::1]", "scheme default port")]
        [TestCase("http://127.0.0.1:", "empty port")]
        [TestCase("http://127.0.0.1:abc", "non-numeric port")]
        [TestCase("http://127.0.0.1:0", "port zero")]
        [TestCase("http://127.0.0.1:65536", "port past the TCP range")]
        [TestCase("http://localhost:0", "port zero")]
        [TestCase("http://localhost:65536", "port past the TCP range")]
        [TestCase("http://[::1]:", "empty port")]
        [TestCase("http://[::1]:abc", "non-numeric port")]
        [TestCase("http://[::1]", "scheme default port")]
        [TestCase("http://::1:8081", "unbracketed colon host")]
        [TestCase("http://:8081", "empty host")]
        [TestCase("http://user@localhost:8081", "userinfo")]
        [TestCase("http://user:pass@localhost:8081", "userinfo")]
        [TestCase("https://127.0.0.1:8081", "https scheme")]
        [TestCase("http://127.0.0.1:8081?x=1", "query")]
        [TestCase("http://127.0.0.1:8081#frag", "fragment")]
        [TestCase("http://127.0.0.1:8081/other", "unexpected path")]
        public void ManagedUrl_RefusesMalformedAuthorityAndPort(string url, string reason)
        {
            Assert.That(TryValidate(url, out string normalized, out string error), Is.False,
                $"'{url}' must be refused ({reason})");
            Assert.That(normalized, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("http://127.0.0.1:8081/")]
        [TestCase("http://127.0.0.1:8081/mcp")]
        public void ManagedUrl_KeepsBareOriginAndMcpPath(string url)
        {
            Assert.That(TryValidate(url, out string normalized, out string error), Is.True, error);
            Assert.That(normalized, Is.EqualTo("http://127.0.0.1:8081"));
        }

        [Test]
        public void RawAuthority_ReturnsTheTextExactlyAsWritten()
        {
            Assert.That(
                McpRouteConfiguration.TryExtractManagedAuthority(
                    "http://LOCALHOST:8081", out string host, out int port),
                Is.True);
            Assert.That(host, Is.EqualTo("LOCALHOST"), "the raw spelling must survive, not Uri.Host");
            Assert.That(port, Is.EqualTo(8081));

            Assert.That(
                McpRouteConfiguration.TryExtractManagedAuthority(
                    "http://127.1:8081", out host, out port),
                Is.True);
            Assert.That(host, Is.EqualTo("127.1"),
                "the legacy spelling is visible here, which is what lets the allowlist refuse it");

            Assert.That(
                McpRouteConfiguration.TryExtractManagedAuthority(
                    "http://[::1]:8081", out host, out port),
                Is.True);
            Assert.That(host, Is.EqualTo("[::1]"), "bracketed IPv6 keeps its brackets");
            Assert.That(port, Is.EqualTo(8081));
        }

        [TestCase("http://user@127.0.0.1:8081", "userinfo")]
        [TestCase("http://127.0.0.1", "no port")]
        [TestCase("https://127.0.0.1:8081", "https scheme")]
        [TestCase("http://::1:8081", "unbracketed colon host")]
        [TestCase("http://[::1:8081", "unterminated IPv6 bracket")]
        [TestCase("http://:8081", "empty host")]
        public void RawAuthority_RefusesAmbiguousAuthority(string url, string reason)
        {
            Assert.That(
                McpRouteConfiguration.TryExtractManagedAuthority(url, out string host, out int port),
                Is.False, $"'{url}' must not yield a managed authority ({reason})");
            Assert.That(host, Is.Null);
            Assert.That(port, Is.Zero);
        }

        [Test]
        public void RawHostAllowlist_IsExactTextual()
        {
            Assert.That(McpRouteConfiguration.IsManagedRawHost("localhost"), Is.True);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("LOCALHOST"), Is.True);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("127.0.0.1"), Is.True);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("[::1]"), Is.True);

            Assert.That(McpRouteConfiguration.IsManagedRawHost("127.1"), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("0177.0.0.1"), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("2130706433"), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("::1"), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedRawHost("127.0.0.2"), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedRawHost(null), Is.False);
            Assert.That(McpRouteConfiguration.IsManagedRawHost(string.Empty), Is.False);
        }

        [TestCase("http://127.0.0.1:8081", true)]
        [TestCase("http://localhost:8081", true)]
        [TestCase("http://[::1]:8081", true)]
        [TestCase("http://127.1:8081", false)]
        [TestCase("http://0177.0.0.1:8081", false)]
        [TestCase("http://2130706433:8081", false)]
        [TestCase("http://127.0.0.2:8081", false)]
        public void Resolve_EnforcesTheExactHostAllowlist(string url, bool expected)
        {
            // The end-to-end configuration entry point must fail closed for the alias spellings,
            // not only the validator in isolation.
            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(new McpRouteInputs
            {
                TransportEnvironmentValue = "http",
                HttpUrlEnvironmentValue = url,
                StoredAllowLanBind = true,
            });

            Assert.That(configuration.IsValid, Is.EqualTo(expected), configuration.ValidationError);
        }
    }
}
