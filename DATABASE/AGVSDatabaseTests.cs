using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment;
using AGVSystemCommonNet6.Equipment.AGV;
using AGVSystemCommonNet6.Material;
using AGVSystemCommonNet6.Sys;
using AGVSystemCommonNet6.User;
using AGVSystemTests.DATABASE;
using Microsoft.EntityFrameworkCore;

namespace AGVSystemCommonNet6.DATABASE.Tests
{
    /// <summary>
    /// AGVSDatabase 整合／單元測試：臨時庫建連 → Initialize → 驗證 schema／PK／索引／CRUD → 刪庫。
    /// </summary>
    [TestClass]
    public class AGVSDatabaseTests
    {
        private static SqlServerTestDatabase? _testDb;
        private static string? _originalConnection;
        private static SystemConfigs? _originalConfigs;

        /// <summary>Initialize 後應存在的核心表（對齊 AGVSDbContext DbSet）。</summary>
        private static readonly string[] ExpectedCoreTables =
        {
            "SysStatus",
            "Users",
            "Tasks",
            "AgvStates",
            "SystemAlarms",
            "StationStatus",
            "EQStatus_AGV",
            "EQStatus_MainEQ",
            "EQStatus_Rack",
            "EQStatus_ChargeStation",
            "DeepChargeRecords",
            "EQStatusLogs",
            "AGVSChangeLog",
            "VMSChangeLog",
            "MRMP_Missions"
        };

        /// <summary>表名 → 預期 PK 欄位（GetPrimaryKeyColumnName 對照）。</summary>
        private static readonly Dictionary<string, string> ExpectedPrimaryKeys = new()
        {
            ["SysStatus"] = "FieldName",
            ["Users"] = "UserName",
            ["Tasks"] = "TaskName",
            ["AgvStates"] = "AGV_Name",
            ["SystemAlarms"] = "Time",
            ["StationStatus"] = "StationName",
            ["EQStatus_AGV"] = "Name",
            ["EQStatus_MainEQ"] = "Name",
            ["EQStatus_Rack"] = "Name",
            ["EQStatus_ChargeStation"] = "Name",
            ["DeepChargeRecords"] = "OrderRecievedTime",
            ["SecsLog"] = "LogTime",
        };

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            _originalConfigs = AGVSConfigulator.SysConfigs;
            _originalConnection = AGVSConfigulator.SysConfigs?.DBConnection;

            _testDb = new SqlServerTestDatabase();
            AGVSConfigulator.SysConfigs ??= new SystemConfigs();
            AGVSConfigulator.SysConfigs.DBConnection = _testDb.ConnectionString;

            await AGVSDatabase.Initialize();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            try
            {
                if (_originalConfigs != null)
                    AGVSConfigulator.SysConfigs = _originalConfigs;
                if (_originalConnection != null && AGVSConfigulator.SysConfigs != null)
                    AGVSConfigulator.SysConfigs.DBConnection = _originalConnection;
            }
            finally
            {
                _testDb?.Dispose();
                _testDb = null;
            }
        }

        [TestMethod]
        public void Connection_TestDatabase_IsOnline()
        {
            Assert.IsNotNull(_testDb);
            Assert.IsTrue(_testDb!.DatabaseExists());
            Assert.IsTrue(_testDb.CanConnect());
        }

        [TestMethod]
        public void GetPrimaryKeyColumnName_AllMappedTables_MatchDictionary()
        {
            foreach (KeyValuePair<string, string> pair in ExpectedPrimaryKeys)
            {
                Assert.AreEqual(pair.Value, AGVSDatabase.GetPrimaryKeyColumnName(pair.Key),
                    $"PK mapping mismatch for {pair.Key}");
            }

            Assert.IsTrue(string.IsNullOrEmpty(AGVSDatabase.GetPrimaryKeyColumnName("UnknownTable_XYZ")));
        }

        [TestMethod]
        public void Initialize_CreatesAllExpectedCoreTables()
        {
            Assert.IsNotNull(_testDb);
            List<string> tables = _testDb!.ListUserTables();
            Assert.IsTrue(tables.Count >= ExpectedCoreTables.Length,
                $"Expected at least {ExpectedCoreTables.Length} tables, got {tables.Count}: {string.Join(",", tables)}");

            foreach (string table in ExpectedCoreTables)
            {
                Assert.IsTrue(_testDb.TableExists(table), $"Missing table: {table}");
            }
        }

