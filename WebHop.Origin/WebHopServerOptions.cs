using WebHop.Core;

namespace WebHop.Origin
{
    public sealed class WebHopServerOptions
    {
        /// <summary>
        /// The origin's token (<c>wh_…</c>), issued with <c>webhop-gen token issue</c>. It says the origin id,
        /// what the app serves and, unless the gateway URL is given, where to connect. Defaults to the
        /// WEBHOP_AUTHTOKEN environment variable; UseWebHop overrides it with the WebHop:AuthToken setting when
        /// that is present. Sent as a bearer token on every tunnel's WebSocket upgrade, so use a wss://
        /// (https://) gateway outside of development.
        /// </summary>
        public string? AuthToken { get; set; } = Environment.GetEnvironmentVariable(Constants.AuthTokenEnvironmentVariable);

        /// <summary>
        /// Upper bound on the tunnel streams kept open to the gateway. Each carries one HTTP connection,
        /// so this caps the number of concurrent requests (and upgraded WebSockets). The token's <c>tun</c>
        /// and the gateway (from its WebSocket budget) may set a lower limit, and the server honors the
        /// smallest — so this is a ceiling, not a guarantee.
        /// </summary>
        public int MaxConnections { get; set; } = 10;

        /// <summary>Upper bound for the delay between reconnect attempts while the gateway is unreachable.</summary>
        public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>WebSocket ping interval of tunnel streams.</summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
    }
}
