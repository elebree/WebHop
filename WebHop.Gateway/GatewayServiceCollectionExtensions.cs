using WebHop.Gateway;

// Same namespace as AddHttpForwarder, so AddWebHopGateway() needs no extra using
namespace Microsoft.Extensions.DependencyInjection
{
    public static class GatewayServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the WebHop gateway services: the tunnel registry, the token check with the issuer's
        /// public key set and its throttle on refused tokens, YARP's direct forwarder and the
        /// tunnel-resolving HTTP client. Pair with <c>UseWebHopGateway()</c>.
        /// </summary>
        public static IServiceCollection AddWebHopGateway(this IServiceCollection services)
        {
            services.AddHttpForwarder();
            services.AddSingleton<TunnelRegistry>();
            services.AddSingleton<TunnelAuthorization>();
            services.AddSingleton(_ => new HandshakeThrottle());
            services.AddSingleton<GatewayTunnelClient>();
            return services;
        }
    }
}
