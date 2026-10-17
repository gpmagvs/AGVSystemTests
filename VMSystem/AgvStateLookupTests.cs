using AGVSystemCommonNet6;
using VMSystem.BackgroundServices;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// VehicleStateService 一次查出全部 AgvStates 的查詢表：名稱重複時容錯 (舊 patch 的 ToDictionary 會丟 ArgumentException)。
    /// </summary>
    [TestClass]
    public class AgvStateLookupTests
    {
        private static clsAGVStateDto Dto(string name, string id = "") => new clsAGVStateDto { AGV_Name = name, AGV_ID = id };

        [TestMethod]
        public void ToDictionary_WouldThrowOnDuplicates_LookupDoesNot()
        {
            var states = new[] { Dto("AGV_1", "first"), Dto("AGV_1", "second") };
            // 原 patch 的寫法遇到重複名稱會整輪更新失敗
            Assert.ThrowsException<ArgumentException>(() => states.ToDictionary(s => s.AGV_Name, StringComparer.OrdinalIgnoreCase));

            var lookup = AgvStateLookup.Build(states, _ => { });
            Assert.AreEqual(1, lookup.Count);
            Assert.AreEqual(1, lookup.DuplicateCount);
            Assert.IsTrue(lookup.TryGet("AGV_1", out var found));
            Assert.IsNotNull(found);
        }

        [TestMethod]
        public void Duplicates_KeepFirstDeterministically_RegardlessOfInputOrder()
        {
            var a = Dto("AGV_1", "a");
            var b = Dto("agv_1", "b");              // 只差大小寫

            var l1 = AgvStateLookup.Build(new[] { a, b }, _ => { });
            var l2 = AgvStateLookup.Build(new[] { b, a }, _ => { });

            Assert.IsTrue(l1.TryGet("AGV_1", out var f1));
            Assert.IsTrue(l2.TryGet("AGV_1", out var f2));
            Assert.AreSame(f1, f2, "輸入順序不同，結果必須相同 (確定性)");
            Assert.AreEqual("a", f1!.AGV_ID);
            // 大小寫完全相同的名稱優先於不分大小寫的比對
            Assert.IsTrue(l1.TryGet("agv_1", out var lower));
            Assert.AreEqual("b", lower!.AGV_ID);
            // 兩個都不是完全相同的名稱時，不分大小寫比對取 Ordinal 排序的第一筆 (確定性，不受輸入順序影響)
            Assert.IsTrue(l1.TryGet("Agv_1", out var mixed1));
            Assert.IsTrue(l2.TryGet("Agv_1", out var mixed2));
            Assert.AreEqual("a", mixed1!.AGV_ID);
            Assert.AreSame(mixed1, mixed2);
        }

        [TestMethod]
        public void ExactDuplicates_FirstInOrdinalOrderWins_AndCallbackFiresPerDuplicate()
        {
            var d1 = Dto("X", "1"); var d2 = Dto("X", "2"); var d3 = Dto("X", "3");
            var seen = new List<string>();
            var lookup = AgvStateLookup.Build(new[] { d1, d2, d3 }, n => seen.Add(n));
            Assert.AreEqual(2, lookup.DuplicateCount);
            CollectionAssert.AreEqual(new[] { "X", "X" }, seen);
            Assert.IsTrue(lookup.TryGet("X", out var found));
            Assert.AreSame(d1, found, "OrderBy 為穩定排序，完全同名時取輸入順序的第一筆");
        }

        [TestMethod]
        public void Lookup_MatchesLegacyWhereFirstOrDefault_ForNormalData()
        {
            var states = new[] { Dto("AGV_001"), Dto("AGV_002"), Dto("AGV_010") };
            var lookup = AgvStateLookup.Build(states, _ => Assert.Fail("沒有重複不該警告"));
            foreach (var name in new[] { "AGV_001", "AGV_002", "AGV_010", "AGV_003", "" })
            {
                var legacy = states.Where(s => s.AGV_Name == name).FirstOrDefault();
                Assert.AreEqual(legacy != null, lookup.TryGet(name, out var found), name);
                Assert.AreSame(legacy, found, name);
            }
            Assert.IsFalse(lookup.TryGet(null!, out _));
            Assert.AreEqual(0, lookup.DuplicateCount);
        }

        [TestMethod]
        public void DefaultWarning_IsLoggedOncePerName_NotEveryCycle()
        {
            // VehicleStateService 每 150 ms 重建一次查詢表；同一個重複名稱的警告只能記錄一次
            string name = "DUP_" + Guid.NewGuid().ToString("N");
            var warnings = new List<string>();
            var target = new NLog.Targets.MethodCallTarget("cap", (logEvent, _) => { if (logEvent.FormattedMessage.Contains(name)) lock (warnings) warnings.Add(logEvent.FormattedMessage); });
            var oldConfig = NLog.LogManager.Configuration;
            var cfg = new NLog.Config.LoggingConfiguration();
            cfg.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, target);
            NLog.LogManager.Configuration = cfg;
            try
            {
                for (int i = 0; i < 20; i++)
                    AgvStateLookup.Build(new[] { Dto(name), Dto(name) });
            }
            finally { NLog.LogManager.Configuration = oldConfig; }
            Assert.AreEqual(1, warnings.Count, "同一個重複名稱 20 輪只能警告 1 次");
        }
    }
}
