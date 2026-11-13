using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WebHop.Gateway
{
    /// <summary>
    /// Holds off tunnel handshakes from an address that keeps presenting wrong auth tokens, so a token can't be
    /// guessed at line rate. After <see cref="Limit"/> rejected tokens within <see cref="Window"/> of the first, the
    /// address is answered 429 until that window is over, without its token being checked. Only rejected tokens
    /// count: origins reconnect all the time (every aborted response closes a tunnel), and those are never held off.
    /// IPv6 addresses count per /64, the block one host usually has to itself.
    /// </summary>
    /// <remarks>
    /// It counts the address the gateway sees. Behind a reverse proxy it doesn't believe (see TrustedProxies),
    /// every client shares the proxy's address, so wrong tokens from one would hold off the others too.
    /// </remarks>
    internal sealed class HandshakeThrottle
    {
        public const int Limit = 20;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        /// <summary>addresses kept before expired ones are swept out; past twice as many, all are forgotten</summary>
        internal const int Tracked = 10_000;

        private static readonly long WindowMs = (long)Window.TotalMilliseconds;

        private readonly ConcurrentDictionary<string, Entry> rejected = new();
        private readonly Func<long> clock;

        public HandshakeThrottle() : this(() => Environment.TickCount64) { }

        /// <param name="clock">milliseconds on a clock that only goes forward</param>
        internal HandshakeThrottle(Func<long> clock) => this.clock = clock;

        /// <summary>how many addresses it keeps a count for</summary>
        internal int Count => rejected.Count;

        /// <summary>How long the address is held off for, or null if its next attempt is checked.</summary>
        public TimeSpan? HeldOff(IPAddress? address)
        {
            if (!rejected.TryGetValue(Key(address), out var entry) || entry.Count < Limit)
                return null;
            var left = entry.Since + WindowMs - clock();
            return left > 0 ? TimeSpan.FromMilliseconds(left) : null;
        }

        /// <summary>Counts a rejected token from the address; true when this one reached the limit.</summary>
        public bool Reject(IPAddress? address)
        {
            var now = clock();
            var entry = rejected.AddOrUpdate(Key(address), _ => new Entry(now, 1),
                (_, old) => now - old.Since >= WindowMs ? new Entry(now, 1) : old with { Count = old.Count + 1 });
            if (rejected.Count > Tracked)
                Sweep(now);
            return entry.Count == Limit;
        }

        private void Sweep(long now)
        {
            foreach (var (key, entry) in rejected)
                if (now - entry.Since >= WindowMs)
                    rejected.TryRemove(key, out _);
            // Wrong tokens from more addresses than that within one window: forget them rather than keep growing
            if (rejected.Count > 2 * Tracked)
                rejected.Clear();
        }

        internal static string Key(IPAddress? address)
        {
            if (address is null)
                return "";
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                return address.ToString();
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }

        /// <summary>rejected tokens since the first in the current window</summary>
        private readonly record struct Entry(long Since, int Count);
    }
}
