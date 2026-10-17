using System.Reflection;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;
using VMSystem.Dispatch.Regions;
using RegionState = VMSystem.Dispatch.Regions.clsRegionControlState;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// RegionManager (報告 F3)：
    /// - 「是否可進入」的評估不再每 10 ms 一次，改為 200 ms 保底輪詢 + 事件 (RequestEvaluate) 立即喚醒
    /// - 等待進入區域的車輛以 RegisterWaitForSingleObject 等待放行訊號 (不佔執行緒池執行緒)，喚醒語意不變
    /// - 每個等待狀態的 ManualResetEvent 在最後一位等待者離開時 Dispose；Dispose 後的 Signal 不會丟例外
    /// - 等待結束一律取消 agv.OnTaskCancel 訂閱
    /// 不需要 SQL Server / 真實 AGV。
    /// </summary>
    [TestClass]
    public class RegionManagerSignalTests
    {
        private int _savedPoll;
        private int _savedSpacing;
        private Dictionary<MapRegion, RegionState> _savedStates = new();

        [TestInitialize]
        public void Init()
        {
            _savedPoll = RegionState.PollIntervalMs;
            _savedSpacing = RegionState.MinEvaluateSpacingMs;
            _savedStates = RegionManager.RegionsStates;
        }

        [TestCleanup]
        public void Cleanup()
        {
            RegionState.PollIntervalMs = _savedPoll;
            RegionState.MinEvaluateSpacingMs = _savedSpacing;
            RegionManager.RegionsStates = _savedStates;
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("等待條件逾時");
                await Task.Delay(5);
            }
        }

        /// <summary>容量 0 → 永遠「不可進入」，用來讓等待者真的卡住</summary>
        private static MapRegion NeverEnterableRegion(string name = "R-test") => new MapRegion { Name = name, MaxVehicleCapacity = 0 };

        private static IAGV CreateAgv(string name, out FakeAgvProxy proxy)
        {
            var agv = FakeAgvFactory.Create(out proxy, name);
            proxy.NavigationStateValue = new VehicleNavigationState();
            return agv;
        }

        private static Task CallStartWait(IAGV agv, MapRegion region, CancellationToken token)
        {
            var m = typeof(RegionManager).GetMethod("StartWaitToEntryRegion", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (Task)m.Invoke(null, new object[] { agv, region, token })!;
        }

        // ───────────────────────── 輪詢頻率 ─────────────────────────

        [TestMethod]
        public async Task Watcher_DefaultPollInterval_IsAtLeast200ms_NotTenMs()
        {
            Assert.IsTrue(RegionState.PollIntervalMs >= 200, $"保底輪詢間隔 {RegionState.PollIntervalMs} ms 應 >= 200");

            var state = new RegionState(NeverEnterableRegion());
            try
            {
                await WaitUntil(() => state.EvaluationCount >= 1);       // 建立後第一次評估立即執行
                long start = state.EvaluationCount;
                await Task.Delay(1000);
                long evaluations = state.EvaluationCount - start;
                // 舊版 Task.Delay(10)：1 秒約 90~100 次。現在 200 ms 輪詢：約 5 次
                Assert.IsTrue(evaluations <= 8, $"1 秒內評估了 {evaluations} 次 (舊版約 100 次)，應 <= 8");
                Assert.IsTrue(evaluations >= 2, $"保底輪詢仍應持續運作，1 秒只評估 {evaluations} 次");
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task Watcher_RequestEvaluate_WakesImmediately_WithoutWaitingForPoll()
        {
            RegionState.PollIntervalMs = 10_000;               // 保底輪詢拉到 10 秒，排除輪詢的影響
            RegionState.MinEvaluateSpacingMs = 20;
            var state = new RegionState(NeverEnterableRegion());
            try
            {
                await WaitUntil(() => state.EvaluationCount >= 1);
                await Task.Delay(300);                                    // 讓初始喚醒的 spacing 結束，進入長等待
                long before = state.EvaluationCount;

                state.RequestEvaluate();
                await WaitUntil(() => state.EvaluationCount > before, 1500);

                // 多次要求會合併，而不是每次都評估
                long mid = state.EvaluationCount;
                for (int i = 0; i < 50; i++) state.RequestEvaluate();
                await Task.Delay(400);
                long burst = state.EvaluationCount - mid;
                Assert.IsTrue(burst >= 1 && burst <= 5, $"50 次要求應合併成少數幾次評估，實際 {burst}");
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task Watcher_Dispose_StopsTheLoopPromptly()
        {
            RegionState.PollIntervalMs = 10_000;
            var state = new RegionState(NeverEnterableRegion());
            await WaitUntil(() => state.EvaluationCount >= 1);
            await Task.Delay(300);
            state.Dispose();
            await Task.Delay(300);
            long after = state.EvaluationCount;
            await Task.Delay(300);
            Assert.AreEqual(after, state.EvaluationCount, "Dispose 後監看迴圈應立刻結束 (不必等到 10 秒保底輪詢)");
            state.RequestEvaluate();                                     // Dispose 後呼叫不可丟例外
        }

        // ───────────────────────── 等待狀態與 ManualResetEvent ─────────────────────────

        [TestMethod]
        public void WaitingState_LastWaiterLeaving_DisposesMre_AndSignalAfterwardsIsSafe()
        {
            var region = NeverEnterableRegion();
            var state = new RegionState(region);
            try
            {
                var agv = CreateAgv("A1", out _);
                var w1 = state.JoinWaitingForEnter(agv, CancellationToken.None);
                var w2 = state.JoinWaitingForEnter(agv, CancellationToken.None);   // 同一台車再次加入：沿用同一個狀態
                Assert.AreSame(w1, w2);
                Assert.AreEqual(2, w1.WaiterCount);

                w1.LeaveWait();
                Assert.IsFalse(w1.IsDisposed, "還有一位等待者時不可釋放 ManualResetEvent");
                Assert.IsTrue(w1.Signal());

                w1.LeaveWait();
                Assert.IsTrue(w1.IsDisposed);
                Assert.IsTrue(w1.allowEnterSignal.SafeWaitHandle.IsClosed, "最後一位離開後 ManualResetEvent 必須已 Dispose");
                Assert.IsFalse(w1.Signal(), "Dispose 之後 Signal 回 false 而不是丟 ObjectDisposedException");
                w1.LeaveWait();                                                      // 重複呼叫也安全
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public void WaitingState_DisposedStateIsReplacedByFreshOneOnNextJoin()
        {
            var state = new RegionState(NeverEnterableRegion());
            try
            {
                var agv = CreateAgv("A1", out _);
                var first = state.JoinWaitingForEnter(agv, CancellationToken.None);
                first.LeaveWait();
                Assert.IsTrue(first.IsDisposed);

                var second = state.JoinWaitingForEnter(agv, CancellationToken.None);
                Assert.AreNotSame(first, second);
                Assert.IsFalse(second.IsDisposed);
                Assert.AreEqual(1, second.WaiterCount);
                second.LeaveWait();
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task WaitForSignalAsync_CompletesOnSignal_AndImmediatelyWhenAlreadySetOrDisposed()
        {
            var state = new RegionState(NeverEnterableRegion());
            try
            {
                var agv = CreateAgv("A1", out _);
                var w = state.JoinWaitingForEnter(agv, CancellationToken.None);

                var wait = w.WaitForSignalAsync();
                await Task.Delay(150);
                Assert.IsFalse(wait.IsCompleted, "沒放行前不可返回");
                w.Signal();
                await wait.WaitAsync(TimeSpan.FromSeconds(2));

                await w.WaitForSignalAsync().WaitAsync(TimeSpan.FromSeconds(2));    // 已 Set：立即返回 (同 WaitOne 語意)

                w.LeaveWait();                                                        // Dispose
                await w.WaitForSignalAsync().WaitAsync(TimeSpan.FromSeconds(2));    // 已 Dispose：立即返回
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task ManyWaiters_DoNotBlockThreadPoolThreads_AndAllWakeOnSignal()
        {
            var state = new RegionState(NeverEnterableRegion());
            try
            {
                const int n = 200;
                var waiters = new List<RegionState.clsVehicleWaitingState>();
                for (int i = 0; i < n; i++)
                    waiters.Add(state.JoinWaitingForEnter(CreateAgv("A" + i, out _), CancellationToken.None));

                int threadsBefore = ThreadPool.ThreadCount;
                var waits = waiters.Select(w => w.WaitForSignalAsync()).ToList();
                await Task.Delay(500);
                int threadsDuring = ThreadPool.ThreadCount;

                Assert.IsTrue(waits.All(t => !t.IsCompleted));
                // 舊版每位等待者都在 Task.Run 裡 WaitOne() 佔住一條執行緒 (200 位 => 執行緒池被迫成長)；新版不佔用
                Assert.IsTrue(threadsDuring - threadsBefore < 20, $"200 位等待者讓執行緒池從 {threadsBefore} 長到 {threadsDuring} 條");

                foreach (var w in waiters) w.Signal();
                await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var w in waiters) w.LeaveWait();
                Assert.IsTrue(waiters.All(w => w.allowEnterSignal.SafeWaitHandle.IsClosed), "每一個 ManualResetEvent 都必須被 Dispose");
            }
            finally { state.Dispose(); }
        }

        // ───────────────────────── StartWaitToEntryRegion 端到端 ─────────────────────────

        [TestMethod]
        public async Task StartWait_BlocksUntilSignal_ThenDisposesMre_RemovesWaiter_AndUnsubscribesTaskCancel()
        {
            var region = NeverEnterableRegion();
            var state = new RegionState(region);
            RegionManager.RegionsStates = new Dictionary<MapRegion, RegionState> { [region] = state };
            try
            {
                var agv = CreateAgv("A1", out var proxy);
                var wait = CallStartWait(agv, region, CancellationToken.None);

                await WaitUntil(() => state.WaitingForEnterVehicles.ContainsKey(agv));
                var waiting = state.WaitingForEnterVehicles[agv];
                await WaitUntil(() => proxy.SubscriberCount("OnTaskCancel") == 1);
                await Task.Delay(200);
                Assert.IsFalse(wait.IsCompleted, "區域不可進入時必須一直等待");

                waiting.Signal();                                       // 放行 (等同區域可進入 / 避讓 / 取消)
                await wait.WaitAsync(TimeSpan.FromSeconds(3));

                Assert.IsTrue(waiting.IsDisposed);
                Assert.IsTrue(waiting.allowEnterSignal.SafeWaitHandle.IsClosed, "等待結束後 ManualResetEvent 必須已 Dispose");
                Assert.IsFalse(state.WaitingForEnterVehicles.ContainsKey(agv), "等待者必須被移除");
                Assert.AreEqual(0, proxy.SubscriberCount("OnTaskCancel"), "等待結束後必須取消 OnTaskCancel 訂閱 (舊版正常放行時 handler 會一直留在事件上)");
                Assert.IsFalse(agv.NavigationState.RegionControlState.TrySignalAllowEnter(), "已釋放的訊號不可再 Set，且不丟例外");
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task StartWait_TaskCancelEvent_WakesWaiter()
        {
            var region = NeverEnterableRegion();
            var state = new RegionState(region);
            RegionManager.RegionsStates = new Dictionary<MapRegion, RegionState> { [region] = state };
            try
            {
                var agv = CreateAgv("A1", out var proxy);
                var wait = CallStartWait(agv, region, CancellationToken.None);
                await WaitUntil(() => proxy.SubscriberCount("OnTaskCancel") == 1);
                await Task.Delay(100);
                Assert.IsFalse(wait.IsCompleted);

                ((EventHandler<string>)proxy.GetEvent("OnTaskCancel")!).Invoke(agv, "task-1");
                await wait.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.AreEqual(0, proxy.SubscriberCount("OnTaskCancel"));
                Assert.IsFalse(state.WaitingForEnterVehicles.ContainsKey(agv));
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task StartWait_AvoidRaisedSignalThroughNavigationState_WakesWaiter()
        {
            // MoveTaskDynamicPathPlanV2 在避讓時透過 NavigationState.RegionControlState 放行等待中的車輛
            var region = NeverEnterableRegion();
            var state = new RegionState(region);
            RegionManager.RegionsStates = new Dictionary<MapRegion, RegionState> { [region] = state };
            try
            {
                var agv = CreateAgv("A1", out var proxy);
                var wait = CallStartWait(agv, region, CancellationToken.None);
                await WaitUntil(() => agv.NavigationState.RegionControlState.CurrentAllowEnterRegionSignal != null);
                Assert.IsFalse(wait.IsCompleted);

                Assert.IsTrue(agv.NavigationState.RegionControlState.TrySignalAllowEnter());
                await wait.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task StartWait_RegionAlreadyEnterable_ReturnsImmediately()
        {
            var region = new MapRegion { Name = "R-open", MaxVehicleCapacity = 5 };
            var state = new RegionState(region);
            RegionManager.RegionsStates = new Dictionary<MapRegion, RegionState> { [region] = state };
            try
            {
                await WaitUntil(() => state.IsEnterable);
                var agv = CreateAgv("A1", out var proxy);
                await CallStartWait(agv, region, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.AreEqual(0, proxy.SubscriberCount("OnTaskCancel"));
            }
            finally { state.Dispose(); }
        }

        [TestMethod]
        public async Task SetAGVNoWaitForEnteryRegion_ReleasesWaiter()
        {
            var region = NeverEnterableRegion();
            var state = new RegionState(region);
            RegionManager.RegionsStates = new Dictionary<MapRegion, RegionState> { [region] = state };
            try
            {
                var agv = CreateAgv("A1", out _);
                var wait = CallStartWait(agv, region, CancellationToken.None);
                await WaitUntil(() => state.WaitingForEnterVehicles.ContainsKey(agv));
                await Task.Delay(100);
                Assert.IsFalse(wait.IsCompleted);

                var m = typeof(RegionManager).GetMethod("SetAGVNoWaitForEnteryRegion", BindingFlags.NonPublic | BindingFlags.Static)!;
                m.Invoke(null, new object[] { agv });
                await wait.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { state.Dispose(); }
        }
    }
}
