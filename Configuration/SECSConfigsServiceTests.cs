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