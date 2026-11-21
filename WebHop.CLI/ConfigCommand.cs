using System.Diagnostics;
using System.Globalization;
using WebHop.Core;

namespace WebHop.CLI
{
    /// <summary>`webhop config ...`, modelled on `ngrok config ...`.</summary>
    internal static class ConfigCommand
    {
        private static readonly HashSet<string> ValueFlags = ["config"];
        private static readonly HashSet<string> BoolFlags = ["help"];

        public static int Run(IEnumerable<string> args)
        {
            var parsed = CliArgs.Parse(args, ValueFlags, BoolFlags);
            var path = parsed.Get("config") ?? CliConfig.DefaultPath;
            if (parsed.Flag("help"))
                return Help.Config();

            switch (parsed.Positional)
            {
                case ["add-authtoken", var value]:
                {
                    var token = OriginToken.TryParse(value, out var parsedToken, out var problem)
                        ? parsedToken
                        : throw new CliException($"That is not a WebHop token: {problem}. The gateway's issuer makes one with `webhop-gen token issue`.");
                    var exitCode = Update(path, c => c.AuthToken = token.Value, "Auth token");
                    Describe(token);
                    return exitCode;
                }
                case ["add-gateway-url", var url]:
                    return Update(path, c => c.GatewayUrl = HttpOptions.ParseGateway(url).ToString(), "Gateway URL");
                case ["check"]:
                    return Check(path);
                case ["edit"]:
                    return Edit(path);
                case [] or ["help"]:
                    return Help.Config();
                default:
                    throw new CliException($"Unknown config command: {string.Join(' ', parsed.Positional)}. Run `webhop config help`.");
            }
        }

        private static int Update(string path, Action<CliConfig> change, string what)
        {
            var config = CliConfig.Load(path);
            change(config);
            config.Save(path);
            Console.WriteLine($"{what} saved to configuration file: {path}");
            return 0;
        }

        private static int Check(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"No configuration file at {path}. Create one with `webhop config add-authtoken <token>`.");
                return 1;
            }

            var config = CliConfig.Load(path);
            if (config.GatewayUrl is { } gatewayUrl)
                HttpOptions.ParseGateway(gatewayUrl);
            OriginToken? token = null;
            if (config.AuthToken is { Length: > 0 } authToken && !OriginToken.TryParse(authToken, out token, out var problem))
                throw new CliException($"The authtoken in {path} is not a WebHop token: {problem}.");

            Console.WriteLine($"Valid configuration file at {path}");
            Console.WriteLine($"  gateway_url  {config.GatewayUrl ?? "(not set)"}");
            Console.WriteLine($"  authtoken    {Mask(config.AuthToken)}");
            if (token is not null)
                Describe(token);
            return 0;
        }

        /// <summary>What a token says, read without checking its signature: the gateway does that.</summary>
        private static void Describe(OriginToken token)
        {
            Console.WriteLine($"  origin       {token.Subject}");
            Console.WriteLine($"  serves       {string.Join(", ", token.Audience)}");
            Console.WriteLine($"  expires      {token.Expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}{(token.Expires < DateTimeOffset.UtcNow ? " (expired)" : "")}");
        }

        private static int Edit(string path)
        {
            if (!File.Exists(path))
                new CliConfig().Save(path);

            var editor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR")
                ?? (OperatingSystem.IsWindows() ? "notepad" : "vi");
            using var process = Process.Start(new ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false })
                ?? throw new CliException($"Cannot start {editor}.");
            process.WaitForExit();
            return process.ExitCode;
        }

        private static string Mask(string? authToken) => authToken switch
        {
            null or "" => "(not set)",
            { Length: <= 4 } => "****",
            _ => authToken[..2] + new string('*', 8),
        };
    }
}
