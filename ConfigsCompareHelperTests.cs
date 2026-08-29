using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VMSystem.Tests
{
    [TestClass()]
    [Ignore("依賴 VMS 設定檔比對環境，非純單元測試")]
    public class ConfigsCompareHelperTests
    {
        [TestMethod()]
        public void CompareTest()
        {
            VMSConfigsCompareHelper helper = new VMSConfigsCompareHelper();
            helper.Compare(DateTime.Now);
        }
    }
}