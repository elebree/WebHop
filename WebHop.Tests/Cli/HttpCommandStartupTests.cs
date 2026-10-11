using WebHop.CLI;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Cli
{
    /// <summary>
    /// `webhop http` fails before starting anything when the gateway or auth token is missing. It
    /// reads WEBHOP_URL and WEBHOP_AUTHTOKEN, so these tests clear them and don't run in parallel.
    /// </summary>
    [NonParallelizable]
    public class HttpCommandStartupTests
    {
        private string directory = null!;
        private EnvironmentVariableScope env = null!;

        private string ConfigPath => Path.Combine(directory, "webhop.json");

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "webhop-tests-" + Guid.NewGuid().ToString("N"));
            env = new EnvironmentVariableScope()
                .Set("WEBHOP_URL", null)
                .Set(Constants.AuthTokenEnvironmentVariable, null);
        }

        [TearDown]
        public void TearDown()
        {
            env.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [Test]
        public async Task Without_a_gateway_it_refuses_to_start()
        {
            var error = await Assert.ThrowsAsync<CliException>(() => Run());

            Assert.That(error.Message, Does.StartWith("No gateway."));
        }

        [Test]
        public async Task Without_an_auth_token_it_refuses_to_start()
        {
            var error = await Assert.ThrowsAsync<CliException>(() => Run("--url", "https://gw.example.com/"));

            Assert.That(error.Message, Does.StartWith("No auth token."));
        }

        [Test]
        public async Task The_gateway_can_come_from_the_config_file()
        {
            new CliConfig { GatewayUrl = "https://gw.example.com/" }.Save(ConfigPath);

            var error = await Assert.ThrowsAsync<CliException>(() => Run());

            // Got past the gateway check
            Assert.That(error.Message, Does.StartWith("No auth token."));
        }

        [Test]
        public async Task The_gateway_can_come_from_the_environment()
        {
            env.Set("WEBHOP_URL", "https://gw.example.com/");

            var error = await Assert.ThrowsAsync<CliException>(() => Run());

            Assert.That(error.Message, Does.StartWith("No auth token."));
        }

        [Test]
        public async Task An_invalid_gateway_from_the_environment_is_rejected()
        {
            env.Set("WEBHOP_URL", "my-gateway");

            var error = await Assert.ThrowsAsync<CliException>(() => Run());

            Assert.That(error.Message, Does.Contain("my-gateway"));
        }

        private Task<int> Run(params string[] flags) =>
            HttpCommand.RunAsync(HttpOptions.Parse(["8080", "--config", ConfigPath, .. flags])!);
    }
}
