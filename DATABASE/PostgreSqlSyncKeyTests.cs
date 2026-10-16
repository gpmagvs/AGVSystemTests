using AGVSystemCommonNet6.AGVDispatch;
using AGVSystemCommonNet6.AGVDispatch.Model;
using AGVSystemCommonNet6.Alarm;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.MCSCIM;
using Microsoft.EntityFrameworkCore;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// PostgreSqlSyncKey（PostgreSqlSyncQueue 與 PostgreSqlSyncBackgroundService.BuildBatch 共用的「同一列」鍵）：
    /// 主鍵編碼必須無損、無歧義 —— 兩個不同的主鍵絕不能被當成同一列。不需要資料庫。
    /// </summary>
    [TestClass]
    public class PostgreSqlSyncKeyTests
    {
        private static PostgreSqlSyncWorkItem Item(Type type, params object?[] keys) => new()
        {
            EntityType = type,
            KeyValues = keys,
            PropertyValues = new Dictionary<string, object?> { ["Seq"] = 0 },
            State = EntityState.Added,
        };

        private static string Enc(params object?[] keys) => PostgreSqlSyncKey.EncodeKeyValues(keys);

        private static void AssertDistinct(object?[] a, object?[] b, string message = "")
            => Assert.AreNotEqual(PostgreSqlSyncKey.EncodeKeyValues(a), PostgreSqlSyncKey.EncodeKeyValues(b), message);

        private static void AssertSame(object?[] a, object?[] b, string message = "")
            => Assert.AreEqual(PostgreSqlSyncKey.EncodeKeyValues(a), PostgreSqlSyncKey.EncodeKeyValues(b), message);

        // ───────────── 紅燈重現：DateTime 主鍵同一秒不同毫秒不可合併 ─────────────

        [TestMethod]
        public void AlarmDto_SameSecond_DifferentMilliseconds_BothStayInQueue()
        {
            var queue = new PostgreSqlSyncQueue();
            var t0 = new DateTime(2026, 10, 16, 9, 30, 15, DateTimeKind.Local);
            var a = Item(typeof(clsAlarmDto), t0.AddMilliseconds(100));
            var b = Item(typeof(clsAlarmDto), t0.AddMilliseconds(900));
            a.PropertyValues["Seq"] = 1;
            b.PropertyValues["Seq"] = 2;

            queue.TryEnqueue(a);
            queue.TryEnqueue(b);

            Assert.AreEqual(2, queue.PendingCount, "同一秒不同毫秒是兩列不同的警報，兩筆都要留在佇列");
            Assert.AreEqual(0, queue.CoalescedCount, "不可被算成合併");
            Assert.AreEqual(0, queue.DroppedCount);

            Assert.IsTrue(queue.TryDequeue(out var first));
            Assert.IsTrue(queue.TryDequeue(out var second));
            Assert.AreEqual(1, first!.PropertyValues["Seq"], "較早那筆必須還在，且維持 FIFO");
            Assert.AreEqual(2, second!.PropertyValues["Seq"]);
        }

        [TestMethod]
        [DataRow(typeof(clsAlarmDto))]
        [DataRow(typeof(SecsMessageLog))]
        [DataRow(typeof(DeadLockSolveRecord))]
        [DataRow(typeof(clsMeasureResult))]
        public void AllDateTimePrimaryKeyEntities_SameSecond_DifferentMilliseconds_NotCoalesced(Type entityType)
        {
            var queue = new PostgreSqlSyncQueue();
            var t0 = new DateTime(2026, 10, 16, 9, 30, 15);

            queue.TryEnqueue(Item(entityType, t0.AddMilliseconds(1)));
            queue.TryEnqueue(Item(entityType, t0.AddMilliseconds(2)));
            queue.TryEnqueue(Item(entityType, t0.AddMilliseconds(999)));

            Assert.AreEqual(3, queue.PendingCount, entityType.Name);
            Assert.AreEqual(0, queue.CoalescedCount, entityType.Name);
        }

        [TestMethod]
        public void DateTimeKey_DifferByOneTick_NotCoalesced()
        {
            var queue = new PostgreSqlSyncQueue();
            var t0 = new DateTime(2026, 10, 16, 9, 30, 15);
            queue.TryEnqueue(Item(typeof(clsAlarmDto), t0));
            queue.TryEnqueue(Item(typeof(clsAlarmDto), t0.AddTicks(1)));

            Assert.AreEqual(2, queue.PendingCount);
            Assert.AreEqual(0, queue.CoalescedCount);
        }

        [TestMethod]
        public void DateTimeKey_Identical_StillCoalesces_KeepsLatest()
        {
            var queue = new PostgreSqlSyncQueue();
            var t = new DateTime(2026, 10, 16, 9, 30, 15, 123);
            var older = Item(typeof(clsAlarmDto), t);
            var newer = Item(typeof(clsAlarmDto), new DateTime(t.Ticks));
            older.PropertyValues["Seq"] = 1;
            newer.PropertyValues["Seq"] = 2;

            queue.TryEnqueue(older);
            queue.TryEnqueue(newer);

            Assert.AreEqual(1, queue.PendingCount);
            Assert.AreEqual(1, queue.CoalescedCount);
            Assert.IsTrue(queue.TryDequeue(out var item));
            Assert.AreEqual(2, item!.PropertyValues["Seq"], "真正同一列仍要合併並保留最新快照");
        }

        [TestMethod]
        public void DateTimeKey_KindIsIgnored_SameTicksAreSameRow()
        {
            // 決定：DateTime 只用 Ticks、不含 Kind。理由：DateTime.Equals 與目標端 timestamp without time zone
            // （消費端 NormalizeDateTimePropertiesToWithoutTimeZone 會抹掉時區）都只看 Ticks。
            var ticks = new DateTime(2026, 10, 16, 9, 30, 15, 123).Ticks;
            AssertSame(
                new object?[] { new DateTime(ticks, DateTimeKind.Utc) },
                new object?[] { new DateTime(ticks, DateTimeKind.Local) });
            AssertSame(
                new object?[] { new DateTime(ticks, DateTimeKind.Unspecified) },
                new object?[] { new DateTime(ticks, DateTimeKind.Utc) });
        }

        [TestMethod]
        public void DateTimeOffsetKey_UsesInstant_NotOffset_AndKeepsSubSecond()
        {
            var utc = new DateTimeOffset(2026, 10, 16, 1, 30, 15, 100, TimeSpan.Zero);
            AssertSame(new object?[] { utc }, new object?[] { utc.ToOffset(TimeSpan.FromHours(8)) }, "同一瞬間不同位移是同一個值");
            AssertDistinct(new object?[] { utc }, new object?[] { utc.AddMilliseconds(1) });
        }

        // ───────────── 其他可能有損的型別 ─────────────

        [TestMethod]
        public void FloatingPointKeys_AreLossless()
        {
            AssertDistinct(new object?[] { 0.1d }, new object?[] { 0.1d + 1e-16 });
            AssertDistinct(new object?[] { 1.0000001d }, new object?[] { 1.0000002d });
            AssertDistinct(new object?[] { 0.1f }, new object?[] { 0.1f + 1e-8f });
            AssertDistinct(new object?[] { double.PositiveInfinity }, new object?[] { double.NegativeInfinity });
            AssertDistinct(new object?[] { double.MaxValue }, new object?[] { double.MaxValue / 2 });
            AssertSame(new object?[] { 0.0d }, new object?[] { -0.0d }, "+0 與 -0 是同一個值");
            AssertSame(new object?[] { double.NaN }, new object?[] { -double.NaN }, "所有 NaN 是同一個值");
            AssertSame(new object?[] { 1.5d }, new object?[] { 1.5d });
        }

        [TestMethod]
        public void DecimalKeys_AreLossless_AndScaleInsensitive()
        {
            AssertDistinct(new object?[] { 1.0000000000000000001m }, new object?[] { 1.0000000000000000002m });
            AssertDistinct(new object?[] { 12.5m }, new object?[] { 12.50001m });
            AssertSame(new object?[] { 1.0m }, new object?[] { 1.00m }, "1.0m 與 1.00m 在 Equals 與資料庫都是同一個值");
            AssertSame(new object?[] { 0m }, new object?[] { 0.000m });
        }

        [TestMethod]
        public void ByteArrayKeys_UseFullContent()
        {
            AssertDistinct(new object?[] { new byte[] { 1, 2, 3 } }, new object?[] { new byte[] { 1, 2, 4 } }, "舊版 ToString 會都變成 System.Byte[]");
            AssertDistinct(new object?[] { new byte[] { 1, 2 } }, new object?[] { new byte[] { 1, 2, 0 } });
            AssertDistinct(new object?[] { new byte[0] }, new object?[] { null });
            AssertSame(new object?[] { new byte[] { 9, 8, 7 } }, new object?[] { new byte[] { 9, 8, 7 } }, "內容相同要合併，不是比參考");
        }

        [TestMethod]
        public void GuidTimeSpanDateOnlyTimeOnlyKeys_AreLossless()
        {
            var g = Guid.NewGuid();
            AssertSame(new object?[] { g }, new object?[] { new Guid(g.ToByteArray()) });
            AssertDistinct(new object?[] { g }, new object?[] { Guid.NewGuid() });

            AssertDistinct(new object?[] { TimeSpan.FromMilliseconds(1) }, new object?[] { TimeSpan.FromMilliseconds(2) });
            AssertDistinct(new object?[] { TimeSpan.FromTicks(1) }, new object?[] { TimeSpan.Zero });

            AssertDistinct(new object?[] { new DateOnly(2026, 10, 16) }, new object?[] { new DateOnly(2026, 10, 17) });
            AssertSame(new object?[] { new DateOnly(2026, 10, 16) }, new object?[] { new DateOnly(2026, 10, 16) });

            AssertDistinct(new object?[] { new TimeOnly(9, 30, 15, 100) }, new object?[] { new TimeOnly(9, 30, 15, 900) });
            AssertDistinct(new object?[] { new TimeOnly(9, 30, 15).Add(TimeSpan.FromTicks(1)) }, new object?[] { new TimeOnly(9, 30, 15) });
        }

        private enum ColorA { Red = 1, Green = 2 }
        private enum ColorB { Red = 1, Green = 2 }
        [Flags] private enum Flags8 : byte { None = 0, A = 1, B = 2, AB = 3 }

        [TestMethod]
        public void EnumKeys_AreDistinctByTypeAndValue()
        {
            AssertDistinct(new object?[] { ColorA.Red }, new object?[] { ColorA.Green });
            AssertDistinct(new object?[] { ColorA.Red }, new object?[] { ColorB.Red }, "不同列舉型別、同一個數值不是同一個鍵");
            AssertDistinct(new object?[] { ColorA.Red }, new object?[] { 1 }, "列舉與整數不同");
            AssertDistinct(new object?[] { Flags8.A }, new object?[] { Flags8.B });
            AssertDistinct(new object?[] { (Flags8)7 }, new object?[] { (Flags8)5 }, "未定義的組合值也不可退化成同一個字串");
            AssertSame(new object?[] { ColorA.Green }, new object?[] { ColorA.Green });
        }

        [TestMethod]
        public void IntegerKeys_AllWidths_AreLosslessAndTypeTaggedAgainstStrings()
        {
            AssertDistinct(new object?[] { 1 }, new object?[] { 2 });
            AssertDistinct(new object?[] { long.MaxValue }, new object?[] { long.MaxValue - 1 });
            AssertDistinct(new object?[] { ulong.MaxValue }, new object?[] { ulong.MaxValue - 1 });
            AssertDistinct(new object?[] { -1 }, new object?[] { 1 });
            AssertDistinct(new object?[] { 1 }, new object?[] { "1" }, "整數 1 與字串 \"1\" 不同");
            AssertDistinct(new object?[] { true }, new object?[] { 1 }, "bool 與整數不同");
            AssertDistinct(new object?[] { true }, new object?[] { false });
            AssertDistinct(new object?[] { 'a' }, new object?[] { "a" });
        }

        [TestMethod]
        public void NullKeys_AreDistinctFromEmptyAndMagicStrings()
        {
            AssertDistinct(new object?[] { null }, new object?[] { "" });
            AssertDistinct(new object?[] { null }, new object?[] { "\u0000null" }, "舊版用 \"\\u0000null\" 表示 null，字串剛好長這樣會衝突");
            AssertDistinct(new object?[] { null }, new object?[] { "null" });
            AssertDistinct(new object?[] { null }, new object?[] { 0 });
            AssertSame(new object?[] { null }, new object?[] { null });
        }

        // ───────────── 分隔字元／複合鍵不可碰撞 ─────────────

        [TestMethod]
        public void Strings_ContainingOldSeparators_DoNotCollide()
        {
            // 舊版 QueueKey 用 \u001f 串接、BuildBatch 用 '|' 串接
            AssertDistinct(new object?[] { "a\u001fb" }, new object?[] { "a", "b" });
            AssertDistinct(new object?[] { "a|b" }, new object?[] { "a", "b" });
            AssertDistinct(new object?[] { "a", "b|c" }, new object?[] { "a|b", "c" });
            AssertDistinct(new object?[] { "ab", "c" }, new object?[] { "a", "bc" });
            AssertDistinct(new object?[] { "a", "" }, new object?[] { "a" });
            AssertDistinct(new object?[] { "a", null }, new object?[] { "a" });
            AssertDistinct(new object?[] { "", "" }, new object?[] { "" });
        }

        [TestMethod]
        public void Strings_ThatLookLikeEncodedElements_DoNotCollide()
        {
            // 內容剛好長得像編碼後的元素，長度前綴仍可區分
            AssertDistinct(new object?[] { "S1:a" }, new object?[] { "S1:a", "S1:a" });
            var inner = Enc("x", "y");
            AssertDistinct(new object?[] { inner }, new object?[] { "x", "y" });
            AssertDistinct(new object?[] { "1:a2:bc" }, new object?[] { "a", "bc" });
            AssertDistinct(new object?[] { "a:1" }, new object?[] { "a", 1 });
        }

        [TestMethod]
        public void Strings_AreCaseSensitiveOrdinal()
        {
            AssertDistinct(new object?[] { "AGV_1" }, new object?[] { "agv_1" });
            AssertDistinct(new object?[] { "é" }, new object?[] { "e\u0301" }, "Ordinal 比較，不做 Unicode 正規化");
        }

        [TestMethod]
        public void CompositeKey_WithDateTime_Differ_ByMilliseconds()
        {
            var t = new DateTime(2026, 10, 16, 9, 30, 15);
            AssertDistinct(new object?[] { "AGV_1", t.AddMilliseconds(10) }, new object?[] { "AGV_1", t.AddMilliseconds(20) });
            AssertSame(new object?[] { "AGV_1", t.AddMilliseconds(10) }, new object?[] { "AGV_1", t.AddMilliseconds(10) });
        }

        [TestMethod]
        public void FuzzedKeyTuples_NeverCollide_UnlessElementwiseEqual()
        {
            var rnd = new Random(12345);
            string[] pieces = { "", "a", "b", "|", "\u001f", ":", "1", "S1:a", "N0:", "\u0000null", "é", "AGV_1", "I1:1", "D5:12345" };
            var t0 = new DateTime(2026, 10, 16, 9, 30, 15);

            object? RandomElement()
            {
                switch (rnd.Next(8))
                {
                    case 0: return null;
                    case 1: return pieces[rnd.Next(pieces.Length)] + pieces[rnd.Next(pieces.Length)];
                    case 2: return rnd.Next(0, 4);
                    case 3: return (long)rnd.Next(0, 4);
                    case 4: return t0.AddMilliseconds(rnd.Next(0, 4));
                    case 5: return rnd.Next(2) == 0;
                    case 6: return (ColorA)rnd.Next(1, 3);
                    default: return new byte[] { (byte)rnd.Next(0, 3) };
                }
            }

            static bool ElementEquals(object? a, object? b)
            {
                if (a is byte[] x && b is byte[] y) return x.SequenceEqual(y);
                if (a is int i && b is long l) return i == l;   // 整數寬度不同但同值視為同一個主鍵值
                if (a is long l2 && b is int i2) return i2 == l2;
                return Equals(a, b);
            }

            var seen = new Dictionary<string, object?[]>();
            for (int n = 0; n < 200_000; n++)
            {
                int len = rnd.Next(1, 4);
                var tuple = new object?[len];
                for (int i = 0; i < len; i++) tuple[i] = RandomElement();

                string enc = PostgreSqlSyncKey.EncodeKeyValues(tuple);
                if (seen.TryGetValue(enc, out var prior))
                {
                    Assert.AreEqual(prior.Length, tuple.Length, "不同長度的主鍵編成了同一個字串");
                    for (int i = 0; i < len; i++)
                        Assert.IsTrue(ElementEquals(prior[i], tuple[i]), $"碰撞：{enc} 同時代表 [{string.Join(", ", prior)}] 與 [{string.Join(", ", tuple)}]");
                }
                else
                {
                    seen[enc] = tuple;
                }
            }
        }

        // ───────────── 型別／空鍵 ─────────────

        [TestMethod]
        public void SameKey_DifferentEntityType_AreDifferentKeys()
        {
            var a = PostgreSqlSyncKey.From(Item(typeof(clsAlarmDto), "x"));
            var b = PostgreSqlSyncKey.From(Item(typeof(SecsMessageLog), "x"));
            Assert.AreNotEqual(a, b);
            Assert.AreEqual(a, PostgreSqlSyncKey.From(Item(typeof(clsAlarmDto), "x")));
            Assert.AreEqual(a.GetHashCode(), PostgreSqlSyncKey.From(Item(typeof(clsAlarmDto), "x")).GetHashCode());
        }

        [TestMethod]
        public void EmptyKeyValues_AreNeverEqual()
        {
            var a = PostgreSqlSyncKey.From(Item(typeof(clsAlarmDto)));
            var b = PostgreSqlSyncKey.From(Item(typeof(clsAlarmDto)));
            Assert.AreNotEqual(a, b, "沒有主鍵值無法判斷是否同一列，永不合併");
        }
    }
}
