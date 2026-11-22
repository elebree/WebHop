using System.Globalization;
using System.Net;
using WebHop.Core;

namespace WebHop.Gateway
{
    /// <summary>
    /// Where origins open tunnels. A tunnel is accepted when its token is good (else 401: a bad, expired or
    /// revoked token, an unknown kid, a wrong typ), may serve on the host it arrived on (else 403), its origin id
    /// isn't connected serving something else (else 409), and the origin has fewer tunnels than its token's
    /// <c>tun</c> and the host's WebSocket budget allow (else 503). 401 and 403 count towards
    /// <see cref="HandshakeThrottle"/>. A tunnel lives no longer than its token.
    /// </summary>
    internal sealed class TunnelEndpoint(TunnelRegistry registry, TunnelAuthorization authorization, HandshakeThrottle throttle,
        IHostApplicationLifetime lifetime, ILogger logger, int maxConnectionsPerOrigin)
    {
        // The longest a timer waits; a tunnel open that long is closed then, and its origin reconnects
        private static readonly TimeSpan LongestDelay = TimeSpan.FromMilliseconds(int.MaxValue);

        public async Task AcceptAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // An address that presented too many refused tokens isn't checked again until its window is over
            var remoteIp = context.Connection.RemoteIpAddress;
            if (throttle.HeldOff(remoteIp) is { } wait)
            {
                logger.LogDebug("Held off a tunnel from {RemoteIp} for another {Seconds} s", remoteIp, Math.Ceiling(wait.TotalSeconds));
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return;
            }

            // The token itself. Refusals name the origin and serial it claims, never the token.
            var validation = authorization.Validate(TunnelAuthorization.TokenFromRequest(context));
            if (validation is not { IsValid: true, Token: { } token })
            {
                logger.LogWarning("Rejected tunnel from {RemoteIp}: {Reason} (origin {OriginId}, serial {Serial})",
                    remoteIp, validation.Failure, validation.Token?.Subject ?? "-", validation.Token?.Id ?? "-");
                Refuse(context, StatusCodes.Status401Unauthorized, remoteIp);
                return;
            }

            // What it serves, against allow and the host the tunnel arrived on
            var host = TunnelRegistry.HostKey(context.Request.Host);
            if (authorization.CheckHost(token, host) is { } refusal)
            {
                logger.LogWarning("Rejected tunnel of {OriginId} on {Host} from {RemoteIp}: {Reason} (serial {Serial})",
                    token.Subject, host, remoteIp, refusal, token.Id);
                Refuse(context, StatusCodes.Status403Forbidden, remoteIp);
                return;
            }

            // Tunnels count per origin id, so processes sharing a token share its tun
            var maxTunnels = maxConnectionsPerOrigin > 0 ? Math.Min(token.Tunnels, maxConnectionsPerOrigin) : token.Tunnels;
            var served = ServedRoute.Of(token, host);
            switch (registry.Admit(token.Subject, served, maxTunnels))
            {
                case Admission.OtherAudience:
                    logger.LogWarning("Rejected tunnel of {OriginId} on {Host}: the origin id is connected serving something else (serial {Serial})",
                        token.Subject, host, token.Id);
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;

                case Admission.Full:
                    logger.LogDebug("Rejected tunnel of {OriginId}: it has {Max} open, as many as it may", token.Subject, maxTunnels);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
            }

            System.Net.WebSockets.WebSocket webSocket;
            try
            {
                // Tell the origin how many tunnels it may keep open
                context.Response.Headers[Headers.XWebhopMaxConnections] = maxTunnels.ToString(CultureInfo.InvariantCulture);

                // Echo only the fixed marker when a browser authenticated through a subprotocol, never the token
                var subProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(Constants.WebHopSubprotocol)
                    ? Constants.WebHopSubprotocol
                    : null;
                webSocket = await context.WebSockets.AcceptWebSocketAsync(subProtocol);
            }
            catch
            {
                registry.Release(token.Subject);
                throw;
            }

            // Close tunnels when the token expires, so an idle pooled tunnel doesn't serve for up to an hour
            // more; its origin's reconnect then gets 401 until it has a new token. Tokens are accepted until
            // that much skew past exp, so they're closed then too.
            using var expiry = new CancellationTokenSource(Until(token.Expires + TokenValidator.ClockSkew));
            using var expired = expiry.Token.Register(() =>
                logger.LogDebug("Closing a tunnel of {OriginId}: its token expires (serial {Serial})", token.Subject, token.Id));
            // Close tunnels as soon as the gateway stops; otherwise shutdown waits for each tunnel
            // request until the host's 30 s timeout, and origins reconnect that much later
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping, expiry.Token);
            await registry.RunTunnelAsync(token.Subject, new TunnelStream(webSocket), closing.Token);
        }

        private void Refuse(HttpContext context, int status, IPAddress? remoteIp)
        {
            if (throttle.Reject(remoteIp))
                logger.LogWarning("Holding off tunnels from {RemoteIp}: {Limit} refused tokens within {Seconds} s",
                    remoteIp, HandshakeThrottle.Limit, HandshakeThrottle.Window.TotalSeconds);
            context.Response.StatusCode = status;
        }

        private static TimeSpan Until(DateTimeOffset time)
        {
            var delay = time - DateTimeOffset.UtcNow;
            return delay <= TimeSpan.Zero ? TimeSpan.Zero : delay < LongestDelay ? delay : LongestDelay;
        }
    }
}
