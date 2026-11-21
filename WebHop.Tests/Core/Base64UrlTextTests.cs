using System.Text;
using WebHop.Core;

namespace WebHop.Tests.Core
{
    public class Base64UrlTextTests
    {
        // RFC 4648 §10, without padding
        [TestCase("", "")]
        [TestCase("f", "Zg")]
        [TestCase("fo", "Zm8")]
        [TestCase("foo", "Zm9v")]
        [TestCase("foob", "Zm9vYg")]
        [TestCase("fooba", "Zm9vYmE")]
        [TestCase("foobar", "Zm9vYmFy")]
        public void It_encodes_and_decodes_the_rfc_4648_vectors(string text, string encoded)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Base64UrlText.Encode(Encoding.ASCII.GetBytes(text)), Is.EqualTo(encoded));
                Assert.That(Base64UrlText.Decode(encoded), Is.EqualTo(Encoding.ASCII.GetBytes(text)));
            }
        }

        [Test]
        public void It_uses_the_url_safe_alphabet()
        {
            // Standard base64 would be +/8=
            Assert.That(Base64UrlText.Encode([0xfb, 0xff]), Is.EqualTo("-_8"));
            Assert.That(Base64UrlText.Decode("-_8"), Is.EqualTo(new byte[] { 0xfb, 0xff }));
        }

        [Test]
        public void Any_bytes_survive_a_round_trip()
        {
            var random = new Random(7);
            for (var length = 0; length <= 100; length++)
            {
                var bytes = new byte[length];
                random.NextBytes(bytes);
                Assert.That(Base64UrlText.Decode(Base64UrlText.Encode(bytes)), Is.EqualTo(bytes), $"{length} bytes");
            }
        }

        [TestCase(null)]
        [TestCase("Zg==")]          // padding
        [TestCase("Zm8=")]
        [TestCase("+/8")]           // standard alphabet
        [TestCase("Zm9v YmFy")]     // white space, which Convert.FromBase64String skips
        [TestCase(" Zm9v")]
        [TestCase("Zm9v\n")]
        [TestCase("Zm9v\t")]
        [TestCase("Z")]             // a length no bytes encode to
        [TestCase("Zm9vY")]
        [TestCase("Zm9v.")]
        [TestCase("Zm9vé")]
        public void Anything_but_unpadded_url_safe_base64_is_refused(string? text)
        {
            Assert.That(Base64UrlText.TryDecode(text, out _), Is.False);
            if (text is not null)
                Assert.Throws<FormatException>(() => Base64UrlText.Decode(text));
        }

        [TestCase("Zh", "Zg")]       // 'f': the last character's 4 unused bits must be zero
        [TestCase("Zm9", "Zm8")]     // 'fo': 2 unused bits
        [TestCase("-_9", "-_8")]
        public void Only_the_one_spelling_with_zero_unused_bits_is_taken(string respelled, string canonical)
        {
            // Lenient decoders take both and give the same bytes, which would make a second token from one signature
            Assert.That(Base64UrlText.TryDecode(canonical, out _), Is.True);
            Assert.That(Base64UrlText.TryDecode(respelled, out _), Is.False);
        }

        [Test]
        public void No_two_texts_decode_to_the_same_bytes()
        {
            // Every 2- and 3-character text, which spell every 1- and 2-byte value: each value has exactly one
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var seen = new HashSet<string>();
            foreach (var a in alphabet)
            {
                foreach (var b in alphabet)
                {
                    Decode($"{a}{b}");
                    foreach (var c in alphabet)
                        Decode($"{a}{b}{c}");
                }
            }
            Assert.That(seen, Has.Count.EqualTo(256 + 65536));

            void Decode(string text)
            {
                if (!Base64UrlText.TryDecode(text, out var bytes))
                    return;
                if (!seen.Add(Convert.ToHexString(bytes)))
                    Assert.Fail($"{text} decodes to bytes another text gives");
                if (Base64UrlText.Encode(bytes) != text)
                    Assert.Fail($"{text} is not how its bytes are encoded");
            }
        }
    }
}
