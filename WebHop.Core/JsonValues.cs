using System.Globalization;
using System.Text.Json;

namespace WebHop.Core
{
    /// <summary>
    /// Reads key set members and JWT headers. Numbers may also come as strings, as a set nested in
    /// appsettings.json reaches the gateway through IConfiguration, which keeps every value as a string.
    /// </summary>
    internal static class JsonValues
    {
        public static string? String(JsonElement element, string member) =>
            element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        public static List<string>? Strings(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array)
                return null;
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return null;
                result.Add(item.GetString()!);
            }
            return result;
        }

        /// <summary>
        /// Whether an object names a member twice, compared after unescaping, so a name spelled with
        /// <c>\u</c> escapes is the same name. JSON readers disagree on which duplicate counts, so a JWT with one
        /// is refused outright (RFC 7515 §4, RFC 7519 §4).
        /// </summary>
        public static bool HasDuplicateMember(JsonElement element)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    return true;
            }
            return false;
        }

        public static long? Number(JsonElement element, string member, string name)
        {
            if (!element.TryGetProperty(member, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                return number;
            throw new FormatException($"{name}: {member} is not a whole number");
        }

        public static DateTimeOffset? Time(JsonElement element, string member, string name) =>
            Number(element, member, name) is { } seconds
                ? seconds is >= 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : throw new FormatException($"{name}: {member} is not a time")
                : null;

        public static TimeSpan? Seconds(JsonElement element, string member, string name) =>
            Number(element, member, name) is { } seconds
                ? seconds > 0 && seconds <= int.MaxValue ? TimeSpan.FromSeconds(seconds) : throw new FormatException($"{name}: {member} is not a positive number of seconds")
                : null;
    }
}
