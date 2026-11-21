using WebHop.CLI;
using WebHop.Core.Commands;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Cli
{
    /// <summary>Captures Console.Out, which is process-wide, so these tests don't run in parallel.</summary>
    [NonParallelizable]
    public class ConfigCommandTests
    {
        private string directory = null!;
        private ConsoleOutput output = null!;

        private string ConfigPath => Path.Combine(directory, "webhop.json");

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "webhop-tests-" + Guid.NewGuid().ToString("N"));
            output = new ConsoleOutput();
        }

        [TearDown]
        public void TearDown()
        {
            output.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [Test]
        public void Add_authtoken_saves_the_token_and_keeps_other_settings()
        {
            new CliConfig { GatewayUrl = "https://gw.example.com/" }.Save(ConfigPath);
            var token = TestKeys.Token(origin: "app1", audience: ["http://localhost/drop/"]);

            var exitCode = Run("add-authtoken", token);

            var config = CliConfig.Load(ConfigPath);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.Zero);
                Assert.That(config.AuthToken, Is.EqualTo(token));
                Assert.That(config.GatewayUrl, Is.EqualTo("https://gw.example.com/"));
                Assert.That(output.Text, Does.Contain("Auth token saved"));
                Assert.That(output.Text, Does.Contain("origin       app1").And.Contain("serves       http://localhost/drop/"));
            }
        }

        [Test]
        public void Add_authtoken_rejects_what_is_not_a_webhop_token()
        {
            var error = Assert.Throws<CliException>(() => Run("add-authtoken", "s3cr3t-token"));

            Assert.That(error.Message, Does.Contain("wh_"));
            Assert.That(File.Exists(ConfigPath), Is.False);
        }

        [Test]
        public void Add_gateway_url_saves_the_normalized_url()
        {
            var exitCode = Run("add-gateway-url", "https://gw.example.com");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.Zero);
                Assert.That(CliConfig.Load(ConfigPath).GatewayUrl, Is.EqualTo("https://gw.example.com/"));
            }
        }

        [Test]
        public void Add_gateway_url_rejects_an_invalid_url_without_writing_the_file()
        {
            Assert.Throws<CliException>(() => Run("add-gateway-url", "my-gateway"));
            Assert.That(File.Exists(ConfigPath), Is.False);
        }

        [Test]
        public void Check_without_a_file_fails()
        {
            var exitCode = Run("check");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(output.Text, Does.Contain("No configuration file"));
            }
        }

        [Test]
        public void Check_shows_the_settings_with_a_masked_token_and_what_it_says()
        {
            var token = TestKeys.Token(origin: "app1");
            new CliConfig { AuthToken = token, GatewayUrl = "https://gw.example.com/" }.Save(ConfigPath);

            var exitCode = Run("check");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.Zero);
                Assert.That(output.Text, Does.Contain("https://gw.example.com/"));
                Assert.That(output.Text, Does.Contain($"authtoken    wh********{Environment.NewLine}"));
                Assert.That(output.Text, Does.Not.Contain(token[3..]));
                Assert.That(output.Text, Does.Contain("origin       app1").And.Contain("expires"));
            }
        }

        [Test]
        public void Check_rejects_a_token_that_is_not_a_webhop_token()
        {
            new CliConfig { AuthToken = "abcdefgh" }.Save(ConfigPath);

            Assert.Throws<CliException>(() => Run("check"));
        }

        [Test]
        public void Check_reports_missing_settings()
        {
            new CliConfig().Save(ConfigPath);

            Run("check");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(output.Text, Does.Contain("gateway_url  (not set)"));
                Assert.That(output.Text, Does.Contain("authtoken    (not set)"));
            }
        }

        [Test]
        public void Check_rejects_an_invalid_gateway_url_in_the_file()
        {
            new CliConfig { GatewayUrl = "my-gateway" }.Save(ConfigPath);

            Assert.Throws<CliException>(() => Run("check"));
        }

        [Test]
        public void Without_a_subcommand_help_is_shown()
        {
            var exitCode = ConfigCommand.Run([]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.Zero);
                Assert.That(output.Text, Does.Contain("webhop config add-authtoken"));
            }
        }

        [Test]
        public void Unknown_subcommands_are_rejected()
        {
            var error = Assert.Throws<CliException>(() => Run("remove-authtoken"));
            Assert.That(error.Message, Does.Contain("remove-authtoken"));
        }

        private int Run(params string[] args) => ConfigCommand.Run([.. args, "--config", ConfigPath]);
    }
}
