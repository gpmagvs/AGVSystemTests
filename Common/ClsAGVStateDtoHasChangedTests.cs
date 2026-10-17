using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Maintainance;
using Newtonsoft.Json;
using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AGVSystemTests.Common;

/// <summary>
/// clsAGVStateDto.HasChanged（net8 專用：單次序列化、以 ShouldSerialize 略過 Theta／BatteryVoltages／BatteryVoltageMin）
/// 與舊實作（複製 → 歸零 → 兩次序列化比對）的結果等價驗證。
/// </summary>
[TestClass]
public class ClsAGVStateDtoHasChangedTests
{
    private static string OldToJson(object obj)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                IncludeFields = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                WriteIndented = true
            };
            return System.Text.Json.JsonSerializer.Serialize(obj, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return "{}";
        }
    }

    private static clsAGVStateDto OldClone(clsAGVStateDto d) => JsonConvert.DeserializeObject<clsAGVStateDto>(OldToJson(d))!;

    /// <summary>舊 HasChanged 的逐字複本（含 round-trip clone），作為等價比對基準。</summary>
    private static bool OldHasChanged(clsAGVStateDto self, clsAGVStateDto newState)
    {
        var clone_ = OldClone(self);
        var clone_new = OldClone(newState);
        clone_.Theta = clone_new.Theta = 0;
        clone_.BatteryVoltages = clone_new.BatteryVoltages = Array.Empty<int>();
        clone_.BatteryVoltageMin = clone_new.BatteryVoltageMin = 0;
        return OldToJson(clone_) != OldToJson(clone_new);
    }

    private static clsAGVStateDto Baseline() => new clsAGVStateDto
    {
        AGV_Name = "AGV_1",
        AGV_ID = "1",
        Enabled = true,
        AGV_Description = "叉車 <1>",
        CurrentLocation = "5",
        BatteryLevel_1 = 80.5,
        BatteryLevel_2 = 79,
        Theta = 10,
        BatteryVoltageMin = 26000,
        BatteryVoltages = new[] { 26000, 26100 },
        DedicateSiteTagsString = "1,2",
        MaintainSettings = new List<VehicleMaintain> { new VehicleMaintain("AGV_1", MAINTAIN_ITEM.HORIZON_MOTOR) { currentValue = 5 } },
        LastRotationalForcedChargeCompletedTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        TaskETA = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc),
    };

    private static readonly string[] IgnoredProps = { nameof(clsAGVStateDto.Theta), nameof(clsAGVStateDto.BatteryVoltages), nameof(clsAGVStateDto.BatteryVoltageMin) };

    private static object MutatedValue(PropertyInfo p, object? current)
    {
        Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (t == typeof(string)) return ((string?)current ?? "") + "_changed";
        if (t == typeof(bool)) return !(bool)(current ?? false);
        if (t == typeof(int)) return (int)(current ?? 0) + 1;
        if (t == typeof(double)) return (double)(current ?? 0.0) + 1.25;
        if (t == typeof(DateTime)) return ((DateTime?)current ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).AddMinutes(1);
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t).Cast<object>().ToArray();
            int idx = Array.IndexOf(values, current);
            return values[(idx + 1) % values.Length];
        }
        if (t == typeof(int[])) return new[] { 9, 8, 7 };
        if (t == typeof(List<int>)) return new List<int> { 4, 5, 6 };
        if (t == typeof(List<VehicleMaintain>)) return new List<VehicleMaintain> { new VehicleMaintain("AGV_1", MAINTAIN_ITEM.FORKLIFT_BELT) { currentValue = 99 } };
        throw new NotSupportedException($"測試尚未處理屬性型別：{p.Name} {p.PropertyType}");
    }

    private static IEnumerable<PropertyInfo> SettableProps() =>
        typeof(clsAGVStateDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

    [TestMethod]
    public void HasChanged_EqualStates_ReturnsFalse_AndSameAsOld()
    {
        var a = Baseline();
        var b = Baseline();
        Assert.IsFalse(a.HasChanged(b));
        Assert.IsFalse(b.HasChanged(a));
        Assert.IsFalse(a.HasChanged(a));
        Assert.AreEqual(OldHasChanged(a, b), a.HasChanged(b));
    }

    [TestMethod]
    public void HasChanged_IgnoredFields_DoNotCountAsChange()
    {
        var a = Baseline();
        var b = Baseline();
        b.Theta = 359.9;
        b.BatteryVoltageMin = 1;
        b.BatteryVoltages = Array.Empty<int>();
        Assert.IsFalse(a.HasChanged(b));
        Assert.IsFalse(b.HasChanged(a));
        Assert.AreEqual(OldHasChanged(a, b), a.HasChanged(b));
    }

    [TestMethod]
    public void HasChanged_EveryPublicSettableProperty_MatchesOldImplementation()
    {
        var props = SettableProps().ToList();
        Assert.IsTrue(props.Count > 40, "反射應取得所有可寫屬性，目前：" + props.Count);
        var failures = new List<string>();

        foreach (var p in props)
        {
            var a = Baseline();
            var b = Baseline();
            p.SetValue(b, MutatedValue(p, p.GetValue(b)));

            bool expectedChanged = !IgnoredProps.Contains(p.Name);
            bool oldResult = OldHasChanged(a, b);
            bool newAB = a.HasChanged(b);
            bool newBA = b.HasChanged(a);

            if (newAB != oldResult) failures.Add($"{p.Name}: old={oldResult} new={newAB}");
            if (newBA != oldResult) failures.Add($"{p.Name}(反向): old={oldResult} new={newBA}");
            if (newAB != expectedChanged) failures.Add($"{p.Name}: 預期 {expectedChanged} 實際 {newAB}");
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void HasChanged_DedicateSiteTagsChange_IsDetected()
    {
        var a = Baseline();
        var b = Baseline();
        b.DedicateSiteTags = new List<int> { 7, 8 };
        Assert.IsTrue(a.HasChanged(b));
        Assert.AreEqual(OldHasChanged(a, b), a.HasChanged(b));
    }

    [TestMethod]
    public void HasChanged_MaintainSettingsChange_IsDetected()
    {
        var a = Baseline();
        var b = Baseline();
        b.MaintainSettings[0].currentValue += 1;
        Assert.IsTrue(a.HasChanged(b));
        Assert.AreEqual(OldHasChanged(a, b), a.HasChanged(b));

        b = Baseline();
        b.MaintainSettings = null!;
        Assert.AreEqual(OldHasChanged(a, b), a.HasChanged(b));
    }

    [TestMethod]
    public void HasChanged_DbCacheStyle_EmptyVoltageArrays_NotTreatedAsChange()
    {
        // DB 快取的 NotMapped 電壓欄位為預設值；即時狀態有電壓 → 不可因此每輪判定有變更
        var cache = Baseline();
        cache.BatteryVoltages = Array.Empty<int>();
        cache.BatteryVoltageMin = 0;
        var live = Baseline();
        live.BatteryVoltages = new[] { 25000, 25001, 25002 };
        live.BatteryVoltageMin = 25000;
        Assert.IsFalse(cache.HasChanged(live));
        Assert.AreEqual(OldHasChanged(cache, live), cache.HasChanged(live));
    }

    [TestMethod]
    public void HasChanged_UnicodeAndSpecialCharacters_MatchOld()
    {
        var a = Baseline();
        var b = Baseline();
        b.AGV_Description = "叉車 <2> & '😀'";
        Assert.IsTrue(a.HasChanged(b));
        Assert.AreEqual(OldHasChanged(a, b), a.HasChanged(b));
    }

    [TestMethod]
    public void HasChanged_DoesNotMutateEitherInstance()
    {
        var a = Baseline();
        var b = Baseline();
        b.Theta = 77;
        b.BatteryVoltageMin = 123;
        string aBefore = OldToJson(a), bBefore = OldToJson(b);
        a.HasChanged(b);
        Assert.AreEqual(aBefore, OldToJson(a));
        Assert.AreEqual(bBefore, OldToJson(b));
        Assert.AreEqual(77, b.Theta);
        Assert.AreEqual(123, b.BatteryVoltageMin);
    }

    [TestMethod]
    public void HasChanged_ConcurrentCalls_AreSafeAndConsistent()
    {
        var a = Baseline();
        var same = Baseline();
        same.Theta = 5;
        var diff = Baseline();
        diff.CurrentLocation = "99";
        int wrong = 0;

        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (int i = 0; i < 300; i++)
            {
                if (a.HasChanged(same)) Interlocked.Increment(ref wrong);
                if (!a.HasChanged(diff)) Interlocked.Increment(ref wrong);
            }
        });
        Assert.AreEqual(0, wrong);
    }

    [TestMethod]
    public void HasChanged_AllocatesFarLessThanOldCloneBasedImplementation()
    {
        // 沒有可注入的計數縫隙（SerializeForCompare 是 private）；改以配置量間接驗證「不再 clone（序列化＋反序列化）」。
        var a = Baseline();
        var b = Baseline();
        b.BatteryLevel_1 += 1;
        const int n = 200;
        for (int i = 0; i < 5; i++) { a.HasChanged(b); OldHasChanged(a, b); }

        long t0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < n; i++) a.HasChanged(b);
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - t0;

        t0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < n; i++) OldHasChanged(a, b);
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - t0;

        Console.WriteLine($"HasChanged x{n}: new={newBytes / n} B/call old={oldBytes / n} B/call");
        Assert.IsTrue(newBytes * 40 < oldBytes, $"新實作配置量應小於舊實作 1/40（實測約 1/100）：new={newBytes / n} B/call old={oldBytes / n} B/call");
    }

    [TestMethod]
    public void HasChanged_UsesSharedStaticOptions_WithShouldSerializeModifier()
    {
        var f = typeof(clsAGVStateDto).GetField("_hasChangedJsonOptions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(f);
        Assert.IsTrue(f!.IsInitOnly);
        var o1 = (JsonSerializerOptions)f.GetValue(null)!;
        Baseline().HasChanged(Baseline());
        Assert.AreSame(o1, f.GetValue(null));
        Assert.IsTrue(o1.IsReadOnly);

        var info = o1.GetTypeInfo(typeof(clsAGVStateDto));
        foreach (var name in IgnoredProps)
        {
            var prop = info.Properties.Single(x => x.Name == name);
            Assert.IsNotNull(prop.ShouldSerialize, name);
            Assert.IsFalse(prop.ShouldSerialize!(Baseline(), null), name);
        }
        Assert.IsTrue(info.Properties.Where(x => !IgnoredProps.Contains(x.Name)).All(x => x.ShouldSerialize == null),
            "除了三個略過欄位，其餘屬性不應被加上 ShouldSerialize 條件");
    }
}
