using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.MAP;
using VMSystem;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.AGV.TaskDispatch.Tasks;
using static AGVSystemCommonNet6.clsEnums;

namespace AGVSystemTests.VMSystemTests
{
    [TestClass]
    public class EventSubscriptionLeakTests
    {
        private const string DownEvent = "OnAGVStatusDown";
        private const string MapPointEvent = "OnMapPointChanged";

        /// <summary>只用來驗證 TaskBase 訂閱/取消訂閱 AGV DOWN 事件的最小任務。</summary>
        private class ProbeTask : ChargeTask
        {
            public int DownHandlerCalls;
            public ProbeTask(IAGV agv, clsTaskDto dto) : base(agv, dto, new SemaphoreSlim(1, 1)) { }
            public void Subscribe() => SubscribeAGVStatusDown();
            public void Unsubscribe() => UnsubscribeAGVStatusDown();
            protected override void HandleAGVStatusDown(object? sender, EventArgs e)
            {
                // 不呼叫 base：base 會 Task.Run 呼叫 Agv.CancelTaskAsync，與本測試無關
                Interlocked.Increment(ref DownHandlerCalls);
                UnsubscribeAGVStatusDown();
            }
        }

        private static ProbeTask NewProbe(out FakeAgvProxy proxy, string taskName = "T-1")
        {
            var agv = FakeAgvFactory.Create(out proxy);
            return new ProbeTask(agv, new clsTaskDto { TaskName = taskName });
        }

        private static void RaiseDown(FakeAgvProxy proxy)
            => ((EventHandler?)proxy.GetEvent(DownEvent))?.Invoke(proxy, EventArgs.Empty);

        // ───────────── Red 1：TaskBase / OnAGVStatusDown ─────────────

        [TestMethod]
        public void TaskBase_Subscribe_Twice_OnlySubscribesOnce()
        {
            var task = NewProbe(out var proxy);
            task.Subscribe();
            task.Subscribe();
            Assert.AreEqual(1, proxy.SubscriberCount(DownEvent), "-= 再 += 應避免重複訂閱");

            RaiseDown(proxy);
            Assert.AreEqual(1, task.DownHandlerCalls, "DOWN 時同一任務的 handler 只能被呼叫一次");
        }

        [TestMethod]
        public void TaskBase_Dispose_RemovesDownHandler_WhenTaskEndsWithoutDown()
        {
            var task = NewProbe(out var proxy);
            task.Subscribe();
            Assert.AreEqual(1, proxy.SubscriberCount(DownEvent));

            task.Dispose(); // 任務正常結束 / 取消 / 失敗 都會走到 Dispose

            Assert.AreEqual(0, proxy.SubscriberCount(DownEvent), "Dispose 後不得再殘留 handler");
            RaiseDown(proxy);
            Assert.AreEqual(0, task.DownHandlerCalls, "已結束的任務不得再被 DOWN 事件喚醒");
        }

        [TestMethod]
        public void TaskBase_Dispose_IsIdempotent_AndSafeWhenNeverSubscribed()
        {
            var task = NewProbe(out var proxy);
            task.Dispose();
            task.Dispose();
            Assert.AreEqual(0, proxy.SubscriberCount(DownEvent));
        }

        [TestMethod]
        public void TaskBase_ManyFinishedTasks_DoNotAccumulateHandlers_AndDownFiresNoOldTask()
        {
            // 模擬「一天跑很多任務、都沒 DOWN」：每個任務結束 (Dispose) 後 AGV 上不得累積 handler
            var agv = FakeAgvFactory.Create(out var proxy);
            var tasks = new List<ProbeTask>();
            for (int i = 0; i < 50; i++)
            {
                var t = new ProbeTask(agv, new clsTaskDto { TaskName = $"T-{i}" });
                t.Subscribe();
                t.Dispose();
                tasks.Add(t);
            }
            Assert.AreEqual(0, proxy.SubscriberCount(DownEvent));

            var running = new ProbeTask(agv, new clsTaskDto { TaskName = "T-running" });
            running.Subscribe();
            RaiseDown(proxy);

            Assert.AreEqual(1, running.DownHandlerCalls, "只有當前任務會處理 DOWN");
            Assert.IsTrue(tasks.All(t => t.DownHandlerCalls == 0), "舊任務不得被 DOWN 觸發 (否則會對舊任務名重複廣播取消)");
        }

