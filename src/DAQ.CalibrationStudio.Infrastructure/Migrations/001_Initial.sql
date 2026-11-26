CREATE TABLE IF NOT EXISTS SchemaMigrations (
    Version INTEGER PRIMARY KEY,
    AppliedUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Devices (
    DeviceKey TEXT PRIMARY KEY,
    Model TEXT NOT NULL,
    Serial TEXT,
    Firmware TEXT,
    Revision TEXT,
    LastSeenUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Profiles (
    Id TEXT NOT NULL,
    Version INTEGER NOT NULL CHECK (Version > 0),
    Name TEXT NOT NULL,
    Kind INTEGER NOT NULL,
    SnapshotJson TEXT NOT NULL,
    SavedUtc TEXT NOT NULL,
    PRIMARY KEY (Id, Version)
);

CREATE TABLE IF NOT EXISTS Runs (
    Id TEXT PRIMARY KEY,
    StartedUtc TEXT NOT NULL,
    FinishedUtc TEXT NOT NULL,
    ProfileId TEXT NOT NULL,
    ProfileVersion INTEGER NOT NULL,
    ProfileName TEXT NOT NULL,
    Kind INTEGER NOT NULL,
    Outcome INTEGER NOT NULL,
    DeviceModel TEXT NOT NULL,
    DeviceSerial TEXT,
    HardwareRevision TEXT,
    FirmwareVersion TEXT,
    Operator TEXT NOT NULL,
    Simulated INTEGER NOT NULL CHECK (Simulated IN (0,1)),
    SnapshotJson TEXT NOT NULL,
    Sha256 TEXT NOT NULL,
    FOREIGN KEY (ProfileId, ProfileVersion) REFERENCES Profiles(Id, Version)
);

CREATE INDEX IF NOT EXISTS IX_Runs_StartedUtc ON Runs(StartedUtc DESC);
CREATE INDEX IF NOT EXISTS IX_Runs_DeviceSerial ON Runs(DeviceSerial);
CREATE INDEX IF NOT EXISTS IX_Runs_ProfileName ON Runs(ProfileName);
CREATE INDEX IF NOT EXISTS IX_Runs_Outcome ON Runs(Outcome);

CREATE TABLE IF NOT EXISTS Approvals (
    Id TEXT PRIMARY KEY,
    RunId TEXT NOT NULL,
    TimeUtc TEXT NOT NULL,
    Operator TEXT NOT NULL,
    Target TEXT NOT NULL,
    Verified INTEGER NOT NULL CHECK (Verified IN (0,1)),
    SnapshotJson TEXT NOT NULL,
    FOREIGN KEY (RunId) REFERENCES Runs(Id)
);
CREATE INDEX IF NOT EXISTS IX_Approvals_RunId ON Approvals(RunId, TimeUtc);

CREATE TABLE IF NOT EXISTS Settings (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    SnapshotJson TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE TRIGGER IF NOT EXISTS TR_Runs_NoUpdate BEFORE UPDATE ON Runs
BEGIN SELECT RAISE(ABORT, 'Prüfläufe sind unveränderlich.'); END;
CREATE TRIGGER IF NOT EXISTS TR_Runs_NoDelete BEFORE DELETE ON Runs
BEGIN SELECT RAISE(ABORT, 'Prüfläufe sind unveränderlich.'); END;
CREATE TRIGGER IF NOT EXISTS TR_Approvals_NoUpdate BEFORE UPDATE ON Approvals
BEGIN SELECT RAISE(ABORT, 'Freigaben sind unveränderlich.'); END;
CREATE TRIGGER IF NOT EXISTS TR_Approvals_NoDelete BEFORE DELETE ON Approvals
BEGIN SELECT RAISE(ABORT, 'Freigaben sind unveränderlich.'); END;
