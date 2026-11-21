using WebHop.Core;
using WebHop.Gateway;

namespace WebHop.Tests.Infrastructure
{
    /// <summary>A real gateway on a loopback port, started in-process.</summary>
    internal sealed class TestGateway : IAsyncDisposable
    {
        private TestGateway(WebApplication app, string url)
        {
            App = app;
            Url = url;
            Client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
        }

        public WebApplication App { get; }
        public string Url { get; }
        public HttpClient Client { get; }
        public TunnelRegistry Registry => App.Services.GetRequiredService<TunnelRegistry>();

        /// <param name="port">0 picks a free port; pass a previous gateway's port to restart "the same" gateway.</param>
        /// <param name="maxWebSockets">Sets WebHop:MaxWebSockets, so the gateway caps tunnels per origin.</param>
        /// <param name="listenHost">
        /// "localhost" listens on both loopback addresses, so the gateway is reachable under two names
        /// (127.0.0.1 and localhost); it needs a fixed port.
        /// </param>
        /// <param name="keys">The issuer's set; <see cref="TestKeys.Set"/> by default.</param>
        public static Task<TestGateway> StartAsync(int port = 0, int? maxWebSockets = null, string listenHost = "127.0.0.1", KeySet? keys = null) =>
            StartWithSettingAsync((keys ?? TestKeys.Set).Encode(), port, maxWebSockets, listenHost);

        /// <param name="keySetSetting">The WebHop:KeySet setting as given; null for none.</param>
        public static async Task<TestGateway> StartWithSettingAsync(string? keySetSetting, int port = 0, int? maxWebSockets = null, string listenHost = "127.0.0.1")
        {
            List<string> args = ["--urls", $"http://{listenHost}:{port}", "--Logging:LogLevel:Default=Warning"];
            if (keySetSetting is not null)
                args.Add($"--WebHop:KeySet={keySetSetting}");
            if (maxWebSockets is { } max)
                args.Add($"--WebHop:MaxWebSockets={max}");

            // Mirror the deployable host, including its branded error pages
            var app = GatewayApp.Create([.. args], a => a.UseWebHopErrorPages());
            await app.StartAsync();
            return new TestGateway(app, app.Urls.Single());
        }

        public int Port => new Uri(Url).Port;

        /// <summary>The Host header of requests to <see cref="Url"/>.</summary>
        public string Host => new Uri(Url).Authority;

        /// <summary>The tunnel endpoint's WebSocket URL on <paramref name="host"/> (this gateway's address by default).</summary>
        public Uri TunnelUri(string? host = null, string endpoint = "/webhop") =>
            new($"ws://{host ?? "127.0.0.1"}:{Port}{endpoint}");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
