using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Core
{
    /// <summary>
    /// WebHop signs and checks tokens with its own code. Microsoft.IdentityModel, referenced by the tests alone, is
    /// the independent reference: each accepts what the other signs, and both refuse the same forgeries.
    /// </summary>
    public class IdentityModelInteropTests
    {
        private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

        private static TokenValidationParameters Parameters(KeySet set) => new()
        {
            ValidIssuer = set.Issuer,
            ValidTypes = [Constants.TokenType],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidateAudience = false,
            IssuerSigningKeys = new JsonWebKeySet(set.ToPublicJson()).Keys,
            ClockSkew = TokenValidator.ClockSkew,
        };

        [Test]
        public async Task Tokens_webhop_issues_pass_identity_models_checks()
        {
            // Many, so that signatures whose r or s starts with zero bytes come along too
            var set = KeySet.Create([AudienceEntry.Parse("https://*.example.com/")]);
            var parameters = Parameters(set);

            for (var i = 0; i < 300; i++)
            {
                var token = set.Issue(new TokenRequest { Audience = [AudienceEntry.Parse("https://app1.example.com/")], Origin = "app1" }, DateTimeOffset.UtcNow);

                var result = await Handler.ValidateTokenAsync(token.Jwt, parameters);

                Assert.That(result.IsValid, Is.True, result.Exception?.Message);
                Assert.That(result.Claims["sub"], Is.EqualTo("app1"));
            }
        }

        [Test]
        public void The_header_webhop_writes_reads_the_same_in_identity_model()
        {
            var token = TestKeys.Set.Issue(new TokenRequest(), DateTimeOffset.UtcNow);

            var jwt = new JsonWebToken(token.Jwt);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(jwt.Alg, Is.EqualTo("ES256"));
                Assert.That(jwt.Typ, Is.EqualTo(Constants.TokenType));
                Assert.That(jwt.Kid, Is.EqualTo(TestKeys.Set.SigningKey!.KeyId));
                Assert.That(jwt.Issuer, Is.EqualTo(TestKeys.Set.Issuer));
                Assert.That(jwt.Id, Is.EqualTo(token.Id));
            }
        }

        [Test]
        public void Tokens_identity_model_signs_pass_webhops_checks()
        {
            var set = TestKeys.Set;
            var validator = new TokenValidator(set);
            using var ecdsa = TestKeys.PrivateKey(set);
            var credentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa) { KeyId = set.SigningKey!.KeyId }, SecurityAlgorithms.EcdsaSha256);

            for (var i = 0; i < 300; i++)
            {
                var now = DateTime.UtcNow;
                var jwt = Handler.CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = set.Issuer,
                    TokenType = Constants.TokenType,
                    IssuedAt = now,
                    NotBefore = now,
                    Expires = now.AddHours(1),
                    Claims = new Dictionary<string, object> { ["sub"] = "app1", ["jti"] = KeySet.RandomSerial(), ["aud"] = "http://localhost/" },
                    SigningCredentials = credentials,
                });

                var result = validator.Validate(Constants.TokenPrefix + jwt);

                Assert.That(result.IsValid, Is.True, result.Failure);
                Assert.That(result.Token!.Subject, Is.EqualTo("app1"));
                Assert.That(result.Token.NotBefore, Is.Not.Null);
            }
        }

        [TestCase("unsigned")]
        [TestCase("hmac with the public set")]
        [TestCase("changed payload")]
        [TestCase("changed header")]
        [TestCase("another key under the set's kid")]
        [TestCase("another key's kid")]
        [TestCase("zero signature")]
        [TestCase("der signature")]
        [TestCase("signature of another token")]
        [TestCase("typ JWT")]
        [TestCase("alg ES384")]
        [TestCase("another issuer")]
        [TestCase("expired")]
        public async Task Both_refuse_the_same_forgeries(string forgery)
        {
            var set = TestKeys.Set;
            var token = Forge(forgery);

            var ours = new TokenValidator(set).Validate(token);
            var theirs = await Handler.ValidateTokenAsync(token[Constants.TokenPrefix.Length..], Parameters(set));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ours.IsValid, Is.False, "WebHop accepted it");
                Assert.That(theirs.IsValid, Is.False, "IdentityModel accepted it");
            }
        }

        [Test]
        public async Task Both_accept_the_untouched_token_the_forgeries_start_from()
        {
            var token = Forge("none");

            Assert.That(new TokenValidator(TestKeys.Set).Validate(token).IsValid, Is.True);
            Assert.That((await Handler.ValidateTokenAsync(token[Constants.TokenPrefix.Length..], Parameters(TestKeys.Set))).IsValid, Is.True);
        }

        private static string Forge(string forgery)
        {
            var set = TestKeys.Set;
            var kid = set.SigningKey!.KeyId;
            var claims = TestKeys.Claims();
            var header = TestKeys.Header();
            var payload = JsonSerializer.Serialize(claims);
            var token = TestKeys.SignJson(payload);
            var parts = token[Constants.TokenPrefix.Length..].Split('.');
            var other = KeySet.Create([AudienceEntry.Parse("/")]);

            switch (forgery)
            {
                case "none":
                    return token;
                case "unsigned":
                    return Join(TestKeys.Encode($$"""{"alg":"none","typ":"webhop+jwt","kid":"{{kid}}"}"""), parts[1], "");
                case "hmac with the public set":
                    var input = TestKeys.Encode($$"""{"alg":"HS256","typ":"webhop+jwt","kid":"{{kid}}"}""") + "." + parts[1];
                    return Constants.TokenPrefix + input + "." + Base64UrlText.Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(set.ToPublicJson()), Encoding.ASCII.GetBytes(input)));
                case "changed payload":
                    return Join(parts[0], TestKeys.Encode(JsonSerializer.Serialize(TestKeys.Claims(origin: "admin"))), parts[2]);
                case "changed header":
                    return Join(TestKeys.Encode($$"""{"alg":"ES256","typ":"webhop+jwt","kid":"{{kid}}","x":1}"""), parts[1], parts[2]);
                case "another key under the set's kid":
                    return Constants.TokenPrefix + KeySet.Sign(header, payload, other.SigningKey!);
                case "another key's kid":
                    return TestKeys.SignJson(payload, set: other);
                case "zero signature":
                    return Join(parts[0], parts[1], Base64UrlText.Encode(new byte[64]));
                case "der signature":
                    using (var ecdsa = TestKeys.PrivateKey(set))
                        return Join(parts[0], parts[1], Base64UrlText.Encode(ecdsa.SignData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
                case "signature of another token":
                    return Join(parts[0], parts[1], TestKeys.SignJson(JsonSerializer.Serialize(TestKeys.Claims(origin: "admin")))[Constants.TokenPrefix.Length..].Split('.')[2]);
                case "typ JWT":
                    return TestKeys.SignJson(payload, header: $$"""{"alg":"ES256","typ":"JWT","kid":"{{kid}}"}""");
                case "alg ES384":
                    return TestKeys.SignJson(payload, header: $$"""{"alg":"ES384","typ":"webhop+jwt","kid":"{{kid}}"}""");
                case "another issuer":
                    claims["iss"] = "someoneelse1";
                    return TestKeys.Sign(claims);
                case "expired":
                    claims["iat"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
                    claims["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                    return TestKeys.Sign(claims);
                default:
                    throw new ArgumentException(forgery);
            }
        }

        private static string Join(params string[] parts) => Constants.TokenPrefix + string.Join('.', parts);
    }
}
