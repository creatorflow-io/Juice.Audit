
using FluentAssertions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.XUnit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using Xunit.Abstractions;

namespace Juice.Audit.Tests
{
    public class IntegrationTest
        : IClassFixture<WebApplicationFactory<Program>>
    {
        private WebApplicationFactory<Program> factory;
        private ITestOutputHelper output;

        public IntegrationTest(WebApplicationFactory<Program> factory, ITestOutputHelper output)
        {
            this.factory = factory;
            this.output = output;
        }
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
            var accessLogRepo = scope.ServiceProvider.GetRequiredService<IAccessLogRepository>();

#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var accessLog = await accessLogRepo.FindAsync(l => l.Request.TraceId == traceId!);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            accessLog.Should().NotBeNull();
            output.WriteLine(accessLog!.Request!.TraceId);

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
            var accessLogRepo = scope.ServiceProvider.GetRequiredService<IAccessLogRepository>();
            var dataAuditRepo = scope.ServiceProvider.GetRequiredService<IDataAuditRepository>();


#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var accessLog = await accessLogRepo.FindAsync(l => l.Request.TraceId == traceId);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            accessLog.Should().NotBeNull();
            accessLog!.Response!.ElapsedMs.Should().BeLessThan(1000);

            var auditEntries = await dataAuditRepo.UnitOfWork.Query<DataAudit>()
                .Where(a => a.TraceId == traceId).ToListAsync();
            auditEntries.Should().HaveCount(2);
        }
    }
}
