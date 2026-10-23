using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MCPForUnity.Editor.Services.Route;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// R1-02 (MAJOR / security): the command line must come from the trusted in-box Windows CIM
    /// provider, never from a module found through a user-controlled search path.
    ///
    /// The fixture builds a harmless fake <c>CimCmdlets</c> binary module whose <c>Get-CimInstance</c>
    /// returns an invented, valid-looking launch command line (and records that it ran). It reproduces
    /// the independent reviewer's exploit against the R1 script as a control, then shows the production
    /// query is not fooled by a poisoned module path, a shadowing function or alias, a missing module,
    /// or a module with the wrong identity. Nothing is installed anywhere: the fake lives in a scratch
    /// directory that only these tests put on a child's module path.
    /// </summary>
    [TestFixture]
    [Platform("Win")]
    [NonParallelizable]
    public class McpWin01TrustTests
    {
        private string _scratch;
        private string _poisonedModules;
        private string _wrongGuidModule;
        private string _spoofedGuidModule;
        private string _markerFile;

        private static readonly string ForgedCommandLine =
            Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);

        // The R1 query as the vulnerable control: it names Get-CimInstance unqualified and trusts whatever
        // that resolves to. Only the output framing is brought up to date so today's parser can read it.
        private static string LegacyR1Script(int pid)
            => "$ErrorActionPreference='Stop';"
               + "$p=Get-CimInstance -ClassName Win32_Process -Filter 'ProcessId=" + pid + "';"
               + "if($null -eq $p){'NOTFOUND'}"
               + "elseif([string]::IsNullOrEmpty($p.CommandLine)){'NULL'}"
               + "else{$b=[Text.Encoding]::UTF8.GetBytes([string]$p.CommandLine);'OK:'+$b.Length+':'+[Convert]::ToBase64String($b)}";

        private static int OwnPid => Process.GetCurrentProcess().Id;

        [OneTimeSetUp]
        public void BuildTheFakeProvider()
        {
            string powerShell = WindowsProcessCommandLine.ResolvePowerShellPath();
            Assume.That(powerShell != null && File.Exists(powerShell), "Windows PowerShell is required.");

            _scratch = Path.Combine(Path.GetTempPath(), "mcp-win01-trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            _markerFile = Path.Combine(_scratch, "fake-provider-ran.marker");

            string source = @"
using System.IO;
using System.Management.Automation;

namespace FakeCim
{
    public sealed class FakeProc
    {
        public string CommandLine { get; set; }
    }

    [Cmdlet(""Get"", ""CimInstance"")]
    public sealed class GetFakeCimInstance : PSCmdlet
    {
        [Parameter] public string ClassName { get; set; }
        [Parameter] public string Filter { get; set; }

        protected override void ProcessRecord()
        {
            File.AppendAllText(System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(""" + B64(_markerFile) + @""")), ""ran\n"");
            WriteObject(new FakeProc
            {
                CommandLine = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(""" + B64(ForgedCommandLine) + @"""))
            });
        }
    }
}";
            string sourceFile = Path.Combine(_scratch, "FakeCim.cs");
            File.WriteAllText(sourceFile, source, new UTF8Encoding(false));

            string dll = Path.Combine(_scratch, "built", "CimCmdlets.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dll));
            RunPowerShell(
                powerShell,
                "Add-Type -Path '" + sourceFile + "' -ReferencedAssemblies System.Management.Automation "
                + "-OutputAssembly '" + dll + "' -OutputType Library");
            Assume.That(File.Exists(dll), "the fake provider could not be compiled");

            // A) the reviewer's exploit layout: a binary module folder named CimCmdlets, first on the path.
            _poisonedModules = Path.Combine(_scratch, "poisoned");
            Directory.CreateDirectory(Path.Combine(_poisonedModules, "CimCmdlets"));
            File.Copy(dll, Path.Combine(_poisonedModules, "CimCmdlets", "CimCmdlets.dll"));

            // B) a manifest with a different identity (wrong GUID).
            _wrongGuidModule = MakeManifestModule("wrongguid", "11111111-2222-3333-4444-555555555555", dll);

            // C) a manifest that spoofs the real GUID but is not under the Windows directory.
            _spoofedGuidModule = MakeManifestModule("spoofedguid", "fb6cc51d-c096-4b38-b78d-0fed6277096a", dll);
        }

        [OneTimeTearDown]
        public void RemoveTheScratchFiles()
        {
            try
            {
                if (_scratch != null && Directory.Exists(_scratch))
                {
                    Directory.Delete(_scratch, recursive: true);
                }
            }
            catch
            {
                // Scratch only.
            }
        }

        private string MakeManifestModule(string name, string guid, string dll)
        {
            string folder = Path.Combine(_scratch, name, "CimCmdlets");
            Directory.CreateDirectory(folder);
            File.Copy(dll, Path.Combine(folder, "CimCmdlets.dll"));
            File.WriteAllText(
                Path.Combine(folder, "CimCmdlets.psd1"),
                "@{ RootModule = 'CimCmdlets.dll'; ModuleVersion = '1.0.0.0'; GUID = '" + guid + "'; "
                + "CmdletsToExport = @('Get-CimInstance') }",
                new UTF8Encoding(false));
            return folder;
        }

        private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        private static void RunPowerShell(string powerShell, string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = powerShell,
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
                            + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
            }
        }

        private string PoisonedModulePath()
            => _poisonedModules + ";" + Environment.GetEnvironmentVariable("PSModulePath");

        private static Dictionary<string, string> Env(string psModulePath)
            => new Dictionary<string, string> { ["PSModulePath"] = psModulePath };

        /// <summary>Runs an arbitrary script text through the production runner and the production parser.</summary>
        private static WindowsCommandLineResult RunScript(string script, IReadOnlyDictionary<string, string> environment)
        {
            WindowsCommandRunOutcome outcome = WindowsProcessCommandLine.BoundedProcessRunner.Instance.Run(
                new WindowsCommandRunRequest
                {
                    ExecutablePath = WindowsProcessCommandLine.ResolvePowerShellPath(),
                    Arguments = WindowsProcessCommandLine.BuildArguments(script),
                    TimeoutMs = 30000,
                    Environment = environment,
                },
                CancellationToken.None);
            return WindowsProcessCommandLine.ParseOutcome(outcome);
        }

        private void ResetMarker()
        {
            if (File.Exists(_markerFile))
            {
                File.Delete(_markerFile);
            }
        }

        // ---- the exploit, reproduced ------------------------------------------------------

        [Test]
        public void Control_TheR1Script_IsSubstitutedByAMaliciousModulePath()
        {
            ResetMarker();

            WindowsCommandLineResult result = RunScript(LegacyR1Script(OwnPid), Env(PoisonedModulePath()));

            // This is the reviewer's finding, reproduced: the forged line is accepted as OS metadata.
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), result.Detail);
            Assert.That(result.CommandLine, Is.EqualTo(ForgedCommandLine));
            Assert.That(File.Exists(_markerFile), Is.True, "the fake provider's code ran");
        }

        [Test]
        public void Control_ThatForgedLineIsAPerfectLaunchIdentity_SoOnlyProviderTrustCanStopIt()
        {
            // The forged line carries the right transport, endpoint, root, pid file and nonce, so the
            // argument check alone would accept it. This is why the provider itself must be trusted.
            var expected = new McpExpectedLaunch
            {
                InstanceToken = Win01.Token,
                ProjectRoot = McpRunStatePaths.Canonicalize(Win01.Worker04Root),
                Endpoint = Win01.Endpoint,
                PidFilePath = McpRunStatePaths.GetPidFilePath(McpRunStatePaths.Canonicalize(Win01.Worker04Root), Win01.Port),
            };

            Assert.That(
                McpLaunchArguments.Verify(ForgedCommandLine, McpCommandLineFormat.WindowsQuoted, expected).Verified,
                Is.True);
        }

        // ---- the production query is not fooled -------------------------------------------

        [Test]
        public void ProductionQuery_IgnoresAMaliciousInheritedModulePath()
        {
            ResetMarker();
            string original = Environment.GetEnvironmentVariable("PSModulePath");
            try
            {
                // The test process itself carries the poisoned path, as an inherited environment would.
                Environment.SetEnvironmentVariable("PSModulePath", PoisonedModulePath());

                WindowsCommandLineResult result = WindowsProcessCommandLine.Query(OwnPid);

                Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), result.Detail);
                Assert.That(result.CommandLine, Is.Not.EqualTo(ForgedCommandLine));
                Assert.That(result.CommandLine, Does.Not.Contain(Win01.Token));
                Assert.That(
                    result.CommandLine,
                    Does.Contain(Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName)).IgnoreCase,
                    "the genuine OS command line of this test process");
                Assert.That(File.Exists(_markerFile), Is.False, "the fake provider never ran");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PSModulePath", original);
            }
        }

        [Test]
        public void ProductionScript_IsNotFooledEvenIfTheChildInheritsThePoisonedPath()
        {
            // Defence in depth: bypass the production environment override and hand the child the
            // poisoned path anyway. The explicit absolute import must still win.
            ResetMarker();

            WindowsCommandLineResult result = RunScript(
                WindowsProcessCommandLine.BuildQueryScript(OwnPid),
                Env(PoisonedModulePath()));

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), result.Detail);
            Assert.That(result.CommandLine, Is.Not.EqualTo(ForgedCommandLine));
            Assert.That(File.Exists(_markerFile), Is.False, "the fake provider never ran");
        }

        [Test]
        public void AShadowingFunctionAndAlias_AreNotUsed()
        {
            ResetMarker();
            string shadows =
                "function Get-CimInstance { [pscustomobject]@{ CommandLine = 'SHADOWED forged line' } };"
                + "function Import-Module { };"
                + "Set-Alias -Name Get-CimInstance -Value Write-Output -Force -Scope Global -Option AllScope;"
                + "Set-Alias -Name Import-Module -Value Write-Output -Force -Scope Global -Option AllScope;";

            WindowsCommandLineResult result = RunScript(
                shadows + WindowsProcessCommandLine.BuildQueryScript(OwnPid),
                Env(Path.Combine(WindowsProcessCommandLine.ResolvePowerShellHome(), "Modules")));

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), result.Detail);
            Assert.That(result.CommandLine, Does.Not.Contain("SHADOWED"));
            Assert.That(
                result.CommandLine,
                Does.Contain(Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName)).IgnoreCase);
        }

        [Test]
        public void AForgedValidLookingCommandLine_NeverAuthorizesOwnership()
        {
            ResetMarker();
            string original = Environment.GetEnvironmentVariable("PSModulePath");
            try
            {
                Environment.SetEnvironmentVariable("PSModulePath", PoisonedModulePath());

                // R2: the hardened query supplies the evidence for the "server" (really this test host).
                var hardened = new ChainHarness((pid, token) => WindowsProcessCommandLine.Query(
                    OwnPid, null, null, WindowsProcessCommandLine.DefaultTimeoutMs, token));
                McpRunStateRecord running = hardened.RunningRecord();
                hardened.Observe(running);
                hardened.Settle();
                McpOwnershipObservation observation = hardened.Observe(running);

                Assert.That(observation.ServerCommandLineAvailable, Is.True);
                Assert.That(observation.ServerCommandLine, Is.Not.EqualTo(ForgedCommandLine));
                McpOwnershipDecision decision = McpOwnershipEvaluator.EvaluateStop(running, observation);
                Assert.That(decision.Allowed, Is.False, "forged metadata must never authorize ownership");
                Assert.That(
                    McpManagedConnectionGate.TryGetLaunchToken(running, observation, out string token, out _, out _),
                    Is.False);
                Assert.That(token, Is.Null);
                Assert.That(File.Exists(_markerFile), Is.False);

                // Control (R1 script, same poisoned environment): the forged line DOES authorize.
                var vulnerable = new ChainHarness((pid, tok) => RunScript(
                    LegacyR1Script(OwnPid), Env(PoisonedModulePath())));
                vulnerable.Observe(running);
                vulnerable.Settle();
                Assert.That(
                    McpOwnershipEvaluator.EvaluateStop(running, vulnerable.Observe(running)).Allowed,
                    Is.True,
                    "control: without provider trust a forged line grants ownership");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PSModulePath", original);
            }
        }

        [Test]
        public void AMissingTrustedModule_FailsClosed()
        {
            WindowsCommandLineResult result = WindowsProcessCommandLine.QueryWithScript(
                OwnPid,
                runner: null,
                powerShellPath: null,
                timeoutMs: 30000,
                cancellationToken: CancellationToken.None,
                hostRootOverride: null,
                moduleBaseOverride: Path.Combine(_scratch, "does-not-exist", "CimCmdlets"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.CommandLine, Is.Null);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.UntrustedProvider));
            Assert.That(result.Detail, Does.Contain("unavailable"));
        }

        [Test]
        public void AModuleWithTheWrongIdentity_FailsClosed_WithoutRunningIt()
        {
            ResetMarker();

            WindowsCommandLineResult result = WindowsProcessCommandLine.QueryWithScript(
                OwnPid, null, null, 30000, CancellationToken.None, hostRootOverride: null, moduleBaseOverride: _wrongGuidModule);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.UntrustedProvider));
            Assert.That(result.Detail, Does.Contain("module"));
            Assert.That(File.Exists(_markerFile), Is.False);
        }

        [Test]
        public void AModuleThatSpoofsTheGuidButLivesOutsideTheWindowsDirectory_FailsClosed()
        {
            ResetMarker();

            WindowsCommandLineResult result = WindowsProcessCommandLine.QueryWithScript(
                OwnPid, null, null, 30000, CancellationToken.None, hostRootOverride: null, moduleBaseOverride: _spoofedGuidModule);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.UntrustedProvider));
            Assert.That(result.Detail, Does.Contain("cmdlet"));
            Assert.That(File.Exists(_markerFile), Is.False, "the spoofed cmdlet was refused before it could run");
        }

        [Test]
        public void AHostThatIsNotTheSystemWindowsPowerShell_FailsClosed()
        {
            WindowsCommandLineResult result = WindowsProcessCommandLine.QueryWithScript(
                OwnPid, null, null, 30000, CancellationToken.None, hostRootOverride: _scratch, moduleBaseOverride: null);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.UntrustedProvider));
            Assert.That(result.Detail, Does.Contain("host"));
        }

        [Test]
        public void ALegitimateTrustedQuery_ReturnsTheRealCommandLine()
        {
            WindowsCommandLineResult result = WindowsProcessCommandLine.Query(OwnPid);

            Assert.That(result.Status, Is.EqualTo(WindowsCommandLineStatus.Ok), result.Detail);
            Assert.That(
                result.CommandLine,
                Does.Contain(Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName)).IgnoreCase);
        }

        // ---- static properties of the query -----------------------------------------------

        [Test]
        public void TheChildModuleSearchPath_IsReplacedByTheSystemModulesOnly()
        {
            var runner = new ScriptedRunner(Win01.Ok("tool.exe --x"));

            WindowsProcessCommandLine.Query(77, runner);

            string expected = Path.Combine(WindowsProcessCommandLine.ResolvePowerShellHome(), "Modules");
            Assert.That(runner.LastRequest.Environment, Is.Not.Null);
            Assert.That(runner.LastRequest.Environment["PSModulePath"], Is.EqualTo(expected));
            Assert.That(runner.LastRequest.Environment["PSModulePath"], Does.Not.Contain(";"));
        }

        [Test]
        public void TheScript_ImportsByAbsolutePath_InvokesTheVerifiedCmdletObject_AndUsesNoWmic()
        {
            string script = WindowsProcessCommandLine.BuildQueryScript(4242);

            Assert.That(script, Does.Contain("Microsoft.PowerShell.Core\\Import-Module -Name"), "qualified, by absolute path");
            Assert.That(script, Does.Contain("& $c -ClassName Win32_Process"), "the verified CmdletInfo is invoked, not a name");
            Assert.That(script, Does.Not.Contain("Get-CimInstance -"), "the cmdlet name is never resolved by lookup");
            Assert.That(script, Does.Contain("fb6cc51d-c096-4b38-b78d-0fed6277096a"), "the module GUID is pinned");
            Assert.That(script, Does.Contain("ProcessId=4242"));
            Assert.That(script, Does.Not.Contain("\""));
            Assert.That(script, Does.Not.Contain("wmic").IgnoreCase);
            Assert.That(script, Does.Not.Contain("Invoke-Expression").IgnoreCase);
        }

        [Test]
        public void NoPersistentStateIsChanged()
        {
            string before = Environment.GetEnvironmentVariable("PSModulePath");

            WindowsProcessCommandLine.Query(OwnPid);

            Assert.That(Environment.GetEnvironmentVariable("PSModulePath"), Is.EqualTo(before));
        }
    }
}
