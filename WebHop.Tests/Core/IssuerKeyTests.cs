using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Core
{
    /// <summary>ES256 signing and checking with an issuer key, and reading keys.</summary>
    public class IssuerKeyTests
    {
        // RFC 7515 Appendix A.3: an ES256 JWS and the P-256 key that signed it
        private const string RfcX = "f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU";
        private const string RfcY = "x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0";
        private const string RfcD = "jpsQnnGQmL-YBIffH1136cspYG6-0iY7X1fCE9-E9LI";
        private const string RfcSigningInput =
            "eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFtcGxlLmNvbS9pc19yb290Ijp0cnVlfQ";
        private const string RfcSignature = "DtEhU3ljbEg8L38VWAfUAqOyKAM6-Xx-F4GawxaepmXFCgfTjDxw5djxLa8ISlSApmWQxfKTUJqPP3-Kg6NU1Q";

        /// <summary>The order of P-256's base point: r and s are below it.</summary>
        private static readonly BigInteger Order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);

        private static readonly byte[] Data = Encoding.ASCII.GetBytes("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJhcHAxIn0");

        [Test]
        public void The_rfc_7515_example_verifies()
        {
            var key = ReadKey(RfcX, RfcY, RfcD);
            var input = Encoding.ASCII.GetBytes(RfcSigningInput);
            var signature = Base64UrlText.Decode(RfcSignature);

            using (Assert.EnterMultipleScope())
            {
                // .NET's own ECDsa first, so a wrong vector can't pass for a working check
                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Base64UrlText.Decode(RfcX), Y = Base64UrlText.Decode(RfcY) },
                });
                Assert.That(ecdsa.VerifyData(input, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), Is.True);

                Assert.That(key.Verify(input, signature), Is.True);
                Assert.That(key.WithoutPrivateKey().Verify(input, signature), Is.True);
            }
        }

        [Test]
        public void Changing_any_bit_of_the_rfc_7515_example_fails_it()
        {
            var key = ReadKey(RfcX, RfcY);
            var input = Encoding.ASCII.GetBytes(RfcSigningInput);
            var signature = Base64UrlText.Decode(RfcSignature);

            for (var bit = 0; bit < signature.Length * 8; bit++)
                Assert.That(key.Verify(input, Flip(signature, bit)), Is.False, $"signature bit {bit}");
            for (var bit = 0; bit < input.Length * 8; bit += 7)
                Assert.That(key.Verify(Flip(input, bit), signature), Is.False, $"input bit {bit}");
        }

        [Test]
        public void A_signature_verifies_with_the_public_part_alone_and_with_nothing_else()
        {
            var key = IssuerKey.Generate();
            var other = IssuerKey.Generate();

            var signature = key.Sign(Data);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(signature, Has.Length.EqualTo(64));
                Assert.That(key.Verify(Data, signature), Is.True);
                Assert.That(key.WithoutPrivateKey().Verify(Data, signature), Is.True);
                Assert.That(key.Retire(DateTimeOffset.UtcNow).Verify(Data, signature), Is.True);
                Assert.That(other.Verify(Data, signature), Is.False);
                Assert.That(key.Verify([.. Data, (byte)'x'], signature), Is.False);
                Assert.That(key.Verify(Data[..^1], signature), Is.False);
            }
        }

        [Test]
        public void Its_signatures_verify_with_dotnets_own_ecdsa()
        {
            var set = KeySet.Create([AudienceEntry.Parse("/")]);
            using var ecdsa = TestKeys.PrivateKey(set);

            var signature = set.SigningKey!.Sign(Data);

            Assert.That(ecdsa.VerifyData(Data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), Is.True);
            Assert.That(set.SigningKey.Verify(Data, ecdsa.SignData(Data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)), Is.True);
        }

        [Test]
        public void A_key_without_its_private_part_or_retired_does_not_sign()
        {
            var key = IssuerKey.Generate();

            Assert.Throws<InvalidOperationException>(() => key.WithoutPrivateKey().Sign(Data));
            Assert.Throws<InvalidOperationException>(() => key.Retire(DateTimeOffset.UtcNow).Sign(Data));
        }

        [Test]
        public void A_der_signature_is_refused_even_when_it_is_right()
        {
            // Other JWT libraries' mistake: ASN.1 DER is not JWS's form, however valid the signature in it
            var set = KeySet.Create([AudienceEntry.Parse("/")]);
            using var ecdsa = TestKeys.PrivateKey(set);

            var der = ecdsa.SignData(Data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            Assert.That(ecdsa.VerifyData(Data, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), Is.True);
            Assert.That(set.SigningKey!.Verify(Data, der), Is.False);
        }

        [TestCase("empty")]
        [TestCase("63 bytes")]
        [TestCase("65 bytes")]
        [TestCase("zeros")]                 // CVE-2022-21449, "psychic signatures": r = s = 0
        [TestCase("r zero")]
        [TestCase("s zero")]
        [TestCase("r and s the order")]
        [TestCase("r and s above the order")]
        [TestCase("ones")]
        [TestCase("r and s swapped")]
        [TestCase("r of another signature")]
        public void Made_up_and_mangled_signatures_are_refused(string kind)
        {
            var key = IssuerKey.Generate();
            var signature = key.Sign(Data);
            byte[] r = signature[..32], s = signature[32..];
            var order = Bytes(Order);

            byte[] made = kind switch
            {
                "empty" => [],
                "63 bytes" => signature[..63],
                "65 bytes" => [.. signature, 0],
                "zeros" => new byte[64],
                "r zero" => [.. new byte[32], .. s],
                "s zero" => [.. r, .. new byte[32]],
                "r and s the order" => [.. order, .. order],
                "r and s above the order" => [.. Bytes(Order + 1), .. Bytes(Order + 1)],
                "ones" => Enumerable.Repeat((byte)0xff, 64).ToArray(),
                "r and s swapped" => [.. s, .. r],
                "r of another signature" => [.. key.Sign([.. Data, 1])[..32], .. s],
                _ => throw new ArgumentException(kind),
            };

            Assert.That(key.Verify(Data, made), Is.False);
        }

        [Test]
        public void The_mirror_signature_verifies_too_and_grants_nothing_more()
        {
            // ECDSA's (r, n - s) is as valid as (r, s); JWS doesn't forbid it. It signs the same claims, and revoking
            // goes by serial (jti), never by the token's text, so a second spelling of a token adds nothing.
            var key = IssuerKey.Generate();
            var signature = key.Sign(Data);
            var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);

            byte[] mirror = [.. signature[..32], .. Bytes(Order - s)];

            Assert.That(key.Verify(Data, mirror), Is.True);
        }

        [Test]
        public void A_point_off_the_curve_is_not_a_key()
        {
            var y = Base64UrlText.Decode(RfcY);
            y[31] ^= 1;

            var error = Assert.Throws<FormatException>(() => ReadKey(RfcX, Base64UrlText.Encode(y)));
            Assert.That(error.Message, Does.Contain("not a valid P-256 key"));
        }

        [Test]
        public void A_private_part_of_another_key_is_refused()
        {
            var other = Base64UrlText.Encode(Base64UrlText.Decode(RfcD).Select(b => (byte)(b ^ 0x5a)).ToArray());

            var error = Assert.Throws<FormatException>(() => ReadKey(RfcX, RfcY, other));
            Assert.That(error.Message, Does.Contain("its d doesn't belong to its x and y").Or.Contain("not a valid P-256 key"));
        }

        [TestCase("x", 31)]
        [TestCase("x", 33)]
        [TestCase("y", 0)]
        [TestCase("d", 31)]
        [TestCase("d", 33)]
        public void A_coordinate_or_private_part_of_another_size_is_refused(string member, int size)
        {
            var value = Base64UrlText.Encode(new byte[size].Select((_, i) => (byte)(i + 1)).ToArray());

            var error = Assert.Throws<FormatException>(() => ReadKey(
                member == "x" ? value : RfcX, member == "y" ? value : RfcY, member == "d" ? value : RfcD, kid: RfcKid));
            Assert.That(error.Message, Does.Contain($"{member} is not 32 bytes"));
        }

        [TestCase("x", "padded")]
        [TestCase("y", "padded")]
        [TestCase("x", "unused bits set")]
        [TestCase("y", "unused bits set")]
        [TestCase("y", "standard alphabet")]
        public void A_coordinate_must_be_spelled_canonically(string member, string spelling)
        {
            // Lenient decoders read each of these as the very same bytes
            var canonical = member == "x" ? RfcX : RfcY;
            var respelled = spelling switch
            {
                "padded" => canonical + "=",
                "unused bits set" => canonical[..^1] + (char)(canonical[^1] + 1),
                "standard alphabet" => canonical.Replace('-', '+').Replace('_', '/'),
                _ => throw new ArgumentException(spelling),
            };
            Assert.That(respelled, Is.Not.EqualTo(canonical));

            var error = Assert.Throws<FormatException>(() => ReadKey(member == "x" ? respelled : RfcX, member == "y" ? respelled : RfcY, kid: RfcKid));
            Assert.That(error.Message, Does.Contain($"{member} is not 32 bytes of base64url"));
        }

        [Test]
        public void Generated_keys_read_back_whatever_their_bytes()
        {
            // About one key in 128 has a coordinate starting with a zero byte, which must still be 32 bytes
            for (var i = 0; i < 300; i++)
            {
                var set = KeySet.Create([AudienceEntry.Parse("/")]);
                var read = KeySet.Parse(set.ToJson());
                Assert.That(read.SigningKey!.KeyId, Is.EqualTo(set.SigningKey!.KeyId));
                Assert.That(read.SigningKey.Verify(Data, set.SigningKey.Sign(Data)), Is.True);
            }
        }

        private static string RfcKid => IssuerKey.Thumbprint(Base64UrlText.Decode(RfcX), Base64UrlText.Decode(RfcY));

        private static IssuerKey ReadKey(string x, string y, string? d = null, string? kid = null)
        {
            kid ??= IssuerKey.Thumbprint(Base64UrlText.Decode(x), Base64UrlText.Decode(y));
            var json = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = x,
                ["y"] = y,
                ["d"] = d,
                ["alg"] = "ES256",
                ["kid"] = kid,
            }.Where(p => p.Value is not null).ToDictionary());
            using var document = JsonDocument.Parse(json);
            return IssuerKey.Read(document.RootElement);
        }

        private static byte[] Flip(byte[] bytes, int bit)
        {
            var copy = (byte[])bytes.Clone();
            copy[bit / 8] ^= (byte)(1 << (bit % 8));
            return copy;
        }

        /// <summary>A number as 32 big-endian bytes, as r and s are written.</summary>
        private static byte[] Bytes(BigInteger value)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            return bytes.Length >= 32 ? bytes[^32..] : [.. new byte[32 - bytes.Length], .. bytes];
        }
    }
}
