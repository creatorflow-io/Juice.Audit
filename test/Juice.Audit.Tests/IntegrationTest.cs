
using FluentAssertions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Measurement.Stores;
using Juice.XUnit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Juice.Audit.Tests
{
    public class IntegrationTest(WebApplicationFactory<Program> factory, ITestOutputHelper output)
        : IClassFixture<WebApplicationFactory<Program>>
    {
        [IgnoreOnCIFact(DisplayName = "Should request log")]
        public async Task Should_request_logAsync()
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/request_accesslog");
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            responseString.Should().Be("ok");

            var traceId = response.Headers.GetValues("X-Trace-Id").FirstOrDefault();
            traceId.Should().NotBeNullOrEmpty();

            using var scope = factory.Services.CreateScope();
            var timeRepo = scope.ServiceProvider.GetRequiredService<ITimeRepository>();
            var accessLogRepo = scope.ServiceProvider.GetRequiredService<IAccessLogRepository>();

#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var accessLog = await accessLogRepo.FindAsync(l => l.Request.TraceId == traceId!);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            accessLog.Should().NotBeNull();

            var time = await timeRepo.GetTimeSummaryAsync(traceId!);
            time.Should().BeNull();

        }

        [IgnoreOnCIFact(DisplayName = "Should time exceeded")]
        public async Task Should_time_exceededAsync()
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/time_exceeded");
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            var responseString = await response.Content.ReadAsStringAsync();
            responseString.Should().Be("ok");

            var traceId = response.Headers.GetValues("X-Trace-Id").FirstOrDefault();
            traceId.Should().NotBeNullOrEmpty();

            using var scope = factory.Services.CreateScope();
            var timeRepo = scope.ServiceProvider.GetRequiredService<ITimeRepository>();
            var accessLogRepo = scope.ServiceProvider.GetRequiredService<IAccessLogRepository>();


#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var accessLog = await accessLogRepo.FindAsync(l => l.Request.TraceId == traceId);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            accessLog.Should().NotBeNull();
            accessLog!.Response!.ElapsedMs.Should().BeGreaterThan(2000);

            var time = await timeRepo.GetTimeSummaryAsync(traceId!);
            time.Should().NotBeNull();
            output.WriteLine(time!.Summary);
        }

        [IgnoreOnCIFact(DisplayName = "Should request audit")]
        public async Task Should_request_auditAsync()
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/request_audit");
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            responseString.Should().Be("ok");

            var traceId = response.Headers.GetValues("X-Trace-Id").FirstOrDefault();
            traceId.Should().NotBeNullOrEmpty();

            using var scope = factory.Services.CreateScope();
            var timeRepo = scope.ServiceProvider.GetRequiredService<ITimeRepository>();
            var accessLogRepo = scope.ServiceProvider.GetRequiredService<IAccessLogRepository>();
            var dataAuditRepo = scope.ServiceProvider.GetRequiredService<IDataAuditRepository>();


#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var accessLog = await accessLogRepo.FindAsync(l => l.Request.TraceId == traceId);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            accessLog.Should().NotBeNull();
            accessLog!.Response!.ElapsedMs.Should().BeLessThan(1000);

            var time = await timeRepo.GetTimeSummaryAsync(traceId!);
            time.Should().BeNull();

            var auditEntries = await dataAuditRepo.UnitOfWork.Query()
                .Where(a => a.TraceId == traceId).ToListAsync();
            auditEntries.Should().HaveCount(2);
        }
    }
}
