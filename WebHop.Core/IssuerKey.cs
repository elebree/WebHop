using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebHop.Core
{
    /// <summary>
    /// One issuer key: an ECDSA P-256 key that signs tokens with ES256, as a JWK. Its <c>kid</c> is its
    /// RFC 7638 thumbprint, so two keys never share a name. In a private set it has its private part
    /// (<c>d</c>); a retired key keeps only its public part to verify tokens it signed before.
    /// </summary>
    public sealed class IssuerKey
    {
        /// <summary>The only algorithm WebHop's keys use; the gateway uses no other with them.</summary>
        public const string Algorithm = "ES256";

        /// <summary>An ES256 signature: r and s, 32 bytes each (IEEE P1363), as JWS carries it (RFC 7518 §3.4).</summary>
        internal const int SignatureSize = 64;

        private const int CoordinateSize = 32;

        private readonly byte[] x;
        private readonly byte[] y;
        private readonly byte[]? d;

        private IssuerKey(byte[] x, byte[] y, byte[]? d, bool verifyOnly, DateTimeOffset? retired)
        {
            this.x = x;
            this.y = y;
            this.d = d;
            VerifyOnly = verifyOnly;
            Retired = retired;
            KeyId = Thumbprint(x, y);
        }

        /// <summary><c>kid</c>: the key's RFC 7638 thumbprint.</summary>
        public string KeyId { get; }

        /// <summary>Whether it has its private part (<c>d</c>).</summary>
        public bool HasPrivateKey => d is not null;

        /// <summary>Whether it only verifies (<c>"key_ops": ["verify"]</c>), as a rotated-out key does.</summary>
        public bool VerifyOnly { get; }

        /// <summary>When it was rotated out; kept in private sets only.</summary>
        public DateTimeOffset? Retired { get; }

        public bool CanSign => d is not null && !VerifyOnly;

        public static IssuerKey Generate()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
            return new IssuerKey(parameters.Q.X!, parameters.Q.Y!, parameters.D, verifyOnly: false, retired: null);
        }

        /// <summary>The key without its private part, only verifying from <paramref name="when"/> on.</summary>
        internal IssuerKey Retire(DateTimeOffset when) => new(x, y, null, verifyOnly: true, retired: when);

        internal IssuerKey WithoutPrivateKey() => new(x, y, null, VerifyOnly, Retired);

        /// <summary>An ES256 signature: SHA-256 and the raw r‖s (IEEE P1363) form JWS uses.</summary>
        internal byte[] Sign(byte[] data)
        {
            if (!CanSign)
                throw new InvalidOperationException($"Key {KeyId} only verifies.");
            using var ecdsa = CreateECDsa(includePrivate: true);
            return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        /// <summary>
        /// Whether <paramref name="signature"/> is this key's ES256 signature of <paramref name="data"/>: exactly
        /// 64 bytes of r‖s, checked with the public part only. Any other length, such as DER, is refused.
        /// </summary>
        internal bool Verify(byte[] data, byte[] signature)
        {
            if (signature.Length != SignatureSize)
                return false;
            using var ecdsa = CreateECDsa(includePrivate: false);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        private ECDsa CreateECDsa(bool includePrivate) => ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
            D = includePrivate ? d : null,
        });

        /// <summary>The RFC 7638 thumbprint of a P-256 public key: SHA-256 of its required members in order.</summary>
        internal static string Thumbprint(byte[] x, byte[] y)
        {
            var json = $$"""{"crv":"P-256","kty":"EC","x":"{{Base64UrlText.Encode(x)}}","y":"{{Base64UrlText.Encode(y)}}"}""";
            return Base64UrlText.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        /// <param name="isPrivate">Whether to write <c>d</c> and <c>retired</c>, which the public set leaves out.</param>
        internal void WriteTo(Utf8JsonWriter writer, bool isPrivate)
        {
            writer.WriteStartObject();
            writer.WriteString("kty", "EC");
            writer.WriteString("crv", "P-256");
            writer.WriteString("x", Base64UrlText.Encode(x));
            writer.WriteString("y", Base64UrlText.Encode(y));
            if (isPrivate && d is not null)
                writer.WriteString("d", Base64UrlText.Encode(d));
            writer.WriteString("alg", Algorithm);
            writer.WriteString("kid", KeyId);
            if (VerifyOnly)
            {
                writer.WriteStartArray("key_ops");
                writer.WriteStringValue("verify");
                writer.WriteEndArray();
            }
            if (isPrivate && Retired is { } retired)
                writer.WriteNumber("retired", retired.ToUnixTimeSeconds());
            writer.WriteEndObject();
        }

        /// <exception cref="FormatException">It isn't an ES256 P-256 key with a matching <c>kid</c>.</exception>
        internal static IssuerKey Read(JsonElement key)
        {
            if (key.ValueKind != JsonValueKind.Object)
                throw new FormatException("a key is not a JSON object");

            var kid = JsonValues.String(key, "kid");
            var name = kid is null ? "a key" : $"key {kid}";
            if (JsonValues.String(key, "kty") != "EC" || JsonValues.String(key, "crv") != "P-256")
                throw new FormatException($"{name} is not an EC P-256 key");
            // The algorithm comes from the key, never from a token: every key states its own
            if (JsonValues.String(key, "alg") is not { } alg)
                throw new FormatException($"{name} states no alg");
            if (alg != Algorithm)
                throw new FormatException($"{name} is for {alg}; WebHop's keys are {Algorithm}");

            var x = Coordinate(key, "x", name) ?? throw new FormatException($"{name} has no x");
            var y = Coordinate(key, "y", name) ?? throw new FormatException($"{name} has no y");
            var d = Coordinate(key, "d", name);

            var verifyOnly = false;
            if (key.TryGetProperty("key_ops", out var ops))
            {
                var values = JsonValues.Strings(ops) ?? throw new FormatException($"{name}: key_ops is not a list of strings");
                verifyOnly = values.Contains("verify") && !values.Contains("sign");
            }

            var retired = JsonValues.Time(key, "retired", name);
            var result = new IssuerKey(x, y, d, verifyOnly, retired);
            if (kid is null)
                throw new FormatException($"key {result.KeyId} has no kid");
            if (kid != result.KeyId)
                throw new FormatException($"key {kid}: its kid is not its RFC 7638 thumbprint ({result.KeyId})");

            try
            {
                // Rejects a point that isn't on the curve, and a d that doesn't belong to it
                using var ecdsa = result.CreateECDsa(includePrivate: true);
                if (d is not null)
                {
                    var check = ecdsa.ExportParameters(includePrivateParameters: false);
                    if (!check.Q.X!.AsSpan().SequenceEqual(x) || !check.Q.Y!.AsSpan().SequenceEqual(y))
                        throw new FormatException($"{name}: its d doesn't belong to its x and y");
                }
            }
            // Windows reports a point off the curve as PlatformNotSupportedException ("its parameters are not valid
            // for this platform"); either way it's a key file problem, not a crash
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
                throw new FormatException($"{name} is not a valid P-256 key: {ex.Message}");
            }

            return result;
        }

        private static byte[]? Coordinate(JsonElement key, string member, string name)
        {
            if (!key.TryGetProperty(member, out var value))
                return null;
            return value.ValueKind == JsonValueKind.String && Base64UrlText.TryDecode(value.GetString(), out var bytes) && bytes.Length == CoordinateSize
                ? bytes
                : throw new FormatException($"{name}: {member} is not 32 bytes of base64url");
        }
    }
}
