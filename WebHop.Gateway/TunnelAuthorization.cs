using System.Globalization;
using System.Text;
using System.Text.Json;
using WebHop.Core;

namespace WebHop.Gateway
{
    /// <summary>
    /// Checks the tokens origins present when opening a tunnel (<c>Authorization: Bearer wh_…</c>, or the
    /// <c>webhop.token.</c> subprotocol from browsers), with the issuer's public key set from the
    /// <c>WebHop:KeySet</c> setting or the <c>WEBHOP_KEYSET</c> environment variable. The gateway holds no secret
    /// that could make a token. Only tunnel registration is protected; public traffic stays open. Without a
    /// usable set the gateway still starts, but accepts no tunnels at all.
    /// </summary>
    public sealed class TunnelAuthorization
    {
        private readonly TokenValidator? validator;

        public TunnelAuthorization(IConfiguration configuration)
        {
            try
            {
                Keys = ReadKeys(configuration);
                if (Keys is not null)
                    validator = new TokenValidator(Keys);
            }
            catch (FormatException ex)
            {
                Keys = null;
                Error = ex.Message;
            }
        }

        /// <summary>The public key set, when one is configured and usable.</summary>
        public KeySet? Keys { get; }

        /// <summary>Why the configured set can't be used; null when it can, or when none is configured.</summary>
        public string? Error { get; }

        public bool IsConfigured => validator is not null;

        /// <summary>Where origins open tunnels: the set's <c>ept</c>, or <see cref="Constants.DefaultWebHopEndpoint"/>.</summary>
        public string Endpoint => Keys?.Endpoint ?? Constants.DefaultWebHopEndpoint;

        /// <summary>Checks the token itself: a failure means 401.</summary>
        public TokenValidation Validate(string? token) =>
            validator?.Validate(token) ?? new TokenValidation(null, "the gateway has no key set");

        /// <summary>Whether a valid token may serve on the tunnel's host; why not, or null. A refusal means 403.</summary>
        public string? CheckHost(OriginToken token, string host) =>
            validator is null ? "the gateway has no key set" : validator.CheckHost(token, host);

        /// <summary>The token from <c>Authorization: Bearer</c>, else from a <c>webhop.token.</c> subprotocol; null if neither.</summary>
        public static string? TokenFromRequest(HttpContext context)
        {
            const string scheme = "Bearer ";
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return header[scheme.Length..].Trim();

            // Browsers cannot set Authorization on a WebSocket, so a token may ride in Sec-WebSocket-Protocol
            return TokenFromSubprotocols(context.WebSockets.WebSocketRequestedProtocols);
        }

        /// <summary>
        /// The token from a <c>webhop.token.wh_…</c> subprotocol, or null. A JWT has only characters a
        /// subprotocol allows, so it rides there as is.
        /// </summary>
        public static string? TokenFromSubprotocols(IEnumerable<string> protocols) =>
            protocols.FirstOrDefault(p => p.StartsWith(Constants.TokenSubprotocolPrefix, StringComparison.Ordinal))?[Constants.TokenSubprotocolPrefix.Length..];

        /// <summary>
        /// The set from the setting (a string, or a nested object in appsettings.json), else from the environment
        /// variable; null when neither is set.
        /// </summary>
        /// <exception cref="FormatException">It's set but not a public key set, with a message that says why.</exception>
        internal static KeySet? ReadKeys(IConfiguration configuration)
        {
            var section = configuration.GetSection(Constants.KeySetSetting);
            var text = section.Value;
            var source = Constants.KeySetSetting;
            if (string.IsNullOrWhiteSpace(text) && section.GetChildren().Any())
                text = SectionToJson(section);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = Environment.GetEnvironmentVariable(Constants.KeySetEnvironmentVariable);
                source = Constants.KeySetEnvironmentVariable;
            }
            if (string.IsNullOrWhiteSpace(text))
                return null;

            KeySet set;
            try
            {
                set = KeySet.Parse(text);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{source} is not a public key set: {ex.Message}");
            }

            // Whoever holds a private key can issue tokens: it stays with the issuer
            if (set.IsPrivate)
                throw new FormatException($"{source} holds a private key (d). Deploy the public set that `webhop issuer public` prints; the private one stays with the issuer.");
            return set;
        }

        /// <summary>A configuration section as JSON. Arrays reach IConfiguration as children named 0, 1, 2…</summary>
        private static string SectionToJson(IConfigurationSection section)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Write(writer, section);
            return Encoding.UTF8.GetString(stream.ToArray());

            static void Write(Utf8JsonWriter writer, IConfigurationSection section)
            {
                var children = section.GetChildren().ToList();
                if (children.Count == 0)
                {
                    if (section.Value is null)
                        writer.WriteNullValue();
                    else
                        writer.WriteStringValue(section.Value);
                    return;
                }

                if (children.All(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                {
                    writer.WriteStartArray();
                    foreach (var child in children.OrderBy(c => int.Parse(c.Key, CultureInfo.InvariantCulture)))
                        Write(writer, child);
                    writer.WriteEndArray();
                    return;
                }

                writer.WriteStartObject();
                foreach (var child in children)
                {
                    writer.WritePropertyName(child.Key);
                    Write(writer, child);
                }
                writer.WriteEndObject();
            }
        }
    }
}
