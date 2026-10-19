using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.Configuration;
using AGVSystemCommonNet6.DATABASE;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using System.Net.Sockets;
using System.Reflection;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// PostgreSqlSyncBackgroundService 的失敗路徑（不需要 PostgreSQL：以內部接縫取代 Ensure／探測／寫入／退避等待）：
    /// 暫時性失敗時已出列的資料不會遺失、非暫時性資料錯誤略過並計數、指數退避與重設、斷線時先探測才出列、
    /// 狀態化 log（第一次 Error、之後 Debug、5 分鐘一筆 Warn、恢復一筆 Info）、正常關機不記 Error。
    /// </summary>
    [TestClass]
    public class PostgreSqlSyncFailurePathTests
    {
        // 不是真的密碼，只用來確認 log 中不會出現 Password（也就是 BuildRuntimeConfigSignature／連線字串沒被寫進 log）
        private const string PasswordSentinel = "sentinel-not-a-real-password-7f3a";

        private DatabaseSyncConfig _originalConfig = null!;
        private LoggingConfiguration? _originalLogConfig;
        private MemoryTarget _log = null!;

        [TestInitialize]
        public void Setup()
        {
            _originalConfig = AGVSConfigulator.DatabaseSyncConfig;
            AGVSConfigulator.DatabaseSyncConfig = new DatabaseSyncConfig
            {
                TransactionToPostgreSqlDatabase = true,
                FullSyncTablesAtBegin = "",
                PostgreSqlSync = new PostgreSqlSyncConfig
                {
                    Host = "pg-test-host",
                    Port = 5432,
                    Username = "sync_user",
                    Password = PasswordSentinel,
                    Database = "warroom",
                },
            };

            _originalLogConfig = LogManager.Configuration;
            _log = new MemoryTarget("pgsync-failure-test") { Layout = "${level:uppercase=true}|${logger}|${message}|${exception:format=type}" };
            var config = new LoggingConfiguration();
            config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, _log, typeof(PostgreSqlSyncBackgroundService).FullName!);
            LogManager.Configuration = config;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AGVSConfigulator.DatabaseSyncConfig = _originalConfig;
            LogManager.Configuration = _originalLogConfig;
        }

        private IReadOnlyList<string> Logs(string level) => _log.Logs.Where(l => l.StartsWith(level + "|", StringComparison.Ordinal)).ToList();

        private static PostgreSqlSyncWorkItem Item(int seq) => new()
        {
            EntityType = typeof(clsAlarmDto),
            KeyValues = new object?[] { seq },
            PropertyValues = new Dictionary<string, object?> { ["Seq"] = seq },
            ModifiedPropertyNames = new[] { "P" + seq },
            State = EntityState.Modified,
        };

        private static int Seq(PostgreSqlSyncWorkItem item) => (int)item.PropertyValues["Seq"]!;

        private static NpgsqlException TransientConnectionError() => new("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));

        private static PostgresException PgError(string sqlState) => new("simulated", "ERROR", "ERROR", sqlState);

        private sealed class SimClock
        {
            public DateTime Now = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        }

        private static PostgreSqlSyncBackgroundService NewService(PostgreSqlSyncQueue queue, SimClock? clock = null, List<TimeSpan>? delays = null)
        {
            var service = new PostgreSqlSyncBackgroundService(queue, null!, Options.Create(new PostgreSqlSyncBackgroundServiceOptions
            {
                EnableStartupTableSync = false,
                EnableAliveStatusReport = false,
            }));
            service.EnsureContextReadyHook = _ => Task.CompletedTask;
            service.ProbeTargetHook = _ => Task.CompletedTask;
            service.JitterSample = () => 0.5; // 0 抖動，讓退避序列可預期
            if (clock != null)
                service.UtcNow = () => clock.Now;
            service.RetryDelayAsync = (delay, token) =>
            {
                delays?.Add(delay);
                if (clock != null) clock.Now += delay;
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };
            return service;
        }

        // ───────────── 防遺失資料 ─────────────

        [TestMethod]
        public async Task TransientFailureMidBatch_FailedAndUnprocessedItemsRequeued_InOrder_NothingLost()
        {
            var queue = new PostgreSqlSyncQueue();
            for (int i = 1; i <= 5; i++) queue.TryEnqueue(Item(i));
            var service = NewService(queue);
            var written = new List<int>();
            service.SyncWorkItemHook = (item, _) =>
            {
                if (Seq(item) == 3) throw TransientConnectionError();
                written.Add(Seq(item));
                return Task.CompletedTask;
            };

            await Assert.ThrowsExceptionAsync<NpgsqlException>(() => service.RunSyncIterationAsync(CancellationToken.None));

            CollectionAssert.AreEqual(new[] { 1, 2 }, written);
            Assert.AreEqual(3, queue.PendingCount, "修改前：已出列的 3、4、5 直接丟掉且 DroppedCount 不加（無聲遺失）");
            Assert.AreEqual(0, queue.DroppedCount);
            var rest = new List<int>();
            while (queue.TryDequeue(out var it)) rest.Add(Seq(it!));
            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, rest, "失敗那筆排第一，之後依原順序");
        }

        [TestMethod]
        public async Task TransientFailure_ThenRecovery_EveryItemWrittenExactlyOnce()
        {
            var queue = new PostgreSqlSyncQueue();
            for (int i = 1; i <= 12; i++) queue.TryEnqueue(Item(i));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var service = NewService(queue);
            var written = new List<int>();
            int calls = 0;
            service.SyncWorkItemHook = (item, _) =>
            {
                calls++;
                if (calls == 4 || calls == 9) throw new TimeoutException("simulated");
                written.Add(Seq(item));
                if (written.Count == 12) cts.Cancel();
                return Task.CompletedTask;
            };

            await service.RunSyncLoopAsync(cts.Token);

            CollectionAssert.AreEqual(Enumerable.Range(1, 12).ToArray(), written.ToArray());
            Assert.AreEqual(0, queue.PendingCount);
            Assert.AreEqual(0, queue.DroppedCount);
            Assert.AreEqual(0, queue.SkippedCount);
        }

        [TestMethod]
        public async Task NonTransientDataError_SkippedAndCounted_RestOfBatchContinues_NoValuesLogged()
        {
            var queue = new PostgreSqlSyncQueue();
            for (int i = 1; i <= 4; i++) queue.TryEnqueue(Item(i));
            var service = NewService(queue);
            var written = new List<int>();
            service.SyncWorkItemHook = (item, _) =>
            {
                if (Seq(item) == 2) throw new DbUpdateException("save failed", PgError(PostgresErrorCodes.NotNullViolation));
                if (Seq(item) == 3) throw new InvalidCastException("rebuild failed: value 'secret-ish-column-value'");
                written.Add(Seq(item));
                return Task.CompletedTask;
            };

            await service.RunSyncIterationAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 4 }, written);
            Assert.AreEqual(2, queue.SkippedCount);
            Assert.AreEqual(0, queue.PendingCount, "非暫時性錯誤不放回佇列，不會卡住後面的資料");
            Assert.AreEqual(0, service.ConsecutiveFailures, "略過不算同步失敗，不進退避");
            var errors = Logs("ERROR");
            Assert.AreEqual(2, errors.Count);
            StringAssert.Contains(errors[0], "Type=clsAlarmDto");
            StringAssert.Contains(errors[0], "Keys=[2]");
            StringAssert.Contains(errors[0], "SqlState=23502");
            Assert.IsFalse(_log.Logs.Any(l => l.Contains("secret-ish-column-value")), "不記例外訊息內文（可能帶欄位值）");
            Assert.IsFalse(_log.Logs.Any(l => l.Contains(PasswordSentinel)));
        }

        [TestMethod]
        public async Task ServerWideNonTransientPostgresError_Auth_IsRequeued_NotSkipped()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item(1));
            var service = NewService(queue);
            service.SyncWorkItemHook = (_, _) => throw PgError(PostgresErrorCodes.InvalidPassword);

            await Assert.ThrowsExceptionAsync<PostgresException>(() => service.RunSyncIterationAsync(CancellationToken.None));

            Assert.AreEqual(1, queue.PendingCount, "認證錯誤是整台 PG 的問題，跳過會把所有資料丟光");
            Assert.AreEqual(0, queue.SkippedCount);
        }

        [DataTestMethod]
        [DataRow("23505", false, DisplayName = "unique_violation → 略過")]
        [DataRow("23502", false, DisplayName = "not_null_violation → 略過")]
        [DataRow("22001", false, DisplayName = "string_data_right_truncation → 略過")]
        [DataRow("42703", false, DisplayName = "undefined_column → 略過")]
        [DataRow("42501", true, DisplayName = "insufficient_privilege → 放回")]
        [DataRow("28P01", true, DisplayName = "invalid_password → 放回")]
        [DataRow("3D000", true, DisplayName = "invalid_catalog_name → 放回")]
        [DataRow("57P01", true, DisplayName = "admin_shutdown → 放回")]
        [DataRow("53300", true, DisplayName = "too_many_connections → 放回")]
        [DataRow("40001", true, DisplayName = "serialization_failure → 放回")]
        public void ClassifyItemFailure_PostgresSqlStates(string sqlState, bool requeue)
        {
            var kind = PostgreSqlSyncBackgroundService.ClassifyItemFailure(new DbUpdateException("x", PgError(sqlState)));
            Assert.AreEqual(requeue ? PostgreSqlSyncBackgroundService.PostgreSqlSyncFailureKind.Transient
                                    : PostgreSqlSyncBackgroundService.PostgreSqlSyncFailureKind.NonTransientData, kind);
        }

        [TestMethod]
        public void ClassifyItemFailure_ConnectionTypeErrors_AreTransient()
        {
            var transient = new Exception[]
            {
                TransientConnectionError(),
                new NpgsqlException("protocol error"),
                new SocketException((int)SocketError.HostUnreachable),
                new TimeoutException(),
                new IOException("broken pipe"),
                new OperationCanceledException(),
                new DbUpdateException("x", new NpgsqlException("x", new IOException())),
                new AggregateException(new TimeoutException()),
            };
            foreach (var ex in transient)
                Assert.AreEqual(PostgreSqlSyncBackgroundService.PostgreSqlSyncFailureKind.Transient,
                    PostgreSqlSyncBackgroundService.ClassifyItemFailure(ex), ex.GetType().Name);

            Assert.AreEqual(PostgreSqlSyncBackgroundService.PostgreSqlSyncFailureKind.NonTransientData,
                PostgreSqlSyncBackgroundService.ClassifyItemFailure(new InvalidCastException()));
        }

        // ───────────── 退避 ─────────────

        [DataTestMethod]
        [DataRow(1, 0.5, 1000)]
        [DataRow(2, 0.5, 2000)]
        [DataRow(3, 0.5, 4000)]
        [DataRow(6, 0.5, 32000)]
        [DataRow(7, 0.5, 60000)]
        [DataRow(50, 0.5, 60000)]
        [DataRow(3, 0.0, 3200)]   // -20%
        [DataRow(3, 1.0, 4800)]   // +20%
        [DataRow(1, 0.0, 1000)]   // -20% 但不低於 1 秒
        [DataRow(10, 1.0, 60000)] // +20% 但不超過 60 秒
        public void ComputeRetryDelay_ExponentialWithJitter_ClampedTo1To60s(int failures, double jitter, int expectedMs)
        {
            var delay = PostgreSqlSyncFailureTracker.ComputeRetryDelay(failures, 1000, 60000, jitter);
            Assert.AreEqual(expectedMs, delay.TotalMilliseconds, 0.001);
        }

        [TestMethod]
        public void ComputeRetryDelay_RandomJitter_StaysWithinPlusMinus20Percent()
        {
            var rnd = new Random(1234);
            for (int i = 0; i < 1000; i++)
            {
                var ms = PostgreSqlSyncFailureTracker.ComputeRetryDelay(4, 1000, 60000, rnd.NextDouble()).TotalMilliseconds;
                Assert.IsTrue(ms >= 6400 && ms <= 9600, ms.ToString());
            }
        }

        [TestMethod]
        public async Task Loop_BackoffDoublesUpTo60s_ProbesBeforeDequeue_ThenResetsAfterSuccess_AndLogsAreSummarized()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item(1));
            var clock = new SimClock();
            var delays = new List<TimeSpan>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var service = NewService(queue, clock, delays);

            int probeCalls = 0;
            var pendingDuringProbe = new List<int>();
            service.ProbeTargetHook = _ =>
            {
                probeCalls++;
                pendingDuringProbe.Add(queue.PendingCount);
                if (probeCalls <= 10) throw TransientConnectionError();
                return Task.CompletedTask;
            };

            int syncCalls = 0;
            var written = new List<int>();
            service.SyncWorkItemHook = (item, _) =>
            {
                syncCalls++;
                if (syncCalls == 1) throw TransientConnectionError();        // PG 中途斷線
                if (syncCalls == 3) throw TransientConnectionError();        // 恢復後又斷一次 → 退避要從 1 秒重來
                written.Add(Seq(item));
                if (Seq(item) == 1 && written.Count == 1) queue.TryEnqueue(Item(2));
                if (written.Count == 2) cts.Cancel();
                return Task.CompletedTask;
            };

            await service.RunSyncLoopAsync(cts.Token);

            CollectionAssert.AreEqual(new[] { 1, 2 }, written);
            var expected = new[] { 1, 2, 4, 8, 16, 32, 60, 60, 60, 60, 60, /* 成功後重設 */ 1 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
            CollectionAssert.AreEqual(expected, delays.ToArray());
            Assert.AreEqual(12, probeCalls, "第一次失敗前連線正常不探測；之後每次重試都先探測（10 次失敗 + 2 次成功）");
            Assert.IsTrue(pendingDuringProbe.All(p => p >= 1), "探測成功前不出列：資料一直留在佇列");
            Assert.AreEqual(4, syncCalls);
            Assert.IsFalse(service.IsPgLinkDown);

            // 第一段斷線：失敗 11 次、模擬時間 0→303 秒；第二段：1 次
            var errors = Logs("ERROR");
            Assert.AreEqual(2, errors.Count, "每段斷線只有第一次失敗記 Error（含 stack）");
            Assert.IsTrue(errors.All(e => e.TrimEnd().EndsWith("NpgsqlException")), "第一次 Error 有帶例外");
            Assert.AreEqual(1, Logs("WARN").Count(l => l.Contains("仍無法同步")), "第 11 次失敗時已持續 ≥ 5 分鐘 → 一筆 Warn 摘要");
            Assert.AreEqual(2, Logs("INFO").Count(l => l.Contains("已恢復同步")), "每段恢復一筆 Info");
            Assert.AreEqual(10, Logs("DEBUG").Count(l => l.Contains("重試失敗")));
            Assert.IsTrue(errors[0].Contains("pg-test-host:5432/warroom"), "只記 Host/Port/Database");
            Assert.IsFalse(_log.Logs.Any(l => l.Contains(PasswordSentinel)), "log 不可含密碼／連線字串／設定簽章");
        }

        [TestMethod]
        public void FailureTracker_SummaryEvery5Minutes_RecoveryOnce()
        {
            var tracker = new PostgreSqlSyncFailureTracker(TimeSpan.FromMinutes(5));
            var t0 = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.First, tracker.RecordFailure(t0));
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.Quiet, tracker.RecordFailure(t0.AddSeconds(10)));
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.Quiet, tracker.RecordFailure(t0.AddSeconds(299)));
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.Summary, tracker.RecordFailure(t0.AddSeconds(300)));
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.Quiet, tracker.RecordFailure(t0.AddSeconds(400)));
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.Summary, tracker.RecordFailure(t0.AddSeconds(600)));

            Assert.IsTrue(tracker.RecordSuccess(t0.AddSeconds(660), out var downtime, out int failures));
            Assert.AreEqual(TimeSpan.FromSeconds(660), downtime);
            Assert.AreEqual(6, failures);
            Assert.IsFalse(tracker.RecordSuccess(t0.AddSeconds(661), out _, out _), "恢復 log 只記一次");
            Assert.AreEqual(PostgreSqlSyncFailureTracker.FailureLogKind.First, tracker.RecordFailure(t0.AddSeconds(700)), "恢復後再失敗重新算第一次");
            Assert.AreEqual("26:03:04", PostgreSqlSyncFailureTracker.FormatDuration(new TimeSpan(1, 2, 3, 4)));
        }

        // ───────────── 關機 ─────────────

        [TestMethod]
        public async Task Shutdown_WhileIdle_BreaksWithoutErrorLog()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            using var cts = new CancellationTokenSource();
            var loop = service.RunSyncLoopAsync(cts.Token);
            await Task.Delay(200);
            cts.Cancel();

            var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreSame(loop, finished, "關機時迴圈要立刻結束（修改前會在 catch 裡 Delay(5000, token) 再丟一次例外）");
            Assert.AreEqual(TaskStatus.RanToCompletion, loop.Status);
            Assert.AreEqual(0, Logs("ERROR").Count, "正常關機不記 Error");
        }

        [TestMethod]
        public async Task Shutdown_DuringWrite_ItemRequeued_NoErrorLog()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item(1));
            var service = NewService(queue);
            using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.SyncWorkItemHook = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token); // 丟 TaskCanceledException（OperationCanceledException 的子類別）
            };

            var loop = service.RunSyncLoopAsync(cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.AreEqual(1, queue.PendingCount, "關機中斷的那筆放回佇列（記憶體內），不算略過");
            Assert.AreEqual(0, queue.SkippedCount);
            Assert.AreEqual(0, Logs("ERROR").Count);
            Assert.AreEqual(0, service.ConsecutiveFailures, "關機不算同步失敗");
        }

        [TestMethod]
        public async Task Shutdown_DuringBackoffDelay_BreaksPromptly_OnlyFirstFailureLogged()
        {
            var queue = new PostgreSqlSyncQueue();
            queue.TryEnqueue(Item(1));
            var service = NewService(queue);
            var inDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.RetryDelayAsync = (delay, token) => { inDelay.TrySetResult(); return Task.Delay(delay, token); };
            service.SyncWorkItemHook = (_, _) => throw TransientConnectionError();
            using var cts = new CancellationTokenSource();

            var loop = service.RunSyncLoopAsync(cts.Token);
            await inDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.AreEqual(TaskStatus.RanToCompletion, loop.Status);
            Assert.AreEqual(1, Logs("ERROR").Count, "只有那一次同步失敗的 Error；退避等待被取消不再記 Error");
            Assert.AreEqual(1, queue.PendingCount);
        }

        [TestMethod]
        public async Task HostedService_StartStop_ExecuteTaskCompletesCleanly()
        {
            var queue = new PostgreSqlSyncQueue();
            var service = NewService(queue);
            await service.StartAsync(CancellationToken.None);
            await Task.Delay(200);
            Assert.IsNotNull(service.ExecuteTask);
            Assert.IsFalse(service.ExecuteTask!.IsCompleted, "ExecuteAsync 回傳的是迴圈本身，Host 可以觀察與等待它");

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.AreEqual(TaskStatus.RanToCompletion, service.ExecuteTask.Status);
            Assert.AreEqual(0, Logs("ERROR").Count);
        }

        // ───────────── 連線設定 ─────────────

        [TestMethod]
        public void TargetConnectionString_UsesConnectTimeoutFromOptions_Default5s()
        {
            var service = NewService(new PostgreSqlSyncQueue());
            var method = typeof(PostgreSqlSyncBackgroundService).GetMethod("BuildTargetConnectionString", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cs = (string)method.Invoke(service, new object?[] { AGVSConfigulator.DatabaseSyncConfig.PostgreSqlSync, null })!;
            Assert.AreEqual(5, new NpgsqlConnectionStringBuilder(cs).Timeout, "取代 Npgsql 預設的 15 秒");

            var admin = typeof(PostgreSqlSyncBackgroundService).GetMethod("BuildAdminConnectionString", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var adminCs = (string)admin.Invoke(service, new object?[] { AGVSConfigulator.DatabaseSyncConfig.PostgreSqlSync })!;
            Assert.AreEqual(5, new NpgsqlConnectionStringBuilder(adminCs).Timeout);
        }
    }
}
