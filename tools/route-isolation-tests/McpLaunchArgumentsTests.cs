using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Services.Route;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnity.RouteIsolation.Tests
{
    /// <summary>
    /// R1-04 / MAJOR-03: the ownership proof must validate the server's actual launch ARGUMENTS, not search
    /// the command line for the nonce. A valid token substring alone must never pass.
    ///
    /// Every case is decided at all three production decision points - the running-server stop/"is it
    /// ours" evaluation, the starting -> running promotion and the nonce-release connection gate - with a
    /// record, listener, PID file and lifetime that are otherwise perfectly coherent, so only the command
    /// line differs from a legitimate launch.
    /// </summary>
    [TestFixture]
    public class McpLaunchArgumentsTests
    {
        // ---- builders ---------------------------------------------------------------------

        private static string Quote(string value)
            => value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;

        /// <summary>The command line of a legitimate managed launch, as fragments a test can replace.</summary>
        private sealed class Launch
        {
            public string Program = "uvx mcp-for-unity";
            public string Transport = "--transport http";
            public string Url;
            public string Root;
            public string PidFile;
            public string Token;
            public string Extra = string.Empty;

            public static Launch For(OwnershipFixture f)
            {
                string root = McpRunStatePaths.Canonicalize(f.ProjectRoot);
                string pidFile = McpRunStatePaths.GetPidFilePath(root, new Uri(f.Endpoint).Port);
                return new Launch
                {
                    Url = "--http-url " + f.Endpoint,
                    Root = "--unity-project-root " + Quote(root),
                    PidFile = "--pidfile " + Quote(pidFile),
                    Token = "--unity-instance-token " + f.Nonce,
                };
            }

            public string Build()
                => string.Join(
                    " ",
                    new[] { Program, Transport, Url, Root, PidFile, Token, Extra }.Where(p => !string.IsNullOrEmpty(p)));
        }

        private static OwnershipFixture Fixture(string root = null)
            => root == null ? new OwnershipFixture() : new OwnershipFixture { ProjectRoot = root };

        private sealed class Decisions
        {
            public McpOwnershipDecision Stop;
            public bool Promoted;
            public string PromotionDetail;
            public bool GateReleased;
            public string Token;

            public bool AllAccept => Stop.Allowed && Promoted && GateReleased;

            public bool AllDeny => !Stop.Allowed && !Promoted && !GateReleased && Token == null;
        }

        private static Decisions Decide(
            OwnershipFixture f,
            string commandLine,
            McpCommandLineFormat format = McpCommandLineFormat.WindowsQuoted,
            Action<McpOwnershipObservation> perturb = null)
        {
            var d = new Decisions();

            McpRunStateRecord running = f.BuildRecord();
            McpOwnershipObservation runningObservation = f.BuildObservation();
            runningObservation.ServerCommandLine = commandLine;
            runningObservation.ServerCommandLineFormat = format;
            perturb?.Invoke(runningObservation);
            d.Stop = McpOwnershipEvaluator.EvaluateStop(running, runningObservation);
            d.GateReleased = McpManagedConnectionGate.TryGetLaunchToken(
                running, runningObservation, out d.Token, out _, out _);

            var pendingFixture = new OwnershipFixture
            {
                ProjectRoot = f.ProjectRoot,
                Endpoint = f.Endpoint,
                Nonce = f.Nonce,
                LifecycleState = McpRunStateRecord.LifecycleStarting,
            };
            McpOwnershipObservation pendingObservation = pendingFixture.BuildObservation();
            pendingObservation.ServerCommandLine = commandLine;
            pendingObservation.ServerCommandLineFormat = format;
            perturb?.Invoke(pendingObservation);
            d.Promoted = McpOwnershipEvaluator.TryIdentifyPendingServer(
                pendingFixture.BuildRecord(), pendingObservation, out _, out d.PromotionDetail);

            return d;
        }

        private static void AssertAccepted(OwnershipFixture f, string line, McpCommandLineFormat format = McpCommandLineFormat.WindowsQuoted)
        {
            Decisions d = Decide(f, line, format);
            Assert.That(d.Stop.Allowed, Is.True, d.Stop.Reason + ": " + d.Stop.Detail);
            Assert.That(d.Promoted, Is.True, d.PromotionDetail);
            Assert.That(d.GateReleased, Is.True);
            Assert.That(d.Token, Is.EqualTo(f.Nonce));
        }

        private static Decisions AssertDenied(
            OwnershipFixture f,
            string line,
            McpCommandLineFormat format = McpCommandLineFormat.WindowsQuoted)
        {
            Decisions d = Decide(f, line, format);
            Assert.That(d.Stop.Allowed, Is.False, "running evaluation must deny: " + line);
            Assert.That(d.Promoted, Is.False, "promotion must deny: " + line);
            Assert.That(d.GateReleased, Is.False, "the nonce must not be released: " + line);
            Assert.That(d.Token, Is.Null);
            return d;
        }

        // ================================================================================
        // The fifteen required cases
        // ================================================================================

        [Test]
        public void C01_TheExactLegitimateNonce_IsAccepted()
        {
            OwnershipFixture f = Fixture();

            AssertAccepted(f, Launch.For(f).Build());
        }

        [Test]
        public void C02_ANoncePrefix_IsRejected()
        {
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);

            launch.Token = "--unity-instance-token " + f.Nonce.Substring(0, f.Nonce.Length - 1);
            AssertDenied(f, launch.Build());

            launch.Token = "--unity-instance-token " + f.Nonce.Substring(0, 8);
            AssertDenied(f, launch.Build());

            // The reverse: the real nonce is only a PREFIX of what the process carries.
            launch.Token = "--unity-instance-token " + f.Nonce + "0";
            AssertDenied(f, launch.Build());
        }

        [Test]
        public void C03_ANonceSuffix_IsRejected()
        {
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);

            launch.Token = "--unity-instance-token " + f.Nonce.Substring(1);
            AssertDenied(f, launch.Build());

            launch.Token = "--unity-instance-token 0" + f.Nonce;
            AssertDenied(f, launch.Build());

            launch.Token = "--unity-instance-token " + f.Nonce + "-extra";
            AssertDenied(f, launch.Build());
        }

        [Test]
        public void C04_ANonceEmbeddedInAnotherArgument_IsRejected()
        {
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);

            // The nonce is mentioned, but never as the token argument.
            launch.Token = "--comment before" + f.Nonce + "after";
            AssertDenied(f, launch.Build());

            launch.Token = "--unity-instance-token WRONG --note " + f.Nonce;
            AssertDenied(f, launch.Build());

            launch.Token = "--unity-instance-token WRONG";
            launch.Extra = "--label=" + f.Nonce;
            AssertDenied(f, launch.Build());
        }

        [Test]
        public void C05_ANonceEmbeddedInAFilesystemPath_IsRejected()
        {
            OwnershipFixture f = Fixture();
            string root = McpRunStatePaths.Canonicalize(f.ProjectRoot);

            var inRoot = Launch.For(f);
            inRoot.Root = "--unity-project-root " + Quote(root + "\\" + f.Nonce);
            inRoot.Token = "--unity-instance-token WRONG";
            AssertDenied(f, inRoot.Build());

            var inPidFile = Launch.For(f);
            inPidFile.PidFile = "--pidfile " + Quote(root + "\\Library\\MCPForUnity\\RunState\\" + f.Nonce + ".pid");
            inPidFile.Token = string.Empty;
            AssertDenied(f, inPidFile.Build());

            // The real flag is absent; the nonce only names a directory the process was started in.
            var inProgram = Launch.For(f);
            inProgram.Program = Quote("C:\\Tools With Space\\" + f.Nonce + "\\server.exe");
            inProgram.Token = string.Empty;
            AssertDenied(f, inProgram.Build());
        }

        [Test]
        public void C06_DuplicateTokenArguments_AreRejected()
        {
            OwnershipFixture f = Fixture();
            string right = "--unity-instance-token " + f.Nonce;
            string wrong = "--unity-instance-token f00dfeedf00dfeedf00dfeedf00dfeed";

            foreach (string tokens in new[]
                     {
                         right + " " + right,
                         right + " " + wrong,
                         wrong + " " + right,
                         right + " --unity-instance-token=" + f.Nonce,
                         right + " --unity-instance-tok " + f.Nonce,
                     })
            {
                var launch = Launch.For(f);
                launch.Token = tokens;
                AssertDenied(f, launch.Build());
            }
        }

        [Test]
        public void C07_AMissingTokenArgument_IsRejected()
        {
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);
            launch.Token = string.Empty;

            Decisions d = AssertDenied(f, launch.Build());
            Assert.That(d.Stop.Reason, Is.EqualTo(McpOwnershipDenyReason.NonceMismatch));
            Assert.That(d.PromotionDetail, Does.Contain("nonce"));
        }

        [Test]
        public void C08_AnEmptyToken_IsRejected()
        {
            OwnershipFixture f = Fixture();

            var emptyValue = Launch.For(f);
            emptyValue.Token = "--unity-instance-token \"\"";
            AssertDenied(f, emptyValue.Build());

            var noValue = Launch.For(f);
            noValue.Token = string.Empty;
            noValue.Extra = "--unity-instance-token";
            AssertDenied(f, noValue.Build());

            // And a record that itself carries no usable nonce can never be satisfied.
            var emptyNonce = new OwnershipFixture { Nonce = string.Empty };
            McpRunStateRecord record = emptyNonce.BuildRecord();
            McpOwnershipObservation observation = emptyNonce.BuildObservation();
            observation.ServerCommandLine = "uvx mcp-for-unity --transport http --unity-instance-token ";
            Assert.That(McpOwnershipEvaluator.EvaluateStop(record, observation).Allowed, Is.False);
        }

        [Test]
        public void C09_AnIncorrectToken_IsRejected()
        {
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);

            launch.Token = "--unity-instance-token f00dfeedf00dfeedf00dfeedf00dfeed";
            Decisions d = AssertDenied(f, launch.Build());
            Assert.That(d.Stop.Reason, Is.EqualTo(McpOwnershipDenyReason.NonceMismatch));

            // Same letters, different case: the server compares the nonce case-sensitively.
            launch.Token = "--unity-instance-token " + f.Nonce.ToUpperInvariant();
            AssertDenied(f, launch.Build());
        }

        [Test]
        public void C10_QuotedCommandLines_AreParsedAsTheServerReceivesThem()
        {
            OwnershipFixture f = Fixture();
            string root = McpRunStatePaths.Canonicalize(f.ProjectRoot);

            // A quoted argument is the same argument.
            var quotedToken = Launch.For(f);
            quotedToken.Token = "--unity-instance-token \"" + f.Nonce + "\"";
            AssertAccepted(f, quotedToken.Build());

            var quotedFlags = Launch.For(f);
            quotedFlags.Transport = "\"--transport\" \"http\"";
            quotedFlags.Token = "\"--unity-instance-token\" " + f.Nonce;
            AssertAccepted(f, quotedFlags.Build());

            var quotedProgram = Launch.For(f);
            quotedProgram.Program = "\"C:\\Program Files\\uv\\uvx.exe\" --from \"git+https://example.invalid/x.git\" mcp-for-unity";
            AssertAccepted(f, quotedProgram.Build());

            // But the "--flag=value" spelling is not what a managed launch emits.
            var equalsForm = Launch.For(f);
            equalsForm.Token = "--unity-instance-token=" + f.Nonce;
            AssertDenied(f, equalsForm.Build());

            // A quote that glues the nonce to something else is a different argument.
            var glued = Launch.For(f);
            glued.Token = "--unity-instance-token \"" + f.Nonce + " extra\"";
            AssertDenied(f, glued.Build());

            // An unbalanced quote swallows the rest of the line into one argument.
            var unbalanced = Launch.For(f);
            unbalanced.Root = "--unity-project-root \"" + root;
            AssertDenied(f, unbalanced.Build());
        }

        [Test]
        public void C11_PathsWithSpaces_MatchOnlyWhenQuotedAsTheyWereLaunched()
        {
            OwnershipFixture f = Fixture(); // the fixture root is "C:\Projects\The Last Noob"
            Assert.That(f.ProjectRoot, Does.Contain(" "));

            AssertAccepted(f, Launch.For(f).Build());

            // Unquoted, Windows hands the server THREE arguments, so the server's root is only "C:\Projects\The".
            string root = McpRunStatePaths.Canonicalize(f.ProjectRoot);
            var unquoted = Launch.For(f);
            unquoted.Root = "--unity-project-root " + root;
            AssertDenied(f, unquoted.Build());

            // A longer path that merely starts with the expected one.
            var longer = Launch.For(f);
            longer.Root = "--unity-project-root \"" + root + " Copy\"";
            AssertDenied(f, longer.Build());
        }

        [Test]
        public void C12_UnicodePaths_AreMatchedExactly()
        {
            OwnershipFixture f = Fixture(@"C:\Projects\Ünï cödé 日本語\Wörker 04 🚀");

            AssertAccepted(f, Launch.For(f).Build());

            var other = Launch.For(f);
            other.Root = "--unity-project-root \"C:\\Projects\\Ünï cödé 日本語\\Wörker 05 🚀\"";
            AssertDenied(f, other.Build());

            var lookalike = Launch.For(f);
            lookalike.Root = "--unity-project-root \"C:\\Projects\\Unï cödé 日本語\\Wörker 04 🚀\"";
            AssertDenied(f, lookalike.Build());
        }

        [Test]
        public void C13_ACorrectToken_WithTheWrongProjectRoot_IsRejected()
        {
            OwnershipFixture f = Fixture();

            var elsewhere = Launch.For(f);
            elsewhere.Root = "--unity-project-root \"C:\\Projects\\Workers\\WORKER-01\"";
            Decisions d = AssertDenied(f, elsewhere.Build());
            Assert.That(d.Stop.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignProject));

            var missing = Launch.For(f);
            missing.Root = string.Empty;
            AssertDenied(f, missing.Build());

            var relative = Launch.For(f);
            relative.Root = "--unity-project-root ..\\The-Last-Noob";
            AssertDenied(f, relative.Build());

            var twice = Launch.For(f);
            twice.Extra = "--unity-project-root \"C:\\Projects\\Workers\\WORKER-01\"";
            AssertDenied(f, twice.Build());
        }

        [Test]
        public void C14_ACorrectToken_WithTheWrongPort_IsRejected()
        {
            OwnershipFixture f = Fixture();

            var otherPort = Launch.For(f);
            otherPort.Url = "--http-url http://127.0.0.1:8102";
            Decisions d = AssertDenied(f, otherPort.Build());
            Assert.That(d.Stop.Reason, Is.EqualTo(McpOwnershipDenyReason.ForeignEndpoint));

            var otherHost = Launch.For(f);
            otherHost.Url = "--http-url http://localhost:8101";
            AssertDenied(f, otherHost.Build());

            var missing = Launch.For(f);
            missing.Url = string.Empty;
            AssertDenied(f, missing.Build());

            var prefixPort = Launch.For(f);
            prefixPort.Url = "--http-url http://127.0.0.1:81010";
            AssertDenied(f, prefixPort.Build());

            // The spelling of the same endpoint with a trailing slash is the same endpoint.
            var slash = Launch.For(f);
            slash.Url = "--http-url " + f.Endpoint + "/";
            AssertAccepted(f, slash.Build());
        }

        [Test]
        public void C15_ACorrectToken_WithAReusedPid_IsRejected()
        {
            OwnershipFixture f = Fixture();
            string line = Launch.For(f).Build();

            // A DIFFERENT process (different creation instant) now holds the recorded server PID.
            Decisions reused = Decide(
                f,
                line,
                perturb: o => o.ServerProcessStartUtc = OwnershipFixture.DefaultServerStart.AddSeconds(90));
            Assert.That(reused.Stop.Allowed, Is.False);
            Assert.That(reused.Stop.Reason, Is.EqualTo(McpOwnershipDenyReason.PidReuse));
            Assert.That(reused.GateReleased, Is.False);

            // For a pending record: the process predates the record that supposedly launched it.
            Decisions predates = Decide(
                f,
                line,
                perturb: o => o.ServerProcessStartUtc = OwnershipFixture.DefaultWrittenAt.AddMinutes(-5));
            Assert.That(predates.Promoted, Is.False);
            Assert.That(predates.PromotionDetail, Does.Contain("PID reuse"));
        }

        // ================================================================================
        // The reviewer's two counterexamples, and the other identity flags
        // ================================================================================

        [Test]
        public void ReviewerCounterexample1_AForeignProcessCarryingTheNonceInAComment_IsRejected()
        {
            OwnershipFixture f = Fixture();

            AssertDenied(f, "foreign.exe --comment " + f.Nonce);
            AssertDenied(f, "foreign.exe --unity-instance-token " + f.Nonce);
            AssertDenied(f, "python.exe -m http.server 8101 # " + f.Nonce);
        }

        [Test]
        public void ReviewerCounterexample2_ACommandNamingAForeignProjectOrEndpoint_IsRejected()
        {
            OwnershipFixture f = Fixture();

            var foreignProject = Launch.For(f);
            foreignProject.Root = "--unity-project-root \"C:\\Projects\\Workers\\WORKER-02\"";
            AssertDenied(f, foreignProject.Build());

            var foreignEndpoint = Launch.For(f);
            foreignEndpoint.Url = "--http-url http://127.0.0.1:9999";
            AssertDenied(f, foreignEndpoint.Build());
        }

        [Test]
        public void TheTransport_AndThePidFile_AreIdentityToo()
        {
            OwnershipFixture f = Fixture();

            var stdio = Launch.For(f);
            stdio.Transport = "--transport stdio";
            AssertDenied(f, stdio.Build());

            var noTransport = Launch.For(f);
            noTransport.Transport = string.Empty;
            AssertDenied(f, noTransport.Build());

            var twoTransports = Launch.For(f);
            twoTransports.Transport = "--transport http --transport http";
            AssertDenied(f, twoTransports.Build());

            var otherPidFile = Launch.For(f);
            otherPidFile.PidFile = "--pidfile \"C:\\Projects\\The Last Noob\\Library\\MCPForUnity\\RunState\\mcp_http_9999.pid\"";
            Decisions d = AssertDenied(f, otherPidFile.Build());
            Assert.That(d.Stop.Reason, Is.EqualTo(McpOwnershipDenyReason.PidFileMismatch));

            var noPidFile = Launch.For(f);
            noPidFile.PidFile = string.Empty;
            AssertDenied(f, noPidFile.Build());

            var relativePidFile = Launch.For(f);
            relativePidFile.PidFile = "--pidfile mcp_http_8101.pid";
            AssertDenied(f, relativePidFile.Build());
        }

        [Test]
        public void AbbreviatedIdentityFlags_NeverCountAsTheRealOne()
        {
            OwnershipFixture f = Fixture();

            var abbreviated = Launch.For(f);
            abbreviated.Token = "--unity-instance " + f.Nonce;
            AssertDenied(f, abbreviated.Build());

            var abbreviatedRoot = Launch.For(f);
            abbreviatedRoot.Root = "--unity-project " + Quote(McpRunStatePaths.Canonicalize(f.ProjectRoot));
            AssertDenied(f, abbreviatedRoot.Build());
        }

        [Test]
        public void TheOldNormalizedForm_CanNoLongerSatisfyTheProof()
        {
            OwnershipFixture f = Fixture();
            string line = Launch.For(f).Build();
            var detector = new ProcessDetector(new ScriptedRunner(Win01.Ok("x")));

            // Lower-cased and whitespace-stripped, as ProcessDetector.TryGetProcessCommandLine still returns it.
            string normalized = detector.NormalizeForMatch(line);
            Assert.That(normalized, Does.Contain(f.Nonce), "the nonce text is still in the string ...");

            AssertDenied(f, normalized); // ... but the argument structure is gone, so it proves nothing
        }

        [Test]
        public void ExtraUnrelatedArguments_AreAllowed_WhenTheIdentityIsExact()
        {
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);
            launch.Extra = "--project-scoped-tools --some-future-flag value";

            AssertAccepted(f, launch.Build());
        }

        [Test]
        public void ARealWorkerStyleCommandLine_IsAccepted()
        {
            // The shape of a managed Windows launch: python.exe running the console script.
            var f = new OwnershipFixture
            {
                ProjectRoot = Win01.Worker04Root,
                Endpoint = Win01.Endpoint,
                Nonce = Win01.Token,
            };
            string line = Win01.ServerCommandLine(Win01.Worker04Root, Win01.Endpoint, Win01.Token);

            AssertAccepted(f, line);
        }

        // ================================================================================
        // POSIX (ps) command lines: quoting is lost, so values are matched as runs of words
        // ================================================================================

        [Test]
        public void Posix_AFlatCommandLine_IsMatchedWordForWord()
        {
            var f = new OwnershipFixture { ProjectRoot = "/Users/dev/My Project" };
            string root = McpRunStatePaths.Canonicalize(f.ProjectRoot);
            string pidFile = McpRunStatePaths.GetPidFilePath(root, 8101);
            string flat = $"uvx mcp-for-unity --transport http --http-url {f.Endpoint} --unity-project-root {root} "
                          + $"--pidfile {pidFile} --unity-instance-token {f.Nonce}";

            AssertAccepted(f, flat, McpCommandLineFormat.PosixFlat);

            // Trailing words after the project path are not part of it.
            AssertDenied(f, flat.Replace("--pidfile", "Copy --pidfile"), McpCommandLineFormat.PosixFlat);

            // A shorter path is not the project.
            AssertDenied(f, flat.Replace(root, "/Users/dev/My"), McpCommandLineFormat.PosixFlat);

            // The nonce rules are identical on every platform.
            AssertDenied(f, flat.Replace("--unity-instance-token", "--note"), McpCommandLineFormat.PosixFlat);
            AssertDenied(f, flat + " --unity-instance-token " + f.Nonce, McpCommandLineFormat.PosixFlat);
        }

        // ================================================================================
        // The tokenizer itself
        // ================================================================================

        private static IEnumerable<TestCaseData> WindowsTokenization()
        {
            yield return new TestCaseData("prog a b c", new[] { "prog", "a", "b", "c" }).SetName("plain");
            yield return new TestCaseData("prog\ta\t\tb", new[] { "prog", "a", "b" }).SetName("tabs_separate");
            yield return new TestCaseData("  prog   a  ", new[] { "prog", "a" }).SetName("outer_blanks");
            yield return new TestCaseData("\"C:\\Program Files\\x.exe\" --a \"b c\"", new[] { "C:\\Program Files\\x.exe", "--a", "b c" })
                .SetName("quoted_program_and_argument");
            yield return new TestCaseData("prog \"a\\\"b\"", new[] { "prog", "a\"b" }).SetName("escaped_quote");
            yield return new TestCaseData("prog a\\\\b", new[] { "prog", "a\\\\b" }).SetName("backslashes_not_before_quote");
            yield return new TestCaseData("prog \"a\\\\\" b", new[] { "prog", "a\\", "b" }).SetName("even_backslashes_then_quote_closes");
            yield return new TestCaseData("prog \"a\\\\\\\"b\"", new[] { "prog", "a\\\"b" }).SetName("odd_backslashes_then_literal_quote");
            yield return new TestCaseData("prog \"a \"\"b\"\" c\"", new[] { "prog", "a \"b\" c" }).SetName("doubled_quote_in_quotes");
            yield return new TestCaseData("prog a\"b c\"d", new[] { "prog", "ab cd" }).SetName("quote_inside_a_word");
            yield return new TestCaseData("prog \"\" x", new[] { "prog", "", "x" }).SetName("empty_argument");
            yield return new TestCaseData("\"prog with space\"", new[] { "prog with space" }).SetName("program_only");
            yield return new TestCaseData("", new string[0]).SetName("empty");
            yield return new TestCaseData("   ", new string[0]).SetName("blank");
            yield return new TestCaseData("prog \"unterminated arg", new[] { "prog", "unterminated arg" }).SetName("unterminated_quote_takes_the_rest");
        }

        [TestCaseSource(nameof(WindowsTokenization))]
        public void Windows_Tokenizer_FollowsTheCRuntimeRules(string commandLine, string[] expected)
        {
            Assert.That(
                McpLaunchArguments.Tokenize(commandLine, McpCommandLineFormat.WindowsQuoted),
                Is.EqualTo(expected));
        }

        [Test]
        public void Posix_Tokenizer_SplitsOnWhitespaceOnly()
        {
            Assert.That(
                McpLaunchArguments.Tokenize("uvx  a\tb\nc", McpCommandLineFormat.PosixFlat),
                Is.EqualTo(new[] { "uvx", "a", "b", "c" }));
            Assert.That(
                McpLaunchArguments.Tokenize("prog \"a b\"", McpCommandLineFormat.PosixFlat),
                Is.EqualTo(new[] { "prog", "\"a", "b\"" }),
                "quotes carry no meaning once ps has flattened the line");
        }

        [Test]
        public void TheVerifier_NeverEchoesTheCommandLine()
        {
            // Command lines can carry secrets (for example an API service token); failure text names flags only.
            OwnershipFixture f = Fixture();
            var launch = Launch.For(f);
            launch.Extra = "--api-key-service-token SUPER-SECRET-VALUE";
            launch.Token = "--unity-instance-token WRONG";

            Decisions d = AssertDenied(f, launch.Build());

            Assert.That(d.Stop.Detail, Does.Not.Contain("SUPER-SECRET-VALUE"));
            Assert.That(d.PromotionDetail, Does.Not.Contain("SUPER-SECRET-VALUE"));
        }
    }
}
