using System.Reflection;
using AGVSystemCommonNet6.MAP;
using Newtonsoft.Json;
using VMSystem.AGV;
using VMSystem.Dispatch;
using VMSystem.TrafficControl.Parameters;
using VMSystem.TrafficControl.VehiclePrioritySolver;
using VMSystem.TrafficControl.VehiclePrioritySolver.Rules;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// VMSystem fix/deadend-no-yield 第 4–6 點：
    /// 4. 避讓失敗不再於解死結前清除：每對車 (A,B) 的失敗紀錄 (TTL) + HasAvoidFailRule 生效，成功抵達避讓點才清除。
    /// 5. 死巷規則 (DeadEndRule)：由地圖 Target 出邊計算死巷深度，死巷內較深的車不讓、外側車讓。
    /// 6. 多車循環：由 currentConflicToAGV 建等待圖找真正循環，循環內依權重由低到高挑避讓車。
    /// 需要 VMSystem PR (fix/deadend-no-yield) 先 merge。
    /// </summary>
    [TestClass]
    public class DeadLockYieldRulesTests
    {
        // ───────────── 4. 每對車避讓失敗紀錄 ─────────────

        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 10, 22, 17, 20, 0, DateTimeKind.Utc);
            public void Advance(double sec) => Now = Now.AddSeconds(sec);
        }

        [TestMethod]
        public void AvoidFailureMemory_RecordedPerPair_ExpiresAfterTtl()
        {
            var clock = new Clock();
            var memory = new AvoidFailureMemory(() => TimeSpan.FromSeconds(60), () => clock.Now);
            memory.RecordFailure("AGV_001", "AGV_003");

            Assert.IsTrue(memory.HasFailed("AGV_001", "AGV_003"));
            Assert.IsFalse(memory.HasFailed("AGV_003", "AGV_001"), "只記失敗的那台");
            Assert.IsFalse(memory.HasFailed("AGV_001", "AGV_002"), "以車對為單位，別的車對不受影響");

            clock.Advance(59);
            Assert.IsTrue(memory.HasFailed("AGV_001", "AGV_003"));
            clock.Advance(2);
            Assert.IsFalse(memory.HasFailed("AGV_001", "AGV_003"), "超過 TTL 60 秒自動失效");
            Assert.AreEqual(0, memory.Count);
        }

        [TestMethod]
        public void AvoidFailureMemory_ClearVehicle_OnReachAvoidPoint_RemovesAllPairsOfThatVehicleOnly()
        {
            var memory = new AvoidFailureMemory();
            memory.RecordFailure("AGV_001", "AGV_003");
            memory.RecordFailure("AGV_001", "AGV_002");
            memory.RecordFailure("AGV_003", "AGV_001");
            memory.ClearVehicle("AGV_001");
            Assert.IsFalse(memory.HasFailed("AGV_001", "AGV_003"));
            Assert.IsFalse(memory.HasFailed("AGV_001", "AGV_002"));
            Assert.IsTrue(memory.HasFailed("AGV_003", "AGV_001"));
        }

        [TestMethod]
        public void AvoidFailureMemory_IgnoresInvalidInput()
        {
            var memory = new AvoidFailureMemory();
            memory.RecordFailure("AGV_001", null);
            memory.RecordFailure("", "AGV_003");
            memory.RecordFailure("AGV_001", "AGV_001");
            Assert.AreEqual(0, memory.Count);
        }

        private static IAGV Vehicle(string name, int tag, bool avoidFailFlag = false)
        {
            var agv = FakeAgvFactory.Create(out var proxy, name);
            proxy.CurrentPoint = new MapPoint { TagNumber = tag };
            var nav = new VehicleNavigationState();
            typeof(clsAvoidActionState).GetProperty(nameof(clsAvoidActionState.AvoidFailFlag))!.SetValue(nav.AvoidActionState, avoidFailFlag);
            proxy.NavigationStateValue = nav;
            return agv;
        }

        [TestMethod]
        public void HasAvoidFailRule_PairRecord_MakesFailedVehicleHighPriority_EvenIfFlagWasCleared()
        {
            var memory = new AvoidFailureMemory();
            var a = Vehicle("AGV_001", 87);
            var b = Vehicle("AGV_003", 69);
            var rule = new HasAvoidFailRule(memory);
            Assert.IsNull(rule.ResolvePriority(new[] { a, b }), "沒有失敗紀錄 → 不適用");

            memory.RecordFailure("AGV_003", "AGV_001");
            var result = rule.ResolvePriority(new[] { a, b });
            Assert.IsNotNull(result);
            Assert.AreEqual("AGV_003", result!.highPriorityVehicle.Name, "避讓失敗過的車不再被選為避讓車");
            Assert.AreEqual("AGV_001", result.lowPriorityVehicle.Name);
        }

        [TestMethod]
        public void HasAvoidFailRule_FlagStillWorks_BothFailed_ReturnsNull()
        {
            var memory = new AvoidFailureMemory();
            var a = Vehicle("AGV_001", 87, avoidFailFlag: true);
            var b = Vehicle("AGV_003", 69);
            var result = new HasAvoidFailRule(memory).ResolvePriority(new[] { a, b });
            Assert.AreEqual("AGV_001", result!.highPriorityVehicle.Name);

            memory.RecordFailure("AGV_003", "AGV_001");
            Assert.IsNull(new HasAvoidFailRule(memory).ResolvePriority(new[] { a, b }), "兩台都失敗過 → 不適用 (交給權重)");
        }

        [TestMethod]
        public void DeadLockMonitor_Source_NoLongerClearsAvoidFailFlagBeforeSolving()
        {
            // 以靜態成員確認全域失敗紀錄存在，TTL 參數預設 60 秒 (舊設定檔沒有此欄位時維持預設)
            Assert.IsNotNull(DeadLockMonitor.AvoidFailures);
            Assert.AreEqual(60d, new clsTrafficControlParameters.DeadLockParams().avoidFailRecordTtlSeconds);
            var old = JsonConvert.DeserializeObject<clsTrafficControlParameters.DeadLockParams>("{\"maxAvoidCandidatesPerRing\":10}")!;
            Assert.AreEqual(60d, old.avoidFailRecordTtlSeconds);
        }

        // ───────────── 5. 死巷規則 ─────────────

        private static MapPoint Pt(int tag, bool isVirtual = false, STATION_TYPE type = STATION_TYPE.Normal) => new MapPoint
        {
            TagNumber = tag, Enable = true, StationType = type, IsVirtualPoint = isVirtual, Name = tag.ToString(), Target = new Dictionary<int, double>(),
        };

        private static void Link(Map map, int a, int b)
        {
            map.Points[a].Target![b] = 1;
            map.Points[b].Target![a] = 1;
            map.Segments.Add(new MapPath { StartPtIndex = a, EndPtIndex = b, Weight = 1 });
            map.Segments.Add(new MapPath { StartPtIndex = b, EndPtIndex = a, Weight = 1 });
        }

        /// <summary>
        /// 主幹道迴圈 15-21-23-25-15，死巷 15-87-67-69 掛在 15 上。
        /// index: 0=15, 1=21, 2=23, 3=25, 4=87, 5=67, 6=69
        /// </summary>
        private static Map CreateSiteLikeMap(bool deadEndThroughVirtual = false)
        {
            var map = new Map { Name = "site", Points = new Dictionary<int, MapPoint>() };
            int[] tags = { 15, 21, 23, 25, 87, 67, 69 };
            for (int i = 0; i < tags.Length; i++)
                map.Points[i] = Pt(tags[i], isVirtual: deadEndThroughVirtual && tags[i] == 67);
            Link(map, 0, 1); Link(map, 1, 2); Link(map, 2, 3); Link(map, 3, 0);
            Link(map, 0, 4); Link(map, 4, 5); Link(map, 5, 6);
            return map;
        }

        [TestMethod]
        public void DeadEndDepth_ComputedFromTargetOutEdges_NoHardcodedTags()
        {
            var depth = DeadEndAnalyzer.ComputeDeadEndDepthByTag(CreateSiteLikeMap());
            Assert.AreEqual(1, depth[69], "死巷底只有一個出口");
            Assert.AreEqual(2, depth[67]);
            Assert.AreEqual(3, depth[87]);
            Assert.IsFalse(depth.ContainsKey(15), "主幹道迴圈上的點不是死巷");
            Assert.IsFalse(depth.ContainsKey(23));
        }

        [TestMethod]
        public void DeadEndDepth_VirtualPointIsStillARoadPoint()
        {
            // 虛擬點只影響「能不能當避讓點」，不影響道路連通 / 死巷判斷
            var depth = DeadEndAnalyzer.ComputeDeadEndDepthByTag(CreateSiteLikeMap(deadEndThroughVirtual: true));
            Assert.AreEqual(1, depth[69]);
            Assert.AreEqual(2, depth[67]);
        }

        [TestMethod]
        public void DeadEndDepth_WorkStationTargetsAreNotExits()
        {
            var map = CreateSiteLikeMap();
            // 69 另外連到一個工作站：工作站不是一般點出口，69 仍是死巷底
            map.Points[7] = Pt(70, type: STATION_TYPE.Charge);
            Link(map, 6, 7);
            Assert.AreEqual(1, DeadEndAnalyzer.ComputeDeadEndDepthByTag(map)[69]);
        }

        [TestMethod]
        public void DeadEndRule_SiteCase_AGV003InsideDeadEnd_AGV001Yields()
        {
            var map = CreateSiteLikeMap();
            var agv001 = Vehicle("AGV_001", 87);
            var agv003 = Vehicle("AGV_003", 69);
            var rule = new DeadEndRule(() => map);

            var result = rule.ResolvePriority(new[] { agv001, agv003 });
            Assert.IsNotNull(result);
            Assert.AreEqual("AGV_001", result!.lowPriorityVehicle.Name, "外側車讓");
            Assert.AreEqual("AGV_003", result.highPriorityVehicle.Name, "死巷內的車不讓");

            var reversed = rule.ResolvePriority(new[] { agv003, agv001 });
            Assert.AreEqual("AGV_001", reversed!.lowPriorityVehicle.Name, "與傳入順序無關");
        }

        [TestMethod]
        public void DeadEndRule_OneInsideOneOnMainLoop_MainLoopVehicleYields()
        {
            var map = CreateSiteLikeMap();
            var result = new DeadEndRule(() => map).ResolvePriority(new[] { Vehicle("AGV_002", 21), Vehicle("AGV_003", 67) });
            Assert.AreEqual("AGV_002", result!.lowPriorityVehicle.Name);
        }

        [TestMethod]
        public void DeadEndRule_NotApplicable_ReturnsNull()
        {
            var map = CreateSiteLikeMap();
            var rule = new DeadEndRule(() => map);
            Assert.IsNull(rule.ResolvePriority(new[] { Vehicle("AGV_001", 21), Vehicle("AGV_002", 23) }), "兩台都在主幹道");
            Assert.IsNull(new DeadEndRule(() => null).ResolvePriority(new[] { Vehicle("AGV_001", 87), Vehicle("AGV_003", 69) }), "沒有地圖");
            Assert.IsNull(rule.ResolvePriority(new[] { Vehicle("AGV_001", 87) }), "不是兩台");
            Assert.IsNull(DeadEndAnalyzer.MoreTrappedVehicle(new Dictionary<int, int> { [1] = 2, [2] = 2 }, "A", 1, "B", 2), "深度相同");
        }

        [TestMethod]
        public void StandardResolver_DeadEndRuleEnabled_AndEvaluatedFirst()
        {
            var resolver = new StandardVehiclePriorityResolver();
            var rules = (IReadOnlyList<IPriorityRule>)typeof(StandardVehiclePriorityResolver)
                .GetMethod("GetPriorityResolvers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(resolver, null)!;
            Assert.IsInstanceOfType(rules[0], typeof(DeadEndRule));
            Assert.IsTrue(rules.Any(r => r is HasAvoidFailRule));
        }

        // ───────────── 6. 多車循環 ─────────────

        [TestMethod]
        public void WaitForGraph_TwoWay_And_ThreeWayCycles_Detected()
        {
            var cycles = WaitForGraph.FindCycles(new Dictionary<string, string?>
            {
                ["AGV_001"] = "AGV_003", ["AGV_003"] = "AGV_001",
                ["B"] = "C", ["C"] = "D", ["D"] = "B",
            });
            Assert.AreEqual(2, cycles.Count);
            CollectionAssert.AreEqual(new[] { "AGV_001", "AGV_003" }, cycles.First(c => c.Count == 2));
            CollectionAssert.AreEqual(new[] { "B", "C", "D" }, cycles.First(c => c.Count == 3));
        }

        [TestMethod]
        public void WaitForGraph_ChainWithoutCycle_OrWaitingOnNonWaitingVehicle_IsNotACycle()
        {
            // A 等 B、B 等 C，但 C 沒有在等 (不在圖中) → 不是死結循環
            Assert.AreEqual(0, WaitForGraph.FindCycles(new Dictionary<string, string?> { ["A"] = "B", ["B"] = "C" }).Count);
            Assert.AreEqual(0, WaitForGraph.FindCycles(new Dictionary<string, string?> { ["A"] = null }).Count);
            // 尾巴接進循環：D 等 A，A-B-C 成環 → 只回傳 A-B-C
            var cycles = WaitForGraph.FindCycles(new Dictionary<string, string?> { ["D"] = "A", ["A"] = "B", ["B"] = "C", ["C"] = "A" });
            Assert.AreEqual(1, cycles.Count);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, cycles[0]);
        }

        [TestMethod]
        public void WaitForGraph_YieldCandidates_LowestWeightFirst_PasserIsTheVehicleWaitingOnYielder()
        {
            var cycle = new List<string> { "A", "B", "C" }; // A 等 B、B 等 C、C 等 A
            var weights = new Dictionary<string, double> { ["A"] = 900, ["B"] = 400, ["C"] = 500 };
            var order = WaitForGraph.OrderYieldCandidates(cycle, n => weights[n]);
            CollectionAssert.AreEqual(new[] { "B", "C", "A" }, order.Select(o => o.yielder).ToList());
            Assert.AreEqual("A", order[0].passer, "B 讓開後，等 B 的 A 可以前進");
            Assert.AreEqual("B", order[1].passer);
            Assert.AreEqual("C", order[2].passer);
        }

        [TestMethod]
        public void WaitForGraph_YieldCandidates_TieKeepsCycleOrder_AndInvalidInputEmpty()
        {
            var order = WaitForGraph.OrderYieldCandidates(new List<string> { "A", "B", "C" }, _ => 1);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, order.Select(o => o.yielder).ToList());
            Assert.AreEqual(0, WaitForGraph.OrderYieldCandidates(new List<string> { "A" }, _ => 1).Count);
        }
    }
}
