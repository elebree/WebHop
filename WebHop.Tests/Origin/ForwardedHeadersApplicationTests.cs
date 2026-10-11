using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using WebHop.Core;
using WebHop.Origin;

namespace WebHop.Tests.Origin
{
    public class ForwardedHeadersApplicationTests
    {
        [Test]
        public void Scheme_client_address_and_request_id_come_from_the_gateway_headers()
        {
            var features = Features(new()
            {
                [Headers.XForwardedProto] = "https",
                [Headers.XForwardedFor] = "203.0.113.7:4567",
                [Headers.XWebhopRequestId] = "gateway-request-1",
            });

            new ForwardedHeadersApplication<IFeatureCollection>(new PassThroughApplication()).CreateContext(features);

            Assert.That(features.Get<IHttpRequestFeature>()!.Scheme, Is.EqualTo("https"));
            Assert.That(features.Get<IHttpConnectionFeature>()!.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("203.0.113.7")));
            Assert.That(features.Get<IHttpConnectionFeature>()!.RemotePort, Is.EqualTo(4567));
            Assert.That(features.Get<IHttpRequestIdentifierFeature>()!.TraceIdentifier, Is.EqualTo("gateway-request-1"));
        }

        [Test]
        public void IPv6_client_addresses_are_understood()
        {
            var features = Features(new() { [Headers.XForwardedFor] = "[2001:db8::1]:443" });

            new ForwardedHeadersApplication<IFeatureCollection>(new PassThroughApplication()).CreateContext(features);

            Assert.That(features.Get<IHttpConnectionFeature>()!.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("2001:db8::1")));
            Assert.That(features.Get<IHttpConnectionFeature>()!.RemotePort, Is.EqualTo(443));
        }

        [Test]
        public void Without_gateway_headers_nothing_changes()
        {
            var features = Features([]);
            features.Get<IHttpConnectionFeature>()!.RemoteIpAddress = IPAddress.Loopback;

            new ForwardedHeadersApplication<IFeatureCollection>(new PassThroughApplication()).CreateContext(features);

            Assert.That(features.Get<IHttpRequestFeature>()!.Scheme, Is.EqualTo("http"));
            Assert.That(features.Get<IHttpConnectionFeature>()!.RemoteIpAddress, Is.EqualTo(IPAddress.Loopback));
        }

        [Test]
        public void An_unparseable_client_address_is_ignored()
        {
            var features = Features(new() { [Headers.XForwardedFor] = "not an address" });
            features.Get<IHttpConnectionFeature>()!.RemoteIpAddress = IPAddress.Loopback;

            new ForwardedHeadersApplication<IFeatureCollection>(new PassThroughApplication()).CreateContext(features);

            Assert.That(features.Get<IHttpConnectionFeature>()!.RemoteIpAddress, Is.EqualTo(IPAddress.Loopback));
        }

        private static FeatureCollection Features(Dictionary<string, string> headers)
        {
            var request = new HttpRequestFeature { Scheme = "http" };
            foreach (var (name, value) in headers)
                request.Headers[name] = value;

            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(request);
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature());
            features.Set<IHttpRequestIdentifierFeature>(new HttpRequestIdentifierFeature());
            return features;
        }

        private sealed class PassThroughApplication : IHttpApplication<IFeatureCollection>
        {
            public IFeatureCollection CreateContext(IFeatureCollection contextFeatures) => contextFeatures;
            public Task ProcessRequestAsync(IFeatureCollection context) => Task.CompletedTask;
            public void DisposeContext(IFeatureCollection context, Exception? exception) { }
        }
    }
}
