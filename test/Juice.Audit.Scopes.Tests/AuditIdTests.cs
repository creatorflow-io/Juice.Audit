using System.Globalization;
using FluentAssertions;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// Record ids are given in the app and are the deduplication key: unique, version 7, and time-ordered at
    /// millisecond precision. The net8 run covers the fallback layout, net9 and later <c>Guid.CreateVersion7</c>.
    /// </summary>
    public class AuditIdTests
    {
        private static long Timestamp(Guid id)
            => long.Parse(id.ToString("N")[..12], NumberStyles.HexNumber);

        [Fact]
        public void Ids_are_version_7_with_the_RFC_variant()
        {
            for (var i = 0; i < 1000; i++)
            {
                var text = AuditId.New().ToString("D");
                text[14].Should().Be('7');
                "89ab".Should().Contain(text[19].ToString());
            }
        }

        [Fact]
        public void Ids_are_unique_and_their_timestamps_never_go_back()
        {
            var ids = Enumerable.Range(0, 10_000).Select(_ => AuditId.New()).ToList();

            ids.Should().OnlyHaveUniqueItems();
            ids.Select(Timestamp).Should().BeInAscendingOrder();
        }

        [Fact]
        public void The_timestamp_is_the_creation_time_in_unix_milliseconds()
        {
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = AuditId.New();
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Timestamp(id).Should().BeInRange(before, after);
        }

        [Fact]
        public void New_records_get_an_id_when_created()
        {
            new Domain.AccessLogAggregate.AccessLog("action", "user").Id.Should().NotBeEmpty();
            new Domain.DataAuditAggregate.DataAudit("user", DateTimeOffset.UtcNow, "Modified", null, null, "Orders",
                "{}", "{}", "trace").Id.Should().NotBeEmpty();
        }
    }
}
