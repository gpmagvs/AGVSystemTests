using System.Reflection;
using AGVSystemCommonNet6.DATABASE;
using VMSystem;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.AGV.TaskDispatch.Tasks;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// clsTaskDatabaseWriteableAbstract (TaskBase / OrderHandlerBase 的共同基底) 的 AGVSDatabase 釋放行為：
    /// Dispose 會釋放、冪等、有人使用時延後釋放、釋放後不會 use-after-dispose、訂單結束路徑一定釋放。
    /// 不需要 SQL Server：AGVSDatabase 在 SysConfigs 為 null 時不會建立連線，Dispose 也安全。
    /// </summary>
    [TestClass]
    public class AgvsDatabaseDisposeTests
    {
        /// <summary>可計算 Dispose 次數的 AGVSDatabase 假物件</summary>
        private class CountingDb : AGVSDatabase
        {
            public int DisposeCount;
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    Interlocked.Increment(ref DisposeCount);
                base.Dispose(disposing);
            }
        }

        private class Probe : clsTaskDatabaseWriteableAbstract
        {
            public readonly List<CountingDb> Created = new();
            public Probe() : base(new SemaphoreSlim(1, 1), null) { }
            private Probe(List<CountingDb> created, Func<AGVSDatabase> factory) : base(new SemaphoreSlim(1, 1), factory) { Created = created; }

            public static Probe Create()
            {
                var created = new List<CountingDb>();
                return new Probe(created, () =>
                {
                    var db = new CountingDb();
                    lock (created) created.Add(db);
                    return db;
                });
            }

            /// <summary>第一個建立的 = 建構時建立的共用連線</summary>
            public CountingDb Shared => Created[0];
            public IDisposable Lease(out AGVSDatabase db)
            {
                var lease = AcquireDatabase();
                db = lease.Database;
                return lease;
            }
        }

        [TestMethod]
        public void Dispose_ReleasesDatabase_Once()
        {
            var p = Probe.Create();
            Assert.IsFalse(p.IsDatabaseDisposed);
            Assert.AreEqual(0, p.Shared.DisposeCount);

            p.Dispose();

            Assert.IsTrue(p.IsDatabaseDisposed);
            Assert.AreEqual(1, p.Shared.DisposeCount);
        }

        [TestMethod]
        public void Dispose_IsIdempotent()
        {
            var p = Probe.Create();
            p.Dispose();
            p.Dispose();
            p.Dispose();
            Assert.AreEqual(1, p.Shared.DisposeCount, "重複 Dispose 不可重複釋放 (no double dispose)");
        }

        [TestMethod]
        public void Dispose_WhileLeaseHeld_DefersUntilLeaseReturned()
        {
            var p = Probe.Create();
            var lease = p.Lease(out var db);
            Assert.AreSame(p.Shared, db);

            p.Dispose();
            Assert.AreEqual(0, p.Shared.DisposeCount, "仍有使用者 (ModifyOrder 進行中) 時不可釋放");
            Assert.IsFalse(p.IsDatabaseDisposed);

            lease.Dispose();
            Assert.AreEqual(1, p.Shared.DisposeCount, "最後一個租約歸還時才釋放");
            Assert.IsTrue(p.IsDatabaseDisposed);
        }

        [TestMethod]
        public void Acquire_AfterDispose_GivesTransientDb_NotTheDisposedOne()
        {
            var p = Probe.Create();
            p.Dispose();

            var lease = p.Lease(out var db);
            Assert.AreNotSame(p.Shared, db, "釋放後不可再拿到已釋放的共用連線 (use-after-dispose)");
            var transient = (CountingDb)db;
            Assert.AreEqual(0, transient.DisposeCount);

            lease.Dispose();
            Assert.AreEqual(1, transient.DisposeCount, "一次性連線在租約歸還時釋放");
            Assert.AreEqual(1, p.Shared.DisposeCount, "共用連線不會因此被再次釋放");
        }

        [TestMethod]
        public void Lease_DoubleDispose_DoesNotReleaseOtherUsersEarly()
        {
            var p = Probe.Create();
            var a = p.Lease(out _);
            var b = p.Lease(out _);

            p.Dispose();
            a.Dispose();
            a.Dispose(); // 重複歸還不可多扣一次
            Assert.AreEqual(0, p.Shared.DisposeCount, "b 還在使用中");

            b.Dispose();
            Assert.AreEqual(1, p.Shared.DisposeCount);
        }

        [TestMethod]
        public void Concurrent_AcquireAndDispose_NeverDisposesWhileInUse_AndDisposesExactlyOnce()
        {
            for (int round = 0; round < 50; round++)
            {
                var p = Probe.Create();
                int violations = 0;
                var start = new ManualResetEventSlim(false);
                var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    for (int i = 0; i < 200; i++)
                    {
                        using (p.Lease(out var db))
                        {
                            // 拿到共用連線時，持有期間它不可已被釋放
                            if (db is CountingDb c && ReferenceEquals(db, p.Shared) && c.DisposeCount != 0)
                                Interlocked.Increment(ref violations);
                            Thread.SpinWait(50);
                            if (db is CountingDb c2 && c2.DisposeCount != 0)
                                Interlocked.Increment(ref violations);
                        }
                    }
                })).ToArray();
                var disposer = Task.Run(() => { start.Wait(); Thread.Sleep(1); p.Dispose(); p.Dispose(); });
                start.Set();
                Task.WaitAll(workers.Append(disposer).ToArray());

                Assert.AreEqual(0, violations, $"round {round}: 使用中的連線被釋放");
                Assert.AreEqual(1, p.Shared.DisposeCount, $"round {round}: 共用連線必須只釋放一次");
            }
        }

        [TestMethod]
        public void TaskBase_Dispose_ReleasesDatabase_AndIsIdempotent()
        {
            var task = new MoveToDestineTask();
            Assert.IsFalse(task.IsDatabaseDisposed);
            task.Dispose();
            task.Dispose();
            Assert.IsTrue(task.IsDatabaseDisposed);
        }

        /// <summary>覆寫 ExecuteOrder，不連 AGV / DB / HTTP，專門驗證「訂單結束一定釋放資源」</summary>
        private class ProbeOrderHandler : MoveToOrderHandler
        {
            public Exception? ThrowInExecute;
            public ProbeOrderHandler() : base(new SemaphoreSlim(1, 1)) { }
            protected override Task ExecuteOrder()
            {
                if (ThrowInExecute != null)
                    throw ThrowInExecute;
                return Task.CompletedTask;
            }
            public Task Run()
            {
                var m = typeof(OrderHandlerBase).GetMethod("ExecuteOrderAndReleaseResources", BindingFlags.Instance | BindingFlags.NonPublic)!;
                return (Task)m.Invoke(this, null)!;
            }
        }

        [TestMethod]
        public async Task OrderHandler_NormalEnd_ReleasesHandlerAndTasks()
        {
            var h = new ProbeOrderHandler();
            var pending1 = new MoveToDestineTask();
            var pending2 = new MoveToDestineTask();
            h.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { pending1, pending2 });
            var running = h.RunningTask;

            await h.Run();

            Assert.IsTrue(h.IsDatabaseDisposed, "OrderHandler 的 DB 應已釋放");
            Assert.IsTrue(running.IsDatabaseDisposed, "RunningTask 的 DB 應已釋放");
            Assert.IsTrue(pending1.IsDatabaseDisposed && pending2.IsDatabaseDisposed, "尚未執行的任務 DB 也要釋放");
        }

        [TestMethod]
        public async Task OrderHandler_ExecuteOrderThrows_StillReleases_AndExceptionPropagates()
        {
            var h = new ProbeOrderHandler { ThrowInExecute = new InvalidOperationException("boom") };
            var running = h.RunningTask;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => h.Run());

            Assert.IsTrue(h.IsDatabaseDisposed);
            Assert.IsTrue(running.IsDatabaseDisposed);
        }

        [TestMethod]
        public async Task OrderHandler_DisposeAgainAfterEnd_IsSafe()
        {
            var h = new ProbeOrderHandler();
            await h.Run();
            h.Dispose();
            h.Dispose();
            Assert.IsTrue(h.IsDatabaseDisposed);
        }
    }
}
