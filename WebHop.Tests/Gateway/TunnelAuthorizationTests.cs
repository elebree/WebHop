using System.Text;
using Microsoft.Extensions.Configuration;
using WebHop.Core;
using WebHop.Gateway;
using WebHop.Origin;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Gateway
{
    /// <summary>The gateway falls back to WEBHOP_KEYSET, so these tests set or clear it.</summary>
    [NonParallelizable]
    public class TunnelAuthorizationTests
    {
        private EnvironmentVariableScope env = null!;

        [SetUp]
        public void SetUp() => env = new EnvironmentVariableScope().Set(Constants.KeySetEnvironmentVariable, null);

        [TearDown]
        public void TearDown() => env.Dispose();

        [Test]
        public void Without_a_key_set_no_tunnel_is_accepted()
        {
            var authorization = Create();

            Assert.That(authorization.IsConfigured, Is.False);
            Assert.That(authorization.Error, Is.Null);
            Assert.That(authorization.Endpoint, Is.EqualTo(Constants.DefaultWebHopEndpoint));
            Assert.That(authorization.Validate(TestKeys.Token()).IsValid, Is.False);
        }

        [Test]
        public void The_set_can_be_encoded()
        {
            var authorization = Create(TestKeys.Set.Encode());

            Assert.That(authorization.IsConfigured, Is.True);
            Assert.That(authorization.Validate(TestKeys.Token()).IsValid, Is.True);
        }

        [Test]
        public void The_set_can_be_json()
        {
            Assert.That(Create(TestKeys.Set.ToPublicJson(indented: true)).Keys!.Issuer, Is.EqualTo(TestKeys.Set.Issuer));
        }

        [Test]
        public void The_set_can_be_a_nested_object_in_appsettings()
        {
            var json = $$"""{ "WebHop": { "KeySet": {{TestKeys.Set.ToPublicJson(indented: true)}} } }""";
            var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

            var authorization = new TunnelAuthorization(configuration);

            Assert.That(authorization.Error, Is.Null);
            Assert.That(authorization.Keys!.Allow, Is.EqualTo(TestKeys.Set.Allow));
            Assert.That(authorization.Keys.MaxLifetime, Is.EqualTo(TestKeys.Set.MaxLifetime));
            Assert.That(authorization.Validate(TestKeys.Token()).IsValid, Is.True);
        }

        [Test]
        public void The_environment_variable_is_used_when_the_setting_is_missing()
        {
            env.Set(Constants.KeySetEnvironmentVariable, TestKeys.Set.Encode());

            Assert.That(Create().Keys!.Issuer, Is.EqualTo(TestKeys.Set.Issuer));
        }

        [Test]
        public void The_setting_wins_over_the_environment_variable()
        {
            var other = KeySet.Create([AudienceEntry.Parse("/")]);
            env.Set(Constants.KeySetEnvironmentVariable, other.Encode());

            Assert.That(Create(TestKeys.Set.Encode()).Keys!.Issuer, Is.EqualTo(TestKeys.Set.Issuer));
        }

        [Test]
        public void The_tunnel_endpoint_comes_from_the_set()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")], endpoint: "/t-7f3k9q");

            Assert.That(Create(set.Encode()).Endpoint, Is.EqualTo("/t-7f3k9q"));
        }

        [Test]
        public void A_private_set_is_refused_with_a_clear_error()
        {
            var authorization = Create(TestKeys.Set.ToJson());

            Assert.That(authorization.IsConfigured, Is.False);
            Assert.That(authorization.Error, Does.Contain("private key"));
        }

        [Test]
        public void A_token_pasted_as_the_set_is_refused_with_a_clear_error()
        {
            var authorization = Create(TestKeys.Token());

            Assert.That(authorization.IsConfigured, Is.False);
            Assert.That(authorization.Error, Does.Contain("origin's token").And.Contain(Constants.KeySetSetting));
        }

        [Test]
        public void Origins_read_their_token_from_the_auth_token_variable()
        {
            using var scope = new EnvironmentVariableScope().Set(Constants.AuthTokenEnvironmentVariable, "wh_from-env");

            Assert.That(new WebHopServerOptions().AuthToken, Is.EqualTo("wh_from-env"));
        }

        [TestCase("Bearer wh_abc.def.ghi", "wh_abc.def.ghi")]
        [TestCase("bearer  wh_abc.def.ghi ", "wh_abc.def.ghi")]
        [TestCase("Basic wh_abc", null)]
        [TestCase(null, null)]
        public void The_token_comes_from_the_authorization_header(string? header, string? expected)
        {
            var context = new DefaultHttpContext();
            if (header is not null)
                context.Request.Headers.Authorization = header;

            Assert.That(TunnelAuthorization.TokenFromRequest(context), Is.EqualTo(expected));
        }

        [Test]
        public void Browsers_send_the_token_in_a_subprotocol_as_is()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["Sec-WebSocket-Protocol"] = Constants.WebHopSubprotocol + ", " + Constants.TokenSubprotocolPrefix + "wh_abc.def.ghi";

            Assert.That(TunnelAuthorization.TokenFromRequest(context), Is.EqualTo("wh_abc.def.ghi"));
        }

        private static TunnelAuthorization Create(string? setting = null)
        {
            var values = new Dictionary<string, string?>();
            if (setting is not null)
                values[Constants.KeySetSetting] = setting;
            return new TunnelAuthorization(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }
    }
}
