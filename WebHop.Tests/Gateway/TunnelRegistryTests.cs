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
        private readonly List<WebSocketPair> pairs = [];

        [SetUp]
        public void SetUp()
        {
            registry = new TunnelRegistry(NullLogger<TunnelRegistry>.Instance);
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (var pair in pairs)
                await pair.DisposeAsync();
            pairs.Clear();
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
            Assert.That(registry.PickOrigin(Host, "/"), Is.Null);
            Assert.That(registry.OriginCount, Is.EqualTo(0));
        }

        [Test]
        public async Task A_registered_tunnel_can_be_taken_and_carries_data()
        {
            var pair = await ConnectAsync("abc", Route(Host));

            Assert.That(registry.PickOrigin(Host, "/"), Is.EqualTo("abc"));
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
            await ConnectAsync("abc", Route(Host));
            await registry.TakeStreamAsync("abc", CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.CatchAsync<OperationCanceledException>(() => registry.TakeStreamAsync("abc", cts.Token).AsTask());
        }

        [Test]
        public async Task An_origin_is_removed_when_its_idle_tunnel_closes()
        {
            var pair = await WebSocketPair.CreateAsync();
            pairs.Add(pair);
            Assert.That(registry.Admit("abc", [Route(Host)], 3), Is.EqualTo(Admission.Admitted));
            var running = registry.RunTunnelAsync("abc", new TunnelStream(pair.Server), CancellationToken.None);

            await new TunnelStream(pair.Client).DisposeAsync();

            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(registry.OriginCount, Is.EqualTo(0));
            Assert.That(registry.PickOrigin(Host, "/"), Is.Null);
        }

        [Test]
        public async Task Origins_on_the_same_host_and_path_take_turns()
        {
            await ConnectAsync("one", Route(Host));
            await ConnectAsync("two", Route(Host));

            var picks = Enumerable.Range(0, 4).Select(_ => registry.PickOrigin(Host, "/")).ToList();

            Assert.That(picks.Distinct().Count(), Is.EqualTo(2));
            Assert.That(picks[1], Is.Not.EqualTo(picks[0]));
            Assert.That(picks[2], Is.EqualTo(picks[0]));
        }

        [Test]
        public async Task An_origin_is_picked_only_for_the_host_it_serves()
        {
            await ConnectAsync("one", Route("app1.example.test"));
            await ConnectAsync("two", Route("app2.example.test"));

            Assert.That(Enumerable.Range(0, 4).Select(_ => registry.PickOrigin("app1.example.test", "/")), Is.All.EqualTo("one"));
            Assert.That(Enumerable.Range(0, 4).Select(_ => registry.PickOrigin("app2.example.test", "/")), Is.All.EqualTo("two"));
            Assert.That(registry.PickOrigin("other.example.test", "/"), Is.Null);
        }

        [Test]
        public async Task Picks_on_one_host_dont_skew_the_turns_on_another()
        {
            await ConnectAsync("one", Route(Host));
            await ConnectAsync("two", Route(Host));
            await ConnectAsync("three", Route("other.example.test"));

            // Interleaved with the other host, this host still alternates between its two origins
            var picks = Enumerable.Range(0, 4).Select(_ =>
            {
                registry.PickOrigin("other.example.test", "/");
                return registry.PickOrigin(Host, "/");
            }).ToList();

            Assert.That(picks.Distinct().Count(), Is.EqualTo(2));
            Assert.That(picks[1], Is.Not.EqualTo(picks[0]));
        }

        [Test]
        public async Task The_longest_path_wins_on_segment_boundaries()
        {
            await ConnectAsync("site", Route(Host));
            await ConnectAsync("api", Route(Host, "/api"));
            await ConnectAsync("v2", Route(Host, "/api/v2"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.PickOrigin(Host, "/"), Is.EqualTo("site"));
                Assert.That(registry.PickOrigin(Host, "/api"), Is.EqualTo("api"));
                Assert.That(registry.PickOrigin(Host, "/API/v1/users"), Is.EqualTo("api"));
                Assert.That(registry.PickOrigin(Host, "/api/v2/users"), Is.EqualTo("v2"));
                Assert.That(registry.PickOrigin(Host, "/apix"), Is.EqualTo("site"));
            }
        }

        [Test]
        public async Task A_request_no_origin_serves_is_left_to_the_pipeline()
        {
            await ConnectAsync("api", Route(Host, "/api"));

            Assert.That(registry.PickOrigin(Host, "/api/x"), Is.EqualTo("api"));
            Assert.That(registry.PickOrigin(Host, "/login"), Is.Null);
        }

        [Test]
        public async Task A_pattern_serves_one_label_and_loses_to_an_exact_host_at_equal_paths()
        {
            await ConnectAsync("catchall", Route("*.example.test"));
            await ConnectAsync("robots", Route("*.example.test", "/robots.txt"));
            await ConnectAsync("app1", Route("app1.example.test"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(registry.PickOrigin("app1.example.test", "/"), Is.EqualTo("app1"));
                Assert.That(registry.PickOrigin("app1.example.test", "/robots.txt"), Is.EqualTo("robots"));
                Assert.That(registry.PickOrigin("app2.example.test", "/"), Is.EqualTo("catchall"));
                Assert.That(registry.PickOrigin("app2.example.test", "/robots.txt"), Is.EqualTo("robots"));
                Assert.That(registry.PickOrigin("a.b.example.test", "/"), Is.Null);
                Assert.That(registry.PickOrigin("example.test", "/"), Is.Null);
            }
        }

        [Test]
        public async Task An_origin_serves_every_entry_of_its_audience()
        {
            await ConnectAsync("app1", Route("app1.example.test"), Route("app1.example.org"));

            Assert.That(registry.PickOrigin("app1.example.test", "/"), Is.EqualTo("app1"));
            Assert.That(registry.PickOrigin("app1.example.org", "/"), Is.EqualTo("app1"));
        }

        [Test]
        public async Task An_origin_id_serves_one_audience_at_a_time()
        {
            await ConnectAsync("abc", Route("app1.example.test"));

            Assert.That(registry.Admit("abc", [Route("app2.example.test")], 3), Is.EqualTo(Admission.OtherAudience));
            Assert.That(registry.Admit("abc", [Route("app1.example.test"), Route("app2.example.test")], 3), Is.EqualTo(Admission.OtherAudience));
            Assert.That(registry.Admit("abc", [Route("app1.example.test")], 3), Is.EqualTo(Admission.Admitted));
            registry.Release("abc");
            Assert.That(registry.PickOrigin("app2.example.test", "/"), Is.Null);
        }

        [Test]
        public async Task An_origin_gets_no_more_tunnels_than_it_may_have()
        {
            await ConnectAsync("abc", Route(Host));
            await ConnectAsync("abc", Route(Host));

            Assert.That(registry.Admit("abc", [Route(Host)], 2), Is.EqualTo(Admission.Full));
            Assert.That(registry.OpenTunnels("abc"), Is.EqualTo(2));
            Assert.That(registry.Admit("abc", [Route(Host)], 3), Is.EqualTo(Admission.Admitted));
        }

        [Test]
        public void An_admitted_tunnel_that_never_opened_is_given_back()
        {
            Assert.That(registry.Admit("abc", [Route(Host)], 3), Is.EqualTo(Admission.Admitted));
            Assert.That(registry.PickOrigin(Host, "/"), Is.EqualTo("abc"));

            registry.Release("abc");

            Assert.That(registry.OriginCount, Is.Zero);
            Assert.That(registry.PickOrigin(Host, "/"), Is.Null);
        }

        [Test]
        public void A_path_alone_serves_the_tunnels_host()
        {
            var claims = TestKeys.Claims();
            claims["aud"] = new[] { "/api/v1/", "https://*.example.test/robots.txt" };
            var token = OriginToken.Parse(TestKeys.Sign(claims));

            var served = ServedRoute.Of(token, "app1.example.test");

            Assert.That(served, Is.EqualTo(new[] { new ServedRoute("app1.example.test", "/api/v1"), new ServedRoute("*.example.test", "/robots.txt") }));
        }

        private static ServedRoute Route(string host, string path = "") => new(host, path);

        // Admits and runs a tunnel of the origin, as the gateway does after checking its token
        private async Task<WebSocketPair> ConnectAsync(string originId, params ServedRoute[] served)
        {
            var pair = await WebSocketPair.CreateAsync();
            pairs.Add(pair);
            Assert.That(registry.Admit(originId, served, 10), Is.EqualTo(Admission.Admitted));
            _ = registry.RunTunnelAsync(originId, new TunnelStream(pair.Server), CancellationToken.None);
            return pair;
        }
    }
}
