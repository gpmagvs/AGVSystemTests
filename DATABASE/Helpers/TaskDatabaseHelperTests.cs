using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystemCommonNet6.DATABASE.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AGVSystemCommonNet6.Alarm;

namespace AGVSystemCommonNet6.DATABASE.Helpers.Tests
{
    [TestClass()]
    [Ignore("依賴 AlarmManagerCenter 初始化與外部資源，非穩定單元測試")]
    public class TaskDatabaseHelperTests
    {
        [TestMethod()]
        public void CheckAndRebuildFailReasonTest()
        {
            AlarmManagerCenter.InitializeAsync();
            TaskDatabaseHelper.CheckAndRebuildFailReason($"[1063] UNLOAD_BUT_AGV_NO_CARGO_MOUNTED(UNLOAD_BUT_AGV_NO_CARGO_MOUNTED)");
        }
    }
}