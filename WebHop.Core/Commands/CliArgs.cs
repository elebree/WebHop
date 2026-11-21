namespace WebHop.Core.Commands
{
    /// <summary>
    /// ngrok-style arguments: positional words plus --flag value, --flag=value or -flag value,
    /// in any order. Boolean flags take no value unless written as --flag=true|false. A value flag
    /// may be repeated; <see cref="Get"/> reads the last, <see cref="GetAll"/> all of them.
    /// Shared by webhop and webhop-gen.
    /// </summary>
    internal sealed class CliArgs
    {
        private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);

        public List<string> Positional { get; } = [];

        /// <param name="tool">The command to point to for help in errors.</param>
        public static CliArgs Parse(IEnumerable<string> args, IReadOnlySet<string> valueFlags, IReadOnlySet<string> boolFlags, string tool = "webhop")
        {
            var result = new CliArgs();
            using var e = args.GetEnumerator();
            while (e.MoveNext())
            {
                var arg = e.Current;
                if (arg is "--" or "-" || !arg.StartsWith('-'))
                {
                    result.Positional.Add(arg);
                    continue;
                }

                var name = arg.TrimStart('-');
                string? value = null;
                if (name.IndexOf('=') is var eq and > 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }

                if (name is "h" or "help")
                    name = "help";

                if (boolFlags.Contains(name))
                {
                    result.Add(name, value ?? "true");
                }
                else if (valueFlags.Contains(name))
                {
                    if (value is null)
                        value = e.MoveNext() ? e.Current : throw new CliException($"--{name} needs a value.");
                    result.Add(name, value);
                }
                else
                {
                    throw new CliException($"Unknown flag --{name}. Run `{tool} help`.");
                }
            }

            return result;
        }

        private void Add(string name, string value)
        {
            if (!values.TryGetValue(name, out var list))
                values[name] = list = [];
            list.Add(value);
        }

        public string? Get(string name) => values.TryGetValue(name, out var list) ? list[^1] : null;

        public IReadOnlyList<string> GetAll(string name) => values.TryGetValue(name, out var list) ? list : [];

        public bool Flag(string name) => Get(name) is { } value && value is not ("false" or "0");
    }

    /// <summary>An error shown to the user without a stack trace.</summary>
    internal sealed class CliException(string message) : Exception(message);
}
