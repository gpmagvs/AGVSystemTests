using AGVSystemCommonNet6;
using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Dispatch;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// VMSystem fix/deadend-no-yield R1 (Fixed By Review's Request)：Map_UMTC_5F 死巷 87–67–69 情境 S6 / S3 / S7 的修正。
    /// 1. 避讓階段 GetPredictPt 回避讓點 (不再誤判「終點被佔」)；請離 no-op 回「未受理」且不 latch；讓車沒動不算成功。
    /// 2. 閒置車擋路 (S7)：IDLE、無訂單、一般點、非虛擬點的擋路車請離一次。
    /// 3. 多車成環 (S3)：每台車的全部衝突車都當等待邊，找得到 3 車環。
    /// 需要 VMSystem PR #40 (fix/deadend-no-yield) 先 merge。
    /// </summary>
    [TestClass]
    public class DeadEndNoYieldR1Tests
    {
        private static MapPoint Pt(int tag, STATION_TYPE type = STATION_TYPE.Normal, bool isVirtual = false) => new MapPoint
        {
            TagNumber = tag, Name = tag.ToString(), StationType = type, IsVirtualPoint = isVirtual, Enable = true,
        };

        // ───────────── 1a. GetPredictPt 避讓階段 ─────────────

        [TestMethod]
        public void PredictPt_AvoidPath_ReturnsAvoidPoint_NotOriginalDestine()
        {
            MapPoint avoidPt = Pt(73), current = Pt(67);
            Assert.IsTrue(NoPathConflicHelper.TryGetAvoidStagePredictPt(VehicleMovementStage.AvoidPath, () => avoidPt, current, out MapPoint? predict));
            Assert.AreEqual(73, predict!.TagNumber, "避讓中預測點是避讓點 73，不是原終點 69");
        }

        [TestMethod]
        public void PredictPt_AvoidPathPark_ReturnsAvoidMoveDestine()
        {
            MapPoint parkEntry = Pt(73), current = Pt(67);
            Assert.IsTrue(NoPathConflicHelper.TryGetAvoidStagePredictPt(VehicleMovementStage.AvoidPath_Park, () => parkEntry, current, out MapPoint? predict));
            Assert.AreEqual(73, predict!.TagNumber);
        }

        [TestMethod]
        public void PredictPt_AvoidPointNull_OrThrows_FallsBackToCurrentPoint()
        {
            MapPoint current = Pt(67);
            Assert.IsTrue(NoPathConflicHelper.TryGetAvoidStagePredictPt(VehicleMovementStage.AvoidPath, () => null, current, out MapPoint? predict));
            Assert.AreEqual(67, predict!.TagNumber);
            Assert.IsTrue(NoPathConflicHelper.TryGetAvoidStagePredictPt(VehicleMovementStage.AvoidPath_Park, null, current, out predict));
            Assert.AreEqual(67, predict!.TagNumber);
            Assert.IsTrue(NoPathConflicHelper.TryGetAvoidStagePredictPt(VehicleMovementStage.AvoidPath, () => throw new NullReferenceException(), current, out predict));
            Assert.AreEqual(67, predict!.TagNumber, "AvoidToPtMoveDestine 丟例外 (AvoidPt 為 null) 時退回目前點");
        }

        [TestMethod]
        public void PredictPt_MoveToDestine_NotAvoidStage_KeepsOriginalLogic()
        {
            foreach (var stage in new[] { VehicleMovementStage.Traveling_To_Destine, VehicleMovementStage.Traveling_To_Source, VehicleMovementStage.Traveling,
                                          VehicleMovementStage.MoveToTurnPoint, VehicleMovementStage.Traveling_To_Region_Wait_Point })
            {
                Assert.IsFalse(NoPathConflicHelper.TryGetAvoidStagePredictPt(stage, () => Pt(73), Pt(67), out MapPoint? predict), stage.ToString());
                Assert.IsNull(predict, "非避讓階段由呼叫端照原邏輯回原終點");
            }
        }

        // ───────────── 1b. 請離 no-op ─────────────

        [TestMethod]
        public void LeaveAwayRequest_TargetExecuting_IsNotAccepted_AndNotLatched()
        {
            Assert.IsTrue(NoPathConflicHelper.IsLeaveAwayRequestNoOp(true, VehicleMovementStage.Traveling_To_Destine));
            Assert.IsTrue(NoPathConflicHelper.IsLeaveAwayRequestNoOp(true, VehicleMovementStage.Traveling));
            Assert.IsFalse(NoPathConflicHelper.ShouldLatchLeaveAwayRequested(NoPathConflicHelper.LeaveAwayRequestResult.IgnoredTargetBusy), "未受理不可鎖住呼叫端旗標");
        }

        [TestMethod]
        public void LeaveAwayRequest_TargetIdle_OrCharging_IsAccepted_AndLatched()
        {
            Assert.IsFalse(NoPathConflicHelper.IsLeaveAwayRequestNoOp(false, VehicleMovementStage.Not_Start_Yet), "目標閒置 → 受理");
            Assert.IsFalse(NoPathConflicHelper.IsLeaveAwayRequestNoOp(true, VehicleMovementStage.WorkingAtChargeStation), "既有行為：充電任務中仍受理");
            Assert.IsTrue(NoPathConflicHelper.ShouldLatchLeaveAwayRequested(NoPathConflicHelper.LeaveAwayRequestResult.Raised));
        }

        [TestMethod]
        public void LeaveAwayRequestArgs_Defaults_KeepOldBehavior()
        {
            var args = new MoveTaskDynamicPathPlanV3.RequestVehicleLeaveWorkStationArgs();
            Assert.IsFalse(args.isRequestIgnored);
            Assert.IsFalse(args.forbidVirtualPointDestine);
        }

        // ───────────── 1c. 讓車沒動不算成功 ─────────────

        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 10, 22, 16, 0, 0, DateTimeKind.Utc);
        }

        [TestMethod]
        public async Task YieldVerify_YielderNotMoved_RecordsFailure_NotSuccess()
        {
            var clock = new Clock();
            var backoff = new AvoidSolveBackoff(null, () => clock.Now);
            string key = AvoidSolveBackoff.PairKey("AGV_001", "AGV_003");
            var snap = new YieldProgressVerifier.Snapshot(67, new[] { 67, 69 });
            bool noProgressCalled = false;

            bool moved = await YieldProgressVerifier.VerifyAsync(() => snap, TimeSpan.FromSeconds(10), backoff, key, () => noProgressCalled = true, _ => Task.CompletedTask);

            Assert.IsFalse(moved);
            Assert.IsTrue(noProgressCalled, "沒動 → 通知換人 (AvoidFailures)");
            Assert.AreEqual(1, backoff.FailureCount(key), "沒動 → 記失敗，不是 RecordSuccess");
            Assert.IsTrue(backoff.ShouldSkip(key, out _), "backoff 介入");
        }

        [TestMethod]
        public async Task YieldVerify_PositionOrTrajectoryChanged_RecordsSuccess()
        {
            var clock = new Clock();
            var backoff = new AvoidSolveBackoff(null, () => clock.Now);
            string key = "k";
            backoff.RecordFailure(key);

            int calls = 0;
            YieldProgressVerifier.Snapshot Take() => calls++ == 0 ? new(67, new[] { 67 }) : new(67, new[] { 67, 87, 73 });
            Assert.IsTrue(await YieldProgressVerifier.VerifyAsync(Take, TimeSpan.Zero, backoff, key, null, _ => Task.CompletedTask), "軌跡變了 (避讓已下發) 算有動");
            Assert.AreEqual(0, backoff.FailureCount(key), "有動 → RecordSuccess 歸零");

            Assert.IsTrue(YieldProgressVerifier.HasYieldProgress(new(67, null), new(87, null)), "位置變了算有動");
            Assert.IsFalse(YieldProgressVerifier.HasYieldProgress(new(67, new[] { 1, 2 }), new(67, new[] { 1, 2 })));
        }

        [TestMethod]
        public void YieldVerify_WaitUsesExistingSetting_Default10Sec()
        {
            Assert.AreEqual(10d, new VMSystem.TrafficControl.Parameters.clsTrafficControlParameters.DeadLockParams().inferConflicVehicleWhenNoPathSeconds);
        }

        // ───────────── 2. 閒置車擋路請離 (S7) ─────────────

        [TestMethod]
        public void IdleBlocker_IdleOnNormalPoint_NoOrder_ShouldBeRequested()
        {
            Assert.IsTrue(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.IDLE, false, STATION_TYPE.Normal, false), "S7：001 IDLE 停在 87");
        }

        [TestMethod]
        public void IdleBlocker_ChargeStation_WorkStation_Down_Executing_Offline_Virtual_NotRequested()
        {
            foreach (var type in new[] { STATION_TYPE.Charge, STATION_TYPE.Charge_Buffer, STATION_TYPE.Charge_STK, STATION_TYPE.EQ, STATION_TYPE.Buffer, STATION_TYPE.Buffer_EQ, STATION_TYPE.STK })
                Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.IDLE, false, type, false), type.ToString());

            Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.DOWN, false, STATION_TYPE.Normal, false), "DOWN");
            Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.RUN, false, STATION_TYPE.Normal, false), "RUN");
            Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.Charging, false, STATION_TYPE.Normal, false), "充電中");
            Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.IDLE, true, STATION_TYPE.Normal, false), "有執行中/排隊中訂單");
            Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(false, clsEnums.MAIN_STATUS.IDLE, false, STATION_TYPE.Normal, false), "offline");
            Assert.IsFalse(NoPathConflicHelper.ShouldRequestIdleBlockerLeave(true, clsEnums.MAIN_STATUS.IDLE, false, STATION_TYPE.Normal, true), "虛擬點 (IsVirtualPoint)");
        }

        [TestMethod]
        public void IdleBlocker_SameTask_SecondTime_NotResent_UntilReset()
        {
            var tracker = new NoPathConflicHelper.IdleBlockerLeaveRequestTracker();
            Assert.IsTrue(tracker.TryMarkRequested("AGV_001"));
            Assert.IsFalse(tracker.TryMarkRequested("AGV_001"), "同一任務第二次不重發");
            Assert.IsTrue(tracker.IsRequested("AGV_001"));
            Assert.IsTrue(tracker.TryMarkRequested("AGV_002"), "不同擋路車各一次");
            Assert.IsFalse(tracker.TryMarkRequested(null));
            Assert.IsFalse(tracker.TryMarkRequested(""));
            tracker.Reset();
            Assert.AreEqual(0, tracker.Count);
            Assert.IsTrue(tracker.TryMarkRequested("AGV_001"), "重新規劃 (:730) Reset 後才可再請");
        }

        // ───────────── 3. 多車成環 (S3) ─────────────

        private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Graph(params (string from, string[] to)[] edges)
            => edges.ToDictionary(e => e.from, e => (IReadOnlyCollection<string>)e.to);

        [TestMethod]
        public void WaitForGraph_S3_ThreeVehicleRing_FoundWithExtraEdges()
        {
            // S3：001@21→87 等 002；002@87→71 等 003；003@71→21 等 001。PathPlaner 依路段順序列衝突車，
            // 001 第一台衝突車其實是 004 (不在等待中)，只取 FirstOrDefault 時圖上沒有環。
            var multi = Graph(("AGV_001", new[] { "AGV_004", "AGV_002" }),
                              ("AGV_002", new[] { "AGV_003", "AGV_001" }),
                              ("AGV_003", new[] { "AGV_001" }));
            var firstOnly = multi.ToDictionary(kp => kp.Key, kp => (string?)kp.Value.First());
            Assert.AreEqual(0, WaitForGraph.FindCycles(firstOnly).Count(c => c.Count >= 3), "舊：只取第一台衝突車 → 找不到 3 車環");

            var cycles = WaitForGraph.FindCycles(multi);
            var ring = cycles.FirstOrDefault(c => c.Count >= 3);
            Assert.IsNotNull(ring, "新：走全部邊 → 找到 3 車環");
            CollectionAssert.AreEqual(new[] { "AGV_001", "AGV_002", "AGV_003" }, ring);

            var order = WaitForGraph.OrderYieldCandidates(ring!, _ => 1);
            Assert.AreEqual("AGV_003", order[0].passer, "讓車 001 讓開後，等 001 的 003 可前進");
        }

        [TestMethod]
        public void WaitForGraph_OnlyTwoVehiclesMutual_NoThreeVehicleRing()
        {
            var cycles = WaitForGraph.FindCycles(Graph(("AGV_001", new[] { "AGV_003" }), ("AGV_003", new[] { "AGV_001", "AGV_009" })));
            Assert.AreEqual(1, cycles.Count);
            CollectionAssert.AreEqual(new[] { "AGV_001", "AGV_003" }, cycles[0]);
            Assert.IsFalse(cycles.Any(c => c.Count >= 3));
        }

        [TestMethod]
        public void WaitForGraph_MultiEdges_EachCycleReportedOnce()
        {
            var g = Graph(("A", new[] { "B", "C", "B", "A", "" }), ("B", new[] { "C", "A" }), ("C", new[] { "A", "B" }));
            var cycles = WaitForGraph.FindCycles(g);
            var keys = cycles.Select(c => string.Join(">", c)).ToList();
            CollectionAssert.AllItemsAreUnique(keys, "同一循環不重複回報");
            // 基本循環：A>B, A>B>C, A>C, A>C>B, B>C
            CollectionAssert.AreEquivalent(new[] { "A>B", "A>B>C", "A>C", "A>C>B", "B>C" }, keys);
            Assert.IsTrue(cycles.All(c => c.Count >= 2), "自己指向自己 / 空名稱不算");
            Assert.AreEqual(2, WaitForGraph.FindCycles(g, maxCycles: 2).Count, "上限保護");
        }

        [TestMethod]
        public void WaitForGraph_MultiEdges_EdgeToNonWaitingVehicleIgnored_AndEmptyInput()
        {
            Assert.AreEqual(0, WaitForGraph.FindCycles(Graph(("A", new[] { "B" }), ("B", new[] { "C" }))).Count);
            Assert.AreEqual(0, WaitForGraph.FindCycles(new Dictionary<string, IReadOnlyCollection<string>>()).Count);
            Assert.AreEqual(0, WaitForGraph.FindCycles((IReadOnlyDictionary<string, IReadOnlyCollection<string>>)null!).Count);
        }

        [TestMethod]
        public void CollectWaitTargets_PrimaryPlusExtras_StaleExtrasDropped()
        {
            CollectionAssert.AreEqual(new[] { "AGV_002", "AGV_004" },
                WaitForGraph.CollectWaitTargets("AGV_001", "AGV_002", "AGV_002", new[] { "AGV_002", "AGV_004", null, "", "AGV_001", "AGV_004" }));
            CollectionAssert.AreEqual(new[] { "AGV_003" },
                WaitForGraph.CollectWaitTargets("AGV_001", "AGV_003", "AGV_002", new[] { "AGV_004" }), "主要衝突車已被其他流程改掉 → 舊的其他衝突車過期");
            Assert.AreEqual(0, WaitForGraph.CollectWaitTargets("AGV_001", null, "AGV_002", new[] { "AGV_004" }).Count, "沒有主要衝突車 = 沒在等");
        }
    }
}
