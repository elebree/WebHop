using WebHop.Core;
using WebHop.Origin;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Origin
{
    public class TunnelUriTests
    {
        [TestCase(null, "wss://app1.example.com/webhop")]
        [TestCase("https://gw.example.com/", "wss://gw.example.com/webhop")]
        [TestCase("https://gw.example.com", "wss://gw.example.com/webhop")]
        [TestCase("http://localhost:5199/", "ws://localhost:5199/webhop")]
        [TestCase("wss://gw.example.com/", "wss://gw.example.com/webhop")]
        [TestCase("https://gw.example.com:8443/", "wss://gw.example.com:8443/webhop")]
        public void The_token_or_the_gateway_url_becomes_the_tunnel_websocket_url(string? address, string expected)
        {
            Assert.That(WebHopServer.GetTunnelUri(address, Token("https://app1.example.com/drop/")).AbsoluteUri, Is.EqualTo(expected));
        }

        [TestCase("https://gw.example.com/custom/path")]
        [TestCase("https://gw.example.com/webhop")]
        [TestCase("https://gw.example.com/?x=1")]
        public void A_gateway_url_with_a_path_is_rejected_pointing_to_aud_and_ept(string address)
        {
            var error = Assert.Throws<InvalidOperationException>(() => WebHopServer.GetTunnelUri(address, Token("https://app1.example.com/")));
            Assert.That(error.Message, Does.Contain("aud").And.Contain("ept"));
        }

        [Test]
        public void A_token_with_paths_alone_needs_a_gateway_url()
        {
            var error = Assert.Throws<InvalidOperationException>(() => WebHopServer.GetTunnelUri(null, Token("/api/v1/")));
            Assert.That(error.Message, Does.Contain("--urls"));

            Assert.That(WebHopServer.GetTunnelUri("https://gw.example.com/", Token("/api/v1/")).AbsoluteUri, Is.EqualTo("wss://gw.example.com/webhop"));
        }

        [Test]
        public void Other_schemes_are_rejected()
        {
            Assert.Throws<InvalidOperationException>(() => WebHopServer.GetTunnelUri("ftp://gw.example.com/", Token("/")));
        }

        [TestCase("http://*:8080", true)]
        [TestCase("http://+:80", true)]
        [TestCase("http://0.0.0.0:5000", true)]
        [TestCase("http://[::]:5000", true)]
        [TestCase("https://gw.example.com/", false)]
        [TestCase("http://[::1]:5000", false)]
        public void Binding_wildcards_name_no_gateway(string address, bool wildcard)
        {
            Assert.That(WebHopServer.IsBindingWildcard(address), Is.EqualTo(wildcard));
        }

        private static OriginToken Token(string audience)
        {
            var claims = TestKeys.Claims(origin: "app1");
            claims["aud"] = audience;
            return OriginToken.Parse(TestKeys.Sign(claims));
        }
    }
}
