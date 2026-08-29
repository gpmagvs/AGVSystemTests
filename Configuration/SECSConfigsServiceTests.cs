using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystemCommonNet6.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGVSystemCommonNet6.Configuration.Tests
{
    [TestClass()]
    [Ignore("依賴 SECS 設定檔路徑，非純單元測試")]
    public class SECSConfigsServiceTests
    {
        [TestMethod()]
        public void ReloadTest()
        {
            SECSConfigsService secsConfigsService = new();
            secsConfigsService.InitializeAsync();
        }
    }
}