        [TestMethod]
        public void Initialize_CoreTables_HavePrimaryKeys()
        {
            Assert.IsNotNull(_testDb);
            var mustHavePk = new[]
            {
                "SysStatus", "Users", "Tasks", "AgvStates", "StationStatus",
                "EQStatus_AGV", "EQStatus_MainEQ", "EQStatus_Rack", "EQStatus_ChargeStation"
            };

            foreach (string table in mustHavePk)
            {
                Assert.IsTrue(_testDb!.HasPrimaryKey(table), $"{table} should have PK");
                string? pkColumn = _testDb.GetPrimaryKeyColumn(table);
                Assert.AreEqual(ExpectedPrimaryKeys[table], pkColumn, $"{table} PK column mismatch");
            }
        }

        [TestMethod]
        public void CheckPrimaryKeys_CoreTables_NotInMissingList()
        {
            using AGVSDatabase database = new AGVSDatabase();
            AGVSDatabase.CheckPrimaryKeys(database, out List<string> missing);

            string[] required =
            {
                "SysStatus", "Users", "Tasks", "AgvStates",
                "EQStatus_AGV", "EQStatus_MainEQ", "EQStatus_Rack", "EQStatus_ChargeStation"
            };

            foreach (string table in required)
            {
                Assert.IsFalse(missing.Contains(table),
                    $"{table} should not be missing PK. Missing: {string.Join(",", missing)}");
            }
        }

        [TestMethod]
        public void Initialize_CreatesExpectedIndexes()
        {
            Assert.IsNotNull(_testDb);
            Assert.IsTrue(_testDb!.IndexExists("IX_Tasks_State"));
            Assert.IsTrue(_testDb.IndexExists("IX_Tasks_State_RecieveTime"));
            Assert.IsTrue(_testDb.IndexExists("IX_TaskTrajecotroyStores_TaskName"));
            Assert.IsTrue(_testDb.IndexExists("IX_TaskTrajecotroyStores_AGVName"));
            Assert.IsTrue(_testDb.IndexExists("IX_TaskTrajecotroyStores_AGVName_TaskName"));
            Assert.IsTrue(_testDb.IndexExists("IX_SystemAlarms_Checked_Time"));
        }

        [TestMethod]
        public void RestorePrimaryKeys_Idempotent_ForMultipleTables()
        {
            Assert.IsNotNull(_testDb);
            using AGVSDatabase database = new AGVSDatabase();
            var tables = new List<string>
            {
                "EQStatus_AGV", "EQStatus_MainEQ", "EQStatus_Rack", "EQStatus_ChargeStation", "Users"
            };
            AGVSDatabase.RestorePrimaryKeys(database, tables);

            foreach (string table in tables)
                Assert.IsTrue(_testDb!.HasPrimaryKey(table), $"{table} PK should remain after RestorePrimaryKeys");
        }

        [TestMethod]
        public async Task Crud_SysStatus_RoundTrip()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string field = $"UT_Field_{Guid.NewGuid():N}"[..20];
            database.tables.SysStatus.Add(new AGVSSystemStatus
            {
                FieldName = field,
                Version = "9.9.9",
                CurrentMapVersion = "map-ut"
            });
            await database.SaveChanges();

