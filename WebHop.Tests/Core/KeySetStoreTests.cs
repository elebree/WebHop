using WebHop.Core;

namespace WebHop.Tests.Core
{
    public class KeySetStoreTests
    {
        private string directory = null!;
        private KeySetStore store = null!;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "webhop-tests-" + Guid.NewGuid().ToString("N"));
            store = new KeySetStore(directory);
            store.EnsureDirectory();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [TestCase("*.example.com", "_.example.com.json")]
        [TestCase("example.com", "example.com.json")]
        [TestCase("webhop.example.com", "webhop.example.com.json")]
        public void A_set_is_named_after_its_host_with_underscore_for_the_wildcard(string host, string fileName)
        {
            Assert.That(KeySetStore.FileName(host), Is.EqualTo(fileName));
        }

        [TestCase("webhop.example.com", "https://webhop.example.com/")]
        [TestCase("webhop.example.com/api/v1", "https://webhop.example.com/api/v1")]
        [TestCase("http://localhost:5000", "http://localhost:5000/")]
        [TestCase("*.example.com", "https://*.example.com/")]
        [TestCase("_.example.com/robots.txt", "https://*.example.com/robots.txt")]
        [TestCase("/api/v1/", "/api/v1/")]
        public void Arguments_are_hosts_urls_or_paths(string argument, string entry)
        {
            Assert.That(KeySetStore.ParseArgument(argument).Value, Is.EqualTo(entry));
        }

        [Test]
        public void A_host_picks_its_own_set_before_the_wildcard_one_label_up()
        {
            Touch("_.example.com.json");

            Assert.That(Picked("app1.example.com"), Is.EqualTo("_.example.com.json"));
            Assert.That(Picked("https://app1.example.com/drop/"), Is.EqualTo("_.example.com.json"));
            Assert.That(Picked("a.b.example.com"), Is.Null);
            Assert.That(Picked("example.com"), Is.Null);

            Touch("app1.example.com.json");
            Assert.That(Picked("app1.example.com"), Is.EqualTo("app1.example.com.json"));
        }

        [Test]
        public void A_wildcard_picks_its_own_set_and_a_bare_host_never_the_wildcards()
        {
            Touch("_.example.com.json");
            Touch("example.com.json");

            Assert.That(Picked("*.example.com"), Is.EqualTo("_.example.com.json"));
            Assert.That(Picked("_.example.com"), Is.EqualTo("_.example.com.json"));
            Assert.That(Picked("example.com"), Is.EqualTo("example.com.json"));
        }

        [Test]
        public void A_set_files_path_works_too()
        {
            var path = Touch("custom.json");

            Assert.That(store.Find(path), Is.EqualTo(path));
            Assert.That(store.Find(Path.Combine(directory, "missing.json")), Is.Null);
        }

        [Test]
        public void The_log_keeps_serials_origins_audience_and_times_but_not_tokens()
        {
            var set = KeySet.Create([AudienceEntry.Parse("https://*.example.com/")]);
            var path = store.PathFor("*.example.com");
            set.Save(path);
            var token = set.Issue(new TokenRequest { Audience = [AudienceEntry.Parse("https://app1.example.com/")], Origin = "app1" }, DateTimeOffset.UtcNow);

            KeySetStore.AppendLog(path, token);
            KeySetStore.AppendLog(path, token);
            File.AppendAllText(KeySetStore.LogPath(path), "not json\n");

            var records = KeySetStore.ReadLog(path);
            Assert.That(KeySetStore.LogPath(path), Does.EndWith("_.example.com.log"));
            Assert.That(records, Has.Count.EqualTo(2));
            Assert.That(records.Select(r => r.Id), Is.All.EqualTo(token.Id));
            Assert.That(records[0].Subject, Is.EqualTo("app1"));
            Assert.That(records[0].Audience, Is.EqualTo(new[] { "https://app1.example.com/" }));
            Assert.That(records[0].Expires, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(token.Expires.ToUnixTimeSeconds())));
            Assert.That(File.ReadAllText(KeySetStore.LogPath(path)), Does.Not.Contain(token.Jwt.Split('.')[2]));
        }

        [Test]
        public void The_log_has_no_origin_for_a_token_without_one_and_reads_its_serial_as_it()
        {
            var set = KeySet.Create([AudienceEntry.Parse("https://*.example.com/")]);
            var path = store.PathFor("*.example.com");
            set.Save(path);
            var token = set.Issue(new TokenRequest { Audience = [AudienceEntry.Parse("https://app1.example.com/")] }, DateTimeOffset.UtcNow);

            KeySetStore.AppendLog(path, token);

            Assert.That(File.ReadAllText(KeySetStore.LogPath(path)), Does.Not.Contain("\"sub\""));
            Assert.That(KeySetStore.ReadLog(path).Single().Subject, Is.EqualTo(token.Id));
        }

        private string? Picked(string argument) => store.Find(argument) is { } path ? Path.GetFileName(path) : null;

        private string Touch(string fileName)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "{}");
            return path;
        }
    }
}
