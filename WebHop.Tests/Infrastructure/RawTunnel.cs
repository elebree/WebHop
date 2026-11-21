using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using WebHop.Core;

namespace WebHop.Tests.Infrastructure
{
    /// <summary>
    /// A tunnel handshake made by hand, as a browser or another origin would: any token, any host name (dialed on
    /// loopback), the token in the Authorization header or a subprotocol. Keeps the tunnel open until disposed.
    /// </summary>
    internal sealed class RawTunnel : IAsyncDisposable
    {
        private readonly HttpMessageInvoker invoker;

        private RawTunnel(ClientWebSocket socket, HttpMessageInvoker invoker)
        {
            Socket = socket;
            this.invoker = invoker;
        }

        public ClientWebSocket Socket { get; }

        /// <summary>101 when accepted, else why not.</summary>
        public HttpStatusCode Status => Socket.HttpStatusCode;

        public string? Header(string name) =>
            Socket.HttpResponseHeaders?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

        /// <param name="host">The host the tunnel arrives on; the gateway is dialed on 127.0.0.1 whatever it is.</param>
        /// <param name="inSubprotocol">Sends the token as browsers do, in Sec-WebSocket-Protocol.</param>
        public static async Task<RawTunnel> OpenAsync(TestGateway gateway, string? token, string host = "127.0.0.1", string endpoint = "/webhop", bool inSubprotocol = false)
        {
            var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            if (inSubprotocol)
            {
                socket.Options.AddSubProtocol(Constants.WebHopSubprotocol);
                if (token is not null)
                    socket.Options.AddSubProtocol(Constants.TokenSubprotocolPrefix + token);
            }
            else if (token is not null)
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            }

            var invoker = new HttpMessageInvoker(new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await tcp.ConnectAsync(IPAddress.Loopback, gateway.Port, ct);
                    return new NetworkStream(tcp, ownsSocket: true);
                },
            });

            try
            {
                await socket.ConnectAsync(gateway.TunnelUri(host, endpoint), invoker, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Refused: the status says why
            }
            return new RawTunnel(socket, invoker);
        }

        public async ValueTask DisposeAsync()
        {
            if (Socket.State == WebSocketState.Open)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // The gateway closed it first
                }
            }
            Socket.Dispose();
            invoker.Dispose();
        }
    }
}