        [TestMethod]
        public void TaskBase_DownHandler_StillUnsubscribesItself()
        {
            var task = NewProbe(out var proxy);
            task.Subscribe();
            RaiseDown(proxy);
            Assert.AreEqual(1, task.DownHandlerCalls);
            Assert.AreEqual(0, proxy.SubscriberCount(DownEvent));
        }

        [TestMethod]
        public void TaskBase_SubscribeAfterDispose_CanStillBeUnsubscribedByFinally()
        {
            // DischargeTask 在 CancelTask(→Dispose) 之後仍可能執行到訂閱那一行，
            // 所以各任務 SendTaskToAGV 的 finally 也要呼叫 Unsubscribe，不能只靠 Dispose。
            var task = NewProbe(out var proxy);
            task.Dispose();
            task.Subscribe();
            Assert.AreEqual(1, proxy.SubscriberCount(DownEvent));
            task.Unsubscribe();
            Assert.AreEqual(0, proxy.SubscriberCount(DownEvent));
        }

        // ───────────── Red 2：OrderHandlerBase / OnMapPointChanged ─────────────

        private class ProbeOrderHandler : OrderHandlerBase
        {
            public ProbeOrderHandler() : base(new SemaphoreSlim(1, 1)) { }
            public override ACTION_TYPE OrderAction => ACTION_TYPE.None;
            public Func<Task<double>> Prepare = () => Task.FromResult(0d);
            // StartOrder 內的 `_ = ExecuteOrder()` 不要在背景真的跑 (會與測試搶訂閱數量)；測試要跑時用 RunExecuteOrder 明確呼叫基底實作
            protected override Task ExecuteOrder() => Task.CompletedTask;
            public Task RunExecuteOrder() => base.ExecuteOrder();
            // 測試不連 DB / HTTP：準備階段由測試自行決定成功或失敗
            protected override Task<double> PrepareOrderExecution() => Prepare();
            // 以下三個 virtual 蓋掉會連 DB / 警報中心的行為，讓 ExecuteOrder 在假 AGV 上能快速、確定地跑完 finally
            protected override Task _SetOrderAsFinishState(bool isLoadAtTransferStation = false)
                => throw new InvalidOperationException("simulated failure inside ExecuteOrder try");
            protected override void _SetOrderAsFaiiureState(string FailReason, ALARMS alarm, bool bypassOrderTransferedCheck = false) { }
            protected override Task ActionsWhenOrderCancle() => Task.CompletedTask;
        }

        /// <summary>
        /// 讓假 AGV 具備 StartOrder 走到「訂閱」所需的最小狀態 (NavigationState / states)，
        /// 之後呼叫 StartOrder；StartOrder 只有在訂閱之後才會丟例外時，這裡吞掉。
        /// </summary>
        private static async Task TryStartOrder(ProbeOrderHandler handler, IAGV agv)
        {
            var proxy = (FakeAgvProxy)(object)agv;
            proxy.NavigationStateValue ??= new VehicleNavigationState();
            proxy.StatesValue ??= new clsRunningStatus();
            try { await handler.StartOrder(agv); }
            catch (Exception) { }
        }

