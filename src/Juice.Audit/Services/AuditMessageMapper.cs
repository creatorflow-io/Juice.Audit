using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Grpc;
using Newtonsoft.Json;
using GrpcRecordResult = Juice.Audit.Grpc.AuditRecordResult;
using RecordResult = Juice.Audit.Commands.AuditRecordResult;

namespace Juice.Audit.Services
{
    /// <summary>
    /// Audit records ↔ gRPC messages, for the client (<c>GrpcAuditExporter</c>, <c>GrpcAuditService</c>) and the server
    /// (<c>AuditGrpcServer</c>). Record ids travel too, so the collector can store each record once.
    /// </summary>
    internal static class AuditMessageMapper
    {
        public static AuditRequest ToMessage(AccessLog accessLog, IEnumerable<DataAudit> auditEntries)
        {
            var request = new AuditRequest
            {
                AccessLog = new AccessLogMessage
                {
                    Id = accessLog.Id == Guid.Empty ? null : accessLog.Id.ToString("D"),
                    User = accessLog.User,
                    TenantId = accessLog.TenantId,
                    Action = accessLog.Action ?? "",
                    Datetime = accessLog.DateTime.ToTimestamp(),
                    Metadata = Bytes(accessLog.Metadata.ToString(Formatting.None)),
                    Restricted = accessLog.IsRestricted,
                    ReqId = accessLog.Request?.TraceId,
                    ReqMethod = accessLog.Request?.Method ?? "",
                    ReqPath = accessLog.Request?.Path ?? "",
                    ReqData = Bytes(accessLog.Request?.Data),
                    ReqQuery = accessLog.Request?.Query,
                    ReqHeaders = Bytes(accessLog.Request?.Headers),
                    ReqScheme = accessLog.Request?.Scheme,
                    ReqRemoteIp = accessLog.Request?.RIPA,
                    ReqHost = accessLog.Request?.Host,
                    ReqZone = accessLog.Request?.Zone,
                    ResStatus = accessLog.Response?.Status,
                    ResData = Bytes(accessLog.Response?.Data),
                    ResErr = Bytes(accessLog.Response?.Err),
                    ResMsg = Bytes(accessLog.Response?.Msg),
                    ResElapsed = accessLog.Response?.ElapsedMs,
                    ResHeaders = Bytes(accessLog.Response?.Headers),
                    SrvMachine = accessLog.Server?.Machine,
                    SrvOS = accessLog.Server?.OS,
                    SrvApp = accessLog.Server?.App,
                    SrvAppVer = accessLog.Server?.AppVer
                }
            };
            request.AuditEntries.AddRange(auditEntries.Select(ToMessage));
            return request;
        }

        public static DataAuditMessage ToMessage(DataAudit entry)
            => new()
            {
                Id = entry.Id == Guid.Empty ? null : entry.Id.ToString("D"),
                User = entry.User,
                Datetime = entry.DateTime.ToTimestamp(),
                Action = entry.Action ?? "",
                Database = entry.Db,
                Schema = entry.Schema,
                Table = entry.Tbl ?? "",
                KeyValues = Bytes(entry.Kvps),
                DataChanges = Bytes(entry.Changes),
                ReqId = entry.TraceId,
                TenantId = entry.TenantId
            };

