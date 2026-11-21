using WebHop.Core;

namespace WebHop.Tests.Core
{
    public class AudienceEntryTests
    {
        [TestCase("https://app1.example.com/drop/", "https://app1.example.com/drop/")]
        [TestCase("https://App1.EXAMPLE.com", "https://app1.example.com/")]
        [TestCase("HTTPS://app1.example.com:443/", "https://app1.example.com/")]
        [TestCase("http://localhost:5000/api", "http://localhost:5000/api")]
        [TestCase("https://app1.example.com./", "https://app1.example.com/")]
        [TestCase("https://*.example.com/robots.txt", "https://*.example.com/robots.txt")]
        [TestCase("/api/v1/", "/api/v1/")]
        [TestCase("/", "/")]
        public void Entries_are_written_in_one_form(string value, string expected)
        {
            Assert.That(AudienceEntry.Parse(value).Value, Is.EqualTo(expected));
        }

        [Test]
        public void A_url_has_its_scheme_host_port_and_path()
        {
            var entry = AudienceEntry.Parse("http://App1.example.test:8080/Drop/");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(entry.Scheme, Is.EqualTo("http"));
                Assert.That(entry.Host, Is.EqualTo("app1.example.test"));
                Assert.That(entry.Port, Is.EqualTo(8080));
                Assert.That(entry.Path, Is.EqualTo("/Drop/"));
                Assert.That(entry.BasePath, Is.EqualTo("/Drop"));
                Assert.That(entry.IsPath, Is.False);
                Assert.That(entry.IsPattern, Is.False);
            }
        }

        [Test]
        public void A_path_alone_has_no_host()
        {
            var entry = AudienceEntry.Parse("/api/v1/");

            Assert.That(entry.IsPath, Is.True);
            Assert.That(entry.Host, Is.Null);
            Assert.That(entry.BasePath, Is.EqualTo("/api/v1"));
            Assert.That(AudienceEntry.Parse("/").BasePath, Is.Empty);
        }

        [TestCase("")]
        [TestCase("app1.example.com")]
        [TestCase("ftp://app1.example.com/")]
        [TestCase("https://app1.example.com/?q=1")]
        [TestCase("https://user@app1.example.com/")]
        [TestCase("https://a.*.example.com/")]
        [TestCase("https://*/")]
        [TestCase("https://*.127.0.0.1/")]
        [TestCase("//app1.example.com/")]
        [TestCase("/api/*")]
        public void Other_values_are_rejected(string value)
        {
            Assert.That(AudienceEntry.TryParse(value, out _, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase("/drop", "/drop", true)]
        [TestCase("/drop", "/drop/x", true)]
        [TestCase("/drop/", "/drop", true)]
        [TestCase("/drop/", "/drop/x", true)]
        [TestCase("/drop", "/dropbox", false)]
        [TestCase("/drop", "/DROP/x", true)]
        [TestCase("/drop", "/", false)]
        [TestCase("", "/anything", true)]
        [TestCase("/", "/anything", true)]
        public void Paths_match_on_segment_boundaries(string basePath, string path, bool covered)
        {
            Assert.That(AudienceEntry.PathCovers(basePath, path), Is.EqualTo(covered));
        }

        [TestCase("app1.example.com", true)]
        [TestCase("example.com", false)]
        [TestCase("a.b.example.com", false)]
        [TestCase("app1.example.org", false)]
        [TestCase("xexample.com", false)]
        public void A_pattern_covers_one_label(string host, bool covered)
        {
            Assert.That(AudienceEntry.Parse("https://*.example.com/").CoversHost(host), Is.EqualTo(covered));
        }

        [TestCase("https://*.example.com/", "https://app1.example.com/drop/", true)]
        [TestCase("https://*.example.com/", "https://*.example.com/robots.txt", true)]
        [TestCase("https://*.example.com/", "https://a.b.example.com/", false)]
        [TestCase("https://*.example.com/", "/api/", false)]
        [TestCase("https://app1.example.com/", "http://app1.example.com:8080/x", true)]
        [TestCase("https://app1.example.com/drop/", "https://app1.example.com/", false)]
        [TestCase("https://app1.example.com/", "https://*.example.com/", false)]
        [TestCase("/api/", "https://app1.example.com/api/v1/", true)]
        [TestCase("/api/", "/api/v1/", true)]
        [TestCase("/api/", "/apix/", false)]
        public void Allow_entries_contain_what_falls_within_them(string allow, string claim, bool contained)
        {
            Assert.That(AudienceEntry.Parse(allow).Contains(AudienceEntry.Parse(claim)), Is.EqualTo(contained));
        }

        [TestCase("https://app1.example.com/drop/", "app1", "https://app1.example.com/")]
        [TestCase("http://localhost:5000/", "app1", "http://localhost:5000/")]
        [TestCase("https://*.example.com/robots.txt", "robots", "https://robots.example.com/")]
        public void Origins_connect_to_the_scheme_host_and_port(string value, string originId, string expected)
        {
            Assert.That(AudienceEntry.Parse(value).ConnectUri(originId)!.AbsoluteUri, Is.EqualTo(expected));
        }

        [Test]
        public void A_path_alone_names_nowhere_to_connect()
        {
            Assert.That(AudienceEntry.Parse("/api/").ConnectUri("app1"), Is.Null);
        }
    }
}
