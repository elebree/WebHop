using System.Text;
using System.Text.Json;

namespace WebHop.Core
{
    /// <summary>
    /// The gateway's checks of an origin's token, with the issuer's public keys only. <see cref="Validate(string?)"/>
    /// decides whether the token is good at all (a failure is a 401); <see cref="CheckHost"/> whether it may serve
    /// on the host its tunnel arrived on (a failure is a 403).
    /// </summary>
    /// <remarks>
    /// A token is <c>wh_</c> and a compact JWS (RFC 7515) of exactly one shape: header
    /// <c>{"alg":"ES256","typ":"webhop+jwt","kid":…}</c>, the claims, and a 64-byte ES256 signature by the set's
    /// key named by <c>kid</c>. The algorithm is fixed, never the token's choice, which closes JWT's algorithm
    /// confusion (<c>none</c>, or a public key used as an HMAC secret); keys a header carries (<c>jwk</c>,
    /// <c>jku</c>, <c>x5c</c>, <c>x5u</c>) are ignored, and a header with <c>crit</c> is refused, as the gateway
    /// knows no extension. Checks before the signature only ever refuse; nothing a token says admits it until its
    /// signature verifies.
    /// </remarks>
    public sealed class TokenValidator
    {
        /// <summary>How far clocks may differ, for <c>iat</c>, <c>nbf</c> and <c>exp</c>.</summary>
        public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

        /// <summary>The longest token read, prefix included, so a huge one costs nothing; real ones are well under 1 KB.</summary>
        public const int MaxTokenLength = 16 * 1024;

        private readonly Dictionary<string, IssuerKey> keysById;

        public TokenValidator(KeySet keySet)
        {
            Keys = keySet.ToPublic();
            keysById = Keys.Keys.ToDictionary(k => k.KeyId, StringComparer.Ordinal);
        }

        /// <summary>The public set tokens are checked against.</summary>
        public KeySet Keys { get; }

        /// <summary>Checks a token as it arrived, with its <c>wh_</c> prefix.</summary>
        public TokenValidation Validate(string? value) => Validate(value, DateTimeOffset.UtcNow);

        internal TokenValidation Validate(string? value, DateTimeOffset now)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
                return new TokenValidation(null, "no token");
            if (!value.StartsWith(Constants.TokenPrefix, StringComparison.Ordinal))
                return new TokenValidation(null, $"not a WebHop token: no {Constants.TokenPrefix} prefix");
            if (value.Length > MaxTokenLength)
                return new TokenValidation(null, $"it is longer than {MaxTokenLength} characters");

            var parts = value[Constants.TokenPrefix.Length..].Split('.');
            if (parts.Length != 3)
                return new TokenValidation(null, "it is not a JWT of three parts");

            // Read first, unverified, so a refusal can name the origin and serial it claims
            OriginToken.TryParse(value, out var claims, out var claimsError);

            if (ReadHeader(parts[0], out var header) is { } headerError)
                return new TokenValidation(claims, headerError);
            if (header.Algorithm != IssuerKey.Algorithm)
                return new TokenValidation(claims, $"its algorithm (alg) is not {IssuerKey.Algorithm}");
            if (header.Type != Constants.TokenType)
                return new TokenValidation(claims, $"its typ is not {Constants.TokenType}");
            if (claims is null)
                return new TokenValidation(null, claimsError ?? "its claims are invalid");
            if (!string.Equals(claims.Issuer, Keys.Issuer, StringComparison.Ordinal))
                return new TokenValidation(claims, "another issuer made it (iss)");
            if (header.KeyId is null || !keysById.TryGetValue(header.KeyId, out var key))
                return new TokenValidation(claims, "no key of the set signed it (kid)");

            // The signature covers the header and payload exactly as they arrived
            if (!Base64UrlText.TryDecode(parts[2], out var signature) || signature.Length != IssuerKey.SignatureSize)
                return new TokenValidation(claims, $"its signature is not {IssuerKey.SignatureSize} bytes of base64url");
            if (!key.Verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature))
                return new TokenValidation(claims, "its signature doesn't verify");

            // From here the claims are the issuer's. Times are compared without adding to them: exp may be near
            // the largest time there is.
            if (claims.Expires <= claims.IssuedAt)
                return new TokenValidation(claims, "it expires before it was issued (exp)");
            if (claims.IssuedAt - ClockSkew > now)
                return new TokenValidation(claims, "it was issued in the future (iat)");
            if (claims.NotBefore is { } notBefore && notBefore - ClockSkew > now)
                return new TokenValidation(claims, "it isn't valid yet (nbf)");
            if (claims.Expires < now - ClockSkew)
                return new TokenValidation(claims, "it has expired");
            if (claims.Expires - claims.IssuedAt > Keys.MaxLifetime)
                return new TokenValidation(claims, "it lives longer than maxLifetime allows");
            if (Keys.IssuedAfter is { } issuedAfter && claims.IssuedAt < issuedAfter)
                return new TokenValidation(claims, "it was revoked: issued before issuedAfter");
            if (Keys.Deny.Contains(claims.Id))
                return new TokenValidation(claims, "it was revoked: its serial is denied");

            return new TokenValidation(claims, null);
        }

        /// <summary>
        /// Whether a valid token may serve on a tunnel that arrived on <paramref name="host"/> (a
        /// <see cref="AudienceEntry.HostKey"/>): every <c>aud</c> entry within <c>allow</c>, and one of them
        /// serving that host. Null when it may, else why not.
        /// </summary>
        public string? CheckHost(OriginToken token, string host)
        {
            if (!Keys.AllowsHost(host))
                return $"the gateway doesn't serve {host} (allow)";
            foreach (var entry in token.Audience)
            {
                if (!Keys.AllowsOn(entry, host))
                    return entry.IsPath ? $"{entry} is not allowed on {host}" : $"{entry} is not within allow";
            }
            if (!token.Audience.Any(e => e.IsPath || e.CoversHost(host)))
                return $"it doesn't serve {host}";
            return null;
        }

        private readonly record struct Header(string? Algorithm, string? Type, string? KeyId);

        /// <summary>The JOSE header's <c>alg</c>, <c>typ</c> and <c>kid</c>; null when it can be used, else why not.</summary>
        private static string? ReadHeader(string encoded, out Header header)
        {
            header = default;
            if (!Base64UrlText.TryDecode(encoded, out var bytes))
                return "its header is not base64url";

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                return "its header is not JSON";
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return "its header is not a JSON object";
                // Not named: refusals are logged, and the name is the sender's text
                if (JsonValues.HasDuplicateMember(root))
                    return "its header has a member twice";
                if (root.TryGetProperty("crit", out _))
                    return "its header has extensions the gateway must understand (crit)";
                header = new Header(JsonValues.String(root, "alg"), JsonValues.String(root, "typ"), JsonValues.String(root, "kid"));
                return null;
            }
        }
    }

    /// <summary>The outcome of <see cref="TokenValidator.Validate(string?)"/>.</summary>
    /// <param name="Token">
    /// The claims: verified when <see cref="IsValid"/>, otherwise as the token states them, if it can be
    /// read, for naming it in logs.
    /// </param>
    /// <param name="Failure">Why it was refused; null when valid. Never contains the token.</param>
    public sealed record TokenValidation(OriginToken? Token, string? Failure)
    {
        public bool IsValid => Failure is null;
    }
}