        public static AccessLog FromMessage(AccessLogMessage ral)
        {
            // The time the app made the record, not when it arrived: batching and retries delay it by up to minutes.
            var accessLog = ral.Datetime == null
                ? new AccessLog(ral.Action, ral.User, ral.TenantId)
                : new AccessLog(ral.Action, ral.User, ral.TenantId) { DateTime = ral.Datetime.ToDateTimeOffset() };
            if (TryParseId(ral.Id, out var id))
            {
                accessLog.Id = id;
            }

            var request = new RequestInfo(
                ral.ReqMethod, ral.ReqPath, ral.ReqData.ToStringUtf8(), ral.ReqQuery,
                ral.ReqHeaders.ToStringUtf8(), ral.ReqScheme, ral.ReqRemoteIp, ral.ReqId, ral.ReqHost);
            if (!string.IsNullOrEmpty(ral.ReqZone))
            {
                request.SetAccessZone(ral.ReqZone);
            }
            accessLog.SetRequestInfo(request);

            if (ral.Restricted)
            {
                accessLog.Restricted();
            }

            var metadata = ral.Metadata.ToStringUtf8();
            if (!string.IsNullOrEmpty(metadata))
            {
                accessLog.SetMetadataJson(metadata);
            }

            accessLog.UpdateResponseInfo(res =>
            {
                if (ral.ResStatus.HasValue && ral.ResElapsed.HasValue)
                {
                    res.SetResponseInfo(ral.ResStatus.Value, ral.ResHeaders.ToStringUtf8(), ral.ResElapsed.Value);
                }
                var resData = ral.ResData.ToStringUtf8();
                var resErr = ral.ResErr.ToStringUtf8();
                var resMsg = ral.ResMsg.ToStringUtf8();
                if (!string.IsNullOrEmpty(resData))
                {
                    res.TrySetData(resData);
                }
                if (!string.IsNullOrEmpty(resErr))
                {
                    res.TrySetError(resErr);
                }
                if (!string.IsNullOrEmpty(resMsg))
                {
                    res.TrySetMessage(resMsg);
                }
            });

            accessLog.SetServerInfo(new ServerInfo(ral.SrvMachine, ral.SrvOS, ral.SrvAppVer, ral.SrvApp));
            return accessLog;
        }

        /// <param name="tenantId">The access record's tenant, for entries sent without one.</param>
        public static DataAudit FromMessage(DataAuditMessage ae, string? tenantId)
        {
            var entry = new DataAudit(
                ae.User, ae.Datetime?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
                ae.Action, ae.Database, ae.Schema, ae.Table,
                ae.KeyValues.ToStringUtf8(), ae.DataChanges.ToStringUtf8(), ae.ReqId,
                ae.TenantId ?? tenantId);
            if (TryParseId(ae.Id, out var id))
            {
                entry.Id = id;
            }
            return entry;
        }

        public static AuditBatchItem FromMessage(AuditRequest request)
        {
            var accessLog = request.AccessLog != null ? FromMessage(request.AccessLog) : null;
            var entries = request.AuditEntries.Select(ae => FromMessage(ae, request.AccessLog?.TenantId)).ToArray();
            return new AuditBatchItem(accessLog, entries);
        }

        public static GrpcRecordResult ToMessage(RecordResult result)
            => new()
            {
                Id = result.Id.ToString("D"),
                Outcome = result.Outcome switch
                {
                    AuditRecordOutcome.Stored => GrpcRecordResult.Types.Outcome.Stored,
                    AuditRecordOutcome.Duplicate => GrpcRecordResult.Types.Outcome.Duplicate,
                    AuditRecordOutcome.Retryable => GrpcRecordResult.Types.Outcome.Retryable,
                    // Rejected, and Unconfirmed, which a collector never gives: not a success if it ever were.
                    _ => GrpcRecordResult.Types.Outcome.Rejected
                },
                Reason = result.Reason
            };

        public static AuditRecordOutcome FromMessage(GrpcRecordResult.Types.Outcome outcome)
            => outcome switch
            {
                GrpcRecordResult.Types.Outcome.Stored => AuditRecordOutcome.Stored,
                GrpcRecordResult.Types.Outcome.Duplicate => AuditRecordOutcome.Duplicate,
                GrpcRecordResult.Types.Outcome.Rejected => AuditRecordOutcome.Rejected,
                // Not set, or added by a later collector: not known to be stored, so sent again (it dedupes).
                _ => AuditRecordOutcome.Retryable
            };

        private static bool TryParseId(string? value, out Guid id)
            => Guid.TryParse(value, out id) && id != Guid.Empty;

        private static ByteString Bytes(string? value)
            => string.IsNullOrEmpty(value) ? ByteString.Empty : ByteString.CopyFromUtf8(value);
    }
}
