using WebHop.CLI;

namespace WebHop.Tests.Cli
{
    public class SessionTests
    {
        private Session session = null!;

        [SetUp]
        public void SetUp()
        {
            session = new Session();
        }

        [Test]
        public void There_are_no_percentiles_before_the_first_request()
        {
            Assert.That(session.Percentiles(), Is.Null);
        }

        [Test]
        public void Percentiles_are_computed_over_request_durations()
        {
            foreach (var ms in new[] { 100, 10, 90, 20, 80, 30, 70, 40, 60, 50 })
                Complete(ms);

            var (p50, p90) = session.Percentiles()!.Value;

            Assert.That(p50, Is.EqualTo(50));
            Assert.That(p90, Is.EqualTo(90));
        }

        [Test]
        public void Recent_requests_are_listed_newest_first()
        {
            Complete(1, "/first");
            Complete(1, "/second");
            Complete(1, "/third");

            Assert.That(session.RecentRequests(2).Select(r => r.Path), Is.EqualTo(new[] { "/third", "/second" }));
        }

        [Test]
        public void Totals_and_requests_in_flight_are_counted()
        {
            session.BeginRequest();
            session.BeginRequest();
            Assert.That(session.InFlight, Is.EqualTo(2));

            session.EndRequest(Entry(5, "/"));

            Assert.That(session.InFlight, Is.EqualTo(1));
            Assert.That(session.Total, Is.EqualTo(1));
        }

        [Test]
        public void Every_change_is_visible_to_the_screen_and_to_listeners()
        {
            var completed = new List<RequestEntry>();
            session.RequestCompleted += completed.Add;
            var before = session.Version;

            Complete(5);
            session.ReportError("boom");

            Assert.That(session.Version > before, Is.True);
            Assert.That(completed, Has.Count.EqualTo(1));
            Assert.That(session.LastError, Is.EqualTo("boom"));
        }

        private void Complete(int milliseconds, string path = "/")
        {
            session.BeginRequest();
            session.EndRequest(Entry(milliseconds, path));
        }

        private static RequestEntry Entry(int milliseconds, string path) =>
            new(DateTime.Now, "GET", path, 200, TimeSpan.FromMilliseconds(milliseconds), null);
    }
}
