using System.Diagnostics.CodeAnalysis;

namespace WebHop.Core
{
    /// <summary>
    /// One entry of a token's <c>aud</c> (what an origin serves) or of a key set's <c>allow</c> (what tokens
    /// may claim): a URL such as <c>https://app1.example.com/drop/</c>, a path alone such as <c>/api/v1/</c>,
    /// or a host pattern such as <c>https://*.example.com/robots.txt</c>, whose <c>*</c> stands for one label.
    /// Hosts are compared as <see cref="HostKey"/> makes them, without scheme or port; paths match as
    /// prefixes on segment boundaries, regardless of case and of a trailing slash.
    /// </summary>
    public sealed class AudienceEntry : IEquatable<AudienceEntry>
    {
        // Stands in for the * of a pattern while Uri checks the rest, as Uri accepts no * in a host
        private const string PatternLabel = "x";

        private AudienceEntry(string value, string? scheme, string? host, int port, string path, string basePath)
        {
            Value = value;
            Scheme = scheme;
            Host = host;
            Port = port;
            Path = path;
            BasePath = basePath;
        }

        /// <summary>The entry as written in tokens and key sets: lowercase scheme and host, no default port.</summary>
        public string Value { get; }

        /// <summary><c>http</c> or <c>https</c>; null for a path alone.</summary>
        public string? Scheme { get; }

        /// <summary>The host key, <c>*.example.com</c> for a pattern; null for a path alone.</summary>
        public string? Host { get; }

        /// <summary>The port written in the URL, or -1 for the scheme's default (and for a path alone).</summary>
        public int Port { get; }

        /// <summary>The path as written, starting with <c>/</c>.</summary>
        public string Path { get; }

        /// <summary>The path as requests are matched against it: unescaped, without a trailing slash, empty for the root.</summary>
        public string BasePath { get; }

        public bool IsPath => Host is null;

        public bool IsPattern => Host is not null && Host.StartsWith("*.", StringComparison.Ordinal);

        /// <summary>
        /// The routing key of a host name: lowercase, without the port or a trailing dot. Tunnels, public
        /// requests, tokens and key sets are matched on it.
        /// </summary>
        public static string HostKey(string host) => host.TrimEnd('.').ToLowerInvariant();

        public static AudienceEntry Parse(string value) =>
            TryParse(value, out var entry, out var error) ? entry : throw new FormatException(error);

        public static bool TryParse(string? value, [NotNullWhen(true)] out AudienceEntry? entry, [NotNullWhen(false)] out string? error)
        {
            entry = null;
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                error = "an empty entry is neither a URL nor a path";
                return false;
            }

            if (value[0] == '/')
                return TryParsePath(value, out entry, out error);

            var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
            var scheme = schemeEnd > 0 ? value[..schemeEnd].ToLowerInvariant() : null;
            if (scheme is not ("http" or "https"))
            {
                error = $"`{value}` is neither an http(s) URL nor a path";
                return false;
            }

            var rest = value[(schemeEnd + 3)..];
            var pattern = rest.StartsWith("*.", StringComparison.Ordinal);
            if (pattern)
                rest = PatternLabel + rest[1..];
            if (rest.Contains('*'))
            {
                error = $"`{value}`: * stands only for the whole first label of a host, as in https://*.example.com/";
                return false;
            }

            if (!Uri.TryCreate($"{scheme}://{rest}", UriKind.Absolute, out var uri)
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                error = $"`{value}` is not a URL with a host and a path only";
                return false;
            }
            if (pattern && (uri.HostNameType != UriHostNameType.Dns || System.Net.IPAddress.TryParse(uri.Host[(PatternLabel.Length + 1)..], out _)))
            {
                error = $"`{value}`: a pattern needs a domain name after *.";
                return false;
            }

            var host = HostKey(uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : uri.Host);
            if (pattern)
                host = "*" + host[PatternLabel.Length..];
            var port = uri.IsDefaultPort ? -1 : uri.Port;
            var path = uri.AbsolutePath;

            entry = new AudienceEntry($"{scheme}://{host}{(port >= 0 ? ":" + port : "")}{path}", scheme, host, port, path, BasePathOf(path));
            error = null;
            return true;
        }

        private static bool TryParsePath(string value, [NotNullWhen(true)] out AudienceEntry? entry, [NotNullWhen(false)] out string? error)
        {
            entry = null;
            if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains('*')
                || !Uri.TryCreate("http://path.invalid" + value, UriKind.Absolute, out var uri)
                || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                error = $"`{value}` is not a path";
                return false;
            }

            var path = uri.AbsolutePath;
            entry = new AudienceEntry(path, null, null, -1, path, BasePathOf(path));
            error = null;
            return true;
        }

        private static string BasePathOf(string path) => Uri.UnescapeDataString(path).TrimEnd('/');

        /// <summary>Whether this URL or pattern entry serves <paramref name="host"/> (a <see cref="HostKey"/>).</summary>
        public bool CoversHost(string host)
        {
            if (Host is null)
                return false;
            if (!IsPattern)
                return string.Equals(Host, host, StringComparison.Ordinal);
            // * is one label: app1.example.com, but neither example.com nor a.b.example.com
            var suffix = Host.AsSpan(1);
            return host.Length > suffix.Length
                && host.AsSpan().EndsWith(suffix, StringComparison.Ordinal)
                && host.AsSpan(0, host.Length - suffix.Length).IndexOf('.') < 0;
        }

        /// <summary>Whether a request for <paramref name="path"/> falls under this entry's path.</summary>
        public bool CoversPath(string path) => PathCovers(BasePath, path);

        /// <summary>
        /// Whether <paramref name="path"/> is <paramref name="basePath"/> or below it, on a segment boundary:
        /// <c>/drop</c> and <c>/drop/</c> cover <c>/drop</c> and <c>/drop/x</c>, not <c>/dropbox</c>.
        /// </summary>
        public static bool PathCovers(string basePath, string path)
        {
            basePath = basePath.TrimEnd('/');
            if (basePath.Length == 0)
                return true;
            return path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
                && (path.Length == basePath.Length || path[basePath.Length] == '/');
        }

        /// <summary>
        /// Whether everything <paramref name="other"/> claims is also covered by this entry, as an <c>allow</c>
        /// entry: its path is this path or below, and its host this host (for a pattern, a host it covers or
        /// the same pattern). A path alone here covers every host. Whether a path alone in
        /// <paramref name="other"/> is allowed depends on the host its tunnel arrives on, so it's false here.
        /// </summary>
        public bool Contains(AudienceEntry other)
        {
            if (!PathCovers(BasePath, other.BasePath))
                return false;
            if (IsPath)
                return true;
            if (other.IsPath)
                return false;
            if (IsPattern)
                return other.IsPattern ? other.Host == Host : CoversHost(other.Host!);
            return !other.IsPattern && other.Host == Host;
        }

        /// <summary>
        /// The scheme, host and port an origin connects to for this URL or pattern entry, with its origin id
        /// for the <c>*</c> of a pattern; null for a path alone.
        /// </summary>
        public Uri? ConnectUri(string originId)
        {
            if (Host is null)
                return null;
            var host = IsPattern ? originId + Host[1..] : Host;
            return new Uri($"{Scheme}://{host}{(Port >= 0 ? ":" + Port : "")}/");
        }

        public bool Equals(AudienceEntry? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => Equals(obj as AudienceEntry);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
    }
}
