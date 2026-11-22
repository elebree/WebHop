using System.Text;
using System.Text.Json;

namespace WebHop.Core
{
    /// <summary>
    /// The private key sets on this machine, one file per host their tokens are for, in a directory restricted
    /// to the user: <c>%APPDATA%\webhop\keys</c> on Windows, <c>~/.config/webhop/keys</c> elsewhere. A wildcard
    /// host is written with <c>_</c> for <c>*</c>, which Windows allows in no file name:
    /// <c>_.example.com.json</c> is the set for <c>*.example.com</c>. Beside each set, a log
    /// (<c>_.example.com.log</c>) lists the tokens issued from it, never the tokens themselves.
    /// </summary>
    public sealed class KeySetStore(string directory)
    {
        public static string DefaultDirectory { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "webhop", "keys");

        public string Directory { get; } = directory;

        /// <summary>The file name of the set for a host or pattern (a <see cref="AudienceEntry.HostKey"/>).</summary>
        public static string FileName(string host)
        {
            var name = new StringBuilder(host.Length + 5);
            foreach (var c in host)
                name.Append(c is '*' or ':' or '[' or ']' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return name.Append(".json").ToString();
        }

        /// <summary>The file of the set for <paramref name="host"/>, whether it exists or not.</summary>
        public string PathFor(string host) => Path.Combine(Directory, FileName(host));

        /// <summary>The set files in the directory.</summary>
        public IReadOnlyList<string> List() =>
            System.IO.Directory.Exists(Directory)
                ? [.. System.IO.Directory.GetFiles(Directory, "*.json").Order(StringComparer.OrdinalIgnoreCase)]
                : [];

        /// <summary>
        /// The set an argument picks: a set file's path, or a host (with a path or scheme, as commands take them).
        /// A host picks its own set if there is one, else the wildcard one label up, as a TLS wildcard covers one
        /// label; a wildcard picks its own. Null when there's none.
        /// </summary>
        public string? Find(string argument)
        {
            if (IsFilePath(argument))
                return File.Exists(argument) ? Path.GetFullPath(argument) : null;

            var entry = ParseArgument(argument);
            if (entry.Host is not { } host)
                return null;

            var own = PathFor(host);
            if (File.Exists(own))
                return own;

            var dot = host.IndexOf('.');
            if (!entry.IsPattern && dot > 0 && PathFor("*" + host[dot..]) is var wildcard && File.Exists(wildcard))
                return wildcard;
            return null;
        }

        /// <summary>Whether a command's argument names a set file rather than a host: set files end in .json, hosts never do.</summary>
        public static bool IsFilePath(string argument) => argument.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A command's argument as an <c>aud</c> or <c>allow</c> entry: a host, a host with a path, or a URL,
        /// <c>https://</c> without a scheme. <c>_.</c> stands for <c>*.</c>, for shells that expand <c>*</c>.
        /// A path alone stays one.
        /// </summary>
        /// <exception cref="FormatException">It's neither.</exception>
        public static AudienceEntry ParseArgument(string argument)
        {
            var value = argument.Trim();
            if (value.StartsWith('/'))
                return AudienceEntry.Parse(value);

            var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
            var scheme = schemeEnd >= 0 ? value[..(schemeEnd + 3)] : "https://";
            var rest = schemeEnd >= 0 ? value[(schemeEnd + 3)..] : value;
            if (rest.StartsWith("_.", StringComparison.Ordinal))
                rest = "*" + rest[1..];
            return AudienceEntry.Parse(scheme + rest);
        }

        /// <summary>The log of tokens issued from a set: beside it, <c>.log</c> for <c>.json</c>.</summary>
        public static string LogPath(string setPath) => Path.ChangeExtension(setPath, ".log");

        /// <summary>
        /// Appends a token's serial, origin id (when it has one), audience and times, never the token itself, to its
        /// set's log.
        /// </summary>
        public static void AppendLog(string setPath, OriginToken token)
        {
            var line = KeySet.WriteJson(writer =>
            {
                writer.WriteString("jti", token.Id);
                // As in the token: without sub, the serial is the origin id
                if (!token.SubjectOmitted)
                    writer.WriteString("sub", token.Subject);
                writer.WriteStartArray("aud");
                foreach (var entry in token.Audience)
                    writer.WriteStringValue(entry.Value);
                writer.WriteEndArray();
                writer.WriteNumber("iat", token.IssuedAt.ToUnixTimeSeconds());
                writer.WriteNumber("exp", token.Expires.ToUnixTimeSeconds());
            });

            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(LogPath(setPath), options);
            stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        }

        /// <summary>The tokens issued from a set, oldest first; lines that can't be read are skipped.</summary>
        public static IReadOnlyList<IssuedTokenRecord> ReadLog(string setPath)
        {
            var path = LogPath(setPath);
            if (!File.Exists(path))
                return [];

            var records = new List<IssuedTokenRecord>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var id = root.GetProperty("jti").GetString()!;
                    records.Add(new IssuedTokenRecord(
                        id,
                        root.TryGetProperty("sub", out var sub) ? sub.GetString()! : id,
                        [.. root.GetProperty("aud").EnumerateArray().Select(a => a.GetString()!)],
                        DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("iat").GetInt64()),
                        DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64())));
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
                {
                    // A line written by hand or cut off: the others still count
                }
            }
            return records;
        }

        /// <summary>
        /// Creates the directory restricted to the user (on Unix; on Windows it inherits the profile's access rules).
        /// </summary>
        public void EnsureDirectory()
        {
            if (OperatingSystem.IsWindows())
                System.IO.Directory.CreateDirectory(Directory);
            else
                System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A line of a set's log: a token issued from it. <c>Subject</c> is its origin id, the serial for a token issued
    /// without one.
    /// </summary>
    public sealed record IssuedTokenRecord(string Id, string Subject, IReadOnlyList<string> Audience, DateTimeOffset IssuedAt, DateTimeOffset Expires);
}
