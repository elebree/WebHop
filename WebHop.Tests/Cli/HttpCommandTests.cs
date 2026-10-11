using Microsoft.Extensions.Logging.Abstractions;
using WebHop.CLI;
using WebHop.Origin;

namespace WebHop.Tests.Cli
{
    public class HttpCommandTests
    {
        [TestCase(0.4, "0 ms")]
        [TestCase(12, "12 ms")]
        [TestCase(999, "999 ms")]
        [TestCase(1000, "1.0 s")]
        [TestCase(1540, "1.5 s")]
        [SetCulture("en-US")]
        public void Durations_are_shown_in_milliseconds_below_a_second(double milliseconds, string expected)
        {
            Assert.That(HttpCommand.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)), Is.EqualTo(expected));
        }

        [TestCase("https://gw.example.com/", "https://gw.example.com")]
        [TestCase("https://gw.example.com:8443/custom/path", "https://gw.example.com:8443")]
        [TestCase("wss://gw.example.com/webhop", "https://gw.example.com")]
        [TestCase("ws://localhost:5199/", "http://localhost:5199")]
        public void The_public_url_is_the_gateways_http_origin(string gateway, string expected)
        {
            using var server = new WebHopServer(new WebHopServerOptions(), NullLoggerFactory.Instance);

            var info = new SessionInfo(new Uri(gateway), new Uri("http://localhost:8080/"), server);

            Assert.That(info.PublicUrl, Is.EqualTo(expected));
        }

        [TestCase(null, "public.example.com")]
        [TestCase("rewrite", "localhost:3000")]
        [TestCase("custom.test", "custom.test")]
        public async Task The_host_header_follows_the_host_header_flag(string? hostHeader, string expected)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Host = new HostString("public.example.com");
            using var proxyRequest = new HttpRequestMessage();

            await new LocalTransformer(new Uri("http://localhost:3000/"), hostHeader)
                .TransformRequestAsync(context, proxyRequest, "http://localhost:3000/", CancellationToken.None);

            Assert.That(proxyRequest.Headers.Host, Is.EqualTo(expected));
        }

        [Test]
        public void Host_errors_are_reported_to_the_session()
        {
            var session = new Session();
            using var provider = new SessionLoggerProvider(session);

            provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel")
                .LogError(new InvalidOperationException("outer", new IOException("root cause")), "ignored message");

            Assert.That(session.LastError, Is.EqualTo("root cause"));
        }

        [Test]
        public void Host_messages_below_error_are_not_reported()
        {
            var session = new Session();
            using var provider = new SessionLoggerProvider(session);

            provider.CreateLogger("Microsoft.Hosting.Lifetime").LogWarning("just a warning");

            Assert.That(session.LastError, Is.Null);
        }

        [Test]
        public void Tunnel_errors_are_left_to_the_server_status()
        {
            var session = new Session();
            using var provider = new SessionLoggerProvider(session);

            provider.CreateLogger(typeof(WebHopServer).FullName!).LogError("gateway unreachable");

            Assert.That(session.LastError, Is.Null);
        }
    }
}
