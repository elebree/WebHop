using WebHop.CLI;
using WebHop.Core.Commands;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Cli
{
    /// <summary>
    /// `webhop http` fails before starting anything when the token is missing or unusable, or names no
    /// gateway and none is given. It reads WEBHOP_GATEWAY and WEBHOP_TOKEN, so these tests clear them and
    /// don't run in parallel.
    /// </summary>
    [NonParallelizable]
    public class HttpCommandStartupTests
    {
        private EnvironmentVariableScope env = null!;

        [SetUp]
        public void SetUp()
        {
            env = new EnvironmentVariableScope()
                .Set(HttpCommand.GatewayEnvironmentVariable, null)
                .Set(Constants.AuthTokenEnvironmentVariable, null);
        }

        [TearDown]
        public void TearDown() => env.Dispose();

        [Test]
        public async Task Without_an_auth_token_it_refuses_to_start()
        {
            var error = await Assert.ThrowsAsync<CliException>(() => Run("--gateway", "https://gw.example.com/"));

            Assert.That(error.Message, Does.StartWith("No auth token."));
        }

        [Test]
        public async Task With_something_else_than_a_token_it_refuses_to_start()
        {
            var error = await Assert.ThrowsAsync<CliException>(() => Run("--token", "s3cr3t-token"));

            Assert.That(error.Message, Does.Contain("wh_"));
        }

        [Test]
        public async Task A_token_serving_paths_alone_needs_a_gateway()
        {
            var error = await Assert.ThrowsAsync<CliException>(() => Run("--token", TestKeys.Token(audience: ["/api/"])));

            Assert.That(error.Message, Does.StartWith("No gateway").And.Contain("/api/"));
        }

        [Test]
        public async Task The_token_can_come_from_the_environment()
        {
            env.Set(Constants.AuthTokenEnvironmentVariable, TestKeys.Token(audience: ["/api/"]));

            var error = await Assert.ThrowsAsync<CliException>(() => Run());

            // Got past the token check
            Assert.That(error.Message, Does.StartWith("No gateway"));
        }

        [Test]
        public async Task The_token_flag_wins_over_the_environment()
        {
            env.Set(Constants.AuthTokenEnvironmentVariable, TestKeys.Token(audience: ["/api/"]));

            var error = await Assert.ThrowsAsync<CliException>(() => Run("--token", "s3cr3t-token"));

            Assert.That(error.Message, Does.Contain("wh_"));
        }

        [Test]
        public async Task An_invalid_gateway_from_the_environment_is_rejected()
        {
            env.Set(HttpCommand.GatewayEnvironmentVariable, "my-gateway");

            var error = await Assert.ThrowsAsync<CliException>(() => Run("--token", TestKeys.Token()));

            Assert.That(error.Message, Does.Contain("my-gateway"));
        }

        [Test]
        public async Task The_gateway_flag_wins_over_the_environment()
        {
            env.Set(HttpCommand.GatewayEnvironmentVariable, "https://gw.example.com/");

            var error = await Assert.ThrowsAsync<CliException>(() => Run("--token", TestKeys.Token(), "--gateway", "https://gw.example.com/app/"));

            Assert.That(error.Message, Does.Contain("https://gw.example.com/app/").And.Contain("path"));
        }

        private static Task<int> Run(params string[] flags) =>
            HttpCommand.RunAsync(HttpOptions.Parse(["8080", .. flags])!);
    }
}
