using Microsoft.Extensions.Logging.Abstractions;
using WebHop.Core;
using WebHop.Gateway;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Gateway
{
    public class TunnelRegistryTests
    {
        private const string Host = "gateway.example.test";

        private TunnelRegistry registry = null!;

        [SetUp]
        public void SetUp()
        {
            registry = new TunnelRegistry(NullLogger<TunnelRegistry>.Instance);
        }

        [TestCase("abc123", true)]
        [TestCase("0123456789abcdef0123456789abcdef", true)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("ABC", false)]
        [TestCase("a-b", false)]
        [TestCase("a.b", false)]
        public void Origin_ids_must_be_valid_host_names(string? id, bool valid)
        {
            Assert.That(TunnelRegistry.IsValidOriginId(id), Is.EqualTo(valid));
        }

        [TestCase("gateway.example.test", "gateway.example.test")]
        [TestCase("App1.Example.TEST:8443", "app1.example.test")]
        [TestCase("example.test.", "example.test")]
        [TestCase("[::1]:5000", "[::1]")]
        [TestCase("", "")]
        public void Hosts_are_matched_without_port_case_or_trailing_dot(string host, string key)
        {
            Assert.That(TunnelRegistry.HostKey(new HostString(host)), Is.EqualTo(key));
        }

        [Test]
        public void No_origin_is_picked_while_none_is_connected()
        {
            Assert.That(registry.PickOrigin(Host), Is.Null);
            Assert.That(registry.OriginCount, Is.EqualTo(0));
        }

        [Test]
        public async Task A_registered_tunnel_can_be_taken_and_carries_data()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            _ = registry.RunTunnelAsync(Host, "abc", new TunnelStream(pair.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 1, "origin registered");

            Assert.That(registry.PickOrigin(Host), Is.EqualTo("abc"));
            var stream = await registry.TakeStreamAsync("abc", CancellationToken.None);
            await stream.WriteAsync("ping"u8.ToArray());

            var received = new byte[4];
            await using var client = new TunnelStream(pair.Client);
            await client.ReadExactlyAsync(received);
            Assert.That(received, Is.EqualTo("ping"u8.ToArray()));
        }

        [Test]
        public async Task Taking_a_tunnel_of_an_unknown_origin_fails()
        {
            await Assert.ThrowsAsync<IOException>(() => registry.TakeStreamAsync("nobody", CancellationToken.None).AsTask());
        }

        [Test]
        public async Task Taking_waits_while_all_tunnels_of_the_origin_are_in_use()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            _ = registry.RunTunnelAsync(Host, "abc", new TunnelStream(pair.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 1, "origin registered");
            await registry.TakeStreamAsync("abc", CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.CatchAsync<OperationCanceledException>(() => registry.TakeStreamAsync("abc", cts.Token).AsTask());
        }

        [Test]
        public async Task An_origin_is_removed_when_its_idle_tunnel_closes()
        {
            await using var pair = await WebSocketPair.CreateAsync();
            var running = registry.RunTunnelAsync(Host, "abc", new TunnelStream(pair.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 1, "origin registered");

            await new TunnelStream(pair.Client).DisposeAsync();

            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(registry.OriginCount, Is.EqualTo(0));
            Assert.That(registry.PickOrigin(Host), Is.Null);
        }

        [Test]
        public async Task Origins_take_turns()
        {
            await using var first = await WebSocketPair.CreateAsync();
            await using var second = await WebSocketPair.CreateAsync();
            _ = registry.RunTunnelAsync(Host, "one", new TunnelStream(first.Server), CancellationToken.None);
            _ = registry.RunTunnelAsync(Host, "two", new TunnelStream(second.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 2, "both origins registered");

            var picks = Enumerable.Range(0, 4).Select(_ => registry.PickOrigin(Host)).ToList();

            Assert.That(picks.Distinct().Count(), Is.EqualTo(2));
            Assert.That(picks[1], Is.Not.EqualTo(picks[0]));
            Assert.That(picks[2], Is.EqualTo(picks[0]));
        }

        [Test]
        public async Task An_origin_is_picked_only_for_the_host_it_connected_on()
        {
            await using var first = await WebSocketPair.CreateAsync();
            await using var second = await WebSocketPair.CreateAsync();
            _ = registry.RunTunnelAsync("app1.example.test", "one", new TunnelStream(first.Server), CancellationToken.None);
            _ = registry.RunTunnelAsync("app2.example.test", "two", new TunnelStream(second.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 2, "both origins registered");

            Assert.That(Enumerable.Range(0, 4).Select(_ => registry.PickOrigin("app1.example.test")), Is.All.EqualTo("one"));
            Assert.That(Enumerable.Range(0, 4).Select(_ => registry.PickOrigin("app2.example.test")), Is.All.EqualTo("two"));
            Assert.That(registry.PickOrigin("other.example.test"), Is.Null);
        }

        [Test]
        public async Task Picks_on_one_host_dont_skew_the_turns_on_another()
        {
            await using var first = await WebSocketPair.CreateAsync();
            await using var second = await WebSocketPair.CreateAsync();
            await using var other = await WebSocketPair.CreateAsync();
            _ = registry.RunTunnelAsync(Host, "one", new TunnelStream(first.Server), CancellationToken.None);
            _ = registry.RunTunnelAsync(Host, "two", new TunnelStream(second.Server), CancellationToken.None);
            _ = registry.RunTunnelAsync("other.example.test", "three", new TunnelStream(other.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 3, "all origins registered");

            // Interleaved with the other host, this host still alternates between its two origins
            var picks = Enumerable.Range(0, 4).Select(_ =>
            {
                registry.PickOrigin("other.example.test");
                return registry.PickOrigin(Host);
            }).ToList();

            Assert.That(picks.Distinct().Count(), Is.EqualTo(2));
            Assert.That(picks[1], Is.Not.EqualTo(picks[0]));
        }

        [Test]
        public async Task An_origin_connected_on_one_host_serves_no_other()
        {
            await using var first = await WebSocketPair.CreateAsync();
            await using var second = await WebSocketPair.CreateAsync();
            _ = registry.RunTunnelAsync("app1.example.test", "abc", new TunnelStream(first.Server), CancellationToken.None);
            await Eventually.TrueAsync(() => registry.OriginCount == 1, "origin registered");

            Assert.That(registry.ServesOtherHost("abc", "app1.example.test"), Is.False);
            Assert.That(registry.ServesOtherHost("abc", "app2.example.test"), Is.True);
            Assert.That(registry.ServesOtherHost("nobody", "app2.example.test"), Is.False);

            // A tunnel that slipped past the check is closed rather than added
            await registry.RunTunnelAsync("app2.example.test", "abc", new TunnelStream(second.Server), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(registry.PickOrigin("app2.example.test"), Is.Null);
            Assert.That(registry.PickOrigin("app1.example.test"), Is.EqualTo("abc"));
        }
    }
}
