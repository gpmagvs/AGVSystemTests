using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystemCommonNet6.Utilis.Snap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGVSystemCommonNet6.Utilis.Snap.Tests
{
    [TestClass()]
    public class JsonSnapshotProviderTests
    {
        [TestMethod()]
        public void LoadSnapshotTest()
        {
            JsonSnapshotProvider<Configuration.SystemConfigs> provider = new JsonSnapshotProvider<Configuration.SystemConfigs>("SystemConfigs.json");
            var result = provider.LoadSnapshotWithObj();
            Assert.IsNotNull(result);

        }

        [TestMethod()]
        public void DiffEnginTest()
        {
            JsonSnapshotProvider<Configuration.SystemConfigs> provider = new JsonSnapshotProvider<Configuration.SystemConfigs>("SystemConfigs.json");
            var orignal = provider.LoadSnapshotWithObj();
            Assert.IsNotNull(orignal);

            provider = new JsonSnapshotProvider<Configuration.SystemConfigs>("SystemConfigs-SomeThingChange.json");
            var modified = provider.LoadSnapshotWithObj();
            Assert.IsNotNull(modified);
            var diff = ObjectComparer.CompareObjects(orignal, modified, ignoreProperties: new HashSet<string>()
            {
                "SECSGem"
            });
            Assert.AreEqual(3, diff.Count);
        }
    }
}