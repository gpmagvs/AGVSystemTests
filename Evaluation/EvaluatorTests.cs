using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystem.Evaluation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AGVSystemCommonNet6.Configuration;
using EquipmentManagment.Manager;

namespace AGVSystem.Evaluation.Tests
{
    [TestClass()]
    public class EvaluatorTests
    {
        [ClassInitialize]
        public static void Init(TestContext context)
        {
            string configFolderPath = "C:\\AGVS";
            AGVSConfigulator.Init(configFolderPath);
            string eqConfigFolder = AGVSConfigulator.SysConfigs.PATHES_STORE[SystemConfigs.PATH_ENUMS.EQ_CONFIGS_FOLDER_PATH];
            StaEQPManagager.InitializeAsync(new EquipmentManagment.MainEquipment.clsEQManagementConfigs()
            {
                EQConfigPath = $"{eqConfigFolder}//EQConfigs.json",
                WIPConfigPath = $"{eqConfigFolder}//WIPConfigs.json",
                ChargeStationConfigPath = $"{eqConfigFolder}//ChargStationConfigs.json",

            });
        }

        [TestMethod()]
        public void EvaluatorTest()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void StartTest()
        {
            EvaluateFactory.StartTest().GetAwaiter().GetResult();
        }
    }
}