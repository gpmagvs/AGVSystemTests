using AGVSystemCommonNet6;
using System.Reflection;
using AGVSystemCommonNet6.MAP;
using VMSystem.Extensions;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// 單次路徑規劃成本 (報告 F2)：
    /// - MapPath.PathID 快取 (CommonNet6)：不再每次存取組字串，但索引被改時仍然正確
    /// - PathFinder 距離矩陣：以字典取代 O(E²) 的 FirstOrDefault，結果與舊演算法逐格相同；並以「地圖內容簽章」快取
    /// - Map 差異覆蓋 (VMSystem.CreateOverlay) 取代整張 JSON 深拷貝
    /// 需要 CommonNet6 PR (PathID 快取 / 距離矩陣快取) 先 merge。
    /// </summary>
    [TestClass]
    public class PathPlanningCostTests
    {
        // ───────────────────────── 測試用地圖 ─────────────────────────

        private static MapPoint Pt(int tag, double x, double y, bool enable = true) => new MapPoint
        {
            TagNumber = tag,
            X = x,
            Y = y,
            Enable = enable,
            StationType = STATION_TYPE.Normal,
            Name = tag.ToString(),
        };

        private static void Link(Map map, int from, int to, double weight = 1, bool bidirectional = true)
        {
            map.Segments.Add(new MapPath { StartPtIndex = from, EndPtIndex = to, Weight = weight });
            if (bidirectional)
                map.Segments.Add(new MapPath { StartPtIndex = to, EndPtIndex = from, Weight = weight });
        }

        /// <summary>
        ///  0 - 1 - 2 - 3
        ///  |           |
        ///  4 ---- 5 ---+   (0-4, 4-5, 5-3)
        /// 上排 0-1-2-3 每段長 10，下繞 0-4(10) 4-5(30) 5-3(10)，所以預設走上排。
        /// </summary>
        private static Map CreateSmallMap()
        {
            var map = new Map { Name = "t", Points = new Dictionary<int, MapPoint>() };
            map.Points[0] = Pt(100, 0, 0);
            map.Points[1] = Pt(101, 10, 0);
            map.Points[2] = Pt(102, 20, 0);
            map.Points[3] = Pt(103, 30, 0);
            map.Points[4] = Pt(104, 0, 10);
            map.Points[5] = Pt(105, 30, 10);
            Link(map, 0, 1); Link(map, 1, 2); Link(map, 2, 3);
            Link(map, 0, 4); Link(map, 4, 5); Link(map, 5, 3);
            return map;
        }

        private static PathFinder.PathFinderOption Dijkstra(Map map, params int[] constrain) => new PathFinder.PathFinderOption
        {
            Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
            OnlyNormalPoint = true,
            Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
            MapUse = map,
            ConstrainTags = constrain.ToList(),
        };

        private static List<int> Tags(PathFinder.clsPathInfo info) => info.stations.Select(s => s.TagNumber).ToList();

        [TestInitialize]
        public void Init()
        {
            PathFinderDistanceMatrixCache.Enabled = true;
            PathFinderDistanceMatrixCache.Clear();
            PathFinderDistanceMatrixCache.ResetStats();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PathFinderDistanceMatrixCache.Enabled = true;
            PathFinderDistanceMatrixCache.Clear();
        }

        // ───────────────────────── MapPath.PathID ─────────────────────────

        [TestMethod]
        public void PathID_HasSameFormatAsBefore()
        {
            var p = new MapPath { StartPtIndex = 12, EndPtIndex = 345 };
            Assert.AreEqual("12_345", p.PathID);
        }

        [TestMethod]
        public void PathID_IsComputedOnce_SameStringInstanceOnRepeatedAccess()
        {
            var p = new MapPath { StartPtIndex = 1, EndPtIndex = 2 };
            string first = p.PathID;
            for (int i = 0; i < 1000; i++)
                Assert.AreSame(first, p.PathID, "沒改索引時必須回傳同一個字串實例 (不重新組字串)");
        }

        [TestMethod]
        public void PathID_RepeatedAccess_AllocatesNothing()
        {
            var p = new MapPath { StartPtIndex = 7, EndPtIndex = 8 };
            _ = p.PathID;   // 第一次可能配置
            long before = GC.GetAllocatedBytesForCurrentThread();
            int len = 0;
            for (int i = 0; i < 100_000; i++)
                len += p.PathID.Length;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(300_000, len);
            Assert.IsTrue(allocated < 4096, $"重複存取 100,000 次配置了 {allocated} bytes (舊版約 100,000 個字串 ≈ 4 MB)");
        }

        [TestMethod]
        public void PathID_FollowsMutableIndexes_InvalidatesCache()
        {
            var p = new MapPath { StartPtIndex = 1, EndPtIndex = 2 };
            Assert.AreEqual("1_2", p.PathID);

            p.StartPtIndex = 5;
            Assert.AreEqual("5_2", p.PathID);
            p.EndPtIndex = 9;
            Assert.AreEqual("5_9", p.PathID);
            p.StartPtIndex = 1; p.EndPtIndex = 2;
            Assert.AreEqual("1_2", p.PathID);
        }

        [TestMethod]
        public void PathID_DefaultIndexes_AndSerializationRoundTrips()
        {
            Assert.AreEqual("0_0", new MapPath().PathID);

            var original = new MapPath { StartPtIndex = 3, EndPtIndex = 4, Weight = 2.5 };
            _ = original.PathID;

            // Newtonsoft (地圖檔)
            var nj = Newtonsoft.Json.JsonConvert.SerializeObject(original);
            StringAssert.Contains(nj, "\"PathID\":\"3_4\"");
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<MapPath>(nj)!;
            Assert.AreEqual("3_4", back.PathID);
            Assert.AreEqual(2.5, back.Weight);

            // System.Text.Json (Map.Clone 走這條，IncludeFields=true)
            var sj = System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
            StringAssert.Contains(sj, "\"PathID\":\"3_4\"");
            StringAssert.DoesNotMatch(sj, new System.Text.RegularExpressions.Regex("_pathIdCache|PathIdCache"), "快取欄位不可被序列化");
            var back2 = System.Text.Json.JsonSerializer.Deserialize<MapPath>(sj)!;
            Assert.AreEqual("3_4", back2.PathID);
        }

        // ───────────────────────── 距離矩陣 ─────────────────────────

        /// <summary>舊版 CreateDistanceMatrix 的逐字複本 (以 PathID 字串 + FirstOrDefault)，當作基準</summary>
        private static int[,] LegacyMatrix(List<KeyValuePair<int, MapPoint>> stations, List<MapPath> pathes)
        {
            int n = stations.Count;
            int[,] graph = new int[n, n];
            for (int row = 0; row < n; row++)
            {
                var start = stations[row];
                int startIndex = start.Key;
                var near = pathes.FindAll(path => path.StartPtIndex == startIndex).Select(path => path.EndPtIndex).ToList();
                for (int col = 0; col < n; col++)
                {
                    var ele = stations[col];
                    int weight = 0;
                    if (near.Contains(ele.Key))
                    {
                        var pathID = startIndex + "_" + ele.Key;
                        var found = pathes.FirstOrDefault(path => path.PathID == pathID);
                        double w = 1.0;
                        if (found != null) w = found.Weight <= 0 ? 1.0 : found.Weight;
                        double distance = Math.Sqrt(Math.Pow(ele.Value.X - start.Value.X, 2) + Math.Pow(ele.Value.Y - start.Value.Y, 2)) / w;
                        weight = int.Parse(Math.Round(distance * 10000, 0).ToString());
                    }
                    graph[row, col] = weight;
                }
            }
            return graph;
        }

        private static int[,] CallNewMatrix(PathFinder finder, List<KeyValuePair<int, MapPoint>> stations, List<MapPath> pathes)
        {
            var m = typeof(PathFinder).GetMethod("CreateDistanceMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var args = new object[] { stations, pathes };
            return (int[,])m.Invoke(finder, args)!;
        }

        [TestMethod]
        public void DistanceMatrix_EqualsLegacyAlgorithm_OnRandomGraphWithDuplicatesAndZeroWeights()
        {
            var rnd = new Random(12345);
            var map = new Map { Points = new Dictionary<int, MapPoint>() };
            for (int i = 0; i < 80; i++)
                map.Points[i * 3] = Pt(1000 + i, rnd.NextDouble() * 100, rnd.NextDouble() * 100);   // 索引不連續
            var keys = map.Points.Keys.ToList();
            for (int e = 0; e < 400; e++)
            {
                int a = keys[rnd.Next(keys.Count)], b = keys[rnd.Next(keys.Count)];
                double w = rnd.Next(0, 4) == 0 ? 0 : (rnd.Next(0, 5) == 0 ? -1 : 0.5 + rnd.NextDouble() * 3);  // 含 0 與負數權重
                map.Segments.Add(new MapPath { StartPtIndex = a, EndPtIndex = b, Weight = w });
            }
            // 刻意放重複路段：舊版 FirstOrDefault 取第一條
            map.Segments.Add(new MapPath { StartPtIndex = keys[0], EndPtIndex = keys[1], Weight = 7 });
            map.Segments.Add(new MapPath { StartPtIndex = keys[0], EndPtIndex = keys[1], Weight = 9 });
            // 參與規劃的站點只取一部分 (模擬 ConstrainTags / Enable 過濾後)
            var stations = map.Points.Where(kv => kv.Key % 2 == 0 || kv.Key % 3 == 0).OrderBy(kv => kv.Key).ToList();

            var expected = LegacyMatrix(stations, map.Segments);
            var actual = CallNewMatrix(new PathFinder(), stations, map.Segments);

            Assert.AreEqual(expected.GetLength(0), actual.GetLength(0));
            for (int r = 0; r < expected.GetLength(0); r++)
                for (int c = 0; c < expected.GetLength(1); c++)
                    Assert.AreEqual(expected[r, c], actual[r, c], $"graph[{r},{c}] 與舊演算法不同");
        }

        [TestMethod]
        public void DistanceMatrix_Creation_AllocatesFarLessThanLegacy()
        {
            var rnd = new Random(7);
            var map = new Map { Points = new Dictionary<int, MapPoint>() };
            const int N = 500;
            for (int i = 0; i < N; i++) map.Points[i] = Pt(i + 1, rnd.NextDouble() * 500, rnd.NextDouble() * 500);
            for (int i = 0; i < N; i++)
                for (int k = 1; k <= 4; k++)
                    map.Segments.Add(new MapPath { StartPtIndex = i, EndPtIndex = (i + k * 17) % N, Weight = 1 });
            var stations = map.Points.OrderBy(kv => kv.Key).ToList();
            var finder = new PathFinder();

            // 預熱 (JIT / 型別初始化)
            _ = LegacyMatrix(stations.Take(10).ToList(), map.Segments); _ = CallNewMatrix(finder, stations.Take(10).ToList(), map.Segments);

            long b0 = GC.GetAllocatedBytesForCurrentThread();
            _ = LegacyMatrix(stations, map.Segments);
            long legacy = GC.GetAllocatedBytesForCurrentThread() - b0;

            long b1 = GC.GetAllocatedBytesForCurrentThread();
            _ = CallNewMatrix(finder, stations, map.Segments);
            long now = GC.GetAllocatedBytesForCurrentThread() - b1;

            long cellsBytes = (long)N * N * 4;
            long legacyOverhead = legacy - cellsBytes;     // 扣掉矩陣本身 (兩者都要配置)，只比較「額外垃圾」
            long nowOverhead = now - cellsBytes;
            Assert.IsTrue(nowOverhead * 5 < legacyOverhead, $"額外配置：新版 {nowOverhead} bytes，舊版 {legacyOverhead} bytes (矩陣本身 {cellsBytes} bytes)；期待新版至少少 5 倍");
        }

        [TestMethod]
        public void MatrixCache_SecondCallWithSameMap_Hits_AndGivesSameResult()
        {
            var map = CreateSmallMap();
            var finder = new PathFinder();

            var first = finder.FindShortestPath(100, 103, Dijkstra(map));
            Assert.AreEqual(0, PathFinderDistanceMatrixCache.Hits);
            Assert.AreEqual(1, PathFinderDistanceMatrixCache.Misses);

            var second = finder.FindShortestPath(100, 103, Dijkstra(map));
            Assert.AreEqual(1, PathFinderDistanceMatrixCache.Hits, "同一份地圖內容第二次應命中快取");
            CollectionAssert.AreEqual(Tags(first), Tags(second));
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(first));

            // 不同起訖點也共用同一份矩陣 (矩陣只依站點與路段，不依起訖)
            var other = finder.FindShortestPath(104, 103, Dijkstra(map));
            Assert.AreEqual(2, PathFinderDistanceMatrixCache.Hits);
            CollectionAssert.AreEqual(new[] { 104, 105, 103 }, Tags(other));
        }

        [TestMethod]
        public void MatrixCache_ResultsIdenticalWithCacheOnAndOff()
        {
            var map = CreateSmallMap();
            var finder = new PathFinder();
            var cases = new[] { (100, 103), (103, 100), (104, 102), (105, 101) };

            PathFinderDistanceMatrixCache.Enabled = false;
            var off = cases.Select(c => Tags(finder.FindShortestPath(c.Item1, c.Item2, Dijkstra(map)))).ToList();
            Assert.AreEqual(0, PathFinderDistanceMatrixCache.Hits);

            PathFinderDistanceMatrixCache.Enabled = true;
            var on1 = cases.Select(c => Tags(finder.FindShortestPath(c.Item1, c.Item2, Dijkstra(map)))).ToList();
            var on2 = cases.Select(c => Tags(finder.FindShortestPath(c.Item1, c.Item2, Dijkstra(map)))).ToList();
            Assert.IsTrue(PathFinderDistanceMatrixCache.Hits > 0);
            for (int i = 0; i < cases.Length; i++)
            {
                CollectionAssert.AreEqual(off[i], on1[i]);
                CollectionAssert.AreEqual(off[i], on2[i]);
            }
        }

        [TestMethod]
        public void MatrixCache_DynamicallyClosedPath_Misses_AndNeverReturnsStaleMatrix()
        {
            var map = CreateSmallMap();
            var finder = new PathFinder();
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));
            _ = finder.FindShortestPath(100, 103, Dijkstra(map));
            Assert.AreEqual(1, PathFinderDistanceMatrixCache.Hits);

            // VMSystem 會原地動態關閉路段 (Segments.Remove)，同一個 Map 物件、內容變了
            var closed = map.Segments.First(s => s.PathID == "1_2");
            map.Segments.Remove(closed);
            long missesBefore = PathFinderDistanceMatrixCache.Misses;
            var detour = finder.FindShortestPath(100, 103, Dijkstra(map));
            Assert.AreEqual(missesBefore + 1, PathFinderDistanceMatrixCache.Misses, "路段被關閉後必須 miss");
            CollectionAssert.AreEqual(new[] { 100, 104, 105, 103 }, Tags(detour), "關閉 1->2 後應改走下繞路徑");

            // 恢復路段 (AddPathDynamic)：回到原本的結果
            map.Segments.Add(closed);
            // 順序不同 (被加到最後) 也是不同簽章 → 重建，但結果正確
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));
        }

        [TestMethod]
        public void MatrixCache_WeightChange_PointDisabled_Moved_AndConstrainTags_AllInvalidate()
        {
            var map = CreateSmallMap();
            var finder = new PathFinder();
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));

            // 權重：把 0->1 變得很貴 (距離 / 權重 → 權重越小越遠)
            foreach (var s in map.Segments.Where(s => s.PathID == "0_1" || s.PathID == "1_0")) s.Weight = 0.01;
            CollectionAssert.AreEqual(new[] { 100, 104, 105, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))), "權重改變必須讓快取失效");
            foreach (var s in map.Segments.Where(s => s.PathID == "0_1" || s.PathID == "1_0")) s.Weight = 1;
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));

            // 點位停用 (站點清單改變)
            map.Points[2].Enable = false;
            CollectionAssert.AreEqual(new[] { 100, 104, 105, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));
            map.Points[2].Enable = true;

            // 座標被改 (距離改變)：把 104/105 拉近到很短，下繞路徑變成比上排短
            map.Points[1].X = 10; map.Points[2].X = 20;
            map.Points[3].X = 30;
            map.Points[0].Y = 0;
            map.Points[1].Y = 40; map.Points[2].Y = 40;          // 上排拉遠
            CollectionAssert.AreEqual(new[] { 100, 104, 105, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))), "座標改變必須讓快取失效");

            // 限制點位 (ConstrainTags) 是另一份站點清單 → 另一份矩陣，彼此不污染
            map.Points[1].Y = 0; map.Points[2].Y = 0;
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));
            CollectionAssert.AreEqual(new[] { 100, 104, 105, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map, 102))));
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));
        }

        [TestMethod]
        public void MatrixCache_IsBounded()
        {
            var finder = new PathFinder();
            for (int i = 0; i < PathFinderDistanceMatrixCache.MaxEntries * 3; i++)
            {
                var map = CreateSmallMap();
                map.Points[0].X = -i;                  // 每次內容都不同
                _ = finder.FindShortestPath(100, 103, Dijkstra(map));
            }
            Assert.IsTrue(PathFinderDistanceMatrixCache.Count <= PathFinderDistanceMatrixCache.MaxEntries,
                $"快取份數 {PathFinderDistanceMatrixCache.Count} 超過上限 {PathFinderDistanceMatrixCache.MaxEntries}");
        }

        [TestMethod]
        public void MatrixCache_Concurrent_NoExceptionsAndCorrectResults()
        {
            var map = CreateSmallMap();
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 200, i =>
            {
                try
                {
                    var tags = Tags(new PathFinder().FindShortestPath(100, 103, Dijkstra(map)));
                    if (!tags.SequenceEqual(new[] { 100, 101, 102, 103 })) errors.Add(string.Join(",", tags));
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            });
            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors.Take(3)));
        }

        // ───────────────────────── Map 差異覆蓋 ─────────────────────────

        [TestMethod]
        public void Overlay_RemovingSegments_DoesNotAffectBaseMap()
        {
            var map = CreateSmallMap();
            int baseSegments = map.Segments.Count;
            var overlay = map.CreateOverlay();

            Assert.AreEqual(baseSegments, overlay.Segments.Count);
            Assert.IsTrue(overlay.TryRemovePathDynamic(map.Points[1], map.Points[2]).removed);
            Assert.AreEqual(baseSegments - 1, overlay.Segments.Count);
            Assert.AreEqual(baseSegments, map.Segments.Count, "覆蓋地圖移除路段不可影響原地圖");

            Assert.AreNotSame(map.Segments, overlay.Segments);
            Assert.AreNotSame(map.Points, overlay.Points);
            Assert.AreNotSame(map.Regions, overlay.Regions);
            overlay.Points.Remove(0);
            Assert.IsTrue(map.Points.ContainsKey(0));
        }

        [TestMethod]
        public void Overlay_PathFindingOnOverlay_MatchesPathFindingOnDeepClone()
        {
            var map = CreateSmallMap();
            var overlay = map.CreateOverlay();
            var clone = map.Clone();
            overlay.TryRemovePathDynamic(map.Points[1], map.Points[2]);
            clone.TryRemovePathDynamic(clone.Points[1], clone.Points[2]);

            var finder = new PathFinder();
            var onOverlay = Tags(finder.FindShortestPath(100, 103, Dijkstra(overlay)));
            var onClone = Tags(finder.FindShortestPath(100, 103, Dijkstra(clone)));
            CollectionAssert.AreEqual(onClone, onOverlay);
            CollectionAssert.AreEqual(new[] { 100, 104, 105, 103 }, onOverlay);
            // 原地圖沒被影響
            CollectionAssert.AreEqual(new[] { 100, 101, 102, 103 }, Tags(finder.FindShortestPath(100, 103, Dijkstra(map))));
        }

        [TestMethod]
        public void Overlay_CopiesScalarMetadata_AndSharesPointObjects()
        {
            var map = CreateSmallMap();
            map.Name = "N"; map.Note = "note"; map.PointIndex = 9; map.ImageName = "img";
            map.TagNoStopOfForkAGV.Add(5);
            var overlay = map.CreateOverlay();
            Assert.AreEqual("N", overlay.Name);
            Assert.AreEqual("note", overlay.Note);
            Assert.AreEqual(9, overlay.PointIndex);
            Assert.AreEqual("img", overlay.ImageName);
            CollectionAssert.AreEqual(new[] { 5 }, overlay.TagNoStopOfForkAGV);
            overlay.TagNoStopOfForkAGV.Add(6);
            Assert.AreEqual(1, map.TagNoStopOfForkAGV.Count);
            Assert.AreSame(map.Points[0], overlay.Points[0], "點位物件共用 (不複製) 才是差異覆蓋省記憶體的來源");
        }

        [TestMethod]
        public void Overlay_AllocatesFarLessThanJsonClone_OnLargeMap()
        {
            var rnd = new Random(3);
            var map = new Map { Name = "big", Points = new Dictionary<int, MapPoint>() };
            for (int i = 0; i < 500; i++) map.Points[i] = Pt(i + 1, rnd.NextDouble() * 500, rnd.NextDouble() * 500);
            for (int i = 0; i < 500; i++)
                for (int k = 1; k <= 4; k++)
                    map.Segments.Add(new MapPath { StartPtIndex = i, EndPtIndex = (i + k * 31) % 500 });

            _ = map.Clone(); _ = map.CreateOverlay();   // 預熱

            long b0 = GC.GetAllocatedBytesForCurrentThread();
            _ = map.Clone();
            long clone = GC.GetAllocatedBytesForCurrentThread() - b0;
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            _ = map.CreateOverlay();
            long overlay = GC.GetAllocatedBytesForCurrentThread() - b1;

            Assert.IsTrue(overlay * 20 < clone, $"差異覆蓋配置 {overlay} bytes；JSON 深拷貝 {clone} bytes；期待至少少 20 倍");
        }
    }
}
