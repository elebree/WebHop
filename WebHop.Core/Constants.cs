namespace WebHop.Core
{
    public static class Constants
    {
        /// <summary>Where origins open tunnels when the key set and the token name no other <c>ept</c>.</summary>
        public const string DefaultWebHopEndpoint = "/webhop";
        public const string WebHopProtoVersion = "2.0";

        /// <summary>
        /// WebSocket subprotocol the gateway selects when a tunnel authenticates through the handshake's
        /// <c>Sec-WebSocket-Protocol</c> header instead of <c>Authorization</c> (browsers cannot set the
        /// latter). The token rides as a second offered subprotocol, as is: <c>webhop.token.wh_eyJ…</c>.
        /// The gateway echoes only this fixed marker, never the token.
        /// </summary>
        public const string WebHopSubprotocol = "webhop";

        /// <summary>Prefix of the offered subprotocol that carries the origin's token.</summary>
        public const string TokenSubprotocolPrefix = "webhop.token.";

        /// <summary>Prefix of every origin token (<c>wh_</c> and a JWT), so a leaked one is recognizable.</summary>
        public const string TokenPrefix = "wh_";

        /// <summary>JWT <c>typ</c> of origin tokens, so a JWT the issuer signed for something else doesn't pass.</summary>
        public const string TokenType = "webhop+jwt";

        /// <summary>Tunnels an origin may keep open at once when its token has no <c>tun</c>.</summary>
        public const int DefaultTunnels = 3;

        /// <summary>Environment variable holding the origin's token, read by apps and the CLI.</summary>
        public const string AuthTokenEnvironmentVariable = "WEBHOP_AUTHTOKEN";

        /// <summary>
        /// Configuration key for the origin's token (appsettings, user secrets, Azure app settings), read by
        /// apps. Takes precedence over <see cref="AuthTokenEnvironmentVariable"/>.
        /// </summary>
        public const string AuthTokenSetting = "WebHop:AuthToken";

        /// <summary>Environment variable holding the gateway's public key set (<c>whk_…</c> or JSON).</summary>
        public const string KeySetEnvironmentVariable = "WEBHOP_KEYSET";

        /// <summary>
        /// Gateway configuration key for the public key set: <c>whk_…</c>, JSON, or a nested object in
        /// appsettings.json. Takes precedence over <see cref="KeySetEnvironmentVariable"/>.
        /// </summary>
        public const string KeySetSetting = "WebHop:KeySet";

        /// <summary>Prefix of an encoded key set: <c>whk_</c> and the base64url of its JSON.</summary>
        public const string KeySetPrefix = "whk_";

        /// <summary>
        /// Optional gateway setting (<c>WebHop:MaxWebSockets</c>) overriding the host's total WebSocket
        /// budget. When unset the gateway infers it from <see cref="WebsiteSkuEnvironmentVariable"/>.
        /// </summary>
        public const string MaxWebSocketsSetting = "WebHop:MaxWebSockets";

        /// <summary>Azure App Service sets this to the pricing tier (Free, Shared, Basic, ...).</summary>
        public const string WebsiteSkuEnvironmentVariable = "WEBSITE_SKU";
    }
}
