using System.Text.Json;
using WebHop.CLI;

namespace WebHop.Tests.Cli
{
    public class CliConfigTests
    {
        private string directory = null!;
        private string ConfigPath => Path.Combine(directory, "nested", "webhop.json");

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "webhop-tests-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [Test]
        public void Settings_survive_a_save_and_load()
        {
            new CliConfig { AuthToken = "tok", GatewayUrl = "https://gw/" }.Save(ConfigPath);

            var loaded = CliConfig.Load(ConfigPath);

            Assert.That(loaded.AuthToken, Is.EqualTo("tok"));
            Assert.That(loaded.GatewayUrl, Is.EqualTo("https://gw/"));
        }

        [Test]
        public void The_file_uses_ngrok_style_keys_and_omits_unset_values()
        {
            new CliConfig { AuthToken = "tok" }.Save(ConfigPath);

            using var json = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.That(keys, Is.EqualTo(new[] { "authtoken" }));
        }

        [Test]
        public void A_missing_file_means_no_settings()
        {
            var loaded = CliConfig.Load(ConfigPath);

            Assert.That(loaded.AuthToken, Is.Null);
            Assert.That(loaded.GatewayUrl, Is.Null);
        }

        [Test]
        public void A_broken_file_is_reported()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, "{ not json");

            Assert.Throws<CliException>(() => CliConfig.Load(ConfigPath));
        }
    }
}
