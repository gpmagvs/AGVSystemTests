using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// PostgreSqlSyncQueue.RequeueFront：寫入 PG 失敗的資料放回佇列最前面（維持原順序）、
    /// 失敗期間已有更新的快照時以新的為準、佇列滿了計入 DroppedCount。不需要資料庫。
    /// </summary>
    [TestClass]
    public class PostgreSqlSyncRequeueFrontTests
    {
        private static PostgreSqlSyncWorkItem Item(int seq, object key, params string[] modified) => new()
        {
            EntityType = typeof(clsAlarmDto),
            KeyValues = new[] { key },
            PropertyValues = new Dictionary<string, object?> { ["Seq"] = seq },
            ModifiedPropertyNames = modified.Length > 0 ? modified : new[] { "P" + seq },
            State = EntityState.Modified,
        };

        private static List<int> DrainSeq(PostgreSqlSyncQueue queue)
        {
            var result = new List<int>();
            while (queue.TryDequeue(out var item))
                result.Add((int)item!.PropertyValues["Seq"]!);
            return result;
        }

        [TestMethod]
        public void RequeueFront_PutsItemsBeforeExisting_InOriginalOrder()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item(10, "c"));
            queue.TryEnqueue(Item(11, "d"));

            int requeued = queue.RequeueFront(new[] { Item(1, "a"), Item(2, "b") });

            Assert.AreEqual(2, requeued);
            Assert.AreEqual(4, queue.PendingCount);
            CollectionAssert.AreEqual(new[] { 1, 2, 10, 11 }, DrainSeq(queue), "失敗那筆（a）要排第一個，之後是還沒處理的（b），再來才是新入列的");
            Assert.AreEqual(0, queue.DroppedCount);
            Assert.AreEqual(0, queue.CoalescedCount);
        }

        [TestMethod]
        public void RequeueFront_NewerSnapshotAlreadyQueued_NewerWins_AndKeepsItsPosition()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item(20, "x"));
            queue.TryEnqueue(Item(2, "a", "NewCol"));   // 失敗期間 a 又被更新

            queue.RequeueFront(new[] { Item(1, "a", "OldCol"), Item(3, "b") });

            Assert.AreEqual(3, queue.PendingCount, "a 不可變成兩筆");
            Assert.AreEqual(1, queue.CoalescedCount, "舊快照被新快照取代，計入合併");
            var drained = new List<PostgreSqlSyncWorkItem>();
            while (queue.TryDequeue(out var it)) drained.Add(it!);
            CollectionAssert.AreEqual(new[] { 3, 20, 2 }, drained.Select(i => (int)i.PropertyValues["Seq"]!).ToArray(),
                "b 放回最前面；a 以佇列裡較新的快照（Seq=2）為準，維持原位置，不被舊資料蓋掉");
            CollectionAssert.AreEquivalent(new[] { "OldCol", "NewCol" }, drained[2].ModifiedPropertyNames, "ModifiedPropertyNames 合併");
        }

        [TestMethod]
        public void RequeueFront_DuplicateKeysWithinItems_LaterOneWins()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.RequeueFront(new[] { Item(1, "a"), Item(2, "a") });
            Assert.AreEqual(1, queue.PendingCount);
            CollectionAssert.AreEqual(new[] { 2 }, DrainSeq(queue));
        }

        [TestMethod]
        public void RequeueFront_QueueFull_CountsDropped_AndDoesNotEvictNewerData()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 3);
            queue.TryEnqueue(Item(10, "x"));
            queue.TryEnqueue(Item(11, "y"));
            queue.TryEnqueue(Item(12, "z"));

            int requeued = queue.RequeueFront(new[] { Item(1, "a"), Item(2, "b") });

            Assert.AreEqual(0, requeued);
            Assert.AreEqual(2, queue.DroppedCount, "放不回去的必須計入 DroppedCount，讓補同步（TryRearmFullSyncAfterDrops）接手");
            Assert.AreEqual(3, queue.PendingCount);
            CollectionAssert.AreEqual(new[] { 10, 11, 12 }, DrainSeq(queue), "佇列裡較新的資料不可被擠掉");
        }

        [TestMethod]
        public void RequeueFront_PartiallyFull_KeepsEarliestItems_DropsTheRest()
        {
            var queue = new PostgreSqlSyncQueue(maxPendingCount: 3);
            queue.TryEnqueue(Item(10, "x"));

            queue.RequeueFront(new[] { Item(1, "a"), Item(2, "b"), Item(3, "c") });

            Assert.AreEqual(1, queue.DroppedCount);
            Assert.AreEqual(3, queue.PendingCount);
            // 由後往前放：c 先放、b 再放，a 放不下（a 是最舊的一筆，丟最舊的與 TryEnqueue 的語意一致）
            CollectionAssert.AreEqual(new[] { 2, 3, 10 }, DrainSeq(queue));
        }

        [TestMethod]
        public async Task RequeueFront_SignalsWaitingConsumer()
        {
            var queue = new PostgreSqlSyncQueue();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var waiting = queue.DequeueAsync(cts.Token).AsTask();
            Assert.IsFalse(waiting.IsCompleted);

            queue.RequeueFront(new[] { Item(1, "a") });

            var got = await waiting;
            Assert.AreEqual(1, got.PropertyValues["Seq"]);
            Assert.AreEqual(0, queue.PendingCount);
            Assert.IsFalse(queue.TryDequeue(out _), "信號量計數與佇列一致，不會多出幽靈項目");
        }

        [TestMethod]
        public void RequeueFront_NullOrEmpty_NoOp()
        {
            var queue = new PostgreSqlSyncQueue();
            Assert.AreEqual(0, queue.RequeueFront(null));
            Assert.AreEqual(0, queue.RequeueFront(Array.Empty<PostgreSqlSyncWorkItem>()));
            Assert.AreEqual(0, queue.RequeueFront(new PostgreSqlSyncWorkItem?[] { null }));
            Assert.AreEqual(0, queue.PendingCount);
        }

        [TestMethod]
        public void SkippedCount_StartsAtZero_AndRecordSkippedIncrements()
        {
            var queue = new PostgreSqlSyncQueue();
            Assert.AreEqual(0, queue.SkippedCount);
            Assert.AreEqual(1, queue.RecordSkipped());
            Assert.AreEqual(2, queue.RecordSkipped());
            Assert.AreEqual(2, queue.SkippedCount);
        }
    }
}
