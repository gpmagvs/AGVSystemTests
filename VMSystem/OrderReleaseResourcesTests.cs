using System.Reflection;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using VMSystem;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.AGV.TaskDispatch.Tasks;
using static AGVSystemCommonNet6.clsEnums;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// 回歸保護：OrderHandlerBase.StartOrder 必須透過 ExecuteOrderAndReleaseResources 啟動訂單，
    /// 以及各 OrderHandler 的 StartOrder 前置檢查失敗而提早 return 時，一定要呼叫 ReleaseOrderResources。
    /// (曾發生 merge develop 後 StartOrder 被改回 `_ = ExecuteOrder()`，使釋放邏輯變成死碼，訂單結束後資料庫連線不再釋放。)
    /// 不需要 SQL Server / HTTP / 真實 AGV：以 DispatchProxy 假 IAGV 與覆寫 virtual 方法隔離外部相依。
    /// </summary>
    [TestClass]
    public class OrderReleaseResourcesTests
    {
        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("等待條件逾時");
                await Task.Delay(10);
            }
        }

        private static IAGV CreateStartableAgv(out FakeAgvProxy proxy)
        {
            var agv = FakeAgvFactory.Create(out proxy);
            proxy.NavigationStateValue ??= new VehicleNavigationState();
            proxy.StatesValue ??= new clsRunningStatus();
            return agv;
        }

        /// <summary>ExecuteOrder 由測試控制何時結束 / 是否丟例外；其餘不碰 DB / HTTP。</summary>
        private class ControlledOrderHandler : OrderHandlerBase
        {
            public readonly TaskCompletionSource<bool> ExecuteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int ExecuteOrderCalls;
            public ControlledOrderHandler() : base(new SemaphoreSlim(1, 1)) { }
            public override ACTION_TYPE OrderAction => ACTION_TYPE.None;
            protected override async Task ExecuteOrder()
            {
                Interlocked.Increment(ref ExecuteOrderCalls);
                await ExecuteGate.Task;
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task StartOrder_OrderFinishes_ReleasesHandlerRunningTaskAndPendingTasks()
        {
            var agv = CreateStartableAgv(out var proxy);
            var handler = new ControlledOrderHandler();
            var pending1 = new MoveToDestineTask();
            var pending2 = new MoveToDestineTask();
            handler.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { pending1, pending2 });
            var running = handler.RunningTask;

            await handler.StartOrder(agv);

            Assert.AreEqual(1, handler.ExecuteOrderCalls, "StartOrder 應啟動 ExecuteOrder");
            Assert.IsFalse(handler.IsDatabaseDisposed, "訂單進行中不可釋放");
            Assert.AreEqual(1, proxy.SubscriberCount("OnMapPointChanged"), "StartOrder 仍要在啟動前訂閱 OnMapPointChanged");

            handler.ExecuteGate.SetResult(true); // 訂單結束

            await WaitUntil(() => handler.IsDatabaseDisposed);
            Assert.IsTrue(running.IsDatabaseDisposed, "RunningTask 的 DB 應已釋放");
            Assert.IsTrue(pending1.IsDatabaseDisposed && pending2.IsDatabaseDisposed, "尚未執行的任務 DB 也要釋放");
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task StartOrder_ExecuteOrderThrows_StillReleases()
        {
            var agv = CreateStartableAgv(out _);
            var handler = new ControlledOrderHandler();
            var running = handler.RunningTask;

            await handler.StartOrder(agv);
            handler.ExecuteGate.SetException(new InvalidOperationException("boom"));

            await WaitUntil(() => handler.IsDatabaseDisposed);
            Assert.IsTrue(running.IsDatabaseDisposed);
        }

        // ───────────── StartOrder 前置檢查失敗而提早 return ─────────────

        /// <summary>_SetOrderAsFaiiureState 覆寫為 no-op (真實實作會連警報中心 / DB)，其餘走真實的 StartOrder 前置檢查。</summary>
        private class LoadHandlerProbe : LoadOrderHandler
        {
            public int FailureCalls;
            public LoadHandlerProbe() : base(new SemaphoreSlim(1, 1)) { }
            protected override void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm, bool bypassOrderTransferedCheck = false)
                => Interlocked.Increment(ref FailureCalls);
        }

        private class MoveToHandlerProbe : MoveToOrderHandler
        {
            public int FailureCalls;
            public MoveToHandlerProbe() : base(new SemaphoreSlim(1, 1)) { }
            protected override void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm, bool bypassOrderTransferedCheck = false)
                => Interlocked.Increment(ref FailureCalls);
        }

        private class ChargeHandlerProbe : ChargeOrderHandler
        {
            public int FailureCalls;
            public ChargeHandlerProbe() : base(new SemaphoreSlim(1, 1)) { }
            protected override void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm, bool bypassOrderTransferedCheck = false)
                => Interlocked.Increment(ref FailureCalls);
        }

        private static void AssertReleased(OrderHandlerBase handler, TaskBase running, params TaskBase[] pending)
        {
            Assert.IsTrue(handler.IsDatabaseDisposed, "前置檢查失敗而提早 return，OrderHandler 的 DB 必須釋放");
            Assert.IsTrue(running.IsDatabaseDisposed, "RunningTask 的 DB 必須釋放");
            foreach (var t in pending)
                Assert.IsTrue(t.IsDatabaseDisposed, "已建立但未執行的任務必須釋放 (其建構時可能已訂閱 AGV 事件)");
        }

        [TestMethod]
        public async Task LoadOrderHandler_StartOrder_PreconditionFails_ReleasesResources()
        {
            // 假 AGV 的 IsAGVHasCargoOrHasCargoID 回傳 false → LoadOrderHandler 判定「車上沒有貨物」而失敗
            var agv = CreateStartableAgv(out _);
            var handler = new LoadHandlerProbe();
            var pending = new MoveToDestineTask();
            handler.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { pending });
            var running = handler.RunningTask;

            await handler.StartOrder(agv);

            Assert.AreEqual(1, handler.FailureCalls);
            AssertReleased(handler, running, pending);
        }

        [TestMethod]
        public async Task MoveToOrderHandler_StartOrder_ForbiddenDestine_ReleasesResources()
        {
            var agv = CreateStartableAgv(out _);
            var originalMap = StaMap.Map;
            try
            {
                StaMap.Map = new Map { TagNoStopOfForkAGV = new List<int> { 7 } };
                var handler = new MoveToHandlerProbe();
                handler.OrderData.To_Station = "7";
                var pending = new MoveToDestineTask();
                handler.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { pending });
                var running = handler.RunningTask;

                await handler.StartOrder(agv);

                Assert.AreEqual(1, handler.FailureCalls);
                AssertReleased(handler, running, pending);
            }
            finally
            {
                StaMap.Map = originalMap;
            }
        }

        [TestMethod]
        public async Task ChargeOrderHandler_StartOrder_CargoOnVehicle_ReleasesResources()
        {
            var agv = CreateStartableAgv(out var proxy);
            proxy.StatesValue = new clsRunningStatus { Cargo_Status = 1 };
            var handler = new ChargeHandlerProbe();
            var pending = new MoveToDestineTask();
            handler.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { pending });
            var running = handler.RunningTask;

            await handler.StartOrder(agv);

            Assert.AreEqual(1, handler.FailureCalls);
            AssertReleased(handler, running, pending);
        }

        [TestMethod]
        public void ReleaseOrderResources_IsIdempotent_AndClearsWhatBareDisposeDoesNot()
        {
            // 說明為何不能只 Dispose()：Dispose 不會碰 SequenceTaskQueue / RunningTask
            var bare = new ControlledOrderHandler();
            var bareRunning = bare.RunningTask;
            var barePending = new MoveToDestineTask();
            bare.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { barePending });
            bare.Dispose();
            Assert.IsTrue(bare.IsDatabaseDisposed);
            Assert.IsFalse(bareRunning.IsDatabaseDisposed, "裸 Dispose 不會釋放 RunningTask");
            Assert.IsFalse(barePending.IsDatabaseDisposed, "裸 Dispose 不會釋放尚未執行的任務");

            var h = new ControlledOrderHandler();
            var running = h.RunningTask;
            var pending = new MoveToDestineTask();
            h.SequenceTaskQueue = new Queue<TaskBase>(new TaskBase[] { pending });
            // ReleaseOrderResources 為 internal (VMSystem 未開放 InternalsVisibleTo)，以反射呼叫
            var release = typeof(OrderHandlerBase).GetMethod("ReleaseOrderResources", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsNotNull(release, "ReleaseOrderResources 必須存在 (internal)");
            Assert.IsFalse(release.IsPublic, "ReleaseOrderResources 應為 internal，不對外公開");
            release.Invoke(h, null);
            release.Invoke(h, null);
            release.Invoke(h, null);
            Assert.IsTrue(h.IsDatabaseDisposed && running.IsDatabaseDisposed && pending.IsDatabaseDisposed);
        }
    }
}
