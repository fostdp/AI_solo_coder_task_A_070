USE AluminaDetectionDB;
GO

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ApplicationLogs]') AND type in (N'U'))
BEGIN
    CREATE TABLE ApplicationLogs (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        Message         NVARCHAR(MAX),
        MessageTemplate NVARCHAR(MAX),
        Level           NVARCHAR(128),
        TimeStamp       DATETIMEOFFSET,
        Exception       NVARCHAR(MAX),
        LogEvent        NVARCHAR(MAX),
        Properties      NVARCHAR(MAX)
    );

    CREATE NONCLUSTERED INDEX IX_ApplicationLogs_Level_TimeStamp
        ON ApplicationLogs(Level, TimeStamp);
END
GO

IF OBJECT_ID(N'tbl_IndexMaintenanceLog', N'U') IS NULL
BEGIN
    CREATE TABLE tbl_IndexMaintenanceLog (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        TableName       NVARCHAR(128) NOT NULL,
        IndexName       NVARCHAR(128) NOT NULL,
        FragmentationPct FLOAT,
        PageCount       BIGINT,
        Operation       NVARCHAR(50),
        ExecutedAt      DATETIME NOT NULL DEFAULT GETDATE(),
        DurationMs      INT
    );
END
GO

CREATE OR ALTER PROCEDURE sp_RebuildFragmentedIndexes
    @FragmentationThreshold FLOAT = 30.0,
    @MinPageCount INT = 1000
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SQL NVARCHAR(MAX);
    DECLARE @TableName NVARCHAR(128);
    DECLARE @IndexName NVARCHAR(128);
    DECLARE @FragPct FLOAT;
    DECLARE @PageCount BIGINT;
    DECLARE @StartTime DATETIME2;

    DECLARE idx_cursor CURSOR FOR
        SELECT
            OBJECT_NAME(ind.object_id) AS TableName,
            ind.name AS IndexName,
            stat.avg_fragmentation_in_percent,
            stat.page_count
        FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') stat
        INNER JOIN sys.indexes ind ON stat.object_id = ind.object_id AND stat.index_id = ind.index_id
        WHERE stat.avg_fragmentation_in_percent > @FragmentationThreshold
          AND stat.page_count > @MinPageCount
          AND ind.name IS NOT NULL
          AND OBJECT_NAME(ind.object_id) NOT LIKE 'sys%'
          AND OBJECT_NAME(ind.object_id) NOT LIKE 'tbl_IndexMaintenanceLog'
        ORDER BY stat.avg_fragmentation_in_percent DESC;

    OPEN idx_cursor;
    FETCH NEXT FROM idx_cursor INTO @TableName, @IndexName, @FragPct, @PageCount;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @StartTime = SYSDATETIME();

        IF @FragPct > 60.0
        BEGIN
            SET @SQL = N'ALTER INDEX [' + @IndexName + N'] ON [dbo].[' + @TableName + N'] REBUILD WITH (ONLINE = ON, SORT_IN_TEMPDB = ON)';
            EXEC sp_executesql @SQL;
        END
        ELSE
        BEGIN
            SET @SQL = N'ALTER INDEX [' + @IndexName + N'] ON [dbo].[' + @TableName + N'] REORGANIZE';
            EXEC sp_executesql @SQL;
        END

        INSERT INTO tbl_IndexMaintenanceLog (TableName, IndexName, FragmentationPct, PageCount, Operation, DurationMs)
        VALUES (@TableName, @IndexName, @FragPct, @PageCount,
                CASE WHEN @FragPct > 60.0 THEN 'REBUILD' ELSE 'REORGANIZE' END,
                DATEDIFF(MILLISECOND, @StartTime, SYSDATETIME()));

        FETCH NEXT FROM idx_cursor INTO @TableName, @IndexName, @FragPct, @PageCount;
    END

    CLOSE idx_cursor;
    DEALLOCATE idx_cursor;
END
GO

CREATE OR ALTER PROCEDURE sp_UpdateStatistics
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SQL NVARCHAR(MAX);
    DECLARE @TableName NVARCHAR(128);

    DECLARE tbl_cursor CURSOR FOR
        SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
        WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = 'dbo';

    OPEN tbl_cursor;
    FETCH NEXT FROM tbl_cursor INTO @TableName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @SQL = N'UPDATE STATISTICS [dbo].[' + @TableName + N'] WITH FULLSCAN';
        EXEC sp_executesql @SQL;
        FETCH NEXT FROM tbl_cursor INTO @TableName;
    END

    CLOSE tbl_cursor;
    DEALLOCATE tbl_cursor;
END
GO

