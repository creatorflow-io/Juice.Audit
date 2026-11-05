using System.Diagnostics;
using System.Reflection;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Measurement;
using Juice.Measurement.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Audit.Api.Scopes
{
    /// <summary>
    /// The steps every audit scope shares, an HTTP request's (<see cref="AuditMiddleware"/>) or a mediator request's
    /// or message's (<see cref="AuditScopeRunner"/>): the server and span it ran on, why it is saved, the save, and
    /// the time logging.
    /// </summary>
    internal static class AuditRecorder
    {
        /// <summary>
        /// The W3C trace id (32 hex characters) of <paramref name="activity"/>, the id tracing back ends such as Juice
        /// Observability show, so an audit record can be looked up there; <paramref name="fallback"/> without one.
        /// </summary>
        public static string TraceId(Activity? activity, string fallback)
            => activity is { IdFormat: ActivityIdFormat.W3C } ? activity.TraceId.ToHexString() : fallback;

        public static ServerInfo CreateServerInfo(string? appName)
        {
            var assembly = Assembly.GetEntryAssembly();
            // Location is empty in a single-file app.
            var fvi = assembly != null && !string.IsNullOrEmpty(assembly.Location)
                ? FileVersionInfo.GetVersionInfo(assembly.Location) : default;
            return new ServerInfo(
                Environment.MachineName,
                Environment.OSVersion.ToString(),
                fvi?.ProductVersion ?? assembly?.GetName()?.Version?.ToString(),
                appName
                );
        }

        /// <summary>The scope's own span, not a time tracker scope's.</summary>
        public static void SetSpanId(AuditContext auditContext, Activity? activity)
        {
            if (activity is { IdFormat: ActivityIdFormat.W3C })
            {
                auditContext.AccessRecord.SetMetadata("SpanId", activity.SpanId.ToHexString());
            }
        }

        /// <summary>Why the record is saved.</summary>
        public static void SetReasons(AuditContext auditContext, bool isFilterMatched, string? rule,
            bool isAccessLogRequested, bool isMeasureRequested, bool isTimeExceeded)
        {
            var metadata = auditContext.AccessRecord.Metadata;
            metadata["FilterMatched"] = isFilterMatched;
            metadata["FilterRule"] = rule;
            metadata["AccessLogRequest"] = isAccessLogRequested;
            metadata["DataAuditRequest"] = auditContext.IsRequestedForAuditLog;
            metadata["MeasureRequested"] = isMeasureRequested;
            metadata["TimeExceeded"] = isTimeExceeded;
        }

        /// <summary>Saves the access record and its data audits; never throws.</summary>
        public static async Task PersistAsync(IServiceProvider services, AuditContext auditContext, ITimeTracker tracker,
            ILogger logger)
        {
            try
            {
                using var _ = tracker.BeginScope("SaveAuditData");
                if (auditContext.UncommittedAuditEntryCount > 0)
                {
                    auditContext.AccessRecord.Metadata["UncommittedChanges"] = auditContext.UncommittedAuditEntryCount;
                }
                var auditService = services.GetService<IAuditService>();
                tracker.Checkpoint("GetAuditService");

                if (auditService != null)
                {
                    var rs = await auditService.PersistAuditInformationAsync(auditContext.AccessRecord,
                        auditContext.AuditEntries.ToArray(), default);
                    if (!rs.Succeeded)
                    {
                        logger.LogWarning("Error while saving audit information. {0}", rs.ToString());
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace(rs.StackTrace);
                        }
                    }
                    tracker.Checkpoint("Persist");
                }
                else if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Skip PersistAuditInformation because IAuditService is not registered.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"Error while saving audit information");
            }
        }

        /// <summary>
        /// Saves the time tracker's records to the optional time store, or logs them without one; never throws.
        /// </summary>
        public static async Task SaveTimeAsync(IServiceProvider services, AuditContext auditContext, ITimeTracker tracker,
            Activity? activity, string traceId, ILogger logger)
        {
            if (activity is { Recorded: true })
            {
                activity.SetTag(AuditMiddleware.TimeLoggedTag, true);
            }
            try
            {
                using var _ = tracker.BeginScope("SaveTrackData");
                // The time store is an optional plugin: registered only by apps that keep the records.
                var timeRepository = services.GetService<ITimeRepository>();
                _.Dispose();
                var actionName = auditContext.AccessRecord.Action;
                if (timeRepository != null)
                {
                    await timeRepository.SaveTrackDataAsync(tracker, traceId,
                        auditContext.Items.GetValueOrDefault("ScopeId") as string ?? actionName,
                        actionName);
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.LogTrace(tracker.ToString(true));
                    }
                }
                else if (logger.IsEnabled(LogLevel.Information))
                {
                    // Without a store the log is where the timing goes.
                    logger.LogInformation("Execution time of {Action}, trace {TraceId}:\n{TimeTable}",
                        actionName, traceId, tracker.ToString(true));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while saving execution time trace data");
            }
        }
    }
}
