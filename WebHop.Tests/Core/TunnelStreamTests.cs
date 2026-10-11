using System.Net.WebSockets;
using System.Text;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Core
{
    public class TunnelStreamTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Test]
        public async Task Bytes_written_on_one_side_are_read_on_the_other()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            await using var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);

            await server.WriteAsync("hello"u8.ToArray());

            Assert.That(await ReadStringAsync(client, 5), Is.EqualTo("hello"));
        }

        [Test]
        public async Task Large_payloads_arrive_complete_and_in_order()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            await using var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);
            var payload = Enumerable.Range(0, 1_000_000).Select(i => (byte)i).ToArray();

            var writing = Task.Run(async () =>
            {
                for (var offset = 0; offset < payload.Length; offset += 64 * 1024)
                    await server.WriteAsync(payload.AsMemory(offset, Math.Min(64 * 1024, payload.Length - offset)));
            });
            var received = new byte[payload.Length];
            await client.ReadExactlyAsync(received).AsTask().WaitAsync(Timeout);
            await writing;

            Assert.That(received, Is.EqualTo(payload));
        }

        [Test]
        public async Task A_cancelled_read_does_not_break_the_stream()
        {
            // Regression: cancelling a WebSocket receive aborts the socket, and Kestrel and
            // SocketsHttpHandler cancel reads routinely, which used to kill idle tunnels
            await using var pair = await WebSocketPair.CreateAsync();
            await using var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);

            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.CatchAsync<OperationCanceledException>(() => client.ReadAsync(new byte[16], cts.Token).AsTask());

            Assert.That(pair.Client.State, Is.EqualTo(WebSocketState.Open));
            await server.WriteAsync("still alive"u8.ToArray());
            Assert.That(await ReadStringAsync(client, 11), Is.EqualTo("still alive"));
        }

        [Test]
        public async Task A_zero_byte_read_waits_for_data_without_consuming_it()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            await using var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);

            var waiting = client.ReadAsync(Memory<byte>.Empty).AsTask();
            Assert.That(waiting.IsCompleted, Is.False);

            await server.WriteAsync("data"u8.ToArray());
            Assert.That(await waiting.WaitAsync(Timeout), Is.EqualTo(0));
            Assert.That(await ReadStringAsync(client, 4), Is.EqualTo("data"));
        }

        [Test]
        public async Task Disposing_one_side_ends_the_other_sides_reads()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);

            await server.DisposeAsync();

            Assert.That(await client.ReadAsync(new byte[16]).AsTask().WaitAsync(Timeout), Is.EqualTo(0));
            Assert.That(server.Closed.IsCompleted, Is.True);
        }

        [Test]
        public async Task Read_ahead_hands_early_data_to_the_first_read()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            await using var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);

            var readAhead = client.StartReadAhead();
            await server.WriteAsync("early"u8.ToArray());
            await readAhead.WaitAsync(Timeout);

            Assert.That(await ReadStringAsync(client, 5), Is.EqualTo("early"));
        }

        [Test]
        public async Task Read_ahead_completes_when_the_peer_closes_an_idle_stream()
        {
            // The gateway relies on this to drop tunnels that close while waiting in its pool
            await using var pair = await WebSocketPair.CreateAsync();
            var server = new TunnelStream(pair.Server);
            await using var client = new TunnelStream(pair.Client);

            var readAhead = client.StartReadAhead();
            await server.DisposeAsync();

            var result = await readAhead.WaitAsync(Timeout);
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
        }

        private static async Task<string> ReadStringAsync(Stream stream, int length)
        {
            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Timeout);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
