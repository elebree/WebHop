using System.Text.Json;
using WebHop.CLI;

namespace WebHop.Tests.Cli
{
    public class LineLoggerProviderTests
    {
        private string directory = null!;

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
        public void Text_lines_have_time_level_category_and_message()
        {
            var writer = new StringWriter();
            using var provider = new LineLoggerProvider(writer, json: false);

            provider.CreateLogger("webhop").LogInformation("GET {Path} 200", "/ping");

            Assert.That(writer.ToString(), Does.Match(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} info webhop: GET /ping 200\r?\n$"));
        }

        [Test]
        public void Text_lines_end_with_the_exception_message()
        {
            var writer = new StringWriter();
            using var provider = new LineLoggerProvider(writer, json: false);

            provider.CreateLogger("webhop").LogWarning(new IOException("connection refused"), "GET / 502");

            Assert.That(writer.ToString().TrimEnd(), Does.EndWith("warn webhop: GET / 502 (connection refused)"));
        }

        [Test]
        public void Json_lines_use_ngrok_field_names()
        {
            var writer = new StringWriter();
            using var provider = new LineLoggerProvider(writer, json: true);

            provider.CreateLogger("webhop").LogError(new IOException("connection refused"), "GET / 502");

            using var line = JsonDocument.Parse(writer.ToString());
            var root = line.RootElement;
            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "t", "lvl", "obj", "msg", "err" }));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(DateTimeOffset.TryParse(root.GetProperty("t").GetString(), out _), Is.True);
                Assert.That(root.GetProperty("lvl").GetString(), Is.EqualTo("error"));
                Assert.That(root.GetProperty("obj").GetString(), Is.EqualTo("webhop"));
                Assert.That(root.GetProperty("msg").GetString(), Is.EqualTo("GET / 502"));
                Assert.That(root.GetProperty("err").GetString(), Is.EqualTo("connection refused"));
            }
        }

        [Test]
        public void Json_lines_keep_a_null_error_and_stay_on_one_line()
        {
            var writer = new StringWriter();
            using var provider = new LineLoggerProvider(writer, json: true);

            provider.CreateLogger("webhop").LogInformation("first");
            provider.CreateLogger("webhop").LogInformation("second");

            var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(lines, Has.Length.EqualTo(2));
            using var first = JsonDocument.Parse(lines[0]);
            Assert.That(first.RootElement.GetProperty("err").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [TestCase(LogLevel.Trace, "debug")]
        [TestCase(LogLevel.Debug, "debug")]
        [TestCase(LogLevel.Information, "info")]
        [TestCase(LogLevel.Warning, "warn")]
        [TestCase(LogLevel.Error, "error")]
        [TestCase(LogLevel.Critical, "crit")]
        public void Levels_use_ngrok_names(LogLevel level, string expected)
        {
            var writer = new StringWriter();
            using var provider = new LineLoggerProvider(writer, json: true);

            provider.CreateLogger("webhop").Log(level, "message");

            using var line = JsonDocument.Parse(writer.ToString());
            Assert.That(line.RootElement.GetProperty("lvl").GetString(), Is.EqualTo(expected));
        }

        [Test]
        public void Nothing_is_written_at_level_none()
        {
            var writer = new StringWriter();
            using var provider = new LineLoggerProvider(writer, json: false);
            var logger = provider.CreateLogger("webhop");

            logger.Log(LogLevel.None, "message");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(logger.IsEnabled(LogLevel.None), Is.False);
                Assert.That(writer.ToString(), Is.Empty);
            }
        }

        [Test]
        public void A_log_file_is_created_with_its_directory_and_appended_to()
        {
            var path = Path.Combine(directory, "logs", "webhop.log");

            using (var provider = LineLoggerProvider.ForFile(path, json: false))
                provider.CreateLogger("webhop").LogInformation("first run");
            using (var provider = LineLoggerProvider.ForFile(path, json: false))
                provider.CreateLogger("webhop").LogInformation("second run");

            var lines = File.ReadAllLines(path);
            Assert.That(lines, Has.Length.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Does.EndWith("first run"));
                Assert.That(lines[1], Does.EndWith("second run"));
            }
        }

        [Test]
        public void A_log_file_that_cannot_be_written_is_reported()
        {
            // The log file's directory would have to be a file that already exists
            Directory.CreateDirectory(directory);
            var blocker = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blocker, "");

            var error = Assert.Throws<CliException>(() => LineLoggerProvider.ForFile(Path.Combine(blocker, "webhop.log"), json: false));
            Assert.That(error.Message, Does.StartWith("Cannot write log file"));
        }
    }
}
