using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.OrderHandler.DestineChangeWokers;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// VMSystem 充電換站無限循環修正：
    /// 共用排除 helper、帶過期的排除 Tag 集合、充電取消冷卻、任務名稱 {base}_Chg{n} ≤ 50、換站次數上限。
    /// </summary>
    [TestClass]
    public class ChargeStationChangeLoopTests
    {
        // ---------- ChargeStationExclusionHelper ----------

        [TestMethod]
        public void CollectNextWorkStationTags_OnlyExecutingVehiclesWithPositiveTag()
        {
            var tags = ChargeStationExclusionHelper.CollectNextWorkStationTags(new[]
            {
                (true, 4),    // AGV_003 執行中、下一個工作站 tag4 → 排除
                (false, 5),   // 非執行中 → 不排除
                (true, 0),    // 執行中但沒有下一個工作站 → 不排除
                (true, -1),
                (true, 4),    // 重複
                (true, 7),
            });
            CollectionAssert.AreEquivalent(new[] { 4, 7 }, tags.ToArray());
        }

        [TestMethod]
        public void CollectNextWorkStationTags_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(0, ChargeStationExclusionHelper.CollectNextWorkStationTags(null!).Count);
            Assert.AreEqual(0, ChargeStationExclusionHelper.CollectNextWorkStationTags(Array.Empty<(bool, int)>()).Count);
        }

        [TestMethod]
        public void GetNextWorkStationTags_VehicleWithoutDispatchModule_IsIgnoredNotThrow()
        {
            // FakeAgv 的 taskDispatchModule 為 null：helper 不可丟例外（找站流程不能因此中斷）
            IAGV agv = FakeAgvFactory.Create(out _, "AGV_003");
            var tags = ChargeStationExclusionHelper.GetNextWorkStationTagsOfExecutingVehicles(new[] { agv });
            Assert.AreEqual(0, tags.Count);
            Assert.IsFalse(ChargeStationExclusionHelper.IsNextWorkStationOfAnyExecutingVehicle(new[] { agv }, 4));
            Assert.AreEqual(0, ChargeStationExclusionHelper.GetNextWorkStationTagsOfExecutingVehicles(null!).Count);
        }

        // ---------- ExpiringTagSet ----------

        [TestMethod]
        public void ExpiringTagSet_TagExpiresAfterTtl()
        {
            DateTime now = new DateTime(2026, 10, 22, 13, 13, 0);
            var set = new ExpiringTagSet(TimeSpan.FromSeconds(60), () => now);
            set.Add(4);
            Assert.IsTrue(set.Contains(4));
            now = now.AddSeconds(59);
            Assert.IsTrue(set.Contains(4), "60 秒內仍排除");
            now = now.AddSeconds(1);
            Assert.IsFalse(set.Contains(4), "滿 60 秒後過期");
            Assert.AreEqual(0, set.GetActiveTags().Count);
        }

        [TestMethod]
        public void ExpiringTagSet_HoldsMultipleTags_AndReAddExtends()
        {
            DateTime now = new DateTime(2026, 10, 22, 13, 13, 0);
            var set = new ExpiringTagSet(TimeSpan.FromSeconds(60), () => now);
            set.Add(4);
            now = now.AddSeconds(30);
            set.Add(8);
            CollectionAssert.AreEquivalent(new[] { 4, 8 }, set.GetActiveTags().ToArray(), "不再只記一個 Tag");
            set.Add(4); // 延長
            now = now.AddSeconds(45);
            CollectionAssert.AreEquivalent(new[] { 4, 8 }, set.GetActiveTags().ToArray());
            now = now.AddSeconds(16);
            Assert.AreEqual(0, set.GetActiveTags().Count);
        }

        [TestMethod]
        public void ExpiringTagSet_IgnoresNonPositiveTag_AndSnapshotIsCopy()
        {
            var set = new ExpiringTagSet();
            set.Add(-1);
            set.Add(0);
            Assert.AreEqual(0, set.GetActiveTags().Count);
            set.Add(4);
            var snapshot = set.GetActiveTags();
            snapshot.Add(99);
            Assert.IsFalse(set.Contains(99));
            Assert.AreEqual(TimeSpan.FromSeconds(60), set.TimeToLive);
        }

        // ---------- CooldownGate ----------

        [TestMethod]
        public void CooldownGate_ActiveOnlyWithinDuration()
        {
            DateTime now = new DateTime(2026, 10, 22, 13, 13, 0);
            var gate = new CooldownGate(TimeSpan.FromSeconds(60), () => now);
            Assert.IsFalse(gate.IsActive(out _), "未啟動前不冷卻");
            gate.Start();
            Assert.IsTrue(gate.IsActive(out var remaining));
            Assert.AreEqual(TimeSpan.FromSeconds(60), remaining);
            now = now.AddSeconds(59);
            Assert.IsTrue(gate.IsActive(out remaining));
            Assert.AreEqual(TimeSpan.FromSeconds(1), remaining);
            now = now.AddSeconds(1);
            Assert.IsFalse(gate.IsActive(out remaining));
            Assert.AreEqual(TimeSpan.Zero, remaining);
        }

        [TestMethod]
        public void ChargeCancelCooldown_IsBetween30And60Seconds()
        {
            Assert.IsTrue(clsAGVTaskDisaptchModule.ChargeCancelCooldownDuration >= TimeSpan.FromSeconds(30));
            Assert.IsTrue(clsAGVTaskDisaptchModule.ChargeCancelCooldownDuration <= TimeSpan.FromSeconds(60));
        }

        // ---------- Task name {base}_Chg{n} ----------

        [TestMethod]
        public void BuildChangedTaskName_UsesCounterNotRepeatedSuffix()
        {
            string name = "Charge-AGV_001_261022_131300123";
            string n1 = DestineChangeBase.BuildChangedTaskName(name, out int t1);
            Assert.AreEqual(name + "_Chg1", n1);
            Assert.AreEqual(1, t1);
            string n2 = DestineChangeBase.BuildChangedTaskName(n1, out int t2);
            Assert.AreEqual(name + "_Chg2", n2);
            Assert.AreEqual(2, t2);
            Assert.IsFalse(n2.Contains("_Chg_Chg"));
        }

        [TestMethod]
        public void ParseChangedTaskName_SupportsLegacyRepeatedSuffix()
        {
            var (baseName, times) = DestineChangeBase.ParseChangedTaskName("Charge-AGV_001_261022_131300123_Chg_Chg_Chg_Chg");
            Assert.AreEqual("Charge-AGV_001_261022_131300123", baseName);
            Assert.AreEqual(4, times);
            Assert.AreEqual(("Charge-AGV_001", 0), DestineChangeBase.ParseChangedTaskName("Charge-AGV_001"));
            Assert.AreEqual(("Charge-AGV_001", 12), DestineChangeBase.ParseChangedTaskName("Charge-AGV_001_Chg12"));
            Assert.AreEqual(("", 0), DestineChangeBase.ParseChangedTaskName(null!));
        }

        [DataTestMethod]
        [DataRow("Charge-AGV_001_261022_131300123")]
        [DataRow("Charge-AGV_001_261022_131300123_Chg_Chg_Chg_Chg")]
        [DataRow("DeepCharge-A_VERY_LONG_VEHICLE_NAME_0001_261022_131300123")] // > 50
        [DataRow("12345678901234567890123456789012345678901234567890")]          // = 50
        [DataRow("123456789012345678901234567890123456789012345_Chg9")]          // 50，下一次變 _Chg10
        public void BuildChangedTaskName_NeverExceeds50(string name)
        {
            string current = name;
            for (int i = 0; i < 12; i++)
            {
                current = DestineChangeBase.BuildChangedTaskName(current, out int times);
                Assert.IsTrue(current.Length <= DestineChangeBase.TaskNameMaxLength, $"{current} ({current.Length})");
                Assert.IsTrue(current.EndsWith($"_Chg{times}"), current);
            }
            Assert.AreEqual(50, DestineChangeBase.TaskNameMaxLength);
        }

        // ---------- 換站次數上限 ----------

        [TestMethod]
        public void DestineChangeLimit_IsTwoChangesPerOrder()
        {
            Assert.AreEqual(2, DestineChangeBase.MaxDestineChangeTimes);
            string original = "Charge-AGV_001_261022_131300123";
            Assert.IsFalse(DestineChangeBase.IsDestineChangeLimitReached(original), "原單可換站");
            string chg1 = DestineChangeBase.BuildChangedTaskName(original, out _);
            Assert.IsFalse(DestineChangeBase.IsDestineChangeLimitReached(chg1), "第 1 次換站後仍可再換一次");
            string chg2 = DestineChangeBase.BuildChangedTaskName(chg1, out _);
            Assert.IsTrue(DestineChangeBase.IsDestineChangeLimitReached(chg2), "換站 2 次後停止換站");
            Assert.IsTrue(DestineChangeBase.IsDestineChangeLimitReached(original + "_Chg_Chg_Chg_Chg"), "舊格式名稱也套用上限");
        }
    }
}
