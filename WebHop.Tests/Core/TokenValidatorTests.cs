using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Core
{
    /// <summary>
    /// The gateway's checks of a token, part by part: its shape, its header, its signature, then its claims. Tokens
    /// that must be refused are mostly signed by the set's own key, so the one thing wrong with them is what's tested.
    /// </summary>
    public class TokenValidatorTests
    {
        private static readonly TokenValidator Validator = new(TestKeys.Set);

        /// <summary>A fixed clock for times written into claims; <see cref="Check"/> validates at it.</summary>
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_795_046_400);

        private static readonly long At = Now.ToUnixTimeSeconds();

        private static readonly long Skew = (long)TokenValidator.ClockSkew.TotalSeconds;

        private static string Kid => TestKeys.Set.SigningKey!.KeyId;

        // ---- Valid tokens

        [Test]
        public void A_token_from_the_set_is_valid()
        {
            var result = Validator.Validate(TestKeys.Token(origin: "app1"));

            Assert.That(result.IsValid, Is.True, result.Failure);
            Assert.That(result.Token!.Subject, Is.EqualTo("app1"));
        }

        [Test]
        public void Claims_signed_with_the_issuers_header_are_valid()
        {
            // What the refusals below change one thing of
            Valid(TestKeys.Sign(Claims()));
        }

        [Test]
        public void White_space_around_the_token_is_ignored()
        {
            Valid("  " + TestKeys.Sign(Claims()) + "\r\n");
        }

        [Test]
        public void Header_members_the_gateway_doesnt_use_are_ignored()
        {
            Valid(TestKeys.Sign(Claims(), header: $$"""{"alg":"ES256","typ":"webhop+jwt","kid":"{{Kid}}","cty":"text/plain","x-note":7}"""));
        }

        [Test]
        public void A_retired_key_still_verifies_its_tokens()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")]);
            var token = set.Issue(new TokenRequest(), DateTimeOffset.UtcNow).Value;
            set.Rotate(DateTimeOffset.UtcNow);

            Assert.That(new TokenValidator(set).Validate(token).IsValid, Is.True);
        }

        [Test]
        public void A_key_dropped_from_the_set_verifies_nothing_any_more()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")]);
            var token = set.Issue(new TokenRequest(), Now).Value;
            set.Rotate(Now);
            set.DropRetiredKeys(Now + set.MaxLifetime + TimeSpan.FromSeconds(1));

            Refused(new TokenValidator(set).Validate(token, Now), token, "(kid)");
        }

        [Test]
        public void The_validator_holds_the_public_set_only()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")]);

            Assert.That(new TokenValidator(set).Keys.IsPrivate, Is.False);
            Assert.That(new TokenValidator(set).Keys.SigningKey, Is.Null);
        }

        [Test]
        public void Validating_at_once_from_many_threads_gives_the_same_answers()
        {
            var good = TestKeys.Sign(Claims());
            var forged = Forge(good, signature: Encode(new byte[64]));

            Parallel.For(0, 2000, i =>
            {
                var result = Validator.Validate(i % 2 == 0 ? good : forged, Now);
                if (result.IsValid != (i % 2 == 0))
                    throw new AssertionException($"Validation {i} gave {result.Failure ?? "valid"}");
            });
        }

        // ---- Shape

        [TestCase(null, "no token")]
        [TestCase("", "no token")]
        [TestCase("   ", "no token")]
        [TestCase("eyJhbGciOiJFUzI1NiJ9.e30.c2ln", "prefix")]
        [TestCase("WH_eyJhbGciOiJFUzI1NiJ9.e30.c2ln", "prefix")]
        [TestCase("Bearer wh_eyJhbGciOiJFUzI1NiJ9.e30.c2ln", "prefix")]
        public void Without_a_wh_token_it_is_refused(string? token, string reason)
        {
            Refused(Check(token), token, reason);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(5)]
        public void Anything_but_three_parts_is_refused(int count)
        {
            // Five parts would be an encrypted JWT (JWE), which WebHop doesn't use
            var parts = Parts(TestKeys.Sign(Claims()));
            var token = Constants.TokenPrefix + string.Join('.', Enumerable.Range(0, count).Select(i => parts[i % 3]));

            Refused(Check(token), token, "three parts");
        }

        [Test]
        public void A_token_longer_than_the_limit_is_refused_unread()
        {
            // The longest token within the limit is fine, one character more is not
            var claims = Claims();
            string token;
            var padding = TokenValidator.MaxTokenLength * 3 / 4 - 600;
            do
            {
                claims["x-padding"] = new string('a', ++padding);
                token = TestKeys.Sign(claims);
            }
            while (token.Length < TokenValidator.MaxTokenLength - 3);
            Assert.That(token.Length, Is.LessThanOrEqualTo(TokenValidator.MaxTokenLength));
            Valid(token);

            claims["x-padding"] = new string('a', padding + 3);
            var longer = TestKeys.Sign(claims);
            Assert.That(longer.Length, Is.GreaterThan(TokenValidator.MaxTokenLength));
            var result = Check(longer);
            Refused(result, longer, "longer than");
            Assert.That(result.Token, Is.Null, "not even read for the log");
        }

        // ---- Header

        [TestCase(null)]
        [TestCase("\"none\"")]
        [TestCase("\"HS256\"")]
        [TestCase("\"ES384\"")]
        [TestCase("\"ES512\"")]
        [TestCase("\"RS256\"")]
        [TestCase("\"PS256\"")]
        [TestCase("\"EdDSA\"")]
        [TestCase("\"es256\"")]
        [TestCase("\"ES256 \"")]
        [TestCase("\"\"")]
        [TestCase("null")]
        [TestCase("7")]
        [TestCase("[\"ES256\"]")]
        public void The_algorithm_must_be_es256_whatever_the_signature(string? alg)
        {
            // Signed correctly with ES256 all the same: the header must say so, exactly
            var header = alg is null
                ? $$"""{"typ":"webhop+jwt","kid":"{{Kid}}"}"""
                : $$"""{"alg":{{alg}},"typ":"webhop+jwt","kid":"{{Kid}}"}""";
            var token = TestKeys.Sign(Claims(), header: header);

            Refused(Check(token), token, "algorithm (alg) is not ES256");
        }

        [Test]
        public void An_unsigned_token_is_refused()
        {
            var token = Join(Encode($$"""{"alg":"none","typ":"webhop+jwt","kid":"{{Kid}}"}"""), Encode(JsonSerializer.Serialize(Claims())), "");

            Refused(Check(token), token, "algorithm");
        }

        [Test]
        public void The_public_key_used_as_an_hmac_secret_is_refused()
        {
            // JWT's algorithm confusion: an HS256 token "signed" with what the gateway knows of the key
            foreach (var secret in new[] { TestKeys.Set.ToPublicJson(), TestKeys.Set.Encode(), Kid })
            {
                var input = Encode($$"""{"alg":"HS256","typ":"webhop+jwt","kid":"{{Kid}}"}""") + "." + Encode(JsonSerializer.Serialize(Claims()));
                var token = Constants.TokenPrefix + input + "." + Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(input)));

                Refused(Check(token), token, "algorithm");
            }
        }

        [TestCase(null)]
        [TestCase("\"JWT\"")]
        [TestCase("\"at+jwt\"")]
        [TestCase("\"application/webhop+jwt\"")]
        [TestCase("\"WEBHOP+JWT\"")]
        [TestCase("\"webhop+jwt \"")]
        [TestCase("7")]
        public void A_token_of_another_type_is_refused(string? typ)
        {
            // So a JWT the issuer's key signed for something else can't open a tunnel
            var header = typ is null
                ? $$"""{"alg":"ES256","kid":"{{Kid}}"}"""
                : $$"""{"alg":"ES256","typ":{{typ}},"kid":"{{Kid}}"}""";
            var token = TestKeys.Sign(Claims(), header: header);

            Refused(Check(token), token, "typ");
        }

        [TestCase(null)]
        [TestCase("\"\"")]
        [TestCase("\"someone-elses-key\"")]
        [TestCase("7")]
        [TestCase("upper")]
        public void A_kid_naming_no_key_of_the_set_is_refused(string? kid)
        {
            var value = kid == "upper" ? $"\"{Kid.ToUpperInvariant()}\"" : kid;
            var header = value is null
                ? """{"alg":"ES256","typ":"webhop+jwt"}"""
                : $$"""{"alg":"ES256","typ":"webhop+jwt","kid":{{value}}}""";
            var token = TestKeys.Sign(Claims(), header: header);

            Refused(Check(token), token, "no key of the set signed it (kid)");
        }

        [Test]
        public void A_token_signed_by_a_key_outside_the_set_is_refused()
        {
            var other = KeySet.Create([AudienceEntry.Parse("/")]);
            var token = TestKeys.Sign(Claims(), set: other);

            Refused(Check(token), token, "(kid)");
        }

        [Test]
        public void A_key_the_header_brings_along_is_not_used()
        {
            // An attacker's own key, in the header (jwk) and pointed to (jku), signing under the set's kid
            var attacker = KeySet.Create([AudienceEntry.Parse("/")]);
            using var jwk = JsonDocument.Parse(attacker.ToPublicJson());
            var key = jwk.RootElement.GetProperty("keys")[0].GetRawText();
            var header = $$"""{"alg":"ES256","typ":"webhop+jwt","kid":"{{Kid}}","jwk":{{key}},"jku":"https://attacker.example.com/keys"}""";
            var token = Constants.TokenPrefix + KeySet.Sign(header, JsonSerializer.Serialize(Claims()), attacker.SigningKey!);

            Refused(Check(token), token, "signature doesn't verify");
        }

        [Test]
        public void A_header_with_critical_extensions_is_refused()
        {
            // RFC 7515 §4.1.11: a recipient that doesn't understand them must refuse; the gateway understands none
            var token = TestKeys.Sign(Claims(), header: $$"""{"alg":"ES256","typ":"webhop+jwt","kid":"{{Kid}}","crit":["b64"],"b64":false}""");

            Refused(Check(token), token, "(crit)");
        }

        [TestCase("""{"alg":"ES256","alg":"ES256","typ":"webhop+jwt","kid":"KID"}""")]
        [TestCase("""{"alg":"ES256","typ":"webhop+jwt","kid":"KID","alg":"none"}""")]
        [TestCase("""{"alg":"ES256","typ":"webhop+jwt","kid":"KID","ALG":"none"}""")]
        [TestCase("""{"alg":"ES256","typ":"webhop+jwt","kid":"KID","kid":"another"}""")]
        public void A_header_naming_a_member_twice_is_refused(string header)
        {
            // Readers disagree on which one counts. ALG stands for alg spelled with an escape, which is the same name.
            var token = TestKeys.Sign(Claims(), header: header.Replace("KID", Kid).Replace("ALG", Escaped("alg")));

            Refused(Check(token), token, "member twice");
        }

        [TestCase("not base64url!", "header is not base64url")]
        [TestCase("e30=", "header is not base64url")]
        [TestCase("bm90IGpzb24", "header is not JSON")]
        [TestCase("W10", "header is not a JSON object")]
        [TestCase("ImVzMjU2Ig", "header is not a JSON object")]
        [TestCase("", "header is not JSON")]
        public void A_header_that_cant_be_read_is_refused(string header, string reason)
        {
            var parts = Parts(TestKeys.Sign(Claims()));
            var token = Join(header, parts[1], parts[2]);

            Refused(Check(token), token, reason);
        }

        // ---- Signature

        [Test]
        public void A_changed_payload_is_refused()
        {
            var token = TestKeys.Sign(Claims(origin: "app1"));
            var forged = Forge(token, payload: Encode(JsonSerializer.Serialize(Claims(origin: "admin"))));

            Refused(Check(forged), forged, "signature doesn't verify");
        }

        [Test]
        public void A_changed_header_is_refused_even_when_it_says_the_same()
        {
            // The signature covers the header's bytes as sent, not what they mean
            var token = TestKeys.Sign(Claims());
            var forged = Forge(token, header: Encode($$"""{ "alg": "ES256", "typ": "webhop+jwt", "kid": "{{Kid}}" }"""));

            Refused(Check(forged), forged, "signature doesn't verify");
        }

        [Test]
        public void A_signature_of_another_token_is_refused()
        {
            // Both from the set's own key: a signature belongs to its header and payload alone
            var mine = TestKeys.Sign(Claims(origin: "app1"));
            var admin = TestKeys.Sign(Claims(origin: "admin"));
            var spliced = Forge(admin, signature: Parts(mine)[2]);

            Refused(Check(spliced), spliced, "signature doesn't verify");
        }

        [Test]
        public void A_signature_by_another_key_under_the_sets_kid_is_refused()
        {
            var other = KeySet.Create([AudienceEntry.Parse("/")]);
            var forged = Constants.TokenPrefix + KeySet.Sign(TestKeys.Header(), JsonSerializer.Serialize(Claims()), other.SigningKey!);

            Refused(Check(forged), forged, "signature doesn't verify");
        }

        [TestCase("empty", "three parts")]
        [TestCase("zeros", "signature doesn't verify")]
        [TestCase("ones", "signature doesn't verify")]
        [TestCase("swapped", "signature doesn't verify")]
        [TestCase("one bit", "signature doesn't verify")]
        [TestCase("cut", "signature is not 64 bytes")]
        [TestCase("longer", "signature is not 64 bytes")]
        [TestCase("padded", "signature is not 64 bytes")]
        public void A_mangled_signature_is_refused(string kind, string reason)
        {
            var token = TestKeys.Sign(Claims());
            var bytes = Base64UrlText.Decode(Parts(token)[2]);
            var text = Parts(token)[2];

            var signature = kind switch
            {
                "empty" => "",
                "zeros" => Encode(new byte[64]),
                "ones" => Encode(Enumerable.Repeat((byte)0xff, 64).ToArray()),
                "swapped" => Encode([.. bytes[32..], .. bytes[..32]]),
                "one bit" => Encode([.. bytes[..63], (byte)(bytes[63] ^ 1)]),
                "cut" => Encode(bytes[..63]),
                "longer" => Encode([.. bytes, 0]),
                "padded" => text + "==",
                _ => throw new ArgumentException(kind),
            };
            var forged = Forge(token, signature: signature);

            Refused(Check(forged), forged, reason);
        }

        [Test]
        public void A_signature_in_der_form_is_refused_even_when_right()
        {
            var token = TestKeys.Sign(Claims());
            var parts = Parts(token);
            using var ecdsa = TestKeys.PrivateKey();
            var der = ecdsa.SignData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            var forged = Forge(token, signature: Encode(der));

            Refused(Check(forged), forged, "signature is not 64 bytes");
        }

        [Test]
        public void A_respelled_signature_is_refused()
        {
            // The last of a signature's 86 characters has 4 unused bits. Set one and lenient decoders read the
            // very same signature, which would make a second valid token from one.
            var token = TestKeys.Sign(Claims());
            var signature = Parts(token)[2];
            var respelled = signature[..^1] + Next(signature[^1]);
            Assert.That(Convert.FromBase64String(respelled.Replace('-', '+').Replace('_', '/') + "=="),
                Is.EqualTo(Base64UrlText.Decode(signature)), "a lenient decoder reads the same bytes");

            var forged = Forge(token, signature: respelled);

            Refused(Check(forged), forged, "signature is not 64 bytes of base64url");
        }

        [Test]
        public void A_signature_in_the_standard_base64_alphabet_is_refused()
        {
            // About one signature in fifteen has neither - nor _; take one that has
            string token;
            do
                token = TestKeys.Sign(Claims());
            while (!Parts(token)[2].AsSpan().ContainsAny('-', '_'));

            var forged = Forge(token, signature: Parts(token)[2].Replace('-', '+').Replace('_', '/'));

            Refused(Check(forged), forged, "signature is not 64 bytes of base64url");
        }

        [Test]
        public void Respelling_the_header_or_payload_is_refused()
        {
            // Lenient decoders read the same claims; the signature is over the text as sent all the same. The
            // extra header member and the longer origin id leave unused bits in both parts' last characters.
            var token = TestKeys.Sign(Claims(origin: "app12"), header: $$"""{"alg":"ES256","typ":"webhop+jwt","kid":"{{Kid}}","x":1}""");
            Valid(token);
            var parts = Parts(token);
            for (var i = 0; i < 2; i++)
            {
                Assert.That(parts[i].Length % 4, Is.Not.Zero, $"part {i} has unused bits");
                var respelled = (string[])parts.Clone();
                respelled[i] = parts[i][..^1] + Next(parts[i][^1]);

                var forged = Join(respelled);
                Assert.That(Check(forged).IsValid, Is.False, $"part {i}");
            }
        }

        // ---- Claims, signed by the set's key

        [Test]
        public void A_token_of_another_issuer_is_refused()
        {
            var other = KeySet.Create([AudienceEntry.Parse("/")]);
            var token = other.Issue(new TokenRequest(), DateTimeOffset.UtcNow).Value;

            var result = Validator.Validate(token);

            Refused(result, token, "another issuer made it (iss)");
            // The claims it states still name it in logs
            Assert.That(result.Token, Is.Not.Null);
        }

        [Test]
        public void A_token_naming_another_issuer_is_refused()
        {
            var claims = Claims();
            claims["iss"] = "someoneelse1";
            var token = TestKeys.Sign(claims);

            Refused(Check(token), token, "(iss)");
        }

        [TestCase("sub", "admin")]
        [TestCase("jti", "000000000001")]
        [TestCase("exp", 9999999999L)]
        [TestCase("iss", "h3x9k2p7m4qz")]
        public void A_payload_naming_a_claim_twice_is_refused(string claim, object second)
        {
            var json = JsonSerializer.Serialize(Claims());
            var token = TestKeys.SignJson(json[..^1] + "," + JsonSerializer.Serialize(claim) + ":" + JsonSerializer.Serialize(second) + "}");

            Refused(Check(token), token, "claim twice");
        }

        [Test]
        public void A_payload_naming_a_claim_twice_with_an_escaped_name_is_refused()
        {
            var json = JsonSerializer.Serialize(Claims());
            var token = TestKeys.SignJson(json[..^1] + $$""","{{Escaped("sub")}}":"admin"}""");

            Refused(Check(token), token, "claim twice");
        }

        [TestCase("iss", "no issuer")]
        [TestCase("iat", "issue time")]
        [TestCase("exp", "expiry")]
        [TestCase("jti", "serial")]
        public void A_token_without_a_required_claim_is_refused(string claim, string reason)
        {
            var claims = Claims();
            claims.Remove(claim);
            var token = TestKeys.Sign(claims);

            Refused(Check(token), token, reason);
        }

        [TestCase("exp", "1797638400")]
        [TestCase("iat", "1795046400")]
        [TestCase("nbf", "1795046400")]
        [TestCase("exp", true)]
        [TestCase("exp", -1)]
        [TestCase("exp", 1e20)]
        [TestCase("nbf", "soon")]
        public void A_time_that_is_no_number_of_seconds_is_refused(string claim, object value)
        {
            var claims = Claims();
            claims[claim] = value;
            var token = TestKeys.Sign(claims);

            Assert.That(Check(token).IsValid, Is.False);
        }

        [Test]
        public void A_token_expiring_before_it_was_issued_is_refused()
        {
            var claims = Claims();
            claims["exp"] = At;
            var same = TestKeys.Sign(claims);
            claims["exp"] = At - 1;
            var before = TestKeys.Sign(claims);

            Refused(Check(same), same, "expires before it was issued");
            Refused(Check(before), before, "expires before it was issued");
        }

        [Test]
        public void Expiry_is_honoured_to_the_second_with_the_clock_skew()
        {
            var token = TestKeys.Sign(Claims());
            var expires = DateTimeOffset.FromUnixTimeSeconds(At + 3600);

            Valid(Validator.Validate(token, expires));
            Valid(Validator.Validate(token, expires + TokenValidator.ClockSkew));
            Refused(Validator.Validate(token, expires + TokenValidator.ClockSkew + TimeSpan.FromSeconds(1)), token, "it has expired");
        }

        [Test]
        public void Issue_time_is_honoured_to_the_second_with_the_clock_skew()
        {
            var token = TestKeys.Sign(Claims());

            Valid(Validator.Validate(token, Now - TokenValidator.ClockSkew));
            Refused(Validator.Validate(token, Now - TokenValidator.ClockSkew - TimeSpan.FromSeconds(1)), token, "issued in the future (iat)");
        }

        [Test]
        public void Not_before_is_honoured_to_the_second_with_the_clock_skew()
        {
            var claims = Claims();
            claims["nbf"] = At + 1800;
            var token = TestKeys.Sign(claims);
            var notBefore = DateTimeOffset.FromUnixTimeSeconds(At + 1800);

            Valid(Validator.Validate(token, notBefore - TokenValidator.ClockSkew));
            Refused(Validator.Validate(token, notBefore - TokenValidator.ClockSkew - TimeSpan.FromSeconds(1)), token, "isn't valid yet (nbf)");
        }

        [Test]
        public void A_not_before_in_the_past_is_fine()
        {
            var claims = Claims();
            claims["nbf"] = At - 60;

            Valid(TestKeys.Sign(claims));
        }

        [Test]
        public void A_lifetime_of_exactly_max_lifetime_is_fine_and_a_second_more_is_not()
        {
            var claims = Claims();
            claims["exp"] = At + (long)TestKeys.Set.MaxLifetime.TotalSeconds;
            var longest = TestKeys.Sign(claims);
            claims["exp"] = At + (long)TestKeys.Set.MaxLifetime.TotalSeconds + 1;
            var longer = TestKeys.Sign(claims);

            Valid(longest);
            Refused(Check(longer), longer, "maxLifetime");
        }

        [TestCase(253402300799L, "maxLifetime")]
        [TestCase(253402300000L, "maxLifetime")]
        public void An_expiry_near_the_end_of_time_is_refused_without_overflowing(long exp, string reason)
        {
            var claims = Claims();
            claims["exp"] = exp;
            var token = TestKeys.Sign(claims);

            Refused(Check(token), token, reason);
        }

        [Test]
        public void An_issue_time_near_the_end_of_time_is_refused_without_overflowing()
        {
            var claims = Claims();
            claims["iat"] = 253402300000L;
            claims["exp"] = 253402300799L;
            var token = TestKeys.Sign(claims);

            Refused(Check(token), token, "issued in the future");
        }

        [Test]
        public void Tokens_issued_before_issued_after_are_refused_to_the_second()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")]);
            set.RevokeAll(Now);
            var validator = new TokenValidator(set);
            var claims = Claims(set);
            claims["iat"] = At + 1;
            claims["exp"] = At + 3600;
            var first = TestKeys.Sign(claims, set);
            claims["iat"] = At;
            var revoked = TestKeys.Sign(claims, set);

            Valid(validator.Validate(first, Now));
            Refused(validator.Validate(revoked, Now), revoked, "issued before issuedAfter");
        }

        [Test]
        public void Denied_serials_are_refused()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")]);
            var denied = set.Issue(new TokenRequest(), DateTimeOffset.UtcNow);
            var other = set.Issue(new TokenRequest(), DateTimeOffset.UtcNow);
            set.DenyToken(denied.Id);

            Refused(new TokenValidator(set).Validate(denied.Value), denied.Value, "its serial is denied");
            Valid(new TokenValidator(set).Validate(other.Value));

            set.RevokeAll(DateTimeOffset.UtcNow);
            Refused(new TokenValidator(set).Validate(other.Value), other.Value, "issuedAfter");
            Valid(new TokenValidator(set).Validate(set.Issue(new TokenRequest(), DateTimeOffset.UtcNow.AddSeconds(2)).Value));
        }

        // ---- What it serves

        [TestCase("https://*.example.com/", "https://app1.example.com/", "app1.example.com", true)]
        [TestCase("https://*.example.com/", "https://app1.example.com/", "app2.example.com", false)]
        [TestCase("https://*.example.com/", "https://*.example.com/robots.txt", "robots.example.com", true)]
        [TestCase("https://*.example.com/", "https://*.example.com/robots.txt", "example.com", false)]
        [TestCase("https://*.example.com/", "/api/", "app1.example.com", false)]
        [TestCase("https://webhop.example.com/", "/api/", "webhop.example.com", true)]
        [TestCase("https://webhop.example.com/", "/api/", "other.example.com", false)]
        [TestCase("/api/", "/api/v1/", "anything.example.org", true)]
        [TestCase("/api/", "/admin/", "anything.example.org", false)]
        [TestCase("https://webhop.example.com/drop/", "https://webhop.example.com/", "webhop.example.com", false)]
        public void What_a_token_serves_must_fall_within_allow_and_cover_the_tunnels_host(string allow, string audience, string host, bool accepted)
        {
            var set = KeySet.Create([AudienceEntry.Parse(allow)]);
            var claims = TestKeys.Claims(set);
            claims["aud"] = audience;
            var token = OriginToken.Parse(TestKeys.Sign(claims, set));

            var refusal = new TokenValidator(set).CheckHost(token, host);

            Assert.That(refusal is null, Is.EqualTo(accepted), refusal);
        }

        [Test]
        public void Every_entry_must_fall_within_allow()
        {
            var set = KeySet.Create([AudienceEntry.Parse("https://app1.example.com/")]);
            var claims = TestKeys.Claims(set);
            claims["aud"] = new[] { "https://app1.example.com/", "https://other.example.org/" };

            var refusal = new TokenValidator(set).CheckHost(OriginToken.Parse(TestKeys.Sign(claims, set)), "app1.example.com");

            Assert.That(refusal, Does.Contain("other.example.org"));
        }

        // ---- Helpers

        /// <summary>Claims of a valid token from <paramref name="set"/>, issued at <see cref="Now"/> for an hour.</summary>
        private static Dictionary<string, object> Claims(KeySet? set = null, string origin = "app1")
        {
            var claims = TestKeys.Claims(set, origin);
            claims["iat"] = At;
            claims["exp"] = At + 3600;
            return claims;
        }

        private static TokenValidation Check(string? token) => Validator.Validate(token, Now);

        private static void Valid(string token) => Valid(Check(token));

        private static void Valid(TokenValidation result) => Assert.That(result.IsValid, Is.True, result.Failure);

        /// <summary>Refused for <paramref name="reason"/>, saying nothing of the token itself, as refusals are logged.</summary>
        private static void Refused(TokenValidation result, string? token, string reason)
        {
            Assert.That(result.IsValid, Is.False, "accepted");
            Assert.That(result.Failure, Does.Contain(reason));
            foreach (var part in (token ?? "").Split('.', '_').Where(p => p.Length >= 8))
                Assert.That(result.Failure, Does.Not.Contain(part), "the refusal repeats the token");
        }

        /// <summary>A JSON member name with its second letter written as a \u escape, which JSON readers take as the same name.</summary>
        private static string Escaped(string name) => name[..1] + (char)92 + $"u{(int)name[1]:x4}" + name[2..];

        private static string[] Parts(string token) => token[Constants.TokenPrefix.Length..].Split('.');

        private static string Join(params string[] parts) => Constants.TokenPrefix + string.Join('.', parts);

        private static string Forge(string token, string? header = null, string? payload = null, string? signature = null)
        {
            var parts = Parts(token);
            return Join(header ?? parts[0], payload ?? parts[1], signature ?? parts[2]);
        }

        private static string Encode(string text) => TestKeys.Encode(text);

        private static string Encode(byte[] bytes) => Base64UrlText.Encode(bytes);

        /// <summary>The next character of the base64url alphabet: same high bits, one more in the unused low ones.</summary>
        private static char Next(char c)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            return alphabet[alphabet.IndexOf(c) + 1];
        }
    }
}
