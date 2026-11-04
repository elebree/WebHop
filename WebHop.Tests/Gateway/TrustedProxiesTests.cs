using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebHop.Gateway;

namespace WebHop.Tests.Gateway
{
    /// <summary>ASP.NET Core's forwarded headers middleware, set up the way the standalone gateway sets it up.</summary>
    public class TrustedProxiesTests
    {
        [TestCase("127.0.0.1")]
        [TestCase("::1")]
        [TestCase("172.17.0.1")] // nginx on a Docker host, reaching the container from the bridge
        [TestCase("::ffff:172.17.0.1")] // the same, to Kestrel listening on [::]
        [TestCase("10.1.2.3")]
        [TestCase("192.168.1.10")]
        [TestCase("fd00::5")]
        public async Task A_proxy_on_loopback_or_a_private_network_is_believed(string proxy)
        {
            var context = await ForwardAsync(proxy, "203.0.113.7", "https");

            Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("203.0.113.7")));
            Assert.That(context.Request.Scheme, Is.EqualTo("https"));
        }

        [TestCase("203.0.113.9")]
        [TestCase("172.32.0.1")]
        [TestCase("::ffff:203.0.113.9")]
        [TestCase("2001:db8::9")]
        public async Task A_visitor_from_a_public_address_cannot_name_another_client(string visitor)
        {
            var context = await ForwardAsync(visitor, "198.51.100.66", "https");

            Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse(visitor)));
            Assert.That(context.Request.Scheme, Is.EqualTo("http"));
        }

        [Test]
        public async Task Only_the_address_the_proxy_added_counts()
        {
            // nginx's $proxy_add_x_forwarded_for appends the client to whatever the visitor sent
            var context = await ForwardAsync("172.17.0.1", "198.51.100.66, 203.0.113.7", null);

            Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("203.0.113.7")));
        }

        [TestCase("true", true)]
        [TestCase("TRUE", true)]
        [TestCase("false", false)]
        [TestCase(null, false)]
        public void ASPNETCORE_FORWARDEDHEADERS_ENABLED_means_every_proxy_is_believed(string? value, bool everyone)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders_Enabled"] = value })
                .Build();

            Assert.That(TrustedProxies.TrustEveryone(configuration), Is.EqualTo(everyone));
        }

        private static async Task<HttpContext> ForwardAsync(string from, string forwardedFor, string? proto)
        {
            var options = new ForwardedHeadersOptions();
            TrustedProxies.Configure(options);
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(from);
            context.Request.Scheme = "http";
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (proto is not null)
                context.Request.Headers["X-Forwarded-Proto"] = proto;

            var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
            await middleware.Invoke(context);
            return context;
        }
    }
}
