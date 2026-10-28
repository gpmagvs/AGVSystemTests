using AGVSystemCommonNet6.MAP;
using VMSystem.Services.Navigation;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemTests.VMSystemTests
{
    [TestClass]
    public class PathCandidateSelectorTests
    {
        [TestMethod]
        public void IsDetourTooLong_RequiresBothAbsoluteAndRatio()
        {
            // 增加 12m、比例 0.4 → true
            Assert.IsTrue(PathCandidateSelector.IsDetourTooLong(30, 42, absThreshold: 10, rateThreshold: 0.3));
            // 增加 12m、比例 0.2 → false（比例未過）
            Assert.IsFalse(PathCandidateSelector.IsDetourTooLong(60, 72, absThreshold: 10, rateThreshold: 0.3));
            // 增加 5m、比例 0.5 → false（絕對未過）
            Assert.IsFalse(PathCandidateSelector.IsDetourTooLong(10, 15, absThreshold: 10, rateThreshold: 0.3));
        }

        [TestMethod]
        public void IsBlockedAtStart_SinglePointPath_IsNotBlocked()
        {
            var pt = new MapPoint { TagNumber = 3, X = 0, Y = 0, Enable = true, StationType = STATION_TYPE.Normal };
            var wrapper = new PathPlanResultWrapper
            {
                path = new List<MapPoint> { pt },
                totalDistance = 0,
                conflicPoints = new List<MapPoint>(),
                conflicVehicles = new List<VMSystem.AGV.IAGV>()
            };
            // movablePath 對單點無衝突路徑長度為 1，舊邏輯會誤判卡住
            Assert.IsFalse(PathCandidateSelector.IsBlockedAtStartWithoutProgress(wrapper));
        }

        [TestMethod]
        public void IsBlockedAtStart_NullMovableOnMultiPoint_IsBlocked()
        {
            var a = new MapPoint { TagNumber = 1, X = 0, Y = 0, Enable = true, StationType = STATION_TYPE.Normal };
            var b = new MapPoint { TagNumber = 2, X = 10, Y = 0, Enable = true, StationType = STATION_TYPE.Normal };
            var wrapper = new PathPlanResultWrapper
            {
                path = new List<MapPoint> { a, b },
                totalDistance = 10,
                // 衝突在第一點 → movablePath 為 null
                conflicPoints = new List<MapPoint> { a },
                conflicVehicles = new List<VMSystem.AGV.IAGV>()
            };
            Assert.IsTrue(PathCandidateSelector.IsBlockedAtStartWithoutProgress(wrapper));
        }
    }
}
