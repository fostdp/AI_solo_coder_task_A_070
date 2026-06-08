USE master;
GO

IF EXISTS (SELECT name FROM sys.databases WHERE name = N'AluminaDetectionDB')
BEGIN
    ALTER DATABASE AluminaDetectionDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE AluminaDetectionDB;
END
GO

CREATE DATABASE AluminaDetectionDB;
GO

USE AluminaDetectionDB;
GO

CREATE TABLE PotInfo (
    PotId       INT NOT NULL PRIMARY KEY,
    PotCode     NVARCHAR(20) NOT NULL,
    RowIndex    INT NOT NULL,
    ColIndex    INT NOT NULL,
    Status      INT NOT NULL DEFAULT 1,
    CreatedAt   DATETIME NOT NULL DEFAULT GETDATE()
);
GO

CREATE TABLE PotRealtimeData (
    Id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    PotId                   INT NOT NULL FOREIGN KEY REFERENCES PotInfo(PotId),
    Voltage                 FLOAT,
    AnodeCurrentDistribution NVARCHAR(500),
    PotTemperature          FLOAT,
    BathTemperature         FLOAT,
    AluminumLevel           FLOAT,
    BathLevel               FLOAT,
    AluminaConcentration    FLOAT,
    EstimatedConcentration  FLOAT,
    AnodeEffectProbability  FLOAT,
    RecordedAt              DATETIME NOT NULL DEFAULT GETDATE()
);
GO

CREATE TABLE FeedingRecord (
    Id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    PotId                   INT NOT NULL FOREIGN KEY REFERENCES PotInfo(PotId),
    FeedAmount              FLOAT NOT NULL,
    FeedType                NVARCHAR(50) NOT NULL,
    FeedTime                DATETIME NOT NULL DEFAULT GETDATE(),
    Operator                NVARCHAR(50),
    EstimatedConcentration  FLOAT NULL,
    Status                  NVARCHAR(20) NOT NULL DEFAULT 'Pending'
);
GO

CREATE TABLE AlarmRecord (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    PotId       INT NOT NULL FOREIGN KEY REFERENCES PotInfo(PotId),
    AlarmType   NVARCHAR(50) NOT NULL,
    AlarmLevel  INT NOT NULL,
    Message     NVARCHAR(500),
    IsHandled   BIT NOT NULL DEFAULT 0,
    CreatedAt   DATETIME NOT NULL DEFAULT GETDATE(),
    HandledAt   DATETIME NULL,
    HandledBy   NVARCHAR(50) NULL
);
GO

CREATE TABLE ConcentrationHistory (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    PotId       INT NOT NULL FOREIGN KEY REFERENCES PotInfo(PotId),
    Concentration FLOAT NOT NULL,
    Source      NVARCHAR(20) NOT NULL,
    RecordedAt  DATETIME NOT NULL DEFAULT GETDATE()
);
GO

CREATE TABLE VoltageFeature (
    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    PotId           INT NOT NULL FOREIGN KEY REFERENCES PotInfo(PotId),
    MeanVoltage     FLOAT,
    StdVoltage      FLOAT,
    Skewness        FLOAT,
    Kurtosis        FLOAT,
    FrequencyPeak   FLOAT,
    NoisePower      FLOAT,
    WindowStart     DATETIME NOT NULL,
    WindowEnd       DATETIME NOT NULL,
    SampleCount     INT NOT NULL DEFAULT 0,
    ExtractedAt     DATETIME NOT NULL DEFAULT GETDATE()
);
GO

CREATE NONCLUSTERED INDEX IX_PotRealtimeData_PotId_RecordedAt
    ON PotRealtimeData(PotId, RecordedAt);
GO

CREATE NONCLUSTERED INDEX IX_FeedingRecord_PotId_FeedTime
    ON FeedingRecord(PotId, FeedTime);
GO

CREATE NONCLUSTERED INDEX IX_AlarmRecord_PotId_CreatedAt
    ON AlarmRecord(PotId, CreatedAt);
GO

CREATE NONCLUSTERED INDEX IX_ConcentrationHistory_PotId_RecordedAt
    ON ConcentrationHistory(PotId, RecordedAt);
GO

CREATE PROCEDURE sp_InitPotData
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM PotInfo;

    DECLARE @i INT = 1;
    DECLARE @rowIdx INT;
    DECLARE @colIdx INT;
    DECLARE @potCode NVARCHAR(20);

    WHILE @i <= 200
    BEGIN
        SET @rowIdx = ((@i - 1) / 20) + 1;
        SET @colIdx = ((@i - 1) % 20) + 1;
        SET @potCode = N'P-' + RIGHT(N'000' + CAST(@i AS NVARCHAR(3)), 3);

        INSERT INTO PotInfo (PotId, PotCode, RowIndex, ColIndex, Status, CreatedAt)
        VALUES (@i, @potCode, @rowIdx, @colIdx, 1, GETDATE());

        SET @i = @i + 1;
    END
END
GO

CREATE PROCEDURE sp_GetPotTrend
    @PotId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Voltage,
        AnodeCurrentDistribution,
        RecordedAt
    FROM PotRealtimeData
    WHERE PotId = @PotId
      AND RecordedAt >= DATEADD(HOUR, -8, GETDATE())
    ORDER BY RecordedAt ASC;
END
GO

CREATE PROCEDURE sp_GetRecentFeedings
    @PotId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 10
        Id,
        PotId,
        FeedAmount,
        FeedType,
        FeedTime,
        Operator
    FROM FeedingRecord
    WHERE PotId = @PotId
    ORDER BY FeedTime DESC;
END
GO

CREATE PROCEDURE sp_CleanupOldData
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Cutoff30 DATETIME = DATEADD(DAY, -30, GETDATE());
    DECLARE @Cutoff90 DATETIME = DATEADD(DAY, -90, GETDATE());

    DELETE FROM PotRealtimeData
    WHERE RecordedAt < @Cutoff30;

    DELETE FROM ConcentrationHistory
    WHERE RecordedAt < @Cutoff90;
END
GO
