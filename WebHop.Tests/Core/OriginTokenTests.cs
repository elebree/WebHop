using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Core
{
    public class OriginTokenTests
    {
        [Test]
        public void A_token_says_what_the_origin_serves_and_where_it_connects()
        {
            var claims = TestKeys.Claims(origin: "app1");
            claims["aud"] = new[] { "https://app1.example.com/", "https://app1.example.org/" };
            claims["ept"] = "/t-7f3k9q";
            claims["tun"] = 5;

            var token = OriginToken.Parse(TestKeys.Sign(claims));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(token.Subject, Is.EqualTo("app1"));
                Assert.That(token.SubjectOmitted, Is.False);
                Assert.That(token.Issuer, Is.EqualTo(TestKeys.Set.Issuer));
                Assert.That(token.Audience.Select(a => a.Value), Is.EqualTo(new[] { "https://app1.example.com/", "https://app1.example.org/" }));
                Assert.That(token.AudienceOmitted, Is.False);
                Assert.That(token.Endpoint, Is.EqualTo("/t-7f3k9q"));
                Assert.That(token.Tunnels, Is.EqualTo(5));
                Assert.That(token.Id, Is.EqualTo(claims["jti"]));
                Assert.That(token.Expires.ToUnixTimeSeconds(), Is.EqualTo(claims["exp"]));
                Assert.That(token.IssuedAt.ToUnixTimeSeconds(), Is.EqualTo(claims["iat"]));
            }
        }

        [Test]
        public void Left_out_claims_have_their_defaults()
        {
            var token = OriginToken.Parse(TestKeys.Sign(TestKeys.Claims()));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(token.Audience.Single().Value, Is.EqualTo("/"));
                Assert.That(token.AudienceOmitted, Is.True);
                Assert.That(token.Endpoint, Is.EqualTo(Constants.DefaultWebHopEndpoint));
                Assert.That(token.Tunnels, Is.EqualTo(Constants.DefaultTunnels));
            }
        }

        [Test]
        public void A_token_without_the_prefix_is_rejected()
        {
            var token = TestKeys.Sign(TestKeys.Claims());

            Assert.That(OriginToken.TryParse(token[Constants.TokenPrefix.Length..], out _, out var error), Is.False);
            Assert.That(error, Does.Contain(Constants.TokenPrefix));
        }

        [TestCase("sub", "App1")]
        [TestCase("sub", "")]
        [TestCase("sub", 7)]
        [TestCase("jti", "not-a-serial")]
        [TestCase("jti", "0123456789abcdef0123456789abcdef")]
        [TestCase("jti", "Ab3dEf9hJk1L")]
        [TestCase("ept", "webhop")]
        [TestCase("ept", "/webhop/")]
        [TestCase("tun", 0)]
        [TestCase("aud", "app1.example.com")]
        [TestCase("aud", 7)]
        [TestCase("iss", 7)]
        public void Claims_in_the_wrong_form_are_rejected(string claim, object value)
        {
            var claims = TestKeys.Claims();
            claims[claim] = value;

            Assert.That(OriginToken.TryParse(TestKeys.Sign(claims), out _, out var error), Is.False, error);
        }

        [TestCase("iss")]
        [TestCase("iat")]
        [TestCase("exp")]
        [TestCase("jti")]
        public void Required_claims_must_be_there(string claim)
        {
            var claims = TestKeys.Claims();
            claims.Remove(claim);

            Assert.That(OriginToken.TryParse(TestKeys.Sign(claims), out _, out _), Is.False);
        }

        [Test]
        public void A_token_without_an_origin_id_is_the_origin_its_serial_names()
        {
            var claims = TestKeys.Claims();
            claims.Remove("sub");

            var token = OriginToken.Parse(TestKeys.Sign(claims));

            Assert.That(token.Subject, Is.EqualTo(claims["jti"]));
            Assert.That(token.SubjectOmitted, Is.True);
        }

        [Test]
        public void A_pattern_token_without_an_origin_id_connects_on_its_serial()
        {
            var claims = TestKeys.Claims();
            claims.Remove("sub");
            claims["jti"] = "ab3def9hjk1l";
            claims["aud"] = "https://*.example.com/robots.txt";

            Assert.That(OriginToken.Parse(TestKeys.Sign(claims)).TunnelUri(null)!.AbsoluteUri, Is.EqualTo("wss://ab3def9hjk1l.example.com/webhop"));
        }

        [TestCase("ab3def9hjk1l", true)]
        [TestCase("000000000001", true)]
        [TestCase("Ab3dEf9hJk1L", false)]
        [TestCase("0123456789abcdef0123456789abcdef", false)]
        [TestCase("ab3def9hjk1", false)]
        [TestCase("ab3def9hjk1-", false)]
        [TestCase(null, false)]
        public void Serials_are_12_lowercase_letters_and_digits(string? id, bool valid)
        {
            Assert.That(OriginToken.IsValidSerial(id), Is.EqualTo(valid));
        }

        [TestCase("wh_abc")]
        [TestCase("wh_a.b")]
        [TestCase("wh_a.!!!.c")]
        [TestCase("wh_a.bm90IGpzb24.c")]
        public void Garbage_is_rejected(string value)
        {
            Assert.That(OriginToken.TryParse(value, out _, out _), Is.False);
        }

        [Test]
        public void A_not_before_time_is_read_when_there_is_one()
        {
            var claims = TestKeys.Claims();
            claims["nbf"] = 1_795_046_400L;

            Assert.That(OriginToken.Parse(TestKeys.Sign(claims)).NotBefore, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_795_046_400)));
            Assert.That(OriginToken.Parse(TestKeys.Sign(TestKeys.Claims())).NotBefore, Is.Null);
        }

        [Test]
        public void A_payload_naming_a_claim_twice_is_rejected()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(TestKeys.Claims(origin: "app1"));

            Assert.That(OriginToken.TryParse(TestKeys.SignJson(json[..^1] + ""","sub":"admin"}"""), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("twice"));
        }

        [Test]
        public void A_payload_in_padded_or_standard_base64_is_rejected()
        {
            // 91 bytes of claims, so standard base64 pads them
            var parts = TestKeys.Sign(TestKeys.Claims(origin: "app12"))[Constants.TokenPrefix.Length..].Split('.');
            var standard = Convert.ToBase64String(Base64UrlText.Decode(parts[1]));
            Assert.That(standard, Does.EndWith("="));

            Assert.That(OriginToken.TryParse(Constants.TokenPrefix + string.Join('.', parts[0], standard, parts[2]), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("base64url"));
        }

        [TestCase("abc123", true)]
        [TestCase("0123456789abcdef0123456789abcdef", true)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("ABC", false)]
        [TestCase("a-b", false)]
        [TestCase("a.b", false)]
        public void Origin_ids_must_be_valid_host_names(string? id, bool valid)
        {
            Assert.That(OriginToken.IsValidOriginId(id), Is.EqualTo(valid));
        }

        [TestCase("https://app1.example.com/", null, "wss://app1.example.com/webhop")]
        [TestCase("http://localhost:5000/drop/", null, "ws://localhost:5000/webhop")]
        [TestCase("https://*.example.com/robots.txt", null, "wss://app1.example.com/webhop")]
        [TestCase("https://app1.example.com/", "http://127.0.0.1:5000/", "ws://127.0.0.1:5000/webhop")]
        [TestCase("/api/v1/", "https://gw.example.com/", "wss://gw.example.com/webhop")]
        public void Tunnels_connect_to_the_first_url_or_the_given_gateway(string aud, string? gateway, string expected)
        {
            var claims = TestKeys.Claims(origin: "app1");
            claims["aud"] = aud;
            var token = OriginToken.Parse(TestKeys.Sign(claims));

            Assert.That(token.TunnelUri(gateway is null ? null : new Uri(gateway))!.AbsoluteUri, Is.EqualTo(expected));
        }

        [Test]
        public void Tunnels_connect_at_the_tokens_endpoint()
        {
            var claims = TestKeys.Claims();
            claims["aud"] = "https://app1.example.com/";
            claims["ept"] = "/t-7f3k9q";

            Assert.That(OriginToken.Parse(TestKeys.Sign(claims)).TunnelUri(null)!.AbsoluteUri, Is.EqualTo("wss://app1.example.com/t-7f3k9q"));
        }

        [Test]
        public void A_token_with_paths_alone_needs_a_gateway()
        {
            var claims = TestKeys.Claims();
            claims["aud"] = "/api/v1/";

            Assert.That(OriginToken.Parse(TestKeys.Sign(claims)).TunnelUri(null), Is.Null);
        }

        [TestCase("https://app1.example.com/drop/", "wss://app1.example.com/webhop", "https://app1.example.com/drop/")]
        [TestCase("/api/v1/", "ws://gw.example.com:8080/webhop", "http://gw.example.com:8080/api/v1/")]
        public void The_public_url_is_the_tunnels_host_with_the_path_served_there(string aud, string tunnelUri, string expected)
        {
            var claims = TestKeys.Claims();
            claims["aud"] = aud;

            Assert.That(OriginToken.Parse(TestKeys.Sign(claims)).PublicUrl(new Uri(tunnelUri)).AbsoluteUri, Is.EqualTo(expected));
        }
    }
}
