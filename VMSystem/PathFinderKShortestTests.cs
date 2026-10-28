using AGVSystemCommonNet6.MAP;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// Yen's K-shortest：候選正確性、繞行比例上限、時間預算、快取矩陣不被 spur 改寫。
    /// </summary>
    [TestClass]
    public class PathFinderKShortestTests
    {
        private static MapPoint Pt(int tag, double x, double y) => new MapPoint
        {
            TagNumber = tag,
            X = x,
            Y = y,
            Enable = true,
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
        ///  4 ---- 5 ---+
        /// 上排每段長 10；下繞 0-4(10)+4-5(30)+5-3(10)。
        /// </summary>
        private static Map CreateSmallMap()
        {
            var map = new Map { Name = "kshort", Points = new Dictionary<int, MapPoint>() };
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

        private static PathFinder.PathFinderOption Opt(Map map, int k = 5, double maxDetour = 0, int timeMs = 0) => new()
        {
            Algorithm = PathFinder.PathFinderOption.ALGORITHM.Dijsktra,
            OnlyNormalPoint = true,
            Strategy = PathFinder.PathFinderOption.STRATEGY.SHORST_DISTANCE,
            MapUse = map,
            KShortestCount = k,
            KShortestMaxDetourRatio = maxDetour,
            KShortestTimeBudgetMs = timeMs,
        };

        private static List<int> Tags(PathFinder.clsPathInfo info) => info.stations.Select(s => s.TagNumber).ToList();

        [TestInitialize]
        public void Init()
        {
            PathFinderDistanceMatrixCache.Enabled = true;
            PathFinderDistanceMatrixCache.Clear();
            PathFinderDistanceMatrixCache.ResetStats();
        }

        [TestMethod]
        public void FindKShortest_ReturnsShortestThenDetour()
        {
            Map map = CreateSmallMap();
            var finder = new PathFinder();
            List<PathFinder.clsPathInfo> paths = finder.FindKShortestPaths(100, 103, Opt(map, k: 3, maxDetour: 0));

            Assert.IsTrue(paths.Count >= 2, $"應至少兩條候選，實際 {paths.Count}");
            CollectionAssert.AreEqual(new List<int> { 100, 101, 102, 103 }, Tags(paths[0]));
            CollectionAssert.AreEqual(new List<int> { 100, 104, 105, 103 }, Tags(paths[1]));
            Assert.IsTrue(paths[0].graphCost > 0);
            Assert.IsTrue(paths[1].graphCost >= paths[0].graphCost);
        }

        [TestMethod]
        public void FindKShortest_MaxDetourRatio_FiltersLongDetour()
        {
            Map map = CreateSmallMap();
            var finder = new PathFinder();
            // 下繞約 50、上排 30 → 比例約 1.67；設 1.2 應只剩最短
            List<PathFinder.clsPathInfo> paths = finder.FindKShortestPaths(100, 103, Opt(map, k: 5, maxDetour: 1.2));
            Assert.AreEqual(1, paths.Count);
            CollectionAssert.AreEqual(new List<int> { 100, 101, 102, 103 }, Tags(paths[0]));
        }

        [TestMethod]
        public void FindKShortest_SameStartGoal_ReturnsSinglePoint()
        {
            Map map = CreateSmallMap();
            var finder = new PathFinder();
            List<PathFinder.clsPathInfo> paths = finder.FindKShortestPaths(100, 100, Opt(map, k: 3));
            Assert.AreEqual(1, paths.Count);
            Assert.AreEqual(100, paths[0].stations[0].TagNumber);
            Assert.AreEqual(0, paths[0].graphCost);
        }

        [TestMethod]
        public void FindKShortest_DoesNotMutateCachedMatrix()
        {
            Map map = CreateSmallMap();
            var finder = new PathFinder();
            var opt = Opt(map, k: 3, maxDetour: 0);

            // 先跑一次建立快取
            _ = finder.FindKShortestPaths(100, 103, opt);
            Assert.IsTrue(PathFinderDistanceMatrixCache.Misses >= 1);

            long hitsBefore = PathFinderDistanceMatrixCache.Hits;
            List<PathFinder.clsPathInfo> second = finder.FindKShortestPaths(100, 103, opt);
            Assert.IsTrue(PathFinderDistanceMatrixCache.Hits > hitsBefore, "第二次應命中距離矩陣快取");
            Assert.IsTrue(second.Count >= 2);

            // 再跑 Dijkstra 單一路徑，確認主圖仍正確（未被 spur 清邊）
            PathFinder.clsPathInfo single = finder.FindShortestPath(100, 103, opt);
            CollectionAssert.AreEqual(new List<int> { 100, 101, 102, 103 }, Tags(single));
        }

        [TestMethod]
        public void FindKShortest_RespectsConstrainTags()
        {
            Map map = CreateSmallMap();
            var finder = new PathFinder();
            var opt = Opt(map, k: 3, maxDetour: 0);
            opt.ConstrainTags = new List<int> { 101, 102 }; // 擋上排中間 → 只剩下繞

            List<PathFinder.clsPathInfo> paths = finder.FindKShortestPaths(100, 103, opt);
            Assert.AreEqual(1, paths.Count);
            CollectionAssert.AreEqual(new List<int> { 100, 104, 105, 103 }, Tags(paths[0]));
        }
    }
}
