using System.Net;
using WebHop.Gateway;

namespace WebHop.Tests.Gateway
{
    public class HandshakeThrottleTests
    {
        private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");
        private static readonly long WindowMs = (long)HandshakeThrottle.Window.TotalMilliseconds;

        private long now;
        private HandshakeThrottle throttle = null!;

        [SetUp]
        public void SetUp()
        {
            now = 1_000_000;
            throttle = new HandshakeThrottle(() => now);
        }

        [Test]
        public void An_address_is_held_off_from_the_limit_until_its_window_is_over()
        {
            for (var i = 1; i < HandshakeThrottle.Limit; i++)
                Assert.That(throttle.Reject(Client), Is.False);
            Assert.That(throttle.HeldOff(Client), Is.Null);

            now += 10_000;
            Assert.That(throttle.Reject(Client), Is.True, "the one that reaches the limit says so, to be logged once");
            Assert.That(throttle.HeldOff(Client), Is.EqualTo(HandshakeThrottle.Window - TimeSpan.FromSeconds(10)));

            now += WindowMs - 10_000;
            Assert.That(throttle.HeldOff(Client), Is.Null);
        }

        [Test]
        public void Wrong_tokens_after_the_window_start_a_new_count()
        {
            for (var i = 1; i < HandshakeThrottle.Limit; i++)
                throttle.Reject(Client);
            now += WindowMs;

            Assert.That(throttle.Reject(Client), Is.False);
            Assert.That(throttle.HeldOff(Client), Is.Null);
        }

        [Test]
        public void Other_addresses_are_not_held_off()
        {
            for (var i = 0; i < HandshakeThrottle.Limit; i++)
                throttle.Reject(Client);

            Assert.That(throttle.HeldOff(Client), Is.Not.Null);
            Assert.That(throttle.HeldOff(IPAddress.Parse("203.0.113.8")), Is.Null);
        }

        [TestCase("2001:db8:1:2::1", "2001:db8:1:2:ffff::9", true)]
        [TestCase("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
        [TestCase("::ffff:203.0.113.7", "203.0.113.7", true)]
        [TestCase("203.0.113.7", "203.0.113.8", false)]
        public void IPv6_addresses_count_per_64_block(string a, string b, bool same)
        {
            Assert.That(HandshakeThrottle.Key(IPAddress.Parse(a)) == HandshakeThrottle.Key(IPAddress.Parse(b)), Is.EqualTo(same));
        }

        [Test]
        public void Expired_counts_are_swept_out_once_many_addresses_are_kept()
        {
            for (var i = 1; i <= HandshakeThrottle.Tracked; i++)
                throttle.Reject(new IPAddress(i));
            now += WindowMs;

            throttle.Reject(Client);

            Assert.That(throttle.Count, Is.EqualTo(1));
        }
    }
}
