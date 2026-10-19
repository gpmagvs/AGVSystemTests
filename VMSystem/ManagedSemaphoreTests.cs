using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using VMSystem.Dispatch;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// ManagedSemaphore (交管派車鎖) 的行為驗證：
    /// 1. lockTimeoutSec 由建構子傳入的值確實生效 (舊版建構子鏈 `: this()` 會讓監控吃到預設 15 秒).
    /// 2. 逾時被強制釋放後, 原持有者再 Release 不會丟 SemaphoreFullException、不會把信號量再釋放一次、也不會釋放新持有者的鎖.
    /// 3. 強制釋放後新持有者取得鎖, 鎖在任何時間點只會有一個「有效持有者」.
    /// 時間以注入的假時鐘 + 直接呼叫 CheckTimeoutAndForceRelease() 驗證, 不需要真的睡 10 秒.
    /// </summary>
    [TestClass]
    public class ManagedSemaphoreTests
    {
        private sealed class FakeClock
        {
            private long _ticks = new DateTime(2026, 1, 1, 0, 0, 0).Ticks;
            public DateTime Now => new DateTime(Interlocked.Read(ref _ticks));
            public void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
        }

        private sealed class Fixture : IDisposable
        {
            public readonly FakeClock Clock = new();
            public readonly MemoryTarget Log = new("mem") { Layout = "${level:uppercase=true}|${message}" };
            public readonly ManagedSemaphore Sem;

            /// <param name="monitorIntervalMs">預設很長 (1 小時), 讓背景監控不干擾, 由測試主動呼叫 CheckTimeoutAndForceRelease</param>
            public Fixture(double timeoutSec, bool useFakeClock = true, double monitorIntervalMs = 3_600_000)
            {
                var cfg = new LoggingConfiguration();
                cfg.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, Log);
                var logger = new LogFactory(cfg).GetLogger("ManagedSemaphoreTests");
                Sem = new ManagedSemaphore(timeoutSec, logger, TimeSpan.FromMilliseconds(monitorIntervalMs), useFakeClock ? () => Clock.Now : null);
            }

            public int Count(string level, string contains = "") =>
                Log.Logs.Count(l => l.StartsWith(level + "|") && l.Contains(contains));

            public void Dispose() => Sem.Dispose();
        }

        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(80);

        // ───────────── 1. 逾時值生效 ─────────────

        [TestMethod]
        public void Ctor_TimeoutValuePassedIn_IsApplied()
        {
            using var f = new Fixture(10);
            Assert.AreEqual(10d, f.Sem.lockTimeoutSec);
        }

        [TestMethod]
        public async Task Timeout10Sec_ForceReleasesAfter10Sec_NotAt15Sec()
        {
            using var f = new Fixture(10);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));

            f.Clock.Advance(9.5);
            Assert.IsFalse(f.Sem.CheckTimeoutAndForceRelease(), "9.5 秒不該被強制釋放");
            Assert.IsTrue(f.Sem.IsLocked);

            f.Clock.Advance(1.0); // 共 10.5 秒 (舊版實際逾時是 15 秒, 此時不會被釋放)
            Assert.IsTrue(f.Sem.CheckTimeoutAndForceRelease(), "超過 10 秒應強制釋放 (舊版吃到 15 秒預設值)");
            Assert.IsFalse(f.Sem.IsLocked);
            Assert.AreEqual(1, f.Sem.ForceReleaseCount);
            Assert.AreEqual(1, f.Count("WARN", "強制釋放"));
        }

        [TestMethod]
        public async Task RealMonitorLoop_UsesCtorTimeout_ForceReleasesQuickly()
        {
            // 用真實時間 + 真實監控迴圈: 逾時 0.3 秒, 週期 30ms. 若逾時值被忽略 (吃到 15 秒), 5 秒內不會被釋放.
            using var f = new Fixture(0.3, useFakeClock: false, monitorIntervalMs: 30);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (f.Sem.ForceReleaseCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.AreEqual(1, f.Sem.ForceReleaseCount, "監控迴圈應依建構子的 0.3 秒逾時強制釋放");
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("B", TimeSpan.FromSeconds(1)));
        }

        [TestMethod]
        public void DispatchCenter_PathDispatchSemaphore_UsesTimeout10()
        {
            // DispatchCenter.cs: new(lockTimeoutSec: 10, ...) 現在必須真的是 10 秒
            var field = typeof(DispatchCenter).GetField("pathDispatchSemaphoreManager", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);
            var sem = (ManagedSemaphore)field!.GetValue(null)!;
            Assert.AreEqual(10d, sem.lockTimeoutSec);
        }

        [TestMethod]
        public async Task DefaultTimeout_Is15Sec_AndParameterlessCtor_HasLogger()
        {
            using var sem = new ManagedSemaphore();
            Assert.AreEqual(15d, sem.lockTimeoutSec);
            Assert.IsTrue(await sem.WaitWithTrackAsync("A"));
            // 舊版無參數建構子 logger = null, 等待逾時 (logger.Info) 會 NullReferenceException
            Assert.IsFalse(await sem.WaitWithTrackAsync("B", Short));
            sem.Release("A");
        }

        // ───────────── 2. 強制釋放後原持有者 Release ─────────────

        [TestMethod]
        public async Task StaleRelease_AfterForceRelease_DoesNotThrow_AndDoesNotReleaseAgain()
        {
            using var f = new Fixture(10);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            f.Clock.Advance(11);
            Assert.IsTrue(f.Sem.CheckTimeoutAndForceRelease());

            // 原持有者 A 之後才來 Release: 三種 API 都不可丟例外
            Assert.IsFalse(f.Sem.Release("A"));
            f.Sem.Release();
            Assert.AreEqual(2, f.Sem.StaleReleaseIgnoredCount);
            Assert.AreEqual(2, f.Count("WARN", "Release 被忽略"), "每次被忽略的 Release 都要記 Warn");

            // 計數仍一致 (沒有被多釋放成 2): 第一個取得成功, 第二個必須等不到
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("B", Short));
            Assert.IsFalse(await f.Sem.WaitWithTrackAsync("C", Short), "信號量計數不可超過 1 (舊版 Release 會丟 SemaphoreFullException 或變成可兩人同時進入)");
        }

        [TestMethod]
        public async Task StaleLeaseRelease_AfterForceRelease_IsIgnored()
        {
            using var f = new Fixture(10);
            var lease = await f.Sem.AcquireAsync("A");
            Assert.IsNotNull(lease);
            Assert.IsTrue(lease!.IsValid);
            f.Clock.Advance(11);
            f.Sem.CheckTimeoutAndForceRelease();
            Assert.IsFalse(lease.IsValid);
            Assert.IsFalse(lease.Release());
            lease.Dispose(); // using 模式也不可丟例外
            Assert.AreEqual(2, f.Count("WARN", "Release 被忽略"));
        }

        [TestMethod]
        public void Release_WhenNeverAcquired_DoesNotThrow_LogsWarn()
        {
            using var f = new Fixture(10);
            f.Sem.Release();
            Assert.IsFalse(f.Sem.Release("A"));
            Assert.AreEqual(2, f.Count("WARN", "沒有持有者"));
        }

        // ───────────── 3. 不會雙持有 ─────────────

        [TestMethod]
        public async Task NoDoubleHold_NewHolderKeepsLock_WhenStaleHolderReleases()
        {
            using var f = new Fixture(10);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            f.Clock.Advance(11);
            f.Sem.CheckTimeoutAndForceRelease();

            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("B", Short), "強制釋放後新持有者應能取得鎖");
            Assert.IsTrue(f.Sem.IsHeldBy("B"));
            Assert.IsFalse(f.Sem.IsHeldBy("A"), "被強制釋放的 A 不再是持有者");

            // A 的過期 Release (名稱 API) 不可釋放 B 的鎖
            Assert.IsFalse(f.Sem.Release("A"));
            Assert.IsTrue(f.Sem.IsHeldBy("B"));
            Assert.AreEqual("B", f.Sem.currentOwner);
            Assert.IsFalse(await f.Sem.WaitWithTrackAsync("C", Short), "B 還在持有, C 不可取得 (不可雙持有)");

            Assert.IsTrue(f.Sem.Release("B"));
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("C", Short));
        }

        [TestMethod]
        public async Task NoDoubleHold_SameOwnerReacquire_OldLeaseCannotReleaseNewLease()
        {
            using var f = new Fixture(10);
            var old = await f.Sem.AcquireAsync("A");
            f.Clock.Advance(11);
            f.Sem.CheckTimeoutAndForceRelease();
            var fresh = await f.Sem.AcquireAsync("A", Short);
            Assert.IsNotNull(fresh);
            Assert.AreNotEqual(old!.Version, fresh!.Version);

            Assert.IsFalse(old.Release(), "同一台車重新取得鎖後, 舊憑證的 Release 不可釋放新憑證的鎖");
            Assert.IsTrue(fresh.IsValid);
            Assert.IsFalse(await f.Sem.WaitWithTrackAsync("B", Short));
            Assert.IsTrue(fresh.Release());
        }

        [TestMethod]
        public async Task NonHolderName_CannotReleaseOthersLock()
        {
            using var f = new Fixture(10);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            Assert.IsFalse(f.Sem.Release("B"));
            Assert.IsTrue(f.Sem.IsHeldBy("A"));
            Assert.AreEqual(1, f.Count("WARN", "目前持有者是 [A]"));
        }

        // ───────────── 4. 一般行為 ─────────────

        [TestMethod]
        public async Task NormalAcquireRelease_Works_AndTracksOwnerAndLockTime()
        {
            using var f = new Fixture(10);
            Assert.AreEqual("None", f.Sem.currentOwner);
            Assert.IsNull(f.Sem.lockTime);

            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            Assert.AreEqual("A", f.Sem.currentOwner);
            Assert.AreEqual(f.Clock.Now, f.Sem.lockTime);
            Assert.IsFalse(await f.Sem.WaitWithTrackAsync("B", Short), "A 持有中, B 應等待逾時");
            Assert.AreEqual(1, f.Count("INFO", "等待 Semaphore 釋放超時"));

            f.Sem.Release();
            Assert.AreEqual("None", f.Sem.currentOwner);
            Assert.IsNull(f.Sem.lockTime);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("B", Short));
            Assert.IsTrue(f.Sem.Release("B"));
            Assert.AreEqual(0, f.Count("WARN"));
            Assert.AreEqual(0, f.Sem.ForceReleaseCount);
        }

        [TestMethod]
        public async Task WaiterGetsLock_WhenHolderReleases()
        {
            using var f = new Fixture(10);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            var waiter = f.Sem.WaitWithTrackAsync("B", TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.IsFalse(waiter.IsCompleted);
            Assert.IsTrue(f.Sem.Release("A"));
            Assert.IsTrue(await waiter);
            Assert.IsTrue(f.Sem.IsHeldBy("B"));
        }

        [TestMethod]
        public async Task WaiterGetsLock_WhenHolderForceReleased()
        {
            using var f = new Fixture(10);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            var waiter = f.Sem.WaitWithTrackAsync("B", TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            f.Clock.Advance(11);
            Assert.IsTrue(f.Sem.CheckTimeoutAndForceRelease());
            Assert.IsTrue(await waiter);
            Assert.IsTrue(f.Sem.IsHeldBy("B"));
            // 剛取得的 B 不會因為 A 的舊 lockTime 被立刻強制釋放
            Assert.IsFalse(f.Sem.CheckTimeoutAndForceRelease());
            Assert.IsFalse(f.Sem.Release("A"));
            Assert.IsTrue(f.Sem.IsHeldBy("B"));
        }

        [TestMethod]
        public async Task OwnerChange_IsLoggedAtTrace_NotCommentedOut()
        {
            using var f = new Fixture(10);
            await f.Sem.WaitWithTrackAsync("A");
            f.Sem.Release("A");
            Assert.AreEqual(1, f.Count("TRACE", "owner changed from None to A"));
            Assert.AreEqual(1, f.Count("TRACE", "owner changed from A to None"));
        }

        [TestMethod]
        public async Task Dispose_StopsMonitor()
        {
            using var f = new Fixture(0.05, useFakeClock: false, monitorIntervalMs: 20);
            f.Sem.Dispose();
            await Task.Delay(100);
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("A"));
            await Task.Delay(300);
            Assert.AreEqual(0, f.Sem.ForceReleaseCount, "Dispose 後不再監控");
            Assert.IsTrue(f.Sem.IsHeldBy("A"));
        }

        // ───────────── 5. DispatchCenter 靜態包裝 ─────────────

        [TestMethod]
        public async Task DispatchCenter_ReleaseByOwnerName_IgnoresOtherOwner()
        {
            Assert.IsTrue(await DispatchCenter.WaitDispatchLocker("UT-A", TimeSpan.FromSeconds(1)));
            try
            {
                DispatchCenter.ReleaseDispatchLocker("UT-B"); // 非持有者, 忽略不丟例外
                Assert.IsFalse(await DispatchCenter.WaitDispatchLocker("UT-C", Short), "UT-A 仍持有");
            }
            finally
            {
                DispatchCenter.ReleaseDispatchLocker("UT-A");
            }
            Assert.IsTrue(await DispatchCenter.WaitDispatchLocker("UT-C", TimeSpan.FromSeconds(1)));
            DispatchCenter.ReleaseDispatchLocker("UT-C");
            DispatchCenter.ReleaseDispatchLocker("UT-C"); // 重複釋放: 忽略
        }

        // ───────────── 6. 並行壓力 ─────────────

        [TestMethod]
        public async Task Stress_ConcurrentAcquireRelease_NeverTwoHoldersAtOnce()
        {
            using var f = new Fixture(60, useFakeClock: false);
            int inside = 0, maxInside = 0, entered = 0;
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var workers = Enumerable.Range(0, 24).Select(w => Task.Run(async () =>
            {
                for (int i = 0; i < 150; i++)
                {
                    try
                    {
                        string name = $"AGV{w}";
                        var lease = await f.Sem.AcquireAsync(name, TimeSpan.FromSeconds(30));
                        Assert.IsNotNull(lease);
                        int now = Interlocked.Increment(ref inside);
                        int seen;
                        while ((seen = Volatile.Read(ref maxInside)) < now && Interlocked.CompareExchange(ref maxInside, now, seen) != seen) { }
                        Interlocked.Increment(ref entered);
                        if (i % 7 == 0) await Task.Yield();
                        Interlocked.Decrement(ref inside);
                        Assert.IsTrue(lease!.Release());
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            })).ToArray();
            await Task.WhenAll(workers);

            Assert.AreEqual(0, errors.Count, string.Join("; ", errors.Select(e => e.Message)));
            Assert.AreEqual(1, maxInside, "同時進入臨界區的持有者不可超過 1");
            Assert.AreEqual(24 * 150, entered);
            Assert.AreEqual(0, f.Sem.ForceReleaseCount);
            Assert.AreEqual(0, f.Sem.StaleReleaseIgnoredCount);
        }

        [TestMethod]
        public async Task Stress_ForceReleaseAndStaleReleases_NeverThrow_AndCountStaysConsistent()
        {
            // 逾時極短 + 監控極密 + 一半的 worker 會重複 Release (模擬過期持有者), 同時有人用 legacy Release()
            using var f = new Fixture(0.002, useFakeClock: false, monitorIntervalMs: 1);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var workers = Enumerable.Range(0, 16).Select(w => Task.Run(async () =>
            {
                var rnd = new Random(w);
                for (int i = 0; i < 200; i++)
                {
                    try
                    {
                        string name = $"AGV{w % 4}";
                        var lease = await f.Sem.AcquireAsync(name, TimeSpan.FromMilliseconds(50));
                        if (lease == null) continue;
                        if (rnd.Next(3) == 0) await Task.Delay(rnd.Next(1, 8)); // 可能逾時被強制釋放
                        switch (rnd.Next(4))
                        {
                            case 0: lease.Release(); lease.Release(); break;
                            case 1: f.Sem.Release(name); f.Sem.Release(name); break;
                            case 2: f.Sem.Release(); break;
                            default: lease.Dispose(); break;
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            })).ToArray();
            await Task.WhenAll(workers);

            Assert.AreEqual(0, errors.Count, string.Join("; ", errors.Select(e => e.GetType().Name + ":" + e.Message)));
            Assert.IsTrue(f.Sem.StaleReleaseIgnoredCount > 0, "壓力測試必須真的觸發過期 Release 的情境");

            // 收尾: 先讓殘留持有者被強制釋放, 之後信號量必須恰好可被 1 人持有
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (f.Sem.IsLocked && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.IsFalse(f.Sem.IsLocked);
            f.Sem.Dispose();
            Assert.IsTrue(await f.Sem.WaitWithTrackAsync("final", Short));
            Assert.IsFalse(await f.Sem.WaitWithTrackAsync("final2", Short), "信號量計數不可超過 1");
        }
    }
}
