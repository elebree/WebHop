using WebHop.Core;
using WebHop.Core.Commands;

namespace WebHop.Tests.Core
{
    /// <summary>The issuer and token commands, on a directory of their own, with output captured.</summary>
    public class IssuerCommandsTests
    {
        private string directory = null!;
        private StringWriter output = null!;
        private StringWriter error = null!;
        private DateTimeOffset now;

        private string SetPath => Path.Combine(directory, "_.example.com.json");

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "webhop-tests-" + Guid.NewGuid().ToString("N"));
            now = DateTimeOffset.UtcNow;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [Test]
        public void Init_writes_the_set_named_after_its_host_and_prints_the_public_set_alone()
        {
            var exitCode = Issuer("init", "*.example.com");

            var set = KeySet.Load(SetPath);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.Zero);
                Assert.That(set.IsPrivate, Is.True);
                Assert.That(set.Allow.Select(a => a.Value), Is.EqualTo(new[] { "https://*.example.com/" }));
                Assert.That(output.ToString().Trim(), Does.StartWith(Constants.KeySetPrefix).And.Not.Contain("\n"));
                Assert.That(KeySet.Parse(output.ToString()).IsPrivate, Is.False);
                Assert.That(error.ToString(), Does.Contain(Constants.KeySetEnvironmentVariable));
            }
        }

        [Test]
        public void Init_takes_more_allow_entries_the_endpoint_and_lifetimes()
        {
            Issuer("init", "example.com/api/v1", "--allow", "/frontend/,app2.example.com", "--allow", "/status", "--ept", "/t-7f3k9q",
                "--lifetime", "7d", "--max-lifetime", "30d");

            var set = KeySet.Load(Path.Combine(directory, "example.com.json"));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(set.Allow.Select(a => a.Value), Is.EqualTo(new[] { "https://example.com/api/v1", "/frontend/", "https://app2.example.com/", "/status" }));
                Assert.That(set.Endpoint, Is.EqualTo("/t-7f3k9q"));
                Assert.That(set.Lifetime, Is.EqualTo(TimeSpan.FromDays(7)));
                Assert.That(set.MaxLifetime, Is.EqualTo(TimeSpan.FromDays(30)));
            }
        }

        [TestCase("--ept", "t-7f3k9q")]
        [TestCase("--lifetime", "30")]
        [TestCase("--lifetime", "100d")]
        public void Init_rejects_bad_settings(string flag, string value)
        {
            Assert.Throws<CliException>(() => Issuer("init", "*.example.com", flag, value));
            Assert.That(File.Exists(SetPath), Is.False);
        }

        [Test]
        public void Init_never_overwrites_a_set()
        {
            Issuer("init", "*.example.com");
            var before = File.ReadAllText(SetPath);

            var failure = Assert.Throws<CliException>(() => Issuer("init", "_.example.com"));

            Assert.That(failure.Message, Does.Contain("exists"));
            Assert.That(File.ReadAllText(SetPath), Is.EqualTo(before));
        }

        [Test]
        public void Issue_finds_the_wildcard_set_signs_and_logs_the_token()
        {
            Issuer("init", "*.example.com");

            Token("issue", "app1.example.com", "--origin", "app1", "--tunnels", "5", "--lifetime", "12h");

            var token = OriginToken.Parse(output.ToString().Trim());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(token.Subject, Is.EqualTo("app1"));
                Assert.That(token.Audience.Single().Value, Is.EqualTo("https://app1.example.com/"));
                Assert.That(token.Tunnels, Is.EqualTo(5));
                Assert.That(token.Expires - token.IssuedAt, Is.EqualTo(TimeSpan.FromHours(12)));
                Assert.That(KeySetStore.ReadLog(SetPath).Single().Id, Is.EqualTo(token.Id));
                Assert.That(error.ToString(), Does.Contain(token.Id));
            }
        }

        [Test]
        public void An_issued_token_passes_the_gateways_checks()
        {
            Issuer("init", "*.example.com");
            var set = KeySet.Parse(output.ToString());

            Token("issue", "*.example.com/robots.txt", "--origin", "robots");

            var validator = new TokenValidator(set);
            var result = validator.Validate(output.ToString().Trim());
            Assert.That(result.IsValid, Is.True, result.Failure);
            Assert.That(validator.CheckHost(result.Token!, "robots.example.com"), Is.Null);
        }

        [Test]
        public void Several_arguments_serve_together_and_must_pick_one_set()
        {
            Issuer("init", "*.example.com");
            Issuer("init", "*.example.org");

            Token("issue", "app1.example.com", "app2.example.com/drop/");
            var token = OriginToken.Parse(output.ToString().Trim());

            Assert.That(token.Audience.Select(a => a.Value), Is.EqualTo(new[] { "https://app1.example.com/", "https://app2.example.com/drop/" }));
            Assert.Throws<CliException>(() => Token("issue", "app1.example.com", "app1.example.org"));
        }

        [Test]
        public void Relative_leaves_the_host_out()
        {
            Issuer("init", "webhop.example.com");

            Token("issue", "webhop.example.com/api/v1/", "--relative");

            Assert.That(OriginToken.Parse(output.ToString().Trim()).Audience.Single().Value, Is.EqualTo("/api/v1/"));
        }

        [Test]
        public void A_token_outside_allow_or_without_a_set_is_not_issued()
        {
            Issuer("init", "*.example.com");

            Assert.That(Assert.Throws<CliException>(() => Token("issue", "example.com")).Message, Does.Contain("No key set"));
            Assert.That(Assert.Throws<CliException>(() => Token("issue", "app1.example.com/x", "--relative")).Message, Does.Contain("allow"));
            Assert.That(Assert.Throws<CliException>(() => Token("issue", "app1.example.com", "--origin", "App1")).Message, Does.Contain("--origin"));
        }

        [Test]
        public void A_set_file_can_be_passed_instead()
        {
            var file = Path.Combine(directory, "elsewhere", "ci.json");
            Issuer("init", "*.example.com", "--set", file);

            Token("issue", "app1.example.com", "--set", file);

            Assert.That(OriginToken.TryParse(output.ToString().Trim(), out _, out _), Is.True);
            Assert.That(File.Exists(SetPath), Is.False);
            Assert.That(KeySetStore.ReadLog(file), Has.Count.EqualTo(1));
        }

        [Test]
        public void List_shows_the_tokens_and_their_state()
        {
            Issuer("init", "*.example.com");
            Token("issue", "app1.example.com", "--origin", "app1");
            var token = OriginToken.Parse(output.ToString().Trim());

            Token("list", "*.example.com");

            Assert.That(output.ToString(), Does.Contain(token.Id).And.Contain("app1").And.Contain("valid"));
        }

        [Test]
        public void Revoking_a_token_denies_its_serial_and_prints_the_new_public_set()
        {
            Issuer("init", "*.example.com");
            Token("issue", "app1.example.com");
            var token = OriginToken.Parse(output.ToString().Trim());

            Token("revoke", token.Value);

            Assert.That(KeySet.Load(SetPath).Deny, Is.EqualTo(new[] { token.Id }));
            Assert.That(KeySet.Parse(output.ToString().Trim()).Deny, Is.EqualTo(new[] { token.Id }));
        }

        [Test]
        public void Revoking_by_origin_finds_its_unexpired_tokens_in_the_log()
        {
            Issuer("init", "*.example.com");
            Token("issue", "app1.example.com", "--origin", "app1");
            var first = OriginToken.Parse(output.ToString().Trim());
            Token("issue", "app1.example.com", "--origin", "app1");
            var second = OriginToken.Parse(output.ToString().Trim());
            Token("issue", "app2.example.com", "--origin", "app2");

            Token("revoke", "app1.example.com", "--origin", "app1");

            Assert.That(KeySet.Load(SetPath).Deny, Is.EquivalentTo(new[] { first.Id, second.Id }));
            Assert.Throws<CliException>(() => Token("revoke", "app1.example.com", "--origin", "app1"));
        }

        [Test]
        public void Revoking_all_sets_issued_after_and_drops_denied_serials_that_expired()
        {
            Issuer("init", "*.example.com");
            Token("issue", "app1.example.com", "--lifetime", "1h");
            Token("revoke", output.ToString().Trim());
            Assert.That(KeySet.Load(SetPath).Deny, Has.Count.EqualTo(1));

            now += TimeSpan.FromHours(2);
            Token("revoke", "*.example.com", "--all");

            var set = KeySet.Load(SetPath);
            Assert.That(set.IssuedAfter, Is.GreaterThan(now - TimeSpan.FromSeconds(1)));
            Assert.That(set.Deny, Is.Empty);
        }

        [Test]
        public void Rotate_adds_a_signing_key_and_keeps_the_old_one_for_verifying()
        {
            Issuer("init", "*.example.com");
            var old = KeySet.Load(SetPath).SigningKey!.KeyId;

            Issuer("rotate", "_.example.com");

            var set = KeySet.Load(SetPath);
            Assert.That(set.Keys.Select(k => k.KeyId), Does.Contain(old));
            Assert.That(set.SigningKey!.KeyId, Is.Not.EqualTo(old));
            Assert.That(KeySet.Parse(output.ToString().Trim()).Keys, Has.Count.EqualTo(2));
        }

        [Test]
        public void Public_prints_the_public_set_again()
        {
            Issuer("init", "*.example.com");
            var printed = output.ToString().Trim();

            Issuer("public", "app1.example.com");

            Assert.That(output.ToString().Trim(), Is.EqualTo(printed));
        }

        [Test]
        public void List_shows_the_sets_with_their_signing_key()
        {
            Issuer("init", "*.example.com");
            var kid = KeySet.Load(SetPath).SigningKey!.KeyId;

            Issuer("list");

            Assert.That(output.ToString(), Does.Contain("_.example.com.json").And.Contain($"{kid} (signs)").And.Contain("https://*.example.com/"));
        }

        [TestCase("issuer", "bogus")]
        [TestCase("token", "revoke", "app1.example.com")]
        [TestCase("token", "issue")]
        public void Unknown_or_incomplete_commands_are_rejected(params string[] args)
        {
            var commands = Commands();
            Assert.Throws<CliException>(() => _ = args[0] == "issuer" ? commands.RunIssuer(args[1..]) : commands.RunToken(args[1..]));
        }

        [Test]
        public void Help_names_the_tool()
        {
            Commands("mytool").RunToken(["--help"]);

            Assert.That(output.ToString(), Does.Contain("mytool token issue"));
        }

        private IssuerCommands Commands(string tool = "webhop")
        {
            output = new StringWriter();
            error = new StringWriter();
            return new IssuerCommands(tool, new KeySetStore(directory), output, error, () => now);
        }

        private int Issuer(params string[] args) => Commands().RunIssuer(args);

        private int Token(params string[] args) => Commands().RunToken(args);
    }
}
