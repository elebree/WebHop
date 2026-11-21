using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace WebHop.Core
{
    /// <summary>
    /// The claims of an origin's token (<c>wh_</c> and a JWT), read without checking its signature: that's
    /// the gateway's job. Origins read what they serve, where to connect and their id from it; the gateway
    /// reads the claims of a token it has verified.
    /// </summary>
    public sealed class OriginToken
    {
        private OriginToken(string value, string issuer, string subject, bool subjectOmitted, IReadOnlyList<AudienceEntry> audience,
            bool audienceOmitted, string endpoint, int tunnels, DateTimeOffset issuedAt, DateTimeOffset expires, DateTimeOffset? notBefore, string id)
        {
            Value = value;
            Issuer = issuer;
            Subject = subject;
            SubjectOmitted = subjectOmitted;
            Audience = audience;
            AudienceOmitted = audienceOmitted;
            Endpoint = endpoint;
            Tunnels = tunnels;
            IssuedAt = issuedAt;
            Expires = expires;
            NotBefore = notBefore;
            Id = id;
        }

        /// <summary>The whole token, with its <c>wh_</c> prefix, as origins pass it on.</summary>
        public string Value { get; }

        /// <summary>The JWT without the prefix, as JWT libraries read it.</summary>
        public string Jwt => Value[Constants.TokenPrefix.Length..];

        /// <summary><c>iss</c>: the issuer's random id.</summary>
        public string Issuer { get; }

        /// <summary><c>sub</c>: the origin id; the token's serial (<c>jti</c>) when it has no <c>sub</c>.</summary>
        public string Subject { get; }

        /// <summary>Whether the token has no <c>sub</c>, so its serial is the origin id.</summary>
        public bool SubjectOmitted { get; }

        /// <summary><c>aud</c>: what the origin serves; the path <c>/</c> when the token has none.</summary>
        public IReadOnlyList<AudienceEntry> Audience { get; }

        /// <summary>Whether the token has no <c>aud</c> at all.</summary>
        public bool AudienceOmitted { get; }

        /// <summary><c>ept</c>: the tunnel endpoint, <see cref="Constants.DefaultWebHopEndpoint"/> when left out.</summary>
        public string Endpoint { get; }

        /// <summary><c>tun</c>: the most tunnels at once, <see cref="Constants.DefaultTunnels"/> when left out.</summary>
        public int Tunnels { get; }

        /// <summary><c>iat</c></summary>
        public DateTimeOffset IssuedAt { get; }

        /// <summary><c>exp</c></summary>
        public DateTimeOffset Expires { get; }

        /// <summary><c>nbf</c>, when the token has one; WebHop's issuer writes none, but the gateway honours it.</summary>
        public DateTimeOffset? NotBefore { get; }

        /// <summary><c>jti</c>: the token's serial, 12 lowercase letters and digits.</summary>
        public string Id { get; }

        /// <summary>Origin ids are lowercase letters and digits, at most 63, so they're also DNS labels.</summary>
        public static bool IsValidOriginId([NotNullWhen(true)] string? id) =>
            id is { Length: > 0 and <= 63 } && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

        /// <summary>
        /// A serial (<c>jti</c>): 12 lowercase letters and digits, so it's a valid origin id too, which a token
        /// without <c>sub</c> uses it as.
        /// </summary>
        public static bool IsValidSerial([NotNullWhen(true)] string? id) => id is { Length: 12 } && IsValidOriginId(id);

        /// <summary>
        /// A tunnel endpoint (<c>ept</c>): an absolute path other than <c>/</c>, of segments of unreserved
        /// characters, without a trailing slash, such as <c>/webhop</c> or <c>/t-7f3k9q</c>.
        /// </summary>
        public static bool IsValidEndpoint([NotNullWhen(true)] string? path) =>
            path is { Length: > 1 } && path[0] == '/' && path[^1] != '/' && !path.Contains("//", StringComparison.Ordinal)
            && path.All(c => c is '/' or '-' or '.' or '_' or '~' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'));

        public static OriginToken Parse(string value) =>
            TryParse(value, out var token, out var error) ? token : throw new FormatException(error);

        public static bool TryParse(string? value, [NotNullWhen(true)] out OriginToken? token, [NotNullWhen(false)] out string? error)
        {
            token = null;
            value = value?.Trim();
            if (string.IsNullOrEmpty(value) || !value.StartsWith(Constants.TokenPrefix, StringComparison.Ordinal))
            {
                error = $"a WebHop token starts with {Constants.TokenPrefix}";
                return false;
            }

            var parts = value[Constants.TokenPrefix.Length..].Split('.');
            if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            {
                error = $"a WebHop token is {Constants.TokenPrefix} and a signed JWT, three parts joined by dots";
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(Base64UrlText.Decode(parts[1]));
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                error = "its payload is not base64url JSON";
                return false;
            }

            using (document)
            {
                var payload = document.RootElement;
                if (payload.ValueKind != JsonValueKind.Object)
                {
                    error = "its payload is not a JSON object";
                    return false;
                }
                if (JsonValues.HasDuplicateMember(payload))
                    return Fail("its payload has a claim twice", out error);

                if (String(payload, "iss") is not { Length: > 0 } issuer)
                    return Fail("it names no issuer (iss)", out error);
                if (String(payload, "jti") is not { } id || !IsValidSerial(id))
                    return Fail("its serial (jti) is not 12 lowercase letters and digits", out error);

                // A token issued without an origin id is an origin of its own, named by its serial
                var subject = id;
                var subjectOmitted = !payload.TryGetProperty("sub", out var sub);
                if (!subjectOmitted)
                {
                    if (sub.ValueKind != JsonValueKind.String || !IsValidOriginId(sub.GetString()))
                        return Fail("its origin id (sub) is not 1-63 lowercase letters and digits", out error);
                    subject = sub.GetString()!;
                }
                if (Time(payload, "iat") is not { } issuedAt)
                    return Fail("it has no issue time (iat)", out error);
                if (Time(payload, "exp") is not { } expires)
                    return Fail("it has no expiry (exp)", out error);
                DateTimeOffset? notBefore = null;
                if (payload.TryGetProperty("nbf", out _))
                {
                    if (Time(payload, "nbf") is not { } nbf)
                        return Fail("its not-before time (nbf) is not a time", out error);
                    notBefore = nbf;
                }

                var endpoint = Constants.DefaultWebHopEndpoint;
                if (payload.TryGetProperty("ept", out var ept))
                {
                    if (ept.ValueKind != JsonValueKind.String || !IsValidEndpoint(ept.GetString()))
                        return Fail("its tunnel endpoint (ept) is not a path such as /webhop", out error);
                    endpoint = ept.GetString()!;
                }

                var tunnels = Constants.DefaultTunnels;
                if (payload.TryGetProperty("tun", out var tun) && (!tun.TryGetInt32(out tunnels) || tunnels < 1))
                    return Fail("its tunnel limit (tun) is not a positive number", out error);

                if (!TryReadAudience(payload, out var audience, out var omitted, out error))
                    return false;

                token = new OriginToken(value, issuer, subject, subjectOmitted, audience, omitted, endpoint, tunnels, issuedAt, expires, notBefore, id);
                error = null;
                return true;
            }
        }

        private static bool TryReadAudience(JsonElement payload, out IReadOnlyList<AudienceEntry> audience, out bool omitted, [NotNullWhen(false)] out string? error)
        {
            audience = [];
            omitted = !payload.TryGetProperty("aud", out var aud);
            if (omitted)
            {
                audience = [AudienceEntry.Parse("/")];
                error = null;
                return true;
            }

            var values = aud.ValueKind switch
            {
                JsonValueKind.String => [aud],
                JsonValueKind.Array => aud.EnumerateArray().ToList(),
                _ => [],
            };
            if (values.Count == 0)
                return Fail("what it serves (aud) is neither a string nor a list of them", out error);

            var entries = new List<AudienceEntry>();
            foreach (var value in values)
            {
                if (value.ValueKind != JsonValueKind.String)
                    return Fail("what it serves (aud) is neither a string nor a list of them", out error);
                if (!AudienceEntry.TryParse(value.GetString(), out var entry, out var entryError))
                    return Fail($"what it serves (aud): {entryError}", out error);
                entries.Add(entry);
            }

            audience = entries;
            error = null;
            return true;
        }

        private static string? String(JsonElement payload, string name) =>
            payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // A NumericDate may have a fraction; whole seconds are enough here
        private static DateTimeOffset? Time(JsonElement payload, string name) =>
            payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.GetDouble() is var seconds && seconds is >= 0 and < 253402300800
                ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
                : null;

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

        /// <summary>
        /// Where the origin connects: the scheme, host and port of <paramref name="gateway"/> when given
        /// (<c>--url</c>, <c>--urls</c>), else of the first URL or pattern in <c>aud</c>, at <see cref="Endpoint"/>.
        /// Null when the token serves only paths and no gateway is given.
        /// </summary>
        public Uri? TunnelUri(Uri? gateway)
        {
            var target = gateway ?? Audience.Select(e => e.ConnectUri(Subject)).FirstOrDefault(u => u is not null);
            if (target is null)
                return null;

            var scheme = target.Scheme switch
            {
                "https" or "wss" => "wss",
                "http" or "ws" => "ws",
                _ => throw new FormatException($"Unsupported WebHop gateway URL scheme: {target.Scheme}"),
            };
            return new UriBuilder(scheme, target.Host, target.Port, Endpoint).Uri;
        }

        /// <summary>
        /// The public URL visitors reach the origin on through a tunnel to <paramref name="tunnelUri"/>: its
        /// host with the path of the first entry served there.
        /// </summary>
        public Uri PublicUrl(Uri tunnelUri)
        {
            var host = AudienceEntry.HostKey(tunnelUri.Host);
            var entry = Audience.FirstOrDefault(e => e.IsPath || e.CoversHost(host)) ?? Audience[0];
            var scheme = tunnelUri.Scheme is "wss" or "https" ? "https" : "http";
            return new UriBuilder(scheme, tunnelUri.Host, tunnelUri.IsDefaultPort ? -1 : tunnelUri.Port, entry.Path).Uri;
        }
    }
}
