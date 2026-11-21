using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebHop.Core;

namespace WebHop.Origin
{
    /// <summary>
    /// Serves the application through a WebHop gateway instead of a local port. The token
    /// (<see cref="WebHopServerOptions.AuthToken"/>) says where to connect: the first URL it serves, at its
    /// tunnel endpoint. The first configured URL (--urls / applicationUrl), if any, overrides the scheme, host
    /// and port. Internally this is Kestrel whose connections are tunnel streams opened to the gateway.
    /// </summary>
    public sealed class WebHopServer : IServer, IAsyncDisposable
    {
        private readonly WebHopServerOptions options;
        private readonly ILoggerFactory loggerFactory;
        private readonly bool ownsLoggerFactory;
        private WebHopConnectionListenerFactory? listenerFactory;
        private KestrelServer? kestrel;

        public WebHopServer() : this(new WebHopServerOptions())
        {
        }

        public WebHopServer(WebHopServerOptions options, ILoggerFactory? loggerFactory = null)
        {
            this.options = options;
            ownsLoggerFactory = loggerFactory is null;
            this.loggerFactory = loggerFactory ?? LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Information));

            Features = new FeatureCollection();
            Features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
        }

        public IFeatureCollection Features { get; }

        /// <summary>The origin's token, read when the server starts.</summary>
        public OriginToken? Token { get; private set; }

        /// <summary>How the gateway identifies this origin: the token's <c>sub</c>, once started.</summary>
        public string? OriginId => Token?.Subject;

        /// <summary>The WebSocket URL tunnels connect to, once started.</summary>
        public Uri? TunnelUri { get; private set; }

        /// <summary>Where visitors reach the app, once started.</summary>
        public Uri? PublicUrl { get; private set; }

        /// <summary>Current state of the tunnels to the gateway.</summary>
        public WebHopOriginStatus Status =>
            listenerFactory?.Listener?.Status
            ?? new WebHopOriginStatus(WebHopConnectionState.Connecting, 0, Math.Min(options.MaxConnections, Token?.Tunnels ?? int.MaxValue), null);

        public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken ct) where TContext : notnull
        {
            // The gateway rejects every tunnel without one; fail now instead of retrying forever
            if (string.IsNullOrWhiteSpace(options.AuthToken))
                throw new InvalidOperationException(
                    $"No WebHop auth token configured. Set {Constants.AuthTokenEnvironmentVariable}, the {Constants.AuthTokenSetting} setting or WebHopServerOptions.AuthToken.");
            if (!OriginToken.TryParse(options.AuthToken, out var token, out var problem))
                throw new InvalidOperationException($"The WebHop auth token can't be used: {problem}. Issue one with `webhop-gen token issue`.");

            var addresses = Features.Get<IServerAddressesFeature>()?.Addresses;
            // ASPNETCORE_HTTP_PORTS in container images adds http://*:8080, which names no gateway
            var address = addresses?.FirstOrDefault(a => !IsBindingWildcard(a));
            var tunnelUri = GetTunnelUri(address, token);
            Token = token;
            TunnelUri = tunnelUri;
            PublicUrl = token.PublicUrl(tunnelUri);
            if (addresses is { IsReadOnly: false } && address is null)
            {
                // So the host logs where the app is reachable ("Now listening on")
                addresses.Clear();
                addresses.Add(PublicUrl.ToString());
            }

            var logger = loggerFactory.CreateLogger<WebHopServer>();
            if (tunnelUri.Scheme == "ws" && !tunnelUri.IsLoopback)
                logger.LogWarning("The WebHop auth token is sent unencrypted to {Gateway}; use an https:// gateway", tunnelUri.GetLeftPart(UriPartial.Authority));
            if (token.Expires < DateTimeOffset.UtcNow)
                logger.LogWarning("The WebHop auth token expired at {Expires:u}; the gateway refuses it. Issue a new one", token.Expires);

            var kestrelOptions = new KestrelServerOptions
            {
                ApplicationServices = new ServiceCollection().AddSingleton(loggerFactory).BuildServiceProvider(),
            };
            kestrelOptions.Limits.MaxRequestBodySize = null;
            // Idle tunnel streams wait for the gateway; it closes pooled connections it no longer needs.
            // Not infinite: Kestrel then aborts every connection within a second (write data rate timeout)
            kestrelOptions.Limits.KeepAliveTimeout = TimeSpan.FromHours(1);
            kestrelOptions.Listen(new WebHopEndPoint(tunnelUri), listen => listen.Protocols = HttpProtocols.Http1);

            listenerFactory = new WebHopConnectionListenerFactory(options, token, loggerFactory);
            kestrel = new KestrelServer(Options.Create(kestrelOptions), listenerFactory, loggerFactory);

            await kestrel.StartAsync(new ForwardedHeadersApplication<TContext>(application), ct);
        }

        public Task StopAsync(CancellationToken ct)
        {
            return kestrel?.StopAsync(ct) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            kestrel?.Dispose();
            if (ownsLoggerFactory)
                loggerFactory.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// The WebSocket URL tunnels connect to: the scheme, host and port of <paramref name="address"/> when
        /// given, else of the token's first URL or pattern, at the token's tunnel endpoint.
        /// </summary>
        internal static Uri GetTunnelUri(string? address, OriginToken token)
        {
            Uri? gateway = null;
            if (address is not null)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out gateway))
                    throw new InvalidOperationException($"The WebHop gateway URL `{address}` is not a URL.");
                // The token says what the app serves (aud) and where tunnels connect (ept)
                if (gateway.AbsolutePath != "/" || gateway.Query.Length > 0 || gateway.Fragment.Length > 0)
                    throw new InvalidOperationException(
                        $"The WebHop gateway URL {address} has a path. It takes the scheme, host and port only: the token's aud says what the app serves, and its ept where tunnels connect.");
            }

            try
            {
                return token.TunnelUri(gateway)
                    ?? throw new InvalidOperationException(
                        $"The WebHop auth token serves only paths ({string.Join(", ", token.Audience)}), so it needs the gateway's URL: pass it with --urls or applicationUrl.");
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        /// <summary>A listening address such as http://*:8080 or http://+:80, which binds every interface but names no host.</summary>
        internal static bool IsBindingWildcard(string address)
        {
            var start = address.IndexOf("://", StringComparison.Ordinal);
            if (start < 0)
                return false;
            var host = address[(start + 3)..];
            var end = host.StartsWith('[') ? host.IndexOf(']') + 1 : host.IndexOfAny([':', '/']);
            if (end > 0)
                host = host[..end];
            return host is "*" or "+" or "0.0.0.0" or "[::]";
        }
    }
}