        [TestMethod]
        public async Task OrderHandler_StartOrder_FailsBeforeExecuteOrder_LeavesNoSubscription()
        {
            // 訂閱已移到 `_ = ExecuteOrder()` 正前方：前面的步驟 (ClearEstimatePathInfo / TrajectoryRecorder) 丟例外時不得留下訂閱。
            // 假 AGV 的 NavigationState 預設為 null，StartOrder 會在 ClearEstimatePathInfo 丟 NullReferenceException。
            var agv = FakeAgvFactory.Create(out var proxy);
            var handler = new ProbeOrderHandler();
            await Assert.ThrowsExceptionAsync<NullReferenceException>(() => handler.StartOrder(agv));
            Assert.AreEqual(0, proxy.SubscriberCount(MapPointEvent), "StartOrder 在訂閱前失敗，不得殘留訂閱");
        }

        [TestMethod]
        public async Task OrderHandler_StartOrder_Twice_OnlySubscribesOnce()
        {
            var agv = FakeAgvFactory.Create(out var proxy);
            var handler = new ProbeOrderHandler();
            await TryStartOrder(handler, agv);
            await TryStartOrder(handler, agv);
            Assert.AreEqual(1, proxy.SubscriberCount(MapPointEvent), "StartOrder 訂閱前先 -= 再 +=");
        }

        [TestMethod]
        public async Task OrderHandler_StartOrder_OnAnotherAgv_UnsubscribesFromPreviousAgv()
        {
            var agv1 = FakeAgvFactory.Create(out var proxy1, "A1");
            var agv2 = FakeAgvFactory.Create(out var proxy2, "A2");
            var handler = new ProbeOrderHandler();
            await TryStartOrder(handler, agv1);
            await TryStartOrder(handler, agv2);
            Assert.AreEqual(0, proxy1.SubscriberCount(MapPointEvent), "換車後不得再掛在舊車上");
            Assert.AreEqual(1, proxy2.SubscriberCount(MapPointEvent));
        }


        [TestMethod]
        [Timeout(30000)]
        public async Task OrderHandler_ExecuteOrder_PrepareFails_UnsubscribesMapPointChanged()
        {
            // ExecuteOrder 在進入主 try 之前就失敗 (SetOrderProgress / SyncTraffic / SetRunning 丟例外)：finally 不會跑
            var agv = FakeAgvFactory.Create(out var proxy);
            var handler = new ProbeOrderHandler { Prepare = () => throw new InvalidOperationException("prepare failed") };
            await TryStartOrder(handler, agv);
            Assert.AreEqual(1, proxy.SubscriberCount(MapPointEvent));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => handler.RunExecuteOrder());

            Assert.AreEqual(0, proxy.SubscriberCount(MapPointEvent), "準備階段失敗後，舊 OrderHandler 不得殘留在 AGV 上");
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task OrderHandler_ExecuteOrder_TransferredOrder_StillUnsubscribes_EvenIfLaterCleanupThrows()
        {
            // 被轉移的單 (isVehicleAssignedChanged) 讓 UpdateMileageAndTagsTracking 提前 return，
            // 另外 finally 後段 (VehicleUnassignedReport / NavigationState / trajectoryRecorder) 也可能丟例外：
            // 假 AGV 沒有 NavigationState，finally 內 ClearEstimatePathInfo 會 NullReferenceException。
            // 取消訂閱必須在 finally 最前面，不能依賴後面任何一步。
            var agv = FakeAgvFactory.Create(out var proxy);
            var handler = new ProbeOrderHandler();
            handler.OrderData.isVehicleAssignedChanged = true;
            await TryStartOrder(handler, agv);
            Assert.AreEqual(1, proxy.SubscriberCount(MapPointEvent));
            // StartOrder 之後把 NavigationState 拿掉，讓 finally 後段 (ClearEstimatePathInfo) 丟 NullReferenceException
            proxy.NavigationStateValue = null;

            try { await handler.RunExecuteOrder(); } catch (Exception) { /* finally 後段丟例外是預期的 */ }

            Assert.AreEqual(0, proxy.SubscriberCount(MapPointEvent), "任務轉移 / 清理丟例外時，handler 仍須已取消訂閱");
        }
    }
}
