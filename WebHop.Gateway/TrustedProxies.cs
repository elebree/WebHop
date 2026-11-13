using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace WebHop.Gateway
{
    /// <summary>
    /// The reverse proxies the standalone gateway believes about the client: those that connect from loopback or a
    /// private network. That covers the documented deployment, a TLS-terminating proxy in front of the gateway on the
    /// same host or network (nginx on a Docker host reaches the container from the bridge, 172.17.0.1), while a
    /// visitor who reaches the gateway directly from a public address can't name another client or scheme. The
    /// gateway then forwards the client the proxy named (<see cref="WebHopTransformer"/> writes X-Forwarded-*).
    /// </summary>
    internal static class TrustedProxies
    {
        // Loopback, the private IPv4 ranges and IPv6 unique local addresses. The IPv4 ones are listed again as
        // IPv4-mapped IPv6: Kestrel listening on [::] (as in the Docker image) sees an IPv4 peer that way.
        private static readonly (string Prefix, int Length)[] Networks =
        [
            ("127.0.0.0", 8),
            ("10.0.0.0", 8),
            ("172.16.0.0", 12),
            ("192.168.0.0", 16),
            ("::ffff:127.0.0.0", 104),
            ("::ffff:10.0.0.0", 104),
            ("::ffff:172.16.0.0", 108),
            ("::ffff:192.168.0.0", 112),
            ("::1", 128),
            ("fc00::", 7),
        ];

        /// <summary>Believes X-Forwarded-For and X-Forwarded-Proto from <see cref="Networks"/> only.</summary>
        public static void Configure(ForwardedHeadersOptions options)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
#if NET10_0_OR_GREATER
            options.KnownIPNetworks.Clear();
            foreach (var (prefix, length) in Networks)
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(prefix), length));
#else
            options.KnownNetworks.Clear();
            foreach (var (prefix, length) in Networks)
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(prefix), length));
#endif
        }

        /// <summary>
        /// Whether ASPNETCORE_FORWARDEDHEADERS_ENABLED is set. ASP.NET Core then believes every proxy, with a
        /// middleware of its own: for a proxy that connects from a public address, such as a CDN. The gateway leaves
        /// it at that.
        /// </summary>
        public static bool TrustEveryone(IConfiguration configuration) =>
            string.Equals(configuration["ForwardedHeaders_Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }
}
