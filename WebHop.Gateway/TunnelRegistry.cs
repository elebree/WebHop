using System.Threading.Channels;
using WebHop.Core;

namespace WebHop.Gateway
{
    /// <summary>
    /// Tracks connected WebHop origins and the idle tunnel streams each of them keeps open.
    /// An origin is registered while it has at least one tunnel stream connected. Each origin serves
    /// the host it opened its tunnels on, so one gateway can serve several sites on different names.
    /// </summary>
    public sealed class TunnelRegistry(ILogger<TunnelRegistry> logger)
    {
        private readonly object sync = new();
        private readonly Dictionary<string, OriginTunnel> origins = [];
        // Rebuilt under the lock on every connect and disconnect, read without it
        private Dictionary<string, Route> routes = [];

        public int OriginCount
        {
            get
            {
                lock (sync)
                    return origins.Count;
            }
        }

        /// <summary>Origin ids double as host names in YARP destination URLs.</summary>
        public static bool IsValidOriginId(string? id) =>
            id is { Length: > 0 and <= 63 } && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

        /// <summary>
        /// The routing key of a request's host: the name without the port, lowercase, without a
        /// trailing dot. Tunnels and public requests are matched on it.
        /// </summary>
        public static string HostKey(HostString host) => host.Host.TrimEnd('.').ToLowerInvariant();

        /// <summary>Round-robin over the origins connected on <paramref name="host"/>; null when none are.</summary>
        public string? PickOrigin(string host)
        {
            if (!Volatile.Read(ref routes).TryGetValue(host, out var route))
                return null;
            return route.OriginIds[(int)((uint)Interlocked.Increment(ref route.Next) % (uint)route.OriginIds.Length)];
        }

        /// <summary>
        /// True when <paramref name="originId"/> is already connected on a host other than
        /// <paramref name="host"/>. An origin serves one host; its tunnels on another are refused.
        /// </summary>
        public bool ServesOtherHost(string originId, string host)
        {
            lock (sync)
                return origins.TryGetValue(originId, out var tunnel) && tunnel.Host != host;
        }

        /// <summary>
        /// Offers a freshly accepted tunnel stream to the pool and completes when the stream closes.
        /// </summary>
        /// <param name="host">The tunnel's <see cref="HostKey"/>: public requests for it go to this origin.</param>
        public async Task RunTunnelAsync(string host, string originId, TunnelStream stream, CancellationToken ct)
        {
            OriginTunnel tunnel;
            lock (sync)
            {
                if (!origins.TryGetValue(originId, out tunnel!))
                {
                    origins[originId] = tunnel = new OriginTunnel(host);
                    RebuildRoutes();
                    logger.LogInformation("Origin {OriginId} connected on {Host} ({Count} connected)", originId, host, origins.Count);
                }
                else if (tunnel.Host != host)
                {
                    // Lost a race with a tunnel on another host that ServesOtherHost didn't see yet
                    logger.LogWarning("Closed a tunnel of {OriginId} on {Host}: it is connected on {ConnectedHost}", originId, host, tunnel.Host);
                    stream.Dispose();
                    return;
                }
                tunnel.Connections++;
                logger.LogDebug("Tunnel stream of {OriginId} opened ({Count} open)", originId, tunnel.Connections);
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
                lock (sync)
                {
                    logger.LogDebug("Tunnel stream of {OriginId} closed ({Count} open)", originId, tunnel.Connections - 1);
                    if (--tunnel.Connections == 0)
                    {
                        origins.Remove(originId);
                        RebuildRoutes();
                        tunnel.Idle.Writer.TryComplete();
                        logger.LogInformation("Origin {OriginId} disconnected from {Host} ({Count} connected)", originId, host, origins.Count);
                    }
                }
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

        /// <summary>Publishes a fresh host-to-origins map. Call under the lock.</summary>
        private void RebuildRoutes()
        {
            Volatile.Write(ref routes, origins
                .GroupBy(o => o.Value.Host, o => o.Key)
                .ToDictionary(g => g.Key, g => new Route([.. g])));
        }

        /// <summary>The origins of one host, with its own turn counter so hosts don't skew each other's turns.</summary>
        private sealed class Route(string[] originIds)
        {
            public readonly string[] OriginIds = originIds;
            public int Next;
        }

        private sealed class OriginTunnel(string host)
        {
            public string Host { get; } = host;
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
}
