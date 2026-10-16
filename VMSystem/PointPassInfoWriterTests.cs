using System.Reflection;
using AGVSystemCommonNet6.Availability;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VMSystem.AGV;
using VMSystem.VMS;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// PointPassTime 寫入改成「每次一個全新、用完即 Dispose 的 DbContext」後的行為：
    /// - 寫 N 筆後沒有任何 DbContext 留著追蹤實體 (舊作法：共用 DbContext 追蹤 N 筆，永不釋放)
    /// - 每次呼叫都是新的 DbContext，且一定被 Dispose
    /// - 已存進資料庫的資料與舊版相同 (Add 之後的整列 UPDATE 也一樣)
    /// - 多執行緒並行寫入不會踩到「同一個 DbContext 同時被兩個操作使用」
    /// 使用 SQLite，不需要 SQL Server。
    /// </summary>
    [TestClass]
    public class PointPassInfoWriterTests
    {
        private const string CreateTableSql = @"
CREATE TABLE PointPassTime (
    DataKey TEXT NOT NULL PRIMARY KEY,
    Time TEXT NOT NULL,
    LeaveTime TEXT NOT NULL,
    StartWaitTrafficSolveTime TEXT NOT NULL,
    EndWaitTrafficSolveTime TEXT NOT NULL,
    Tag INTEGER NOT NULL,
    Duration REAL NOT NULL,
    AGVName TEXT NOT NULL,
    TaskName TEXT NOT NULL,
    StageWhenReach INTEGER NOT NULL,
    StageWhenLeaving INTEGER NOT NULL,
    IsWaitingTrafficControlSolve INTEGER NOT NULL
);";

        /// <summary>可記錄「建立數 / Dispose 數 / Dispose 當下仍被追蹤的實體數」的 DbContext</summary>
        private sealed class ProbeDb : AGVSDbContext
        {
            private readonly ProbeStats _stats;
            private int _disposed;
            public ProbeDb(DbContextOptions<AGVSDbContext> options, ProbeStats stats) : base(options)
            {
                _stats = stats;
                Interlocked.Increment(ref stats.Created);
                lock (stats.Instances) stats.Instances.Add(this);
            }

            public override void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    int tracked = ChangeTracker.Entries().Count();
                    Interlocked.Increment(ref _stats.Disposed);
                    InterlockedMax(ref _stats.MaxTrackedAtDispose, tracked);
                }
                base.Dispose();
            }

            private static void InterlockedMax(ref int target, int value)
            {
                int cur;
                while (value > (cur = Volatile.Read(ref target)))
                    if (Interlocked.CompareExchange(ref target, value, cur) == cur) break;
            }
        }

        private sealed class ProbeStats
        {
            public int Created, Disposed, MaxTrackedAtDispose;
            public readonly List<ProbeDb> Instances = new();
        }

        private sealed class ProbeFactory : IDbContextFactory<AGVSDbContext>
        {
            private readonly Func<DbContextOptions<AGVSDbContext>> _options;
            public readonly ProbeStats Stats = new();
            public ProbeFactory(Func<DbContextOptions<AGVSDbContext>> options) => _options = options;
            public AGVSDbContext CreateDbContext() => new ProbeDb(_options(), Stats);
        }

        private SqliteConnection _connection = null!;
        private string _dbFile = null!;

        [TestInitialize]
        public void Setup()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            Exec(_connection, CreateTableSql);

            _dbFile = Path.Combine(Path.GetTempPath(), $"ppt-{Guid.NewGuid():N}.db");
            using var fileConn = new SqliteConnection($"Data Source={_dbFile}");
            fileConn.Open();
            Exec(fileConn, CreateTableSql);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(_dbFile); } catch { }
        }

        private static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private DbContextOptions<AGVSDbContext> MemoryOptions()
            => new DbContextOptionsBuilder<AGVSDbContext>().UseSqlite(_connection).Options;

        private DbContextOptions<AGVSDbContext> FileOptions()
            => new DbContextOptionsBuilder<AGVSDbContext>().UseSqlite($"Data Source={_dbFile}").Options;

        private static clsPointPassInfo Info(int tag, string agv = "AGV_1")
            => new()
            {
                DataKey = $"{DateTime.Now:yyyyMMddHHmmssffff}_{tag}_{agv}_{Guid.NewGuid():N}",
                Tag = tag,
                AGVName = agv,
                TaskName = "TASK_1",
                Time = new DateTime(2026, 10, 16, 8, 0, 0),
                StageWhenReach = 2,
                StartWaitTrafficSolveTime = DateTime.MinValue,
                EndWaitTrafficSolveTime = DateTime.MinValue,
            };

        [TestMethod]
        public async Task ControlGroup_SharedContext_KeepsEveryEntityTracked()
        {
            // 舊作法 (對照組)：同一個 DbContext 一直 Add + SaveChanges，實體永遠留在 ChangeTracker
            using var shared = new AGVSDbContext(MemoryOptions());
            for (int i = 0; i < 50; i++)
            {
                shared.PointPassTime.Add(Info(i));
                await shared.SaveChangesAsync();
            }
            Assert.AreEqual(50, shared.ChangeTracker.Entries().Count(),
                "對照組：共用 DbContext 追蹤了全部 50 筆，這就是記憶體只增不減的來源");
        }

        [TestMethod]
        public async Task Add_N_Rows_LeavesNoTrackedEntity_AndEveryContextIsDisposed()
        {
            var factory = new ProbeFactory(MemoryOptions);
            var writer = new PointPassInfoWriter(factory.CreateDbContext);

            for (int i = 0; i < 200; i++)
                await writer.AddAsync(Info(i));

            Assert.AreEqual(200, factory.Stats.Created, "每次寫入都要新建一個 DbContext");
            Assert.AreEqual(200, factory.Stats.Disposed, "每個 DbContext 用完都要 Dispose");
            Assert.AreEqual(1, factory.Stats.MaxTrackedAtDispose, "每個 DbContext 在釋放時最多只追蹤本次寫入的那 1 筆，不會累積");
            Assert.AreEqual(200, factory.Stats.Instances.Distinct().Count(), "不能重複使用同一個 DbContext");

            using var verify = new AGVSDbContext(MemoryOptions());
            Assert.AreEqual(200, verify.PointPassTime.AsNoTracking().Count(), "資料照常寫進資料庫");
        }

        [TestMethod]
        public async Task Update_AfterAdd_WritesWholeRow_SameAsLegacyBehavior()
        {
            var factory = new ProbeFactory(MemoryOptions);
            var writer = new PointPassInfoWriter(factory.CreateDbContext);
            var info = Info(7);
            await writer.AddAsync(info);

            // 與 clsAGV.StorePointStayInfo 離開點位時相同的欄位更新
            info.StageWhenLeaving = 5;
            info.LeaveTime = new DateTime(2026, 10, 16, 8, 0, 30);
            info.Duration = (info.LeaveTime - info.Time).TotalSeconds;
            await writer.UpdateAsync(info);

            // 與 clsAGV.NavigationState_OnStart/EndWaitConflicSolve 相同
            info.StartWaitTrafficSolveTime = new DateTime(2026, 10, 16, 8, 0, 10);
            await writer.UpdateAsync(info);
            info.EndWaitTrafficSolveTime = new DateTime(2026, 10, 16, 8, 0, 20);
            await writer.UpdateAsync(info);

            using var verify = new AGVSDbContext(MemoryOptions());
            var row = verify.PointPassTime.AsNoTracking().Single();
            Assert.AreEqual(info.DataKey, row.DataKey);
            Assert.AreEqual(7, row.Tag);
            Assert.AreEqual("AGV_1", row.AGVName);
            Assert.AreEqual("TASK_1", row.TaskName);
            Assert.AreEqual(2, row.StageWhenReach);
            Assert.AreEqual(5, row.StageWhenLeaving);
            Assert.AreEqual(30d, row.Duration);
            Assert.AreEqual(new DateTime(2026, 10, 16, 8, 0, 30), row.LeaveTime);
            Assert.AreEqual(new DateTime(2026, 10, 16, 8, 0, 10), row.StartWaitTrafficSolveTime);
            Assert.AreEqual(new DateTime(2026, 10, 16, 8, 0, 20), row.EndWaitTrafficSolveTime);

            Assert.AreEqual(4, factory.Stats.Created);
            Assert.AreEqual(4, factory.Stats.Disposed);
            Assert.AreEqual(1, factory.Stats.MaxTrackedAtDispose);
        }

        [TestMethod]
        public async Task SaveFailure_StillDisposesContext_AndDoesNotPoisonNextWrite()
        {
            var factory = new ProbeFactory(MemoryOptions);
            var writer = new PointPassInfoWriter(factory.CreateDbContext);
            var dup = Info(1);
            await writer.AddAsync(dup);

            // 同主鍵再 Add -> 失敗 (舊作法：失敗實體留在共用 DbContext，之後每次 SaveChanges 都會重試並失敗)
            await Assert.ThrowsExceptionAsync<DbUpdateException>(() => writer.AddAsync(new clsPointPassInfo { DataKey = dup.DataKey, Tag = 1 }));
            Assert.AreEqual(2, factory.Stats.Disposed, "失敗時 DbContext 也要被 Dispose");

            await writer.AddAsync(Info(2)); // 下一筆正常
            using var verify = new AGVSDbContext(MemoryOptions());
            Assert.AreEqual(2, verify.PointPassTime.AsNoTracking().Count());
        }

        [TestMethod]
        public async Task ConcurrentWrites_FromManyVehicles_DoNotShareAContext()
        {
            var factory = new ProbeFactory(FileOptions);
            var writer = new PointPassInfoWriter(factory.CreateDbContext);

            var tasks = Enumerable.Range(0, 8).Select(v => Task.Run(async () =>
            {
                for (int i = 0; i < 25; i++)
                {
                    var info = Info(i, $"AGV_{v}");
                    await writer.AddAsync(info);
                    info.LeaveTime = DateTime.Now;
                    await writer.UpdateAsync(info);
                }
            })).ToArray();
            await Task.WhenAll(tasks); // 若共用同一個 DbContext，這裡會丟「A second operation was started on this context」

            Assert.AreEqual(8 * 25 * 2, factory.Stats.Created);
            Assert.AreEqual(factory.Stats.Created, factory.Stats.Disposed);
            using var verify = new AGVSDbContext(FileOptions());
            Assert.AreEqual(8 * 25, verify.PointPassTime.AsNoTracking().Count());
        }

        [TestMethod]
        public void Ctor_NullFactory_Throws()
            => Assert.ThrowsException<ArgumentNullException>(() => new PointPassInfoWriter(null!));

        // ---- clsAGV 接線：clsAGV 的寫入器要走 VMSManager.AGVSDbContextFactory ----

        private static readonly PropertyInfo FactoryProp =
            typeof(VMSManager).GetProperty(nameof(VMSManager.AGVSDbContextFactory), BindingFlags.Public | BindingFlags.Static)!;

        private static PointPassInfoWriter GetAgvWriter()
        {
            var agv = new clsAGV();
            var field = typeof(clsAGV).GetField("pointPassInfoWriter", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "clsAGV 應有 pointPassInfoWriter 欄位");
            return (PointPassInfoWriter)field!.GetValue(agv)!;
        }

        [TestMethod]
        public async Task ClsAgvWriter_UsesVmsManagerFactory_AndFailsClearlyWhenNotInitialized()
        {
            var original = FactoryProp.GetValue(null);
            try
            {
                FactoryProp.SetValue(null, null);
                var writer = GetAgvWriter();
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => writer.AddAsync(Info(1)));

                var factory = new ProbeFactory(MemoryOptions);
                FactoryProp.SetValue(null, factory);
                await writer.AddAsync(Info(2));
                await writer.AddAsync(Info(3));

                Assert.AreEqual(2, factory.Stats.Created, "clsAGV 每次寫入向工廠要一個新的 DbContext");
                Assert.AreEqual(2, factory.Stats.Disposed);
            }
            finally
            {
                FactoryProp.SetValue(null, original);
            }
        }

        [TestMethod]
        public void ClsAgv_NoLongerUsesSharedContextForPointPassTime()
        {
            // 舊版 clsAGV 直接對共用 dbContext 操作 PointPassTime；現在 clsAGV.cs 不應再出現這種寫法
            string? dir = AppContext.BaseDirectory;
            string? src = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir, "VMSystem", "AGV", "clsAGV.cs");
                if (File.Exists(candidate)) { src = candidate; break; }
                dir = Path.GetDirectoryName(dir);
            }
            if (src == null) { Assert.Inconclusive("找不到 VMSystem 原始碼，略過原始碼檢查"); return; }
            string text = File.ReadAllText(src);
            Assert.IsFalse(text.Contains("dbContext.PointPassTime"), "不可再直接使用共用 dbContext.PointPassTime");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"dbContext\.Entry\(tagPointStayInfo\)"), "不可再用共用 dbContext 更新 tagPointStayInfo");
        }
    }
}
