using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGVSystem.Tests
{
    [TestClass()]
    [Ignore("依賴 AGVS 設定檔比對環境，非純單元測試")]
    public class AGVSystemConfigsCompareHelperTests
    {
        [TestMethod()]
        public void CompareTest()
        {
            AGVSystemConfigsCompareHelper helper = new AGVSystemConfigsCompareHelper();
            helper.Compare(DateTime.Now);
        }
    }
}