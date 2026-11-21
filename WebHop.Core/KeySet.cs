using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebHop.Core
{
    /// <summary>
    /// An issuer's key set: a JWK Set (RFC 7517) whose extra members are the issuer's limits on a gateway.
    /// The issuer keeps the private set (keys with <c>d</c>, and the default <c>lifetime</c>) and signs tokens
    /// with it; the gateway gets the public set derived from it (<see cref="Encode"/>), so the two can't
    /// disagree. Both apply the same rules: <see cref="Allows"/> when issuing, <see cref="AllowsOn"/> when a
    /// tunnel opens.
    /// </summary>
    public sealed class KeySet
    {
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);
        public static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromDays(90);

        private static readonly JsonWriterOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private static readonly JsonWriterOptions IndentedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true };

        private readonly List<AudienceEntry> allow;
        private readonly List<string> deny;
        private readonly List<IssuerKey> keys;
        private readonly TimeSpan? lifetime;

        private KeySet(string issuer, string endpoint, List<AudienceEntry> allow, TimeSpan maxLifetime, DateTimeOffset? issuedAfter,
            List<string> deny, TimeSpan? lifetime, List<IssuerKey> keys)
        {
            Issuer = issuer;
            Endpoint = endpoint;
            this.allow = allow;
            MaxLifetime = maxLifetime;
            IssuedAfter = issuedAfter;
            this.deny = deny;
            this.lifetime = lifetime;
            this.keys = keys;
        }

        /// <summary><c>iss</c>: a random id of 12 lowercase letters and digits that gives the gateway away no more than tokens do.</summary>
        public string Issuer { get; }

        /// <summary><c>ept</c>: the tunnel endpoint; <see cref="Constants.DefaultWebHopEndpoint"/> when the set has none.</summary>
        public string Endpoint { get; }

        /// <summary><c>allow</c>: what any token may claim, and the hosts tunnels may arrive on.</summary>
        public IReadOnlyList<AudienceEntry> Allow => allow;

        /// <summary><c>maxLifetime</c>: the longest <c>exp</c> minus <c>iat</c> a token may have.</summary>
        public TimeSpan MaxLifetime { get; }

        /// <summary><c>issuedAfter</c>: tokens issued before it are refused.</summary>
        public DateTimeOffset? IssuedAfter { get; private set; }

        /// <summary><c>deny</c>: the serials (<c>jti</c>) of revoked tokens.</summary>
        public IReadOnlyList<string> Deny => deny;

        /// <summary><c>lifetime</c>: the default expiry of new tokens, kept in the private set only.</summary>
        public TimeSpan Lifetime => lifetime ?? (DefaultLifetime < MaxLifetime ? DefaultLifetime : MaxLifetime);

        /// <summary><c>keys</c>: one key, or several while tokens move to a new one.</summary>
        public IReadOnlyList<IssuerKey> Keys => keys;

        /// <summary>Whether a key has its private part: a set for the issuer, never for the gateway.</summary>
        public bool IsPrivate => keys.Any(k => k.HasPrivateKey);

        /// <summary>The key new tokens are signed with; null in a public set.</summary>
        public IssuerKey? SigningKey => keys.LastOrDefault(k => k.CanSign);

        /// <summary>A new private set with one key and a random <see cref="Issuer"/>.</summary>
        /// <exception cref="ArgumentException">A setting is out of range.</exception>
        public static KeySet Create(IEnumerable<AudienceEntry> allow, string? endpoint = null, TimeSpan? lifetime = null, TimeSpan? maxLifetime = null)
        {
            var max = maxLifetime ?? DefaultMaxLifetime;
            var set = new KeySet(RandomId(), endpoint ?? Constants.DefaultWebHopEndpoint, [.. allow], max,
                null, [], lifetime ?? (DefaultLifetime < max ? DefaultLifetime : max), [IssuerKey.Generate()]);
            if (set.Problem() is { } problem)
                throw new ArgumentException(problem);
            return set;
        }

        /// <summary>12 random lowercase letters and digits: an issuer id.</summary>
        public static string RandomId()
        {
            const string characters = "abcdefghijklmnopqrstuvwxyz0123456789";
            var id = new char[12];
            for (var i = 0; i < id.Length; i++)
                id[i] = characters[RandomNumberGenerator.GetInt32(characters.Length)];
            return new string(id);
        }

        /// <summary>
        /// A serial (<c>jti</c>): 12 random lowercase letters and digits, about 62 bits. It only has to be unique
        /// among one issuer's unexpired tokens, and it's no secret: it's in the token and in <c>deny</c>. It has an
        /// origin id's form, as a token issued without one uses its serial instead.
        /// </summary>
        public static string RandomSerial() => RandomId();

        /// <summary>Reads a set encoded as <c>whk_…</c> or written as JSON.</summary>
        /// <exception cref="FormatException">With a message that says what's wrong.</exception>
        public static KeySet Parse(string text)
        {
            text = text.Trim();
            string json;
            if (text.StartsWith(Constants.TokenPrefix, StringComparison.Ordinal))
                throw new FormatException($"this is an origin's token ({Constants.TokenPrefix}…), not a key set ({Constants.KeySetPrefix}…)");
            if (text.StartsWith(Constants.KeySetPrefix, StringComparison.Ordinal))
            {
                try
                {
                    json = Encoding.UTF8.GetString(Base64UrlText.Decode(text[Constants.KeySetPrefix.Length..]));
                }
                catch (FormatException)
                {
                    throw new FormatException($"what follows {Constants.KeySetPrefix} is not base64url");
                }
            }
            else if (text.StartsWith('{'))
            {
                json = text;
            }
            else
            {
                throw new FormatException($"a key set is {Constants.KeySetPrefix}… or JSON starting with {{");
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                return Read(document.RootElement);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"it is not valid JSON: {ex.Message}");
            }
        }

        /// <exception cref="FormatException">With a message that says what's wrong.</exception>
        public static KeySet Read(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("a key set is a JSON object");

            const string name = "the key set";
            var issuer = JsonValues.String(root, "iss") ?? throw new FormatException("the key set names no issuer (iss)");
            var endpoint = Constants.DefaultWebHopEndpoint;
            if (root.TryGetProperty("ept", out var ept))
                endpoint = ept.ValueKind == JsonValueKind.String ? ept.GetString()! : throw new FormatException("the key set's ept is not a path");

            var allow = new List<AudienceEntry>();
            if (!root.TryGetProperty("allow", out var allowValue) || JsonValues.Strings(allowValue) is not { } allowStrings)
                throw new FormatException("the key set has no allow list");
            foreach (var value in allowStrings)
                allow.Add(AudienceEntry.TryParse(value, out var entry, out var error) ? entry : throw new FormatException($"allow: {error}"));

            var deny = new List<string>();
            if (root.TryGetProperty("deny", out var denyValue))
                deny = JsonValues.Strings(denyValue) ?? throw new FormatException("the key set's deny is not a list of serials");

            if (!root.TryGetProperty("keys", out var keysValue) || keysValue.ValueKind != JsonValueKind.Array)
                throw new FormatException("the key set has no keys");
            var keys = keysValue.EnumerateArray().Select(IssuerKey.Read).ToList();

            var set = new KeySet(issuer, endpoint, allow, JsonValues.Seconds(root, "maxLifetime", name) ?? DefaultMaxLifetime,
                JsonValues.Time(root, "issuedAfter", name), deny, JsonValues.Seconds(root, "lifetime", name), keys);
            if (set.Problem() is { } problem)
                throw new FormatException(problem);
            return set;
        }

        private string? Problem()
        {
            if (!OriginToken.IsValidOriginId(Issuer))
                return "the key set's issuer (iss) is not lowercase letters and digits";
            if (!OriginToken.IsValidEndpoint(Endpoint))
                return $"the key set's ept `{Endpoint}` is not a path such as /webhop";
            if (allow.Count == 0)
                return "the key set's allow list is empty";
            if (deny.FirstOrDefault(id => !OriginToken.IsValidSerial(id)) is { } bad)
                return $"the key set's deny list has `{bad}`, which is not a serial of 12 lowercase letters and digits";
            if (MaxLifetime <= TimeSpan.Zero)
                return "the key set's maxLifetime is not positive";
            if (lifetime is { } l && (l <= TimeSpan.Zero || l > MaxLifetime))
                return $"the key set's lifetime ({Durations.Format(l)}) is not between zero and maxLifetime ({Durations.Format(MaxLifetime)})";
            if (keys.Count == 0)
                return "the key set has no keys";
            if (keys.GroupBy(k => k.KeyId).FirstOrDefault(g => g.Count() > 1) is { } twice)
                return $"the key set has key {twice.Key} twice";
            return null;
        }

        /// <exception cref="FormatException">The file is not a key set, with its path in the message.</exception>
        public static KeySet Load(string path)
        {
            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{path}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Writes the set as JSON, readable by the user only on Unix (on Windows the file inherits its folder's
        /// access rules). Replaces an existing file atomically, or refuses to when <paramref name="overwrite"/> is false.
        /// </summary>
        /// <exception cref="IOException">The file exists and <paramref name="overwrite"/> is false, or it can't be written.</exception>
        public void Save(string path, bool overwrite = true)
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            var target = overwrite ? fullPath + ".tmp" : fullPath;
            if (overwrite)
                File.Delete(target);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(target, options))
                stream.Write(Encoding.UTF8.GetBytes(ToJson() + "\n"));
            if (overwrite)
                File.Move(target, fullPath, overwrite: true);
        }

        /// <summary>The set as written to its file: indented, private parts included.</summary>
        public string ToJson() => Write(isPrivate: true, IndentedJson);

        /// <summary>The public set as JSON: without <c>d</c>, <c>lifetime</c> and <c>retired</c>.</summary>
        public string ToPublicJson(bool indented = false) => Write(isPrivate: false, indented ? IndentedJson : CompactJson);

        /// <summary>The public set as the gateway takes it in an environment variable: <c>whk_</c> and base64url JSON.</summary>
        public string Encode() => Constants.KeySetPrefix + Base64UrlText.Encode(Encoding.UTF8.GetBytes(ToPublicJson()));

        /// <summary>The public set derived from this one.</summary>
        public KeySet ToPublic() =>
            new(Issuer, Endpoint, [.. allow], MaxLifetime, IssuedAfter, [.. deny], null, [.. keys.Select(k => k.WithoutPrivateKey())]);

        private string Write(bool isPrivate, JsonWriterOptions options)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, options))
            {
                writer.WriteStartObject();
                writer.WriteString("iss", Issuer);
                if (Endpoint != Constants.DefaultWebHopEndpoint)
                    writer.WriteString("ept", Endpoint);
                writer.WriteStartArray("allow");
                foreach (var entry in allow)
                    writer.WriteStringValue(entry.Value);
                writer.WriteEndArray();
                writer.WriteNumber("maxLifetime", (long)MaxLifetime.TotalSeconds);
                if (IssuedAfter is { } issuedAfter)
                    writer.WriteNumber("issuedAfter", issuedAfter.ToUnixTimeSeconds());
                if (deny.Count > 0)
                {
                    writer.WriteStartArray("deny");
                    foreach (var id in deny)
                        writer.WriteStringValue(id);
                    writer.WriteEndArray();
                }
                if (isPrivate && lifetime is { } l)
                    writer.WriteNumber("lifetime", (long)l.TotalSeconds);
                writer.WriteStartArray("keys");
                foreach (var key in keys)
                    key.WriteTo(writer, isPrivate);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Whether a token may claim <paramref name="entry"/>, as checked when issuing. A URL or pattern must
        /// fall within an <c>allow</c> entry. A path alone serves the host its tunnel arrives on, so it needs
        /// an exact host or a path alone in <c>allow</c> that covers it; a wildcard host doesn't, as each of
        /// its hosts is another app.
        /// </summary>
        public bool Allows(AudienceEntry entry) =>
            entry.IsPath
                ? allow.Any(a => !a.IsPattern && AudienceEntry.PathCovers(a.BasePath, entry.BasePath))
                : allow.Any(a => a.Contains(entry));

        /// <summary>Whether a token may claim <paramref name="entry"/> on a tunnel that arrived on <paramref name="host"/>.</summary>
        public bool AllowsOn(AudienceEntry entry, string host) =>
            entry.IsPath
                ? allow.Any(a => (a.IsPath || (!a.IsPattern && a.Host == host)) && AudienceEntry.PathCovers(a.BasePath, entry.BasePath))
                : allow.Any(a => a.Contains(entry));

        /// <summary>Whether tunnels may arrive on <paramref name="host"/> at all, so names the gateway doesn't mean to serve stay unused.</summary>
        public bool AllowsHost(string host) => allow.Any(a => a.IsPath || a.CoversHost(host));

        /// <summary>
        /// Signs a token for <paramref name="request"/>, after checking it as the gateway will: what it serves
        /// against <c>allow</c>, its lifetime against <c>maxLifetime</c>.
        /// </summary>
        /// <exception cref="ArgumentException">The request is outside the set's limits.</exception>
        /// <exception cref="InvalidOperationException">The set has no key to sign with.</exception>
        public OriginToken Issue(TokenRequest request, DateTimeOffset now)
        {
            var key = SigningKey ?? throw new InvalidOperationException("The key set has no private key to sign with.");

            var origin = request.Origin;
            if (origin is not null && !OriginToken.IsValidOriginId(origin))
                throw new ArgumentException($"The origin id `{origin}` is not 1-63 lowercase letters and digits.");

            IReadOnlyList<AudienceEntry> audience = request.Audience.Count > 0 ? request.Audience : [AudienceEntry.Parse("/")];
            foreach (var entry in audience)
            {
                if (!Allows(entry))
                    throw new ArgumentException(entry.IsPath
                        ? $"{entry} alone is not allowed: no exact host or path alone in allow ({string.Join(", ", allow)}) covers it."
                        : $"{entry} is not within allow ({string.Join(", ", allow)}).");
            }

            var lifetime = request.Lifetime ?? Lifetime;
            if (lifetime < TimeSpan.FromSeconds(1))
                throw new ArgumentException("The lifetime must be at least a second.");
            if (lifetime > MaxLifetime)
                throw new ArgumentException($"A lifetime of {Durations.Format(lifetime)} exceeds the set's maxLifetime of {Durations.Format(MaxLifetime)}.");
            if (request.Tunnels is < 1)
                throw new ArgumentException("The tunnel limit must be at least 1.");

            var issuedAt = now.ToUnixTimeSeconds();
            var payload = WriteJson(writer =>
            {
                writer.WriteString("iss", Issuer);
                // Left out without an origin id: the serial names the origin then
                if (origin is not null)
                    writer.WriteString("sub", origin);
                // Left out for the root path alone, which is what a token without aud serves
                if (audience is not [{ IsPath: true, BasePath: "" }])
                {
                    if (audience.Count == 1)
                    {
                        writer.WriteString("aud", audience[0].Value);
                    }
                    else
                    {
                        writer.WriteStartArray("aud");
                        foreach (var entry in audience)
                            writer.WriteStringValue(entry.Value);
                        writer.WriteEndArray();
                    }
                }
                if (Endpoint != Constants.DefaultWebHopEndpoint)
                    writer.WriteString("ept", Endpoint);
                if (request.Tunnels is { } tunnels && tunnels != Constants.DefaultTunnels)
                    writer.WriteNumber("tun", tunnels);
                writer.WriteNumber("iat", issuedAt);
                writer.WriteNumber("exp", issuedAt + (long)lifetime.TotalSeconds);
                writer.WriteString("jti", RandomSerial());
            });

            return OriginToken.Parse(Constants.TokenPrefix + Sign(Header(key), payload, key));
        }

        internal static string Header(IssuerKey key) => WriteJson(writer =>
        {
            writer.WriteString("alg", IssuerKey.Algorithm);
            writer.WriteString("typ", Constants.TokenType);
            writer.WriteString("kid", key.KeyId);
        });

        /// <summary>A JWS in compact form: the base64url header, payload and ES256 signature, joined by dots.</summary>
        internal static string Sign(string header, string payload, IssuerKey key)
        {
            var signingInput = Base64UrlText.Encode(Encoding.UTF8.GetBytes(header)) + "." + Base64UrlText.Encode(Encoding.UTF8.GetBytes(payload));
            return signingInput + "." + Base64UrlText.Encode(key.Sign(Encoding.ASCII.GetBytes(signingInput)));
        }

        internal static string WriteJson(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, CompactJson))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Adds a new signing key. The one that signed until now loses its private part and only verifies
        /// (<c>"key_ops": ["verify"]</c>), so the gateway still accepts its tokens; it's marked <c>retired</c>,
        /// and <see cref="DropRetiredKeys"/> removes it once <see cref="MaxLifetime"/> has passed since.
        /// </summary>
        public void Rotate(DateTimeOffset now)
        {
            for (var i = 0; i < keys.Count; i++)
                if (keys[i].CanSign)
                    keys[i] = keys[i].Retire(now);
            keys.Add(IssuerKey.Generate());
        }

        /// <summary>Retired keys whose last tokens have all expired; returns how many were dropped.</summary>
        public int DropRetiredKeys(DateTimeOffset now) => keys.RemoveAll(k => !k.CanSign && k.Retired is { } retired && retired + MaxLifetime < now);

        /// <summary>Adds a serial to <c>deny</c>; false when it's there already.</summary>
        public bool DenyToken(string id)
        {
            if (!OriginToken.IsValidSerial(id))
                throw new ArgumentException($"`{id}` is not a serial of 12 lowercase letters and digits.");
            if (deny.Contains(id))
                return false;
            deny.Add(id);
            return true;
        }

        /// <summary>Drops <c>deny</c> entries the gateway no longer needs; returns how many.</summary>
        public int ForgetDenied(Func<string, bool> expired) => deny.RemoveAll(id => expired(id));

        /// <summary>Revokes every token issued until <paramref name="now"/> by setting <c>issuedAfter</c> past it.</summary>
        public void RevokeAll(DateTimeOffset now) => IssuedAfter = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + 1);
    }

    /// <summary>What a new token is for.</summary>
    public sealed class TokenRequest
    {
        /// <summary>What the origin serves (<c>aud</c>); empty for the root path on the host it connects to.</summary>
        public IReadOnlyList<AudienceEntry> Audience { get; init; } = [];

        /// <summary>The origin id (<c>sub</c>); when null, the token has no <c>sub</c> and its serial is the origin id.</summary>
        public string? Origin { get; init; }

        /// <summary>How long it's valid; the set's <see cref="KeySet.Lifetime"/> when null.</summary>
        public TimeSpan? Lifetime { get; init; }

        /// <summary>The most tunnels at once (<c>tun</c>); <see cref="Constants.DefaultTunnels"/> when null.</summary>
        public int? Tunnels { get; init; }
    }
}
