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
    /// that could make a token. Only tunnel registration is protected; public traffic stays open.
    /// </summary>
    public sealed class TunnelAuthorization
    {
        private readonly TokenValidator validator;

        /// <exception cref="GatewayConfigurationException">No set is configured, or it can't be used.</exception>
        public TunnelAuthorization(IConfiguration configuration)
        {
            Keys = ReadKeys(configuration);
            validator = new TokenValidator(Keys);
        }

        /// <summary>The issuer's public key set.</summary>
        public KeySet Keys { get; }

        /// <summary>Where origins open tunnels: the set's <c>ept</c>, or <see cref="Constants.DefaultWebHopEndpoint"/>.</summary>
        public string Endpoint => Keys.Endpoint;

        /// <summary>Checks the token itself: a failure means 401.</summary>
        public TokenValidation Validate(string? token) => validator.Validate(token);

        /// <summary>Whether a valid token may serve on the tunnel's host; why not, or null. A refusal means 403.</summary>
        public string? CheckHost(OriginToken token, string host) => validator.CheckHost(token, host);

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
        /// variable.
        /// </summary>
        /// <exception cref="GatewayConfigurationException">Neither is set, or it's not a public key set; the message says why.</exception>
        private static KeySet ReadKeys(IConfiguration configuration)
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
                throw new GatewayConfigurationException(
                    $"No key set: set {Constants.KeySetEnvironmentVariable} or the {Constants.KeySetSetting} setting to the public set from `webhop issuer public`.");

            KeySet set;
            try
            {
                set = KeySet.Parse(text);
            }
            catch (FormatException ex)
            {
                throw new GatewayConfigurationException($"{source} is not a public key set: {ex.Message.TrimEnd('.')}.");
            }

            // Whoever holds a private key can issue tokens: it stays with the issuer
            if (set.IsPrivate)
                throw new GatewayConfigurationException($"{source} holds a private key (d). Deploy the public set that `webhop issuer public` prints; the private one stays with the issuer.");
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
