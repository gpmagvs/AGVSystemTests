using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Equipment.AGV;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// PostgreSqlTransactionInterceptor 與 DatabaseSyncConfig.TransactionToPostgreSqlDatabase：
    /// 預設 false → 完全不入列（行為與改動前相同）；true → 入列且同列合併。使用 SQLite in-memory，不需要 SQL Server。
    /// </summary>
    [TestClass]
    public class PostgreSqlSyncInterceptorFlagTests
    {
        private SqliteConnection _connection = null!;
        private DatabaseSyncConfig _originalConfig = null!;

        [TestInitialize]
        public void Setup()
        {
            _originalConfig = AGVSConfigulator.DatabaseSyncConfig;
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SqliteAgvStatusSchema.CreateTableSql;
            cmd.ExecuteNonQuery();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AGVSConfigulator.DatabaseSyncConfig = _originalConfig;
            _connection.Dispose();
        }

        private AGVSDbContext NewContext(PostgreSqlSyncQueue queue)
        {
            var options = new DbContextOptionsBuilder<AGVSDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new PostgreSqlTransactionInterceptor(queue))
                .Options;
            return new AGVSDbContext(options);
        }

        [TestMethod]
        public void DatabaseSyncConfig_DefaultsToFalse()
        {
            Assert.IsFalse(new DatabaseSyncConfig().TransactionToPostgreSqlDatabase, "預設必須是 false");
        }

        [TestMethod]
        public void FlagFalse_SaveChanges_EnqueuesNothing()
        {
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig(); // 預設 false
            var queue = new PostgreSqlSyncQueue();
            using var db = NewContext(queue);

            db.EQStatus_AGV.Add(new AGVStatus { Name = "AGV_1" });
            db.SaveChanges();
            for (int i = 0; i < 100; i++)
            {
                var row = db.EQStatus_AGV.Single(s => s.Name == "AGV_1");
                row.Tag = i + 1;
                db.Entry(row).State = EntityState.Modified;
                db.SaveChanges();
            }

            Assert.AreEqual(0, queue.PendingCount, "旗標為 false 時，攔截器不得把任何資料放進佇列");
            Assert.AreEqual(0, queue.CoalescedCount);
            Assert.AreEqual(0, queue.DroppedCount);
            Assert.AreEqual(100, db.EQStatus_AGV.AsNoTracking().Single().Tag, "資料庫本身照常寫入");
        }

        [TestMethod]
        public void FlagTrue_RepeatedSavesOfSameRow_CoalesceToSingleQueueItem()
        {
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig { TransactionToPostgreSqlDatabase = true };
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 50);
            using var db = NewContext(queue);

            db.EQStatus_AGV.Add(new AGVStatus { Name = "AGV_1" });
            db.SaveChanges();
            for (int i = 0; i < 500; i++)
            {
                var row = db.EQStatus_AGV.Single(s => s.Name == "AGV_1");
                row.Tag = i + 1;
                db.Entry(row).State = EntityState.Modified; // 模擬舊行為：每次都強制 Modified
                db.SaveChanges();
            }

            Assert.AreEqual(1, queue.PendingCount, "沒有消費者時，同一列 501 次寫入只能留 1 筆");
            Assert.AreEqual(500, queue.CoalescedCount);
            Assert.IsTrue(queue.TryDequeue(out var item));
            Assert.AreEqual(500, item!.PropertyValues[nameof(AGVStatus.Tag)], "留下的是最新一筆快照");
            Assert.AreEqual("AGV_1", item.KeyValues.Single());
        }
    }
}
