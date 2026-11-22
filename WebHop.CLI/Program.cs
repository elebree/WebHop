using System.Reflection;
using WebHop.CLI;
using WebHop.Core;

try
{
    return CommandFirst(args) switch
    {
        ["http", .. var rest] => HttpOptions.Parse(rest) is { } options ? await HttpCommand.RunAsync(options) : Help.Http(),
        // Issuing tokens: the key sets that sign them, and the tokens themselves
        ["issuer", .. var rest] => IssuerCommands.Create("webhop").RunIssuer(rest),
        ["token", .. var rest] => IssuerCommands.Create("webhop").RunToken(rest),
        ["version" or "--version" or "-v"] => Help.Version(),
        ["help", "http"] => Help.Http(),
        ["help", "issuer"] => IssuerCommands.Create("webhop").RunIssuer(["help"]),
        ["help", "token"] => IssuerCommands.Create("webhop").RunToken(["help"]),
        [] or ["help" or "--help" or "-h", ..] => Help.General(),
        _ => throw new CliException($"Unknown command: {args[0]}. Run `webhop help`."),
    };
}
catch (CliException ex)
{
    Console.Error.WriteLine($"ERROR:  {ex.Message}");
    return 1;
}

// Like ngrok, flags may come before the command (webhop --log stdout http 8080)
static string[] CommandFirst(string[] args)
{
    string[] valueFlags = ["gateway", "token", "host-header", "connections", "log", "log-level", "log-format", .. IssuerCommands.ValueFlags];
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith('-'))
            return i == 0 ? args : [arg, .. args[..i], .. args[(i + 1)..]];
        // Skip a flag's separate value, so `--log http` is not mistaken for the command
        if (!arg.Contains('=') && valueFlags.Contains(arg.TrimStart('-')))
            i++;
    }
    return args;
}

internal static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

internal static class Help
{
    public static int Version()
    {
        Console.WriteLine($"webhop version {AppVersion.Current}");
        return 0;
    }

    public static int General()
    {
        Console.WriteLine($"""
            NAME:
              webhop - expose a local server through a WebHop gateway

            USAGE:
              webhop [command] [flags]

            COMMANDS:
              http       start an HTTP tunnel to a local server
              issuer     create and manage key sets, which sign origins' tokens
              token      issue and revoke origins' tokens
              version    print the version
              help       help about any command

            EXAMPLES:
              webhop http 8080 --token <token>
              webhop token issue app1.example.com --origin app1

            Run `webhop help <command>` for details. Version {AppVersion.Current}.
            """);
        return 0;
    }

    public static int Http()
    {
        Console.WriteLine($"""
            NAME:
              http - start an HTTP tunnel to a local server

            USAGE:
              webhop http [address:port | port | url] [flags]

            EXAMPLES:
              webhop http 8080 --token=<token>             forward to http://localhost:8080
              webhop http 8080                             the same, with the token in {Constants.AuthTokenEnvironmentVariable}
              webhop http app.local:80                     forward to another host on your network
              webhop http https://localhost:7001           forward to a local https server (self-signed is fine)
              webhop http 3000 --host-header=rewrite       send Host: localhost:3000 (Vite, webpack dev server)
              webhop http 8080 --gateway=https://gw.example.com/   for a token that serves only paths
              webhop http 8080 --log=stdout                log lines instead of the live screen

            FLAGS:
              --token string         the origin's token, wh_… (default: {Constants.AuthTokenEnvironmentVariable})
              --gateway string       the gateway's scheme, host and port, instead of the token's
                                     (default: {HttpCommand.GatewayEnvironmentVariable})
              --host-header string   Host header sent to the local server: `rewrite` for the
                                     target's host, or any value (default: the public host)
              --connections int      tunnels to keep open; caps concurrent requests (default 10,
                                     at most the token's limit)
              --log string           where to write logs: stdout, stderr, false or a file path.
                                     stdout/stderr replace the live screen (default false)
              --log-level string     debug, info, warn, error or crit (default info)
              --log-format string    term or json (default term)
              -h, --help             help for http
            """);
        return 0;
    }
}
