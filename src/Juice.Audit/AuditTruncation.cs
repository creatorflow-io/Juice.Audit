using System.Text;
using Newtonsoft.Json.Linq;

namespace Juice.Audit
{
    /// <summary>
    /// Cuts a JSON value that is too big to send or store, so it stays valid JSON:
    /// <c>{"$truncated":true,"$originalSize":n,"$partial":"…"}</c>. Shared by the outbox (in the app) and the
    /// collector's truncation processor.
    /// </summary>
    internal static class AuditTruncation
    {
        /// <summary><paramref name="json"/> if it is at most <paramref name="maxBytes"/> UTF-8 bytes, else what fits.</summary>
        public static string TruncateJson(string json, int maxBytes) => TruncateJson(json, maxBytes, Bytes);

        /// <summary>
        /// <paramref name="json"/> if <paramref name="measure"/> of it is at most <paramref name="maxBytes"/>, else the
        /// marked value that keeps the most of it and still fits: a caller that stores the value inside something
        /// bigger (an event, escaped again) measures that instead of the value alone. With no room even for the mark,
        /// the mark alone.
        /// </summary>
        public static string TruncateJson(string json, int maxBytes, Func<string, int> measure)
        {
            if (measure(json) <= maxBytes)
            {
                return json;
            }
            var size = Bytes(json);
            // The measure grows with what is kept: the most that fits, in about log2(length) tries.
            var best = Mark(json, 0, size);
            int low = 1, high = json.Length;
            while (low <= high)
            {
                var keep = low + (high - low) / 2;
                var candidate = Mark(json, keep, size);
                if (measure(candidate) <= maxBytes)
                {
                    best = candidate;
                    low = keep + 1;
                }
                else
                {
                    high = keep - 1;
                }
            }
            return best;
        }

        private static string Mark(string json, int keep, int size)
            => new JObject
            {
                ["$truncated"] = true,
                ["$originalSize"] = size,
                ["$partial"] = Prefix(json, keep)
            }.ToString(Newtonsoft.Json.Formatting.None);

        public static int Bytes(string? value) => value == null ? 0 : Encoding.UTF8.GetByteCount(value);

        /// <summary>
        /// The first <paramref name="length"/> characters, or one fewer when that would split a surrogate pair (an
        /// emoji, say): half of one is not valid UTF-16, and fails or turns into U+FFFD when encoded.
        /// </summary>
        public static string Prefix(string value, int length)
        {
            if (length >= value.Length)
            {
                return value;
            }
            if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            {
                length--;
            }
            return value.Substring(0, Math.Max(0, length));
        }
    }
}
