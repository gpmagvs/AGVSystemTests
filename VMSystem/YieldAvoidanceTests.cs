using AGVSystemCommonNet6.MAP;
using VMSystem.Dispatch;
using VMSystem.Dispatch.YieldActions;
using VMSystem.TrafficControl.Parameters;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// 多車避讓的記憶體／CPU 放大器修正 (報告 F1)：
    /// - 候選點只取最近 N 個 (AvoidCandidateSelector)
    /// - 路徑規劃限流 (AvoidPlanLimiter) 與短時間快取 (ShortLivedCache)
    /// - 找不到避讓點時的退避 (AvoidSolveBackoff)，DeadLockMonitor 以它取代「每 100 ms 重試」
    /// 全部是純邏輯測試，不需要 SQL Server、地圖或真實 AGV。
    /// </summary>
    [TestClass]
    public class YieldAvoidanceTests
    {
        private sealed class FakeClock
        {
            public DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            public Func<DateTime> Func => () => Now;
            public void Advance(TimeSpan span) => Now += span;
        }

        // ───────────────────────── 候選點上限 ─────────────────────────

        [TestMethod]
        public void TakeNearest_OnlyKeepsTheNearestN_InDistanceOrder()
        {
            // 距離 = 值本身，故意打散順序
            var distances = Enumerable.Range(1, 60).Select(i => (double)((i * 37) % 61)).ToList();
            var picked = AvoidCandidateSelector.TakeNearest(distances, d => d, 10);

            Assert.AreEqual(10, picked.Count);
            CollectionAssert.AreEqual(distances.OrderBy(d => d).Take(10).ToList(), picked);
        }

        [TestMethod]
        public void TakeNearest_WhenNotMoreThanMax_ReturnsSameListInSameOrder()
        {
            // 候選不多的現場：順序與內容與舊版完全相同 (沒有排序)
            var points = new List<double> { 9, 3, 7, 1, 5 };
            var picked = AvoidCandidateSelector.TakeNearest(points, d => d, 10);
            CollectionAssert.AreEqual(new List<double> { 9, 3, 7, 1, 5 }, picked);
        }

        [TestMethod]
        public void TakeNearest_MaxZeroOrNegative_MeansUnlimited()
        {
            var points = Enumerable.Range(0, 100).Select(i => (double)(100 - i)).ToList();
            Assert.AreEqual(100, AvoidCandidateSelector.TakeNearest(points, d => d, 0).Count);
            Assert.AreEqual(100, AvoidCandidateSelector.TakeNearest(points, d => d, -1).Count);
        }

        [TestMethod]
        public void TakeNearest_Ties_KeepOriginalRelativeOrder()
        {
            var points = new List<(string name, double d)> { ("a", 1), ("b", 1), ("c", 1), ("d", 0.5) };
            var picked = AvoidCandidateSelector.TakeNearest(points, p => p.d, 3);
            CollectionAssert.AreEqual(new[] { "d", "a", "b" }, picked.Select(p => p.name).ToArray());
        }

        [TestMethod]
        public void DefaultConfig_LimitIsTen_AndOtherKnobsHaveSafeDefaults()
        {
            var p = new clsTrafficControlParameters.DeadLockParams();
            Assert.AreEqual(10, p.maxAvoidCandidatesPerRing);
            Assert.AreEqual(AvoidCandidateSelector.DefaultMaxCandidatesPerRing, p.maxAvoidCandidatesPerRing);
            Assert.AreEqual(2, p.avoidPlanMaxConcurrency);
            Assert.AreEqual(2d, p.avoidPlanCacheSeconds);
            CollectionAssert.AreEqual(new double[] { 1, 2, 4, 8, 15 }, p.avoidRetryBackoffSeconds);
            // 既有設定不變
            CollectionAssert.AreEqual(new double[] { 5, 20, 35, 50 }, p.searchRadiiForAvoidPoint);
        }

        [TestMethod]
        public async Task OnlyNearestNCandidates_AreActuallyPlanned()
        {
            // 300 個候選點 → 只有最近 10 個會進規劃 (以計數器驗證「實際規劃次數」，不是只驗清單長度)
            var candidates = Enumerable.Range(1, 300).Select(i => (tag: i, dist: (double)((i * 53) % 301))).ToList();
            int planned = 0;
            var plannedTags = new System.Collections.Concurrent.ConcurrentBag<int>();
            var limiter = new AvoidPlanLimiter(2);

            var nearest = AvoidCandidateSelector.TakeNearest(candidates, c => c.dist, AvoidCandidateSelector.DefaultMaxCandidatesPerRing);
            var results = await limiter.RunAllAsync(nearest, async c =>
            {
                Interlocked.Increment(ref planned);
                plannedTags.Add(c.tag);
                await Task.Yield();
                return c.tag;
            });

            Assert.AreEqual(10, planned);
            Assert.AreEqual(10, results.Length);
            var expectedTags = candidates.OrderBy(c => c.dist).Take(10).Select(c => c.tag).OrderBy(t => t).ToList();
            CollectionAssert.AreEqual(expectedTags, plannedTags.OrderBy(t => t).ToList());
        }

        // ───────────────────────── 限流 ─────────────────────────

        [TestMethod]
        public async Task Limiter_NeverRunsMoreThanMaxConcurrency_AndKeepsInputOrder()
        {
            var limiter = new AvoidPlanLimiter(2);
            int running = 0, peak = 0;
            var items = Enumerable.Range(0, 20).ToList();

            var results = await limiter.RunAllAsync(items, async i =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)))
                    Interlocked.CompareExchange(ref peak, now, seen);
                await Task.Delay(15);
                Interlocked.Decrement(ref running);
                return i * 10;
            });

            Assert.IsTrue(peak <= 2, $"同時執行數 {peak} 超過上限 2");
            Assert.IsTrue(peak >= 1);
            Assert.IsTrue(limiter.MaxObservedRunning <= 2);
            CollectionAssert.AreEqual(items.Select(i => i * 10).ToArray(), results);
        }

        [TestMethod]
        public async Task Limiter_MaxConcurrencyOne_IsFullySequential()
        {
            var limiter = new AvoidPlanLimiter(1);
            int running = 0, peak = 0;
            await limiter.RunAllAsync(Enumerable.Range(0, 8).ToList(), async i =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)))
                    Interlocked.CompareExchange(ref peak, now, seen);
                await Task.Delay(5);
                Interlocked.Decrement(ref running);
                return i;
            });
            Assert.AreEqual(1, peak);
        }

        [TestMethod]
        public async Task Limiter_AWorkThatThrows_DoesNotLeakTheSlot()
        {
            var limiter = new AvoidPlanLimiter(1);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                limiter.RunAllAsync(new List<int> { 1 }, (Func<int, Task<int>>)(_ => throw new InvalidOperationException("boom"))));
            // 名額已歸還：下一批仍能執行
            var results = await limiter.RunAllAsync(new List<int> { 1, 2, 3 }, i => Task.FromResult(i));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, results);
        }

        [TestMethod]
        public void Limiter_ZeroOrNegativeConfig_IsClampedToOne()
        {
            Assert.AreEqual(1, new AvoidPlanLimiter(0).MaxConcurrency);
            Assert.AreEqual(1, new AvoidPlanLimiter(-5).MaxConcurrency);
        }

        // ───────────────────────── 短時間快取 ─────────────────────────

        [TestMethod]
        public void Cache_HitsWithinTtl_MissesAfterTtl()
        {
            var clock = new FakeClock();
            var cache = new ShortLivedCache<string, int>(TimeSpan.FromSeconds(2), clock.Func);

            Assert.IsFalse(cache.TryGet("k", out _));
            cache.Set("k", 42);
            clock.Advance(TimeSpan.FromSeconds(1.9));
            Assert.IsTrue(cache.TryGet("k", out int v));
            Assert.AreEqual(42, v);

            clock.Advance(TimeSpan.FromSeconds(0.2));   // 共 2.1 秒，已過期
            Assert.IsFalse(cache.TryGet("k", out _));
            Assert.AreEqual(0, cache.Count, "過期項目在查詢時應被移除");
        }

        [TestMethod]
        public void Cache_ZeroTtl_IsDisabled()
        {
            var cache = new ShortLivedCache<string, int>(TimeSpan.Zero);
            cache.Set("k", 1);
            Assert.IsFalse(cache.Enabled);
            Assert.IsFalse(cache.TryGet("k", out _));
            Assert.AreEqual(0, cache.Count);
        }

        [TestMethod]
        public void Cache_IsBounded_ExpiredEntriesPrunedFirst()
        {
            var clock = new FakeClock();
            var cache = new ShortLivedCache<int, int>(TimeSpan.FromSeconds(1), clock.Func, maxEntries: 8);
            for (int i = 0; i < 8; i++) cache.Set(i, i);
            Assert.AreEqual(8, cache.Count);

            clock.Advance(TimeSpan.FromSeconds(2));      // 全部過期
            cache.Set(100, 100);                         // 達上限 → 先清過期的
            Assert.AreEqual(1, cache.Count);
            Assert.IsTrue(cache.TryGet(100, out _));

            // 全部都沒過期仍超量：不會無限成長
            for (int i = 0; i < 1000; i++) cache.Set(1000 + i, i);
            Assert.IsTrue(cache.Count <= 8, $"快取大小 {cache.Count} 超過上限 8");
        }

        // ───────────────────────── 退避 ─────────────────────────

        [TestMethod]
        public void Backoff_FollowsSteppedSchedule_AndRepeatsLastStep()
        {
            var clock = new FakeClock();
            var backoff = new AvoidSolveBackoff(clock: clock.Func);   // 預設階梯 1,2,4,8,15
            var delays = new List<double>();
            for (int i = 0; i < 8; i++)
            {
                delays.Add(backoff.RecordFailure("A|B").TotalSeconds);
                clock.Advance(TimeSpan.FromSeconds(100));              // 每次都等過退避期才失敗下一次
            }
            CollectionAssert.AreEqual(new double[] { 1, 2, 4, 8, 15, 15, 15, 15 }, delays);
            Assert.AreEqual(8, backoff.FailureCount("A|B"));
        }

        [TestMethod]
        public void Backoff_SkipsUntilDelayElapsed_ThenAllowsRetry()
        {
            var clock = new FakeClock();
            var backoff = new AvoidSolveBackoff(clock: clock.Func);

            Assert.IsFalse(backoff.ShouldSkip("A|B", out _), "從未失敗過不該被跳過");
            backoff.RecordFailure("A|B");                              // 退避 1 秒
            Assert.IsTrue(backoff.ShouldSkip("A|B", out var remaining));
            Assert.IsTrue(remaining > TimeSpan.FromSeconds(0.9) && remaining <= TimeSpan.FromSeconds(1));

            // 以前每 100 ms 重試：在退避期間內每 100 ms 檢查一次，全部都該被跳過 (9 次)
            int skipped = 0;
            for (int i = 0; i < 9; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                if (backoff.ShouldSkip("A|B", out _)) skipped++;
            }
            Assert.AreEqual(9, skipped);

            clock.Advance(TimeSpan.FromMilliseconds(100));            // 滿 1 秒
            Assert.IsFalse(backoff.ShouldSkip("A|B", out _));
        }

        [TestMethod]
        public void Backoff_ResetOnSuccess_RestartsFromFirstStep()
        {
            var clock = new FakeClock();
            var backoff = new AvoidSolveBackoff(clock: clock.Func);
            backoff.RecordFailure("A|B");
            clock.Advance(TimeSpan.FromSeconds(5));
            backoff.RecordFailure("A|B");
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, backoff.FailureCount("A|B"));

            backoff.RecordSuccess("A|B");
            Assert.AreEqual(0, backoff.FailureCount("A|B"));
            Assert.IsFalse(backoff.ShouldSkip("A|B", out _));
            Assert.AreEqual(1d, backoff.RecordFailure("A|B").TotalSeconds, "成功後下一次失敗應從第一階 (1 秒) 重新開始");
        }

        [TestMethod]
        public void Backoff_PairKey_IsSymmetric_AndPairsAreIndependent()
        {
            Assert.AreEqual(AvoidSolveBackoff.PairKey("AGV_1", "AGV_2"), AvoidSolveBackoff.PairKey("AGV_2", "AGV_1"));
            Assert.AreNotEqual(AvoidSolveBackoff.PairKey("AGV_1", "AGV_2"), AvoidSolveBackoff.PairKey("AGV_1", "AGV_3"));

            var clock = new FakeClock();
            var backoff = new AvoidSolveBackoff(clock: clock.Func);
            backoff.RecordFailure(AvoidSolveBackoff.PairKey("AGV_1", "AGV_2"));
            Assert.IsTrue(backoff.ShouldSkip(AvoidSolveBackoff.PairKey("AGV_2", "AGV_1"), out _));
            Assert.IsFalse(backoff.ShouldSkip(AvoidSolveBackoff.PairKey("AGV_1", "AGV_3"), out _), "別組車不受影響");
        }

        [TestMethod]
        public void Backoff_PruneIdleAndClear_RemoveStaleState()
        {
            var clock = new FakeClock();
            var backoff = new AvoidSolveBackoff(clock: clock.Func);
            backoff.RecordFailure("old");
            clock.Advance(TimeSpan.FromSeconds(50));
            backoff.RecordFailure("fresh");
            clock.Advance(TimeSpan.FromSeconds(20));                   // old 閒置 70s，fresh 閒置 20s

            Assert.AreEqual(1, backoff.PruneIdle(TimeSpan.FromSeconds(60)));
            Assert.AreEqual(0, backoff.FailureCount("old"));
            Assert.AreEqual(1, backoff.FailureCount("fresh"));

            backoff.Clear();
            Assert.AreEqual(0, backoff.Count);
        }

        [TestMethod]
        public void Backoff_CustomStepsAndEmptyStepsFallback()
        {
            var clock = new FakeClock();
            var custom = new AvoidSolveBackoff(new[] { TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(3) }, clock.Func);
            Assert.AreEqual(0.5, custom.RecordFailure("k").TotalSeconds);
            Assert.AreEqual(3, custom.RecordFailure("k").TotalSeconds);
            Assert.AreEqual(3, custom.RecordFailure("k").TotalSeconds);

            var fallback = new AvoidSolveBackoff(Array.Empty<TimeSpan>(), clock.Func);
            Assert.AreEqual(1, fallback.RecordFailure("k").TotalSeconds);
        }

        [TestMethod]
        public void DeadLockMonitor_UsesBackoffWithConfiguredDefaultSchedule()
        {
            // DeadLockMonitor 的迴圈在「找不到避讓點」時以這個物件決定是否跳過；預設階梯來自 DeadLockParams.avoidRetryBackoffSeconds
            var monitor = new DeadLockMonitor();
            Assert.IsNotNull(monitor.SolveBackoff);
            Assert.AreEqual(1d, monitor.SolveBackoff.RecordFailure("x|y").TotalSeconds);
            Assert.AreEqual(2d, monitor.SolveBackoff.RecordFailure("x|y").TotalSeconds);
            Assert.IsTrue(monitor.SolveBackoff.ShouldSkip("x|y", out _), "找不到避讓點後，下一個 100 ms 輪詢必須被跳過");
        }
    }
}
