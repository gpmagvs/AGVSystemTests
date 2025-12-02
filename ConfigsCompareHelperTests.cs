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