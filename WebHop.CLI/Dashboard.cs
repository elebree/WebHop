using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using WebHop.Core;
using WebHop.Origin;

namespace WebHop.CLI
{
    /// <summary>
    /// ngrok-style live console: session status at the top, recent requests below.
    /// Redraws in place, only when something changed.
    /// </summary>
    internal sealed class Dashboard(SessionInfo info, Session session)
    {
        private const int LabelWidth = 30;

        internal readonly record struct Segment(string Text, ConsoleColor? Color = null);

        public async Task RunAsync(CancellationToken ct)
        {
            var lastVersion = -1;
            WebHopOriginStatus? lastStatus = null;
            Segment? lastExpiry = null;
            var lastSize = (Width: 0, Height: 0);

            TrySetCursorVisible(false);
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                do
                {
                    var status = info.Server.Status;
                    var size = (Width: Console.WindowWidth, Height: Console.WindowHeight);
                    if (size != lastSize)
                    {
                        Console.Clear();
                        lastVersion = -1;
                        lastSize = size;
                    }

                    var expiry = Expiry(info.Token, DateTimeOffset.UtcNow);
                    if (session.Version != lastVersion || status != lastStatus || expiry != lastExpiry)
                    {
                        lastVersion = session.Version;
                        lastStatus = status;
                        lastExpiry = expiry;
                        Render(Build(status, expiry, size.Height), size.Width, size.Height);
                    }
                }
                while (await WaitAsync(timer, ct));
            }
            finally
            {
                Console.ResetColor();
                Console.Clear();
                TrySetCursorVisible(true);
                Console.WriteLine($"WebHop session closed ({session.Total} requests).");
            }
        }

        private List<Segment[]> Build(WebHopOriginStatus status, Segment expiry, int height)
        {
            List<Segment[]> lines =
            [
                [new("WebHop", ConsoleColor.Cyan), new("                                                (Ctrl+C to quit)", ConsoleColor.DarkGray)],
                [],
                Row("Session Status", StatusText(status)),
                Row("Version", new Segment(AppVersion.Current)),
                Row("Origin Id", new Segment(info.Token.Subject)),
                Row("Tunnels", new Segment($"{status.OpenTunnels}/{status.MaxTunnels} open")),
                Row("Forwarding", new Segment($"{info.PublicUrl} -> {info.Target.ToString().TrimEnd('/')}")),
                Row("Token Expires", expiry),
            ];

            if (status.LastError is { } error)
                lines.Add(Row(status.State == WebHopConnectionState.Online ? "Warning" : "Error",
                    new Segment(error, status.State == WebHopConnectionState.Online ? ConsoleColor.Yellow : ConsoleColor.Red)));
            if (info.Server.TunnelUri is { Scheme: "ws", IsLoopback: false })
                lines.Add(Row("Warning", new Segment("the auth token is sent unencrypted; use an https:// gateway", ConsoleColor.Yellow)));
            if (session.LastError is { } hostError)
                lines.Add(Row("Last Error", new Segment(hostError, ConsoleColor.Red)));

            var percentiles = session.Percentiles();
            lines.Add([]);
            lines.Add(Row("Connections", new Segment($"{"ttl",-8}{"opn",-8}{"p50",-10}{"p90",-10}", ConsoleColor.DarkGray)));
            lines.Add(Row("", new Segment($"{session.Total,-8}{session.InFlight,-8}{Ms(percentiles?.P50),-10}{Ms(percentiles?.P90),-10}")));
            lines.Add([]);
            lines.Add([new("HTTP Requests")]);
            lines.Add([new("-------------")]);

            foreach (var entry in session.RecentRequests(Math.Max(0, height - lines.Count - 1)))
            {
                lines.Add([
                    new($"{entry.Time:HH:mm:ss}  ", ConsoleColor.DarkGray),
                    new($"{entry.Method,-7} "),
                    new($"{Truncate(entry.Path, 40),-41} "),
                    new($"{entry.StatusCode} {ReasonPhrases.GetReasonPhrase(entry.StatusCode),-22} ", StatusColor(entry.StatusCode)),
                    new($"{HttpCommand.FormatDuration(entry.Duration),8}", ConsoleColor.DarkGray),
                    new(entry.Error is null ? "" : $"  {entry.Error}", ConsoleColor.Red),
                ]);
            }

            return lines;
        }

        private static void Render(List<Segment[]> lines, int width, int height)
        {
            // Stay one column short of the edge so a full line never wraps and scrolls the screen
            var usable = Math.Max(1, width - 1);
            for (var row = 0; row < height - 1; row++)
            {
                Console.SetCursorPosition(0, row);
                var written = 0;
                if (row < lines.Count)
                {
                    foreach (var segment in lines[row])
                    {
                        if (written >= usable)
                            break;
                        var text = segment.Text.Length > usable - written ? segment.Text[..(usable - written)] : segment.Text;
                        if (segment.Color is { } color)
                            Console.ForegroundColor = color;
                        Console.Write(text);
                        Console.ResetColor();
                        written += text.Length;
                    }
                }
                Console.Write(new string(' ', usable - written));
            }
        }

        private static Segment[] Row(string label, params Segment[] value) =>
            [new(label.PadRight(LabelWidth)), .. value];

        private static Segment StatusText(WebHopOriginStatus status) => status.State switch
        {
            WebHopConnectionState.Online => new("online", ConsoleColor.Green),
            WebHopConnectionState.Connecting => new("connecting", ConsoleColor.Yellow),
            WebHopConnectionState.Offline => new("reconnecting", ConsoleColor.Red),
            WebHopConnectionState.Unauthorized => new("unauthorized - check the auth token", ConsoleColor.Red),
            WebHopConnectionState.Forbidden => new("forbidden - the token doesn't serve this host", ConsoleColor.Red),
            WebHopConnectionState.Conflict => new("conflict - the origin id is connected with another token", ConsoleColor.Red),
            _ => new(status.State.ToString()),
        };

        /// <summary>
        /// When the token expires; yellow shortly before (within a day, or a tenth of a shorter lifetime), red
        /// once expired. Coarse, so the screen redraws only when the text changes.
        /// </summary>
        internal static Segment Expiry(OriginToken token, DateTimeOffset now)
        {
            var when = token.Expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var left = token.Expires - now;
            if (left <= TimeSpan.Zero)
                return new($"{when} (expired; get a new token)", ConsoleColor.Red);

            var soon = TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(1).Ticks, (token.Expires - token.IssuedAt).Ticks / 10));
            var inText = left.TotalDays >= 2 ? $"in {(int)left.TotalDays} days"
                : left.TotalHours >= 2 ? $"in {(int)left.TotalHours} hours"
                : $"in {Math.Max(1, (int)left.TotalMinutes)} min";
            return left <= soon
                ? new($"{when} ({inText}; get a new token soon)", ConsoleColor.Yellow)
                : new($"{when} ({inText})");
        }

        private static ConsoleColor StatusColor(int statusCode) => statusCode switch
        {
            < 300 => ConsoleColor.Green,
            < 400 => ConsoleColor.Cyan,
            < 500 => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };

        private static string Ms(double? milliseconds) =>
            milliseconds is { } ms ? HttpCommand.FormatDuration(TimeSpan.FromMilliseconds(ms)) : "-";

        private static string Truncate(string value, int length) =>
            value.Length <= length ? value : value[..(length - 1)] + "…";

        private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
        {
            try
            {
                return await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static void TrySetCursorVisible(bool visible)
        {
            try
            {
                Console.CursorVisible = visible;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}
