using WebHop.Core;
using WebHop.Gateway;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Integration
{
    /// <summary>The gateway falls back to WEBHOP_KEYSET, so these tests set or clear it.</summary>
    [NonParallelizable]
    public class GatewayWithoutKeysTests
    {
        [Test]
        public async Task A_gateway_without_a_key_set_does_not_start()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.KeySetEnvironmentVariable, null);

            var ex = await Assert.ThrowsAsync<GatewayConfigurationException>(() => TestGateway.StartWithSettingAsync(keySetSetting: null));

            Assert.That(ex!.Message, Does.Contain(Constants.KeySetEnvironmentVariable));
        }

        [Test]
        public async Task A_gateway_with_a_private_set_does_not_start()
        {
            using var env = new EnvironmentVariableScope().Set(Constants.KeySetEnvironmentVariable, null);

            var ex = await Assert.ThrowsAsync<GatewayConfigurationException>(() => TestGateway.StartWithSettingAsync(TestKeys.Set.ToJson()));

            Assert.That(ex!.Message, Does.Contain("private key"));
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
