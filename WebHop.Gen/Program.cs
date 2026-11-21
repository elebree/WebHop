using System.Reflection;
using WebHop.Core.Commands;

// webhop-gen: the issuer and token commands alone, so the machine that issues tokens needn't allow a
// tunnel client. It opens no connection: it only reads, signs and writes files.
try
{
    var commands = IssuerCommands.Create("webhop-gen");
    return CommandFirst(args) switch
    {
        ["issuer", .. var rest] => commands.RunIssuer(rest),
        ["token", .. var rest] => commands.RunToken(rest),
        ["version" or "--version" or "-v"] => Version(),
        ["help", "issuer"] => commands.RunIssuer(["help"]),
        ["help", "token"] => commands.RunToken(["help"]),
        [] or ["help" or "--help" or "-h", ..] => Help(),
        _ => throw new CliException($"Unknown command: {args[0]}. Run `webhop-gen help`."),
    };
}
catch (CliException ex)
{
    Console.Error.WriteLine($"ERROR:  {ex.Message}");
    return 1;
}

// As in webhop, flags may come before the command (webhop-gen --origin app1 token issue app1.example.com)
static string[] CommandFirst(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith('-'))
            return i == 0 ? args : [arg, .. args[..i], .. args[(i + 1)..]];
        if (!arg.Contains('=') && IssuerCommands.ValueFlags.Contains(arg.TrimStart('-')))
            i++;
    }
    return args;
}

static string AppVersion() =>
    typeof(IssuerCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

static int Version()
{
    Console.WriteLine($"webhop-gen version {AppVersion()}");
    return 0;
}

static int Help()
{
    Console.WriteLine($"""
        NAME:
          webhop-gen - issue the tokens WebHop origins open tunnels with

        USAGE:
          webhop-gen [command] [flags]

        COMMANDS:
          issuer     create and manage key sets: init, list, public, rotate
          token      issue, list and revoke tokens
          version    print the version
          help       help about any command

        EXAMPLES:
          webhop-gen issuer init *.example.com             prints whk_… for the gateway's WEBHOP_KEYSET
          webhop-gen token issue app1.example.com --origin app1
          webhop-gen token revoke <token>

        Key sets are kept in {WebHop.Core.KeySetStore.DefaultDirectory}.
        Run `webhop-gen help <command>` for details. Version {AppVersion()}.
        """);
    return 0;
}
