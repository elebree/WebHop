using System.Globalization;

namespace WebHop.Core
{
    /// <summary>Durations as the commands take and show them: a whole number and a unit, such as 30m, 12h or 7d.</summary>
    internal static class Durations
    {
        public static bool TryParse(string? value, out TimeSpan duration)
        {
            duration = default;
            if (string.IsNullOrEmpty(value) || value.Length < 2
                || !long.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0 || amount > int.MaxValue)
                return false;

            double? seconds = value[^1] switch
            {
                's' => amount,
                'm' => amount * 60.0,
                'h' => amount * 3600.0,
                'd' => amount * 86400.0,
                'w' => amount * 604800.0,
                _ => null,
            };
            if (seconds is not { } s || s > int.MaxValue)
                return false;
            duration = TimeSpan.FromSeconds(s);
            return true;
        }

        /// <summary>The largest unit that fits evenly: 90d, 12h, 30m, 45s.</summary>
        public static string Format(TimeSpan duration)
        {
            var seconds = (long)duration.TotalSeconds;
            return seconds switch
            {
                _ when seconds % 86400 == 0 => $"{seconds / 86400}d",
                _ when seconds % 3600 == 0 => $"{seconds / 3600}h",
                _ when seconds % 60 == 0 => $"{seconds / 60}m",
                _ => $"{seconds}s",
            };
        }
    }
}
