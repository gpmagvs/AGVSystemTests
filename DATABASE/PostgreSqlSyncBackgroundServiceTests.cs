using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// PostgreSqlSyncBackgroundService：
    /// (1) BuildBatch 的去重鍵與佇列共用同一個 PostgreSqlSyncKey（DateTime 不再只到秒）；
    /// (2) 佇列滿載丟資料後，PG 恢復時重新武裝全表同步（TryRearmFullSyncAfterDrops）的條件與防呆。
    /// 不需要資料庫。
    /// </summary>
    [TestClass]
    public class PostgreSqlSyncBackgroundServiceTests
    {
        private DatabaseSyncConfig _originalConfig = null!;

        [TestInitialize]
        public void Setup()
        {
            _originalConfig = AGVSConfigulator.DatabaseSyncConfig;
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig
            {
                TransactionToPostgreSqlDatabase = true,
                FullSyncTablesAtBegin = "SystemAlarms",
            };
        }

        [TestCleanup]
        public void Cleanup() => AGVSConfigulator.DatabaseSyncConfig = _originalConfig;

        private static PostgreSqlSyncBackgroundService NewService(PostgreSqlSyncQueue queue, bool enableStartupTableSync = true)
            => new(queue, null!, Options.Create(new PostgreSqlSyncBackgroundServiceOptions { EnableStartupTableSync = enableStartupTableSync }));

        private static PostgreSqlSyncWorkItem Item(Type type, int seq, params object?[] keys) => new()
        {
            EntityType = type,
            KeyValues = keys,
            PropertyValues = new Dictionary<string, object?> { ["Seq"] = seq },
            ModifiedPropertyNames = new[] { "P" + seq },
            State = EntityState.Modified,
        };

        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        private static bool GetFlag(object target, string name)
            => (bool)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

        private static void Drain(PostgreSqlSyncQueue queue) { while (queue.TryDequeue(out _)) { } }

        // ───────────── BuildBatch 分組 ─────────────

        [TestMethod]
        public void BuildBatch_SameSecondDifferentMilliseconds_KeepsBothRows()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            var t0 = new DateTime(2026, 10, 16, 9, 30, 15);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 1, t0.AddMilliseconds(100)));
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 2, t0.AddMilliseconds(900)));
            Assert.AreEqual(2, queue.PendingCount);

            Assert.IsTrue(queue.TryDequeue(out var first));
            var batch = service.BuildBatch(first!);

            Assert.AreEqual(2, batch.Count, "舊版 k?.ToString() 只到秒，會把兩列不同的警報合成一筆");
            CollectionAssert.AreEqual(new object[] { 1, 2 }, batch.Select(i => i.PropertyValues["Seq"]!).ToArray());
        }

        [TestMethod]
        public void BuildBatch_SameKey_KeepsLatestSnapshot_AndUnionsModifiedNames()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            var t = new DateTime(2026, 10, 16, 9, 30, 15, 500);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 1, t));
            Assert.IsTrue(queue.TryDequeue(out var first));
            // first 已被取走，同一列再入列（佇列裡沒有可合併的待處理）→ 由 BuildBatch 去重
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 2, new DateTime(t.Ticks)));

            var batch = service.BuildBatch(first!);

            Assert.AreEqual(1, batch.Count);
            Assert.AreEqual(2, batch[0].PropertyValues["Seq"], "同一列只保留最後一筆");
            CollectionAssert.AreEquivalent(new[] { "P1", "P2" }, batch[0].ModifiedPropertyNames);
        }

        [TestMethod]
        public void BuildBatch_DifferentTypesSameKey_NotMerged()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            var t = new DateTime(2026, 10, 16, 9, 30, 15);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 1, t));
            queue.TryEnqueue(Item(typeof(AGVSystemCommonNet6.Microservices.MCSCIM.SecsMessageLog), 2, t));
            queue.TryDequeue(out var first);

            Assert.AreEqual(2, service.BuildBatch(first!).Count);
        }

        [TestMethod]
        public void BuildBatch_ItemsWithoutKeyValues_AreNotMergedTogether()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 1));
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 2));
            queue.TryDequeue(out var first);

            Assert.AreEqual(2, service.BuildBatch(first!).Count, "沒有主鍵值無法判斷是否同一列，與佇列一致：不合併");
        }

        [TestMethod]
        public void BuildBatch_StringKeysContainingOldSeparator_NotMerged()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 1, "a|b", "c"));
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 2, "a", "b|c"));
            queue.TryDequeue(out var first);

            Assert.AreEqual(2, service.BuildBatch(first!).Count, "舊版用 '|' 串接，這兩組複合鍵會變成同一個字串");
        }

        // ───────────── 丟資料後補做全表同步 ─────────────

        /// <summary>建立一個「已跑完啟動全表同步、PG 正常」的服務，與一個丟過資料且已消化完的佇列。</summary>
        private static (PostgreSqlSyncBackgroundService service, PostgreSqlSyncQueue queue) ArrangeAfterDrops()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 2);
            var service = NewService(queue);
            SetField(service, "_startupTableSyncChecked", true);
            SetField(service, "_initialFullSyncChecked", true);
            SetField(service, "_lastBatchSucceeded", true);

            for (int i = 0; i < 5; i++) queue.TryEnqueue(Item(typeof(clsAlarmDto), i, i));
            Assert.AreEqual(3, queue.DroppedCount);
            Drain(queue);
            return (service, queue);
        }

        private static readonly long Interval = (long)PostgreSqlSyncBackgroundService.MinDropResyncInterval.TotalMilliseconds;

        [TestMethod]
        public void Rearm_AfterDrops_WhenPgRecovered_ResetsBothFlags()
        {
            var (service, _) = ArrangeAfterDrops();

            Assert.IsTrue(service.TryRearmFullSyncAfterDrops(1_000_000));

            Assert.IsFalse(GetFlag(service, "_startupTableSyncChecked"), "旗標被重置，下一輪 SyncTablesDataAsync 會重跑 FullSyncTablesAtBegin");
            Assert.IsFalse(GetFlag(service, "_initialFullSyncChecked"));
        }

        [TestMethod]
        public void Rearm_NoDropsEver_DoesNothing()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 2);
            var service = NewService(queue);
            SetField(service, "_startupTableSyncChecked", true);
            SetField(service, "_initialFullSyncChecked", true);
            SetField(service, "_lastBatchSucceeded", true);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), 1, 1));

            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(1_000_000));
            Assert.IsTrue(GetFlag(service, "_startupTableSyncChecked"));
        }

        [TestMethod]
        public void Rearm_NotWhilePgNotConfirmedRecovered()
        {
            var (service, _) = ArrangeAfterDrops();
            SetField(service, "_lastBatchSucceeded", false);

            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(1_000_000));
            Assert.IsTrue(GetFlag(service, "_startupTableSyncChecked"));
        }

        [TestMethod]
        public void Rearm_NotWhileBacklogStillHigh()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 100);
            var service = NewService(queue);
            SetField(service, "_startupTableSyncChecked", true);
            SetField(service, "_initialFullSyncChecked", true);
            SetField(service, "_lastBatchSucceeded", true);
            for (int i = 0; i < 105; i++) queue.TryEnqueue(Item(typeof(clsAlarmDto), i, i));
            Assert.IsTrue(queue.DroppedCount > 0);
            Assert.AreEqual(100, queue.PendingCount);

            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(1_000_000), "積壓還在上限附近，PG 只是慢，不要再加全表同步的負擔");

            while (queue.PendingCount > 10) queue.TryDequeue(out _);
            Assert.IsTrue(service.TryRearmFullSyncAfterDrops(1_000_000), "積壓降到上限 10% 以下才補");
        }

        [TestMethod]
        public void Rearm_NotWhileFullSyncStillPending()
        {
            var (service, _) = ArrangeAfterDrops();
            SetField(service, "_startupTableSyncChecked", false);
            SetField(service, "_initialFullSyncChecked", false);

            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(1_000_000), "全表同步還沒跑（或正在跑），不重複武裝");
        }

        [TestMethod]
        public void Rearm_OnlyOncePerDropIncrease_AndRateLimited()
        {
            var (service, queue) = ArrangeAfterDrops();
            long now = 1_000_000;
            Assert.IsTrue(service.TryRearmFullSyncAfterDrops(now));

            // 模擬 SyncTablesDataAsync 跑完（旗標重新設起）
            SetField(service, "_startupTableSyncChecked", true);
            SetField(service, "_initialFullSyncChecked", true);

            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(now + Interval * 10), "沒有新的丟棄，再久也不會重跑（避免無限迴圈）");

            for (int i = 100; i < 105; i++) queue.TryEnqueue(Item(typeof(clsAlarmDto), i, i)); // 又丟了資料
            Drain(queue);
            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(now + Interval - 1), "距離上次補同步不到最短間隔，不觸發");
            Assert.IsTrue(service.TryRearmFullSyncAfterDrops(now + Interval), "超過最短間隔且有新的丟棄，才再補一次");
        }

        [TestMethod]
        public void Rearm_Disabled_WhenStartupTableSyncOff_OrNoTablesConfigured_OrFlagOff()
        {
            var (service, _) = ArrangeAfterDrops();
            SetField(service, "_options", new PostgreSqlSyncBackgroundServiceOptions { EnableStartupTableSync = false });
            Assert.IsFalse(service.TryRearmFullSyncAfterDrops(1_000_000), "EnableStartupTableSync=false → 沒有全表同步機制");

            var (service2, _) = ArrangeAfterDrops();
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig { TransactionToPostgreSqlDatabase = true, FullSyncTablesAtBegin = " , " };
            Assert.IsFalse(service2.TryRearmFullSyncAfterDrops(1_000_000), "FullSyncTablesAtBegin 沒有資料表");

            var (service3, _) = ArrangeAfterDrops();
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig { TransactionToPostgreSqlDatabase = false, FullSyncTablesAtBegin = "SystemAlarms" };
            Assert.IsFalse(service3.TryRearmFullSyncAfterDrops(1_000_000), "同步旗標關閉不動作");
        }
    }
}
