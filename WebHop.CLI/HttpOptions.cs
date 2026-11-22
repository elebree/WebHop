using WebHop.Core;

namespace WebHop.CLI
{
    internal enum LogTarget { None, Stdout, Stderr, File }

    internal sealed class HttpOptions
    {
        private static readonly HashSet<string> ValueFlags =
            ["gateway", "token", "url", "lifetime", "set", "host-header", "connections", "log", "log-level", "log-format"];

        private static readonly HashSet<string> BoolFlags = ["help"];

        public required Uri Target { get; init; }
        public string? Gateway { get; init; }
        public string? AuthToken { get; init; }

        /// <summary>--url: issue the token for this host or URL from this machine's key set, instead of --token.</summary>
        public string? Url { get; init; }

        /// <summary>--lifetime, with --url: how long its token is valid.</summary>
        public TimeSpan? Lifetime { get; init; }

        /// <summary>--set, with --url: the key set's file, instead of finding it by host.</summary>
        public string? SetPath { get; init; }
        public string? HostHeader { get; init; }
        public int? Connections { get; init; }
        public LogTarget Log { get; init; }
        public string? LogFile { get; init; }
        public LogLevel LogLevel { get; init; } = LogLevel.Information;
        public bool JsonLogs { get; init; }

        /// <summary>Null when --help was asked for.</summary>
        public static HttpOptions? Parse(IEnumerable<string> args)
        {
            var parsed = CliArgs.Parse(args, ValueFlags, BoolFlags);
            if (parsed.Flag("help"))
                return null;

            var target = parsed.Positional switch
            {
                [var single] => single,
                [] => throw new CliException("Which local server? For example: webhop http 8080"),
                _ => throw new CliException($"Unexpected argument {parsed.Positional[1]}."),
            };

            var url = parsed.Get("url");
            if (url is not null && parsed.Get("token") is not null)
                throw new CliException("Pass --url or --token, not both: --url issues its own token.");
            if (url is null && (parsed.Get("lifetime") ?? parsed.Get("set")) is not null)
                throw new CliException($"--{(parsed.Get("lifetime") is null ? "set" : "lifetime")} goes with --url, which issues the token here.");

            var log = parsed.Get("log");
            return new HttpOptions
            {
                Target = ParseTarget(target),
                Gateway = parsed.Get("gateway"),
                AuthToken = parsed.Get("token"),
                Url = url,
                Lifetime = parsed.Get("lifetime") is { } lifetime
                    ? Durations.TryParse(lifetime, out var duration) ? duration : throw new CliException($"--lifetime takes a number and a unit, such as 30m, 12h or 7d, not `{lifetime}`.")
                    : null,
                SetPath = parsed.Get("set") is { } set ? Path.GetFullPath(set) : null,
                HostHeader = parsed.Get("host-header"),
                Connections = parsed.Get("connections") is { } c
                    ? int.TryParse(c, out var n) && n > 0 ? n : throw new CliException("--connections needs a positive number.")
                    : null,
                Log = log switch
                {
                    null or "false" => LogTarget.None,
                    "stdout" => LogTarget.Stdout,
                    "stderr" => LogTarget.Stderr,
                    _ => LogTarget.File,
                },
                LogFile = log is null or "false" or "stdout" or "stderr" ? null : Path.GetFullPath(log),
                LogLevel = parsed.Get("log-level") switch
                {
                    null or "info" => LogLevel.Information,
                    "debug" => LogLevel.Debug,
                    "warn" => LogLevel.Warning,
                    "error" => LogLevel.Error,
                    "crit" => LogLevel.Critical,
                    var other => throw new CliException($"--log-level must be debug, info, warn, error or crit, not `{other}`."),
                },
                JsonLogs = parsed.Get("log-format") switch
                {
                    null or "term" or "logfmt" => false,
                    "json" => true,
                    var other => throw new CliException($"--log-format must be term or json, not `{other}`."),
                },
            };
        }

        /// <summary>Accepts 8080, localhost:8080 or a full http(s) URL, like ngrok.</summary>
        public static Uri ParseTarget(string value)
        {
            if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
                return new Uri($"http://localhost:{port}/");

            if (!value.Contains("://"))
                value = "http://" + value;

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");

            throw new CliException($"`{value}` is not a port, host:port or http(s) URL.");
        }

        /// <summary>
        /// A gateway URL overriding the token's host: scheme, host and port only, as the token says what to
        /// serve (aud) and where tunnels connect (ept).
        /// </summary>
        public static Uri ParseGateway(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss"))
                throw new CliException($"`{value}` is not a gateway URL, for example https://my-gateway.azurewebsites.net/");
            if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new CliException($"The gateway URL {value} has a path. It takes the scheme, host and port only: the token's aud says what to serve, and its ept where tunnels connect.");

            return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        }
    }
}
