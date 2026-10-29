using AGVSystemCommonNet6.AGVDispatch;

namespace AGVSystemCommonNet6.AGVDispatch.Tests
{
    [TestClass]
    public class LexoRankHelperTests
    {
        [TestMethod]
        public void GenerateBetween_BothNull_ReturnsDefaultH()
        {
            Assert.AreEqual("h", LexoRankHelper.GenerateBetween(null!, null!));
        }

        [TestMethod]
        public void GenerateBetween_OnlyNext_ReturnsLessThanNext()
        {
            string rank = LexoRankHelper.GenerateBetween(null!, "h");
            Assert.IsTrue(string.CompareOrdinal(rank, "h") < 0);
        }

        [TestMethod]
        public void GenerateBetween_OnlyPrev_ReturnsGreaterThanPrev()
        {
            string rank = LexoRankHelper.GenerateBetween("h", null!);
            Assert.IsTrue(string.CompareOrdinal(rank, "h") > 0);
        }

        [TestMethod]
        public void GenerateBetween_TwoRanks_ReturnsStrictlyBetween()
        {
            string mid = LexoRankHelper.GenerateBetween("a", "z");
            Assert.IsTrue(string.CompareOrdinal(mid, "a") > 0);
            Assert.IsTrue(string.CompareOrdinal(mid, "z") < 0);
        }

        [TestMethod]
        public void GenerateBetween_AdjacentRanks_StillProducesValue()
        {
            string mid = LexoRankHelper.GenerateBetween("a", "b");
            Assert.IsFalse(string.IsNullOrEmpty(mid));
        }

        [TestMethod]
        public void GenerateBetween_MultipleInserts_RemainOrdered()
        {
            string r1 = LexoRankHelper.GenerateBetween("a", "z");
            string r2 = LexoRankHelper.GenerateBetween("a", r1);
            string r3 = LexoRankHelper.GenerateBetween(r1, "z");
            Assert.IsTrue(string.CompareOrdinal(r2, r1) < 0);
            Assert.IsTrue(string.CompareOrdinal(r1, r3) < 0);
        }

        [TestMethod]
        public void GenerateRankForNewTask_EmptyList_ReturnsH()
        {
            var task = new clsTaskDto { Priority = 1, RecieveTime = DateTime.Now };
            Assert.AreEqual("h", LexoRankHelper.GenerateRankForNewTask(task, new List<clsTaskDto>()));
            Assert.AreEqual("h", LexoRankHelper.GenerateRankForNewTask(task, null!));
        }

        [TestMethod]
        public void GenerateRankForNewTask_HigherPriority_InsertsBeforeLower()
        {
            DateTime now = DateTime.Now;
            var existing = new List<clsTaskDto>
            {
                new clsTaskDto { TaskName = "T1", Priority = 1, RecieveTime = now.AddSeconds(-10), priorityRank = "h" }
            };
            var higher = new clsTaskDto { TaskName = "T2", Priority = 5, RecieveTime = now };
            // 未超時：高 Priority 仍可插到前面
            string rank = LexoRankHelper.GenerateRankForNewTask(higher, existing, protectOverdueWaitSec: 180);
            Assert.IsTrue(string.CompareOrdinal(rank, "h") < 0, $"expected {rank} < h");
        }

        [TestMethod]
        public void GenerateRankForNewTask_LowerPriority_InsertsAfterHigher()
        {
            DateTime now = DateTime.Now;
            var existing = new List<clsTaskDto>
            {
                new clsTaskDto { TaskName = "T1", Priority = 9, RecieveTime = now.AddSeconds(-10), priorityRank = "h" }
            };
            var lower = new clsTaskDto { TaskName = "T2", Priority = 1, RecieveTime = now };
            string rank = LexoRankHelper.GenerateRankForNewTask(lower, existing, protectOverdueWaitSec: 180);
            Assert.IsTrue(string.CompareOrdinal(rank, "h") > 0, $"expected {rank} > h");
        }

        [TestMethod]
        public void GenerateRankForNewTask_SamePriority_EarlierReceiveTimeFirst()
        {
            DateTime now = DateTime.Now;
            var existing = new List<clsTaskDto>
            {
                new clsTaskDto
                {
                    TaskName = "T1",
                    Priority = 5,
                    RecieveTime = now.AddSeconds(-30),
                    priorityRank = "h"
                }
            };
            var earlier = new clsTaskDto
            {
                TaskName = "T0",
                Priority = 5,
                RecieveTime = now.AddSeconds(-60)
            };
            string rank = LexoRankHelper.GenerateRankForNewTask(earlier, existing, protectOverdueWaitSec: 180);
            Assert.IsTrue(string.CompareOrdinal(rank, "h") < 0);
        }

