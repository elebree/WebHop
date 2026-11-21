using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using WebHop.Core;

namespace WebHop.Tests.Core
{
    public class KeySetTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_795_046_400);

        private static KeySet Wildcard(string? endpoint = null) => KeySet.Create([AudienceEntry.Parse("https://*.example.com/")], endpoint);

        [Test]
        public void A_new_set_has_one_signing_key_and_a_random_issuer()
        {
            var set = Wildcard();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(set.Issuer, Does.Match("^[a-z0-9]{12}$"));
                Assert.That(set.Keys, Has.Count.EqualTo(1));
                Assert.That(set.SigningKey, Is.SameAs(set.Keys[0]));
                Assert.That(set.IsPrivate, Is.True);
                Assert.That(set.Endpoint, Is.EqualTo(Constants.DefaultWebHopEndpoint));
                Assert.That(set.Lifetime, Is.EqualTo(TimeSpan.FromDays(30)));
                Assert.That(set.MaxLifetime, Is.EqualTo(TimeSpan.FromDays(90)));
            }
        }

        [Test]
        public void The_key_id_is_the_rfc_7638_thumbprint()
        {
            var set = Wildcard();
            using var json = JsonDocument.Parse(set.ToPublicJson());
            var jwk = new JsonWebKey(json.RootElement.GetProperty("keys")[0].GetRawText());

            Assert.That(set.Keys[0].KeyId, Is.EqualTo(Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint())));
        }

        [Test]
        public void The_public_set_leaves_out_private_parts_and_issuing_defaults()
        {
            var set = Wildcard();
            set.Rotate(Now);

            var json = set.ToPublicJson();
            var parsed = KeySet.Parse(set.Encode());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(set.Encode(), Does.StartWith(Constants.KeySetPrefix));
                Assert.That(json, Does.Not.Contain("\"d\"").And.Not.Contain("lifetime\"").And.Not.Contain("retired"));
                Assert.That(json, Does.Contain("maxLifetime"));
                Assert.That(parsed.IsPrivate, Is.False);
                Assert.That(parsed.SigningKey, Is.Null);
                Assert.That(parsed.Issuer, Is.EqualTo(set.Issuer));
                Assert.That(parsed.Keys.Select(k => k.KeyId), Is.EqualTo(set.Keys.Select(k => k.KeyId)));
            }
        }

        [Test]
        public void Identity_models_reader_takes_the_public_set_and_keeps_the_extra_members()
        {
            var set = Wildcard();

            var jwks = new JsonWebKeySet(set.ToPublicJson());

            Assert.That(jwks.Keys.Single().Kid, Is.EqualTo(set.Keys[0].KeyId));
            Assert.That(jwks.Keys.Single().Alg, Is.EqualTo("ES256"));
            Assert.That(jwks.AdditionalData.Keys, Does.Contain("iss").And.Contain("allow"));
        }

        [Test]
        public void A_saved_set_loads_as_it_was()
        {
            var path = Path.Combine(Path.GetTempPath(), "webhop-tests-" + Guid.NewGuid().ToString("N"), "_.example.com.json");
            try
            {
                var set = KeySet.Create([AudienceEntry.Parse("https://*.example.com/"), AudienceEntry.Parse("/api/")], "/t-test1",
                    TimeSpan.FromDays(7), TimeSpan.FromDays(14));
                set.DenyToken("000000000001");
                set.RevokeAll(Now);
                set.Save(path);

                var loaded = KeySet.Load(path);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(loaded.ToJson(), Is.EqualTo(set.ToJson()));
                    Assert.That(loaded.IsPrivate, Is.True);
                    Assert.That(loaded.Endpoint, Is.EqualTo("/t-test1"));
                    Assert.That(loaded.Lifetime, Is.EqualTo(TimeSpan.FromDays(7)));
                    Assert.That(loaded.Deny, Is.EqualTo(new[] { "000000000001" }));
                    Assert.That(loaded.IssuedAfter, Is.EqualTo(Now.AddSeconds(1)));
                }
                Assert.Throws<IOException>(() => set.Save(path, overwrite: false));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }

        [Test]
        public void An_origin_token_is_not_taken_for_a_set()
        {
            var token = TestKeysToken();

            var error = Assert.Throws<FormatException>(() => KeySet.Parse(token));
            Assert.That(error.Message, Does.Contain("origin's token"));
        }

        [TestCase("not a set")]
        [TestCase("whk_!!!")]
        [TestCase("{")]
        [TestCase("{}")]
        [TestCase("""{"iss":"h3x9k2p7m4qz","allow":[],"keys":[]}""")]
        public void Other_text_is_rejected(string text)
        {
            Assert.Throws<FormatException>(() => KeySet.Parse(text));
        }

        [TestCase("alg", null, "states no alg")]
        [TestCase("alg", "RS256", "WebHop's keys are ES256")]
        [TestCase("kid", "someone-else", "thumbprint")]
        [TestCase("crv", "P-384", "P-256")]
        public void Keys_must_be_es256_with_their_thumbprint_as_kid(string member, string? value, string message)
        {
            var json = JsonDocument.Parse(Wildcard().ToPublicJson()).RootElement;
            var set = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.GetRawText())!;
            var key = JsonSerializer.Deserialize<Dictionary<string, object?>>(set["keys"][0].GetRawText())!;
            if (value is null)
                key.Remove(member);
            else
                key[member] = value;
            set["keys"] = JsonSerializer.SerializeToElement(new[] { key });

            var error = Assert.Throws<FormatException>(() => KeySet.Parse(JsonSerializer.Serialize(set)));
            Assert.That(error.Message, Does.Contain(message));
        }

        [Test]
        public void Numbers_may_come_as_strings_as_from_configuration()
        {
            var json = Wildcard().ToPublicJson().Replace("\"maxLifetime\":7776000", "\"maxLifetime\":\"86400\"");

            Assert.That(KeySet.Parse(json).MaxLifetime, Is.EqualTo(TimeSpan.FromDays(1)));
        }

        [Test]
        public void Issuing_signs_the_claims_in_the_documented_form()
        {
            var set = Wildcard("/t-test1");

            var token = set.Issue(new TokenRequest { Audience = [AudienceEntry.Parse("https://app1.example.com/")], Origin = "app1", Tunnels = 5 }, Now);

            var parts = token.Jwt.Split('.');
            Assert.That(Base64UrlEncoder.Decode(parts[0]), Is.EqualTo($$"""{"alg":"ES256","typ":"webhop+jwt","kid":"{{set.Keys[0].KeyId}}"}"""));
            Assert.That(Base64UrlEncoder.Decode(parts[1]), Is.EqualTo(
                $$"""{"iss":"{{set.Issuer}}","sub":"app1","aud":"https://app1.example.com/","ept":"/t-test1","tun":5,"iat":1795046400,"exp":1797638400,"jti":"{{token.Id}}"}"""));
            Assert.That(Base64UrlEncoder.DecodeBytes(parts[2]), Has.Length.EqualTo(64));
            Assert.That(token.Value, Does.StartWith("wh_eyJ"));
        }

        [Test]
        public void Default_claims_are_left_out()
        {
            var token = Wildcard().Issue(new TokenRequest { Audience = [AudienceEntry.Parse("https://app1.example.com/")], Tunnels = 3 }, Now);

            var payload = Base64UrlEncoder.Decode(token.Jwt.Split('.')[1]);
            Assert.That(payload, Does.Not.Contain("\"ept\"").And.Not.Contain("\"tun\"").And.Not.Contain("\"sub\""));
            Assert.That(token.Id, Does.Match("^[a-z0-9]{12}$"));
            Assert.That(token.Subject, Is.EqualTo(token.Id));
        }

        [Test]
        public void Several_entries_make_an_array_and_the_root_path_alone_none()
        {
            var set = KeySet.Create([AudienceEntry.Parse("https://*.example.com/"), AudienceEntry.Parse("/")]);

            var both = set.Issue(new TokenRequest { Audience = [AudienceEntry.Parse("https://app1.example.com/"), AudienceEntry.Parse("https://app2.example.com/")] }, Now);
            var root = set.Issue(new TokenRequest { Audience = [AudienceEntry.Parse("/")] }, Now);

            Assert.That(Base64UrlEncoder.Decode(both.Jwt.Split('.')[1]), Does.Contain("""
                "aud":["https://app1.example.com/","https://app2.example.com/"]
                """));
            Assert.That(root.AudienceOmitted, Is.True);
        }

        [TestCase("https://other.example.org/")]
        [TestCase("https://a.b.example.com/")]
        [TestCase("/api/")]
        public void A_claim_outside_allow_is_not_signed(string audience)
        {
            var error = Assert.Throws<ArgumentException>(() =>
                Wildcard().Issue(new TokenRequest { Audience = [AudienceEntry.Parse(audience)] }, Now));
            Assert.That(error.Message, Does.Contain("allow"));
        }

        [TestCase("https://app1.example.com/", "/api/v1/", true)]
        [TestCase("https://app1.example.com/api/", "/api/v1/", true)]
        [TestCase("https://app1.example.com/api/", "/frontend/", false)]
        [TestCase("/api/", "/api/v1/", true)]
        [TestCase("https://*.example.com/", "/api/v1/", false)]
        public void A_path_alone_needs_an_exact_host_or_a_path_in_allow(string allow, string path, bool allowed)
        {
            var set = KeySet.Create([AudienceEntry.Parse(allow)]);

            Assert.That(set.Allows(AudienceEntry.Parse(path)), Is.EqualTo(allowed));
        }

        [Test]
        public void A_lifetime_beyond_the_maximum_is_not_signed()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")], lifetime: TimeSpan.FromDays(1), maxLifetime: TimeSpan.FromDays(7));

            Assert.That(set.Issue(new TokenRequest(), Now).Expires, Is.EqualTo(Now.AddDays(1)));
            Assert.Throws<ArgumentException>(() => set.Issue(new TokenRequest { Lifetime = TimeSpan.FromDays(8) }, Now));
            Assert.Throws<ArgumentException>(() => set.Issue(new TokenRequest { Origin = "App1" }, Now));
        }

        [Test]
        public void A_public_set_cannot_sign()
        {
            Assert.Throws<InvalidOperationException>(() => Wildcard().ToPublic().Issue(new TokenRequest(), Now));
        }

        [Test]
        public void Rotating_retires_the_old_key_until_its_tokens_have_expired()
        {
            var set = Wildcard();
            var old = set.Keys[0].KeyId;

            set.Rotate(Now);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(set.Keys, Has.Count.EqualTo(2));
                Assert.That(set.Keys[0].KeyId, Is.EqualTo(old));
                Assert.That(set.Keys[0].HasPrivateKey, Is.False);
                Assert.That(set.Keys[0].VerifyOnly, Is.True);
                Assert.That(set.Keys[0].Retired, Is.EqualTo(Now));
                Assert.That(set.SigningKey, Is.SameAs(set.Keys[1]));
                Assert.That(set.ToPublicJson(), Does.Contain("""
                    "key_ops":["verify"]
                    """));
            }

            Assert.That(set.DropRetiredKeys(Now + set.MaxLifetime), Is.Zero);
            Assert.That(set.DropRetiredKeys(Now + set.MaxLifetime + TimeSpan.FromSeconds(1)), Is.EqualTo(1));
            Assert.That(set.Keys.Single().CanSign, Is.True);
        }

        private static string TestKeysToken() => WebHop.Tests.Infrastructure.TestKeys.Token();
    }
}
