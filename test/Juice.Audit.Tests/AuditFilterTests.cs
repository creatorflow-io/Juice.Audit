using System;
using FluentAssertions;
using Juice.Audit.Api.Extensions;
using Juice.Audit.AspNetCore.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit.Abstractions;

namespace Juice.Audit.Tests
{
    public class AuditFilterTests
    {
        private readonly ITestOutputHelper _output;

        public AuditFilterTests(ITestOutputHelper testOutput)
        {
            _output = testOutput;
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        }


        [Fact(DisplayName = "Path should match")]
        public void Path_should_match()
        {
            string? route = default;
            IDictionary<string, string>? routeValues;
            var pattern = "kernel/*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("kernel/info", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info/x", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("kernel", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("x/kernel/info", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "kernel/*/#";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("kernel/info", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info/x", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info/x/y/z", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("x/kernel/info", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "kernel/*/*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("kernel/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("kernel/info/x", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info/x/y", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("kernel", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("x/kernel/info", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "*/kernel/*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("x/kernel/info", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("kernel/info/x", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "#/kernel/*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("x/kernel/info", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("kernel/info/x", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "/kernel/#/info/*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("/x/kernel/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/x/info/y", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("/kernel/x/y/info/z", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("/kernel/info/x", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("/kernel/info/x/y", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("x", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("x/y", pattern, out route, out routeValues).Should().BeFalse();

            pattern = "/#/negotiate";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("/negotiate", pattern, out route, out routeValues).Should().BeTrue();
            StringUtils.IsPathMatch("/negotiate/x", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/signalrhub/negotiate", pattern, out route, out routeValues).Should().BeTrue();

            pattern = "/kernel/*/{id}/info";
            _output.WriteLine(pattern);
            
            StringUtils.IsPathMatch("/kernel/x/abc/info", pattern, out route, out routeValues).Should().BeTrue();
            route.Should().Be("/kernel/x/{id}/info");
            routeValues.Should().HaveCount(1);
            _output.WriteLine($"routeValues: {string.Join(", ", routeValues!.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

            StringUtils.IsPathMatch("/kernel/x/1/info", pattern, out route, out routeValues).Should().BeTrue();
            route.Should().Be("/kernel/x/{id}/info");
            routeValues.Should().HaveCount(1);
            _output.WriteLine($"routeValues: {string.Join(", ", routeValues!.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

            StringUtils.IsPathMatch("/kernel/x/abc/info/1", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/abc/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/x/info", pattern, out route, out routeValues).Should().BeFalse();


            pattern = "/kernel/*/{id}/{id1}/info/*";
            _output.WriteLine(pattern);
            StringUtils.IsPathMatch("/kernel/x/abc/1/info/2", pattern, out route, out routeValues).Should().BeTrue();
            route.Should().Be("/kernel/x/{id}/{id1}/info/2");
            routeValues.Should().HaveCount(2);
            _output.WriteLine($"routeValues: {string.Join(", ", routeValues!.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

            StringUtils.IsPathMatch("/kernel/x/1/2/info/3", pattern, out route, out routeValues).Should().BeTrue();
            route.Should().Be("/kernel/x/{id}/{id1}/info/3");
            routeValues.Should().HaveCount(2);
            _output.WriteLine($"routeValues: {string.Join(", ", routeValues!.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

            StringUtils.IsPathMatch("/kernel/x/abc/info/1", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/x/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/x/abc/info/1/2", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/x/1/info", pattern, out route, out routeValues).Should().BeFalse();
            StringUtils.IsPathMatch("/kernel/x/1/2/info", pattern, out route, out routeValues).Should().BeFalse();
        }

        [Fact(DisplayName = "Filter should match without status")]
        public void Filter_should_match()
        {
            var filter = new AuditFilterOptions();
            filter.Include("", "POST", "PUT");
            filter.Include("kernel/*/#", "POST", "PUT", "PATCH");
            filter.Exclude("kernel/*/*", "POST");
            filter.Include("*/kernel/*", "GET");
            filter.Include("kernel/*/index");
            filter.Exclude("/#/negotiate");

            string? rule = default;
            string? action = default;

            filter.IsMatch("kernel/info", "GET", out rule, out _, out _).Should().BeFalse();
            rule.Should().BeNull();
            _output.WriteLine($"kernel/info does not matched. {rule ?? "none"}");

            filter.IsMatch("kernel/info", "POST", out rule, out action, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/#");
            action.Should().Be("kernel_info");
            _output.WriteLine($"kernel/info matched. {rule ?? "none"}");

            filter.IsMatch("kernel/info/index", "GET", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/index");
            _output.WriteLine($"kernel/info/index matched. {rule ?? "none"}");

            filter.IsMatch("kernel/info/index", "POST", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/index");
            _output.WriteLine($"kernel/info/index matched. {rule ?? "none"}");

            filter.IsMatch("kernel/info/x", "PUT", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/#");
            _output.WriteLine($"kernel/info/x matched. {rule ?? "none"}");

            filter.IsMatch("kernel/info/x", "POST", out rule, out _, out _).Should().BeFalse();
            rule.Should().BeSameAs("kernel/*/*");
            _output.WriteLine($"kernel/info/x does not matched. {rule ?? "none"}");

            filter.IsMatch("kernel/info/x/y/z", "PATCH", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/#");
            _output.WriteLine($"kernel/info/x/y/z matched. {rule ?? "none"}");

            filter.IsMatch("kernel", "POST", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("");
            _output.WriteLine($"kernel matched. {rule ?? "none"}");

            filter.IsMatch("ker/info/index", "POST", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("");
            _output.WriteLine($"ker/info/index matched. {rule ?? "none"}");

            filter.IsMatch("ker/kernel/index", "GET", out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("*/kernel/*");
            _output.WriteLine($"ker/kernel/index matched. {rule ?? "none"}");

            filter.IsMatch("/signalrhub/negotiate", "POST", out rule, out _, out _).Should().BeFalse();
        }

        [Fact(DisplayName = "Filter should match with route")]
        public void Filter_should_match_route()
        {
            var filter = new AuditFilterOptions();
            filter.Include("", "POST", "PUT");
            filter.Exclude("kernel/*/*", "POST");
            filter.Include("*/kernel/*", "GET");
            filter.Include("kernel/*/index");
            filter.Exclude("/#/negotiate");
            filter.Include("kernel/*/{id}/#");


            string? rule = default;
            string? action = default;

            filter.IsMatch("kernel/info/x/y", "GET", out rule, out action, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/{id}/#");
            action.Should().Be("kernel_info_{id}_y");

            filter.IsMatch("kernel/info/6/y", "GET", out rule, out action, out _).Should().BeTrue();
            rule.Should().BeSameAs("kernel/*/{id}/#");
            action.Should().Be("kernel_info_{id}_y");
        }

        [Fact(DisplayName = "Filter should match with status code")]
        public void Filter_should_match_code()
        {
            var filter = new AuditFilterOptions();
            filter.Include("", "POST", "PUT");
            filter.Exclude("/#/negotiate");
            filter.Include("", new int[] { 403 });

            string? rule = default;
            filter.IsMatch("kernel/info", "GET", out rule, out _, out _).Should().BeTrue();
            rule.Should().Be("");

            filter.IsMatch("kernel/info", "POST", out rule, out _, out _).Should().BeTrue();
            rule.Should().Be("");

            filter.IsMatch("kernel/info", "POST", 403, out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("");

            filter.IsMatch("kernel/info", "POST", 404, out rule, out _, out _).Should().BeTrue();
            rule.Should().BeSameAs("");

            filter.IsMatch("kernel/info", "GET", 404, out rule, out _, out _).Should().BeFalse();
            rule.Should().BeSameAs(null);
        }

        [Fact(DisplayName = "Header should match")]
        public void Header_should_match()
        {
            StringUtils.IsHeaderMatch(":authority:", ":authority:").Should().BeTrue();
            StringUtils.IsHeaderMatch("referer", "referer-*").Should().BeFalse();

            StringUtils.IsHeaderMatch("content", "content-*").Should().BeFalse();
            StringUtils.IsHeaderMatch("content-length", "content-*").Should().BeTrue();
            StringUtils.IsHeaderMatch("content-type", "content-*").Should().BeTrue();
            StringUtils.IsHeaderMatch("content-type-x", "content-*").Should().BeFalse();

            StringUtils.IsHeaderMatch("accept", "accept-#").Should().BeTrue();
            StringUtils.IsHeaderMatch("accept-encoding", "accept-#").Should().BeTrue();
            StringUtils.IsHeaderMatch("accept-language", "accept-#").Should().BeTrue();
            StringUtils.IsHeaderMatch("accept-a-b-c", "accept-#").Should().BeTrue();

            var options = new AuditFilterOptions();

            options.IsReqHeaderMatch("referer").Should().BeTrue();
            options.IsReqHeaderMatch("referer-a").Should().BeFalse();
            options.IsReqHeaderMatch("content-type").Should().BeTrue();
            options.IsReqHeaderMatch("content-type-x").Should().BeFalse();
            options.IsReqHeaderMatch("accept").Should().BeTrue();
            options.IsReqHeaderMatch("accept-encoding").Should().BeTrue();
            options.IsReqHeaderMatch("accept-language").Should().BeTrue();
            options.IsReqHeaderMatch("accept-a-b-c").Should().BeTrue();
            options.IsReqHeaderMatch(":authority:").Should().BeTrue();
        }


        [Fact(DisplayName = "Filter should be replaced")]
        public void Path_should_replaced()
        {
            var path = new PathString("/kernel/info/1");
            var (p, id) = path.GetPathComponents();
            p.Should().Be("/kernel/info/{id}");
            id.Should().Be("1");

            path = new PathString("/kernel/info/1/action");
            (p, id) = path.GetPathComponents();
            p.Should().Be("/kernel/info/{id}/action");
            id.Should().Be("1");

            path = new PathString("/kernel/info/" + Guid.NewGuid());
            (p, id) = path.GetPathComponents();
            p.Should().Be("/kernel/info/{id}");
            id.Should().NotBeNull();

            path = new PathString("/kernel/info/" + Guid.NewGuid() + "/action");
            (p, id) = path.GetPathComponents();
            p.Should().Be("/kernel/info/{id}/action");
            id.Should().NotBeNull();
        }
    }
}
