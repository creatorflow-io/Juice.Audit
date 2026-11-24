using System.Security.Cryptography;

namespace Juice.Audit
{
    /// <summary>
    /// Ids of audit records, given in the app when a record is created. They are the deduplication key all the way to
    /// storage: a record sent twice (a retried batch, a redelivered message) is stored once.
    /// </summary>
    public static class AuditId
    {
        /// <summary>
        /// A time-ordered GUID (RFC 9562 version 7): a 48-bit Unix millisecond timestamp, then random bits. Ids made
        /// later sort later, at millisecond precision.
        /// </summary>
        public static Guid New()
        {
#if NET9_0_OR_GREATER
            return Guid.CreateVersion7();
#else
            return NewVersion7(DateTimeOffset.UtcNow);
#endif
        }

        /// <summary>The version 7 layout, for frameworks before .NET 9 (the net8 test run covers it).</summary>
        private static Guid NewVersion7(DateTimeOffset timestamp)
        {
            var ms = timestamp.ToUnixTimeMilliseconds();
            Span<byte> random = stackalloc byte[10];
            RandomNumberGenerator.Fill(random);

            // Guid(int, short, short, bytes) writes the first three fields so that ToString() and the big-endian
            // byte order both start with the timestamp.
            var a = (int)(ms >> 16);
            var b = (short)(ms & 0xFFFF);
            var c = (short)(0x7000 | ((random[0] << 8 | random[1]) & 0x0FFF));
            var d = (byte)(0x80 | (random[2] & 0x3F));
            return new Guid(a, b, c, d, random[3], random[4], random[5], random[6], random[7], random[8], random[9]);
        }
    }
}
