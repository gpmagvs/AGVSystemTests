using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment;
using AGVSystemCommonNet6.Equipment.AGV;
using AGVSystemTests.DATABASE;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VMSystem.BackgroundServices;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// AGVStatsuCollectBackgroundService.SyncVehicleStatusesAsync：只在值真的有變才寫入／入 PG 佇列。
    /// 使用 SQLite in-memory，不需要 SQL Server。
    /// </summary>
    [TestClass]
    public class AGVStatsuCollectBackgroundServiceTests
    {
        private SqliteConnection _connection = null!;
        private DatabaseSyncConfig _originalConfig = null!;
        private PostgreSqlSyncQueue _queue = null!;
        private IMapper _mapper = null!;

        [TestInitialize]
        public void Setup()
        {
            _originalConfig = AGVSConfigulator.DatabaseSyncConfig;
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig { TransactionToPostgreSqlDatabase = true };
            _queue = new PostgreSqlSyncQueue(1000);
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqliteAgvStatusSchema.CreateTableSql;
            cmd.ExecuteNonQuery();
            _mapper = new MapperConfiguration(cfg => cfg.CreateMap<AGVStatus, AGVStatus>()).CreateMapper();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AGVSConfigulator.DatabaseSyncConfig = _originalConfig;
            _connection.Dispose();
        }

        /// <summary>模擬每秒一輪：每輪新建一個 DbContext（與背景服務相同）。</summary>
        private AGVSDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<AGVSDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new PostgreSqlTransactionInterceptor(_queue))
                .Options;
            return new AGVSDbContext(options);
        }

        private static AGVStatus Status(string name, int tag = 1, double x = 1.5, double y = 2.5,
            EquipmentStatusBase.RUN_STATUS status = EquipmentStatusBase.RUN_STATUS.IDLE, double bat = 80)
            => new()
            {
                Name = name,
                Tag = tag,
                Connected = true,
                BatLevel = bat,
                BatDisChargeCurrent = 122000,
                CoordinateX = x,
                CoordinateY = y,
                CurrentPathTag = "1-2-3",
                Mileage = 10,
                Status = status,
            };

        private async Task<int> RunRound(params AGVStatus[] statuses)
        {
            using var db = NewContext();
            return await AGVStatsuCollectBackgroundService.SyncVehicleStatusesAsync(db, statuses, _mapper);
        }

        private void DrainQueue()
        {
            while (_queue.TryDequeue(out _)) { }
        }

        [TestMethod]
        public async Task FirstRound_InsertsNewVehicleRows()
        {
            Assert.AreEqual(2, await RunRound(Status("AGV_1"), Status("AGV_2")));

            using var db = NewContext();
            Assert.AreEqual(2, db.EQStatus_AGV.AsNoTracking().Count());
            Assert.AreEqual(2, _queue.PendingCount, "新增的列照常進佇列");
        }

        [TestMethod]
        public async Task UnchangedValues_ProduceNoUpdate_AndNoQueueTraffic()
        {
            await RunRound(Status("AGV_1"), Status("AGV_2"));
            DrainQueue();

            for (int i = 0; i < 60; i++) // 模擬 60 秒、車輛完全沒動
                Assert.AreEqual(0, await RunRound(Status("AGV_1"), Status("AGV_2")), $"第 {i} 輪不應有任何 UPDATE");

            Assert.AreEqual(0, _queue.PendingCount, "值沒變就不能再進 PostgreSQL 同步佇列（舊行為：每秒每車 1 筆）");
            Assert.AreEqual(0, _queue.CoalescedCount);
        }

        [TestMethod]
        public async Task OnlyChangedVehicle_IsUpdated()
        {
            await RunRound(Status("AGV_1"), Status("AGV_2"));
            DrainQueue();

            Assert.AreEqual(1, await RunRound(Status("AGV_1", tag: 7), Status("AGV_2")));

            Assert.AreEqual(1, _queue.PendingCount);
            Assert.IsTrue(_queue.TryDequeue(out var item));
            Assert.AreEqual("AGV_1", item!.KeyValues.Single());
            Assert.AreEqual(EntityState.Modified, item.State);
        }

        [TestMethod]
        public async Task ChangedValues_ArePersisted_AndFinalRowEqualsLatestStatus()
        {
            await RunRound(Status("AGV_1"));

            var changes = new (string Field, AGVStatus New, Func<AGVStatus, object?> Read, object? Expected)[]
            {
                ("Tag", Status("AGV_1", tag: 9), s => s.Tag, 9),
                ("CoordinateX", Status("AGV_1", tag: 9, x: 99.25), s => s.CoordinateX, 99.25),
                ("CoordinateY", Status("AGV_1", tag: 9, x: 99.25, y: -3.5), s => s.CoordinateY, -3.5),
                ("Status", Status("AGV_1", tag: 9, x: 99.25, y: -3.5, status: EquipmentStatusBase.RUN_STATUS.RUN), s => s.Status, EquipmentStatusBase.RUN_STATUS.RUN),
                ("BatLevel", Status("AGV_1", tag: 9, x: 99.25, y: -3.5, status: EquipmentStatusBase.RUN_STATUS.RUN, bat: 55), s => s.BatLevel, 55d),
            };

            foreach (var c in changes)
            {
                Assert.AreEqual(1, await RunRound(c.New), $"{c.Field} 變動應寫入 1 列");
                using var db = NewContext();
                var row = db.EQStatus_AGV.AsNoTracking().Single(s => s.Name == "AGV_1");
                Assert.AreEqual(c.Expected, c.Read(row), $"{c.Field} 沒有寫進資料庫");
            }

            using var final = NewContext();
            var last = final.EQStatus_AGV.AsNoTracking().Single();
            var expected = changes.Last().New;
            Assert.AreEqual(expected.Tag, last.Tag);
            Assert.AreEqual(expected.Connected, last.Connected);
            Assert.AreEqual(expected.BatLevel, last.BatLevel);
            Assert.AreEqual(expected.BatDisChargeCurrent, last.BatDisChargeCurrent);
            Assert.AreEqual(expected.CoordinateX, last.CoordinateX);
            Assert.AreEqual(expected.CoordinateY, last.CoordinateY);
            Assert.AreEqual(expected.CurrentPathTag, last.CurrentPathTag);
            Assert.AreEqual(expected.Mileage, last.Mileage);
            Assert.AreEqual(expected.Status, last.Status);
        }

        [TestMethod]
        public async Task ChangedRow_QueueSnapshot_ContainsFullLatestRow()
        {
            await RunRound(Status("AGV_1"));
            DrainQueue();

            await RunRound(Status("AGV_1", tag: 42, x: 7.5));

            Assert.IsTrue(_queue.TryDequeue(out var item));
            Assert.AreEqual(42, item!.PropertyValues[nameof(AGVStatus.Tag)]);
            Assert.AreEqual(7.5, item.PropertyValues[nameof(AGVStatus.CoordinateX)]);
            Assert.AreEqual(2.5, item.PropertyValues[nameof(AGVStatus.CoordinateY)], "沒變的欄位也必須在快照內（PG 端以整列覆寫）");
            Assert.AreEqual(80d, item.PropertyValues[nameof(AGVStatus.BatLevel)]);
        }
    }
}
