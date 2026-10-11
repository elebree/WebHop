using System.Net;
using WebHop.Core;
using WebHop.Origin;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Integration
{
    /// <summary>The gateway falls back to WEBHOP_AUTHTOKEN, so these tests clear it.</summary>
    [NonParallelizable]
    public class GatewayWithoutAuthTokenTests
    {
        [Test]
        public async Task A_gateway_without_an_auth_token_starts_but_accepts_no_tunnels()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.AuthTokenEnvironmentVariable, null);
            await using var gateway = await TestGateway.StartAsync(authToken: null);
            await using var app = await TunnelApp.StartAsync(gateway.Url, authToken: "any-token");

            Assert.That(await gateway.Client.GetStringAsync("/webhop/status"), Is.EqualTo("WebHop Gateway status"));
            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Unauthorized, "app reports unauthorized");
            using var response = await gateway.Client.GetAsync("/ping");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task A_gateway_can_take_the_auth_token_from_the_environment()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.AuthTokenEnvironmentVariable, "env-token");
            await using var gateway = await TestGateway.StartAsync(authToken: null);
            await using var app = await TunnelApp.StartAsync(gateway.Url, authToken: "env-token");

            await app.WaitForTunnelsAsync();
            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
        }
    }
}
