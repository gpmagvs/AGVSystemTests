namespace AGVSystemTests.DATABASE
{
    /// <summary>以 SQLite 建立 EQStatus_AGV 資料表（只建這張表；不依賴 SQL Server）。</summary>
    internal static class SqliteAgvStatusSchema
    {
        public const string CreateTableSql = @"
CREATE TABLE EQStatus_AGV (
    Name TEXT NOT NULL PRIMARY KEY,
    Description TEXT NOT NULL,
    Connected INTEGER NOT NULL,
    Tag INTEGER NOT NULL,
    Status INTEGER NOT NULL,
    BatLevel REAL NOT NULL,
    BatVoltage REAL NOT NULL,
    BatChargeCurrent REAL NOT NULL,
    BatDisChargeCurrent REAL NOT NULL,
    MotorRightVoltage REAL NOT NULL,
    MotorRightCurrent REAL NOT NULL,
    MotorRightTemperature REAL NOT NULL,
    MotorRightAlarmCode INTEGER NOT NULL,
    MotorLeftVoltage REAL NOT NULL,
    MotorLeftCurrent REAL NOT NULL,
    MotorLeftTemperature REAL NOT NULL,
    MotorLeftAlarmCode INTEGER NOT NULL,
    CoordinateX REAL NOT NULL,
    CoordinateY REAL NOT NULL,
    CurrentPathTag TEXT NOT NULL,
    Mileage REAL NOT NULL
);";
    }
}
