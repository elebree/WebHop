using System.Globalization;

namespace WebHop.Core.Commands
{
    /// <summary>
    /// The <c>issuer</c> (init, list, public, rotate) and <c>token</c> (issue, list, revoke) commands, the same
    /// in webhop-gen and webhop. Values meant for other tools (a <c>whk_…</c> set, a <c>wh_…</c> token) go to
    /// <c>output</c> alone, so they can be piped; what's said about them goes to <c>error</c>.
    /// </summary>
    internal sealed class IssuerCommands(string tool, KeySetStore store, TextWriter output, TextWriter error, Func<DateTimeOffset> clock)
    {
        public static readonly HashSet<string> ValueFlags = ["origin", "lifetime", "max-lifetime", "tunnels", "ept", "allow", "set"];
        public static readonly HashSet<string> BoolFlags = ["help", "relative", "all"];

        /// <summary>The commands as the tools run them: the user's own directory, the console, the clock.</summary>
        public static IssuerCommands Create(string tool) =>
            new(tool, new KeySetStore(KeySetStore.DefaultDirectory), Console.Out, Console.Error, () => DateTimeOffset.UtcNow);

        public int RunIssuer(IEnumerable<string> args)
        {
            var parsed = CliArgs.Parse(args, ValueFlags, BoolFlags, tool);
            if (parsed.Flag("help"))
                return Help(IssuerHelp(tool));

            return parsed.Positional switch
            {
                ["init", var host] => Init(host, parsed),
                ["init"] => throw new CliException($"Which host is the set for? For example: {tool} issuer init webhop.example.com"),
                ["list"] => List(),
                ["public", var set] => Public(set, parsed),
                ["rotate", var set] => Rotate(set, parsed),
                [] or ["help"] => Help(IssuerHelp(tool)),
                _ => throw new CliException($"Unknown issuer command: {string.Join(' ', parsed.Positional)}. Run `{tool} issuer help`."),
            };
        }

        public int RunToken(IEnumerable<string> args)
        {
            var parsed = CliArgs.Parse(args, ValueFlags, BoolFlags, tool);
            if (parsed.Flag("help"))
                return Help(TokenHelp(tool));

            string[] positional = [.. parsed.Positional];
            return positional switch
            {
                ["issue", _, ..] => Issue(positional[1..], parsed),
                ["issue"] => throw new CliException($"What does the token serve? For example: {tool} token issue app1.example.com"),
                ["list", var set] => ListTokens(set, parsed),
                ["revoke", var target] => Revoke(target, parsed),
                [] or ["help"] => Help(TokenHelp(tool)),
                _ => throw new CliException($"Unknown token command: {string.Join(' ', parsed.Positional)}. Run `{tool} token help`."),
            };
        }

