using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using WebHop.Core;
using WebHop.Gateway;
using WebHop.Origin;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Integration
{
    /// <summary>A real gateway and a real UseWebHop app, talking over loopback.</summary>
    public class TunnelTests
    {
        [Test]
        public async Task Requests_reach_the_app_through_the_gateway()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            using var response = await gateway.Client.GetAsync("/ping");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("pong"));
            Assert.That(response.Headers.GetValues(Headers.XWebhopOriginId).Single(), Is.EqualTo(app.Server.OriginId));
            Assert.That(app.Server.Status.State, Is.EqualTo(WebHopConnectionState.Online));
        }

        [Test]
        public async Task An_app_connects_where_its_token_says()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gatewayUrl: null, TestKeys.Token(origin: "app1", audience: [gateway.Url + "/"]));
            await app.WaitForTunnelsAsync();

            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
            Assert.That(app.Server.OriginId, Is.EqualTo("app1"));
            Assert.That(app.Server.TunnelUri!.AbsoluteUri, Is.EqualTo(gateway.Url.Replace("http://", "ws://") + "/webhop"));
            Assert.That(app.Server.PublicUrl!.AbsoluteUri, Is.EqualTo(gateway.Url + "/"));
        }

        [Test]
        public async Task Without_a_connected_app_the_gateway_answers_503()
        {
            await using var gateway = await TestGateway.StartAsync();

            using var response = await gateway.Client.GetAsync("/ping");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task A_gateway_error_is_a_branded_html_page()
        {
            await using var gateway = await TestGateway.StartAsync();

            using var response = await gateway.Client.GetAsync("/ping");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.That(body, Does.Contain("WebHop"));
            Assert.That(body, Does.Contain("503"));
        }

        [Test]
        public async Task The_app_sees_the_visitors_request_details_not_the_tunnels()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            using var response = await gateway.Client.GetAsync("/info");
            var info = await response.Content.ReadFromJsonAsync<InfoResponse>();

            Assert.That(info!.Scheme, Is.EqualTo("http"));
            Assert.That(info.Host, Is.EqualTo(new Uri(gateway.Url).Authority));
            Assert.That(info.RemoteIp, Is.EqualTo("127.0.0.1"));
            Assert.That(info.TraceIdentifier, Is.EqualTo(response.Headers.GetValues(Headers.XWebhopRequestId).Single()));
        }

        [Test]
        public async Task Behind_a_proxy_on_the_same_host_the_app_sees_the_client_it_names()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            // The test's requests come from loopback, as a proxy's on the same host would. A visitor from a public
            // address can't do this (TrustedProxiesTests).
            using var request = new HttpRequestMessage(HttpMethod.Get, "/info");
            request.Headers.Add(Headers.XForwardedProto, "https");
            request.Headers.Add(Headers.XForwardedFor, "203.0.113.7");
            using var response = await gateway.Client.SendAsync(request);
            var info = await response.Content.ReadFromJsonAsync<InfoResponse>();

            Assert.That(info!.Scheme, Is.EqualTo("https"));
            Assert.That(info.RemoteIp, Is.EqualTo("203.0.113.7"));
            Assert.That(info.ForwardedFor, Is.EqualTo("203.0.113.7"));
        }

        [Test]
        public async Task Multi_value_headers_and_late_headers_pass_through()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            using var cookies = await gateway.Client.GetAsync("/cookies");
            using var onStarting = await gateway.Client.GetAsync("/on-starting");

            Assert.That(cookies.Headers.GetValues("Set-Cookie").Count(), Is.EqualTo(2));
            Assert.That(onStarting.Headers.GetValues("X-On-Starting").Single(), Is.EqualTo("yes"));
        }

        [Test]
        public async Task An_app_exception_is_a_500_right_away()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            var stopwatch = Stopwatch.StartNew();
            using var response = await gateway.Client.GetAsync("/throw");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public async Task Large_bodies_stream_in_both_directions()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();
            const int size = 20 * 1024 * 1024;

            using var upload = await gateway.Client.PostAsync("/count", new ByteArrayContent(new byte[size]));
            var download = await gateway.Client.GetByteArrayAsync($"/bytes/{size}");

            Assert.That(await upload.Content.ReadAsStringAsync(), Is.EqualTo(size.ToString()));
            Assert.That(download.Length, Is.EqualTo(size));
        }

        [Test]
        public async Task Requests_run_concurrently_up_to_the_number_of_tunnels()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway, maxConnections: 4);
            await app.WaitForTunnelsAsync();

            var stopwatch = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gateway.Client.GetStringAsync("/slow/1000")));

            // One at a time would take 4 s
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        }

        [Test]
        public async Task WebSockets_work_through_the_tunnel()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(gateway.Url.Replace("http://", "ws://") + "/echo-ws"), CancellationToken.None);
            await ws.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[64];
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

            Assert.That(Encoding.UTF8.GetString(buffer, 0, result.Count), Is.EqualTo("hello"));
            Assert.That(ws.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
        }

        [Test]
        public async Task A_visitor_disconnecting_aborts_the_request_and_the_tunnel_is_replaced()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway, maxConnections: 2);
            await app.WaitForTunnelsAsync();

            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
                await Assert.CatchAsync<OperationCanceledException>(() => gateway.Client.GetAsync("/wait-for-abort", cts.Token));

            await app.RequestAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await app.WaitForTunnelsAsync();
            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
        }

        [Test]
        public async Task The_app_reconnects_after_the_gateway_restarts()
        {
            var gateway = await TestGateway.StartAsync();
            var port = gateway.Port;
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            await gateway.DisposeAsync();
            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Offline, "app notices the gateway is gone");

            await using var restarted = await TestGateway.StartAsync(port: port);
            await app.WaitForTunnelsAsync();

            Assert.That(await restarted.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
            Assert.That(app.Server.Status.State, Is.EqualTo(WebHopConnectionState.Online));
        }

        [Test]
        public async Task The_app_starts_before_the_gateway_and_connects_when_it_comes_up()
        {
            var port = await ReservePortAsync();

            await using var app = await TunnelApp.StartAsync($"http://127.0.0.1:{port}/", TestKeys.Token());
            await Task.Delay(500);
            Assert.That(app.Server.Status.State, Is.Not.EqualTo(WebHopConnectionState.Online));

            await using var gateway = await TestGateway.StartAsync(port: port);
            await app.WaitForTunnelsAsync();
            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
        }

        [Test]
        public async Task Stopping_the_app_is_fast_while_tunnels_are_open()
        {
            // Regression: pipes that were never completed made every shutdown wait for Kestrel's 30 s timeout
            await using var gateway = await TestGateway.StartAsync();
            var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();
            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));

            var stopwatch = Stopwatch.StartNew();
            await app.DisposeAsync();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            await Eventually.TrueAsync(() => gateway.Registry.OriginCount == 0, "gateway drops the stopped app");
        }

        [Test]
        public async Task Stopping_the_gateway_is_fast_while_tunnels_are_open()
        {
            // Regression: open tunnel requests made every gateway shutdown wait for the host's 30 s timeout
            var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway);
            await app.WaitForTunnelsAsync();

            var stopwatch = Stopwatch.StartNew();
            await gateway.DisposeAsync();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            await Eventually.TrueAsync(() => app.Server.Status.OpenTunnels == 0, "app sees its tunnels closed");
        }

        [Test]
        public async Task A_token_of_another_issuer_is_rejected()
        {
            var other = KeySet.Create([AudienceEntry.Parse("/")]);
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(set: other));

            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Unauthorized, "app reports unauthorized");
            Assert.That(gateway.Registry.OriginCount, Is.EqualTo(0));
            using var response = await gateway.Client.GetAsync("/ping");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task An_app_without_an_auth_token_refuses_to_start()
        {
            await using var gateway = await TestGateway.StartAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TunnelApp.StartAsync(gateway.Url, authToken: null));
            Assert.That(error.Message, Does.Contain(Constants.AuthTokenEnvironmentVariable));
        }

        [Test]
        public async Task An_app_with_something_else_than_a_token_refuses_to_start()
        {
            await using var gateway = await TestGateway.StartAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TunnelApp.StartAsync(gateway.Url, authToken: "s3cr3t-token"));
            Assert.That(error.Message, Does.Contain(Constants.TokenPrefix));
        }

        [Test]
        public async Task A_tunnel_can_authenticate_with_the_websocket_subprotocol()
        {
            // Browsers cannot set Authorization on a WebSocket, so the token rides in Sec-WebSocket-Protocol
            await using var gateway = await TestGateway.StartAsync();

            await using var tunnel = await RawTunnel.OpenAsync(gateway, TestKeys.Token(), inSubprotocol: true);

            Assert.That(tunnel.Status, Is.EqualTo(HttpStatusCode.SwitchingProtocols));
            Assert.That(tunnel.Socket.SubProtocol, Is.EqualTo(Constants.WebHopSubprotocol));
            await Eventually.TrueAsync(() => gateway.Registry.OriginCount == 1, "tunnel registered via subprotocol");
        }

        [Test]
        public async Task A_tunnel_with_a_wrong_subprotocol_token_is_rejected()
        {
            await using var gateway = await TestGateway.StartAsync();

            await using var tunnel = await RawTunnel.OpenAsync(gateway, "wh_wrong-token", inSubprotocol: true);

            Assert.That(tunnel.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(gateway.Registry.OriginCount, Is.EqualTo(0));
        }

        [Test]
        public async Task Too_many_refused_tokens_hold_off_the_address()
        {
            await using var gateway = await TestGateway.StartAsync();

            for (var i = 0; i < HandshakeThrottle.Limit; i++)
            {
                await using var refused = await RawTunnel.OpenAsync(gateway, "wh_wrong-token");
                Assert.That(refused.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
            }

            await using (var held = await RawTunnel.OpenAsync(gateway, "wh_wrong-token"))
            {
                Assert.That(held.Status, Is.EqualTo(HttpStatusCode.TooManyRequests));
                Assert.That(int.Parse(held.Header("Retry-After")!), Is.InRange(1, (int)HandshakeThrottle.Window.TotalSeconds));
            }
            // Its tokens aren't checked until the window is over, good ones included
            await using (var good = await RawTunnel.OpenAsync(gateway, TestKeys.Token()))
                Assert.That(good.Status, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(gateway.Registry.OriginCount, Is.EqualTo(0));

            // An origin there says it's a token problem, not an outage
            await using var app = await TunnelApp.StartAsync(gateway);
            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Unauthorized, "app reports unauthorized");
            Assert.That(app.Server.Status.LastError, Does.Contain("holds off"));
        }

        [Test]
        public async Task A_token_for_another_host_is_refused_and_the_app_says_so()
        {
            await using var gateway = await TestGateway.StartAsync();
            // Serves localhost, but connects to 127.0.0.1
            await using var app = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(audience: ["http://localhost/"]));

            await Eventually.TrueAsync(() => app.Server.Status.State == WebHopConnectionState.Forbidden, "app reports forbidden");
            Assert.That(app.Server.Status.LastError, Does.Contain("127.0.0.1"));
            Assert.That(gateway.Registry.OriginCount, Is.Zero);
        }

        [Test]
        public async Task A_token_claiming_more_than_allow_is_refused()
        {
            await using var gateway = await TestGateway.StartAsync();
            var claims = TestKeys.Claims();
            claims["aud"] = new[] { "/", "https://other.example.test/" };

            await using var tunnel = await RawTunnel.OpenAsync(gateway, TestKeys.Sign(claims));

            Assert.That(tunnel.Status, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task A_tunnel_closes_when_its_token_expires()
        {
            await using var gateway = await TestGateway.StartAsync();
            var claims = TestKeys.Claims();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            claims["iat"] = now - 600;
            // Expired, but within the clock skew for two more seconds
            claims["exp"] = now - (long)TokenValidator.ClockSkew.TotalSeconds + 2;
            var token = TestKeys.Sign(claims);

            await using var tunnel = await RawTunnel.OpenAsync(gateway, token);
            Assert.That(tunnel.Status, Is.EqualTo(HttpStatusCode.SwitchingProtocols));
            Assert.That(gateway.Registry.OriginCount, Is.EqualTo(1));

            await Eventually.TrueAsync(() => gateway.Registry.OriginCount == 0, "the gateway closes the tunnel", TimeSpan.FromSeconds(10));
            await using var again = await RawTunnel.OpenAsync(gateway, token);
            Assert.That(again.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        [Test]
        public async Task Each_origin_serves_the_paths_its_token_names()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var site = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(origin: "site"));
            await using var api = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(origin: "api", audience: ["/api/"]));
            await site.WaitForTunnelsAsync();
            await api.WaitForTunnelsAsync();

            Assert.That(await OriginOfAsync(gateway.Client, gateway.Host, "/api/ping"), Is.EqualTo("api"));
            Assert.That(await OriginOfAsync(gateway.Client, gateway.Host, "/ping"), Is.EqualTo("site"));
            Assert.That(await gateway.Client.GetStringAsync("/api/ping"), Is.EqualTo("api-pong"));
        }

        [Test]
        public async Task A_request_no_origin_serves_falls_through()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var api = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(audience: ["/api/"]));
            await api.WaitForTunnelsAsync();

            Assert.That(await gateway.Client.GetStringAsync("/api/ping"), Is.EqualTo("api-pong"));
            using var response = await gateway.Client.GetAsync("/ping");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task Tunnels_open_at_the_sets_endpoint()
        {
            var set = KeySet.Create([AudienceEntry.Parse("http://127.0.0.1/")], endpoint: "/t-test1");
            await using var gateway = await TestGateway.StartAsync(keys: set);
            await using var app = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(set: set));
            await app.WaitForTunnelsAsync();

            Assert.That(app.Server.TunnelUri!.AbsolutePath, Is.EqualTo("/t-test1"));
            Assert.That(await gateway.Client.GetStringAsync("/t-test1/status"), Is.EqualTo("WebHop Gateway status"));
            // /webhop is the app's now
            using var response = await gateway.Client.GetAsync("/webhop/status");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(response.Headers.Contains(Headers.XWebhopOriginId), Is.True);
        }

        [Test]
        public async Task The_gateway_advertises_the_tunnel_limit_of_the_token_and_the_budget()
        {
            await using var gateway = await TestGateway.StartAsync(maxWebSockets: 5); // 60% of 5 -> 3

            await using (var roomy = await RawTunnel.OpenAsync(gateway, TestKeys.Token(tunnels: 10)))
                Assert.That(roomy.Header(Headers.XWebhopMaxConnections), Is.EqualTo("3"));
            await using (var tight = await RawTunnel.OpenAsync(gateway, TestKeys.Token(tunnels: 2)))
                Assert.That(tight.Header(Headers.XWebhopMaxConnections), Is.EqualTo("2"));
        }

        [Test]
        public async Task The_gateway_enforces_the_tokens_tunnel_limit()
        {
            await using var gateway = await TestGateway.StartAsync();
            var token = TestKeys.Token(tunnels: 2);

            await using var first = await RawTunnel.OpenAsync(gateway, token);
            await using var second = await RawTunnel.OpenAsync(gateway, token);
            await using var third = await RawTunnel.OpenAsync(gateway, token);

            Assert.That(new[] { first.Status, second.Status, third.Status },
                Is.EqualTo(new[] { HttpStatusCode.SwitchingProtocols, HttpStatusCode.SwitchingProtocols, HttpStatusCode.ServiceUnavailable }));
        }

        [Test]
        public async Task An_origin_caps_its_tunnels_to_the_gateways_advertised_limit()
        {
            await using var gateway = await TestGateway.StartAsync(maxWebSockets: 5); // advertises 3
            await using var app = await TunnelApp.StartAsync(gateway, maxConnections: 10);

            await Eventually.TrueAsync(() => app.Server.Status.OpenTunnels == 3, "origin settles at the advertised cap");
            Assert.That(app.Server.Status.MaxTunnels, Is.EqualTo(3));

            // It should never climb past the cap, even though it was configured for 10
            await Task.Delay(500);
            Assert.That(app.Server.Status.OpenTunnels, Is.EqualTo(3));
            Assert.That(await gateway.Client.GetStringAsync("/ping"), Is.EqualTo("pong"));
        }

        [Test]
        public async Task An_origin_caps_its_tunnels_to_its_tokens_limit()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var app = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(tunnels: 2), maxConnections: 10);

            await app.WaitForTunnelsAsync();
            await Task.Delay(300);

            Assert.That(app.Server.Status.MaxTunnels, Is.EqualTo(2));
            Assert.That(app.Server.Status.OpenTunnels, Is.EqualTo(2));
        }

        [Test]
        public async Task Apps_connected_on_different_host_names_each_get_their_own_requests()
        {
            var port = await ReservePortAsync();
            await using var gateway = await TestGateway.StartAsync(port: port, listenHost: "localhost");
            await using var first = await TunnelApp.StartAsync($"http://127.0.0.1:{port}/", TestKeys.Token());
            await using var second = await TunnelApp.StartAsync($"http://localhost:{port}/", TestKeys.Token());
            await first.WaitForTunnelsAsync();
            await second.WaitForTunnelsAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            for (var i = 0; i < 4; i++)
            {
                Assert.That(await OriginOfAsync(client, $"127.0.0.1:{port}"), Is.EqualTo(first.Server.OriginId));
                Assert.That(await OriginOfAsync(client, $"LOCALHOST:{port}"), Is.EqualTo(second.Server.OriginId));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
            request.Headers.Host = "unknown.example.test";
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task An_origin_id_serving_a_path_on_one_host_name_is_refused_on_another()
        {
            var port = await ReservePortAsync();
            await using var gateway = await TestGateway.StartAsync(port: port, listenHost: "localhost");
            var token = TestKeys.Token(origin: "fixedid");
            await using var app = await TunnelApp.StartAsync($"http://127.0.0.1:{port}/", token);
            await app.WaitForTunnelsAsync();

            await using var other = await RawTunnel.OpenAsync(gateway, token, host: "localhost");

            Assert.That(other.Status, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(gateway.Registry.OriginCount, Is.EqualTo(1));
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Assert.That(await OriginOfAsync(client, $"127.0.0.1:{port}"), Is.EqualTo("fixedid"));
        }

        [Test]
        public async Task An_origin_id_connected_with_another_audience_is_refused_and_the_app_says_so()
        {
            await using var gateway = await TestGateway.StartAsync();
            await using var first = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(origin: "fixedid"));
            await first.WaitForTunnelsAsync();

            await using var second = await TunnelApp.StartAsync(gateway.Url, TestKeys.Token(origin: "fixedid", audience: ["/api/"]));

            await Eventually.TrueAsync(() => second.Server.Status.State == WebHopConnectionState.Conflict, "app reports the conflict");
            Assert.That(second.Server.Status.LastError, Does.Contain("fixedid"));
            Assert.That(first.Server.Status.State, Is.EqualTo(WebHopConnectionState.Online));
        }

        // A free port, by starting and stopping a gateway on it
        private static async Task<int> ReservePortAsync()
        {
            await using var probe = await TestGateway.StartAsync();
            return probe.Port;
        }

        // The origin that answered a request for the host and path
        private static async Task<string> OriginOfAsync(HttpClient client, string host, string path = "/ping")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), host + path);
            return response.Headers.GetValues(Headers.XWebhopOriginId).Single();
        }

        private sealed record InfoResponse(string Scheme, string Host, string? RemoteIp, string TraceIdentifier, string ForwardedFor);
    }
}
