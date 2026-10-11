using WebHop.Gateway;

namespace WebHop.Tests.Gateway
{
    public class WebSocketBudgetTests
    {
        // Azure tiers (60% of the host's WebSocket limit, leaving room for visitors)
        [TestCase(null, "Free", 3)]     // 5  -> 3
        [TestCase(null, "free", 3)]     // case-insensitive
        [TestCase(null, "Shared", 21)]  // 35 -> 21
        [TestCase(null, "Basic", 0)]    // unlimited
        [TestCase(null, "Standard", 0)]
        [TestCase(null, null, 0)]       // non-Azure
        // Explicit override wins and is also taken at 60%
        [TestCase("10", "Free", 6)]
        [TestCase("100", null, 60)]
        // A non-positive or unparsable override falls back to the SKU
        [TestCase("0", "Free", 3)]
        [TestCase("nonsense", "Free", 3)]
        public void Per_origin_cap_follows_the_budget(string? maxWebSockets, string? sku, int expected)
        {
            Assert.That(WebSocketBudget.PerOriginCap(maxWebSockets, sku), Is.EqualTo(expected));
        }
    }
}
