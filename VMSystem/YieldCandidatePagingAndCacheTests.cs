using AGVSystemCommonNet6.MAP;
using VMSystem.Dispatch.YieldActions;
using VMSystem.Services.Navigation;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// #35 審查的兩個 🟡：
    /// a) 候選點不再先截斷再過濾，改為依距離分批 (每批最多 N 個)，最近一批都不可用時取下一批
    /// b) 規劃快取鍵要包含其他車輛 / 點位註冊的狀態簽章，且只快取「可用」結果 (衝突／失敗不快取)
    /// </summary>
    [TestClass]
    public class YieldCandidatePagingAndCacheTests
    {
        // ───────────────────────── a) 分批 ─────────────────────────

        [TestMethod]
        public void Page_FewerOrEqualThanPageSize_SingleBatch_OriginalOrderUnchanged()
        {
            var items = new List<int> { 5, 3, 9, 1 };
            var pages = AvoidCandidateSelector.Page(items, x => x, 10).ToList();
            Assert.AreEqual(1, pages.Count);
            CollectionAssert.AreEqual(new[] { 5, 3, 9, 1 }, pages[0], "候選數不超過一批時順序不變 (結果與舊版相同)");
            var exact = AvoidCandidateSelector.Page(Enumerable.Range(0, 10).Reverse().ToList(), x => x, 10).ToList();
            Assert.AreEqual(1, exact.Count);
        }

        [TestMethod]
        public void Page_MoreThanPageSize_SortedByDistance_AndPagesCoverEveryCandidateExactlyOnce()
        {
            var items = Enumerable.Range(0, 25).Select(i => (i * 7) % 25).ToList();   // 打亂
            var pages = AvoidCandidateSelector.Page(items, x => x, 10).ToList();
            CollectionAssert.AreEqual(new[] { 10, 10, 5 }, pages.Select(p => p.Count).ToList());
            CollectionAssert.AreEqual(Enumerable.Range(0, 25).ToList(), pages.SelectMany(p => p).ToList(), "全部候選恰好各出現一次、整體由近到遠");
        }

        [TestMethod]
        public void Page_Ties_KeepOriginalRelativeOrder()
        {
            var items = new List<(string n, double d)> { ("a", 1), ("b", 1), ("c", 1), ("d", 0) };
            var pages = AvoidCandidateSelector.Page(items, x => x.d, 2).ToList();
            CollectionAssert.AreEqual(new[] { "d", "a" }, pages[0].Select(x => x.n).ToList());
            CollectionAssert.AreEqual(new[] { "b", "c" }, pages[1].Select(x => x.n).ToList());
        }

        [TestMethod]
        public void Page_ZeroOrNegative_MeansNoLimit_SingleBatchWithEverything()
        {
            var items = Enumerable.Range(0, 50).ToList();
            Assert.AreEqual(1, AvoidCandidateSelector.Page(items, x => -x, 0).Count());
            Assert.AreEqual(50, AvoidCandidateSelector.Page(items, x => -x, -1).First().Count);
            Assert.AreEqual(0, AvoidCandidateSelector.Page(new List<int>(), x => x, 10).Count());
        }

        [TestMethod]
        public void Page_IsLazy_LaterBatchesNotEnumeratedWhenCallerStopsEarly()
        {
            int distanceCalls = 0;
            var items = Enumerable.Range(0, 100).ToList();
            var first = AvoidCandidateSelector.Page(items, x => { distanceCalls++; return x; }, 10).First();
            Assert.AreEqual(10, first.Count);
            Assert.IsTrue(distanceCalls >= 100);  // 排序一次
            // 「取到第一批就停」不會再產生第二批 (呼叫端 break) → 以 Take(1) 驗證只物化一批
            Assert.AreEqual(1, AvoidCandidateSelector.Page(items, x => x, 10).Take(1).Count());
        }

        /// <summary>模擬 clsLowPriorityVehicleMove 的迴圈：每批規劃後套用後續過濾，找到可用就停。</summary>
        private static (List<int> chosen, int batches, int planned) Solve(List<int> candidates, Func<int, bool> isUsable, int pageSize)
        {
            int batches = 0, planned = 0;
            foreach (var page in AvoidCandidateSelector.Page(candidates, x => x, pageSize))
            {
                batches++; planned += page.Count;
                var ok = page.Where(isUsable).ToList();
                if (ok.Any()) return (ok, batches, planned);
            }
            return (new List<int>(), batches, planned);
        }

        [TestMethod]
        public void Solve_NearestTenAllUnusable_ValidEleventhCandidateIsStillFound()
        {
            var candidates = Enumerable.Range(1, 30).ToList();           // 距離 = 值
            var (chosen, batches, planned) = Solve(candidates, x => x == 11, 10);
            CollectionAssert.AreEqual(new[] { 11 }, chosen);
            Assert.AreEqual(2, batches);
            Assert.AreEqual(20, planned);

            // 舊行為 (先截斷前 10 個再過濾) 會漏掉第 11 個
            var legacy = AvoidCandidateSelector.TakeNearest(candidates, x => x, 10).Where(x => x == 11).ToList();
            Assert.AreEqual(0, legacy.Count);
        }

        [TestMethod]
        public void Solve_FirstBatchHasUsable_StopsAfterOneBatch_SamePlanningAmountAsBefore()
        {
            var (chosen, batches, planned) = Solve(Enumerable.Range(1, 30).ToList(), x => x == 4 || x == 7, 10);
            CollectionAssert.AreEqual(new[] { 4, 7 }, chosen);
            Assert.AreEqual(1, batches);
            Assert.AreEqual(10, planned);
        }

        [TestMethod]
        public void Solve_NothingUsable_PlansEveryCandidateAtMostOnce_ThenGivesUp()
        {
            var (chosen, batches, planned) = Solve(Enumerable.Range(1, 37).ToList(), _ => false, 10);
            Assert.AreEqual(0, chosen.Count);
            Assert.AreEqual(4, batches);
            Assert.AreEqual(37, planned, "最多規劃 (候選總數) 個，不會重複也不會無限迴圈");
        }

        // ───────────────────────── b) 快取鍵與快取政策 ─────────────────────────

        private static AvoidPlanStateSignature.VehicleMarker Marker(string name = "AGV_2", int tag = 20, double x = 1, double y = 2, double theta = 90,
                                                                    int main = 1, bool executing = true, bool waitingLeave = false, params int[] next)
            => new(name, tag, x, y, theta, main, executing, waitingLeave, next.Length == 0 ? new[] { 20, 21 } : next);

        private static string Sig(IEnumerable<AvoidPlanStateSignature.VehicleMarker>? others = null, IEnumerable<KeyValuePair<int, string>>? reg = null, string owner = "AGV_1", double theta = 0)
            => AvoidPlanStateSignature.Compute(owner, theta, others ?? new[] { Marker() }, reg ?? new[] { new KeyValuePair<int, string>(5, "AGV_2") });

        [TestMethod]
        public void Signature_SameState_SameSignature_RegardlessOfVehicleOrder()
        {
            var a = Marker("AGV_2"); var b = Marker("AGV_3", tag: 30);
            Assert.AreEqual(Sig(new[] { a, b }), Sig(new[] { b, a }));
            Assert.AreEqual(Sig(new[] { a, b }), Sig(new[] { a, b }));
        }

        [TestMethod]
        public void Signature_ChangesWhenAnyOtherVehicleMarkerChanges()
        {
            string baseline = Sig();
            var variants = new Dictionary<string, AvoidPlanStateSignature.VehicleMarker>
            {
                ["tag"] = Marker(tag: 21),
                ["x"] = Marker(x: 1.5),
                ["y"] = Marker(y: 2.5),
                ["theta"] = Marker(theta: 180),
                ["mainState"] = Marker(main: 3),
                ["executing"] = Marker(executing: false),
                ["waitingLeave"] = Marker(waitingLeave: true),
                ["nextTags"] = Marker(next: new[] { 20, 21, 22 }),
                ["name"] = Marker(name: "AGV_9"),
            };
            foreach (var kv in variants)
                Assert.AreNotEqual(baseline, Sig(new[] { kv.Value }), $"{kv.Key} 改變時簽章必須改變");
        }

        [TestMethod]
        public void Signature_OtherVehicleAddedOrRemoved_Changes()
        {
            Assert.AreNotEqual(Sig(new[] { Marker() }), Sig(new[] { Marker(), Marker("AGV_3", 30) }));
            Assert.AreNotEqual(Sig(new[] { Marker() }), Sig(Array.Empty<AvoidPlanStateSignature.VehicleMarker>()));
        }

        [TestMethod]
        public void Signature_ChangesWhenRegistrationByOthersChanges_ButNotWhenOwnRegistrationChanges()
        {
            string baseline = Sig();
            Assert.AreNotEqual(baseline, Sig(reg: new[] { new KeyValuePair<int, string>(5, "AGV_2"), new KeyValuePair<int, string>(6, "AGV_2") }), "別人新註冊一個點");
            Assert.AreNotEqual(baseline, Sig(reg: new[] { new KeyValuePair<int, string>(5, "AGV_3") }), "點位改由另一台車註冊");
            Assert.AreNotEqual(baseline, Sig(reg: Array.Empty<KeyValuePair<int, string>>()), "註冊被釋放");
            // 自己 (AGV_1) 註冊的點不是限制 (PathPlaner 只排除「別人」註冊的點) → 不影響簽章
            Assert.AreEqual(baseline, Sig(reg: new[] { new KeyValuePair<int, string>(5, "AGV_2"), new KeyValuePair<int, string>(7, "AGV_1") }));
        }

        [TestMethod]
        public void Signature_ChangesWithOwnerAndOwnerHeading()
        {
            Assert.AreNotEqual(Sig(owner: "AGV_1"), Sig(owner: "AGV_5", others: new[] { Marker() }));
            Assert.AreNotEqual(Sig(theta: 0), Sig(theta: 90));
        }

        [TestMethod]
        public void Signature_Position_IsRoundedToOneCentimeter_AndHeadingToOneDegree()
        {
            Assert.AreEqual(Sig(new[] { Marker(x: 1.0001, y: 2.0004, theta: 90.2) }), Sig(new[] { Marker(x: 1.0, y: 2.0, theta: 90.0) }), "毫米等級的抖動視為同一狀態 (讓靜止車輛的重試仍可命中快取)");
            Assert.AreNotEqual(Sig(new[] { Marker(x: 1.0) }), Sig(new[] { Marker(x: 1.05) }));
        }

        private static PathPlanResultWrapper Wrapper(int pathLen, int conflicts)
        {
            var w = new PathPlanResultWrapper();
            for (int i = 0; i < pathLen; i++) w.path.Add(new MapPoint { TagNumber = i + 1 });
            for (int i = 0; i < conflicts; i++) w.conflicPoints.Add(new MapPoint { TagNumber = 100 + i });
            return w;
        }

        [TestMethod]
        public void CachePolicy_OnlyUsableResultsAreCacheable()
        {
            Assert.IsTrue(AvoidPlanCachePolicy.IsCacheable(Wrapper(3, 0)));
            Assert.IsFalse(AvoidPlanCachePolicy.IsCacheable(null), "失敗 (null) 不快取");
            Assert.IsFalse(AvoidPlanCachePolicy.IsCacheable(Wrapper(3, 1)), "仍有衝突點的結果不快取");
            Assert.IsFalse(AvoidPlanCachePolicy.IsCacheable(Wrapper(0, 0)), "沒有路徑不快取");
            var nullPath = new PathPlanResultWrapper { path = null! };
            Assert.IsFalse(AvoidPlanCachePolicy.IsCacheable(nullPath));
        }

        [TestMethod]
        public void ConflictResult_IsNeverServedAfterBackoffStep_BecauseItIsNeverStored()
        {
            // 以 PlanCache 同款的 ShortLivedCache 重現流程：TTL 2 s > 退避第一階 1 s
            var cache = new ShortLivedCache<string, PathPlanResultWrapper?>(TimeSpan.FromSeconds(2));
            var conflicted = Wrapper(3, 2);
            string key = "AGV_1|1|7|" + Sig();
            if (AvoidPlanCachePolicy.IsCacheable(conflicted)) cache.Set(key, conflicted);
            Assert.IsFalse(cache.TryGet(key, out _), "對方車輛移開後重試必須重新規劃，不能拿到舊的衝突結果");

            var usable = Wrapper(3, 0);
            if (AvoidPlanCachePolicy.IsCacheable(usable)) cache.Set(key, usable);
            Assert.IsTrue(cache.TryGet(key, out var hit));
            Assert.AreSame(usable, hit);
            // 其他車輛狀態改變 → 鍵不同 → 不會命中舊結果
            string movedKey = "AGV_1|1|7|" + Sig(new[] { Marker(tag: 99) });
            Assert.IsFalse(cache.TryGet(movedKey, out _));
        }
    }
}
