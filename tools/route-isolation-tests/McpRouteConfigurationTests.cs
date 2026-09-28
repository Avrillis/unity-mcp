using System;
using NUnit.Framework;
using MCPForUnity.Editor.Services.Route;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// Process-scoped configuration resolution: precedence, fail-closed behaviour, route
    /// stability and per-editor isolation.
    /// </summary>
    [TestFixture]
    public class McpRouteConfigurationTests
    {
        private const string StoredUrl = "http://127.0.0.1:8080";

        private static McpRouteInputs Stored(
            bool useHttp = true,
            string url = StoredUrl,
            bool autoStart = false,
            bool allowLanBind = false)
        {
            return new McpRouteInputs
            {
                StoredUseHttpTransport = useHttp,
                StoredLocalHttpBaseUrl = url,
                StoredAutoStartOnLoad = autoStart,
                StoredAllowLanBind = allowLanBind,
            };
        }

        // ------------------------------------------------------------------ A
        [Test]
        public void A_NoProcessOverrides_PreservesExistingConfigurationBehaviour()
        {
            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(
                Stored(useHttp: true, url: "http://127.0.0.1:8099", autoStart: true));

            Assert.That(configuration.IsValid, Is.True);
            Assert.That(configuration.HasAnyOverride, Is.False);
            Assert.That(configuration.IsManagedRoute, Is.False);
            Assert.That(configuration.Transport, Is.EqualTo(McpTransportSelection.Http));
            Assert.That(configuration.LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8099"));
            Assert.That(configuration.AutoStart, Is.True);
        }

        [Test]
        public void A_NoProcessOverrides_StoredStdioSelectionIsPreserved()
        {
            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(
                Stored(useHttp: false, autoStart: false));

            Assert.That(configuration.IsValid, Is.True);
            Assert.That(configuration.Transport, Is.EqualTo(McpTransportSelection.Stdio));
            Assert.That(configuration.AutoStart, Is.False);
        }

        // ------------------------------------------------------------------ B
        [Test]
        public void B_ProcessTransportOverride_WinsOverEditorPrefs()
        {
            McpRouteInputs inputs = Stored(useHttp: false);
            inputs.TransportEnvironmentValue = "http";
            inputs.HttpUrlEnvironmentValue = "http://127.0.0.1:8123";

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.Transport, Is.EqualTo(McpTransportSelection.Http));
            Assert.That(configuration.HasTransportOverride, Is.True);
        }

        [Test]
        public void B_ProcessTransportOverride_StdioWinsOverStoredHttp()
        {
            McpRouteInputs inputs = Stored(useHttp: true);
            inputs.TransportEnvironmentValue = "stdio";

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.Transport, Is.EqualTo(McpTransportSelection.Stdio));
            Assert.That(configuration.IsManagedRoute, Is.False);
        }

        // ------------------------------------------------------------------ C
        [Test]
        public void C_ProcessHttpUrlOverride_WinsOverEditorPrefs()
        {
            McpRouteInputs inputs = Stored(url: "http://127.0.0.1:8080");
            inputs.TransportEnvironmentValue = "http";
            inputs.HttpUrlEnvironmentValue = "http://127.0.0.1:8123";

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8123"));
            Assert.That(configuration.HasHttpUrlOverride, Is.True);
            Assert.That(configuration.IsManagedRoute, Is.True);
            Assert.That(configuration.ForcesLocalScope, Is.True);
        }

        [Test]
        public void C_ProcessHttpUrlOverride_NormalizesLocalhostAndTrailingSegments()
        {
            McpRouteInputs inputs = Stored();
            inputs.TransportEnvironmentValue = "http";
            inputs.HttpUrlEnvironmentValue = "http://localhost:8123/mcp";

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8123"));
        }

        // ------------------------------------------------------------------ D
        [Test]
        public void D_ProcessAutoStartOverride_WinsOverEditorPrefs()
        {
            McpRouteInputs inputs = Stored(autoStart: false);
            inputs.AutoStartEnvironmentValue = "1";

            McpRouteConfiguration enabled = McpRouteConfiguration.Resolve(inputs);
            Assert.That(enabled.AutoStart, Is.True);
            Assert.That(enabled.HasAutoStartOverride, Is.True);

            McpRouteInputs disabledInputs = Stored(autoStart: true);
            disabledInputs.AutoStartEnvironmentValue = "0";
            McpRouteConfiguration disabled = McpRouteConfiguration.Resolve(disabledInputs);
            Assert.That(disabled.AutoStart, Is.False);
        }

        // ------------------------------------------------------------------ E
        [TestCase("tcp")]
        [TestCase("HTTPs")]
        [TestCase("")]
        [TestCase("   ")]
        public void E_InvalidTransportOverride_FailsClosed(string value)
        {
            McpRouteInputs inputs = Stored(useHttp: true);
            inputs.TransportEnvironmentValue = value;

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.IsValid, Is.False);
            Assert.That(configuration.ValidationError, Does.Contain(McpRouteConfiguration.TransportEnvironmentVariable));
            Assert.That(configuration.LocalHttpBaseUrl, Is.Empty, "a rejected route must not expose an endpoint");
            Assert.That(configuration.AutoStart, Is.False, "a rejected route must not auto-start");
        }

        // ------------------------------------------------------------------ F
        [TestCase("not a url")]
        [TestCase("ftp://127.0.0.1:8123")]
        [TestCase("file:///tmp/socket")]
        [TestCase("http://user:secret@127.0.0.1:8123")]
        [TestCase("http://127.0.0.1:8123/extra")]
        [TestCase("http://127.0.0.1:8123/?token=1")]
        [TestCase("http://10.0.0.5:8123")]
        [TestCase("")]
        [TestCase("http://127.0.0.1:0")]
        // Blocker 6: a managed URL must carry an explicit port. Uri.Port silently reports the
        // scheme default (80) for these, which must not be allowed to describe a real endpoint.
        [TestCase("http://127.0.0.1")]
        [TestCase("http://localhost")]
        [TestCase("http://[::1]")]
        [TestCase("http://127.0.0.1/")]
        // Blocker 6: bind-all and remote hosts are never valid for a managed route.
        [TestCase("http://0.0.0.0:8123")]
        [TestCase("http://[::]:8123")]
        [TestCase("http://192.168.1.5:8123")]
        // Blocker 6: the managed server is plain HTTP.
        [TestCase("https://127.0.0.1:8123")]
        public void F_InvalidHttpUrlOverride_FailsClosed(string value)
        {
            McpRouteInputs inputs = Stored();
            inputs.TransportEnvironmentValue = "http";
            inputs.HttpUrlEnvironmentValue = value;

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.IsValid, Is.False);
            Assert.That(configuration.ValidationError, Does.Contain(McpRouteConfiguration.HttpUrlEnvironmentVariable));
            Assert.That(configuration.LocalHttpBaseUrl, Is.Empty);
        }

        [Test]
        public void F_HttpUrlOverride_WithStdioTransport_IsContradictoryAndFailsClosed()
        {
            McpRouteInputs inputs = Stored(useHttp: false);
            inputs.TransportEnvironmentValue = "stdio";
            inputs.HttpUrlEnvironmentValue = "http://127.0.0.1:8123";

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.IsValid, Is.False);
            Assert.That(configuration.LocalHttpBaseUrl, Is.Empty);
        }

        [Test]
        public void F_BindAllUrl_IsRefusedForAManagedRouteEvenWithTheLegacyLanOptIn()
        {
            McpRouteInputs inputs = Stored(allowLanBind: false);
            inputs.TransportEnvironmentValue = "http";
            inputs.HttpUrlEnvironmentValue = "http://0.0.0.0:8123";

            Assert.That(McpRouteConfiguration.Resolve(inputs).IsValid, Is.False);

            // The legacy LAN opt-in must not be able to widen a managed process-scoped route.
            inputs.StoredAllowLanBind = true;
            McpRouteConfiguration stillRefused = McpRouteConfiguration.Resolve(inputs);
            Assert.That(stillRefused.IsValid, Is.False);
            Assert.That(stillRefused.LocalHttpBaseUrl, Is.Empty);

            // The legacy validator itself is unchanged: it is what a stored (unmanaged) value
            // still goes through, and it alone honours the opt-in.
            Assert.That(
                McpRouteConfiguration.TryValidateLocalHttpUrl(
                    "http://0.0.0.0:8123", allowLanBind: true, out string legacy, out _),
                Is.True);
            Assert.That(legacy, Is.EqualTo("http://0.0.0.0:8123"));
        }

        // ------------------------------------------------------------------ G
        [TestCase("true")]
        [TestCase("yes")]
        [TestCase("2")]
        [TestCase("-1")]
        [TestCase("")]
        public void G_InvalidAutoStartOverride_FailsClosed(string value)
        {
            McpRouteInputs inputs = Stored(autoStart: true);
            inputs.AutoStartEnvironmentValue = value;

            McpRouteConfiguration configuration = McpRouteConfiguration.Resolve(inputs);

            Assert.That(configuration.IsValid, Is.False);
            Assert.That(configuration.ValidationError, Does.Contain(McpRouteConfiguration.AutoStartEnvironmentVariable));
            Assert.That(configuration.AutoStart, Is.False);
        }

        // ------------------------------------------------------------------ H
        [Test]
        public void H_LaterConfigurationChanges_CannotRedirectAResolvedRoute()
        {
            var mutable = Stored(useHttp: false, url: "http://127.0.0.1:8080", autoStart: false);
            mutable.TransportEnvironmentValue = "http";
            mutable.HttpUrlEnvironmentValue = "http://127.0.0.1:8123";
            mutable.AutoStartEnvironmentValue = "1";

            var cache = new McpRouteConfigurationCache(() => mutable);
            McpRouteConfiguration first = cache.Get();

            // Simulate a later UI/EditorPrefs change (and even a changed override).
            mutable.StoredUseHttpTransport = false;
            mutable.StoredLocalHttpBaseUrl = "http://127.0.0.1:9999";
            mutable.StoredAutoStartOnLoad = false;
            mutable.TransportEnvironmentValue = null;
            mutable.HttpUrlEnvironmentValue = null;
            mutable.AutoStartEnvironmentValue = null;

            McpRouteConfiguration second = cache.Get();

            Assert.That(second, Is.SameAs(first), "the route must be resolved once per process");
            Assert.That(second.Transport, Is.EqualTo(McpTransportSelection.Http));
            Assert.That(second.LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8123"));
            Assert.That(second.AutoStart, Is.True);
        }

        [Test]
        public void H_AManagedRouteIgnoresTheEditorPrefsUrlEntirely()
        {
            var mutable = Stored(url: "http://127.0.0.1:8080");
            mutable.TransportEnvironmentValue = "http";
            mutable.HttpUrlEnvironmentValue = "http://127.0.0.1:8123";

            var cache = new McpRouteConfigurationCache(() => mutable);
            Assert.That(cache.Get().LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8123"));

            mutable.StoredLocalHttpBaseUrl = "http://127.0.0.1:12345";
            Assert.That(cache.Get().LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8123"));
        }

        // ------------------------------------------------------------------ I
        [Test]
        public void I_TwoSimulatedEditors_ResolveDistinctEndpoints()
        {
            static McpRouteInputs Editor(string url)
            {
                var inputs = Stored();
                inputs.TransportEnvironmentValue = "http";
                inputs.HttpUrlEnvironmentValue = url;
                inputs.AutoStartEnvironmentValue = "1";
                return inputs;
            }

            var main = new McpRouteConfigurationCache(() => Editor("http://127.0.0.1:8100"));
            var worker1 = new McpRouteConfigurationCache(() => Editor("http://127.0.0.1:8101"));
            var worker2 = new McpRouteConfigurationCache(() => Editor("http://127.0.0.1:8102"));

            Assert.That(main.Get().LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8100"));
            Assert.That(worker1.Get().LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8101"));
            Assert.That(worker2.Get().LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8102"));

            Assert.That(main.Get().LocalHttpBaseUrl, Is.Not.EqualTo(worker1.Get().LocalHttpBaseUrl));
            Assert.That(worker1.Get().LocalHttpBaseUrl, Is.Not.EqualTo(worker2.Get().LocalHttpBaseUrl));
            Assert.That(main.Get().IsManagedRoute, Is.True);
        }

        [Test]
        public void I_EachEditorResolvesItsOwnPortFromTheSameMachineState()
        {
            var mutable = Stored(url: "http://127.0.0.1:8080");
            var worker = new McpRouteConfigurationCache(() => mutable);

            mutable.TransportEnvironmentValue = "http";
            mutable.HttpUrlEnvironmentValue = "http://127.0.0.1:8103";
            Assert.That(worker.Get().LocalHttpBaseUrl, Is.EqualTo("http://127.0.0.1:8103"));
        }

        [Test]
        public void I_ManagedRouteRefusesAGlobalServerSourceOverride()
        {
            // The project-root/nonce guard lives in the approved server build, so a managed
            // route must not be launched against an arbitrary --from source.
            Assert.That(
                McpRouteConfiguration.IsServerSourceOverrideAllowed(
                    isManagedRoute: true, sourceOverride: "/tmp/my-fork/Server"),
                Is.False);
            Assert.That(
                McpRouteConfiguration.IsServerSourceOverrideAllowed(
                    isManagedRoute: true, sourceOverride: "   "),
                Is.True);
            Assert.That(
                McpRouteConfiguration.IsServerSourceOverrideAllowed(
                    isManagedRoute: false, sourceOverride: "/tmp/my-fork/Server"),
                Is.True,
                "unmanaged routes keep the existing developer override");
        }
    }
}
