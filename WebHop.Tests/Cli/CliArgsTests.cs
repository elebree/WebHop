using WebHop.CLI;
using WebHop.Core.Commands;

namespace WebHop.Tests.Cli
{
    public class CliArgsTests
    {
        private static readonly HashSet<string> ValueFlags = ["url", "token", "log"];
        private static readonly HashSet<string> BoolFlags = ["help"];

        [Test]
        public void Flags_work_in_all_ngrok_forms_and_any_order()
        {
            var args = CliArgs.Parse(["--url", "https://gw/", "8080", "--token=tok", "-log", "stdout"], ValueFlags, BoolFlags);

            Assert.That(args.Positional, Is.EqualTo(new[] { "8080" }));
            Assert.That(args.Get("url"), Is.EqualTo("https://gw/"));
            Assert.That(args.Get("token"), Is.EqualTo("tok"));
            Assert.That(args.Get("log"), Is.EqualTo("stdout"));
        }

        [Test]
        public void A_value_after_equals_may_contain_equals_signs()
        {
            var args = CliArgs.Parse(["--token=a=b=c"], ValueFlags, BoolFlags);

            Assert.That(args.Get("token"), Is.EqualTo("a=b=c"));
        }

        [TestCase("--help", true)]
        [TestCase("-h", true)]
        [TestCase("--help=true", true)]
        [TestCase("--help=false", false)]
        public void Boolean_flags_take_no_value_unless_given_with_equals(string flag, bool expected)
        {
            var args = CliArgs.Parse([flag, "8080"], ValueFlags, BoolFlags);

            Assert.That(args.Flag("help"), Is.EqualTo(expected));
            Assert.That(args.Positional, Is.EqualTo(new[] { "8080" }));
        }

        [Test]
        public void Unknown_flags_are_rejected()
        {
            var error = Assert.Throws<CliException>(() => CliArgs.Parse(["--bogus"], ValueFlags, BoolFlags));
            Assert.That(error.Message, Does.Contain("--bogus"));
        }

        [Test]
        public void A_value_flag_without_a_value_is_rejected()
        {
            Assert.Throws<CliException>(() => CliArgs.Parse(["8080", "--url"], ValueFlags, BoolFlags));
        }

        [Test]
        public void A_repeated_flag_keeps_every_value_and_reads_as_the_last()
        {
            var args = CliArgs.Parse(["--log", "a", "--log=b"], ValueFlags, BoolFlags);

            Assert.That(args.GetAll("log"), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(args.Get("log"), Is.EqualTo("b"));
            Assert.That(args.GetAll("url"), Is.Empty);
        }

        [Test]
        public void Unknown_flags_point_to_the_tools_help()
        {
            var error = Assert.Throws<CliException>(() => CliArgs.Parse(["--bogus"], ValueFlags, BoolFlags, "webhop-gen"));
            Assert.That(error.Message, Does.Contain("webhop-gen help"));
        }

        [Test]
        public void Missing_flags_read_as_null_and_false()
        {
            var args = CliArgs.Parse(["8080"], ValueFlags, BoolFlags);

            Assert.That(args.Get("url"), Is.Null);
            Assert.That(args.Flag("help"), Is.False);
        }
    }
}
