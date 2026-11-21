using WebHop.CLI;
using WebHop.Core.Commands;

namespace WebHop.Tests.Cli
{
    public class HttpOptionsTests
    {
        [TestCase("8080", "http://localhost:8080/")]
        [TestCase("localhost:3000", "http://localhost:3000/")]
        [TestCase("app.local:8081", "http://app.local:8081/")]
        [TestCase("https://localhost:7001", "https://localhost:7001/")]
        [TestCase("https://localhost:7001/ignored/path?q=1", "https://localhost:7001/")]
        public void Targets_are_accepted_like_ngrok(string value, string expected)
        {
            Assert.That(HttpOptions.ParseTarget(value).AbsoluteUri, Is.EqualTo(expected));
        }

        [TestCase("ftp://localhost:21")]
        [TestCase("http://")]
        public void Invalid_targets_are_rejected(string value)
        {
            Assert.Throws<CliException>(() => HttpOptions.ParseTarget(value));
        }

        [TestCase("https://gw.example.com/", "https://gw.example.com/")]
        [TestCase("http://localhost:5199", "http://localhost:5199/")]
        [TestCase("wss://gw.example.com:8443", "wss://gw.example.com:8443/")]
        public void Gateway_urls_are_http_or_websocket_scheme_host_and_port(string value, string expected)
        {
            Assert.That(HttpOptions.ParseGateway(value).AbsoluteUri, Is.EqualTo(expected));
        }

        [Test]
        public void A_gateway_that_is_not_a_url_is_rejected()
        {
            Assert.Throws<CliException>(() => HttpOptions.ParseGateway("my-gateway"));
        }

        [TestCase("wss://gw.example.com/webhop")]
        [TestCase("https://gw.example.com/app/")]
        public void A_gateway_url_with_a_path_is_rejected_pointing_to_the_token(string value)
        {
            var error = Assert.Throws<CliException>(() => HttpOptions.ParseGateway(value));
            Assert.That(error.Message, Does.Contain("aud").And.Contain("ept"));
        }

        [Test]
        public void Defaults_show_the_live_screen_at_info_level()
        {
            var options = HttpOptions.Parse(["8080"])!;

            Assert.That(options.Log, Is.EqualTo(LogTarget.None));
            Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Information));
            Assert.That(options.JsonLogs, Is.False);
            Assert.That(options.Gateway, Is.Null);
            Assert.That(options.AuthToken, Is.Null);
        }

        [Test]
        public void All_flags_are_read()
        {
            var options = HttpOptions.Parse([
                "3000", "--gateway=https://gw/", "--token", "tok", "--host-header=rewrite",
                "--connections", "4", "--log-level=warn", "--log-format=json",
            ])!;

            Assert.That(options.Target.AbsoluteUri, Is.EqualTo("http://localhost:3000/"));
            Assert.That(options.Gateway, Is.EqualTo("https://gw/"));
            Assert.That(options.AuthToken, Is.EqualTo("tok"));
            Assert.That(options.HostHeader, Is.EqualTo("rewrite"));
            Assert.That(options.Connections, Is.EqualTo(4));
            Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Warning));
            Assert.That(options.JsonLogs, Is.True);
        }

        [TestCase("stdout", "Stdout")]
        [TestCase("stderr", "Stderr")]
        [TestCase("false", "None")]
        [TestCase("webhop.log", "File")]
        public void The_log_flag_selects_where_logs_go(string value, string target)
        {
            var expected = Enum.Parse<LogTarget>(target);
            var options = HttpOptions.Parse(["8080", $"--log={value}"])!;

            Assert.That(options.Log, Is.EqualTo(expected));
            Assert.That(options.LogFile, Is.EqualTo(expected == LogTarget.File ? Path.GetFullPath(value) : null));
        }

        [Test]
        public void Help_returns_no_options()
        {
            Assert.That(HttpOptions.Parse(["--help"]), Is.Null);
            Assert.That(HttpOptions.Parse(["8080", "-h"]), Is.Null);
        }

        [TestCase("")]
        [TestCase("8080 9090")]
        [TestCase("8080 --connections 0")]
        [TestCase("8080 --log-level=loud")]
        [TestCase("8080 --log-format=xml")]
        public void Invalid_arguments_are_rejected(string commandLine)
        {
            var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Throws<CliException>(() => HttpOptions.Parse(args));
        }
    }
}