        private int Init(string argument, CliArgs parsed)
        {
            var entry = Argument(argument);
            if (entry.Host is not { } host)
                throw new CliException($"Name the host the set is for, such as webhop.example.com or *.example.com, not a path alone.");

            List<AudienceEntry> allow = [entry];
            foreach (var value in parsed.GetAll("allow").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                var extra = Argument(value);
                if (!allow.Contains(extra))
                    allow.Add(extra);
            }

            var endpoint = parsed.Get("ept");
            if (endpoint is not null && !OriginToken.IsValidEndpoint(endpoint))
                throw new CliException($"--ept must be a path such as /t-7f3k9q, without a trailing slash, not `{endpoint}`.");

            KeySet set;
            try
            {
                set = KeySet.Create(allow, endpoint, Duration(parsed, "lifetime"), Duration(parsed, "max-lifetime"));
            }
            catch (ArgumentException ex)
            {
                throw new CliException(ex.Message);
            }

            var path = parsed.Get("set");
            if (path is null)
            {
                store.EnsureDirectory();
                path = store.PathFor(host);
            }
            if (File.Exists(path))
                throw new CliException($"{path} exists already. Rotate its key with `{tool} issuer rotate {argument}`, or remove the file first.");

            try
            {
                set.Save(path, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Cannot write {path}: {ex.Message}");
            }

            error.WriteLine($"Created the key set for {host} in {Path.GetFullPath(path)}.");
            error.WriteLine("Keep the file private: whoever has it can issue tokens for the gateway.");
            error.WriteLine($"Set this on the gateway as {Constants.KeySetEnvironmentVariable}:");
            output.WriteLine(set.Encode());
            return 0;
        }

        private int List()
        {
            var files = store.List();
            if (files.Count == 0)
            {
                output.WriteLine($"No key sets in {store.Directory}. Create one with `{tool} issuer init <host>`.");
                return 0;
            }

            var now = clock();
            foreach (var path in files)
            {
                output.WriteLine(Path.GetFileName(path));
                try
                {
                    var set = KeySet.Load(path);
                    var signing = set.SigningKey;
                    var live = KeySetStore.ReadLog(path).Count(r => r.Expires + TokenValidator.ClockSkew >= now);
                    output.WriteLine($"  allow    {string.Join(", ", set.Allow)}");
                    output.WriteLine($"  ept      {set.Endpoint}");
                    foreach (var key in set.Keys)
                        output.WriteLine($"  key      {key.KeyId} {(key == signing ? "(signs)" : key.Retired is { } r ? $"(verifies, retired {Date(r)})" : "(verifies)")}");
                    output.WriteLine($"  tokens   {live} not expired");
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
                {
                    output.WriteLine($"  cannot read: {ex.Message}");
                }
            }
            return 0;
        }

        private int Public(string argument, CliArgs parsed)
        {
            var (_, set) = Load(argument, parsed);
            output.WriteLine(set.Encode());
            return 0;
        }

        private int Rotate(string argument, CliArgs parsed)
        {
            var (path, set) = Load(argument, parsed);
            var old = set.SigningKey;
            set.Rotate(clock());
            Save(path, set);

            error.WriteLine(old is null
                ? $"Added key {set.SigningKey!.KeyId} to {path}."
                : $"Added key {set.SigningKey!.KeyId} to {path}; key {old.KeyId} only verifies now, until its tokens have expired.");
            error.WriteLine($"Deploy the new public set on the gateway as {Constants.KeySetEnvironmentVariable}:");
            output.WriteLine(set.Encode());
            return 0;
        }

        private int Issue(IReadOnlyList<string> arguments, CliArgs parsed)
        {
            var entries = arguments.Select(Argument).ToList();

            var path = parsed.Get("set");
            if (path is null)
            {
                var hosts = entries.Where(e => !e.IsPath).ToList();
                if (hosts.Count == 0)
                    throw new CliException("A path alone doesn't say which key set to use: write it with its host, or pass the set's file with --set.");
                var picked = hosts.Select(e => (Entry: e, Path: store.Find(e.Value))).ToList();
                foreach (var (entry, found) in picked)
                    if (found is null)
                        throw NoSet(entry.Host!);
                if (picked.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    throw new CliException($"The arguments pick different key sets ({string.Join(", ", picked.Select(p => Path.GetFileName(p.Path)).Distinct())}); a token comes from one.");
                path = picked[0].Path!;
            }
            var set = LoadFile(path);

            if (parsed.Flag("relative"))
                entries = [.. entries.Select(e => e.IsPath ? e : AudienceEntry.Parse(e.Path)).Distinct()];

            var origin = parsed.Get("origin");
            if (origin is not null && !OriginToken.IsValidOriginId(origin))
                throw new CliException($"--origin must be 1-63 lowercase letters and digits, not `{origin}`.");

            int? tunnels = null;
            if (parsed.Get("tunnels") is { } t)
                tunnels = int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new CliException("--tunnels needs a positive number.");

            OriginToken token;
            try
            {
                token = set.Issue(new TokenRequest { Audience = entries, Origin = origin, Lifetime = Duration(parsed, "lifetime"), Tunnels = tunnels }, clock());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new CliException(ex.Message);
            }

            try
            {
                KeySetStore.AppendLog(path, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Cannot write the log beside {path}: {ex.Message}");
            }

            error.WriteLine($"Origin {token.Subject} serving {string.Join(", ", token.Audience)} until {Time(token.Expires)} (serial {token.Id}):");
            output.WriteLine(token.Value);
            return 0;
        }

        private int ListTokens(string argument, CliArgs parsed)
        {
            var (path, set) = Load(argument, parsed);
            var records = KeySetStore.ReadLog(path);
            if (records.Count == 0)
            {
                output.WriteLine($"No tokens issued from {path} yet.");
                return 0;
            }

            var now = clock();
            output.WriteLine($"{"SERIAL",-14}{"ORIGIN",-16}{"EXPIRES",-22}{"STATE",-9}SERVES");
            foreach (var record in records)
            {
                var state = set.Deny.Contains(record.Id) || record.IssuedAt < set.IssuedAfter ? "revoked"
                    : record.Expires + TokenValidator.ClockSkew < now ? "expired"
                    : "valid";
                output.WriteLine($"{record.Id,-14}{record.Subject,-16}{Time(record.Expires),-22}{state,-9}{string.Join(", ", record.Audience)}");
            }
            return 0;
        }

        private int Revoke(string target, CliArgs parsed)
        {
            var now = clock();
            string path;
            KeySet set;
            string revoked;

            if (target.StartsWith(Constants.TokenPrefix, StringComparison.Ordinal))
            {
                if (!OriginToken.TryParse(target, out var token, out var problem))
                    throw new CliException($"That is not a WebHop token: {problem}.");
                path = parsed.Get("set")
                    ?? store.List().FirstOrDefault(p => TryLoad(p)?.Issuer == token.Issuer)
                    ?? throw new CliException($"No key set in {store.Directory} has issued it (iss {token.Issuer}). Pass the set's file with --set.");
                set = LoadFile(path);
                if (set.Issuer != token.Issuer)
                    throw new CliException($"{path} hasn't issued it: the set's iss is {set.Issuer}, the token's {token.Issuer}.");
                set.DenyToken(token.Id);
                revoked = $"Revoked the token of origin {token.Subject} (serial {token.Id}).";
            }
            else if (parsed.Flag("all"))
            {
                (path, set) = Load(target, parsed);
                set.RevokeAll(now);
                revoked = $"Revoked every token issued from {Path.GetFileName(path)} until now.";
            }
            else if (parsed.Get("origin") is { } origin)
            {
                (path, set) = Load(target, parsed);
                var ids = new List<string>();
                foreach (var record in KeySetStore.ReadLog(path))
                {
                    var live = record.Expires + TokenValidator.ClockSkew >= now && !(record.IssuedAt < set.IssuedAfter);
                    if (record.Subject == origin && live && set.DenyToken(record.Id))
                        ids.Add(record.Id);
                }
                if (ids.Count == 0)
                    throw new CliException($"The log of {Path.GetFileName(path)} has no unexpired, unrevoked token of origin {origin}.");
                revoked = $"Revoked {ids.Count} token{(ids.Count == 1 ? "" : "s")} of origin {origin} ({string.Join(", ", ids)}).";
            }
            else
            {
                throw new CliException($"Revoke what? Pass a token, or a host with --origin <id> or --all. Run `{tool} token help`.");
            }

            // Serials of tokens that have expired anyway need no denying any more
            var log = KeySetStore.ReadLog(path).ToDictionary(r => r.Id, r => r.Expires);
            set.ForgetDenied(id => log.TryGetValue(id, out var expires) && expires + TokenValidator.ClockSkew < now);
            Save(path, set);

            error.WriteLine(revoked);
            error.WriteLine($"Deploy the new public set on the gateway as {Constants.KeySetEnvironmentVariable}; it takes effect when the gateway restarts:");
            output.WriteLine(set.Encode());
            return 0;
        }

        private (string Path, KeySet Set) Load(string argument, CliArgs parsed)
        {
            var path = parsed.Get("set") ?? Find(argument);
            return (path, LoadFile(path));
        }

        private string Find(string argument)
        {
            if (KeySetStore.IsFilePath(argument))
                return File.Exists(argument) ? Path.GetFullPath(argument) : throw new CliException($"There is no key set file {argument}.");
            var entry = Argument(argument);
            return store.Find(argument) ?? throw NoSet(entry.Host ?? argument);
        }

        private CliException NoSet(string host) =>
            new($"No key set for {host} in {store.Directory}. Create one with `{tool} issuer init {host}`, or pass its file with --set.");

        private static KeySet LoadFile(string path)
        {
            try
            {
                return KeySet.Load(path);
            }
            catch (FormatException ex)
            {
                throw new CliException($"Cannot read the key set {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Cannot read {path}: {ex.Message}");
            }
        }

        private static KeySet? TryLoad(string path)
        {
            try
            {
                return KeySet.Load(path);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Writes a changed set, dropping retired keys whose tokens have all expired.</summary>
        private void Save(string path, KeySet set)
        {
            set.DropRetiredKeys(clock());
            try
            {
                set.Save(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Cannot write {path}: {ex.Message}");
            }
        }

        private static AudienceEntry Argument(string value)
        {
            try
            {
                return KeySetStore.ParseArgument(value);
            }
            catch (FormatException ex)
            {
                throw new CliException($"`{value}` is not a host, a host with a path, or a URL: {ex.Message}.");
            }
        }

        private static TimeSpan? Duration(CliArgs parsed, string flag) =>
            parsed.Get(flag) is { } value
                ? Durations.TryParse(value, out var duration) ? duration : throw new CliException($"--{flag} takes a number and a unit, such as 30m, 12h or 7d, not `{value}`.")
                : null;

        private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        private static string Date(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private int Help(string text)
        {
            output.WriteLine(text);
            return 0;
        }

        public static string IssuerHelp(string tool) => $"""
            NAME:
              issuer - create and manage key sets, which sign the tokens of a gateway's origins

            USAGE:
              {tool} issuer init <host> [flags]      a new key set for a gateway; prints whk_… for it
              {tool} issuer list                     the key sets on this machine
              {tool} issuer public <host|file>       print the set's whk_… again
              {tool} issuer rotate <host|file>       a new signing key; the old one only verifies

            EXAMPLES:
              {tool} issuer init webhop.example.com
              {tool} issuer init *.example.com --ept /t-7f3k9q
              {tool} issuer init example.com/api/v1 --allow /frontend/

            FLAGS:
              --allow string          more hosts or paths tokens may claim (repeat or comma-separate)
              --ept string            the tunnel endpoint (default {Constants.DefaultWebHopEndpoint})
              --lifetime duration     how long tokens are valid by default (default 30d)
              --max-lifetime duration the longest a token may be valid (default 90d)
              --set string            the set's file, instead of one named after the host in
                                      {KeySetStore.DefaultDirectory}

            A host picks its own set, else the wildcard one label up (app1.example.com picks
            *.example.com). Write _.example.com for *.example.com in shells that expand *.
            """;

        public static string TokenHelp(string tool) => $"""
            NAME:
              token - issue and revoke the tokens origins connect with

            USAGE:
              {tool} token issue <host[/path]|url>... [flags]   a token serving those; prints wh_…
              {tool} token list <host|file>                     the tokens issued from a set
              {tool} token revoke <token>                       revoke a token; prints the new whk_…
              {tool} token revoke <host|file> --origin <id>     revoke an origin's tokens, found in the log
              {tool} token revoke <host|file> --all             revoke every token issued until now

            EXAMPLES:
              {tool} token issue app1.example.com --origin app1
              {tool} token issue webhop.example.com/api/v1 --lifetime 7d
              {tool} token issue *.example.com/robots.txt --origin robots
              {tool} token issue app1.example.com app1.example.org --tunnels 5

            FLAGS:
              --origin string         the origin id: lowercase letters and digits (default: the token's serial)
              --lifetime duration     how long it's valid, such as 12h or 7d (default: the set's)
              --tunnels int           the most tunnels at once (default {Constants.DefaultTunnels})
              --relative              serve the path on whatever host the origin connects to
              --set string            the set's file, instead of finding it by host
              --all                   with revoke: every token of the set

            Without a scheme, hosts are https://; write http:// for a gateway without TLS.
            """;
    }
}
