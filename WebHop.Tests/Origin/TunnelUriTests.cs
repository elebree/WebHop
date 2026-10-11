using WebHop.Origin;

namespace WebHop.Tests.Origin
{
    public class TunnelUriTests
    {
        [TestCase("https://gw.example.com/", "wss://gw.example.com/webhop?id=abc")]
        [TestCase("https://gw.example.com", "wss://gw.example.com/webhop?id=abc")]
        [TestCase("http://localhost:5199/", "ws://localhost:5199/webhop?id=abc")]
        [TestCase("wss://gw.example.com/", "wss://gw.example.com/webhop?id=abc")]
        [TestCase("https://gw.example.com/custom/path", "wss://gw.example.com/custom/path?id=abc")]
        [TestCase("https://gw.example.com/custom?x=1", "wss://gw.example.com/custom?x=1&id=abc")]
        [TestCase("https://gw.example.com:8443/", "wss://gw.example.com:8443/webhop?id=abc")]
        public void The_gateway_url_becomes_the_tunnel_websocket_url(string address, string expected)
        {
            Assert.That(WebHopServer.GetTunnelUri(address, "abc").AbsoluteUri, Is.EqualTo(expected));
        }

        [Test]
        public void Other_schemes_are_rejected()
        {
            Assert.Throws<InvalidOperationException>(() => WebHopServer.GetTunnelUri("ftp://gw.example.com/", "abc"));
        }
    }
}
