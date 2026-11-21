using System.Threading.Channels;
using WebHop.Core;

namespace WebHop.Gateway
{
    /// <summary>
    /// Tracks connected WebHop origins and the idle tunnel streams each of them keeps open. An origin is
    /// registered while it has at least one tunnel admitted, under everything its token's <c>aud</c> serves
    /// (<see cref="ServedRoute"/>). A request goes to the origins serving its host with the longest path that
    /// covers it; at equal paths an exact host beats a pattern, and origins on the same host and path take turns.
    /// </summary>
    public sealed class TunnelRegistry(ILogger<TunnelRegistry> logger)
    {
        private readonly object sync = new();
        private readonly Dictionary<string, OriginTunnel> origins = [];
        // Rebuilt under the lock on every connect and disconnect, read without it
        private RouteTable routes = RouteTable.Empty;

        public int OriginCount
        {
            get
            {
                lock (sync)
                    return origins.Count;
            }
        }

        /// <summary>
        /// The routing key of a request's host: the name without the port, lowercase, without a
        /// trailing dot. Tunnels and public requests are matched on it.
        /// </summary>
        public static string HostKey(HostString host) => AudienceEntry.HostKey(host.Host);

        /// <summary>An origin serving <paramref name="path"/> on <paramref name="host"/>, in turns; null when none does.</summary>
        public string? PickOrigin(string host, string path) => Volatile.Read(ref routes).Pick(host, path);

        /// <summary>How many tunnels of the origin are admitted.</summary>
        public int OpenTunnels(string originId)
        {
            lock (sync)
                return origins.TryGetValue(originId, out var tunnel) ? tunnel.Connections : 0;
        }

        /// <summary>
        /// Counts a tunnel of <paramref name="originId"/> serving <paramref name="served"/> before it's accepted,
        /// unless the id is connected serving something else, or has <paramref name="maxTunnels"/> already.
        /// An admitted tunnel is run with <see cref="RunTunnelAsync"/>, or given back with <see cref="Release"/>.
        /// </summary>
        public Admission Admit(string originId, IReadOnlyList<ServedRoute> served, int maxTunnels)
        {
            var binding = ServedRoute.Key(served);
            lock (sync)
            {
                if (origins.TryGetValue(originId, out var tunnel))
                {
                    // One aud per origin id at a time, as one host per id before
                    if (tunnel.Binding != binding)
                        return Admission.OtherAudience;
                    if (tunnel.Connections >= maxTunnels)
                        return Admission.Full;
                    tunnel.Connections++;
                    return Admission.Admitted;
                }

                origins[originId] = new OriginTunnel(served, binding) { Connections = 1 };
                RebuildRoutes();
                logger.LogInformation("Origin {OriginId} connected serving {Routes} ({Count} connected)", originId, ServedRoute.Describe(served), origins.Count);
                return Admission.Admitted;
            }
        }

        /// <summary>Gives back an admitted tunnel that was never run.</summary>
        public void Release(string originId)
        {
            lock (sync)
            {
                if (!origins.TryGetValue(originId, out var tunnel))
                    return;
                if (--tunnel.Connections == 0)
                {
                    origins.Remove(originId);
                    RebuildRoutes();
                    tunnel.Idle.Writer.TryComplete();
                    logger.LogInformation("Origin {OriginId} disconnected ({Count} connected)", originId, origins.Count);
                }
            }
        }

        /// <summary>
        /// Offers an admitted tunnel's freshly accepted stream to the pool and completes when the stream closes.
        /// </summary>
        public async Task RunTunnelAsync(string originId, TunnelStream stream, CancellationToken ct)
        {
            OriginTunnel? tunnel;
            lock (sync)
            {
                origins.TryGetValue(originId, out tunnel);
                if (tunnel is not null)
                    logger.LogDebug("Tunnel stream of {OriginId} opened ({Count} open)", originId, tunnel.Connections);
            }
            if (tunnel is null)
            {
                stream.Dispose();
                throw new InvalidOperationException($"Origin {originId} has no admitted tunnel.");
            }

            try
            {
                var entry = new IdleStream(stream);
                // Nothing arrives on an idle tunnel before a request is sent, so a completed
                // read-ahead on a stream still in the pool means the origin closed it
                _ = stream.StartReadAhead().ContinueWith(t =>
                {
                    if (entry.TryMarkDead())
                    {
                        logger.LogDebug("Idle tunnel stream of {OriginId} closed: {Reason}", originId,
                            t.IsCompletedSuccessfully ? t.Result.MessageType.ToString() : t.Exception?.GetBaseException().Message);
                        stream.Dispose();
                    }
                }, TaskScheduler.Default);
                tunnel.Idle.Writer.TryWrite(entry);

                using (ct.Register(stream.Dispose))
                    await stream.Closed;
            }
            finally
            {
                logger.LogDebug("Tunnel stream of {OriginId} closed", originId);
                Release(originId);
            }
        }

