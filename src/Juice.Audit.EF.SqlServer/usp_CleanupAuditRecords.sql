-- Creates the SchemaTableList type and usp_CleanupAuditRecords procedure.
-- Run once per database. Re-runnable: CREATE OR ALTER for the procedure,
-- the type creation is guarded by an existence check.

IF NOT EXISTS (
    SELECT 1 FROM sys.types t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'Audit' AND t.name = 'SchemaTableList'
)
BEGIN
    EXEC('CREATE TYPE [Audit].[SchemaTableList] AS TABLE (
        [Schema] NVARCHAR(256) NOT NULL,
        Tbl      NVARCHAR(256) NOT NULL
    )');
END
GO

-- Persistent table that tracks which dates have been processed.
-- Only created if it does not already exist.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'Audit' AND t.name = 'CleanupLog'
)
BEGIN
    CREATE TABLE [Audit].[CleanupLog] (
        ProcessedDate DATE      NOT NULL PRIMARY KEY,
        CleanedAt     DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

CREATE OR ALTER PROCEDURE [Audit].[usp_CleanupAuditRecords]
    @Filter     [Audit].[SchemaTableList] READONLY,  -- empty = match any DataAudit row
    @RetainDays INT = 30
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @FilterIsEmpty BIT  = CASE WHEN NOT EXISTS (SELECT 1 FROM @Filter) THEN 1 ELSE 0 END;
    DECLARE @CutoffDate    DATE = CAST(DATEADD(DAY, -@RetainDays, GETUTCDATE()) AS DATE);

    DECLARE @LastProcessedDate DATE;

    SELECT @LastProcessedDate = MAX(ProcessedDate)
    FROM   [Audit].[CleanupLog];

    DECLARE @CurrentDate DATE;

    SELECT @CurrentDate = CAST(MIN([DateTime]) AS DATE)
    FROM   [Audit].[AccessLog]
    WHERE  CAST([DateTime] AS DATE) <= @CutoffDate
      AND  CAST([DateTime] AS DATE) > ISNULL(@LastProcessedDate, '0001-01-01');

    IF @CurrentDate IS NULL
    BEGIN
        PRINT 'No unprocessed access log records found before the cutoff date.';
        RETURN;
    END

    DECLARE @NextDate DATE = DATEADD(DAY, 1, @CurrentDate);

    -- Collect this day's access-log rows
    CREATE TABLE #DayAccessLog (
        Id          UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        Req_TraceId NVARCHAR(64)     NULL
    );

    INSERT INTO #DayAccessLog (Id, Req_TraceId)
    SELECT Id, Req_TraceId
    FROM   [Audit].[AccessLog]
    WHERE  [DateTime] >= @CurrentDate
      AND  [DateTime] <  @NextDate;

    -- Collect DataAudit rows linked by TraceId
    CREATE TABLE #DayDataAudit (
        Id       UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        TraceId  NVARCHAR(64)     NULL,
        [Schema] NVARCHAR(256)    NULL,
        Tbl      NVARCHAR(256)    NULL
    );

    INSERT INTO #DayDataAudit (Id, TraceId, [Schema], Tbl)
    SELECT da.Id, da.TraceId, da.[Schema], da.Tbl
    FROM   [Audit].[DataAudit] da
    INNER JOIN #DayAccessLog al ON al.Req_TraceId = da.TraceId
    WHERE  da.TraceId IS NOT NULL;

    -- Classify each access-log row
    CREATE TABLE #Classification (
        AccessLogId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        TraceId     NVARCHAR(64)     NULL,
        HasAudit    BIT              NOT NULL DEFAULT 0,
        HasMatch    BIT              NOT NULL DEFAULT 0
    );

    INSERT INTO #Classification (AccessLogId, TraceId, HasAudit, HasMatch)
    SELECT
        al.Id,
        al.Req_TraceId,
        CASE WHEN EXISTS (
            SELECT 1 FROM #DayDataAudit da WHERE da.TraceId = al.Req_TraceId
        ) THEN 1 ELSE 0 END,
        CASE WHEN EXISTS (
            SELECT 1 FROM #DayDataAudit da
            WHERE  da.TraceId = al.Req_TraceId
              AND  (@FilterIsEmpty = 1 OR EXISTS (
                       SELECT 1 FROM @Filter f
                       WHERE  f.[Schema] = da.[Schema]
                         AND  f.Tbl      = da.Tbl
                   ))
        ) THEN 1 ELSE 0 END
    FROM #DayAccessLog al;

    -- CASE A: no DataAudit found → delete access log only
    DELETE al
    FROM   [Audit].[AccessLog] al
    INNER JOIN #Classification c ON c.AccessLogId = al.Id
    WHERE  c.HasAudit = 0;

    -- CASE B: matched rows exist → delete unmatched DataAudit entries
    DELETE da
    FROM   [Audit].[DataAudit] da
    INNER JOIN #Classification c ON c.TraceId = da.TraceId
    WHERE  c.HasAudit = 1
      AND  c.HasMatch = 1
      AND  @FilterIsEmpty = 0
      AND  NOT EXISTS (
               SELECT 1 FROM @Filter f
               WHERE  f.[Schema] = da.[Schema]
                 AND  f.Tbl      = da.Tbl
           );

    -- CASE C: DataAudit found but none matched → delete both
    DELETE da
    FROM   [Audit].[DataAudit] da
    INNER JOIN #Classification c ON c.TraceId = da.TraceId
    WHERE  c.HasAudit = 1
      AND  c.HasMatch = 0;

    DELETE al
    FROM   [Audit].[AccessLog] al
    INNER JOIN #Classification c ON c.AccessLogId = al.Id
    WHERE  c.HasAudit = 1
      AND  c.HasMatch = 0;

    -- Record the processed date
    INSERT INTO [Audit].[CleanupLog] (ProcessedDate) VALUES (@CurrentDate);

    SELECT @CurrentDate AS ProcessedDate;
END;
GO

/*
Usage examples:

  -- Keep DataAudit rows matching specific schema/table pairs
  DECLARE @f [Audit].[SchemaTableList];
  INSERT INTO @f VALUES ('dbo', 'Orders'), ('dbo', 'OrderItems'), ('sales', 'Invoice');
  EXEC [Audit].[usp_CleanupAuditRecords] @Filter = @f, @RetainDays = 90;

  -- Empty filter = match any DataAudit row (keep-all mode)
  DECLARE @f [Audit].[SchemaTableList];
  EXEC [Audit].[usp_CleanupAuditRecords] @Filter = @f;
*/
