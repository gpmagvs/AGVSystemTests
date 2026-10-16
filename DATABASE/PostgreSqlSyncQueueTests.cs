using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// PostgreSqlSyncQueue：同列合併（保留最新）＋有界（達上限明確丟最舊並記錄）＋PendingCount 可觀測。
    /// 不需要資料庫。
    /// </summary>
    [TestClass]
    public class PostgreSqlSyncQueueTests
    {
        private sealed class RowA { }
        private sealed class RowB { }

        private static PostgreSqlSyncWorkItem Item(object key, int version, Type? type = null,
            EntityState state = EntityState.Modified, params string[] modified)
            => new()
            {
                EntityType = type ?? typeof(RowA),
                KeyValues = new[] { key },
                PropertyValues = new Dictionary<string, object?> { ["Id"] = key, ["Version"] = version },
                ModifiedPropertyNames = modified,
                State = state,
            };

        private static int VersionOf(PostgreSqlSyncWorkItem item) => (int)item.PropertyValues["Version"]!;

        private static List<PostgreSqlSyncWorkItem> Drain(PostgreSqlSyncQueue queue)
        {
            var list = new List<PostgreSqlSyncWorkItem>();
            while (queue.TryDequeue(out var item))
                list.Add(item!);
            return list;
        }

        // ───────────── 同一 PK 大量寫入只留最新 ─────────────

        [TestMethod]
        public void SameKey_ManyWrites_KeepsOnlyLatest()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 1000);

            for (int i = 1; i <= 50_000; i++)
            {
                Assert.IsTrue(queue.TryEnqueue(Item("AGV_1", i)));
                Assert.AreEqual(1, queue.PendingCount, "同一列不論寫入幾次，待處理都只能有 1 筆");
            }

            Assert.AreEqual(49_999, queue.CoalescedCount);
            Assert.AreEqual(0, queue.DroppedCount, "合併不是丟棄");

            var items = Drain(queue);
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(50_000, VersionOf(items[0]), "留下來的必須是最新一筆，不是第一筆");
            Assert.AreEqual(0, queue.PendingCount);
        }

        [TestMethod]
        public void SameKey_Coalesce_MergesModifiedPropertyNames_AndKeepsLatestState()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item("AGV_1", 1, state: EntityState.Added));
            queue.TryEnqueue(Item("AGV_1", 2, null, EntityState.Modified, "Tag", "Status"));
            queue.TryEnqueue(Item("AGV_1", 3, null, EntityState.Modified, "Status", "BatLevel"));

            var item = Drain(queue).Single();
            Assert.AreEqual(3, VersionOf(item));
            Assert.AreEqual(EntityState.Modified, item.State);
            CollectionAssert.AreEquivalent(new[] { "Tag", "Status", "BatLevel" }, item.ModifiedPropertyNames);
        }

        [TestMethod]
        public void DifferentKeysOrTypes_AreNotCoalesced()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item("1", 1, typeof(RowA)));
            queue.TryEnqueue(Item("2", 1, typeof(RowA)));
            queue.TryEnqueue(Item("1", 1, typeof(RowB)));   // 同 key、不同資料表
            queue.TryEnqueue(Item(1, 1, typeof(RowA)));     // int 1 與字串 "1" 以字串化比對視為同一列
            queue.TryEnqueue(new PostgreSqlSyncWorkItem
            {
                EntityType = typeof(RowA),
                KeyValues = new object?[] { "x", "y" },
                PropertyValues = new() { ["Version"] = 1 },
            });
            queue.TryEnqueue(new PostgreSqlSyncWorkItem
            {
                EntityType = typeof(RowA),
                KeyValues = new object?[] { "x", "z" },
                PropertyValues = new() { ["Version"] = 1 },
            });

            Assert.AreEqual(5, queue.PendingCount);
            Assert.AreEqual(1, queue.CoalescedCount);
        }

        [TestMethod]
        public void Coalesce_KeepsOriginalQueuePosition_Fifo()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item("A", 1));
            queue.TryEnqueue(Item("B", 1));
            queue.TryEnqueue(Item("C", 1));
            queue.TryEnqueue(Item("A", 2)); // 取代 A，但仍排在最前面

            var items = Drain(queue);
            CollectionAssert.AreEqual(new object[] { "A", "B", "C" }, items.Select(i => i.KeyValues[0]!).ToArray());
            Assert.AreEqual(2, VersionOf(items[0]));
        }

        [TestMethod]
        public void AfterDequeue_SameKeyIsQueuedAgainAsNewItem()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item("A", 1));
            Assert.IsTrue(queue.TryDequeue(out var first));
            Assert.AreEqual(1, VersionOf(first!));

            queue.TryEnqueue(Item("A", 2));
            Assert.AreEqual(1, queue.PendingCount);
            Assert.AreEqual(0, queue.CoalescedCount, "已被消費的舊資料不能被後來的資料吃掉");
            Assert.AreEqual(2, VersionOf(Drain(queue).Single()));
        }

        [TestMethod]
        public void ItemsWithoutKeyValues_AreNotCoalesced_ButStillBounded()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 5, dropLogInterval: TimeSpan.FromHours(1));
            for (int i = 0; i < 10; i++)
                queue.TryEnqueue(new PostgreSqlSyncWorkItem { EntityType = typeof(RowA), PropertyValues = new() { ["Version"] = i } });

            Assert.AreEqual(5, queue.PendingCount);
            Assert.AreEqual(5, queue.DroppedCount);
            CollectionAssert.AreEqual(new[] { 5, 6, 7, 8, 9 }, Drain(queue).Select(VersionOf).ToArray());
        }

        // ───────────── 有界：不會超過上限，且丟的是最舊、留的是最新 ─────────────

        [TestMethod]
        public void DistinctKeys_NeverExceedCap_DropsOldestKeepsNewest()
        {
            const int cap = 100;
            var queue = new PostgreSqlSyncQueue(cap, dropLogInterval: TimeSpan.FromHours(1));

            for (int i = 0; i < 1000; i++)
            {
                Assert.IsTrue(queue.TryEnqueue(Item($"K{i}", i)), "新資料一律被接受");
                Assert.IsTrue(queue.PendingCount <= cap, $"第 {i} 筆後 PendingCount={queue.PendingCount} 超過上限 {cap}");
            }

            Assert.AreEqual(cap, queue.PendingCount);
            Assert.AreEqual(900, queue.DroppedCount, "被丟棄的筆數必須被計數，不能無聲丟失");

            var items = Drain(queue);
            Assert.AreEqual(cap, items.Count);
            CollectionAssert.AreEqual(Enumerable.Range(900, 100).ToArray(), items.Select(VersionOf).ToArray(), "必須保留最新的 100 筆");
        }

        [TestMethod]
        public void WhenFull_CoalescingStillWorks_WithoutDropping()
        {
            var queue = new PostgreSqlSyncQueue(3);
            queue.TryEnqueue(Item("A", 1));
            queue.TryEnqueue(Item("B", 1));
            queue.TryEnqueue(Item("C", 1));

            for (int i = 2; i < 100; i++)
                queue.TryEnqueue(Item("B", i));

            Assert.AreEqual(3, queue.PendingCount);
            Assert.AreEqual(0, queue.DroppedCount, "滿了但寫的是已在佇列的列：合併即可，不用丟別人的資料");
            Assert.AreEqual(99, VersionOf(Drain(queue).Single(i => (string)i.KeyValues[0]! == "B")));
        }

        [TestMethod]
        public void Ctor_InvalidCap_Throws()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new PostgreSqlSyncQueue(0));
            Assert.AreEqual(PostgreSqlSyncQueue.DefaultMaxPendingCount, new PostgreSqlSyncQueue().MaxPendingCount);
            Assert.IsFalse(new PostgreSqlSyncQueue().TryEnqueue(null!));
        }

        // ───────────── 丟棄一定有 Warn log（限頻）─────────────

        private static List<string> CaptureWarnings(Action action)
        {
            var original = LogManager.Configuration;
            var target = new MemoryTarget("pgq-test") { Layout = "${level}|${logger}|${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, target);
            LogManager.Configuration = config;
            try
            {
                action();
                LogManager.Flush();
                return target.Logs.ToList();
            }
            finally
            {
                LogManager.Configuration = original;
            }
        }

        [TestMethod]
        public void Drop_WritesWarnLog_WithTypeKeysAndCounts()
        {
            var logs = CaptureWarnings(() =>
            {
                var queue = new PostgreSqlSyncQueue(2, dropLogInterval: TimeSpan.Zero);
                queue.TryEnqueue(Item("old", 1));
                queue.TryEnqueue(Item("mid", 1));
                queue.TryEnqueue(Item("new", 1)); // 丟掉 old
            });

            Assert.AreEqual(1, logs.Count);
            StringAssert.StartsWith(logs[0], "Warn|");
            StringAssert.Contains(logs[0], "已達上限 2");
            StringAssert.Contains(logs[0], "RowA");
            StringAssert.Contains(logs[0], "[old]", "log 要印出被丟棄那一筆的主鍵");
            StringAssert.Contains(logs[0], "累計 1 筆");
        }

        [TestMethod]
        public void Drop_Log_IsRateLimited_ButCountersAreExact()
        {
            long dropped = 0;
            var logs = CaptureWarnings(() =>
            {
                var queue = new PostgreSqlSyncQueue(10, dropLogInterval: TimeSpan.FromHours(1));
                for (int i = 0; i < 5000; i++)
                    queue.TryEnqueue(Item($"K{i}", i));
                dropped = queue.DroppedCount;
            });

            Assert.AreEqual(4990, dropped);
            Assert.AreEqual(1, logs.Count, "限頻：高頻丟棄不能把 log 洗爆（只有第一次會寫）");
        }

        // ───────────── Dequeue 行為 ─────────────

        private static async Task AssertBlocksUntilCancelledAsync(PostgreSqlSyncQueue queue, CancellationToken token)
        {
            try
            {
                await queue.DequeueAsync(token);
                Assert.Fail("佇列已空，DequeueAsync 應該一直等到被取消");
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        public async Task DequeueAsync_WaitsForItem_AndReturnsLatestCoalesced()
        {
            var queue = new PostgreSqlSyncQueue();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var pending = queue.DequeueAsync(cts.Token).AsTask();
            await Task.Delay(50);
            Assert.IsFalse(pending.IsCompleted);

            queue.TryEnqueue(Item("A", 1));
            var item = await pending;
            Assert.AreEqual(1, VersionOf(item));
            Assert.AreEqual(0, queue.PendingCount);

            // 同列合併不能讓信號量多出計數：合併後只能取得一筆
            queue.TryEnqueue(Item("A", 2));
            queue.TryEnqueue(Item("A", 3));
            Assert.AreEqual(3, VersionOf(await queue.DequeueAsync(cts.Token)));
            using var shortCts = new CancellationTokenSource(100);
            await AssertBlocksUntilCancelledAsync(queue, shortCts.Token);
        }

        [TestMethod]
        public async Task DequeueAsync_AfterDropOldest_StillConsistent()
        {
            var queue = new PostgreSqlSyncQueue(2, dropLogInterval: TimeSpan.FromHours(1));
            queue.TryEnqueue(Item("A", 1));
            queue.TryEnqueue(Item("B", 1));
            queue.TryEnqueue(Item("C", 1)); // 丟 A

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.AreEqual("B", (string)(await queue.DequeueAsync(cts.Token)).KeyValues[0]!);
            Assert.AreEqual("C", (string)(await queue.DequeueAsync(cts.Token)).KeyValues[0]!);
            using var shortCts = new CancellationTokenSource(100);
            await AssertBlocksUntilCancelledAsync(queue, shortCts.Token);
        }

        // ───────────── 併發 ─────────────

        [TestMethod]
        public async Task Concurrent_Producers_And_Consumer_NeverExceedCap_AndLatestPerKeyWins()
        {
            const int cap = 64;
            const int producers = 8;
            const int perProducer = 20_000;
            var queue = new PostgreSqlSyncQueue(cap, dropLogInterval: TimeSpan.FromHours(1));
            int maxObserved = 0;
            long consumed = 0;
            bool done = false;

            var consumer = Task.Run(() =>
            {
                while (!Volatile.Read(ref done) || queue.PendingCount > 0)
                {
                    if (queue.TryDequeue(out _)) Interlocked.Increment(ref consumed);
                    else Thread.Yield();
                }
            });

            var tasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
            {
                var rnd = new Random(p);
                for (int i = 0; i < perProducer; i++)
                {
                    queue.TryEnqueue(Item($"K{rnd.Next(0, 200)}", i));
                    int pending = queue.PendingCount;
                    int seen;
                    while (pending > (seen = Volatile.Read(ref maxObserved)))
                        Interlocked.CompareExchange(ref maxObserved, pending, seen);
                }
            })).ToArray();

            await Task.WhenAll(tasks);
            Volatile.Write(ref done, true);
            await consumer;

            Assert.IsTrue(maxObserved <= cap, $"觀察到 PendingCount={maxObserved} 超過上限 {cap}");
            Assert.AreEqual(0, queue.PendingCount);
            // 每一筆寫入都被「消費、合併、丟棄」其中之一吃掉：沒有漏算
            Assert.AreEqual((long)producers * perProducer, consumed + queue.CoalescedCount + queue.DroppedCount);
        }

        [TestMethod]
        public void SharedQueue_IsSingleton_WithDefaultCap()
        {
            Assert.AreSame(PostgreSqlSyncQueue.Shared, PostgreSqlSyncQueue.Shared);
            Assert.AreEqual(PostgreSqlSyncQueue.DefaultMaxPendingCount, PostgreSqlSyncQueue.Shared.MaxPendingCount);
        }
    }
}
