using System.Net;
using WebHop.Core;
using WebHop.Origin;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Integration
{
    /// <summary>The gateway falls back to WEBHOP_KEYSET, so these tests set or clear it.</summary>
    [NonParallelizable]
    public class GatewayWithoutKeysTests
    {
        [Test]
        public async Task A_gateway_without_a_key_set_starts_but_accepts_no_tunnels()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.KeySetEnvironmentVariable, null);
            await using var gateway = await TestGateway.StartWithSettingAsync(keySetSetting: null);
            await using var app = await TunnelApp.StartAsync(gateway);

            Assert.That(await gateway.Client.GetStringAsync("/webhop/status"), Is.EqualTo("WebHop Gateway status"));
            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Unauthorized, "app reports unauthorized");
            using var response = await gateway.Client.GetAsync("/ping");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task A_gateway_with_a_private_set_accepts_no_tunnels()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.KeySetEnvironmentVariable, null);
            await using var gateway = await TestGateway.StartWithSettingAsync(TestKeys.Set.ToJson());
            await using var app = await TunnelApp.StartAsync(gateway);

            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Unauthorized, "app reports unauthorized");
            Assert.That(gateway.Registry.OriginCount, Is.Zero);
        }

        [Test]
        public async Task A_gateway_can_take_the_key_set_from_the_environment()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.KeySetEnvironmentVariable, TestKeys.Set.Encode());
            await using var gateway = await TestGateway.StartWithSettingAsync(keySetSetting: null);
            await using var app = await TunnelApp.StartAsync(gateway);

            await app.WaitForTunnelsAsync();
            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
        }
    }
}