        /// <summary>Takes an idle tunnel stream of the origin, waiting for one if all are in use.</summary>
        public async ValueTask<Stream> TakeStreamAsync(string originId, CancellationToken ct)
        {
            OriginTunnel? tunnel;
            lock (sync)
                origins.TryGetValue(originId, out tunnel);

            if (tunnel is null)
                throw new IOException($"WebHop origin {originId} is not connected.");

            try
            {
                while (true)
                {
                    var entry = await tunnel.Idle.Reader.ReadAsync(ct);
                    if (entry.TryTake())
                        return entry.Stream;
                }
            }
            catch (ChannelClosedException)
            {
                throw new IOException($"WebHop origin {originId} disconnected.");
            }
        }

        /// <summary>Publishes fresh routes. Call under the lock.</summary>
        private void RebuildRoutes() => Volatile.Write(ref routes, RouteTable.Build(origins));

        /// <summary>Routes by host, then the longest path; patterns (<c>*.example.com</c>) by the host one label up.</summary>
        private sealed class RouteTable(Dictionary<string, Route[]> exact, Dictionary<string, Route[]> patterns)
        {
            public static readonly RouteTable Empty = new([], []);

            public static RouteTable Build(Dictionary<string, OriginTunnel> origins)
            {
                var routes = origins
                    .SelectMany(o => o.Value.Served.Select(r => (Route: r, OriginId: o.Key)))
                    .GroupBy(x => x.Route, x => x.OriginId)
                    .Select(g => (g.Key.Host, Route: new Route(g.Key.Path, [.. g.Order(StringComparer.Ordinal)])))
                    .ToList();

                return new RouteTable(
                    ByHost(routes.Where(r => !r.Host.StartsWith("*.", StringComparison.Ordinal)), host => host),
                    ByHost(routes.Where(r => r.Host.StartsWith("*.", StringComparison.Ordinal)), host => host[2..]));

                static Dictionary<string, Route[]> ByHost(IEnumerable<(string Host, Route Route)> routes, Func<string, string> key) =>
                    routes.GroupBy(r => key(r.Host), r => r.Route)
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Path.Length).ToArray(), StringComparer.Ordinal);
            }

            public string? Pick(string host, string path)
            {
                var best = Match(exact, host, path);
                // * is one label, so only the host one label up can have a pattern for it
                if (patterns.Count > 0 && host.IndexOf('.') is var dot and > 0
                    && Match(patterns, host[(dot + 1)..], path) is { } pattern
                    && (best is null || pattern.Path.Length > best.Path.Length))
                    best = pattern;
                return best?.NextOrigin();
            }

            private static Route? Match(Dictionary<string, Route[]> routes, string key, string path)
            {
                if (!routes.TryGetValue(key, out var candidates))
                    return null;
                foreach (var route in candidates)
                    if (AudienceEntry.PathCovers(route.Path, path))
                        return route;
                return null;
            }
        }

        /// <summary>The origins serving one host and path, with their own turn counter.</summary>
        private sealed class Route(string path, string[] originIds)
        {
            private int next;

            public string Path { get; } = path;

            public string NextOrigin() => originIds[(int)((uint)Interlocked.Increment(ref next) % (uint)originIds.Length)];
        }

        private sealed class OriginTunnel(IReadOnlyList<ServedRoute> served, string binding)
        {
            public IReadOnlyList<ServedRoute> Served { get; } = served;
            public string Binding { get; } = binding;
            public Channel<IdleStream> Idle { get; } = Channel.CreateUnbounded<IdleStream>();
            public int Connections { get; set; }
        }

        private sealed class IdleStream(TunnelStream stream)
        {
            private const int Idle = 0, Taken = 1, Dead = 2;
            private int state = Idle;

            public TunnelStream Stream => stream;
            public bool TryTake() => Interlocked.CompareExchange(ref state, Taken, Idle) == Idle;
            public bool TryMarkDead() => Interlocked.CompareExchange(ref state, Dead, Idle) == Idle;
        }
    }

    public enum Admission
    {
        Admitted,

        /// <summary>The origin id is connected serving something else (409).</summary>
        OtherAudience,

        /// <summary>The origin has as many tunnels as its token and the host's budget allow (503).</summary>
        Full,
    }

    /// <summary>
    /// What an origin serves: requests for <see cref="Path"/> and below on <see cref="Host"/>, a host key or a
    /// pattern such as <c>*.example.com</c>. <see cref="Path"/> is lowercase, without a trailing slash, empty
    /// for the root.
    /// </summary>
    public readonly record struct ServedRoute(string Host, string Path)
    {
        /// <summary>
        /// What a token serves on a tunnel that arrived on <paramref name="tunnelHost"/>: a URL or pattern entry
        /// its host and path, a path alone that path on the tunnel's host.
        /// </summary>
        public static IReadOnlyList<ServedRoute> Of(OriginToken token, string tunnelHost) =>
            [.. token.Audience.Select(e => new ServedRoute(e.Host ?? tunnelHost, e.BasePath.ToLowerInvariant())).Distinct()];

        internal static string Key(IReadOnlyList<ServedRoute> served) =>
            string.Join(" ", served.Select(r => r.Host + r.Path).Order(StringComparer.Ordinal));

        internal static string Describe(IReadOnlyList<ServedRoute> served) =>
            string.Join(", ", served.Select(r => r.Host + (r.Path.Length == 0 ? "/" : r.Path)));
    }
}
