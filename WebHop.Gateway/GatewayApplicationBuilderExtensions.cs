using Yarp.ReverseProxy.Forwarder;
using WebHop.Core;
using WebHop.Gateway;

// Same namespace as UseWebSockets, so UseWebHopGateway() needs no extra using
namespace Microsoft.AspNetCore.Builder
{
    public static class GatewayApplicationBuilderExtensions
    {
        /// <summary>
        /// Routes traffic through the WebHop gateway. Origins open tunnels at the key set's tunnel endpoint
        /// (<see cref="Constants.DefaultWebHopEndpoint"/> unless it names another), with a token that says what
        /// they serve. Every other request is forwarded to an origin serving its host and path; when none does,
        /// it falls through to the next middleware, so a host can serve its own content (or end the pipeline
        /// with a 503). Call <c>AddWebHopGateway()</c> first and <c>UseWebSockets()</c> before this.
        /// </summary>
        public static IApplicationBuilder UseWebHopGateway(this IApplicationBuilder app)
        {
            var services = app.ApplicationServices;
            var registry = services.GetRequiredService<TunnelRegistry>();
            var authorization = services.GetRequiredService<TunnelAuthorization>();
            var forwarder = services.GetRequiredService<IHttpForwarder>();
            var tunnelClient = services.GetRequiredService<GatewayTunnelClient>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GatewayApplicationBuilderExtensions).Namespace!);

            if (authorization.Error is { } error)
                logger.LogError("Cannot use the key set: {Error} The gateway accepts no tunnels until it's fixed", error.TrimEnd('.') + ".");
            else if (authorization.Keys is not { } keys)
                logger.LogError("No key set: set {Variable} or the {Setting} setting to the public set from `webhop issuer public`. The gateway accepts no tunnels until then",
                    Constants.KeySetEnvironmentVariable, Constants.KeySetSetting);
            else
                // Nobody can read the encoded form, so say what was loaded
                logger.LogInformation("Accepting tunnels at {Endpoint} with tokens of issuer {Issuer}, keys {Keys}, serving within {Allow}",
                    keys.Endpoint, keys.Issuer, string.Join(", ", keys.Keys.Select(k => k.KeyId)), string.Join(", ", keys.Allow));

            // How many tunnels one origin may keep open, from the host's WebSocket budget (0 = unlimited)
            var maxConnectionsPerOrigin = WebSocketBudget.PerOriginCap(services.GetRequiredService<IConfiguration>());
            if (maxConnectionsPerOrigin > 0)
                logger.LogInformation("Allowing up to {Max} tunnels per origin (host WebSocket budget)", maxConnectionsPerOrigin);

            var endpoint = authorization.Endpoint;
            var statusPath = endpoint + "/status";
            var tunnels = new TunnelEndpoint(registry, authorization, services.GetRequiredService<HandshakeThrottle>(),
                services.GetRequiredService<IHostApplicationLifetime>(), logger, maxConnectionsPerOrigin);

            return app.Use(async (context, next) =>
            {
                var path = context.Request.Path;

                if (path.Equals(statusPath, StringComparison.OrdinalIgnoreCase))
                {
                    await context.Response.WriteAsync("WebHop Gateway status");
                    return;
                }

                // Origins open tunnel streams here: one WebSocket per HTTP connection
                if (path.Equals(endpoint, StringComparison.OrdinalIgnoreCase))
                {
                    await tunnels.AcceptAsync(context);
                    return;
                }

                // Public traffic: forward to an origin serving this host and path, or fall through when none does
                var originId = registry.PickOrigin(TunnelRegistry.HostKey(context.Request.Host), path.Value ?? "/");
                if (originId is null)
                {
                    await next(context);
                    return;
                }

                var error = await forwarder.SendAsync(context, $"http://{originId}/", tunnelClient.Invoker, tunnelClient.Config, new WebHopTransformer(originId));
                if (error != ForwarderError.None)
                {
                    var exception = context.Features.Get<IForwarderErrorFeature>()?.Exception;
                    // Cancellations are the client going away, not a tunnel problem
                    var level = error is ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled
                        or ForwarderError.ResponseBodyCanceled or ForwarderError.UpgradeRequestCanceled or ForwarderError.UpgradeResponseCanceled
                        ? LogLevel.Debug : LogLevel.Warning;
                    logger.Log(level, "{OriginId} {RequestId} forwarding failed: {Error} {Message}", originId, context.TraceIdentifier, error, exception?.Message);
                }
            });
        }
    }
}
