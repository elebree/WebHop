using System.Diagnostics.CodeAnalysis;

namespace WebHop.Core
{
    /// <summary>
    /// base64url without padding (RFC 4648 §5), as JWTs and encoded key sets use it. Decoding is strict: only
    /// the URL-safe alphabet, no padding or white space, and only the one spelling <see cref="Encode"/> gives
    /// (unused trailing bits zero), so no two texts decode to the same bytes and a signature can't be respelled
    /// into a second token that still verifies.
    /// </summary>
    internal static class Base64UrlText
    {
        public static string Encode(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <exception cref="FormatException">The text is not base64url.</exception>
        public static byte[] Decode(string text) =>
            TryDecode(text, out var bytes) ? bytes : throw new FormatException("Not base64url.");

        public static bool TryDecode(string? text, [NotNullWhen(true)] out byte[]? bytes)
        {
            bytes = null;
            if (text is null || text.Length % 4 == 1)
                return false;

            var padding = (text.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => "",
            };
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + padding);
            }
            catch (FormatException)
            {
                return false;
            }

            // Convert takes more than base64url: + and /, padding, white space, and unused bits set in the last
            // character. Encoding the bytes again gives the one canonical spelling, so the text must be exactly it.
            if (!string.Equals(Encode(decoded), text, StringComparison.Ordinal))
                return false;
            bytes = decoded;
            return true;
        }
    }
}
