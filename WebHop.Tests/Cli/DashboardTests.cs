using WebHop.CLI;
using WebHop.Core;
using WebHop.Tests.Infrastructure;

namespace WebHop.Tests.Cli
{
    public class DashboardTests
    {
        [Test]
        public void The_tokens_expiry_is_shown_plainly_while_far_off()
        {
            var token = Token(lifetimeDays: 30);

            var expiry = Dashboard.Expiry(token, token.IssuedAt);

            Assert.That(expiry.Text, Does.Contain(token.Expires.UtcDateTime.ToString("yyyy-MM-dd")).And.Contain("in 30 days"));
            Assert.That(expiry.Color, Is.Null);
        }

        [Test]
        public void Shortly_before_it_expires_it_warns()
        {
            var token = Token(lifetimeDays: 30);

            var expiry = Dashboard.Expiry(token, token.Expires - TimeSpan.FromHours(5));

            Assert.That(expiry.Text, Does.Contain("in 5 hours").And.Contain("soon"));
            Assert.That(expiry.Color, Is.EqualTo(ConsoleColor.Yellow));
        }

        [Test]
        public void A_short_lived_token_warns_within_a_tenth_of_its_lifetime()
        {
            var token = Token(lifetimeDays: 2);

            Assert.That(Dashboard.Expiry(token, token.Expires - TimeSpan.FromHours(6)).Color, Is.Null);
            Assert.That(Dashboard.Expiry(token, token.Expires - TimeSpan.FromHours(4)).Color, Is.EqualTo(ConsoleColor.Yellow));
        }

        [Test]
        public void Once_expired_it_says_so()
        {
            var token = Token(lifetimeDays: 1);

            var expiry = Dashboard.Expiry(token, token.Expires + TimeSpan.FromMinutes(1));

            Assert.That(expiry.Text, Does.Contain("expired"));
            Assert.That(expiry.Color, Is.EqualTo(ConsoleColor.Red));
        }

        private static OriginToken Token(int lifetimeDays) =>
            OriginToken.Parse(TestKeys.Token(lifetime: TimeSpan.FromDays(lifetimeDays)));
    }
}