            AGVSSystemStatus? loaded = await database.tables.SysStatus.AsNoTracking()
                .FirstOrDefaultAsync(x => x.FieldName == field);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("9.9.9", loaded!.Version);
            Assert.AreEqual("map-ut", loaded.CurrentMapVersion);
        }

        [TestMethod]
        public async Task Crud_Users_RoundTrip()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string userName = $"ut_user_{Guid.NewGuid():N}"[..16];
            database.tables.Users.Add(new UserEntity
            {
                UserName = userName,
                Password = "hash",
                Role = ERole.Operator
            });
            await database.SaveChanges();

            UserEntity? loaded = await database.tables.Users.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserName == userName);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("hash", loaded!.Password);
            Assert.AreEqual(ERole.Operator, loaded.Role);
        }

        [TestMethod]
        public async Task Crud_AgvStates_RoundTrip()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string agvName = $"UT_AGV_{Guid.NewGuid():N}"[..16];
            database.tables.AgvStates.Add(new clsAGVStateDto
            {
                AGV_Name = agvName,
                AGV_ID = "ID01",
                Enabled = true,
                Connected = true,
                BatteryLevel_1 = 88.5
            });
            await database.SaveChanges();

            clsAGVStateDto? loaded = await database.tables.AgvStates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AGV_Name == agvName);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("ID01", loaded!.AGV_ID);
            Assert.IsTrue(loaded.Enabled);
            Assert.AreEqual(88.5, loaded.BatteryLevel_1, 0.001);
        }

        [TestMethod]
        public async Task Crud_Tasks_RoundTrip()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string taskName = $"UT_TASK_{Guid.NewGuid():N}"[..20];
            database.tables.Tasks.Add(new clsTaskDto
            {
                TaskName = taskName,
                DesignatedAGVName = "AGV01",
                RecieveTime = DateTime.Now,
                Priority = 3
            });
            await database.SaveChanges();

            clsTaskDto? loaded = await database.tables.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TaskName == taskName);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("AGV01", loaded!.DesignatedAGVName);
            Assert.AreEqual(3, loaded.Priority);
        }

        [TestMethod]
        public async Task Crud_StationStatus_RoundTrip()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string station = $"UT_ST_{Guid.NewGuid():N}"[..16];
            database.tables.StationStatus.Add(new clsStationStatus
            {
                StationName = station,
                StationTag = "100",
                MaterialID = "MAT-1"
            });
            await database.SaveChanges();

            clsStationStatus? loaded = await database.tables.StationStatus.AsNoTracking()
                .FirstOrDefaultAsync(x => x.StationName == station);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("100", loaded!.StationTag);
            Assert.AreEqual("MAT-1", loaded.MaterialID);
        }

        [TestMethod]
        public async Task Crud_EQStatus_Family_RoundTrip()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string suffix = Guid.NewGuid().ToString("N")[..8];

            database.tables.EQStatus_AGV.Add(new AGVStatus
            {
                Name = $"AGV_{suffix}",
                Connected = true,
                BatLevel = 70,
                Status = EquipmentStatusBase.RUN_STATUS.RUN
            });
            database.tables.EQStatus_MainEQ.Add(new MainEQStatus
            {
                Name = $"EQ_{suffix}",
                Connected = true,
                LoadRequest = true,
                Status = EquipmentStatusBase.RUN_STATUS.IDLE
            });
            database.tables.EQStatus_Rack.Add(new RackStatus
            {
                Name = $"RACK_{suffix}",
                Connected = false,
                Status = EquipmentStatusBase.RUN_STATUS.DOWN
            });
            database.tables.EQStatus_ChargeStation.Add(new ChargStationStatus
            {
                Name = $"CHG_{suffix}",
                Connected = true,
                VoltageIn = 220,
                Status = EquipmentStatusBase.RUN_STATUS.IDLE,
                UpdateTime = DateTime.Now
            });
            await database.SaveChanges();

            Assert.IsNotNull(await database.tables.EQStatus_AGV.AsNoTracking().FirstOrDefaultAsync(x => x.Name == $"AGV_{suffix}"));
            Assert.IsNotNull(await database.tables.EQStatus_MainEQ.AsNoTracking().FirstOrDefaultAsync(x => x.Name == $"EQ_{suffix}"));
            Assert.IsNotNull(await database.tables.EQStatus_Rack.AsNoTracking().FirstOrDefaultAsync(x => x.Name == $"RACK_{suffix}"));
            Assert.IsNotNull(await database.tables.EQStatus_ChargeStation.AsNoTracking().FirstOrDefaultAsync(x => x.Name == $"CHG_{suffix}"));
        }

        [TestMethod]
        public async Task SaveChanges_UpdateExisting_UserEntity()
        {
            using AGVSDatabase database = new AGVSDatabase();
            string userName = $"ut_upd_{Guid.NewGuid():N}"[..16];
            database.tables.Users.Add(new UserEntity { UserName = userName, Password = "v1", Role = ERole.VISITOR });
            await database.SaveChanges();

            UserEntity existing = await database.tables.Users.FirstAsync(x => x.UserName == userName);
            existing.Password = "v2";
            existing.Role = ERole.Engineer;
            await database.SaveChanges();

            UserEntity? loaded = await database.tables.Users.AsNoTracking().FirstOrDefaultAsync(x => x.UserName == userName);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("v2", loaded!.Password);
            Assert.AreEqual(ERole.Engineer, loaded.Role);
        }

        [TestMethod]
        public async Task Initialize_IsIdempotent_WhenCalledAgain()
        {
            Assert.IsNotNull(_testDb);
            int tableCountBefore = _testDb!.ListUserTables().Count;
            await AGVSDatabase.Initialize();
            int tableCountAfter = _testDb.ListUserTables().Count;
            Assert.AreEqual(tableCountBefore, tableCountAfter);
            Assert.IsTrue(_testDb.CanConnect());
        }

        [TestMethod]
        public void SqlServerTestDatabase_Lifecycle_CreateThenDrop()
        {
            string name;
            using (var ephemeral = new SqlServerTestDatabase())
            {
                name = ephemeral.DatabaseName;
                Assert.IsTrue(ephemeral.DatabaseExists());
                Assert.IsTrue(ephemeral.CanConnect());
            }

            using var connection = new Microsoft.Data.SqlClient.SqlConnection(SqlServerTestDatabase.BuildConnectionString("master"));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM sys.databases WHERE name = @name";
            command.Parameters.AddWithValue("@name", name);
            Assert.AreEqual(0, Convert.ToInt32(command.ExecuteScalar()), $"Database {name} should be dropped after Dispose");
        }
    }
}
