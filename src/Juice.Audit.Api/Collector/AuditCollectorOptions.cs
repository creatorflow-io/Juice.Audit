namespace Juice.Audit.Collector
{
    /// <summary>
    /// What the collector's built-in processors do to a record before it is stored. Bound from <c>Audit:Collector</c>,
    /// then <c>AddAuditCollector(configure)</c>.
    /// </summary>
    public sealed class AuditCollectorOptions
    {
        /// <summary>
        /// Request and response headers whose value is replaced by <see cref="RedactMask"/>, by name, ignoring case.
        /// Configuration adds to this list; set the property in code to replace it.
        /// </summary>
        public string[] RedactHeaders { get; set; } = { "Authorization", "Cookie", "Set-Cookie", "X-Api-Key" };

        public string RedactMask { get; set; } = "***";

        /// <summary>
        /// The longest request body, response body and response error kept, in characters, and the largest
        /// <c>DataAudit.Changes</c>, in UTF-8 bytes. Longer values are cut, and the original sizes noted.
        /// </summary>
        public int MaxBodyLength { get; set; } = 64 * 1024;
    }
}