        [TestMethod]
        public void GenerateRankForNewTask_HigherPriority_CannotInsertBeforeOverdueWaitTask()
        {
            DateTime now = DateTime.Now;
            var overdueBoosted = new clsTaskDto
            {
                TaskName = "OVERDUE",
                Priority = 1,
                RecieveTime = now.AddSeconds(-300),
                priorityRank = "a"
            };
            var freshLow = new clsTaskDto
            {
                TaskName = "FRESH",
                Priority = 2,
                RecieveTime = now.AddSeconds(-5),
                priorityRank = "m"
            };
            var existing = new List<clsTaskDto> { overdueBoosted, freshLow };
            var higherNew = new clsTaskDto
            {
                TaskName = "NEW_HIGH",
                Priority = 99,
                RecieveTime = now
            };

            string rank = LexoRankHelper.GenerateRankForNewTask(higherNew, existing, protectOverdueWaitSec: 180);

            // 不可插到超時單 OVERDUE 之前；應落在 OVERDUE 與 FRESH 之間或 FRESH 之後，但一定 > overdue rank
            Assert.IsTrue(string.CompareOrdinal(rank, "a") > 0, $"超時單應受保護，新 rank={rank} 不應 < a");
        }

        [TestMethod]
        public void GenerateRankForNewTask_ProtectDisabled_HigherPriorityCanPassOverdue()
        {
            DateTime now = DateTime.Now;
            var overdue = new clsTaskDto
            {
                TaskName = "OVERDUE",
                Priority = 1,
                RecieveTime = now.AddSeconds(-300),
                priorityRank = "h"
            };
            var higherNew = new clsTaskDto { TaskName = "NEW", Priority = 99, RecieveTime = now };
            string rank = LexoRankHelper.GenerateRankForNewTask(higherNew, new List<clsTaskDto> { overdue }, protectOverdueWaitSec: 0);
            Assert.IsTrue(string.CompareOrdinal(rank, "h") < 0);
        }

        [TestMethod]
        public void TaskPriorityBoostTimeout_DefaultConstant_MatchesExperimentalDefault()
        {
            Assert.AreEqual(180, TaskPriorityBoostTimeout.DefaultTimeoutSec);
            Assert.AreEqual(10, TaskPriorityBoostTimeout.DebuggerTimeoutSec);
            Assert.AreEqual(TaskPriorityBoostTimeout.DefaultTimeoutSec, LexoRankHelper.DefaultOverdueProtectWaitSec);
        }

        [TestMethod]
        public void GenerateRankForMove_ClampsNegativeAndOverflowIndex()
        {
            var tasks = new List<clsTaskDto>
            {
                new clsTaskDto { TaskName = "A", priorityRank = "a" },
                new clsTaskDto { TaskName = "B", priorityRank = "m" },
                new clsTaskDto { TaskName = "C", priorityRank = "z" }
            };

            string toFront = LexoRankHelper.GenerateRankForMove(tasks[1], tasks, newIndex: -5);
            Assert.IsFalse(string.IsNullOrEmpty(toFront));

            string toEnd = LexoRankHelper.GenerateRankForMove(tasks[1], tasks, newIndex: 99);
            Assert.IsFalse(string.IsNullOrEmpty(toEnd));
        }

        [TestMethod]
        public void GenerateRankForMove_MiddleIndex_BetweenNeighbors()
        {
            var tasks = new List<clsTaskDto>
            {
                new clsTaskDto { TaskName = "A", priorityRank = "a" },
                new clsTaskDto { TaskName = "B", priorityRank = "m" },
                new clsTaskDto { TaskName = "C", priorityRank = "z" }
            };
            // 把 C 移到 index 1（在 A 與 B 之間，移除自己後）
            string rank = LexoRankHelper.GenerateRankForMove(tasks[2], tasks, newIndex: 1);
            Assert.IsTrue(string.CompareOrdinal(rank, "a") > 0);
            Assert.IsTrue(string.CompareOrdinal(rank, "m") < 0);
        }
    }
}
