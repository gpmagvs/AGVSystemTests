using AGVSystemCommonNet6;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AGVSystemTests.Common;

/// <summary>
/// Extensions.ToJson 改用「靜態共用 JsonSerializerOptions」後的行為驗證：
/// 1. 輸出必須與舊實作（每次 new JsonSerializerOptions）逐字相同
/// 2. options 實例必須被重用（同一個參考、且已被 System.Text.Json 鎖為唯讀）
/// 3. 多執行緒並行呼叫安全、輸出一致
/// 4. 配置量明顯低於舊實作
/// </summary>
[TestClass]
public class ExtensionsToJsonStaticOptionsTests
{
    /// <summary>舊實作（修正前 Extensions.ToJson 的逐字複本），作為輸出比對基準。</summary>
    private static string OldToJson(object obj, Formatting format = Formatting.Indented)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                IncludeFields = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                WriteIndented = format == Formatting.Indented
            };
            return System.Text.Json.JsonSerializer.Serialize(obj, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return "{}";
        }
    }

    public class WithFields
    {
        public int PublicField = 7;
        public string Text { get; set; } = "車輛 <A&B> 'x' \"y\" \u2603 😀";
        public DateTime When { get; set; } = new DateTime(2026, 10, 17, 8, 53, 0, DateTimeKind.Utc);
        public double[] Values { get; set; } = { 1.5, -0.0, 1e-7, 12345678.9012345 };
        public Dictionary<string, int?> Map { get; set; } = new() { ["a"] = 1, ["中文"] = null };
        public List<WithFields>? Children { get; set; }
        public string? NullText { get; set; }
        public clsEnums.VMS_GROUP Group { get; set; } = clsEnums.VMS_GROUP.GPM_FORK;
    }

    private static IEnumerable<object> Samples()
    {
        yield return new { Name = "AGV_1", Pos = new { X = 1.25, Y = -3 }, Tags = new[] { 1, 2, 3 }, Note = "走行馬達保養" };
        yield return new WithFields { Children = new List<WithFields> { new WithFields(), new WithFields { Text = "" } } };
        yield return new clsAGVStateDto { AGV_Name = "AGV_1", AGV_Description = "叉車 <1>", BatteryLevel_1 = 87.5, Theta = 12.5, DedicateSiteTagsString = "1,2,3" };
        yield return new List<int> { 1, 2, 3 };
        yield return "just a string 中文";
        yield return 42;
        yield return new Dictionary<string, object?> { ["k"] = null, ["nested"] = new { a = 1 } };
    }

    [TestMethod]
    public void ToJson_Indented_SameAsOldImplementation()
    {
        foreach (var s in Samples())
            Assert.AreEqual(OldToJson(s, Formatting.Indented), s.ToJson(Formatting.Indented), s.GetType().Name);
    }

    [TestMethod]
    public void ToJson_DefaultFormat_IsIndentedAndSameAsOld()
    {
        foreach (var s in Samples())
        {
            string actual = s.ToJson();
            Assert.AreEqual(OldToJson(s), actual, s.GetType().Name);
            if (s is not string and not int)
                Assert.IsTrue(actual.Contains('\n'), "預設應為縮排輸出：" + s.GetType().Name);
        }
    }

    [TestMethod]
    public void ToJson_None_IsCompactAndSameAsOld()
    {
        foreach (var s in Samples())
        {
            string actual = s.ToJson(Formatting.None);
            Assert.AreEqual(OldToJson(s, Formatting.None), actual, s.GetType().Name);
            Assert.IsFalse(actual.Contains('\n'), "Formatting.None 不應含換行：" + s.GetType().Name);
        }
    }

    [TestMethod]
    public void ToJson_UnicodeNotEscaped_AsBefore()
    {
        string json = new { Name = "車輛" }.ToJson(Formatting.None);
        Assert.AreEqual("{\"Name\":\"車輛\"}", json);
    }

    [TestMethod]
    public void ToJson_IncludeFields_AsBefore()
    {
        string json = new WithFields().ToJson(Formatting.None);
        StringAssert.Contains(json, "\"PublicField\":7");
    }

    [TestMethod]
    public void ToJson_NullInput_SameAsOld()
    {
        Assert.AreEqual(OldToJson(null!, Formatting.None), ((object?)null)!.ToJson(Formatting.None));
        Assert.AreEqual(OldToJson(null!), ((object?)null)!.ToJson());
    }

    [TestMethod]
    public void ToJson_SerializationError_ReturnsEmptyObject_AsBefore()
    {
        // 循環參照會丟 JsonException，兩種實作都應吞掉並回傳 "{}"
        var a = new WithFields();
        a.Children = new List<WithFields> { a };
        Assert.AreEqual("{}", OldToJson(a));
        Assert.AreEqual("{}", a.ToJson());
        // 例外後共用 options 仍可正常使用
        Assert.AreEqual("{\"x\":1}", new { x = 1 }.ToJson(Formatting.None));
    }

    private static JsonSerializerOptions GetStaticOptions(string fieldName)
    {
        var f = typeof(Extensions).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(f, $"找不到 Extensions.{fieldName}（共用 options 欄位被改名或移除？）");
        Assert.IsTrue(f!.IsInitOnly, $"{fieldName} 應為 static readonly");
        return (JsonSerializerOptions)f.GetValue(null)!;
    }

    [TestMethod]
    public void ToJson_OptionsInstance_IsReused_AndConfiguredLikeBefore()
    {
        var indented1 = GetStaticOptions("_toJsonIndentedOptions");
        var compact1 = GetStaticOptions("_toJsonCompactOptions");
        Assert.AreNotSame(indented1, compact1);

        new { a = 1 }.ToJson(Formatting.Indented);
        new { a = 1 }.ToJson(Formatting.None);
        new { b = 2 }.ToJson();

        // 呼叫之後仍是同一個實例（沒有被重新 new）
        Assert.AreSame(indented1, GetStaticOptions("_toJsonIndentedOptions"));
        Assert.AreSame(compact1, GetStaticOptions("_toJsonCompactOptions"));

        // System.Text.Json 在第一次使用後會把 options 鎖成唯讀：代表共用實例真的被用來序列化，且之後不可變
        Assert.IsTrue(indented1.IsReadOnly);
        Assert.IsTrue(compact1.IsReadOnly);

        Assert.IsTrue(indented1.WriteIndented);
        Assert.IsFalse(compact1.WriteIndented);
        foreach (var o in new[] { indented1, compact1 })
        {
            Assert.IsTrue(o.IncludeFields);
            Assert.IsNull(o.PropertyNamingPolicy);
            Assert.IsNotNull(o.Encoder);
            Assert.IsFalse(o.PropertyNameCaseInsensitive);
            Assert.AreEqual(System.Text.Json.Serialization.JsonIgnoreCondition.Never, o.DefaultIgnoreCondition);
        }
        // 編碼器與舊實作行為相同（中文不跳脫）
        Assert.AreEqual("車", compact1.Encoder!.Encode("車"));
    }

    [TestMethod]
    public void ToJson_OptionsAreImmutableAfterFirstUse()
    {
        var o = GetStaticOptions("_toJsonCompactOptions");
        new { a = 1 }.ToJson(Formatting.None);
        Assert.ThrowsException<InvalidOperationException>(() => o.WriteIndented = true);
    }

    [TestMethod]
    public void ToJson_ConcurrentCalls_AreSafeAndConsistent()
    {
        var samples = Samples().ToArray();
        var expectedIndented = samples.Select(s => OldToJson(s, Formatting.Indented)).ToArray();
        var expectedCompact = samples.Select(s => OldToJson(s, Formatting.None)).ToArray();
        var errors = new ConcurrentQueue<string>();

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, worker =>
        {
            for (int i = 0; i < 300; i++)
            {
                int idx = (worker + i) % samples.Length;
                var s = samples[idx];
                if (s.ToJson(Formatting.Indented) != expectedIndented[idx]) errors.Enqueue($"indented mismatch idx={idx}");
                if (s.ToJson(Formatting.None) != expectedCompact[idx]) errors.Enqueue($"compact mismatch idx={idx}");
            }
        });

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors.Take(5)));
    }

    [TestMethod]
    public void ToJson_AllocatesMuchLessThanOldImplementation()
    {
        var obj = new clsAGVStateDto { AGV_Name = "AGV_1", AGV_Description = "叉車", BatteryLevel_1 = 80 };
        const int n = 200;
        // 暖機（JIT、型別 metadata 快取）
        for (int i = 0; i < 5; i++) { obj.ToJson(); OldToJson(obj); }

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < n; i++) obj.ToJson();
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - a0;

        a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < n; i++) OldToJson(obj);
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - a0;

        Console.WriteLine($"ToJson x{n}: new={newBytes / n} B/call old={oldBytes / n} B/call");
        Assert.IsTrue(newBytes * 10 < oldBytes, $"新實作配置量應小於舊實作 1/10（實測約 1/65）：new={newBytes / n} B/call old={oldBytes / n} B/call");
    }
}
