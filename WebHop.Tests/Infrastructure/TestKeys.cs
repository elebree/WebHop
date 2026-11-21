using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebHop.Core;

namespace WebHop.Tests.Infrastructure
{
    /// <summary>The issuer of the tests' gateways, and tokens from it.</summary>
    internal static class TestKeys
    {
        /// <summary>Allows tunnels on both loopback names, whatever their port, for tokens with paths alone.</summary>
        public static KeySet Set { get; } = KeySet.Create([AudienceEntry.Parse("http://127.0.0.1/"), AudienceEntry.Parse("http://localhost/")]);

        /// <summary>
        /// A token from <paramref name="set"/> (<see cref="Set"/> by default) serving <paramref name="audience"/>
        /// (the root path on whatever host it connects to when empty), with room for the tests' tunnels.
        /// </summary>
        public static string Token(string? origin = null, int tunnels = 10, KeySet? set = null, TimeSpan? lifetime = null, params string[] audience) =>
            (set ?? Set).Issue(new TokenRequest
            {
                Origin = origin,
                Tunnels = tunnels,
                Lifetime = lifetime,
                Audience = [.. audience.Select(AudienceEntry.Parse)],
            }, DateTimeOffset.UtcNow).Value;

        /// <summary>Claims of a valid token from <paramref name="set"/>, to change before <see cref="Sign"/>.</summary>
        public static Dictionary<string, object> Claims(KeySet? set = null, string origin = "app1")
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new Dictionary<string, object>
            {
                ["iss"] = (set ?? Set).Issuer,
                ["sub"] = origin,
                ["iat"] = now,
                ["exp"] = now + 3600,
                ["jti"] = KeySet.RandomSerial(),
            };
        }

        /// <summary>Signs any claims with the set's key, past the issuer's own checks, for tokens the gateway must refuse.</summary>
        public static string Sign(Dictionary<string, object> claims, KeySet? set = null, string? header = null) =>
            SignJson(JsonSerializer.Serialize(claims), set, header);

        /// <summary>Signs a payload written by hand, such as one naming a claim twice, with the set's key.</summary>
        public static string SignJson(string payload, KeySet? set = null, string? header = null)
        {
            var key = (set ?? Set).SigningKey!;
            return Constants.TokenPrefix + KeySet.Sign(header ?? KeySet.Header(key), payload, key);
        }

        /// <summary>The header the issuer writes for the set's signing key.</summary>
        public static string Header(KeySet? set = null) => KeySet.Header((set ?? Set).SigningKey!);

        /// <summary>base64url of UTF-8 text, as a JWT's header and payload are written.</summary>
        public static string Encode(string text) => Base64UrlText.Encode(Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// The set's signing key as .NET's own ECDsa, read from the set's file form, to sign without WebHop's
        /// code: in other encodings, or through another JWT library.
        /// </summary>
        public static ECDsa PrivateKey(KeySet? set = null)
        {
            set ??= Set;
            using var json = JsonDocument.Parse(set.ToJson());
            var kid = set.SigningKey!.KeyId;
            var jwk = json.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("kid").GetString() == kid);
            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Base64UrlText.Decode(jwk.GetProperty("x").GetString()!), Y = Base64UrlText.Decode(jwk.GetProperty("y").GetString()!) },
                D = Base64UrlText.Decode(jwk.GetProperty("d").GetString()!),
            });
        }
    }
}
