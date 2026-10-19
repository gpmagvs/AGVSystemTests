using System.Reflection;
using System.Runtime.CompilerServices;
using AGVSystemCommonNet6;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MAP.Geometry;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;
using VMSystem.Extensions;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// 殘留的兩處 Map / MapPoint 深拷貝改寫：
    /// 1) MoveTaskDynamicPathPlanV3.DynamicClosePath：dynamicMapUsing.Clone() (整張地圖 JSON 來回) → CreateOverlay()
    /// 2) VehicleNavigationState.UpdateNavigationPointsForPathCalculation：pathPoints.Clone() (整份 JSON 來回) → 每點淺層複製
    /// 證明：結果與舊的 Clone 版本相同，且原本的 Map / MapPoint 一律不被修改 (前後深度比對)。
    /// 需要 CommonNet6 PR (PathID 快取) 先 merge。
    /// </summary>
    [TestClass]
    public class MapCloneReplacementTests
    {
        private static MapPoint Pt(int tag, double x, double y) => new MapPoint
        {
            TagNumber = tag, X = x, Y = y, Enable = true, StationType = STATION_TYPE.Normal, Name = tag.ToString(),
            Direction = 12.5, Graph = new Graph { Display = "P" + tag },
        };

        private static void Link(Map map, int a, int b)
        {
            map.Segments.Add(new MapPath { StartPtIndex = a, EndPtIndex = b, Weight = 1 });
            map.Segments.Add(new MapPath { StartPtIndex = b, EndPtIndex = a, Weight = 1 });
        }

        /// <summary>0(100) 連 1(101)、4(104)；1-2-3；4-5；5-3 (環)</summary>
        private static PathFinder.PathFinderOption Dijkstra(Map map) => new PathFinder.PathFinderOption
        {
            Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
            OnlyNormalPoint = true,
            Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
            MapUse = map,
        };

        private static Map CreateMap()
        {
            var map = new Map { Name = "t", Points = new Dictionary<int, MapPoint>() };
            map.Points[0] = Pt(100, 0, 0); map.Points[1] = Pt(101, 10, 0); map.Points[2] = Pt(102, 20, 0);
            map.Points[3] = Pt(103, 30, 0); map.Points[4] = Pt(104, 0, 10); map.Points[5] = Pt(105, 30, 10);
            Link(map, 0, 1); Link(map, 1, 2); Link(map, 2, 3); Link(map, 0, 4); Link(map, 4, 5); Link(map, 5, 3);
            map.Regions = new List<MapRegion> { new MapRegion { Name = "R1", MaxVehicleCapacity = 2 } };
            map.TagNoStopOfForkAGV = new List<int> { 101 };
            return map;
        }

        private static string Snapshot(Map map) => map.ToJson();

        private static MapRectangle Rect(MapPoint start, MapPoint end) => new MapRectangle { StartPoint = start, EndPoint = end };

        // ───────────── 舊版 DynamicClosePath (逐行對照 develop 上的原始實作) ─────────────
        private static (bool result, Map? newMap) LegacyDynamicClosePath(Map dynamicMapUsing, MapRectangle pathRect, MapPoint agvCurrentPoint)
        {
            var startPtOfPathClose = pathRect.StartPoint;
            var endPtOfPathClose = pathRect.EndPoint;
            Map mapClone = dynamicMapUsing.Clone();
            (bool removed, MapPath? path) = mapClone.TryRemovePathDynamic(startPtOfPathClose, endPtOfPathClose);
            var agvCurrentPtINdex = agvCurrentPoint.GetIndexOfPoint(ref mapClone);
            var validPathes = mapClone.Segments.Where(p => p.StartPtIndex == agvCurrentPtINdex && mapClone.Points[p.EndPtIndex].StationType == STATION_TYPE.Normal).ToList();
            if (!validPathes.Any())
                return (false, null);
            return (removed, mapClone);
        }

        // ───────────── 以真正的 MoveTaskDynamicPathPlanV3.DynamicClosePath 執行 ─────────────
        private static MoveTaskDynamicPathPlanV3 CreateTask(Map map, int agvCurrentTag)
        {
            var agv = FakeAgvFactory.Create(out var proxy, "AGV_T");
            proxy.CurrentPoint = new MapPoint { TagNumber = agvCurrentTag };
            var task = (MoveTaskDynamicPathPlanV3)RuntimeHelpers.GetUninitializedObject(typeof(MoveTaskDynamicPathPlanV3));
            typeof(TaskBase).GetField("<Agv>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, agv);
            task.logger = NLog.LogManager.GetLogger("MapCloneReplacementTests");
            SetMap(task, map);
            return task;
        }

        private static void SetMap(TaskBase task, Map? map)
            => typeof(TaskBase).GetField("dynamicMapUsing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, map);

        private static Map? GetMap(TaskBase task)
            => (Map?)typeof(TaskBase).GetField("dynamicMapUsing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task);

        private static async Task<bool> Close(MoveTaskDynamicPathPlanV3 task, MapRectangle rect)
        {
            var m = typeof(MoveTaskDynamicPathPlanV3).GetMethod("DynamicClosePath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return await (Task<bool>)m.Invoke(task, new object[] { rect })!;
        }

        private static List<string> SegmentIds(Map m) => m.Segments.Select(s => s.PathID).ToList();

        [TestMethod]
        public async Task DynamicClosePath_RemovesSegment_SameResultAsLegacyClone()
        {
            var baseMap = CreateMap();
            var rect = Rect(baseMap.Points[0], baseMap.Points[1]);   // 關閉 100→101
            var legacy = LegacyDynamicClosePath(baseMap, rect, baseMap.Points[0]);

            var task = CreateTask(baseMap, 100);
            bool result = await Close(task, rect);

            Assert.AreEqual(legacy.result, result);
            Assert.IsTrue(result);
            CollectionAssert.AreEqual(SegmentIds(legacy.newMap!), SegmentIds(GetMap(task)!));
            Assert.IsFalse(SegmentIds(GetMap(task)!).Contains("0_1"));
            // 地圖其餘內容 (點位、區域、禁停) 與舊版相同
            CollectionAssert.AreEqual(legacy.newMap!.Points.Keys.ToList(), GetMap(task)!.Points.Keys.ToList());
            CollectionAssert.AreEqual(legacy.newMap.Points.Values.Select(p => p.ToJson()).ToList(), GetMap(task)!.Points.Values.Select(p => p.ToJson()).ToList());
            CollectionAssert.AreEqual(legacy.newMap.TagNoStopOfForkAGV, GetMap(task)!.TagNoStopOfForkAGV);
            Assert.AreEqual(legacy.newMap.Regions.Count, GetMap(task)!.Regions.Count);
        }

        [TestMethod]
        public async Task DynamicClosePath_DoesNotModifyTheOriginalMap_DeepCompare()
        {
            var baseMap = CreateMap();
            string before = Snapshot(baseMap);
            var segmentsBefore = baseMap.Segments.ToList();
            var task = CreateTask(baseMap, 100);

            bool result = await Close(task, Rect(baseMap.Points[0], baseMap.Points[1]));

            Assert.IsTrue(result);
            Assert.AreNotSame(baseMap, GetMap(task), "關閉後 dynamicMapUsing 是新的覆蓋地圖");
            Assert.AreEqual(before, Snapshot(baseMap), "原地圖 (深度比對) 不可被修改");
            CollectionAssert.AreEqual(segmentsBefore, baseMap.Segments, "原地圖的 Segments 清單內容與順序不變");
            Assert.AreEqual(12, baseMap.Segments.Count);
            Assert.AreEqual(11, GetMap(task)!.Segments.Count);
        }

        [TestMethod]
        public async Task DynamicClosePath_NoPathLeft_ReturnsFalse_AndKeepsCurrentMapUntouched()
        {
            var baseMap = CreateMap();
            // 先關掉 100→101，再關 100→104 就完全無路
            var task = CreateTask(baseMap, 100);
            Assert.IsTrue(await Close(task, Rect(baseMap.Points[0], baseMap.Points[1])));
            var afterFirst = GetMap(task)!;
            string snapAfterFirst = Snapshot(afterFirst);
            var legacy = LegacyDynamicClosePath(afterFirst, Rect(baseMap.Points[0], baseMap.Points[4]), baseMap.Points[0]);
            Assert.IsFalse(legacy.result);

            bool result = await Close(task, Rect(baseMap.Points[0], baseMap.Points[4]));

            Assert.IsFalse(result);
            Assert.AreSame(afterFirst, GetMap(task), "不可行時 dynamicMapUsing 維持原物件");
            Assert.AreEqual(snapAfterFirst, Snapshot(afterFirst), "不可行時也不能把路段從目前地圖移除 (舊版是丟棄深拷貝)");
            Assert.IsTrue(SegmentIds(afterFirst).Contains("0_4"));
        }

        [TestMethod]
        public async Task DynamicClosePath_NonExistingPath_ReturnsFalseLikeLegacy_AndMapUnchanged()
        {
            var baseMap = CreateMap();
            string before = Snapshot(baseMap);
            var rect = Rect(baseMap.Points[0], baseMap.Points[3]);   // 100→103 沒有這條路段
            var legacy = LegacyDynamicClosePath(baseMap, rect, baseMap.Points[0]);
            var task = CreateTask(baseMap, 100);

            bool result = await Close(task, rect);

            Assert.AreEqual(legacy.result, result);
            Assert.IsFalse(result);
            Assert.AreEqual(before, Snapshot(baseMap));
        }

        [TestMethod]
        public async Task DynamicClosePath_SequenceOfCloses_MatchesLegacySequence()
        {
            var legacyMap = CreateMap();
            var newMap = CreateMap();
            var task = CreateTask(newMap, 102);
            var closes = new[] { (1, 2), (2, 3), (3, 2), (1, 2), (2, 1) };
            Map currentLegacy = legacyMap;
            foreach (var (a, b) in closes)
            {
                var legacy = LegacyDynamicClosePath(currentLegacy, Rect(legacyMap.Points[a], legacyMap.Points[b]), legacyMap.Points[2]);
                bool result = await Close(task, Rect(newMap.Points[a], newMap.Points[b]));
                Assert.AreEqual(legacy.result, result, $"關閉 {a}->{b}");
                if (legacy.newMap != null) currentLegacy = legacy.newMap;
                CollectionAssert.AreEqual(SegmentIds(currentLegacy), SegmentIds(GetMap(task)!), $"關閉 {a}->{b} 後的路段");
            }
        }

        [TestMethod]
        public async Task DynamicClosePath_ThenPathFinding_GivesSameShortestPathAsLegacy()
        {
            var legacyBase = CreateMap();
            var newBase = CreateMap();
            var legacy = LegacyDynamicClosePath(legacyBase, Rect(legacyBase.Points[1], legacyBase.Points[2]), legacyBase.Points[0]);
            var task = CreateTask(newBase, 100);
            Assert.IsTrue(await Close(task, Rect(newBase.Points[1], newBase.Points[2])));

            var finder = new PathFinder();
            var legacyPath = finder.FindShortestPath(100, 103, Dijkstra(legacy.newMap!));
            var newPath = finder.FindShortestPath(100, 103, Dijkstra(GetMap(task)!));
            CollectionAssert.AreEqual(legacyPath.stations.Select(s => s.TagNumber).ToList(), newPath.stations.Select(s => s.TagNumber).ToList());
            Assert.IsFalse(newPath.stations.Select(s => s.TagNumber).Zip(newPath.stations.Select(s => s.TagNumber).Skip(1)).Any(p => p == (101, 102)), "已關閉的路段不可被使用");
        }

        [TestMethod]
        public async Task DynamicClosePath_AllocatesFarLessThanJsonClone()
        {
            var map = new Map { Name = "big", Points = new Dictionary<int, MapPoint>() };
            for (int i = 0; i < 400; i++) map.Points[i] = Pt(1000 + i, i * 3, 0);
            for (int i = 0; i < 399; i++) Link(map, i, i + 1);
            var task = CreateTask(map, 1000);
            var rect = Rect(map.Points[0], map.Points[1]);
            await Close(task, rect); // 預熱
            SetMap(task, map);
            map.Segments.Add(new MapPath { StartPtIndex = 0, EndPtIndex = 1, Weight = 1 });
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            await Close(task, rect);
            long overlayBytes = GC.GetAllocatedBytesForCurrentThread() - b0;
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            _ = map.Clone();
            long cloneBytes = GC.GetAllocatedBytesForCurrentThread() - b1;
            Assert.IsTrue(overlayBytes * 5 < cloneBytes, $"覆蓋 {overlayBytes} bytes，JSON 深拷貝 {cloneBytes} bytes；期待至少少 5 倍");
        }

        // ───────────── UpdateNavigationPointsForPathCalculation 的點位複本 ─────────────

        private static List<MapPoint> SamplePath()
        {
            var a = Pt(100, 0, 0); a.Direction = 90; a.Target = new Dictionary<int, double> { [101] = 10.5 };
            var b = Pt(101, 10, 0); b.Direction = -45.25; b.IsAvoid = true; b.RegistInfo.IsRegisted = true; b.RegistInfo.RegisterAGVName = "AGV_X";
            var c = Pt(102, 20, 5); c.StationType = STATION_TYPE.EQ; c.Graph.Display = "EQ-1";
            return new List<MapPoint> { a, b, c };
        }

        [TestMethod]
        public void PointCopy_HasSameSerializedContentAsJsonClone()
        {
            var path = SamplePath();
            var legacy = path.Clone().ToList();
            var copy = MapPointCopyExtensions.CopyForPathCalculation(path);

            Assert.AreEqual(legacy.Count, copy.Count);
            for (int i = 0; i < copy.Count; i++)
                Assert.AreEqual(legacy[i].ToJson(), copy[i].ToJson(), $"第 {i} 個點位內容必須與 JSON 深拷貝相同");
        }

        [TestMethod]
        public void PointCopy_MutatingTheCopy_NeverChangesTheOriginals_DeepCompare()
        {
            var path = SamplePath();
            var before = path.Select(p => p.ToJson()).ToList();
            var originals = path.ToList();

            var copy = MapPointCopyExtensions.CopyForPathCalculation(path);
            copy.Last().Direction = 123.4;           // UpdateNavigationPointsForPathCalculation 的實際動作
            copy.First().Direction = -1;
            copy.First().X = 999;

            CollectionAssert.AreEqual(before, path.Select(p => p.ToJson()).ToList());
            CollectionAssert.AreEqual(originals, path, "傳入清單的內容 (物件) 不變");
            Assert.AreNotSame(path[2], copy[2]);
            Assert.AreEqual(5, path[2].Y);
        }

        [TestMethod]
        public void PointCopy_RegistInfo_IsFreshLikeJsonClone_AndNotSharedWithTheMap()
        {
            var path = SamplePath();
            var copy = MapPointCopyExtensions.CopyForPathCalculation(path);
            var legacy = path.Clone().ToList();

            Assert.AreNotSame(path[1].RegistInfo, copy[1].RegistInfo);
            Assert.IsFalse(copy[1].RegistInfo.IsRegisted);
            Assert.AreEqual(legacy[1].RegistInfo.IsRegisted, copy[1].RegistInfo.IsRegisted);
            Assert.AreEqual(legacy[1].RegistInfo.RegisterAGVName, copy[1].RegistInfo.RegisterAGVName);
            Assert.IsTrue(path[1].RegistInfo.IsRegisted, "原點位的註冊資訊不受影響");
        }

        [TestMethod]
        public void PointCopy_SameInstanceTwice_StaysTwoObjects_SoDistinctBehavesLikeLegacy()
        {
            var p = Pt(100, 0, 0);
            var path = new List<MapPoint> { p, Pt(101, 5, 0), p };
            var current = Pt(100, 0, 0);

            var legacyOut = new List<MapPoint> { current }.Concat(path.Clone().ToList()).Where(pt => pt.TagNumber != 0).Distinct().ToList();
            var newOut = new List<MapPoint> { current }.Concat(MapPointCopyExtensions.CopyForPathCalculation(path)).Where(pt => pt.TagNumber != 0).Distinct().ToList();

            Assert.AreEqual(legacyOut.Count, newOut.Count, "Distinct (參考相等) 的結果數量要與舊版相同");
            CollectionAssert.AreEqual(legacyOut.Select(x => x.TagNumber).ToList(), newOut.Select(x => x.TagNumber).ToList());
        }

        [TestMethod]
        public void PointCopy_AllocatesFarLessThanJsonClone()
        {
            var path = Enumerable.Range(0, 60).Select(i => Pt(100 + i, i * 3, 0)).ToList();
            _ = MapPointCopyExtensions.CopyForPathCalculation(path); _ = path.Clone().ToList();
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            _ = MapPointCopyExtensions.CopyForPathCalculation(path);
            long copyBytes = GC.GetAllocatedBytesForCurrentThread() - b0;
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            _ = path.Clone().ToList();
            long cloneBytes = GC.GetAllocatedBytesForCurrentThread() - b1;
            Assert.IsTrue(copyBytes * 3 < cloneBytes, $"淺層複本 {copyBytes} bytes，JSON 深拷貝 {cloneBytes} bytes；期待至少少 3 倍");
        }

        [TestMethod]
        public void PointCopy_EmptyAndNullElements_Behave()
        {
            Assert.AreEqual(0, MapPointCopyExtensions.CopyForPathCalculation(new List<MapPoint>()).Count);
            var list = MapPointCopyExtensions.CopyForPathCalculation(new List<MapPoint> { null! });
            Assert.AreEqual(1, list.Count);
            Assert.IsNull(list[0]);
        }
    }
}