CREATE OR ALTER PROCEDURE sp_BackupDatabase
    @BackupPath NVARCHAR(256) = N'/var/opt/mssql/backup/'
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @FileName NVARCHAR(512);
    DECLARE @Timestamp NVARCHAR(20);
    DECLARE @SQL NVARCHAR(MAX);

    SET @Timestamp = FORMAT(GETDATE(), 'yyyyMMdd_HHmmss');
    SET @FileName = @BackupPath + N'AluminaDetectionDB_' + @Timestamp + N'.bak';

    SET @SQL = N'BACKUP DATABASE [AluminaDetectionDB] TO DISK = ''' + @FileName + N''' '
             + N'WITH FORMAT, MEDIANAME = ''AluminaBackup'', NAME = ''Full Backup'', '
             + N'COMPRESSION, STATS = 10, CHECKSUM';

    EXEC sp_executesql @SQL;

    PRINT N'Backup completed: ' + @FileName;
END
GO

CREATE OR ALTER PROCEDURE sp_CleanupOldBackups
    @BackupPath NVARCHAR(256) = N'/var/opt/mssql/backup/',
    @RetentionDays INT = 7
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Cmd NVARCHAR(4000);
    SET @Cmd = N'find ' + @BackupPath + N' -name "AluminaDetectionDB_*.bak" -mtime +' + CAST(@RetentionDays AS NVARCHAR(10)) + N' -delete';

    BEGIN TRY
        EXEC xp_cmdshell @Cmd, no_output;
        PRINT N'Old backups cleanup completed (retention: ' + CAST(@RetentionDays AS NVARCHAR(10)) + N' days)';
    END TRY
    BEGIN CATCH
        PRINT N'Backup cleanup skipped (xp_cmdshell may be disabled)';
    END CATCH
END
GO

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Alumina_IndexMaintenance')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Alumina_IndexMaintenance';

BEGIN TRANSACTION;
DECLARE @jobId BINARY(16);
EXEC msdb.dbo.sp_add_job @job_name=N'Alumina_IndexMaintenance',
    @enabled=1,
    @description=N'Weekly index rebuild and statistics update',
    @job_id = @jobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'Rebuild Fragmented Indexes',
    @subsystem=N'TSQL',
    @command=N'EXEC sp_RebuildFragmentedIndexes @FragmentationThreshold = 30.0, @MinPageCount = 1000',
    @database_name=N'AluminaDetectionDB';

EXEC msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'Update Statistics',
    @subsystem=N'TSQL',
    @command=N'EXEC sp_UpdateStatistics',
    @database_name=N'AluminaDetectionDB',
    @on_success_action=1;

EXEC msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'WeeklySunday2AM',
    @freq_type=8,
    @freq_interval=1,
    @active_start_time=20000;

EXEC msdb.dbo.sp_add_jobserver @job_id=@jobId;
COMMIT TRANSACTION;
GO

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Alumina_DailyBackup')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Alumina_DailyBackup';

BEGIN TRANSACTION;
DECLARE @jobId2 BINARY(16);
EXEC msdb.dbo.sp_add_job @job_name=N'Alumina_DailyBackup',
    @enabled=1,
    @description=N'Daily full database backup with compression',
    @job_id = @jobId2 OUTPUT;

EXEC msdb.dbo.sp_add_jobstep @job_id=@jobId2, @step_name=N'Full Backup',
    @subsystem=N'TSQL',
    @command=N'EXEC sp_BackupDatabase @BackupPath = N''/var/opt/mssql/backup/''',
    @database_name=N'AluminaDetectionDB';

EXEC msdb.dbo.sp_add_jobstep @job_id=@jobId2, @step_name=N'Cleanup Old Backups',
    @subsystem=N'TSQL',
    @command=N'EXEC sp_CleanupOldBackups @BackupPath = N''/var/opt/mssql/backup/'', @RetentionDays = 7',
    @database_name=N'AluminaDetectionDB',
    @on_success_action=1;

EXEC msdb.dbo.sp_add_jobschedule @job_id=@jobId2, @name=N'Daily3AM',
    @freq_type=4,
    @freq_interval=1,
    @active_start_time=30000;

EXEC msdb.dbo.sp_add_jobserver @job_id=@jobId2;
COMMIT TRANSACTION;
GO

PRINT N'Maintenance procedures and SQL Agent jobs created successfully.';
PRINT N'  - sp_RebuildFragmentedIndexes: Rebuild/reorganize fragmented indexes';
PRINT N'  - sp_UpdateStatistics: Update all table statistics with fullscan';
PRINT N'  - sp_BackupDatabase: Full backup with compression';
PRINT N'  - sp_CleanupOldBackups: Remove backups older than retention days';
PRINT N'  - Alumina_IndexMaintenance job: Weekly Sunday 2:00 AM';
PRINT N'  - Alumina_DailyBackup job: Daily 3:00 AM';
GO
