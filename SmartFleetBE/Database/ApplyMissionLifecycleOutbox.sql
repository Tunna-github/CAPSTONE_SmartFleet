-- Run in the EXISTING SmartFleet database before starting the updated backend.
-- Adds one queue table; never deletes/resets robot, task, mission or history data.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.TaskAssignments', N'U') IS NULL
    THROW 51000, 'Select the existing SmartFleet database with TaskAssignments first.', 1;

IF OBJECT_ID(N'dbo.RobotCommandOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RobotCommandOutbox (
        Id bigint IDENTITY(1,1) NOT NULL,
        AssignmentId bigint NOT NULL,
        Topic nvarchar(300) NOT NULL,
        Payload nvarchar(4000) NOT NULL,
        CreatedAt datetime2 NOT NULL,
        DeliveredAt datetime2 NULL,
        CONSTRAINT PK_RobotCommandOutbox PRIMARY KEY (Id),
        CONSTRAINT FK_RobotCommandOutbox_TaskAssignments_AssignmentId
            FOREIGN KEY (AssignmentId) REFERENCES dbo.TaskAssignments(AssignmentID)
    );
    CREATE UNIQUE INDEX IX_RobotCommandOutbox_AssignmentId
        ON dbo.RobotCommandOutbox(AssignmentId);
    CREATE INDEX IX_RobotCommandOutbox_DeliveredAt
        ON dbo.RobotCommandOutbox(DeliveredAt);
END;

-- If this DB uses EF migrations, keep its history synchronized with this update.
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
        IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N''20261002034136_MissionLifecycleOutbox'')
            INSERT INTO dbo.__EFMigrationsHistory(MigrationId, ProductVersion)
            VALUES (N''20261002034136_MissionLifecycleOutbox'', N''10.0.12'');';
END;

COMMIT TRANSACTION;
